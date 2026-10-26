using Kufr.Backend.Features.Game;
using Microsoft.Data.Sqlite;

internal static class GameTeamTests
{
    internal static void Run()
    {
        var directory = Path.Combine(Path.GetTempPath(), "game-teams-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var store = new GameStore(directory);
            SeedXp(directory, "group-a", "a0", "a1", "b0", "b1");
            var createdResults = new GameTeamView[2];
            Parallel.For(0, 2, index => createdResults[index] = store.CreateTeamMatch("group-a", "a0",
                new("a1", ["b0", "b1"], "team-create-0001"), 100));
            var created = createdResults[0];
            Check(createdResults[0].Id == createdResults[1].Id, "concurrent idempotent creates resolve to one match");
            ExpectFault(() => store.CreateTeamMatch("group-a", "a0", new("a1", ["b1", "b0"], "team-create-0001"), 100),
                409, "operation id cannot be replayed with a different payload");
            Check(created.Status == "incoming" && created.AcceptedBy.SequenceEqual(["a0"]), "creator is preaccepted");
            var match = created;
            foreach (var user in new[] { "a1", "b0", "b1" })
            {
                match = store.MutateTeam(match.Id, "group-a", user, "accept", new("accept-" + user + "-001", match.Revision), 101);
            }
            Check(match.Status == "active" && match.AcceptedBy.Count == 4, "all four participants must accept");

            var now = 110L;
            GameTeamCommand finalReplay = new("unused-final-command", 0);
            for (var round = 1; round <= 8; round++)
            {
                if (round == 5)
                {
                    var baseline = store.ReadTeamMatch(match.Id, "group-a", "a0", now).OwnSparks;
                    foreach (var user in new[] { "a0", "a1" })
                    {
                        match = store.MutateTeam(match.Id, "group-a", user, "transfer",
                            new("transfer-" + user + "-001", match.Revision, GiveSpark: true), now);
                    }
                    var alice = store.ReadTeamMatch(match.Id, "group-a", "a0", now + 16);
                    Check(alice.OwnSparks == baseline, "simultaneous teammate transfers leave baseline unchanged");
                    match = alice; now += 16;
                }
                var firstHalf = (round - 1) % 4 < 2;
                foreach (var (user, choice) in new[] { ("a0", firstHalf ? "strike" : "trick"), ("a1", firstHalf ? "trick" : "shield"), ("b0", firstHalf ? "trick" : "shield"), ("b1", firstHalf ? "shield" : "strike") })
                {
                    var command = new GameTeamCommand($"choose-{round}-{user}-001", match.Revision, choice, round == 1 ? 1 : 0);
                    match = store.MutateTeam(match.Id, "group-a", user, "choose", command, now++);
                    if (round == 1 && user != "b1") { Check(match.CompletedRounds == 0, "round is hidden until all four choices are locked"); }
                    if (round == 8 && user == "b1") { finalReplay = command; }
                }
                Check(match.CompletedRounds == round, "each round reveals atomically after four locks");
            }
            Check(match.Status == "complete" && match.Rounds.Count == 8, "match completes after eight rounds");
            Check(match.Rounds[0].ChoiceB0 == "trick" && match.Rounds[0].ChoiceB1 == "shield"
                && match.Rounds[4].ChoiceB0 == "shield" && match.Rounds[4].ChoiceB1 == "trick", "opponents rotate after halftime");
            var before = ReadXp(directory, "group-a", "a0");
            var replay = store.MutateTeam(match.Id, "group-a", "b1", "choose", finalReplay, now + 1);
            Check(replay.Status == "complete" && ReadXp(directory, "group-a", "a0") == before, "replayed final choice awards XP exactly once");
            Check(ReadXp(directory, "group-a", "a0") == 70 && ReadXp(directory, "group-a", "b0") == 62,
                "completed match awards each team the exact win or loss XP once");
            var beforeCancelledMatch = ReadXp(directory, "group-a", "a0");
            var cancelled = store.CreateTeamMatch("group-a", "a0", new("a1", ["b0", "b1"], "team-create-0002"), now + 2);
            store.MutateTeam(cancelled.Id, "group-a", "a0", "cancel", new("team-cancel-0001", cancelled.Revision), now + 3);
            Check(ReadXp(directory, "group-a", "a0") == beforeCancelledMatch, "cancelled incomplete match awards no XP");
            var reopened = new GameStore(directory).ReadTeamMatch(match.Id, "group-a", "a0", now + 1);
            Check(reopened.CompletedRounds == 8 && reopened.Status == "complete", "completed match survives store restart");
            ExpectFault(() => store.ReadTeamMatch(match.Id, "group-a", "outsider", now), 404, "foreign member cannot view match");
            ExpectFault(() => store.ReadTeamMatch(match.Id, "group-b", "a0", now), 404, "foreign group cannot view match");
            var transferMatch = store.CreateTeamMatch("group-a", "a0", new("a1", ["b0", "b1"], "transfer-case-create"), now);
            foreach (var user in new[] { "a1", "b0", "b1" })
            { transferMatch = store.MutateTeam(transferMatch.Id, "group-a", user, "accept", new("transfer-case-accept-" + user, transferMatch.Revision), now++); }
            for (var round = 0; round < 4; round++)
            {
                var card = round < 2 ? "strike" : "trick";
                foreach (var user in new[] { "a0", "a1", "b0", "b1" })
                { transferMatch = store.MutateTeam(transferMatch.Id, "group-a", user, "choose", new($"transfer-case-{round}-{user}", transferMatch.Revision, card), now++); }
            }
            transferMatch = store.MutateTeam(transferMatch.Id, "group-a", "a0", "transfer", new("one-way-transfer-yes", transferMatch.Revision, GiveSpark: true), now);
            transferMatch = store.MutateTeam(transferMatch.Id, "group-a", "a1", "transfer", new("one-way-transfer-no", transferMatch.Revision), now);
            var donor = store.ReadTeamMatch(transferMatch.Id, "group-a", "a0", now + 16);
            var recipient = store.ReadTeamMatch(transferMatch.Id, "group-a", "a1", now + 16);
            Check(donor.OwnSparks == 1 && recipient.OwnSparks == 3, "a one-way offer preserves all four team sparks and can leave three with recipient");
            store.RetireIneligibleTeams("group-a", new HashSet<string> { "a0", "a1", "b0" });
            var retired = store.ReadTeamMatch(transferMatch.Id, "group-a", "a0", now + 17);
            Check(retired.Status == "cancelled" && retired.XpAwarded == 0, "ineligible teammate releases the match without XP");
            var restored = store.CreateTeamMatch("group-a", "a0", new("a1", ["b0", "b1"], "restored-team-create"), now + 18);
            Check(restored.Status == "incoming", "retired reservations do not block the surviving players");
            store.MutateTeam(restored.Id, "group-a", "a1", "cancel", new("partner-can-cancel", restored.Revision), now + 19);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(directory, true); }
    }

    private static void SeedXp(string directory, string group, params string[] users)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "mobile-game.sqlite") }.ToString());
        connection.Open(); using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO game_players(group_id,user_id,xp,matches) VALUES($g,$u,50,3) ON CONFLICT(group_id,user_id) DO UPDATE SET xp=50,matches=3";
        command.Parameters.AddWithValue("$g", group); var userParameter = command.Parameters.Add("$u", SqliteType.Text);
        foreach (var user in users) { userParameter.Value = user; command.ExecuteNonQuery(); }
    }
    private static int ReadXp(string directory, string group, string user)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "mobile-game.sqlite") }.ToString());
        connection.Open(); using var command = connection.CreateCommand(); command.CommandText = "SELECT xp FROM game_players WHERE group_id=$g AND user_id=$u";
        command.Parameters.AddWithValue("$g", group); command.Parameters.AddWithValue("$u", user); return Convert.ToInt32(command.ExecuteScalar());
    }
    private static void ExpectFault(Action action, int status, string message)
    {
        try { action(); } catch (GameFault fault) { Check(fault.Status == status, message); return; }
        throw new InvalidOperationException("Expected GameFault: " + message);
    }
    private static void Check(bool condition, string message) { if (!condition) { throw new InvalidOperationException("Game team test failed: " + message); } }
}
