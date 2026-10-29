using System.Text.Json;
using System.Text.Json.Serialization;
using Kufr.Backend.Features.Identity;
using Microsoft.Data.Sqlite;

namespace Kufr.Backend.Features.SealedCase;

internal sealed record SealedCaseRewardCollection(int SchemaVersion, IReadOnlyList<SealedCaseReward> Items);
internal sealed record SealedCaseReward(string CaseId, DateTimeOffset ReleasedAt, string Team,
    int ConfirmedPlans, int SuccessfulPlans,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] SealedCaseRewardScenario? Scenario = null);
internal sealed record SealedCaseRewardScenario(string Id, string Title, string TeamLabel);

internal sealed partial class SealedCaseStore
{
    internal SealedCaseRewardCollection ReadPersonalRewards(string groupId, string accountId,
        Func<CustomerAdmissionGuard> authorize, bool scenarioMetadata = false)
    {
        var items = new List<SealedCaseReward>(100);
        long? beforeCreatedAt = null;
        long? beforeRowId = null;
        while (items.Count < 100)
        {
            var candidates = ReadRewardCandidatePage(groupId, accountId, beforeCreatedAt, beforeRowId);
            if (candidates.Count == 0) { break; }
            foreach (var (caseId, member, _, _) in candidates)
            {
                var play = ReadPlay(caseId, groupId, accountId, authorize);
                if (!scenarioMetadata && play.Scenario?.Id is { } scenarioId && scenarioId != SealedScenarioCatalog.DefaultId) { continue; }
                if (play.Finale is not { Phase: "released", Reconstruction: { } reconstruction }
                    || play.FinaleEndsAt is not { } finaleEndsAt
                    || play.OwnTeam != member.Team.ToString())
                {
                    continue;
                }

                var reconstructedOwnChapters = reconstruction
                    .Where(result => result.Actor == member.PlayerId)
                    .GroupBy(result => result.Chapter)
                    .ToDictionary(group => group.Key, group => group.ToArray());
                var confirmedChapters = ReadConfirmedPlanChapters(caseId, member.PlayerId)
                    .Where(reconstructedOwnChapters.ContainsKey)
                    .ToArray();
                var successfulPlans = confirmedChapters.Count(chapter => reconstructedOwnChapters[chapter]
                    .Any(result => result.Result == nameof(SealedResult.Success)));
                items.Add(new(caseId, DateTimeOffset.FromUnixTimeSeconds(finaleEndsAt), member.Team.ToString(),
                    confirmedChapters.Length, successfulPlans, scenarioMetadata && play.Scenario is { } scenario
                        ? new(scenario.Id, scenario.Title, member.Team == SealedSecretTeam.Guardian ? scenario.GuardianLabel : scenario.ConspiratorLabel) : null));
                if (items.Count == 100) { break; }
            }

            if (items.Count == 100 || candidates.Count < 100) { break; }
            beforeCreatedAt = candidates[^1].CreatedAt;
            beforeRowId = candidates[^1].RowId;
        }

        return new(1, items);
    }

    private List<(string CaseId, SealedStoredRosterMember Member, long CreatedAt, long RowId)>
        ReadRewardCandidatePage(string groupId, string accountId, long? beforeCreatedAt, long? beforeRowId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT cases.id,rosters.roster_json,cases.created_at,cases.rowid
            FROM sealed_cases cases
            JOIN sealed_case_rosters rosters ON rosters.case_id=cases.id
            WHERE cases.group_id=$group AND cases.status<>'privacy_terminated'
                AND EXISTS(SELECT 1 FROM json_each(rosters.roster_json) member
                    WHERE json_extract(member.value,'$.accountId')=$account
                        AND COALESCE(json_extract(member.value,'$.isAi'),0)=0)
                AND ($beforeCreatedAt IS NULL OR cases.created_at<$beforeCreatedAt
                    OR (cases.created_at=$beforeCreatedAt AND cases.rowid<$beforeRowId))
            ORDER BY cases.created_at DESC,cases.rowid DESC
            LIMIT 100
            """;
        command.Parameters.AddWithValue("$group", groupId);
        command.Parameters.AddWithValue("$account", accountId);
        command.Parameters.AddWithValue("$beforeCreatedAt", (object?)beforeCreatedAt ?? DBNull.Value);
        command.Parameters.AddWithValue("$beforeRowId", (object?)beforeRowId ?? DBNull.Value);
        using var reader = command.ExecuteReader();
        var candidates = new List<(string CaseId, SealedStoredRosterMember Member, long CreatedAt, long RowId)>();
        while (reader.Read())
        {
            var members = JsonSerializer.Deserialize<SealedStoredRosterMember[]>(reader.GetString(1), Json) ?? [];
            var member = members.SingleOrDefault(value => value.AccountId == accountId && !value.IsAi);
            if (member is not null)
            {
                candidates.Add((reader.GetString(0), member, reader.GetInt64(2), reader.GetInt64(3)));
            }
        }
        return candidates;
    }

    private int[] ReadConfirmedPlanChapters(string caseId, string playerId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT payload_json FROM sealed_case_events
            WHERE case_id=$case AND actor_id=$player AND kind='confirmed-plan'
            ORDER BY revision
            """;
        command.Parameters.AddWithValue("$case", caseId);
        command.Parameters.AddWithValue("$player", playerId);
        using var reader = command.ExecuteReader();
        var chapters = new HashSet<int>();
        while (reader.Read())
        {
            var plan = JsonSerializer.Deserialize<SealedAction>(reader.GetString(0), Json);
            if (plan is not null) { chapters.Add(plan.Chapter); }
        }
        return chapters.ToArray();
    }
}
