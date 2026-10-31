using Kufr.Backend.Features.Game;
using Microsoft.Data.Sqlite;
using System.Text.Json;

internal static class GameArcadeTests
{
    internal static void Run()
    {
        foreach (var version in new[] { GameArcadeRules.Version, GameArcadeRules.HarderVersion })
        {
            VerifyVectors(version);
        }
        VerifyVersionedRankings();

        var fixtureTaps = Enumerable.Range(0, 65).Select(index => index * 5).ToArray();
        Require(GameArcadeRules.Verify(1, 321, fixtureTaps) == 3,
            "seed 1 with taps every 5 ticks collides at tick 321 after clearing three gates");
        ExpectFault(() => GameArcadeRules.Verify(1, 320, fixtureTaps), "early stop is rejected");
        ExpectFault(() => GameArcadeRules.Verify(1, 321, [0, 0, 1]), "unused taps are rejected");
        ExpectFault(() => GameArcadeRules.Verify(1, 321, [5, 4]), "descending taps are rejected");

        RunLegacyStoreChecks(fixtureTaps);
    }

    private static void VerifyVectors(string version)
    {
        var vectorPath = Path.Combine(AppContext.BaseDirectory, "fixtures", version + ".json");
        using var vectorDocument = JsonDocument.Parse(File.ReadAllText(vectorPath));
        Require(vectorDocument.RootElement.GetProperty("rulesVersion").GetString() == version,
            "shared replay vectors use the backend rules version");
        foreach (var vector in vectorDocument.RootElement.GetProperty("vectors").EnumerateArray())
        {
            var seed = vector.GetProperty("seed").GetUInt32();
            var endTick = vector.GetProperty("endTick").GetInt32();
            var taps = vector.GetProperty("taps").EnumerateArray().Select(tap => tap.GetInt32()).ToArray();
            var expectedScore = vector.GetProperty("score").GetInt32();
            Require(GameArcadeRules.Verify(seed, endTick, taps, version) == expectedScore,
                $"{version} shared replay vector seed={seed}, endTick={endTick} matches expected score");
            if (version == GameArcadeRules.Version && endTick == GameArcadeRules.MaximumTick)
            {
                ExpectFault(() => GameArcadeRules.Verify(seed, endTick, taps, GameArcadeRules.HarderVersion),
                    "legacy edge trajectories collide earlier under narrower v2 rules");
            }
        }
    }

    private static void RunLegacyStoreChecks(int[] fixtureTaps)
    {
        var root = Path.Combine(Path.GetTempPath(), "arcade-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new GameStore(root);
            var issued = store.IssueArcadeRun("group-a", "alice", "operation-123", 1_000);
            Require(issued.ExpiresAt == 7_201_000, "ticket expiry is exactly two hours in Unix milliseconds");
            var retry = store.IssueArcadeRun("group-a", "alice", "operation-123", 1_001);
            Require(issued == retry, "issuance retry returns the same ticket");
            var prefetched = store.IssueArcadeRun("group-a", "alice", "operation-456", 1_001);
            for (var index = 0; index < GameArcadeRules.MaximumOutstandingRuns - 2; index++)
            {
                store.IssueArcadeRun("group-a", "alice", $"operation-filler-{index:D2}", 1_001);
            }
            ExpectFault(() => store.IssueArcadeRun("group-a", "alice", "operation-over-limit", 1_001),
                "issuance caps uncompleted tickets at 64 per account");
            ExpectFault(() => store.FinishArcadeRun("group-a", "bob", issued.RunId, 321, fixtureTaps, 1_100),
                "another account cannot read or finish a ticket");
            using var connection = new SqliteConnection($"Data Source={Path.Combine(root, "mobile-game.sqlite")}");
            connection.Open();
            using (var setSeed = connection.CreateCommand())
            {
                setSeed.CommandText = "UPDATE game_arcade_runs SET seed=1 WHERE run_id=$run";
                setSeed.Parameters.AddWithValue("$run", issued.RunId);
                setSeed.ExecuteNonQuery();
            }
            ExpectFault(() => store.FinishArcadeRun("group-a", "alice", issued.RunId, 320, fixtureTaps, 1_100),
                "a client cannot claim a score from an early replay");
            using (var noScore = connection.CreateCommand())
            {
                noScore.CommandText = "SELECT COUNT(*) FROM game_arcade_bests WHERE group_id='group-a' AND user_id='alice'";
                Require(Convert.ToInt32(noScore.ExecuteScalar()) == 0, "invalid replay awards no score");
            }
            var receipt = store.FinishArcadeRun("group-a", "alice", issued.RunId, 321, fixtureTaps, 1_100);
            Require(receipt == new ArcadeFinishReceipt(3, 3, true), "first valid finish establishes its group best");
            Require(store.FinishArcadeRun("group-a", "alice", issued.RunId, 321, fixtureTaps, 1_200) == receipt,
                "identical finish retry returns its saved receipt");
            ExpectFault(() => store.FinishArcadeRun("group-a", "alice", issued.RunId, 321, [0, 5], 1_200),
                "conflicting replay cannot reuse a completed ticket");
            using (var addTie = connection.CreateCommand())
            {
                addTie.CommandText = "INSERT INTO game_arcade_bests(group_id,user_id,score) VALUES('group-a','bob',3),('foreign','carol',99)";
                addTie.ExecuteNonQuery();
            }
            var ranking = store.ReadArcadeRanking("group-a", "alice", [("alice", "Alice"), ("bob", "Bob")]);
            Require(ranking.Best == 3 && ranking.Ranking.Count == 2
                && ranking.Ranking.All(row => row.Rank == 1), "eligible same-group ties share first rank");
            Require(store.ReadArcadeRanking("group-b", "alice", [("alice", "Alice")]).Best == 0,
                "best scores never cross group boundaries");
            var afterFinish = store.IssueArcadeRun("group-a", "alice", "operation-789", 1_200);
            Require(afterFinish.RunId != prefetched.RunId, "a verified finish frees an issuance slot");
            var otherGroup = store.IssueArcadeRun("group-b", "bob", "operation-123", 1_200);
            Require(otherGroup.RunId != issued.RunId, "run tickets are isolated by group");
            using var exists = connection.CreateCommand();
            exists.CommandText = "SELECT COUNT(*) FROM game_arcade_runs WHERE run_id=$run AND user_id='alice' AND group_id='group-a'";
            exists.Parameters.AddWithValue("$run", issued.RunId);
            Require(Convert.ToInt32(exists.ExecuteScalar()) == 1, "ticket persists with its owner and group");
            _ = store.IssueArcadeRun("group-c", "bob", "operation-expired", 1_000);
            _ = store.IssueArcadeRun("group-c", "bob", "operation-after-expiry", 7_201_001);
            using (var pruned = connection.CreateCommand())
            {
                pruned.CommandText = "SELECT COUNT(*) FROM game_arcade_runs WHERE group_id='group-c' AND user_id='bob'";
                Require(Convert.ToInt32(pruned.ExecuteScalar()) == 1, "issuance prunes expired tickets and receipts");
            }
            using (var expiredReceipt = connection.CreateCommand())
            {
                expiredReceipt.CommandText = "SELECT COUNT(*) FROM game_arcade_runs WHERE run_id=$run";
                expiredReceipt.Parameters.AddWithValue("$run", issued.RunId);
                Require(Convert.ToInt32(expiredReceipt.ExecuteScalar()) == 0, "completed receipts are removed at ticket expiry");
            }
            store.DeleteAccount("alice");
            using var erased = connection.CreateCommand();
            erased.CommandText = "SELECT (SELECT COUNT(*) FROM game_arcade_runs WHERE user_id='alice')+(SELECT COUNT(*) FROM game_arcade_bests WHERE user_id='alice')";
            Require(Convert.ToInt32(erased.ExecuteScalar()) == 0, "account erasure removes arcade tickets, receipts and bests");
            var untouchedRoot = Path.Combine(root, "without-arcade-tables");
            new GameStore(untouchedRoot).DeleteAccount("account-without-arcade");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    private static void VerifyVersionedRankings()
    {
        var root = Path.Combine(Path.GetTempPath(), "arcade-versions-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            // Simulate a database created by the released v1 server.
            using var connection = new SqliteConnection($"Data Source={Path.Combine(root, "mobile-game.sqlite")}");
            connection.Open();
            using (var legacy = connection.CreateCommand())
            {
                legacy.CommandText = """
                    CREATE TABLE game_arcade_bests(group_id TEXT NOT NULL,user_id TEXT NOT NULL,score INTEGER NOT NULL,PRIMARY KEY(group_id,user_id));
                    INSERT INTO game_arcade_bests VALUES('group-a','alice',100);
                    """;
                legacy.ExecuteNonQuery();
            }
            var store = new GameStore(root);
            var members = new[] { ("alice", "Alice") };
            Require(store.ReadArcadeRanking("group-a", "alice", members).Best == 100, "existing v1 best survives upgrade");
            Require(store.ReadArcadeRanking("group-a", "alice", members, "spark-v2").Best == 0, "v2 starts with its own record");
            var pending = store.IssueArcadeRun("group-a", "alice", "pending-legacy", 1000);
            var harder = store.IssueArcadeRun("group-a", "alice", "harder-operation", 1000, "spark-v2");
            Require(pending.RulesVersion == "spark-v1" && harder.RulesVersion == "spark-v2", "omitted version remains v1");
            Require(store.IssueArcadeRun("group-a", "alice", "harder-operation", 1001, "spark-v2") == harder,
                "v2 issuance retry returns the same ticket");
            try
            {
                store.IssueArcadeRun("group-a", "alice", "harder-operation", 1001);
                throw new InvalidOperationException("cross-version operation retry must fail");
            }
            catch (GameFault fault)
            {
                Require(fault.Status == 409 && fault.Code == "arcade_rules_conflict", "cross-version retry is an explicit conflict");
            }
            ExpectFault(() => store.IssueArcadeRun("group-a", "alice", "unknown-version", 1001, "spark-v3"),
                "unknown rules cannot issue a ticket");
            using (var seed = connection.CreateCommand())
            {
                seed.CommandText = "UPDATE game_arcade_runs SET seed=1";
                seed.ExecuteNonQuery();
            }
            var harderReceipt = store.FinishArcadeRun("group-a", "alice", harder.RunId, 72, [], 1100);
            Require(harderReceipt == new ArcadeFinishReceipt(1, 1, true), "v2 finish ignores the easier v1 record");
            var legacyTaps = Enumerable.Range(0, 65).Select(index => index * 5).ToArray();
            Require(store.FinishArcadeRun("group-a", "alice", pending.RunId, 321, legacyTaps, 1100)
                == new ArcadeFinishReceipt(3, 100, false), "pending v1 finishes against its own rules and record");
            Require(store.FinishArcadeRun("group-a", "alice", harder.RunId, 72, [], 1200) == harderReceipt,
                "v2 replay retry preserves its receipt");
            Require(store.ReadArcadeRanking("group-a", "alice", members, "spark-v2").Best == 1,
                "pending v1 completion does not inflate the v2 board");
            Require(store.ReadArcadeRanking("group-a", "alice", members).Best == 100, "v2 completion preserves the legacy board");
            store.DeleteAccount("alice");
            using var erased = connection.CreateCommand();
            erased.CommandText = "SELECT (SELECT COUNT(*) FROM game_arcade_runs WHERE user_id='alice')+(SELECT COUNT(*) FROM game_arcade_bests WHERE user_id='alice')+(SELECT COUNT(*) FROM game_arcade_bests_v2 WHERE user_id='alice')";
            Require(Convert.ToInt32(erased.ExecuteScalar()) == 0, "erasure covers runs and both rule versions' records");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    private static void ExpectFault(Action action, string message)
    {
        try { action(); }
        catch (GameFault) { return; }
        throw new InvalidOperationException($"Arcade test failed: {message}");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException($"Arcade test failed: {message}");
    }
}
