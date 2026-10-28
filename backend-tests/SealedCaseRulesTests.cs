using Kufr.Backend.Features.SealedCase;

internal static class SealedCaseRulesTests
{
    internal static void Run()
    {
        var tutorial = SealedCaseRules.Schedule("first", 8);
        Equal(3, tutorial.Chapters); Equal(480, tutorial.PlanningSeconds); Equal(120, tutorial.QuietSeconds);
        Equal(2700, tutorial.TotalSeconds);
        Equal(16500, SealedCaseRules.Schedule("full", 12).TotalSeconds);
        Equal(20100, SealedCaseRules.Schedule("full", 20).TotalSeconds);
        Equal(23700, SealedCaseRules.Schedule("full", 30).TotalSeconds);
        Throws(() => SealedCaseRules.Schedule("full", 7));
        Throws(() => SealedCaseRules.Schedule("full", 31));
        Equal(2, SealedCaseRules.Conspirators(8));
        Equal(6, SealedCaseRules.Conspirators(30));
        Throws(() => SealedCaseRules.Conspirators(7));
        Throws(() => SealedCaseRules.Conspirators(31));
        Throws(() => SealedCaseRules.Schedule("first", 13));
        Throws(() => SealedCaseRules.Schedule("full", 7));
        Equal(2, SealedCaseRules.Conspirators(8)); Equal(3, SealedCaseRules.Conspirators(13));
        Equal(4, SealedCaseRules.Conspirators(18)); Equal(5, SealedCaseRules.Conspirators(23)); Equal(6, SealedCaseRules.Conspirators(28));
        roster_kits_are_balanced_and_secret_teams_are_independent();
        var players = Enumerable.Range(1, 8).Select(i => $"p{i}").ToArray();
        Throws(() => SealedCaseRules.ValidateVote(players, "p1", ["p1", "p2"]));
        Throws(() => SealedCaseRules.ValidateVote(players, "p1", ["p2", "p2"]));
        Throws(() => SealedCaseRules.ValidateVote(players, "p1", ["p2"]));
        SealedCaseRules.ValidateVote(players, "p1", []); // explicit abstention
        SealedCaseRules.ValidateVote(players, "p1", ["p2", "p3"]);
        var votes = new Dictionary<string, string[]> { ["p1"] = ["p2", "p3"], ["p4"] = ["p2", "p3"] };
        var result = SealedCaseRules.Tally(players, votes, "fixed-server-seed");
        Equal("p2,p3", string.Join(',', result.Selected.Order())); Equal(false, result.NeedsRunoff);
        var tied = SealedCaseRules.Tally(players, new Dictionary<string, string[]>(), "fixed-server-seed");
        Equal(true, tied.NeedsRunoff); Equal(0, tied.Selected.Count); // no automatic cast/coin flip before runoff
        Equal(true, SealedCaseRules.GuardiansWin(["p2", "p3"], ["p2", "p3"], 2));
        Equal(false, SealedCaseRules.GuardiansWin(["p2", "p4"], ["p2", "p3"], 2));
        var clock = new SealedChapterClock(1000, 480, 120);
        Equal("planning", clock.Phase(1479)); Equal("quiet", clock.Phase(1480)); Equal("locked", clock.Phase(1600));
        clock.Pause(1400); Equal("paused", clock.Phase(2000)); clock.Resume(2000);
        Equal("planning", clock.Phase(2079)); Equal("quiet", clock.Phase(2080));
        Console.WriteLine("SealedCase schedule, final-vote and clock checks passed.");
    }

    private static void roster_kits_are_balanced_and_secret_teams_are_independent()
    {
        foreach (var count in Enumerable.Range(8, 23))
        {
            var players = Enumerable.Range(1, count).Select(i => $"account-{i:D2}").ToArray();
            var first = SealedCaseRosterAssignments.Assign(players, "server-seed");
            var retry = SealedCaseRosterAssignments.Assign(players.Reverse().ToArray(), "server-seed");
            Equal(System.Text.Json.JsonSerializer.Serialize(first), System.Text.Json.JsonSerializer.Serialize(retry));
            var conspirators = SealedCaseRules.Conspirators(count);
            Equal(conspirators, first.Count(x => x.Team == SealedSecretTeam.Conspirator));
            Equal(count - conspirators, first.Count(x => x.Team == SealedSecretTeam.Guardian));
            var kitCounts = first.GroupBy(x => x.Kit).Select(group => group.Count()).ToArray();
            if (kitCounts.Max() - kitCounts.Min() > 1)
                throw new InvalidOperationException("Public kits must remain balanced within one seat.");
            var publicJson = System.Text.Json.JsonSerializer.Serialize(SealedCaseRosterAssignments.Public(first));
            if (publicJson.Contains("Conspirator", StringComparison.Ordinal) || publicJson.Contains("Team", StringComparison.Ordinal))
                throw new InvalidOperationException("Public roster must not disclose secret team assignments.");
        }
        var playersAt30 = Enumerable.Range(1, 30).Select(i => $"account-{i:D2}").ToArray();
        Equal(8, SealedCaseRosterAssignments.Assign(playersAt30.Take(8).ToArray(), "seed").Count);
        Equal(30, SealedCaseRosterAssignments.Assign(playersAt30, "seed").Count);
        Throws(() => SealedCaseRosterAssignments.Assign(playersAt30.Take(7).ToArray(), "seed"));
        Throws(() => SealedCaseRosterAssignments.Assign(playersAt30.Append("account-31").ToArray(), "seed"));
        Throws(() => SealedCaseRosterAssignments.Assign(playersAt30.Append(playersAt30[0]).ToArray(), "seed"));
    }
    private static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"Sealed rules expected {expected}, actual {actual}"); }
    private static void Throws(Action action) { try { action(); } catch (SealedCaseRuleException) { return; } throw new InvalidOperationException("Expected rule rejection"); }
}
