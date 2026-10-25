using System.Diagnostics;

namespace Kufr.Backend.Features.Observability;

// .NET's native HTTP pipeline propagates independently of the OTel SDK. Limit
// both ingress and egress to a validated W3C traceparent, never baggage/state.
internal sealed class TraceOnlyPropagator : DistributedContextPropagator
{
    public override IReadOnlyCollection<string> Fields { get; } = ["traceparent"];

    public override void Inject(Activity? activity, object? carrier, PropagatorSetterCallback? setter)
    {
        if (activity?.IdFormat == ActivityIdFormat.W3C && activity.Id is { } parent)
        { setter?.Invoke(carrier, "traceparent", parent); }
    }

    public override void ExtractTraceIdAndState(object? carrier, PropagatorGetterCallback? getter,
        out string? traceId, out string? traceState)
    {
        traceId = null;
        traceState = null;
        if (getter is null) { return; }
        getter(carrier, "traceparent", out var value, out var values);
        if (values is not null)
        {
            var parents = values.Take(2).ToArray();
            if (parents.Length != 1) { return; }
            value = parents[0];
        }
        if (ActivityContext.TryParse(value, null, out _)) { traceId = value; }
    }

    public override IEnumerable<KeyValuePair<string, string?>>? ExtractBaggage(object? carrier, PropagatorGetterCallback? getter) => null;
}
