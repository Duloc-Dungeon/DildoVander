using System.Text.Json.Serialization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Kufr.Backend.Features.SealedCase;

// A proposal carries no actor, server ID, accepted revision, clock or outcome.
// Only this explicit confirmation can append an accepted plan.
internal sealed record SealedPlanConfirmation(int Chapter, int ExpectedActionRevision, bool Confirmed,
    SealedIntent Intent, string? TargetId = null, string? Destination = null, string? Method = null,
    SealedCondition? Condition = null, string Fallback = "HOLD", bool LethalConfirmed = false,
    SealedIntent? PreparedIntent = null, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ResourceId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Summary = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] SealedDefensePolicy? DefensePolicy = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? ShelteredPosition = null);
internal sealed record SealedOwnPlan(SealedAction? Action, bool Locked, long ServerNow);

internal sealed partial class SealedCaseStore
{
    internal SealedCaseActionReceipt ConfirmPlan(string caseId, string groupId, string authenticatedActor,
        string operationId, long expectedCaseRevision, SealedPlanConfirmation confirmation,
        Func<IDisposable>? authorize = null)
    {
        var fingerprint = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(confirmation, Json)));
        return Append(caseId, groupId, authenticatedActor, "confirmed-plan", operationId, expectedCaseRevision,
            clock.GetUtcNow().ToUnixTimeSeconds(), confirmation, authorize, clientFingerprint: fingerprint);
    }

    internal SealedOwnPlan ReadOwnPlan(string caseId, string groupId, string authenticatedActor, int chapter,
        Func<IDisposable>? authorize = null)
    {
        using var connection = Open(); using var transaction = connection.BeginTransaction(deferred: true);
        using var admission = AcquireCaseAccess(connection, transaction, caseId, authorize, authenticatedActor);
        var revision = ReadCaseRevision(connection, transaction, caseId, groupId);
        DemandRosterMember(connection, transaction, caseId, authenticatedActor);
        var now = clock.GetUtcNow().ToUnixTimeSeconds();
        var timing = ReadTiming(connection, transaction, caseId, revision, now);
        var plan = ReadConfirmedPlans(connection, transaction, caseId, chapter).GetValueOrDefault(authenticatedActor);
        transaction.Commit(); return new(plan, !timing.CanSubmit || chapter != timing.Chapter, now);
    }

    internal SealedWorldResolution ResolveConfirmedChapter(string caseId, string groupId, int chapter,
        long expectedRevision, Func<IDisposable>? authorize = null)
        => ResolveWorldChapter(caseId, groupId, chapter, expectedRevision, clock.GetUtcNow().ToUnixTimeSeconds(),
            [], authorize, useConfirmedPlans: true);

    private SealedAction PrepareConfirmedPlan(SqliteConnection connection, SqliteTransaction transaction,
        string caseId, string actorId, long revision, SealedPlanConfirmation confirmation)
    {
        if (!confirmation.Confirmed) { throw new SealedCasePersistenceException("sealed_case_confirmation_required", 400); }
        DemandRosterMember(connection, transaction, caseId, actorId);
        if (Disconnected(connection, transaction, caseId).Contains(actorId)) { throw new SealedCasePersistenceException("sealed_case_disconnected", 409); }
        var timing = ReadTiming(connection, transaction, caseId, revision, clock.GetUtcNow().ToUnixTimeSeconds());
        if (!timing.CanSubmit) { throw new SealedCasePersistenceException("chapter_locked", 409); }
        if (confirmation.Chapter != timing.Chapter) { throw new SealedCasePersistenceException("wrong_chapter", 409); }
        var previous = ReadConfirmedPlans(connection, transaction, caseId, confirmation.Chapter).GetValueOrDefault(actorId);
        if (confirmation.ExpectedActionRevision != (previous?.Revision ?? 0))
        {
            throw new SealedCasePersistenceException("action_revision_conflict", 409);
        }
        var action = new SealedAction("sca_" + Guid.NewGuid().ToString("N"), actorId, confirmation.Chapter,
            confirmation.Intent, confirmation.TargetId, confirmation.Destination, confirmation.Method,
            confirmation.Condition, confirmation.Fallback, confirmation.LethalConfirmed,
            checked(confirmation.ExpectedActionRevision + 1), confirmation.PreparedIntent, confirmation.ResourceId, confirmation.Summary, confirmation.DefensePolicy, confirmation.ShelteredPosition);
        if (action.Summary is { } summary && (summary.Length > 500 || summary.Any(c => char.IsControl(c) && c is not '\n' and not '\r' and not '\t')))
        { throw new SealedCasePersistenceException("sealed_case_invalid_plan", 400); }
        try { action.ValidateForSubmission(); }
        catch (SealedCaseRuleException) { throw new SealedCasePersistenceException("sealed_case_invalid_plan", 400); }
        if (new[] { action.TargetId, action.Destination, action.Method, action.Condition?.EvidenceId, action.ResourceId }.Any(value => value?.Length > 128))
        {
            throw new SealedCasePersistenceException("sealed_case_invalid_plan", 400);
        }
        var world = ReadWorld(connection, transaction, caseId, revision);
        var actor = world.Checkpoint.Players.SingleOrDefault(player => player.Id == actorId)
            ?? throw new SealedCasePersistenceException("case_not_found", 404);
        // Target validation uses only the actor's own knowledge. Do not reject a
        // secretly dead/asleep/elsewhere target and thereby disclose world truth.
        if (action.TargetId is { } target && target != actorId && !actor.KnownPlayers.Contains(target, StringComparer.Ordinal))
        {
            throw new SealedCasePersistenceException("sealed_case_invalid_target", 400);
        }
        if (action.Intent == SealedIntent.Lethal && (action.Chapter < 2 || !action.LethalConfirmed || actor.ExtremeToken < 1))
        {
            throw new SealedCasePersistenceException("sealed_case_extreme_unavailable", 409);
        }
        return action;
    }

    private static void DemandRosterMember(SqliteConnection connection, SqliteTransaction transaction, string caseId, string accountId)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT 1 FROM sealed_case_rosters roster,json_each(roster.roster_json) member WHERE roster.case_id=$case AND json_extract(member.value,'$.accountId')=$account";
        command.Parameters.AddWithValue("$case", caseId); command.Parameters.AddWithValue("$account", accountId);
        if (command.ExecuteScalar() is null) { throw new SealedCasePersistenceException("case_not_found", 404); }
    }

    private static Dictionary<string, SealedAction> ReadConfirmedPlans(SqliteConnection connection,
        SqliteTransaction transaction, string caseId, int chapter)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = """
            SELECT actor_id,payload_json FROM sealed_case_events WHERE case_id=$case AND kind='confirmed-plan'
                AND json_extract(payload_json,'$.chapter')=$chapter ORDER BY revision
            """;
        command.Parameters.AddWithValue("$case", caseId); command.Parameters.AddWithValue("$chapter", chapter);
        using var reader = command.ExecuteReader(); var latest = new Dictionary<string, SealedAction>(StringComparer.Ordinal);
        while (reader.Read()) { latest[reader.GetString(0)] = JsonSerializer.Deserialize<SealedAction>(reader.GetString(1), Json)!; }
        return latest;
    }
}
