using System.Diagnostics;
using OpenTelemetry;

namespace Kufr.Backend.Features.Observability;

// This is the last content boundary before the SDK's bounded exporter queue.
internal sealed class SafeSpanProcessor : BaseProcessor<Activity>
{
    private static readonly HashSet<string> Allowed = new(StringComparer.Ordinal)
    {
        "http.route", "http.request.method", "http.response.status_code", "network.protocol.version",
        "app.request_id", "app.operation_id", "app.conversation_id", "app.attempt", "app.outcome", "app.failure_code",
        "app.ai.engine", "app.ai.billable_seconds", "app.phase", "app.queue.name", "app.job.type", "app.job.age_ms",
        "app.stream.first_output_ms", "app.stream.output_chunks", "app.stream.output_bytes", "app.stream.duration_ms",
        "app.stream.ready_ms", "app.stream.handshake_ms", "app.stream.configuration_ms", "app.stream.disconnect_cause",
        "app.tool.name", "app.tool.outcome", "app.device.command", "app.device.outcome",
        "gen_ai.operation.name", "gen_ai.provider.name", "gen_ai.request.model", "gen_ai.response.model",
        "gen_ai.usage.input_tokens", "gen_ai.usage.output_tokens", "error.type", "db.system.name", "db.operation.name"
    };

    public override void OnEnd(Activity activity)
    {
        // Activity events cannot be removed by the public .NET API. Reject the whole
        // span if an instrumenter adds events or private link attributes, rather than
        // accidentally exporting exception stack traces/content. Our sources use tags.
        if (activity.Events.Any() || activity.Links.Any(link => link.Tags?.Any() == true
            || !string.IsNullOrEmpty(link.Context.TraceState)))
        {
            activity.ActivityTraceFlags &= ~ActivityTraceFlags.Recorded;
            return;
        }
        activity.TraceStateString = null;
        foreach (var baggage in activity.Baggage.ToArray()) { activity.SetBaggage(baggage.Key, null); }
        foreach (var tag in activity.TagObjects.ToArray())
        {
            if (!Allowed.Contains(tag.Key) || !SafeValue(tag.Key, tag.Value)) { activity.SetTag(tag.Key, null); }
        }
        activity.SetStatus(activity.Status);
        // Framework database names can contain SQL; HTTP names can contain raw paths.
        if (activity.Source.Name == "Microsoft.AspNetCore")
        {
            activity.DisplayName = $"{activity.GetTagItem("http.request.method") ?? "HTTP"} {activity.GetTagItem("http.route") ?? "(unmatched)"}";
        }
        else if (activity.Source.Name != BackendTelemetry.SourceName)
        {
            activity.DisplayName = activity.Source.Name.Contains("EntityFrameworkCore", StringComparison.Ordinal) ? "db.query" : "HTTP";
        }
        else if (activity.DisplayName.Length > 160 || !activity.DisplayName.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' or ' '))
        {
            activity.DisplayName = "app.operation";
        }
    }

    private static bool SafeValue(string key, object? value)
    {
        if (value is byte or short or int or long or uint or ulong or bool) { return true; }
        if (value is double number) { return double.IsFinite(number); }
        if (value is float single) { return float.IsFinite(single); }
        if (value is not string text || text.Length > 200) { return false; }
        if (key is "app.request_id" or "app.operation_id" or "app.conversation_id") { return Guid.TryParse(text, out _); }
        // Routes are trusted server templates, never Request.Path or raw request URLs.
        if (key == "http.route") { return text.StartsWith('/') && !text.Contains('?'); }
        return text.Length > 0 && text.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-');
    }
}
