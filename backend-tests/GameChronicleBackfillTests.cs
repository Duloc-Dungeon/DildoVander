using System.Text.Json;
using Kufr.Backend.Features.Chronicle;
using Kufr.Backend.Features.Game;
using Microsoft.Data.Sqlite;

internal static class GameChronicleBackfillTests
{
    internal static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "game-chronicle-backfill-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var ids = SeedLegacyCompletionRows(root);
            var game = new GameStore(root);
            var queued = 0;
            // A corrupt row consumes a bounded sweep slot but cannot starve the
            // next valid event on subsequent passes, even with a one-row batch.
            for (var pass = 0; pass < 12; pass++) { queued += game.BackfillChronicleOutbox(1); }
            Check(queued == 5, "bounded backfill queues completed duel, team and personal boss history");
            Check(game.BackfillChronicleOutbox(2) == 0, "backfill replay does not create duplicate outbox entries");
            Check(game.ReadChronicleBackfillSkippedCount() == 2,
                "missing-time and unreadable completed sources are durably marked unknown instead of blocking the sweep or receiving fabricated dates");

            var chronicle = new ChronicleStore(root);
            game.DrainChronicleOutbox(chronicle);
            var first = Reveal(chronicle, "alice", "yolo", ids.FirstYolo);
            var second = Reveal(chronicle, "alice", "yolo", ids.RematchYolo);
            var third = Reveal(chronicle, "alice", "yolo", ids.LatestYolo);
            Check(first.OccurredAt == 1000 && second.OccurredAt == 2000 && third.OccurredAt == 3000,
                "backfill preserves each original completion time");
            Check(first.Awards.Any(award => award.Id == "expert-victim")
                && !second.Awards.Any(award => award.Id == "expert-victim"),
                "the earliest completed YOLO event retains the group-scoped legacy title");
            Check(second.Awards.Any(award => award.Id == "first-win-yolo")
                && !third.Awards.Any(award => award.Id == "first-win-yolo"),
                "first-win award attaches to the earliest historical victory even when source rows were inserted out of order");
            Check(second.Awards.Any(award => award.Id == "mind-reader")
                && second.Awards.Any(award => award.Id == "successful-rematch"
                    && award.Evidence.Contains(ids.FirstYolo, StringComparison.Ordinal))
                && second.Awards.Any(award => award.Id == "remembered-opponent"
                    && award.Evidence.Contains(ids.FirstYolo, StringComparison.Ordinal)),
                "historical mind-reader and rival titles preserve their evidence and linked first-match provenance");
            Check(first.Participants.Single(participant => participant.AccountId == "alice").VisualReferenceVersion == "unknown",
                "backfilled matches without a frozen profile remain honestly marked unknown");
            Check(second.DecisiveChoice == "Alice: Finish — Consequence",
                "a saved YOLO decision is rendered with frozen public text rather than exposing its account-id reference");
            Check(Reveal(chronicle, "alice", "team", ids.TeamMatch).Awards.Any(award => award.Id == "first-team-match"),
                "completed historical team memory is backfilled with its original milestone");
            Check(Reveal(chronicle, "alice", "boss", ids.BossAttempt).Awards.Any(award => award.Id == "first-boss-victory"),
                "completed historical personal boss attempt is backfilled without current profile lookups");
            Check(chronicle.FindSource("alice", "duel", ids.MissingTime) is null,
                "missing historical occurrence time is not invented as a timeline event");
            Check(chronicle.FindSource("alice", "yolo", ids.InvalidState) is null,
                "unreadable historical state is skipped without blocking other source records");

            var future = PlayQuickMatch(game);
            game.DrainChronicleOutbox(chronicle);
            Check(chronicle.FindSource("future-a", "duel", future) is not null,
                "newly completed games still use the transactional outbox after backfill");
            Console.WriteLine("Game chronicle backfill: bounded replay, original timestamps, earliest awards, evidence links, honest unknown snapshots, unknown-time marker, team/boss history and future completion passed.");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    private static (string FirstYolo, string RematchYolo, string LatestYolo, string TeamMatch,
        string BossAttempt, string MissingTime, string InvalidState) SeedLegacyCompletionRows(string root)
    {
        var first = Guid.NewGuid().ToString("N");
        var rematch = Guid.NewGuid().ToString("N");
        var latest = Guid.NewGuid().ToString("N");
        var boss = Guid.NewGuid().ToString("N");
        const string group = "backfill-group";
        using var connection = new SqliteConnection($"Data Source={Path.Combine(root, "mobile-game.sqlite")}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE game_matches(id TEXT PRIMARY KEY,group_id TEXT NOT NULL,challenger_id TEXT NOT NULL,opponent_id TEXT NOT NULL,state_json TEXT NOT NULL,created_at INTEGER NOT NULL);
            CREATE TABLE game_team_matches(id TEXT PRIMARY KEY,group_id TEXT NOT NULL,state_json TEXT NOT NULL,created_at INTEGER NOT NULL);
            CREATE TABLE game_boss_attempts(id TEXT PRIMARY KEY,group_id TEXT NOT NULL,user_id TEXT NOT NULL,state_json TEXT NOT NULL);
            """;
        command.ExecuteNonQuery();
        InsertMatch(connection, first, group, "opponent", 1000, opponentBid: 0, challengerBid: 1);
        InsertMatch(connection, rematch, group, "challenger", 2000, opponentBid: 1, challengerBid: 0);
        InsertMatch(connection, latest, group, "opponent", 3000, opponentBid: 0, challengerBid: 0);
        var teamState = JsonSerializer.Serialize(new
        {
            groupId = group, a = new[] { "alice", "carol" }, b = new[] { "bob", "dana" },
            status = "complete", completedAt = 4000, createdAt = 500, result = "a", frozenProfiles = new { },
            rounds = new[] { new { index = 1, teamAPoints = 2, teamBPoints = 1 } }
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        InsertRow(connection, "game_team_matches", "team-old", group, "", teamState, 500);
        var bossState = JsonSerializer.Serialize(new
        {
            id = boss, groupId = group, userId = "alice", status = "complete", completedAt = 5000,
            result = "win", chronicleProfile = (object?)null,
            rounds = new[] { new { index = 1, playerChoice = "strike", opponentChoice = "trick", winner = "player" } }
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        InsertBoss(connection, boss, group, "alice", bossState);
        var missingTime = Guid.NewGuid().ToString("N");
        var missingState = JsonSerializer.Serialize(new
        {
            mode = "quick", status = "complete", groupId = group, challengerId = "alice", opponentId = "bob",
            challengerName = "Alice", opponentName = "Bob", result = "challenger"
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        InsertMatch(connection, missingTime, group, "challenger", null, opponentBid: 0, challengerBid: 0, missingState);
        var invalidState = Guid.NewGuid().ToString("N");
        InsertMatch(connection, invalidState, group, "challenger", 3500, opponentBid: 0, challengerBid: 0,
            "{\"mode\":\"yolo\",\"status\":\"complete\",\"completedAt\":3500,\"rounds\":{\"not\":\"an array\"}}");
        return (first, rematch, latest, "team-old", boss, missingTime, invalidState);
    }

    private static void InsertMatch(SqliteConnection connection, string id, string group, string result,
        long? completedAt, int opponentBid, int challengerBid, string? overrideJson = null)
    {
        var state = overrideJson ?? JsonSerializer.Serialize(new
        {
            mode = "yolo", status = "complete", groupId = group, challengerId = "alice", opponentId = "bob",
            challengerName = "Alice", opponentName = "Bob", completedAt, result,
            yoloOutcome = "winner", yoloWinnerId = result == "challenger" ? "alice" : "bob",
            yoloVerdict = "The saved verdict", yoloDecisiveChoice = "alice:3",
            challengerYoloDecisions = new[]
            {
                new { act = 1, choice = "levitate", title = "Plan", benefit = "Benefit", cost = "Cost", consequence = "Consequence" },
                new { act = 2, choice = "protect", title = "Protection", benefit = "Benefit", cost = "Cost", consequence = "Consequence" },
                new { act = 3, choice = "finish", title = "Finish", benefit = "Benefit", cost = "Cost", consequence = "Consequence" }
            },
            opponentYoloDecisions = new[]
            {
                new { act = 1, choice = "tray", title = "Plan", benefit = "Benefit", cost = "Cost", consequence = "Consequence" },
                new { act = 2, choice = "sabotage", title = "Sabotage", benefit = "Benefit", cost = "Cost", consequence = "Consequence" },
                new { act = 3, choice = "finish", title = "Finish", benefit = "Benefit", cost = "Cost", consequence = "Consequence" }
            },
            yoloBeats = new[] { new { text = "An authored event", detailRefs = Array.Empty<string>() } },
            rounds = new[] { new { challengerBid, opponentBid, winner = result } }
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO game_matches(id,group_id,challenger_id,opponent_id,state_json,created_at) VALUES($id,$group,'alice','bob',$state,$created)";
        command.Parameters.AddWithValue("$id", id); command.Parameters.AddWithValue("$group", group);
        command.Parameters.AddWithValue("$state", state); command.Parameters.AddWithValue("$created", completedAt ?? 0);
        command.ExecuteNonQuery();
    }

    private static void InsertRow(SqliteConnection connection, string table, string id, string group,
        string user, string body, long createdAt)
    {
        using var command = connection.CreateCommand();
        command.CommandText = table == "game_team_matches"
            ? "INSERT INTO game_team_matches(id,group_id,state_json,created_at) VALUES($id,$group,$body,$created)"
            : throw new InvalidOperationException("Unsupported backfill fixture table.");
        command.Parameters.AddWithValue("$id", id); command.Parameters.AddWithValue("$group", group);
        command.Parameters.AddWithValue("$body", body); command.Parameters.AddWithValue("$created", createdAt);
        command.ExecuteNonQuery();
    }

    private static void InsertBoss(SqliteConnection connection, string id, string group, string user, string body)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO game_boss_attempts(id,group_id,user_id,state_json) VALUES($id,$group,$user,$body)";
        command.Parameters.AddWithValue("$id", id); command.Parameters.AddWithValue("$group", group);
        command.Parameters.AddWithValue("$user", user); command.Parameters.AddWithValue("$body", body);
        command.ExecuteNonQuery();
    }

    private static ChronicleEventView Reveal(ChronicleStore chronicle, string owner, string kind, string sourceId)
    {
        var locked = chronicle.FindSource(owner, kind, sourceId)
            ?? throw new InvalidOperationException($"Missing {kind} source {sourceId}.");
        Check(!locked.Revealed && string.IsNullOrEmpty(locked.Summary), "backfilled item starts behind its reveal gate");
        Check(chronicle.Reveal(owner, locked.Id), "backfilled owner can reveal their own event");
        return chronicle.Read(owner, locked.Id)!;
    }

    private static string PlayQuickMatch(GameStore store)
    {
        const string group = "future-game-group";
        var challenge = store.Challenge(group, "future-a", "Future A", "future-b", "Future B",
            new("future-b", "quick", Guid.NewGuid().ToString("N")), 6000);
        var state = store.Mutate(challenge.Id, group, "future-b", "accept",
            new(Guid.NewGuid().ToString("N"), challenge.Revision), 6001);
        var now = 6002L;
        while (state.Status == "active")
        {
            var left = store.Mutate(state.Id, group, "future-a", "choose",
                new(Guid.NewGuid().ToString("N"), state.Revision, Choice: "strike"), now);
            state = store.Mutate(state.Id, group, "future-b", "choose",
                new(Guid.NewGuid().ToString("N"), left.Revision, Choice: "trick"), now++);
        }
        Check(state.Status == "complete", "future quick game completes after backfill");
        return state.Id;
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) { throw new InvalidOperationException("Game Chronicle backfill test failed: " + message); }
    }
}
