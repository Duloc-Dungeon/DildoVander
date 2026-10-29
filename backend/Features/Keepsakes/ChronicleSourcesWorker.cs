using Kufr.Backend.Features.Chronicle;
using Kufr.Backend.Features.Game;
using Kufr.Backend.Features.Launch;
using Kufr.Backend.Features.SealedCase;
using Kufr.Backend.Features.Wands;

namespace Kufr.Backend.Features.Keepsakes;

internal sealed class ChronicleSourcesWorker(IServiceScopeFactory scopes, ILogger<ChronicleSourcesWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        long caseCursor = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            using var scope = scopes.CreateScope();
            var services = scope.ServiceProvider;
            var destination = services.GetRequiredService<ChronicleStore>();
            // Sources fail independently: a temporarily unavailable case store
            // must not prevent delivery of an already completed game or wand.
            Run("game", () =>
            {
                if (services.GetService<GameStore>() is not { } games) { return; }
                games.BackfillChronicleOutbox();
                games.DrainChronicleOutbox(destination);
            });
            Run("wand", () => services.GetService<WandGenerationStore>()?.DrainWandChronicle(destination));
            Run("case", () =>
            {
                if (services.GetService<SealedCaseStore>() is not { } cases) { return; }
                caseCursor = cases.CaptureReleasedChronicles(caseCursor);
                cases.DrainCaseChronicle(destination);
            });
            Run("evening", () => services.GetService<LaunchPolicyStore>()?.DrainEveningChronicle(destination));
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }
    }

    private void Run(string source, Action action)
    {
        try { action(); }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            logger.LogWarning("Chronicle source {Source} is temporarily unavailable ({ErrorType}).", source, error.GetType().Name);
        }
    }
}
