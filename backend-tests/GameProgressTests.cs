using Kufr.Backend.Features.Game;
using Microsoft.Data.Sqlite;

internal static class GameProgressTests
{
    internal static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "mobile-game-progress-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new GameStore(root);
            PlayQuickMatch(store, "progress-one", true, 10);
            PlayQuickMatch(store, "progress-two", false, 20);
            var xp = store.Lobby("group-a", "alice", "Alice", []).Player.Xp;
            var peers = new Dictionary<string, string>(StringComparer.Ordinal) { ["alice"] = "Alice", ["bob"] = "Bob" };
            var progress = store.ReadProgress("group-a", "alice", peers);
            var sparks = progress.Pending.Single(milestone => milestone.Id == "sparks");
            var operation = Guid.NewGuid().ToString("D");
            store.AcknowledgeProgress("group-a", "alice", new(operation, progress.Revision, sparks.Id), peers.Count);
            var acknowledged = store.ReadProgress("group-a", "alice", peers);
            Require(acknowledged.Pending.All(milestone => milestone.Id != "sparks"), "milestone acknowledgement hides that message");
            Require(acknowledged.Messages.Any(milestone => milestone.Id == "sparks"), "acknowledged milestone remains in history");
            Require(store.Lobby("group-a", "alice", "Alice", []).Player.Xp == xp, "milestone acknowledgement grants no XP");
            store.AcknowledgeProgress("group-a", "alice", new(operation, progress.Revision, sparks.Id), peers.Count);
            var reopened = new GameStore(root);
            Require(reopened.ReadProgress("group-a", "alice", peers).Pending.All(milestone => milestone.Id != "sparks"),
                "milestone acknowledgement survives restart and idempotent replay");
            var foreign = reopened.ReadProgress("group-b", "alice", peers);
            Require(foreign.Pending.All(milestone => milestone.Id != "sparks"), "milestones are scoped to launch group");
            try
            {
                reopened.AcknowledgeProgress("group-b", "alice", new(Guid.NewGuid().ToString("D"), foreign.Revision, "sparks"), peers.Count);
                throw new InvalidOperationException("Progress test failed: foreign group acknowledgement was accepted");
            }
            catch (GameFault fault) when (fault.Code == "game_milestone_unavailable") { }

            reopened.DeleteAccount("alice");
            using var connection = new SqliteConnection($"Data Source={Path.Combine(root, "mobile-game.sqlite")}");
            connection.Open();
            using var count = connection.CreateCommand();
            count.CommandText = "SELECT COUNT(*) FROM game_progress_seen WHERE user_id='alice'";
            Require(Convert.ToInt32(count.ExecuteScalar()) == 0, "account erasure removes acknowledgement history");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    private static void PlayQuickMatch(GameStore store, string prefix, bool aliceWins, long now)
    {
        var challenge = store.Challenge("group-a", "alice", "Alice", "bob", "Bob",
            new("bob", "quick", $"{prefix}-challenge"), now);
        var state = store.Mutate(challenge.Id, "group-a", "bob", "accept", new($"{prefix}-accept", challenge.Revision), now + 1);
        for (var round = 0; round < 7; round++)
        {
            var first = store.Mutate(challenge.Id, "group-a", "alice", "choose",
                new($"{prefix}-alice-{round}", state.Revision, Choice: aliceWins ? "strike" : "shield"), now + 2 + round);
            state = store.Mutate(challenge.Id, "group-a", "bob", "choose",
                new($"{prefix}-bob-{round}", first.Revision, Choice: "trick"), now + 2 + round);
        }
        Require(state.Status == "complete", "progress fixture match completes");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException($"Progress test failed: {message}");
    }
}
