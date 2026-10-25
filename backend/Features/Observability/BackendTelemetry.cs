using System.Diagnostics;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Kufr.Backend.Features.Observability;

// Export only allowlisted technical metadata. Raw content capture is forbidden.
internal static class BackendTelemetry
{
    internal const string SourceName = "Kufr.Backend";
    internal static readonly ActivitySource Source = new(SourceName);

    internal static IServiceCollection AddBackendTelemetry(this IServiceCollection services,
        IConfiguration configuration, IHostEnvironment environment)
    {
        if (!configuration.GetValue<bool>("Backend:Telemetry:Enabled")) return services;
        var ratio = configuration.GetValue<double?>("Backend:Telemetry:SampleRatio")
            ?? (environment.IsProduction() ? 0.1 : 1.0);
        if (!double.IsFinite(ratio) || ratio < 0 || ratio > 1)
            throw new InvalidOperationException("Backend:Telemetry:SampleRatio must be between 0 and 1.");

        services.AddOpenTelemetry()
            .ConfigureResource(resource => resource
                .AddService("dildovander-backend",
                    serviceVersion: configuration["Backend:Telemetry:ServiceVersion"] ?? "unknown")
                .AddAttributes(new Dictionary<string, object>
                {
                    ["deployment.environment.name"] = environment.EnvironmentName
                }))
            .WithTracing(tracing => tracing
                // Ignore sampled flags from untrusted public HTTP callers.
                .SetSampler(new TraceIdRatioBasedSampler(ratio))
                .AddAspNetCoreInstrumentation(options =>
                {
                    options.RecordException = false;
                    options.Filter = ShouldTraceRequest;
                })
                .AddHttpClientInstrumentation(options =>
                {
                    options.RecordException = false;
                    options.FilterHttpRequestMessage = request =>
                        request.RequestUri?.Host.Equals("api.openai.com", StringComparison.OrdinalIgnoreCase) == true;
                })
                .AddSource(SourceName)
                .AddProcessor(new SafeSpanProcessor())
                .AddOtlpExporter(options =>
                {
                    options.Protocol = OtlpExportProtocol.HttpProtobuf;
                    var exact = configuration["OTEL_EXPORTER_OTLP_TRACES_ENDPOINT"];
                    var root = configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];
                    options.Endpoint = !string.IsNullOrWhiteSpace(exact)
                        ? new Uri(exact, UriKind.Absolute)
                        : !string.IsNullOrWhiteSpace(root)
                            ? new Uri(root.TrimEnd('/') + "/v1/traces", UriKind.Absolute)
                            : new Uri("http://127.0.0.1:4318/v1/traces");
                    var headers = configuration["OTEL_EXPORTER_OTLP_TRACES_HEADERS"]
                        ?? configuration["OTEL_EXPORTER_OTLP_HEADERS"];
                    if (!string.IsNullOrWhiteSpace(headers)) options.Headers = headers;
                }));
        return services;
    }

    internal static bool ShouldTraceRequest(HttpContext context)
    {
        var path = context.Request.Path.Value ?? "";
        if (path.Equals("/healthz", StringComparison.OrdinalIgnoreCase)) return false;
        // Skip high-rate PCM frame uploads. Failures still enter the existing log.
        return !(context.Request.Method == "POST"
            && path.EndsWith("/audio", StringComparison.OrdinalIgnoreCase));
    }

    internal static Activity? StartOperation(string operation, ActivityKind kind = ActivityKind.Internal) =>
        Source.StartActivity(operation, kind);

    internal static Activity? StartOpenAi(string model, string operation = "generate_content")
    {
        var safeModel = model.Length is > 0 and <= 100
            && model.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-')
            ? model : "unknown";
        var span = Source.StartActivity(operation + " " + safeModel, ActivityKind.Client);
        span?.SetTag("gen_ai.operation.name", operation);
        span?.SetTag("gen_ai.provider.name", "openai");
        span?.SetTag("gen_ai.request.model", safeModel);
        return span;
    }

    internal static void Completed(Activity? span) => span?.SetTag("app.outcome", "completed");
    internal static void Cancelled(Activity? span) => span?.SetTag("app.outcome", "cancelled");

    internal static void Failed(Activity? span, Exception error)
    {
        if (span is null) return;
        span.SetTag("app.outcome", "failed");
        span.SetTag("error.type", error.GetType().Name);
        span.SetStatus(ActivityStatusCode.Error);
        // Never log/record error.Message or exceptions: they may embed secrets.
    }
}

// Remove unknown framework / future SDK attributes just before batch export.
internal sealed class SafeSpanProcessor : BaseProcessor<Activity>
{
    private static readonly HashSet<string> Allowed = new(StringComparer.Ordinal)
    {
        "http.route", "http.request.method", "http.response.status_code",
        "http.method", "http.status_code", "network.protocol.version",
        "server.address", "server.port", "url.scheme",
        "app.request_id", "app.operation_id", "app.attempt", "app.outcome",
        "app.ai.engine", "app.phase", "app.queue.name", "app.job.type",
        "gen_ai.operation.name", "gen_ai.provider.name",
        "gen_ai.request.model", "gen_ai.response.model",
        "gen_ai.conversation.id", "gen_ai.usage.input_tokens",
        "gen_ai.usage.output_tokens", "error.type", "exception.slug"
    };

    public override void OnEnd(Activity data)
    {
        foreach (var tag in data.TagObjects.ToArray())
            if (!Allowed.Contains(tag.Key)) data.SetTag(tag.Key, null);
        // Keep status code but strip any private exception/status text.
        if (data.StatusDescription is not null) data.SetStatus(data.Status);
    }
}
