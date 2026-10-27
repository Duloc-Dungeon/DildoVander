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

internal sealed class SealedCaseStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly string _connectionString;

    internal SealedCaseStore(string databasePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(databasePath))!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = Path.GetFullPath(databasePath), ForeignKeys = true }.ToString();
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS sealed_cases(
              id TEXT PRIMARY KEY, group_id TEXT NOT NULL, status TEXT NOT NULL, revision INTEGER NOT NULL,
              started_at INTEGER NULL, chapter INTEGER NOT NULL, chapter_deadline INTEGER NULL,
              setup_json TEXT NOT NULL, lobby_json TEXT NOT NULL, created_at INTEGER NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_sealed_cases_group ON sealed_cases(group_id, created_at DESC);
            CREATE TABLE IF NOT EXISTS sealed_case_events(
              event_id TEXT PRIMARY KEY, case_id TEXT NOT NULL REFERENCES sealed_cases(id),
              actor_id TEXT NOT NULL, idempotency_key TEXT NOT NULL, expected_revision INTEGER NOT NULL,
              revision INTEGER NOT NULL, kind TEXT NOT NULL, payload_json TEXT NOT NULL, created_at INTEGER NOT NULL,
              UNIQUE(case_id, actor_id, idempotency_key));
            """;
        command.ExecuteNonQuery();
    }

    internal SealedCaseSnapshot Create(string groupId, string actorId, long now, int expectedPlayers = 8, string variant = "full", string? operationId = null, Func<IDisposable>? authorizeWrite = null)
    {
        if (string.IsNullOrWhiteSpace(groupId) || string.IsNullOrWhiteSpace(actorId)) throw new SealedCasePersistenceException("invalid_setup", 400);
        if (expectedPlayers is < 8 or > 30) throw new SealedCasePersistenceException("player_count_out_of_range", 400);
        try { _ = SealedCaseRules.Schedule(variant, expectedPlayers); }
        catch (SealedCaseRuleException) { throw new SealedCasePersistenceException("invalid_setup", 400); }
        if (operationId is not null && (string.IsNullOrWhiteSpace(operationId) || operationId.Length is < 8 or > 128)) throw new SealedCasePersistenceException("invalid_operation_id", 400);
        var id = "sc_" + (operationId is null ? Guid.NewGuid().ToString("N") : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new[] { groupId, actorId, operationId }, Json)))).ToLowerInvariant()[..32]);
        var setupJson = JsonSerializer.Serialize(new { createdBy = actorId, expectedPlayers, variant }, Json);
        var empty = "{}";
        using var c = Open(); using var tx = c.BeginTransaction(deferred: false);
        // Lock order for this independent store: lobby -> identity. Reserve only
        // after the lobby wait, and exclude revocation through the actual commit.
        using var admission = authorizeWrite?.Invoke();
        using (var existing = c.CreateCommand())
        {
            existing.Transaction = tx;
            existing.CommandText = "SELECT group_id,status,revision,started_at,chapter,chapter_deadline,setup_json,lobby_json FROM sealed_cases WHERE id=$id";
            existing.Parameters.AddWithValue("$id", id);
            using var reader = existing.ExecuteReader();
            if (reader.Read())
            {
                if (reader.GetString(0) != groupId || reader.GetString(6) != setupJson) throw new SealedCasePersistenceException("idempotency_key_reused", 409);
                var prior = new SealedCaseSnapshot(id, groupId, reader.GetString(1), reader.GetInt64(2), reader.IsDBNull(3) ? null : reader.GetInt64(3), reader.GetInt32(4), reader.IsDBNull(5) ? null : reader.GetInt64(5), reader.GetString(6), reader.GetString(7));
                reader.Close(); tx.Commit(); return prior;
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
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT id,group_id,status,revision,started_at,chapter,chapter_deadline,setup_json,lobby_json FROM sealed_cases WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) throw new SealedCasePersistenceException("case_not_found", 404);
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
        string idempotencyKey, long expectedRevision, long now, object payload, Func<IDisposable>? authorizeWrite = null)
    {
        if (string.IsNullOrWhiteSpace(actorId) || string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 128)
            throw new SealedCasePersistenceException("invalid_command", 400);
        var payloadJson = JsonSerializer.Serialize(payload, Json);
        using var c = Open(); using var tx = c.BeginTransaction(deferred: false);
        using var admission = authorizeWrite?.Invoke();
        using (var existing = c.CreateCommand())
        {
            existing.Transaction = tx;
            existing.CommandText = "SELECT e.event_id,e.revision,ca.status,e.payload_json,ca.group_id,e.kind,e.expected_revision FROM sealed_case_events e JOIN sealed_cases ca ON ca.id=e.case_id WHERE e.case_id=$case AND e.actor_id=$actor AND e.idempotency_key=$key";
            existing.Parameters.AddWithValue("$case", caseId); existing.Parameters.AddWithValue("$actor", actorId); existing.Parameters.AddWithValue("$key", idempotencyKey);
            using var r = existing.ExecuteReader();
            if (r.Read())
            {
                if (r.GetString(4) != groupId) throw new SealedCasePersistenceException("case_not_found", 404);
                if (r.GetString(3) != payloadJson || r.GetString(5) != kind || r.GetInt64(6) != expectedRevision) throw new SealedCasePersistenceException("idempotency_key_reused", 409);
                var receipt = new SealedCaseActionReceipt(r.GetString(0), r.GetInt64(1), true, r.GetString(2));
                r.Close(); tx.Commit(); return receipt;
            }
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
        var next = revision + 1; var nextStatus = status; var nextChapter = chapter; long? nextStarted = started; long? deadline = null;
        // Setup->lobby->started transitions are authoritative. No arbitrary outcome mutation.
        if (kind == "lobby" && status is "setup" or "lobby") nextStatus = "lobby";
        else if (kind == "start" && status == "lobby")
        {
            var duration = PayloadInt(payload, "chapterSeconds");
            if (duration is < 60 or > 7200) throw new SealedCasePersistenceException("invalid_chapter_duration", 400);
            nextStatus = "running"; nextStarted = now; nextChapter = 1; deadline = checked(now + duration);
        }
        else if (kind == "confirmed-action")
        {
            if (status != "running") throw new SealedCasePersistenceException("case_not_running", 409);
            if (chapterDeadline is null || now >= chapterDeadline) throw new SealedCasePersistenceException("chapter_locked", 409);
            if (PayloadInt(payload, "chapter") != chapter) throw new SealedCasePersistenceException("wrong_chapter", 409);
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
            insert.Transaction = tx; insert.CommandText = "INSERT INTO sealed_case_events(event_id,case_id,actor_id,idempotency_key,expected_revision,revision,kind,payload_json,created_at) VALUES($event,$case,$actor,$key,$old,$next,$kind,$payload,$now)";
            insert.Parameters.AddWithValue("$event", eventId); insert.Parameters.AddWithValue("$case", caseId); insert.Parameters.AddWithValue("$actor", actorId);
            insert.Parameters.AddWithValue("$key", idempotencyKey); insert.Parameters.AddWithValue("$old", revision); insert.Parameters.AddWithValue("$next", next);
            insert.Parameters.AddWithValue("$kind", kind); insert.Parameters.AddWithValue("$payload", payloadJson); insert.Parameters.AddWithValue("$now", now); insert.ExecuteNonQuery();
        }
        tx.Commit(); return new(eventId, next, false, nextStatus);
    }

    private static int PayloadInt(object payload, string property)
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(payload, Json));
        return doc.RootElement.TryGetProperty(property, out var value) && value.TryGetInt32(out var result) ? result : 0;
    }
    private SqliteConnection Open() { var c = new SqliteConnection(_connectionString); c.Open(); return c; }
}
