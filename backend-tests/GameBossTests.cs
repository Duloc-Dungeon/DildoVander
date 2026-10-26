using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
                command.CommandText = "INSERT INTO game_players(group_id,user_id,xp,matches) VALUES('g','a',30,4),('g','b',60,6),('g','outlier',9999,50)";
                command.ExecuteNonQuery();
            }
            var reveal = new GameBossCommand(Guid.NewGuid().ToString(), 0);
            var boss = store.RevealBoss("g", "admin", new HashSet<string> { "a", "b" }, reveal);
            Check(boss.ReferenceXp == 45 && boss.Revealed && !boss.Rescued, "median uses only eligible active players");
            Check(store.RevealBoss("g", "admin", new HashSet<string>(), reveal) == boss, "reveal retry preserves frozen reference");
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
            Check(powerful.Result == "win" && store.ReadBoss("g").Rescued, "valid victory rescues the group atomically");
            var rescuedRevision = store.ReadBoss("g").Revision;
            var second = store.StartBoss("g", "b", new(Guid.NewGuid().ToString(), store.ReadBoss("g").Revision));
            Check(store.ReadBoss("g").Rescued && store.ReadBoss("g").Revision == rescuedRevision, "rematches never recapture the keeper");
            Check(store.ReadBoss("g", "b").ActiveAttemptId == second.Id, "active boss attempt can be recovered");
            var cancelled = store.CancelBoss("g", "b", second.Id, new(Guid.NewGuid().ToString(), second.Revision));
            Check(cancelled.Status == "cancelled" && cancelled.XpAwarded == 0, "abandoning an attempt creates no winner or XP");
            store.DeleteAccount("b");
            var erasedDenied = false;
            try { store.StartBoss("g", "b", new(Guid.NewGuid().ToString(), store.ReadBoss("g").Revision)); }
            catch (GameFault fault) { erasedDenied = fault.Status == 403; }
            Check(erasedDenied, "erasure fence prevents recreating boss records");
            Console.WriteLine("Boss game: frozen reference, commitment, restart, authorization, idempotency, inventory, bounded exchanges and erasure passed.");
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, recursive: true); }
    }
    private static void Check(bool condition, string message)
    { if (!condition) { throw new InvalidOperationException(message); } }
}
