using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Kufr.Backend.Features.SealedCase;

internal sealed record SealedTimingSnapshot(long Revision, long ServerNow, string Variant, int OriginalPlayers,
    string Phase, int Chapter, int Chapters, long StartedAt, long? PausedAt, long PausedSeconds,
    long IntroductionEndsAt, long PlanningEndsAt, long QuietEndsAt, long FinaleEndsAt)
{
    internal bool CanSubmit => Phase == "planning";
}
internal sealed record SealedTimingReceipt(string EventId, long Revision, bool Replayed, SealedTimingSnapshot Timing);

internal sealed partial class SealedCaseStore
{
    private static void InitializeTimingSchema(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS sealed_case_timing(
                case_id TEXT PRIMARY KEY REFERENCES sealed_cases(id), variant TEXT NOT NULL,
                original_players INTEGER NOT NULL, started_at INTEGER NOT NULL,
                paused_at INTEGER NULL, paused_seconds INTEGER NOT NULL DEFAULT 0);
            """;
        command.ExecuteNonQuery();
    }

    // Server-owned methods: no caller-supplied duration, chapter or timestamp.
    // Scenario integration will call these after provisioning the frozen world;
    // public start/actions deliberately remain unavailable until that integration.
    internal SealedTimingReceipt StartTimeline(string caseId, string groupId, string actorId,
        string operationId, long expectedRevision, Func<IDisposable>? authorize = null)
        => ChangeTimeline(caseId, groupId, actorId, operationId, expectedRevision, "timeline-start", authorize);

    internal SealedTimingReceipt PauseTimeline(string caseId, string groupId, string actorId,
        string operationId, long expectedRevision, Func<IDisposable>? authorize = null)
        => ChangeTimeline(caseId, groupId, actorId, operationId, expectedRevision, "timeline-pause", authorize);

    internal SealedTimingReceipt ResumeTimeline(string caseId, string groupId, string actorId,
        string operationId, long expectedRevision, Func<IDisposable>? authorize = null)
        => ChangeTimeline(caseId, groupId, actorId, operationId, expectedRevision, "timeline-resume", authorize);

    internal SealedTimingSnapshot ReadTimeline(string caseId, string groupId, Func<IDisposable>? authorize = null)
    {
        using var connection = Open(); using var transaction = connection.BeginTransaction(deferred: true);
        using var admission = AcquireCaseAccess(connection, transaction, caseId, authorize);
        var revision = ReadCaseRevision(connection, transaction, caseId, groupId);
        var timing = ReadTiming(connection, transaction, caseId, revision, clock.GetUtcNow().ToUnixTimeSeconds());
        transaction.Commit(); return timing;
    }

    private SealedTimingReceipt ChangeTimeline(string caseId, string groupId, string actorId,
        string operationId, long expectedRevision, string kind, Func<IDisposable>? authorize)
    {
        if (string.IsNullOrWhiteSpace(operationId) || operationId.Length is < 8 or > 128)
        {
            throw new SealedCasePersistenceException("invalid_operation_id", 400);
        }
        using var connection = Open(); using var transaction = connection.BeginTransaction(deferred: false);
        using var admission = AcquireCaseAccess(connection, transaction, caseId, authorize, actorId);
        var revision = ReadCaseRevision(connection, transaction, caseId, groupId);
        using var setupQuery = connection.CreateCommand(); setupQuery.Transaction = transaction;
        setupQuery.CommandText = "SELECT setup_json,status FROM sealed_cases WHERE id=$case";
        setupQuery.Parameters.AddWithValue("$case", caseId);
        string setupJson; string status;
        using (var reader = setupQuery.ExecuteReader())
        {
            if (!reader.Read()) { throw new SealedCasePersistenceException("case_not_found", 404); }
            setupJson = reader.GetString(0); status = reader.GetString(1);
        }
        using var setup = JsonDocument.Parse(setupJson);
        if (setup.RootElement.GetProperty("createdBy").GetString() != actorId)
        {
            throw new SealedCasePersistenceException("sealed_case_host_required", 403);
        }
        using var replay = connection.CreateCommand(); replay.Transaction = transaction;
        replay.CommandText = "SELECT event_id,revision,kind,expected_revision,payload_json FROM sealed_case_events WHERE case_id=$case AND actor_id=$actor AND idempotency_key=$key";
        replay.Parameters.AddWithValue("$case", caseId); replay.Parameters.AddWithValue("$actor", actorId); replay.Parameters.AddWithValue("$key", operationId);
        using (var reader = replay.ExecuteReader())
        {
            if (reader.Read())
            {
                if (reader.GetString(2) != kind || reader.GetInt64(3) != expectedRevision)
                {
                    throw new SealedCasePersistenceException("idempotency_key_reused", 409);
                }
                var receipt = new SealedTimingReceipt(reader.GetString(0), reader.GetInt64(1), true,
                    JsonSerializer.Deserialize<SealedTimingSnapshot>(reader.GetString(4), Json)!);
                reader.Close(); transaction.Commit(); return receipt;
            }
        }
        if (revision != expectedRevision) { throw new SealedCasePersistenceException("revision_conflict", 409); }
        var now = clock.GetUtcNow().ToUnixTimeSeconds();
        if (kind == "timeline-start")
        {
            if (status != "lobby") { throw new SealedCasePersistenceException("invalid_transition", 409); }
            using var frozen = connection.CreateCommand(); frozen.Transaction = transaction;
            frozen.CommandText = "SELECT json_array_length(roster_json) FROM sealed_case_rosters WHERE case_id=$case";
            frozen.Parameters.AddWithValue("$case", caseId);
            var players = setup.RootElement.GetProperty("expectedPlayers").GetInt32();
            if (frozen.ExecuteScalar() is not long frozenCount || frozenCount != players)
            {
                throw new SealedCasePersistenceException("case_roster_not_frozen", 409);
            }
            using var start = connection.CreateCommand(); start.Transaction = transaction;
            start.CommandText = "INSERT INTO sealed_case_timing(case_id,variant,original_players,started_at) VALUES($case,$variant,$players,$now)";
            start.Parameters.AddWithValue("$case", caseId); start.Parameters.AddWithValue("$variant", setup.RootElement.GetProperty("variant").GetString());
            start.Parameters.AddWithValue("$players", players); start.Parameters.AddWithValue("$now", now); start.ExecuteNonQuery();
        }
        else
        {
            var before = ReadTiming(connection, transaction, caseId, revision, now);
            if (kind == "timeline-pause" && before.Phase is "paused" or "finished"
                || kind == "timeline-resume" && (before.PausedAt is null || now < before.PausedAt))
            {
                throw new SealedCasePersistenceException("invalid_transition", 409);
            }
            using var change = connection.CreateCommand(); change.Transaction = transaction;
            change.CommandText = kind == "timeline-pause"
                ? "UPDATE sealed_case_timing SET paused_at=$now WHERE case_id=$case"
                : "UPDATE sealed_case_timing SET paused_seconds=paused_seconds+($now-paused_at),paused_at=NULL WHERE case_id=$case";
            change.Parameters.AddWithValue("$case", caseId); change.Parameters.AddWithValue("$now", now); change.ExecuteNonQuery();
        }
        var timing = ReadTiming(connection, transaction, caseId, revision + 1, now);
        using var update = connection.CreateCommand(); update.Transaction = transaction;
        update.CommandText = """
            UPDATE sealed_cases SET revision=revision+1,status='running',started_at=$started,
                chapter=$chapter,chapter_deadline=$deadline WHERE id=$case AND revision=$revision;
            """;
        update.Parameters.AddWithValue("$started", timing.StartedAt); update.Parameters.AddWithValue("$chapter", timing.Chapter);
        update.Parameters.AddWithValue("$deadline", timing.PlanningEndsAt); update.Parameters.AddWithValue("$case", caseId);
        update.Parameters.AddWithValue("$revision", revision); update.ExecuteNonQuery();
        var eventId = "scv_" + Guid.NewGuid().ToString("N");
        using var append = connection.CreateCommand(); append.Transaction = transaction;
        append.CommandText = """
            INSERT INTO sealed_case_events(event_id,case_id,actor_id,idempotency_key,expected_revision,revision,kind,payload_json,created_at)
                VALUES($event,$case,$actor,$key,$old,$next,$kind,$payload,$now);
            """;
        append.Parameters.AddWithValue("$event", eventId); append.Parameters.AddWithValue("$case", caseId);
        append.Parameters.AddWithValue("$actor", actorId); append.Parameters.AddWithValue("$key", operationId);
        append.Parameters.AddWithValue("$old", revision); append.Parameters.AddWithValue("$next", revision + 1);
        append.Parameters.AddWithValue("$kind", kind); append.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(timing, Json));
        append.Parameters.AddWithValue("$now", now); append.ExecuteNonQuery();
        transaction.Commit(); return new(eventId, revision + 1, false, timing);
    }

    private static SealedTimingSnapshot ReadTiming(SqliteConnection connection, SqliteTransaction transaction,
        string caseId, long revision, long now)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT variant,original_players,started_at,paused_at,paused_seconds FROM sealed_case_timing WHERE case_id=$case";
        command.Parameters.AddWithValue("$case", caseId);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) { throw new SealedCasePersistenceException("case_not_started", 409); }
        var variant = reader.GetString(0); var players = reader.GetInt32(1); var started = reader.GetInt64(2);
        long? pausedAt = reader.IsDBNull(3) ? null : reader.GetInt64(3); var pausedSeconds = reader.GetInt64(4);
        var schedule = SealedCaseRules.Schedule(variant, players);
        var introductionEnds = checked(started + pausedSeconds + schedule.IntroductionSeconds);
        var block = schedule.PlanningSeconds + schedule.QuietSeconds;
        var finaleStarts = checked(introductionEnds + schedule.Chapters * block);
        var finaleEnds = checked(finaleStarts + schedule.FinaleSeconds);
        var effectiveNow = pausedAt ?? now;
        var chapter = effectiveNow < introductionEnds ? 0
            : (int)Math.Min(schedule.Chapters, 1 + (effectiveNow - introductionEnds) / block);
        var planningEnds = checked(introductionEnds + Math.Max(0, chapter - 1) * block + schedule.PlanningSeconds);
        var quietEnds = checked(planningEnds + schedule.QuietSeconds);
        var phase = pausedAt is not null ? "paused" : effectiveNow < introductionEnds ? "introduction"
            : effectiveNow >= finaleEnds ? "finished" : effectiveNow >= finaleStarts ? "finale"
            : effectiveNow < planningEnds ? "planning" : "quiet";
        return new(revision, now, variant, players, phase, chapter, schedule.Chapters, started, pausedAt,
            pausedSeconds, introductionEnds, planningEnds, quietEnds, finaleEnds);
    }
}
