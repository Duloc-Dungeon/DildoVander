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
            var challenge = store.Challenge("synthetic-group", "synthetic-alice", "Alena", "synthetic-bob", "Boris",
                new("synthetic-bob", "yolo", "live-yolo-challenge-01"), 100);
            var running = store.Mutate(challenge.Id, "synthetic-group", "synthetic-bob", "accept",
                new("live-yolo-accept-001", challenge.Revision), 101);
            if (!store.TryClaimYolo(challenge.Id, running.Revision)) throw new InvalidOperationException("Could not claim synthetic YOLO job.");
            var challenger = new GameYoloProfile("synthetic-alice", "Alena", "Země Oz", "Stříbrná hůlka", ["trpělivá", "hvězdná"]);
            var opponent = new GameYoloProfile("synthetic-bob", "Boris", "Discworld", "Dubová hůlka", ["vynalézavý", "jiskřivý"]);
            var frozen = store.FreezeYoloProfiles(challenge.Id, running.Revision, challenger, opponent)
                ?? throw new InvalidOperationException("Synthetic profile snapshot was not frozen.");

            using var provider = new OpenAiGameYoloProvider(configuration);
            var result = await provider.GenerateAsync(frozen.Challenger, frozen.Opponent, CancellationToken.None);
            store.CompleteYolo(challenge.Id, running.Revision, result);
            // Simulate a late duplicate completion with a different result; the stored result must remain immutable.
            store.CompleteYolo(challenge.Id, running.Revision,
                new(result.WinnerId == challenger.Id ? opponent.Id : challenger.Id, "late duplicate", []));
            var saved = store.Read(challenge.Id, "synthetic-group", "synthetic-alice");
            if (saved.Status != "complete" || saved.Yolo?.WinnerId != result.WinnerId
                || saved.Yolo.Beats.Count != 3 || saved.Yolo.Verdict != result.Verdict)
                throw new InvalidOperationException("Live YOLO result was not stored as an immutable valid result.");
            if (store.Lobby("synthetic-group", "synthetic-alice", "Alena", []).Player.Xp != xpBefore || saved.XpAwarded != 0)
                throw new InvalidOperationException("Live YOLO must not award XP.");
            Console.WriteLine("YOLO live acceptance: one synthetic request, validated result, immutable replay, zero XP.");
            Console.WriteLine($"Winner: {result.WinnerId}; verdict: {result.Verdict}");
            foreach (var beat in result.Beats) Console.WriteLine(beat.Text);
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
