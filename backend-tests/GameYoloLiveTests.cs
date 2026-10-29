using Kufr.Backend.Features.Game;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

internal static class GameYoloLiveTests
{
    internal static async Task RunAsync()
    {
        var configuration = new ConfigurationBuilder().AddEnvironmentVariables().Build();
        if (string.IsNullOrWhiteSpace(configuration["OPENAI_API_KEY"]))
            throw new InvalidOperationException("Set OPENAI_API_KEY in the process environment to run this explicit paid test.");

        var root = Path.Combine(Path.GetTempPath(), "mobile-game-yolo-live-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new GameStore(root);
            PlayQuickMatches(store, 10);
            var xpBefore = store.Lobby("synthetic-group", "synthetic-alice", "Alena", []).Player.Xp;
            using var provider = new OpenAiGameYoloProvider(configuration);
            var cases = new[]
            {
                (Left: new[] { "levitate", "protect", "finish" }, Right: new[] { "tray", "sabotage", "risk" }, Outcome: "winner"),
                (Left: new[] { "cup", "protect", "finish" }, Right: new[] { "cup", "protect", "finish" }, Outcome: "draw"),
                (Left: new[] { "levitate", "sabotage", "risk" }, Right: new[] { "tray", "sabotage", "risk" }, Outcome: "fiasco")
            };
            var paidRequests = 0;
            var requestLimit = int.TryParse(Environment.GetEnvironmentVariable("GAME_DESIGN_PAID_BUDGET"), out var configuredLimit)
                ? Math.Clamp(configuredLimit, 1, 10) : 7;
            void StartPaidRequest(string label)
            {
                if (paidRequests >= requestLimit) { throw new InvalidOperationException("Explicit paid request budget exhausted before request."); }
                Console.WriteLine($"Paid request started: {++paidRequests}/{requestLimit}; {label}");
            }
            for (var caseIndex = 0; caseIndex < cases.Length; caseIndex++)
            {
                var test = cases[caseIndex];
                var challenge = store.Challenge("synthetic-group", "synthetic-alice", "Alena", "synthetic-bob", "Boris",
                    new("synthetic-bob", "yolo", Guid.NewGuid().ToString("N")), 100);
                var left = new GameYoloProfile("synthetic-alice", "Alena", "Syntetická fikce", "Stříbrná hůlka",
                    caseIndex == 0 ? ["učená knihovnice"] : caseIndex == 1 ? ["královna"] : [],
                    WandAffinity: caseIndex == 0 ? "Vzduch" : caseIndex == 1 ? "Měsíční světlo" : "Oheň", WandWeakness: "Hluk");
                var right = new GameYoloProfile("synthetic-bob", "Boris", "Syntetická fikce", "Dubová hůlka", [],
                    WandAffinity: caseIndex == 0 ? "Země" : caseIndex == 1 ? "Měsíční světlo" : "Voda", WandWeakness: "Voda");
                var running = store.Mutate(challenge.Id, "synthetic-group", "synthetic-bob", "accept",
                    new(Guid.NewGuid().ToString("N"), challenge.Revision), 101, yoloSetup: new(left, right));
                if (!store.TryClaimYolo(running.Id, running.Revision)) { throw new InvalidOperationException("Preparation claim failed."); }
                StartPaidRequest("AI scenario and actions");
                var setting = await provider.PrepareAsync(left, right, CancellationToken.None);
                store.CompleteYoloPreparation(running.Id, running.Revision, setting);
                running = store.Read(running.Id, "synthetic-group", "synthetic-alice");
                Console.WriteLine($"Generated scenario: {setting.Title}; {setting.Objective}");
                Console.WriteLine($"Left actions: {string.Join(" | ", setting.ChallengerPlans.Select(option => option.Title))}");
                Console.WriteLine($"Right actions: {string.Join(" | ", setting.OpponentPlans.Select(option => option.Title))}");
                for (var act = 0; act < 3; act++)
                {
                    var first = store.Mutate(running.Id, "synthetic-group", "synthetic-alice", "choose",
                        new(Guid.NewGuid().ToString("N"), running.Revision, Choice: test.Left[act]), 102 + act);
                    running = store.Mutate(running.Id, "synthetic-group", "synthetic-bob", "choose",
                        new(Guid.NewGuid().ToString("N"), first.Revision, Choice: test.Right[act]), 102 + act);
                }
                if (!store.TryClaimYolo(challenge.Id, running.Revision)) { throw new InvalidOperationException("Could not claim synthetic YOLO job."); }
                var frozen = store.FreezeYoloProfiles(challenge.Id, running.Revision,
                    left, right)
                    ?? throw new InvalidOperationException("Synthetic profiles not frozen.");
                StartPaidRequest($"YOLO {test.Outcome}");
                var result = await provider.GenerateAsync(frozen.Challenger, frozen.Opponent, CancellationToken.None);
                if (result.Outcome != test.Outcome) { throw new InvalidOperationException("Verdict does not follow the synthetic scenario rubric."); }
                store.CompleteYolo(challenge.Id, running.Revision, result);
                store.CompleteYolo(challenge.Id, running.Revision, new("synthetic-bob", "late duplicate", []));
                var saved = store.Read(challenge.Id, "synthetic-group", "synthetic-alice");
                if (saved.Status != "complete" || saved.Yolo is null || saved.Yolo.WinnerId != result.WinnerId
                    || saved.Yolo.Beats.Count != 3 || saved.Yolo.Verdict != result.Verdict || saved.XpAwarded != 0)
                {
                    throw new InvalidOperationException("Live YOLO immutable result check failed.");
                }
                Console.WriteLine($"Validated {result.Outcome}: {result.Verdict}");
                foreach (var beat in result.Beats) { Console.WriteLine(beat.Text); }
            }
            StartPaidRequest("tactical narration");
            var narration = await provider.NarrateAsync(new("left", "Alena", "Oz", "Stříbrná", []),
                new("right", "Boris", "Discworld", "Dubová", []),
                "Alena vyhrála 7:0. Odehráno sedm výměn. V poslední výměně Alenin štít zastavil Borisův úder. Jiskry nebyly přisazeny.", CancellationToken.None);
            Console.WriteLine($"Validated tactical narration: {narration}");
            if (store.Lobby("synthetic-group", "synthetic-alice", "Alena", []).Player.Xp != xpBefore)
            {
                throw new InvalidOperationException("Live YOLO must not award XP.");
            }
            Console.WriteLine($"Paid acceptance complete: {paidRequests} requests; synthetic data only; no deployment.");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    private static void PlayQuickMatches(GameStore store, long now)
    {
        for (var game = 0; game < 3; game++)
        {
            var prefix = $"live-yolo-seed-{game}";
            var challenge = store.Challenge("synthetic-group", "synthetic-alice", "Alena", "synthetic-bob", "Boris",
                new("synthetic-bob", "quick", $"{prefix}-challenge"), now);
            var state = store.Mutate(challenge.Id, "synthetic-group", "synthetic-bob", "accept",
                new($"{prefix}-accept", challenge.Revision), now + 1);
            for (var round = 0; round < 7 && state.Status == "active"; round++)
            {
                var first = store.Mutate(challenge.Id, "synthetic-group", "synthetic-alice", "choose",
                    new($"{prefix}-alice-{round}", state.Revision, Choice: "strike"), now + 2 + round);
                state = store.Mutate(challenge.Id, "synthetic-group", "synthetic-bob", "choose",
                    new($"{prefix}-bob-{round}", first.Revision, Choice: "trick"), now + 2 + round);
            }
            now += 20;
        }
    }
}
