using Kufr.Backend.Features.Game;
using Microsoft.Data.Sqlite;

internal static class GameYoloPreparedGameTests
{
    internal static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "yolo-prepared-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new GameStore(root);
            GameYoloScenarioTests.SeedQuickMatches(store);
            const string group = "yolo-scenario-tests";
            var left = new GameYoloProfile("alice", "Alena", "Fikční knihovna", "Světelná hůlka", ["pečlivá knihovnice"], WandAffinity: "Měsíční světlo");
            var right = new GameYoloProfile("bob", "Boris", "Fikční nádraží", "Stínová hůlka", ["netrpělivý průvodčí"], WandAffinity: "Stíny");
            var challenge = store.Challenge(group, "alice", "Alice", "bob", "Bob", new("bob", "yolo", Operation()), 1);
            var preparing = store.Mutate(challenge.Id, group, "bob", "accept", new(Operation(), challenge.Revision), 2, yoloSetup: new(left, right));
            Check(preparing.Status == "preparing" && preparing.Yolo?.Scenario is null && preparing.CanCancel,
                "accepted profiles wait for generated content without invented cards");
            Check(store.Lobby(group, "alice", "Alice", []).Active.Any(match => match.Id == challenge.Id), "preparation remains discoverable as active work");
            ExpectFault("match_already_active", () => store.Challenge(group, "alice", "Alice", "bob", "Bob", new("bob", "quick", Operation()), 3));
            ExpectFault("game_action_unavailable", () => store.Mutate(preparing.Id, group, "alice", "choose", new(Operation(), preparing.Revision, Choice: "levitate"), 3));
            Check(store.TryClaimYolo(preparing.Id, preparing.Revision), "preparation has a durable job claim");
            store.FailYolo(preparing.Id, preparing.Revision, "yolo_preparation_failed");
            store = new GameStore(root);
            var failed = store.Read(preparing.Id, group, "alice");
            var retry = store.Mutate(preparing.Id, group, "alice", "retry", new(Operation(), failed.Revision), 4);
            Check(retry.Status == "preparing" && store.ReadFrozenYoloProfiles(retry.Id, retry.Revision)?.Challenger.Name == "Alena",
                "preparation retry preserves accepted profiles across restart");
            var setting = GameYoloPreparationTests.Setting();
            store.CompleteYoloPreparation(retry.Id, preparing.Revision, setting);
            Check(store.Read(retry.Id, group, "alice").Status == "preparing", "stale preparation cannot publish into retry");
            store.CompleteYoloPreparation(retry.Id, retry.Revision, setting);
            var current = store.Read(retry.Id, group, "alice");
            Check(current.Status == "active" && current.CompletedAt is null && current.Yolo?.Scenario?.Options[0].Title == setting.ChallengerPlans[0].Title,
                "complete generated cards publish atomically before first choice");
            store.CompleteYoloPreparation(retry.Id, retry.Revision, setting with { Title = "Late replacement" });
            string[] choices = ["levitate", "protect", "finish"];
            for (var act = 0; act < 3; act++)
            {
                var first = store.Mutate(current.Id, group, "alice", "choose", new(Operation(), current.Revision, Choice: choices[act]), 5);
                current = store.Mutate(current.Id, group, "bob", "choose", new(Operation(), first.Revision, Choice: choices[act]), 6);
                if (act == 0)
                {
                    var own = store.Read(current.Id, group, "alice");
                    Check(own.Yolo!.Scenario!.Options[0].Title == setting.ChallengerPlans[0].Guard,
                        "counteraction offers the generated guard for the actual chosen plan");
                }
            }
            var frozen = store.ReadFrozenYoloProfiles(current.Id, current.Revision)!.Value;
            Check(frozen.Challenger.Decisions?.Length == 3 && frozen.Opponent.Decisions?.Length == 3
                && frozen.Challenger.Setting?.Title == setting.Title,
                "final provider receives accepted characters, generated setting and all six stored decisions");
            Check(current.Yolo!.Scenario!.Revealed[2].Player.Consequence.Contains(setting.Receipt, StringComparison.Ordinal),
                "final facts use the generated objective");
            var cancelled = store.Mutate(current.Id, group, "alice", "cancel", new(Operation(), current.Revision), 7);
            store.CompleteYolo(current.Id, current.Revision, new(null, "Late result.", [], "draw", "alice:3"));
            Check(cancelled.Status == "cancelled" && store.Read(current.Id, group, "alice").Status == "cancelled", "cancel blocks late verdict publication");
            var next = store.Challenge(group, "alice", "Alice", "bob", "Bob", new("bob", "yolo", Operation()), 8);
            next = store.Mutate(next.Id, group, "bob", "accept", new(Operation(), next.Revision), 9, yoloSetup: new(left, right));
            var ended = store.Mutate(next.Id, group, "alice", "cancel", new(Operation(), next.Revision), 10);
            store.CompleteYoloPreparation(next.Id, next.Revision, setting);
            Check(ended.Status == "cancelled" && store.Read(next.Id, group, "alice").Yolo?.Scenario is null,
                "cancelling preparation rejects a late generated setting");
            Console.WriteLine("YOLO prepared game: frozen paired profiles, async setup, slot reservation, restart/retry, atomic cards, generated counters and immutable decisions passed.");
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
        if (!condition) { throw new InvalidOperationException("Prepared YOLO test failed: " + message); }
    }
    private static void ExpectFault(string code, Action action)
    {
        try { action(); }
        catch (GameFault fault) when (fault.Code == code) { return; }
        throw new InvalidOperationException("Expected prepared game fault: " + code);
    }
}
