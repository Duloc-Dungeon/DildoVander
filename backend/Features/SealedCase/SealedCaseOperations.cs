using System.Text.Json;
using Kufr.Backend.Features.Identity;
using Microsoft.Data.Sqlite;

namespace Kufr.Backend.Features.SealedCase;

internal sealed record SealedOversightVote(int Chapter, string? Nominee);
internal sealed record SealedDisconnectRequest(string OperationId, string PlayerId);
internal sealed record SealedOversightRequest(string OperationId, int Chapter, string? Nominee);
internal sealed partial class SealedCaseStore
{
    internal SealedCaseActionReceipt RecommendOversight(string caseId, string group, string actor, string operationId,
        int chapter, string? nominee, Func<CustomerAdmissionGuard> authorize)
        => AppendSocial(caseId, group, actor, operationId, "oversight-vote", new SealedOversightVote(chapter, nominee), authorize,
            (connection, transaction, revision) =>
            {
                var timing = ReadTiming(connection, transaction, caseId, revision, clock.GetUtcNow().ToUnixTimeSeconds());
                if (!timing.CanSubmit || timing.Chapter != chapter) { throw new SealedCasePersistenceException("chapter_locked", 409); }
                var members = RosterMembers(connection, transaction, caseId);
                if (nominee is not null && (nominee == actor || members.All(m => m.PlayerId != nominee)))
                { throw new SealedCasePersistenceException("sealed_case_invalid_ballot", 400); }
                // Physical eligibility is intentionally not checked at acceptance.
                return new SealedOversightVote(chapter, nominee);
            });

    private static void ElectOversight(SqliteConnection connection, SqliteTransaction transaction, string caseId,
        int chapter, int chapters, SealedGame game)
    {
        if (game.Scenario?.Oversight is null || chapter >= chapters) { return; }
        using var query = connection.CreateCommand(); query.Transaction = transaction;
        query.CommandText = "SELECT actor_id,payload_json FROM sealed_case_events WHERE case_id=$case AND kind='oversight-vote' ORDER BY revision";
        query.Parameters.AddWithValue("$case", caseId); using var reader = query.ExecuteReader(); var votes = new Dictionary<string, string?>(StringComparer.Ordinal);
        while (reader.Read())
        {
            var vote = JsonSerializer.Deserialize<SealedOversightVote>(reader.GetString(1), Json)!;
            if (vote.Chapter == chapter) { votes[reader.GetString(0)] = vote.Nominee; }
        }
        var prior = game.Scenario.Oversight.GetValueOrDefault(chapter);
        var counts = votes.Values.Where(id => id is not null && id != prior && game.Players.TryGetValue(id, out var player)
            && player.State != SealedPlayerState.Echo).GroupBy(id => id!, StringComparer.Ordinal).OrderByDescending(g => g.Count()).ToArray();
        game.Scenario.Oversight[chapter + 1] = counts.Length > 0 && counts[0].Count() >= (game.Players.Count + 2) / 3
            && (counts.Length == 1 || counts[0].Count() > counts[1].Count()) ? counts[0].Key : null;
    }

    private static HashSet<string> Disconnected(SqliteConnection connection, SqliteTransaction transaction, string caseId)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT json_extract(payload_json,'$.playerId') FROM sealed_case_events WHERE case_id=$case AND kind='participant-disconnected'";
        command.Parameters.AddWithValue("$case", caseId); using var reader = command.ExecuteReader(); var ids = new HashSet<string>(StringComparer.Ordinal);
        while (reader.Read()) { ids.Add(reader.GetString(0)); } return ids;
    }

    internal SealedCaseActionReceipt Disconnect(string caseId, string group, string actor, string operationId,
        string player, Func<CustomerAdmissionGuard> authorize)
        => AppendSocial(caseId, group, actor, operationId, "participant-disconnected", new { playerId = player }, authorize,
            (connection, transaction, _) =>
            {
                using var query = connection.CreateCommand(); query.Transaction = transaction;
                query.CommandText = "SELECT json_extract(setup_json,'$.createdBy') FROM sealed_cases WHERE id=$case";
                query.Parameters.AddWithValue("$case", caseId);
                if ((string?)query.ExecuteScalar() != actor) { throw new SealedCasePersistenceException("sealed_case_host_required", 403); }
                if (RosterMembers(connection, transaction, caseId).All(m => m.PlayerId != player)) { throw new SealedCasePersistenceException("case_not_found", 404); }
                return new { playerId = player };
            }, allowOperator: true);

    internal object ReadModerator(string caseId, string group, string actor, Func<CustomerAdmissionGuard> authorize)
    {
        using var connection = Open(); using var transaction = connection.BeginTransaction(deferred: true);
        CustomerAdmissionGuard? moderator = null;
        using var admission = AcquireCaseAccess(connection, transaction, caseId,
            () => moderator = DemandPlayerAdmission(authorize, actor, group), actor);
        var revision = ReadCaseRevision(connection, transaction, caseId, group);
        if (!moderator!.Proof.Roles.Any(role => role is AccountRoles.Owner or AccountRoles.Administrator)
            || RosterMembers(connection, transaction, caseId).Any(m => m.AccountId == actor))
        { throw new SealedCasePersistenceException("sealed_case_moderator_required", 403); }
        var world = ReadWorld(connection, transaction, caseId, revision);
        var recap = Recap(connection, transaction, caseId);
        transaction.Commit(); return new { world.Checkpoint, reconstruction = recap };
    }
}
