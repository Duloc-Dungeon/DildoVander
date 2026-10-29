using System.Text.Json;
using Kufr.Backend.Features.Game;
using Microsoft.Data.Sqlite;

internal static class GameYoloScenarioTests
{
    private const string Group = "yolo-scenario-tests";

    internal static void Run()
    {
        CheckScenarioRules();
        Check(GameStore.HasMindReaderEvidence(true, "challenger", 0, 1)
            && GameStore.HasMindReaderEvidence(false, "opponent", 1, 0),
            "mind-reader title preserves own winning-round and opponent-spark provenance");
        Check(!GameStore.HasMindReaderEvidence(true, "opponent", 0, 1)
            && !GameStore.HasMindReaderEvidence(false, "challenger", 1, 0)
            && !GameStore.HasMindReaderEvidence(true, "challenger", 0, 0),
            "mind-reader title is not awarded for a lost round or without opponent sparks");
        var root = Path.Combine(Path.GetTempPath(), "game-yolo-scenario-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new GameStore(root);
            SeedQuickMatches(store);
            var before = store.Lobby(Group, "alice", "Alice", []).Player;
            var generating = PlayScenario(store, ["levitate", "protect", "finish"],
                ["tray", "sabotage", "finish"], root);
            Check(store.TryClaimYolo(generating.Id, generating.Revision)
                && !store.TryClaimYolo(generating.Id, generating.Revision), "only the completed scenario can claim one generation job");
            var frozen = store.FreezeYoloProfiles(generating.Id, generating.Revision,
                new("alice", "Alice", "", "", [])
                {
                    ProfileId = "later-profile", ProfileRevision = 99, WandId = "later-wand"
                }, new("bob", "Bob", "", "", []))
                ?? throw new InvalidOperationException("Scenario profiles did not freeze.");
            Check(frozen.Challenger.Decisions?.Length == 3 && frozen.Opponent.Decisions?.Length == 3,
                "the generation snapshot includes all six authored decisions");
            var frozenJson = JsonSerializer.Serialize(new { frozen.Challenger, frozen.Opponent });
            var scenarioJson = JsonSerializer.Serialize(generating.Yolo!.Scenario!.Revealed);
            store.FailYolo(generating.Id, generating.Revision, "yolo_generation_failed");
            store = new GameStore(root);
            var failed = store.Read(generating.Id, Group, "alice");
            var retried = store.Mutate(generating.Id, Group, "alice", "retry", Command(failed.Revision), 3000);
            Check(retried.Status == "generating" && retried.Revision > generating.Revision
                && JsonSerializer.Serialize(retried.Yolo!.Scenario!.Revealed) == scenarioJson,
                "retry changes the generation revision and preserves all six decisions and consequences");
            var frozenAgain = store.ReadFrozenYoloProfiles(retried.Id, retried.Revision);
            Check(frozenAgain?.Challenger is { ProfileId: "alice-profile", ProfileRevision: 7, WandId: "original-wand",
                    PortraitReferenceBase64: "AAECAw==", PortraitSha256: "frozen-sha", Wand.Material: "Ořechové dřevo" },
                "persisted restart retains exact frozen portrait bytes/hash and physical wand design");
            Check(frozenAgain is not null
                && JsonSerializer.Serialize(new { frozenAgain.Value.Challenger, frozenAgain.Value.Opponent }) == frozenJson,
                "restart and retry preserve the original public profiles and decision snapshot");
            store.CompleteYolo(retried.Id, generating.Revision, Result("bob"));
            Check(store.Read(retried.Id, Group, "alice").Status == "generating",
                "an old generation revision cannot overwrite a retry");
            ExpectInvalid(() => store.CompleteYolo(retried.Id, retried.Revision, Result("outsider")));
            ExpectInvalid(() => store.CompleteYolo(retried.Id, retried.Revision, Result("bob")));
            ExpectInvalid(() => store.CompleteYolo(retried.Id, retried.Revision,
                Result("alice") with { DecisiveChoice = "alice:4" }));
            ExpectInvalid(() => store.CompleteYolo(retried.Id, retried.Revision,
                Result(null, "draw")));
            ExpectInvalid(() => store.CompleteYolo(retried.Id, retried.Revision,
                Result(null, "fiasco")));
            Check(store.Read(retried.Id, Group, "alice").Status == "generating",
                "invalid verdicts roll back instead of partially completing the match");
            store.CompleteYolo(retried.Id, retried.Revision, Result("alice"));
            var complete = store.Read(retried.Id, Group, "alice");
            Check(complete.Status == "complete" && complete.Result == "win"
                && complete.Yolo?.Outcome == "winner" && complete.Yolo.WinnerId == "alice"
                && complete.Yolo.DecisiveChoice == "alice:3"
                && complete.Yolo.Scenario?.Revealed.Count == 3 && complete.XpAwarded == 0,
                "a valid final verdict retains the replay and awards no XP");
            var chronicle = new Kufr.Backend.Features.Chronicle.ChronicleStore(root);
            store.DrainChronicleOutbox(chronicle);
            var lockedMemory = chronicle.FindSource("alice", "yolo", retried.Id)
                ?? throw new InvalidOperationException("Completed YOLO event was not durably delivered to the chronicle.");
            Check(!lockedMemory.Revealed && lockedMemory.Result is null && lockedMemory.Participants.Count == 0,
                "completion outbox retains one event without revealing the verdict");
            Check(chronicle.Reveal("alice", lockedMemory.Id), "match owner can reveal the saved event");
            var memory = chronicle.Read("alice", lockedMemory.Id)!;
            Check(memory.DecisiveChoice?.StartsWith("Alice:", StringComparison.Ordinal) == true && memory.Decisions.Count == 6
                && memory.Awards.Any(award => award.Id == "expert-victim")
                && memory.Awards.Any(award => award.Id == "first-win-yolo")
                && memory.Awards.Any(award => award.Id == "protection-paid-off")
                && memory.Awards.All(award => award.EventId == memory.Id),
                $"YOLO memory preserves decisive choice, six decisions and sourced achievements ({memory.DecisiveChoice}; {memory.Decisions.Count}; {string.Join(',', memory.Awards.Select(award => award.Id))})");
            Check(memory.Participants.Count == 2
                && memory.Participants.Single(participant => participant.AccountId == "alice").CharacterName == "Alice"
                && memory.Participants.Single(participant => participant.AccountId == "bob").CharacterName == "Bob",
                "revealed match uses the immutable profiles frozen before the game, including their provenance");
            Check(chronicle.Read("bob", lockedMemory.Id) is { Revealed: false },
                "each participant receives independent reveal state");
            var saved = JsonSerializer.Serialize(complete);
            store.CompleteYolo(retried.Id, retried.Revision, Result("bob"));
            Check(JsonSerializer.Serialize(store.Read(retried.Id, Group, "alice")) == saved,
                "a late conflicting verdict cannot replace a completed result");

            CheckNonWinner(store, "draw", ["cup", "protect", "adapt"], ["levitate", "shortcut", "adapt"]);
            CheckNonWinner(store, "fiasco", ["tray", "sabotage", "finish"], ["cup", "sabotage", "risk"]);
            var pending = PlayScenario(store, ["cup", "protect", "finish"], ["tray", "sabotage", "finish"]);
            var cancelled = store.Mutate(pending.Id, Group, "bob", "cancel", Command(pending.Revision), 4000);
            store.CompleteYolo(pending.Id, pending.Revision, Result("alice"));
            store.FailYolo(pending.Id, pending.Revision, "yolo_generation_failed");
            Check(cancelled.Status == "cancelled"
                && JsonSerializer.Serialize(store.Read(pending.Id, Group, "bob")) == JsonSerializer.Serialize(cancelled)
                && store.PendingYolo().All(job => job.MatchId != pending.Id),
                "cancellation wins over late success and failure and removes pending generation");
            var after = store.Lobby(Group, "alice", "Alice", []).Player;
            Check(before.Xp == after.Xp && before.MatchesPlayed == after.MatchesPlayed,
                "scenario wins, draws, fiascos, retries and cancellation grant no XP or match progression");
            Console.WriteLine("YOLO scenario: contextual rules, six private decisions, revision/replay, restart/retry, final generation, validated outcomes and cancellation passed.");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    private static void CheckScenarioRules()
    {
        string[] plans = ["levitate", "tray", "cup"];
        var protectionTitles = new HashSet<string>();
        var sabotageTitles = new HashSet<string>();
        var protectionByPlan = new Dictionary<string, string>();
        var sabotageByPlan = new Dictionary<string, string>();
        foreach (var ownPlan in plans)
        {
            foreach (var otherPlan in plans)
            {
                List<GameYoloDecision> own = [GameYoloScenario.Select(1, ownPlan, [], [])];
                List<GameYoloDecision> other = [GameYoloScenario.Select(1, otherPlan, [], [])];
                GameYoloScenario.Reveal(own, other, 1);
                var options = GameYoloScenario.Options(2, own, other);
                Check(options.Select(option => option.Id).SequenceEqual(["protect", "sabotage", "shortcut"])
                    && options.All(option => option.Benefit.Length > 0 && option.Cost.Length > 0)
                    && own[0].Consequence.Length > 0 && other[0].Consequence.Length > 0,
                    "all nine plan pairs offer priced, contextual preparation and a revealed first act");
                protectionTitles.Add(options.Single(option => option.Id == "protect").Title);
                sabotageTitles.Add(options.Single(option => option.Id == "sabotage").Title);
                var protection = options.Single(option => option.Id == "protect").Title;
                var sabotage = options.Single(option => option.Id == "sabotage").Title;
                Check(!protectionByPlan.TryGetValue(ownPlan, out var priorProtection) || protection == priorProtection,
                    "protection depends only on the player's revealed plan");
                Check(!sabotageByPlan.TryGetValue(otherPlan, out var priorSabotage) || sabotage == priorSabotage,
                    "sabotage targets the opponent's revealed plan");
                protectionByPlan[ownPlan] = protection;
                sabotageByPlan[otherPlan] = sabotage;
            }
        }
        Check(protectionTitles.Count == 3 && sabotageTitles.Count == 3,
            "protection follows the own plan and sabotage follows the opponent plan");
        string[] preparations = ["protect", "sabotage", "shortcut"];
        string[] endings = ["finish", "adapt", "risk"];
        foreach (var preparation in preparations)
        {
            foreach (var ending in endings)
            {
                foreach (var opposition in preparations)
                {
                    List<GameYoloDecision> own = [GameYoloScenario.Select(1, "levitate", [], [])];
                    List<GameYoloDecision> other = [GameYoloScenario.Select(1, "tray", [], [])];
                    GameYoloScenario.Reveal(own, other, 1);
                    own.Add(GameYoloScenario.Select(2, preparation, own, other));
                    other.Add(GameYoloScenario.Select(2, opposition, other, own));
                    GameYoloScenario.Reveal(own, other, 2);
                    own.Add(GameYoloScenario.Select(3, ending, own, other));
                    other.Add(GameYoloScenario.Select(3, "adapt", other, own));
                    GameYoloScenario.Reveal(own, other, 3);
                    var expected = ending == "adapt" || ending == "finish" && preparation == "protect"
                        || ending == "risk" && preparation == "shortcut" && opposition != "sabotage";
                    var delivered = GameYoloScenario.Delivery(own, other);
                    Check(delivered.GoalMet == expected && own[2].Consequence == delivered.Consequence
                        && own.All(decision => decision.Consequence.Length > 0),
                        $"preparation={preparation}, ending={ending}, opponent={opposition} obey authored consequences");
                }
            }
        }
    }

    private static GameMatchView PlayScenario(GameStore store, string[] left, string[] right, string? restartRoot = null)
    {
        var challenge = store.Challenge(Group, "alice", "Alice", "bob", "Bob", new("bob", "yolo", Operation()), 2000);
        var state = store.Mutate(challenge.Id, Group, "bob", "accept", Command(challenge.Revision), 2001,
            yoloSetup: new(
                new GameYoloProfile("alice", "Alice", "", "Ořech", ["klid"])
                {
                    ProfileId = "alice-profile", ProfileRevision = 7, WandId = "original-wand",
                    PortraitReferenceBase64 = "AAECAw==", PortraitSha256 = "frozen-sha", PortraitMediaType = "image/png",
                    Wand = new("original-wand", 1, "Ořech", "Ořechové dřevo", "Jádro z uhlíku", "Klidná", "Historie", "Celá štíhlá hůlka s měděným koncem", "Saze", "Déšť", [])
                },
                new GameYoloProfile("bob", "Bob", "", "Jasan", ["spěch"]) { ProfileId = "bob-profile" },
                TestSetting()));
        Check(state.Status == "active" && state.Yolo?.Status == "choosing"
            && state.Yolo.Scenario?.Act == 1 && !store.PendingYolo().Any(),
            "acceptance starts the authored first act without a generation job");
        for (var index = 0; index < 3; index++)
        {
            var beforeBob = store.Read(state.Id, Group, "bob").Yolo!.Scenario!;
            if (restartRoot is not null)
            {
                ExpectFault(() => store.Mutate(state.Id, Group, "alice", "choose",
                    Command(state.Revision - 1, left[index]), 2002), "game_revision_conflict");
                ExpectFault(() => store.Mutate(state.Id, Group, "alice", "choose",
                    Command(state.Revision, "not-an-option"), 2002), "game_yolo_choice_invalid");
                ExpectFault(() => store.Mutate(state.Id, Group, "alice", "choose",
                    Command(state.Revision, left[index]) with { SparkBid = 1 }, 2002), "spark_bid_invalid");
            }
            var request = Command(state.Revision, left[index]);
            var first = store.Mutate(state.Id, Group, "alice", "choose", request, 2002 + index);
            Check(JsonSerializer.Serialize(store.Mutate(state.Id, Group, "alice", "choose", request, 2002 + index))
                == JsonSerializer.Serialize(first), "replayed decisions do not duplicate an act");
            var bob = store.Read(state.Id, Group, "bob");
            Check(first.Yolo!.Scenario!.OwnChoiceLocked && bob.Yolo!.Scenario!.OpponentChoiceLocked
                && !bob.Yolo.Scenario.OwnChoiceLocked && bob.Yolo.Scenario.Revealed.Count == index
                && JsonSerializer.Serialize(bob.Yolo.Scenario.Options) == JsonSerializer.Serialize(beforeBob.Options)
                && !store.PendingYolo().Any(),
                "a private pending choice changes only lock state, not revealed actions, options or generation");
            if (restartRoot is not null)
            {
                ExpectFault(() => store.Mutate(state.Id, Group, "alice", "choose",
                    Command(first.Revision, left[index]), 2003), "choice_locked");
                store = new GameStore(restartRoot);
                Check(JsonSerializer.Serialize(store.Read(state.Id, Group, "bob")) == JsonSerializer.Serialize(bob),
                    "restart preserves a pending private choice and revision exactly");
            }
            state = store.Mutate(state.Id, Group, "bob", "choose", Command(first.Revision, right[index]), 2010 + index);
            var act = state.Yolo!.Scenario!.Revealed[index];
            var otherView = store.Read(state.Id, Group, "alice").Yolo!.Scenario!.Revealed[index];
            Check(act.Player.Choice == right[index] && act.Opponent.Choice == left[index]
                && otherView.Player == act.Opponent && otherView.Opponent == act.Player,
                "both submitted actions and their consequences reveal together with viewer-relative orientation");
            Check(state.Status == (index == 2 ? "generating" : "active")
                && store.PendingYolo().Length == (index == 2 ? 1 : 0),
                "generation becomes pending only after the sixth stored decision");
        }
        return store.Read(state.Id, Group, "alice");
    }

    private static void CheckNonWinner(GameStore store, string outcome, string[] left, string[] right)
    {
        var generating = PlayScenario(store, left, right);
        ExpectInvalid(() => store.CompleteYolo(generating.Id, generating.Revision, Result("alice", outcome)));
        store.CompleteYolo(generating.Id, generating.Revision, Result(null, outcome));
        foreach (var user in new[] { "alice", "bob" })
        {
            var saved = store.Read(generating.Id, Group, user);
            Check(saved.Status == "complete" && saved.Result == outcome && saved.Yolo?.Outcome == outcome
                && saved.Yolo.WinnerId is null && saved.PartyTask is null && saved.XpAwarded == 0,
                $"{outcome} persists for both participants with no arbitrary winner or loser task");
        }
    }

    internal static void SeedQuickMatches(GameStore store)
    {
        for (var game = 0; game < 3; game++)
        {
            var challenge = store.Challenge(Group, "alice", "Alice", "bob", "Bob", new("bob", "quick", Operation()), 10 + game * 20);
            var state = store.Mutate(challenge.Id, Group, "bob", "accept", Command(challenge.Revision), 11 + game * 20);
            while (state.Status == "active")
            {
                var first = store.Mutate(state.Id, Group, "alice", "choose", Command(state.Revision, "strike"), 12 + game * 20);
                state = store.Mutate(state.Id, Group, "bob", "choose", Command(first.Revision, "trick"), 12 + game * 20);
            }
        }
    }

    private static GameYoloResult Result(string? winner, string outcome = "winner") =>
        new(winner, "Synthetic authored verdict.", [new("Act one.", ["alice:name"]),
            new("Act two.", ["bob:name"]), new("Act three.", ["alice:name"])], outcome, "alice:3");

    private static GameYoloSetting TestSetting()
    {
        var plans = GameYoloScenario.Options(1, [], []);
        return new(GameYoloScenario.Id, GameYoloScenario.Title, GameYoloScenario.Objective,
            GameYoloScenario.Situation, "čaj", "stůl", "kontrolor uznal čaj", plans, plans, "authored test fixture");
    }

    private static string Operation() => Guid.NewGuid().ToString("N");
    private static GameCommand Command(long revision, string? choice = null) => new(Operation(), revision, Choice: choice);

    private static void ExpectFault(Action action, string code)
    {
        try { action(); }
        catch (GameFault fault) when (fault.Code == code) { return; }
        throw new InvalidOperationException($"Expected scenario fault {code}.");
    }

    private static void ExpectInvalid(Action action)
    {
        try { action(); }
        catch (InvalidDataException) { return; }
        throw new InvalidOperationException("A scenario verdict contradicted the authored decisions but was accepted.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) { throw new InvalidOperationException("YOLO scenario test failed: " + message); }
    }
}
