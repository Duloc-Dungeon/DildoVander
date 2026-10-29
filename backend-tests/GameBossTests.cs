using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kufr.Backend.Features.Game;
using Microsoft.Data.Sqlite;

internal static class GameBossTests
{
    internal static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "boss-game-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new GameStore(root);
            using (var connection = new SqliteConnection($"Data Source={Path.Combine(root, "mobile-game.sqlite")}"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "INSERT INTO game_players(group_id,user_id,xp,matches) VALUES('g','a',30,4),('g','b',60,6),('g','outlier',9999,50),('rookie','veteran-a',30,4),('rookie','veteran-b',60,6),('rookie','newbie',0,0)";
                command.ExecuteNonQuery();
            }
            var reveal = new GameBossCommand(Guid.NewGuid().ToString(), 0);
            var boss = store.RevealBoss("g", "admin", new HashSet<string> { "a", "b" }, reveal);
            Check(boss.ReferenceXp == 45 && boss.Revealed && !boss.Rescued, "median uses only eligible active players");
            Check(store.RevealBoss("g", "admin", new HashSet<string>(), reveal) == boss, "reveal retry preserves frozen reference");
            var rookieBoss = store.RevealBoss("rookie", "admin", new HashSet<string> { "veteran-a", "veteran-b" },
                new(Guid.NewGuid().ToString(), 0));
            var novice = store.StartBoss("rookie", "newbie", new(Guid.NewGuid().ToString(), rookieBoss.Revision));
            Check(novice.Exhibition && novice.Strength == 1 && novice.OpponentEnergy == 5,
                "zero-XP no-wand exhibition has usable strength against the common boss energy");
            while (novice.Status == "active")
            {
                string bossChoice;
                using (var connection = new SqliteConnection($"Data Source={Path.Combine(root, "mobile-game.sqlite")}"))
                {
                    connection.Open();
                    using var command = connection.CreateCommand();
                    command.CommandText = "SELECT state_json FROM game_boss_attempts WHERE id=$id";
                    command.Parameters.AddWithValue("$id", novice.Id);
                    using var state = JsonDocument.Parse((string)command.ExecuteScalar()!);
                    bossChoice = state.RootElement.GetProperty("bossChoice").GetString()!;
                }
                var noviceCounter = bossChoice switch { "strike" => "shield", "trick" => "strike", _ => "trick" };
                novice = store.ChooseBoss("rookie", "newbie", novice.Id,
                    new(Guid.NewGuid().ToString(), novice.Revision, noviceCounter));
            }
            Check(novice.Result == "win" && store.ReadBoss("rookie").Rescued,
                "the zero-XP no-wand rescue can actually free a captured group");
            var start = new GameBossCommand(Guid.NewGuid().ToString(), boss.Revision);
            var attempt = store.StartBoss("g", "a", start);
            Check(store.StartBoss("g", "a", start).Id == attempt.Id, "start is idempotent");
            Check(attempt.Rounds.Count == 0 && attempt.SeedReveal is null && attempt.PlayerEnergy == 6 && attempt.OpponentEnergy == 5,
                "initial state hides committed choice and seed");
            var initial = JsonSerializer.Serialize(attempt);
            store = new GameStore(root);
            Check(JsonSerializer.Serialize(store.ReadBossAttempt("g", "a", attempt.Id)) == initial, "restart preserves committed choice and complete view");
            var foreignDenied = false;
            try { store.ReadBossAttempt("g", "b", attempt.Id); } catch (GameFault fault) { foreignDenied = fault.Status == 404; }
            Check(foreignDenied, "other participants cannot inspect private attempts");
            while (attempt.Status == "active")
            {
                var choice = attempt.PlayerInventory.First(entry => entry.Value > 0).Key;
                var request = new GameBossCommand(Guid.NewGuid().ToString(), attempt.Revision, choice,
                    attempt.PlayerSparks > 0 ? 1 : 0);
                var next = store.ChooseBoss("g", "a", attempt.Id, request);
                Check(store.ChooseBoss("g", "a", attempt.Id, request).Revision == next.Revision, "replayed choice does not damage again");
                Check(next.Rounds.Count == attempt.Rounds.Count + 1 && next.XpAwarded == 0, "one exchange consumes one choice and no combat XP");
                attempt = next;
            }
            Check(attempt.Rounds.Count <= 12 && attempt.SeedReveal is not null, "attempt is bounded and reveals committed seed");
            Check(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(attempt.SeedReveal!))).ToLowerInvariant() == attempt.SeedCommitment,
                "finished replay verifies original seed commitment");
            var powerful = store.StartBoss("g", "outlier", new(Guid.NewGuid().ToString(), store.ReadBoss("g").Revision));
            string counter;
            using (var connection = new SqliteConnection($"Data Source={Path.Combine(root, "mobile-game.sqlite")}"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT state_json FROM game_boss_attempts WHERE id=$id";
                command.Parameters.AddWithValue("$id", powerful.Id);
                using var state = JsonDocument.Parse((string)command.ExecuteScalar()!);
                counter = state.RootElement.GetProperty("bossChoice").GetString() switch
                { "strike" => "shield", "trick" => "strike", _ => "trick" };
            }
            powerful = store.ChooseBoss("g", "outlier", powerful.Id, new(Guid.NewGuid().ToString(), powerful.Revision, counter));
            Check(powerful.Status == "active", "extreme XP cannot skip the encounter with one ordinary hit");
            counter = Counter(ReadBossChoice(root, powerful.Id));
            powerful = store.ChooseBoss("g", "outlier", powerful.Id, new(Guid.NewGuid().ToString(), powerful.Revision, counter));
            Check(powerful.Result == "win" && store.ReadBoss("g").Rescued, "valid victory rescues the group atomically");
            var rescuedRevision = store.ReadBoss("g").Revision;
            var second = store.StartBoss("g", "b", new(Guid.NewGuid().ToString(), store.ReadBoss("g").Revision));
            Check(store.ReadBoss("g").Rescued && store.ReadBoss("g").Revision == rescuedRevision, "rematches never recapture the keeper");
            Check(store.ReadBoss("g", "b").ActiveAttemptId == second.Id, "active boss attempt can be recovered");
            var cancelled = store.CancelBoss("g", "b", second.Id, new(Guid.NewGuid().ToString(), second.Revision));
            Check(cancelled.Status == "cancelled" && cancelled.XpAwarded == 0, "abandoning an attempt creates no winner or XP");
            CheckProgression(store, root);
            store.DeleteAccount("b");
            var erasedDenied = false;
            try { store.StartBoss("g", "b", new(Guid.NewGuid().ToString(), store.ReadBoss("g").Revision)); }
            catch (GameFault fault) { erasedDenied = fault.Status == 403; }
            Check(erasedDenied, "erasure fence prevents recreating boss records");
            Console.WriteLine("Boss game: frozen reference, monotonic XP damage, common baseline, novice rescue, deadline, commitment, restart, authorization, idempotency, inventory and erasure passed.");
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, recursive: true); }
    }

    private static void CheckProgression(GameStore store, string root)
    {
        int[] xpValues = [0, 1, 29, 30, 60, 120, 180, int.MaxValue];
        using (var connection = new SqliteConnection($"Data Source={Path.Combine(root, "mobile-game.sqlite")}"))
        {
            connection.Open();
            foreach (var xp in xpValues)
            {
                using var command = connection.CreateCommand();
                command.CommandText = "INSERT INTO game_players(group_id,user_id,xp,matches) VALUES('progression',$user,$xp,1)";
                command.Parameters.AddWithValue("$user", $"xp-{xp}");
                command.Parameters.AddWithValue("$xp", xp);
                command.ExecuteNonQuery();
            }
        }
        var profile = store.RevealBoss("progression", "admin", new HashSet<string> { "xp-60" },
            new(Guid.NewGuid().ToString(), 0));
        Check(profile.ReferenceXp == 60, "progression tests use one frozen reference");
        decimal previousStrength = 0;
        int? previousWinningRounds = null;
        var noviceWinningRounds = 0;
        foreach (var xp in xpValues)
        {
            var user = $"xp-{xp}";
            var attempt = store.StartBoss("progression", user,
                new(Guid.NewGuid().ToString(), store.ReadBoss("progression").Revision));
            SetBossSeed(root, attempt.Id);
            attempt = store.ReadBossAttempt("progression", user, attempt.Id);
            Check(attempt.PlayerEnergy == 6 && attempt.OpponentEnergy == 5
                && attempt.PlayerInventory.Values.All(stock => stock == 2),
                $"XP {xp} retains the common energy and spell stock");
            Check(attempt.Strength > previousStrength && attempt.Strength >= 1 && attempt.Strength < 3,
                $"XP {xp} increases fractional damage without a low-XP cliff or an outlier one-shot");
            Check(attempt.Exhibition == (xp < 30) && attempt.SparksEnabled == (xp >= 30),
                "30 XP unlocks optional sparks without changing baseline conditions");
            if (xp == 0 || xp == 60)
            {
                Check(attempt.Strength == (xp == 0 ? 1 : 2), "zero XP and reference XP anchor the progression");
            }
            var strength = attempt.Strength;
            while (attempt.Status == "active")
            {
                var choice = Counter(ReadBossChoice(root, attempt.Id));
                var priorBossEnergy = attempt.OpponentEnergy;
                attempt = store.ChooseBoss("progression", user, attempt.Id,
                    new(Guid.NewGuid().ToString(), attempt.Revision, choice));
                var round = attempt.Rounds[^1];
                Check(round.PlayerDamage == strength * (1 + round.OpponentSparkBid)
                    && attempt.OpponentEnergy == Math.Max(0, priorBossEnergy - round.PlayerDamage),
                    "fractional strength is applied to actual damage and remaining energy");
                if (attempt.CompletedRounds == 1)
                {
                    Check(attempt.PlayerInventory[choice] == 1, "exhibition and regular spells consume identical stock");
                }
            }
            Check(attempt.Result == "win" && attempt.XpAwarded == 0,
                $"XP {xp} can rescue the whole group without earning combat XP");
            Check(previousWinningRounds is null || attempt.CompletedRounds <= previousWinningRounds.Value,
                "the same committed choices never require more winning exchanges as XP rises");
            if (xp == 0) { noviceWinningRounds = attempt.CompletedRounds; }
            if (xp == 120)
            {
                Check(attempt.CompletedRounds < noviceWinningRounds,
                    "active play creates an effective advantage in required winning exchanges");
            }
            previousStrength = strength;
            previousWinningRounds = attempt.CompletedRounds;
        }
        var rescued = store.ReadBoss("progression");
        foreach (var user in new[] { "xp-0", "xp-30" })
        {
            var attempt = store.StartBoss("progression", user, new(Guid.NewGuid().ToString(), rescued.Revision));
            SetBossSeed(root, attempt.Id);
            // Identical choices consume the same stock on both sides, allowing twelve legal draws.
            while (attempt.Status == "active")
            {
                attempt = store.ChooseBoss("progression", user, attempt.Id,
                    new(Guid.NewGuid().ToString(), attempt.Revision, ReadBossChoice(root, attempt.Id)));
                Check(attempt.Status == (attempt.CompletedRounds == 12 ? "complete" : "active"),
                    "neither exhibition nor regular attempts end before the explicit deadline on draws");
            }
            Check(attempt.CompletedRounds == 12 && attempt.Result == "loss"
                && attempt.PlayerEnergy == 6 && attempt.OpponentEnergy == 5,
                "the twelfth exchange is a defeat even when the player has more remaining energy");
            Check(store.ReadBoss("progression") == rescued,
                "losing a rematch after rescue never recaptures the group or changes its revision");
        }
    }

    private static string ReadBossChoice(string root, string id)
    {
        using var connection = new SqliteConnection($"Data Source={Path.Combine(root, "mobile-game.sqlite")}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT state_json FROM game_boss_attempts WHERE id=$id";
        command.Parameters.AddWithValue("$id", id);
        using var state = JsonDocument.Parse((string)command.ExecuteScalar()!);
        return state.RootElement.GetProperty("bossChoice").GetString()!;
    }

    private static string Counter(string choice) => choice switch
    {
        "strike" => "shield", "trick" => "strike", _ => "trick"
    };

    private static void SetBossSeed(string root, string id)
    {
        // Give every XP level the same legal committed sequence. This is deterministic
        // regression evidence about progression, not a claim about human win rates.
        var seed = new string('a', 64);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{seed}:0"));
        var roll = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes) / ((double)uint.MaxValue + 1);
        string[] choices = ["strike", "trick", "shield"];
        using var connection = new SqliteConnection($"Data Source={Path.Combine(root, "mobile-game.sqlite")}");
        connection.Open();
        using var read = connection.CreateCommand();
        read.CommandText = "SELECT state_json FROM game_boss_attempts WHERE id=$id";
        read.Parameters.AddWithValue("$id", id);
        var state = JsonNode.Parse((string)read.ExecuteScalar()!)!.AsObject();
        state["seed"] = seed;
        state["bossChoice"] = choices[(int)(roll * choices.Length)];
        using var write = connection.CreateCommand();
        write.CommandText = "UPDATE game_boss_attempts SET state_json=$state WHERE id=$id";
        write.Parameters.AddWithValue("$state", state.ToJsonString());
        write.Parameters.AddWithValue("$id", id);
        write.ExecuteNonQuery();
    }

    private static void Check(bool condition, string message)
    { if (!condition) { throw new InvalidOperationException(message); } }
}
