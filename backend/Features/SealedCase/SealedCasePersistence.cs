using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using Kufr.Backend.Features.SealedCase;
using Microsoft.Data.Sqlite;

namespace Kufr.Backend.Features.SealedCase;

// Durable bounded command journal. This deliberately does not resolve scenario
// adapters: accepted setup/lobby/clock/action records are not game outcomes.
internal sealed record SealedCaseSnapshot(string Id, string GroupId, string Status, long Revision,
    long? StartedAt, int Chapter, long? ChapterDeadline, string SetupJson, string LobbyJson);
internal sealed record SealedCaseRecord(string Id, string GroupId, string Status, long Revision,
    long? StartedAt, int Chapter, long? ChapterDeadline, string SetupJson, string LobbyJson,
    IReadOnlyList<string> Events);
internal sealed record SealedCaseActionReceipt(string EventId, long Revision, bool Replayed, string Status);
internal sealed class SealedCasePersistenceException(string code, int status) : Exception(code)
{
    internal string Code { get; } = code;
    internal int Status { get; } = status;
}

internal sealed partial class SealedCaseStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly string _connectionString;
    private readonly SealedCasePrivacyAuthority? privacy;
    private readonly TimeProvider clock;

    internal SealedCaseStore(string databasePath, SealedCasePrivacyAuthority? privacy = null, TimeProvider? clock = null)
    {
        this.privacy = privacy;
        this.clock = clock ?? TimeProvider.System;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(databasePath))!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = Path.GetFullPath(databasePath), ForeignKeys = true }.ToString();
        using var connection = Open();
        // Readers reserve privacy authorities after opening their case snapshot.
        // WAL lets a writer commit while such a reader waits for that reservation;
        // rollback journaling would deadlock the two databases until guard timeout.
        using (var journal = connection.CreateCommand())
        {
            journal.CommandText = "PRAGMA journal_mode=WAL";
            if (!string.Equals((string?)journal.ExecuteScalar(), "wal", StringComparison.OrdinalIgnoreCase))
            { throw new InvalidOperationException("Sealed case storage requires WAL journaling."); }
        }
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS sealed_cases(
              id TEXT PRIMARY KEY, group_id TEXT NOT NULL, status TEXT NOT NULL, revision INTEGER NOT NULL,
              started_at INTEGER NULL, chapter INTEGER NOT NULL, chapter_deadline INTEGER NULL,
              setup_json TEXT NOT NULL, lobby_json TEXT NOT NULL, created_at INTEGER NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_sealed_cases_group ON sealed_cases(group_id, created_at DESC);
            CREATE TABLE IF NOT EXISTS sealed_case_private_snapshots(
              case_id TEXT NOT NULL REFERENCES sealed_cases(id), snapshot_ref TEXT NOT NULL,
              subject_account TEXT NOT NULL, profile_id TEXT NOT NULL, profile_revision INTEGER NOT NULL,
              profile_json TEXT NOT NULL, portrait_json TEXT NULL, image BLOB NULL,
              PRIMARY KEY(case_id,snapshot_ref));
            CREATE TABLE IF NOT EXISTS sealed_case_events(
              event_id TEXT PRIMARY KEY, case_id TEXT NOT NULL REFERENCES sealed_cases(id),
              actor_id TEXT NOT NULL, idempotency_key TEXT NOT NULL, expected_revision INTEGER NOT NULL,
              revision INTEGER NOT NULL, kind TEXT NOT NULL, payload_json TEXT NOT NULL, created_at INTEGER NOT NULL,
              client_fingerprint TEXT NULL,
              UNIQUE(case_id, actor_id, idempotency_key));
            """;
        command.ExecuteNonQuery();
        try
        {
            using var migrate = connection.CreateCommand();
            migrate.CommandText = "ALTER TABLE sealed_case_events ADD COLUMN client_fingerprint TEXT NULL";
            migrate.ExecuteNonQuery();
        }
        catch (SqliteException e) when (e.SqliteErrorCode == 1 && e.Message.Contains("duplicate column", StringComparison.OrdinalIgnoreCase)) { }
        InitializeAiPlayersSchema(connection);
        InitializeWorldSchema(connection);
        InitializeTimingSchema(connection);
        InvalidateLegacySelections();
    }

    internal SealedCaseSnapshot Create(string groupId, string actorId, long now, int expectedPlayers = 8, string variant = "full", string? operationId = null, Func<IDisposable>? authorizeWrite = null, string? scenarioId = null)
    {
        if (string.IsNullOrWhiteSpace(groupId) || string.IsNullOrWhiteSpace(actorId)) throw new SealedCasePersistenceException("invalid_setup", 400);
        if (expectedPlayers is < 8 or > 30) throw new SealedCasePersistenceException("player_count_out_of_range", 400);
        try { _ = SealedCaseRules.Schedule(variant, expectedPlayers); }
        catch (SealedCaseRuleException) { throw new SealedCasePersistenceException("invalid_setup", 400); }
        if (operationId is not null && (string.IsNullOrWhiteSpace(operationId) || operationId.Length is < 8 or > 128)) throw new SealedCasePersistenceException("invalid_operation_id", 400);
        var scenario = SealedScenarioCatalog.Resolve(scenarioId);
        var id = "sc_" + (operationId is null ? Guid.NewGuid().ToString("N") : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new[] { groupId, actorId, operationId }, Json)))).ToLowerInvariant()[..32]);
        var setupJson = JsonSerializer.Serialize(new { createdBy = actorId, expectedPlayers, variant,
            scenarioId = scenario.Id, scenarioVersion = scenario.Version, scenarioSnapshot = scenario }, Json);
        var empty = "{}";
        using var c = Open(); using var tx = c.BeginTransaction(deferred: false);
        // Lock order for this independent store: lobby -> identity. Reserve only
        // after the lobby wait, and exclude revocation through the actual commit.
        using var admission = AcquireCaseAccess(c, tx, id, authorizeWrite, actorId);
        using (var existing = c.CreateCommand())
        {
            existing.Transaction = tx;
            existing.CommandText = "SELECT group_id,status,revision,started_at,chapter,chapter_deadline,setup_json,lobby_json FROM sealed_cases WHERE id=$id";
            existing.Parameters.AddWithValue("$id", id);
            using var reader = existing.ExecuteReader();
            if (reader.Read())
            {
                if (reader.GetString(1) == "privacy_terminated")
                {
                    throw new SealedCasePersistenceException("case_not_found", 404);
                }
                var priorSetupJson = reader.GetString(6);
                using var priorSetup = JsonDocument.Parse(priorSetupJson);
                var prior = priorSetup.RootElement;
                var priorScenario = prior.TryGetProperty("scenarioId", out var priorId) ? priorId.GetString() : SealedScenarioCatalog.DefaultId;
                if (reader.GetString(0) != groupId || prior.GetProperty("createdBy").GetString() != actorId
                    || prior.GetProperty("expectedPlayers").GetInt32() != expectedPlayers
                    || prior.GetProperty("variant").GetString() != variant || priorScenario != scenario.Id)
                { throw new SealedCasePersistenceException("idempotency_key_reused", 409); }
                // Creation replay returns the original setup receipt. The HTTP
                // view separately reads current state; an old operation never
                // changes its persisted result after subsequent transitions.
                var receipt = new SealedCaseSnapshot(id, groupId, "setup", 1, null, 0, null, priorSetupJson, empty);
                reader.Close(); tx.Commit(); return receipt;
            }
        }
        using var cmd = c.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO sealed_cases(id,group_id,status,revision,started_at,chapter,chapter_deadline,setup_json,lobby_json,created_at) VALUES($id,$group,'setup',1,NULL,0,NULL,$setup,$lobby,$now)";
        cmd.Parameters.AddWithValue("$id", id); cmd.Parameters.AddWithValue("$group", groupId);
        cmd.Parameters.AddWithValue("$setup", setupJson);
        cmd.Parameters.AddWithValue("$lobby", empty); cmd.Parameters.AddWithValue("$now", now); cmd.ExecuteNonQuery(); tx.Commit();
        return new(id, groupId, "setup", 1, null, 0, null,
            setupJson, empty);
    }

    internal SealedCaseRecord Read(string id)
    {
        using var c = Open(); using var tx = c.BeginTransaction(deferred: true); using var cmd = c.CreateCommand();
        using var admission = AcquireCaseAccess(c, tx, id);
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT id,group_id,status,revision,started_at,chapter,chapter_deadline,setup_json,lobby_json FROM sealed_cases WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) throw new SealedCasePersistenceException("case_not_found", 404);
        if (r.GetString(2) == "privacy_terminated") throw new SealedCasePersistenceException("case_not_found", 404);
        var record = new SealedCaseSnapshot(r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt64(3),
            r.IsDBNull(4) ? null : r.GetInt64(4), r.GetInt32(5), r.IsDBNull(6) ? null : r.GetInt64(6), r.GetString(7), r.GetString(8));
        r.Close();
        using var events = c.CreateCommand(); events.Transaction = tx; events.CommandText = "SELECT payload_json FROM sealed_case_events WHERE case_id=$id ORDER BY revision";
        events.Parameters.AddWithValue("$id", id);
        var history = new List<string>(); using var er = events.ExecuteReader(); while (er.Read()) history.Add(er.GetString(0));
        er.Close(); tx.Commit();
        return new(record.Id, record.GroupId, record.Status, record.Revision, record.StartedAt,
            record.Chapter, record.ChapterDeadline, record.SetupJson, record.LobbyJson, history);
    }

    // All writes are serialized and compare-and-swap the monotonic revision.
    // The authenticated actor comes from the caller, never from the payload.
    internal SealedCaseActionReceipt Append(string caseId, string groupId, string actorId, string kind,
        string idempotencyKey, long expectedRevision, long now, object payload, Func<IDisposable>? authorizeWrite = null,
        int? lobbyCapacity = null, string? clientFingerprint = null, Func<IReadOnlySet<string>>? readEligibleLobbyMembers = null,
        Func<SealedCasePrivateSnapshot?>? capturePrivateSnapshot = null)
    {
        if (string.IsNullOrWhiteSpace(actorId) || string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 128)
            throw new SealedCasePersistenceException("invalid_command", 400);
        var payloadJson = JsonSerializer.Serialize(payload, Json);
        using var c = Open(); using var tx = c.BeginTransaction(deferred: false);
        using var admission = AcquireCaseAccess(c, tx, caseId, authorizeWrite, actorId);
        using (var terminal = c.CreateCommand())
        {
            terminal.Transaction = tx; terminal.CommandText = "SELECT 1 FROM sealed_cases WHERE id=$id AND status='privacy_terminated'";
            terminal.Parameters.AddWithValue("$id", caseId);
            if (terminal.ExecuteScalar() is not null) throw new SealedCasePersistenceException("case_not_found", 404);
        }
        var eligibleLobbyMembers = kind == "lobby" ? readEligibleLobbyMembers?.Invoke() : null;
        using (var existing = c.CreateCommand())
        {
            existing.Transaction = tx;
            existing.CommandText = "SELECT e.event_id,e.revision,CASE e.kind WHEN 'lobby' THEN 'lobby' ELSE 'running' END,e.payload_json,ca.group_id,e.kind,e.expected_revision,e.client_fingerprint FROM sealed_case_events e JOIN sealed_cases ca ON ca.id=e.case_id WHERE e.case_id=$case AND e.actor_id=$actor AND e.idempotency_key=$key";
            existing.Parameters.AddWithValue("$case", caseId); existing.Parameters.AddWithValue("$actor", actorId); existing.Parameters.AddWithValue("$key", idempotencyKey);
            using var r = existing.ExecuteReader();
            if (r.Read())
            {
                if (r.GetString(4) != groupId) throw new SealedCasePersistenceException("case_not_found", 404);
                var priorFingerprint = r.IsDBNull(7) ? null : r.GetString(7);
                if (r.GetString(5) != kind || r.GetInt64(6) != expectedRevision
                    || (priorFingerprint is not null ? priorFingerprint != clientFingerprint : r.GetString(3) != payloadJson))
                    throw new SealedCasePersistenceException("idempotency_key_reused", 409);
                var receipt = new SealedCaseActionReceipt(r.GetString(0), r.GetInt64(1), true, r.GetString(2));
                r.Close(); tx.Commit(); return receipt;
            }
        }
        if (kind == "lobby")
        {
            using var frozen = c.CreateCommand(); frozen.Transaction = tx;
            frozen.CommandText = "SELECT 1 FROM sealed_case_rosters WHERE case_id=$id"; frozen.Parameters.AddWithValue("$id", caseId);
            if (frozen.ExecuteScalar() is not null) throw new SealedCasePersistenceException("case_roster_frozen", 409);
        }
        string status; long revision; long? started; int chapter; long? chapterDeadline;
        using (var read = c.CreateCommand())
        {
            read.Transaction = tx; read.CommandText = "SELECT status,revision,started_at,chapter,chapter_deadline FROM sealed_cases WHERE id=$id AND group_id=$group";
            read.Parameters.AddWithValue("$id", caseId); read.Parameters.AddWithValue("$group", groupId);
            using var r = read.ExecuteReader();
            if (!r.Read()) throw new SealedCasePersistenceException("case_not_found", 404);
            status = r.GetString(0); revision = r.GetInt64(1); started = r.IsDBNull(2) ? null : r.GetInt64(2); chapter = r.GetInt32(3);
            chapterDeadline = r.IsDBNull(4) ? null : r.GetInt64(4);
        }
        if (revision != expectedRevision) throw new SealedCasePersistenceException("revision_conflict", 409);
        if (kind == "lobby" && lobbyCapacity is { } capacity)
        {
            using var history = c.CreateCommand(); history.Transaction = tx;
            history.CommandText = "SELECT actor_id,payload_json FROM sealed_case_events WHERE case_id=$case AND kind='lobby' ORDER BY revision";
            history.Parameters.AddWithValue("$case", caseId);
            var latestLobby = new Dictionary<string, SealedCaseLobbyEntry>(StringComparer.Ordinal);
            using (var rows = history.ExecuteReader())
                while (rows.Read())
                    if (JsonSerializer.Deserialize<SealedCaseLobbyEntry>(rows.GetString(1), Json) is { } entry)
                        latestLobby[rows.GetString(0)] = entry;
            var currentCount = latestLobby.Where(x => eligibleLobbyMembers is null || eligibleLobbyMembers.Contains(x.Key) || AiPlayers(c, tx, caseId).ContainsKey(x.Key))
                .Count(x => x.Value.Ready && x.Value.Consent && x.Value.SnapshotRef is not null) + AiPlayers(c, tx, caseId).Count;
            var incoming = JsonSerializer.Deserialize<SealedCaseLobbyEntry>(payloadJson, Json)!;
            var actorAlreadyReady = latestLobby.TryGetValue(actorId, out var actorEntry)
                && actorEntry.Ready && actorEntry.Consent && actorEntry.SnapshotRef is not null;
            if (incoming.Ready && incoming.Consent && !actorAlreadyReady && currentCount >= capacity)
                throw new SealedCasePersistenceException("sealed_case_lobby_full", 409);
        }
        SealedCasePrivateSnapshot? privateSnapshot = null;
        if (kind == "lobby" && capturePrivateSnapshot is not null)
        {
            var lobby = JsonSerializer.Deserialize<SealedCaseLobbyEntry>(payloadJson, Json)!;
            if (lobby.Ready && lobby.Consent)
            {
                privateSnapshot = capturePrivateSnapshot() ?? throw new SealedCasePersistenceException("sealed_case_character_required", 409);
                if (privateSnapshot.SubjectAccount != actorId || privateSnapshot.Reference != lobby.SnapshotRef)
                    throw new SealedCasePersistenceException("sealed_case_snapshot_invalid", 409);
                payloadJson = JsonSerializer.Serialize(lobby, Json);
            }
        }
        var next = revision + 1; var nextStatus = status; var nextChapter = chapter; long? nextStarted = started; long? deadline = null;
        // Setup->lobby->started transitions are authoritative. No arbitrary outcome mutation.
        if (kind == "lobby" && status is "setup" or "lobby") nextStatus = "lobby";
        else if (kind == "start" && status == "lobby")
        {
            var duration = PayloadInt(payload, "chapterSeconds");
            if (duration is < 60 or > 7200) throw new SealedCasePersistenceException("invalid_chapter_duration", 400);
            nextStatus = "running"; nextStarted = now; nextChapter = 1; deadline = checked(now + duration);
        }
        else if (kind == "confirmed-plan")
        {
            if (status != "running" || payload is not SealedPlanConfirmation confirmation)
            {
                throw new SealedCasePersistenceException("case_not_running", 409);
            }
            now = clock.GetUtcNow().ToUnixTimeSeconds();
            payloadJson = JsonSerializer.Serialize(PrepareConfirmedPlan(c, tx, caseId, actorId, revision, confirmation), Json);
        }
        else if (kind == "confirmed-action")
        {
            if (status != "running") throw new SealedCasePersistenceException("case_not_running", 409);
            using var managed = c.CreateCommand(); managed.Transaction = tx;
            managed.CommandText = "SELECT 1 FROM sealed_case_timing WHERE case_id=$case";
            managed.Parameters.AddWithValue("$case", caseId);
            if (managed.ExecuteScalar() is not null)
            {
                throw new SealedCasePersistenceException("sealed_case_action_contract_required", 409);
            }
            else
            {
                if (chapterDeadline is null || now >= chapterDeadline) throw new SealedCasePersistenceException("chapter_locked", 409);
                if (PayloadInt(payload, "chapter") != chapter) throw new SealedCasePersistenceException("wrong_chapter", 409);
            }
        }
        else if (kind is "lobby" or "start") throw new SealedCasePersistenceException("invalid_transition", 409);
        else throw new SealedCasePersistenceException("unsupported_action", 422);
        var eventId = "scv_" + Guid.NewGuid().ToString("N");
        using (var update = c.CreateCommand())
        {
            update.Transaction = tx; update.CommandText = "UPDATE sealed_cases SET status=$status,revision=$next,started_at=$started,chapter=$chapter,chapter_deadline=COALESCE($deadline,chapter_deadline) WHERE id=$id AND revision=$old";
            update.Parameters.AddWithValue("$status", nextStatus); update.Parameters.AddWithValue("$next", next); update.Parameters.AddWithValue("$started", (object?)nextStarted ?? DBNull.Value);
            update.Parameters.AddWithValue("$chapter", nextChapter); update.Parameters.AddWithValue("$deadline", (object?)deadline ?? DBNull.Value);
            update.Parameters.AddWithValue("$id", caseId); update.Parameters.AddWithValue("$old", revision);
            if (update.ExecuteNonQuery() != 1) throw new SealedCasePersistenceException("revision_conflict", 409);
        }
        using (var insert = c.CreateCommand())
        {
            insert.Transaction = tx; insert.CommandText = "INSERT INTO sealed_case_events(event_id,case_id,actor_id,idempotency_key,expected_revision,revision,kind,payload_json,created_at,client_fingerprint) VALUES($event,$case,$actor,$key,$old,$next,$kind,$payload,$now,$fingerprint)";
            insert.Parameters.AddWithValue("$event", eventId); insert.Parameters.AddWithValue("$case", caseId); insert.Parameters.AddWithValue("$actor", actorId);
            insert.Parameters.AddWithValue("$key", idempotencyKey); insert.Parameters.AddWithValue("$old", revision); insert.Parameters.AddWithValue("$next", next);
            insert.Parameters.AddWithValue("$kind", kind); insert.Parameters.AddWithValue("$payload", payloadJson); insert.Parameters.AddWithValue("$now", now);
            insert.Parameters.AddWithValue("$fingerprint", (object?)clientFingerprint ?? DBNull.Value); insert.ExecuteNonQuery();
        }
        if (privateSnapshot is not null)
        {
            using (var removePrior = c.CreateCommand())
            {
                removePrior.Transaction = tx;
                removePrior.CommandText = "DELETE FROM sealed_case_private_snapshots WHERE case_id=$case AND subject_account=$account";
                removePrior.Parameters.AddWithValue("$case", caseId); removePrior.Parameters.AddWithValue("$account", actorId);
                removePrior.ExecuteNonQuery();
            }
            using var snapshot = c.CreateCommand(); snapshot.Transaction = tx;
            snapshot.CommandText = "INSERT INTO sealed_case_private_snapshots(case_id,snapshot_ref,subject_account,profile_id,profile_revision,profile_json,portrait_json,image) VALUES($case,$ref,$account,$profile,$revision,$profileJson,$portraitJson,$image)";
            snapshot.Parameters.AddWithValue("$case", caseId); snapshot.Parameters.AddWithValue("$ref", privateSnapshot.Reference);
            snapshot.Parameters.AddWithValue("$account", privateSnapshot.SubjectAccount); snapshot.Parameters.AddWithValue("$profile", privateSnapshot.ProfileId);
            snapshot.Parameters.AddWithValue("$revision", privateSnapshot.ProfileRevision); snapshot.Parameters.AddWithValue("$profileJson", privateSnapshot.ProfileJson);
            snapshot.Parameters.AddWithValue("$portraitJson", (object?)privateSnapshot.PortraitJson ?? DBNull.Value);
            snapshot.Parameters.AddWithValue("$image", (object?)privateSnapshot.Image ?? DBNull.Value); snapshot.ExecuteNonQuery();
        }
        else if (kind == "lobby" && JsonSerializer.Deserialize<SealedCaseLobbyEntry>(payloadJson, Json) is { Ready: false })
        {
            using var removePrior = c.CreateCommand(); removePrior.Transaction = tx;
            removePrior.CommandText = "DELETE FROM sealed_case_private_snapshots WHERE case_id=$case AND subject_account=$account";
            removePrior.Parameters.AddWithValue("$case", caseId); removePrior.Parameters.AddWithValue("$account", actorId);
            removePrior.ExecuteNonQuery();
        }
        if (kind == "lobby" && lobbyCapacity is { } rosterCapacity) FreezeRosterIfFull(c, tx, caseId, rosterCapacity, now, eligibleLobbyMembers);
        tx.Commit(); return new(eventId, next, false, nextStatus);
    }

    // Privacy erasure is case-scoped and terminal. Purging the affected case
    // removes current and historical personal references atomically; a minimal
    // terminal row remains so deterministic create/replay cannot resurrect it.
    internal int EraseAccount(string accountId)
    {
        using var c = Open(); using var tx = c.BeginTransaction(deferred: false);
        Kufr.Backend.Features.Keepsakes.ChronicleSourceOutbox.EraseAccount(c, tx, accountId);
        using var find = c.CreateCommand(); find.Transaction = tx;
        find.CommandText = """
            SELECT id FROM sealed_cases ca WHERE
              json_extract(ca.setup_json,'$.createdBy')=$account
              OR EXISTS(SELECT 1 FROM sealed_case_events e WHERE e.case_id=ca.id AND e.actor_id=$account)
              OR EXISTS(SELECT 1 FROM sealed_case_private_snapshots s WHERE s.case_id=ca.id AND s.subject_account=$account)
              OR EXISTS(SELECT 1 FROM sealed_case_rosters r,json_each(r.roster_json) member
                  WHERE r.case_id=ca.id AND json_extract(member.value,'$.accountId')=$account)
            """;
        find.Parameters.AddWithValue("$account", accountId);
        var ids = new List<string>(); using (var rows = find.ExecuteReader()) while (rows.Read()) ids.Add(rows.GetString(0));
        foreach (var id in ids)
        {
            using var terminal = c.CreateCommand(); terminal.Transaction = tx;
            terminal.CommandText = "UPDATE sealed_cases SET status='privacy_terminated',revision=revision+1,started_at=NULL,chapter=0,chapter_deadline=NULL,setup_json='{}',lobby_json='{}' WHERE id=$id";
            terminal.Parameters.AddWithValue("$id", id); terminal.ExecuteNonQuery();
            foreach (var table in new[] { "sealed_case_ai_turns", "sealed_case_ai_players", "sealed_case_resolutions", "sealed_case_world", "sealed_case_rosters", "sealed_case_events", "sealed_case_private_snapshots", "sealed_case_timing" })
            {
                using var delete = c.CreateCommand(); delete.Transaction = tx;
                delete.CommandText = $"DELETE FROM {table} WHERE case_id=$id"; delete.Parameters.AddWithValue("$id", id); delete.ExecuteNonQuery();
            }
        }
        tx.Commit(); return ids.Count;
    }

    internal SealedCaseActionReceipt? ReadReplay(string caseId, string groupId, string actorId, string kind, string idempotencyKey,
        long expectedRevision, string clientFingerprint, Func<IDisposable>? authorizeWrite = null)
    {
        using var c = Open(); using var tx = c.BeginTransaction(deferred: true); using var admission = AcquireCaseAccess(c, tx, caseId, authorizeWrite, actorId);
        _ = ReadCaseRevision(c, tx, caseId, groupId);
        using var cmd = c.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "SELECT e.event_id,e.revision,CASE e.kind WHEN 'lobby' THEN 'lobby' ELSE 'running' END,e.kind,e.expected_revision,e.client_fingerprint,ca.group_id FROM sealed_case_events e JOIN sealed_cases ca ON ca.id=e.case_id WHERE e.case_id=$case AND e.actor_id=$actor AND e.idempotency_key=$key";
        cmd.Parameters.AddWithValue("$case", caseId); cmd.Parameters.AddWithValue("$actor", actorId); cmd.Parameters.AddWithValue("$key", idempotencyKey);
        using var r = cmd.ExecuteReader(); if (!r.Read()) { r.Close(); tx.Commit(); return null; }
        if (r.GetString(6) != groupId) throw new SealedCasePersistenceException("case_not_found", 404);
        if (r.GetString(3) != kind || r.GetInt64(4) != expectedRevision || r.IsDBNull(5) || r.GetString(5) != clientFingerprint)
            throw new SealedCasePersistenceException("idempotency_key_reused", 409);
        var receipt = new SealedCaseActionReceipt(r.GetString(0), r.GetInt64(1), true, r.GetString(2));
        r.Close(); tx.Commit(); return receipt;
    }

    private void FreezeRosterIfFull(SqliteConnection c, SqliteTransaction tx, string caseId, int capacity, long now, IReadOnlySet<string>? eligibleLobbyMembers)
    {
        using var exists = c.CreateCommand(); exists.Transaction = tx; exists.CommandText = "SELECT 1 FROM sealed_case_rosters WHERE case_id=$id"; exists.Parameters.AddWithValue("$id", caseId);
        if (exists.ExecuteScalar() is not null) return;
        var bots = AiPlayers(c, tx, caseId);
        var latest = LobbyEntries(c, tx, caseId);
        var ready = latest.Where(x => eligibleLobbyMembers is null || eligibleLobbyMembers.Contains(x.Key) || bots.ContainsKey(x.Key)).Select(x => x.Value)
            .Where(x => x.Ready && x.Consent && x.SnapshotRef is not null).OrderBy(x => x.PlayerId, StringComparer.Ordinal).ToArray();
        if (ready.Length != capacity) return;
        var seed = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var assignments = SealedCaseRosterAssignments.Assign(ready.Select(x => x.PlayerId).ToArray(), seed);
        var roster = assignments.Select(a =>
        {
            if (bots.TryGetValue(a.PlayerId, out var botName))
            { return new SealedStoredRosterMember(a.PlayerId, a.PlayerId, a.SeatId, "ai:" + a.PlayerId, 1, null, a.Kit, a.Team, IsAi: true, DisplayName: botName); }
            var entry = ready.Single(x => x.PlayerId == a.PlayerId);
            using var snapshot = c.CreateCommand(); snapshot.Transaction = tx;
            snapshot.CommandText = "SELECT profile_id,profile_revision,portrait_json FROM sealed_case_private_snapshots WHERE case_id=$case AND snapshot_ref=$ref AND subject_account=$account";
            snapshot.Parameters.AddWithValue("$case", caseId); snapshot.Parameters.AddWithValue("$ref", entry.SnapshotRef!);
            snapshot.Parameters.AddWithValue("$account", entry.PlayerId);
            using var row = snapshot.ExecuteReader();
            if (!row.Read()) throw new SealedCasePersistenceException("sealed_case_character_required", 409);
            var portraitStatus = row.IsDBNull(2) ? null : JsonDocument.Parse(row.GetString(2)).RootElement.GetProperty("status").GetString();
            return new SealedStoredRosterMember(a.PlayerId, a.PlayerId, a.SeatId, row.GetString(0), row.GetInt64(1), portraitStatus, a.Kit, a.Team, entry.SnapshotRef);
        }).ToArray();
        using var insert = c.CreateCommand(); insert.Transaction = tx;
        insert.CommandText = "INSERT INTO sealed_case_rosters(case_id,server_seed,roster_json,frozen_at) VALUES($id,$seed,$roster,$now)";
        insert.Parameters.AddWithValue("$id", caseId); insert.Parameters.AddWithValue("$seed", seed);
        insert.Parameters.AddWithValue("$roster", JsonSerializer.Serialize(roster, Json)); insert.Parameters.AddWithValue("$now", now); insert.ExecuteNonQuery();
    }

    private static int PayloadInt(object payload, string property)
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(payload, Json));
        return doc.RootElement.TryGetProperty(property, out var value) && value.TryGetInt32(out var result) ? result : 0;
    }
    private SqliteConnection Open() { var c = new SqliteConnection(_connectionString); c.Open(); return c; }
}
