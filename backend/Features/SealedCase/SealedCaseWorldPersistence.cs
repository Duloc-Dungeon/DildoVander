using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Kufr.Backend.Features.SealedCase;

internal sealed record SealedStoredWorld(long Revision, int LastChapter, string ContentVersionJson, SealedGameCheckpoint Checkpoint);
internal sealed record SealedWorldResolution(long Revision, int Chapter, bool Replayed, SealedEvent[] Results, JsonElement[] Events);

internal sealed partial class SealedCaseStore
{
    // This is a server-only persistence boundary. No HTTP endpoint accepts a
    // checkpoint, seed, outcome or version from a player. Public routes remain
    // closed until scenario/roster/projection integration is complete.
    private static readonly string ContentVersionJson = ReadContentVersion();

    private static string ReadContentVersion()
    {
        using var stream = typeof(SealedCaseStore).Assembly.GetManifestResourceStream("Kufr.Backend.SealedCaseContentVersion")
            ?? throw new InvalidOperationException("Missing sealed case content version.");
        using var document = JsonDocument.Parse(stream);
        return JsonSerializer.Serialize(document.RootElement, Json);
    }

    private static void InitializeWorldSchema(SqliteConnection c)
    {
        using var transaction = c.BeginTransaction(deferred: false);
        using var cmd = c.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS sealed_case_world(
              case_id TEXT PRIMARY KEY REFERENCES sealed_cases(id),
              content_version_json TEXT NOT NULL, initial_checkpoint_json TEXT NOT NULL,
              revision INTEGER NOT NULL, created_at INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS sealed_case_resolutions(
              case_id TEXT NOT NULL REFERENCES sealed_case_world(case_id), chapter INTEGER NOT NULL,
              expected_revision INTEGER NOT NULL, revision INTEGER NOT NULL,
              orders_json TEXT NOT NULL, checkpoint_json TEXT NOT NULL, events_json TEXT NOT NULL,
              created_at INTEGER NOT NULL, PRIMARY KEY(case_id,chapter), UNIQUE(case_id,revision));
            CREATE TABLE IF NOT EXISTS sealed_case_rosters(
              case_id TEXT PRIMARY KEY REFERENCES sealed_cases(id), server_seed TEXT NOT NULL,
              roster_json TEXT NOT NULL, frozen_at INTEGER NOT NULL);
            """;
        cmd.ExecuteNonQuery();
        // A repair appends a new revision; it never edits an earlier fact.
        // Triggers also protect old command journals after an additive upgrade.
        // SQLite REPLACE can bypass delete triggers when recursive_triggers is
        // off, so reject conflicting inserts explicitly as well.
        foreach (var (table, conflict) in new[]
        {
            ("sealed_case_events", "event_id=NEW.event_id OR (case_id=NEW.case_id AND actor_id=NEW.actor_id AND idempotency_key=NEW.idempotency_key)"),
            ("sealed_case_world", "case_id=NEW.case_id"),
            ("sealed_case_resolutions", "case_id=NEW.case_id AND (chapter=NEW.chapter OR revision=NEW.revision)")
        })
        {
            cmd.CommandText = $"CREATE TRIGGER IF NOT EXISTS {table}_no_replace BEFORE INSERT ON {table} WHEN EXISTS(SELECT 1 FROM {table} WHERE {conflict}) BEGIN SELECT RAISE(ABORT,'sealed case history is append-only'); END";
            cmd.ExecuteNonQuery();
        }
        foreach (var table in new[] { "sealed_case_events", "sealed_case_world", "sealed_case_resolutions" })
        foreach (var operation in new[] { "UPDATE", "DELETE" })
        {
            if (operation == "DELETE")
            {
                cmd.CommandText = $"DROP TRIGGER IF EXISTS {table}_no_delete";
                cmd.ExecuteNonQuery();
            }
            var condition = operation == "DELETE"
                ? "WHEN NOT EXISTS(SELECT 1 FROM sealed_cases WHERE id=OLD.case_id AND status='privacy_terminated') " : "";
            cmd.CommandText = $"CREATE TRIGGER IF NOT EXISTS {table}_no_{operation.ToLowerInvariant()} BEFORE {operation} ON {table} {condition}BEGIN SELECT RAISE(ABORT,'sealed case history is append-only'); END";
            cmd.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    internal SealedStoredWorld InitializeWorld(string caseId, string groupId, long expectedRevision, long now,
        SealedGame game, Func<IDisposable>? authorizeWrite = null)
    {
        var checkpoint = game.Checkpoint();
        using var c = Open(); using var tx = c.BeginTransaction(deferred: false);
        using var admission = AcquireCaseAccess(c, tx, caseId, authorizeWrite);
        var revision = ReadCaseRevision(c, tx, caseId, groupId);
        using var exists = c.CreateCommand(); exists.Transaction = tx;
        exists.CommandText = "SELECT initial_checkpoint_json,revision,content_version_json FROM sealed_case_world WHERE case_id=$id";
        exists.Parameters.AddWithValue("$id", caseId);
        using (var r = exists.ExecuteReader())
        {
            if (r.Read())
            {
                if (r.GetString(0) != JsonSerializer.Serialize(checkpoint, Json) || r.GetInt64(1) != checked(expectedRevision + 1))
                    throw new SealedCasePersistenceException("world_already_initialized", 409);
                var original = new SealedStoredWorld(r.GetInt64(1), 0, r.GetString(2), JsonSerializer.Deserialize<SealedGameCheckpoint>(r.GetString(0), Json)!);
                r.Close(); tx.Commit(); return original;
            }
        }
        if (revision != expectedRevision) throw new SealedCasePersistenceException("revision_conflict", 409);
        using var insert = c.CreateCommand(); insert.Transaction = tx;
        insert.CommandText = "INSERT INTO sealed_case_world VALUES($id,$version,$checkpoint,$revision,$now)";
        insert.Parameters.AddWithValue("$id", caseId); insert.Parameters.AddWithValue("$version", ContentVersionJson);
        insert.Parameters.AddWithValue("$checkpoint", JsonSerializer.Serialize(checkpoint, Json));
        insert.Parameters.AddWithValue("$revision", checked(revision + 1)); insert.Parameters.AddWithValue("$now", now);
        insert.ExecuteNonQuery();
        AdvanceWorldRevision(c, tx, caseId, revision);
        tx.Commit();
        return new(revision + 1, 0, ContentVersionJson, checkpoint);
    }

    internal SealedStoredWorld ReadWorld(string caseId, string groupId)
    {
        using var c = Open(); using var tx = c.BeginTransaction(deferred: true);
        using var admission = AcquireCaseAccess(c, tx, caseId);
        var revision = ReadCaseRevision(c, tx, caseId, groupId);
        var world = ReadWorld(c, tx, caseId, revision);
        tx.Commit(); return world;
    }

    internal SealedWorldResolution ResolveWorldChapter(string caseId, string groupId, int chapter,
        long expectedRevision, long now, IReadOnlyList<SealedAction> orders, Func<IDisposable>? authorizeWrite = null,
        bool useConfirmedPlans = false)
    {
        if (chapter is < 1 or > 6) throw new SealedCasePersistenceException("wrong_chapter", 409);
        using var c = Open(); using var tx = c.BeginTransaction(deferred: false);
        IDisposable? callerAdmission = null;
        using var admission = AcquireCaseAccess(c, tx, caseId, authorizeWrite is null ? null : () => callerAdmission = authorizeWrite());
        var revision = ReadCaseRevision(c, tx, caseId, groupId);
        if (!useConfirmedPlans)
        {
            using var managed = c.CreateCommand(); managed.Transaction = tx;
            managed.CommandText = "SELECT 1 FROM sealed_case_timing WHERE case_id=$case";
            managed.Parameters.AddWithValue("$case", caseId);
            if (managed.ExecuteScalar() is not null)
            {
                throw new SealedCasePersistenceException("sealed_case_plan_authority_required", 409);
            }
        }
        var selectedPlans = useConfirmedPlans ? ReadConfirmedPlans(c, tx, caseId, chapter) : null;
        if (selectedPlans is not null)
        {
            var disconnected = Disconnected(c, tx, caseId);
            if (callerAdmission is Kufr.Backend.Features.Identity.CustomerAdmissionGuard current)
            {
                using var active = current.AccountReservation.Command("""
                    SELECT user.Id FROM AspNetUsers user WHERE user.LaunchGroupId=$group AND user.Disabled=0 AND user.LaunchGroupRevokedAt IS NULL
                        AND EXISTS(SELECT 1 FROM AspNetUserRoles membership JOIN AspNetRoles role ON role.Id=membership.RoleId WHERE membership.UserId=user.Id AND role.Name='Customer')
                    """);
                active.Parameters.AddWithValue("$group", groupId); using var rows = active.ExecuteReader(); var eligible = new HashSet<string>(StringComparer.Ordinal);
                while (rows.Read()) { eligible.Add(rows.GetString(0)); }
                foreach (var member in RosterMembers(c, tx, caseId)) { if (!eligible.Contains(member.AccountId)) { disconnected.Add(member.PlayerId); } }
            }
            foreach (var member in RosterMembers(c, tx, caseId))
            {
                var hold = new SealedAction("hold-" + chapter + "-" + member.SeatId, member.PlayerId, chapter, SealedIntent.Hold);
                if (disconnected.Contains(member.PlayerId)) { selectedPlans[member.PlayerId] = hold; }
                else { selectedPlans.TryAdd(member.PlayerId, hold); }
            }
        }
        var canonical = (selectedPlans is not null ? (IEnumerable<SealedAction>)selectedPlans.Values : orders)
            .OrderBy(x => x.ActorId, StringComparer.Ordinal).ToArray();
        var ordersJson = JsonSerializer.Serialize(canonical, Json);
        using (var prior = c.CreateCommand())
        {
            prior.Transaction = tx;
            prior.CommandText = "SELECT orders_json,expected_revision,events_json FROM sealed_case_resolutions WHERE case_id=$id AND chapter=$chapter";
            prior.Parameters.AddWithValue("$id", caseId); prior.Parameters.AddWithValue("$chapter", chapter);
            using var r = prior.ExecuteReader();
            if (r.Read())
            {
                var storedOrders = JsonSerializer.Deserialize<SealedAction[]>(r.GetString(0), Json)!;
                if (JsonSerializer.Serialize(storedOrders, Json) != ordersJson || r.GetInt64(1) != expectedRevision)
                    throw new SealedCasePersistenceException("resolved_chapter_conflict", 409);
                var result = JsonSerializer.Deserialize<SealedWorldResolution>(r.GetString(2), Json)!;
                r.Close(); tx.Commit(); return result with { Replayed = true };
            }
        }
        if (revision != expectedRevision) throw new SealedCasePersistenceException("revision_conflict", 409);
        if (useConfirmedPlans)
        {
            now = clock.GetUtcNow().ToUnixTimeSeconds();
            var timing = ReadTiming(c, tx, caseId, revision, now);
            var schedule = SealedCaseRules.Schedule(timing.Variant, timing.OriginalPlayers);
            var deadline = checked(timing.IntroductionEndsAt + (chapter - 1L) * (schedule.PlanningSeconds + schedule.QuietSeconds) + schedule.PlanningSeconds);
            if (timing.PausedAt is not null || chapter > timing.Chapters || now < deadline)
            {
                throw new SealedCasePersistenceException("chapter_not_closed", 409);
            }
        }
        var world = ReadWorld(c, tx, caseId, revision);
        if (chapter != world.LastChapter + 1) throw new SealedCasePersistenceException("wrong_chapter", 409);
        if (world.ContentVersionJson != ContentVersionJson) throw new SealedCasePersistenceException("unsupported_content_version", 409);
        var game = SealedGame.Restore(world.Checkpoint);
        var results = game.Resolve(chapter, canonical).ToArray();
        if (useConfirmedPlans) { ElectOversight(c, tx, caseId, chapter, SealedCaseRules.Schedule(game.Scenario?.Variant ?? "full", game.Players.Count).Chapters, game); }
        var checkpoint = game.Checkpoint();
        var next = checked(revision + 1);
        var envelope = new SealedWorldResolution(next, chapter, false, results,
            BuildWorldEvents(caseId, chapter, next, now, canonical, results, world.Checkpoint, checkpoint));
        using var insert = c.CreateCommand(); insert.Transaction = tx;
        insert.CommandText = "INSERT INTO sealed_case_resolutions VALUES($id,$chapter,$old,$next,$orders,$checkpoint,$events,$now)";
        insert.Parameters.AddWithValue("$id", caseId); insert.Parameters.AddWithValue("$chapter", chapter);
        insert.Parameters.AddWithValue("$old", revision); insert.Parameters.AddWithValue("$next", next);
        insert.Parameters.AddWithValue("$orders", ordersJson); insert.Parameters.AddWithValue("$checkpoint", JsonSerializer.Serialize(checkpoint, Json));
        insert.Parameters.AddWithValue("$events", JsonSerializer.Serialize(envelope, Json)); insert.Parameters.AddWithValue("$now", now);
        insert.ExecuteNonQuery();
        AdvanceWorldRevision(c, tx, caseId, revision);
        tx.Commit(); return envelope;
    }

    internal IReadOnlyList<SealedWorldResolution> ReadWorldHistory(string caseId, string groupId)
    {
        using var c = Open(); using var tx = c.BeginTransaction(deferred: true);
        using var admission = AcquireCaseAccess(c, tx, caseId);
        _ = ReadCaseRevision(c, tx, caseId, groupId);
        using var cmd = c.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "SELECT events_json FROM sealed_case_resolutions WHERE case_id=$id ORDER BY chapter";
        cmd.Parameters.AddWithValue("$id", caseId); using var r = cmd.ExecuteReader();
        var result = new List<SealedWorldResolution>();
        while (r.Read()) result.Add(JsonSerializer.Deserialize<SealedWorldResolution>(r.GetString(0), Json)!);
        r.Close(); tx.Commit(); return result;
    }

    private static long ReadCaseRevision(SqliteConnection c, SqliteTransaction tx, string caseId, string groupId)
    {
        using var cmd = c.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "SELECT revision FROM sealed_cases WHERE id=$id AND group_id=$group AND status!='privacy_terminated'";
        cmd.Parameters.AddWithValue("$id", caseId); cmd.Parameters.AddWithValue("$group", groupId);
        return cmd.ExecuteScalar() is long revision ? revision : throw new SealedCasePersistenceException("case_not_found", 404);
    }

    private static SealedStoredWorld ReadWorld(SqliteConnection c, SqliteTransaction tx, string caseId, long revision)
    {
        using var cmd = c.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT w.content_version_json, COALESCE(r.checkpoint_json,w.initial_checkpoint_json), COALESCE(r.chapter,0)
            FROM sealed_case_world w LEFT JOIN sealed_case_resolutions r ON r.case_id=w.case_id
            AND r.chapter=(SELECT MAX(chapter) FROM sealed_case_resolutions WHERE case_id=w.case_id)
            WHERE w.case_id=$id
            """;
        cmd.Parameters.AddWithValue("$id", caseId); using var r = cmd.ExecuteReader();
        if (!r.Read()) throw new SealedCasePersistenceException("world_not_initialized", 409);
        return new(revision, r.GetInt32(2), r.GetString(0), JsonSerializer.Deserialize<SealedGameCheckpoint>(r.GetString(1), Json)!);
    }

    private static void AdvanceWorldRevision(SqliteConnection c, SqliteTransaction tx, string caseId, long old)
    {
        using var cmd = c.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "UPDATE sealed_cases SET revision=$next WHERE id=$id AND revision=$old";
        cmd.Parameters.AddWithValue("$next", checked(old + 1)); cmd.Parameters.AddWithValue("$id", caseId); cmd.Parameters.AddWithValue("$old", old);
        if (cmd.ExecuteNonQuery() != 1) throw new SealedCasePersistenceException("revision_conflict", 409);
    }
}
