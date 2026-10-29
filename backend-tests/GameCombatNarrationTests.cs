using Kufr.Backend.Features.Game;
using Microsoft.Data.Sqlite;
using System.Text.Json;

internal static class GameCombatNarrationTests
{
    internal static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "combat-narration-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new GameStore(root);
            for (var game = 0; game < 5; game++)
            {
                var challenge = store.Challenge("group", "left", "Alena", "right", "Boris", new("right", "quick", Operation()), 1);
                var current = store.Mutate(challenge.Id, "group", "right", "accept", new(Operation(), challenge.Revision), 2);
                while (current.Status == "active")
                {
                    var first = store.Mutate(current.Id, "group", "left", "choose", new(Operation(), current.Revision, Choice: game < 3 ? "strike" : "shield"), 3);
                    current = store.Mutate(current.Id, "group", "right", "choose", new(Operation(), first.Revision, Choice: "trick"), 4);
                }
            }
            var duel = store.Challenge("group", "left", "Alena", "right", "Boris", new("right", "tactical", Operation()), 5);
            duel = store.Mutate(duel.Id, "group", "right", "accept", new(Operation(), duel.Revision), 6);
            string[] left = ["strike", "trick", "shield", "strike"];
            string[] right = ["trick", "shield", "strike", "trick"];
            for (var round = 0; duel.Status == "active"; round++)
            {
                var first = store.Mutate(duel.Id, "group", "left", "choose", new(Operation(), duel.Revision, Choice: left[round % 4]), 7);
                duel = store.Mutate(duel.Id, "group", "right", "choose", new(Operation(), first.Revision, Choice: right[round % 4]), 8);
                if (round == 5) { Check(duel.Status == "active", "equal maximum catch-up still permits a draw"); }
            }
            Check(duel.CompletedRounds == 7 && duel.Rounds.Count == 7 && !duel.NeedsLoadout,
                "tactical match stops at strict unreachable margin without invented eighth round");
            Check(duel.XpAwarded == 12 && duel.CombatNarration?.Status == "pending",
                "XP and complete result are available before narration");
            var xp = store.Lobby("group", "left", "Alena", []).Player.Xp;
            var work = store.TryClaimCombatNarration(duel.Id)!;
            Check(work is not null && store.TryClaimCombatNarration(duel.Id) is null, "narration has one durable claim");
            using (var facts = JsonDocument.Parse(work!.Facts))
            {
                Check(facts.RootElement.GetProperty("rounds").GetArrayLength() == 7, "narration only receives seven actual rounds");
            }
            using (var connection = new SqliteConnection($"Data Source={Path.Combine(root, "mobile-game.sqlite")}"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "UPDATE game_matches SET state_json=json_set(state_json,'$.combatNarrationLeaseUntil',0) WHERE id=$id";
                command.Parameters.AddWithValue("$id", duel.Id);
                command.ExecuteNonQuery();
            }
            store = new GameStore(root);
            Check(store.PendingCombatNarrations().Contains(duel.Id), "expired narration lease is recoverable after restart");
            var recovered = store.TryClaimCombatNarration(duel.Id)!;
            Check(recovered.ClaimId > work.ClaimId && store.TryClaimCombatNarration(duel.Id) is null, "recovery owns a fresh unique durable claim");
            store.FinishCombatNarration(duel.Id, work.ClaimId, "Stale worker after lease expiry.");
            work = recovered;
            store.FinishCombatNarration(duel.Id, work.ClaimId - 1, "Stale text.");
            Check(store.Read(duel.Id, "group", "left").CombatNarration?.Text is null, "stale claim cannot publish");
            store.FinishCombatNarration(duel.Id, work.ClaimId, "Alena obešla Borisův štít. Zápas skončil 7 : 0.");
            store.FinishCombatNarration(duel.Id, work.ClaimId, "A different result.");
            var saved = store.Read(duel.Id, "group", "left");
            Check(store.ReadChronicleNarration(duel.Id, "group", "left") == saved.CombatNarration?.Text
                && store.ReadChronicleNarration(duel.Id, "other-group", "left") is null
                && store.ReadChronicleNarration(duel.Id, "group", "stranger") is null,
                "chronicle reads the exact saved tactical story only for the original group and participant");
            Check(saved.Status == "complete" && saved.CombatNarration?.Text == "Alena obešla Borisův štít. Zápas skončil 7 : 0."
                && saved.Revision > duel.Revision && store.Lobby("group", "left", "Alena", []).Player.Xp == xp,
                "saved narration survives restart, is immutable, and never awards XP twice");
            Console.WriteLine("Tactical early completion and durable narration checks passed.");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) { Directory.Delete(root, true); }
        }
    }

    private static string Operation() => Guid.NewGuid().ToString("N");
    private static void Check(bool condition, string message)
    {
        if (!condition) { throw new InvalidOperationException("Combat narration test failed: " + message); }
    }
}
