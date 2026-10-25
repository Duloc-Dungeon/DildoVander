using System.Diagnostics;

namespace Kufr.Backend.Features.Observability;

// A queued job is a new trace linked to its producer, including after restart.
// Never inherit an unrelated request's ambient context when a test/worker claims it.
internal sealed class JobTelemetry : IDisposable
{
    private readonly Activity? previous;
    internal Activity? Activity { get; }

    internal JobTelemetry(string name, string operationId, int attempt, string? traceParent, long createdAt = 0)
    {
        previous = System.Diagnostics.Activity.Current;
        System.Diagnostics.Activity.Current = null;
        var links = System.Diagnostics.ActivityContext.TryParse(traceParent, null, out var producer)
            ? new[] { new ActivityLink(producer) } : [];
        Activity = BackendTelemetry.Source.StartActivity(name, ActivityKind.Consumer, default(ActivityContext), links: links);
        Activity?.SetTag("app.operation_id", operationId);
        Activity?.SetTag("app.attempt", attempt);
        Activity?.SetTag("app.queue.name", "character_enrichment");
        Activity?.SetTag("app.outcome", "interrupted");
        if (createdAt > 0)
        {
            Activity?.SetTag("app.job.age_ms", Math.Max(0, (DateTimeOffset.UtcNow.ToUnixTimeSeconds() - createdAt) * 1000));
        }
    }

    public void Dispose()
    {
        Activity?.Dispose();
        System.Diagnostics.Activity.Current = previous;
    }
}
