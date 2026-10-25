using System.Diagnostics;
using System.Reflection;
using OpenTelemetry;
using OpenTelemetry.Context.Propagation;
using OpenTelemetry.Exporter;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Kufr.Backend.Features.Observability;

internal static class BackendTelemetry
{
    internal const string SourceName = "Kufr.Backend";
    internal static readonly ActivitySource Source = new(SourceName);

    internal static IServiceCollection AddBackendTelemetry(this IServiceCollection services,
        IConfiguration configuration, IHostEnvironment environment, Action<OtlpExporterOptions>? configureExporter = null)
    {
        if (!configuration.GetValue<bool>("Backend:Telemetry:Enabled")) { return services; }

        var ratio = configuration.GetValue<double?>("Backend:Telemetry:SampleRatio")
            ?? (environment.IsProduction() ? 0.1 : 1);
        if (!double.IsFinite(ratio) || ratio < 0 || ratio > 1)
        {
            throw new InvalidOperationException("Backend:Telemetry:SampleRatio must be between 0 and 1.");
        }

        // Do not propagate account or user supplied baggage to dependencies.
        Sdk.SetDefaultTextMapPropagator(new TraceContextPropagator());
        DistributedContextPropagator.Current = new TraceOnlyPropagator();
        services.AddSingleton<DistributedContextPropagator>(DistributedContextPropagator.Current);
        var version = typeof(BackendTelemetry).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
        var sha = version.Contains('+') ? version[(version.IndexOf('+') + 1)..] : "unknown";
        services.AddOpenTelemetry().WithTracing(tracing =>
        {
            // Start with an empty resource: automatic detectors may include private host data.
            tracing.SetResourceBuilder(ResourceBuilder.CreateEmpty()
                .AddService(configuration["OTEL_SERVICE_NAME"] ?? "dildovander-backend", serviceVersion: version,
                    autoGenerateServiceInstanceId: false)
                .AddAttributes(new Dictionary<string, object>
                {
                    ["deployment.environment.name"] = configuration["Backend:Telemetry:Environment"] ?? environment.EnvironmentName,
                    ["vcs.ref.head.revision"] = sha
                }));
            // Public callers cannot force sampling with the remote sampled flag. The same
            // trace ID deterministically receives the same decision throughout this backend.
            tracing.SetSampler(new TraceIdRatioBasedSampler(ratio))
                .AddSource(SourceName)
                .AddAspNetCoreInstrumentation(options =>
                {
                    options.RecordException = false;
                    options.Filter = ShouldTraceRequest;
                })
                .AddHttpClientInstrumentation(options =>
                {
                    options.RecordException = false;
                    options.FilterHttpRequestMessage = request => !IsTelemetryRequest(request.RequestUri, configuration);
                })
                .AddEntityFrameworkCoreInstrumentation()
                // Must run before the exporter batch processor. It can suppress unsafe spans.
                .AddProcessor(new SafeSpanProcessor())
                .AddOtlpExporter(options =>
                {
                    ConfigureExporter(options, configuration);
                    configureExporter?.Invoke(options);
                });
        });
        return services;
    }

    internal static void ConfigureExporter(OtlpExporterOptions options, IConfiguration configuration)
    {
        options.Protocol = OtlpExportProtocol.HttpProtobuf;
        var exact = configuration["OTEL_EXPORTER_OTLP_TRACES_ENDPOINT"];
        var root = configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];
        options.Endpoint = new Uri(!string.IsNullOrWhiteSpace(exact) ? exact
            : !string.IsNullOrWhiteSpace(root) ? root.TrimEnd('/') + "/v1/traces"
            : "http://127.0.0.1:4318/v1/traces", UriKind.Absolute);
        if (options.Endpoint.Scheme != "https" && !(options.Endpoint.Scheme == "http" && options.Endpoint.IsLoopback))
        {
            throw new InvalidOperationException("OTLP requires HTTPS except on loopback.");
        }
        options.Headers = configuration["OTEL_EXPORTER_OTLP_TRACES_HEADERS"]
            ?? configuration["OTEL_EXPORTER_OTLP_HEADERS"] ?? "";
        options.TimeoutMilliseconds = 5000;
        options.ExportProcessorType = ExportProcessorType.Batch;
        options.BatchExportProcessorOptions = new BatchExportActivityProcessorOptions
        {
            MaxQueueSize = 2048,
            MaxExportBatchSize = 256,
            ScheduledDelayMilliseconds = 5000,
            ExporterTimeoutMilliseconds = 5000
        };
    }

    private static bool IsTelemetryRequest(Uri? uri, IConfiguration configuration)
    {
        var endpoint = configuration["OTEL_EXPORTER_OTLP_TRACES_ENDPOINT"] ?? configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];
        return uri is not null && (uri.Host is "api.honeycomb.io" or "api.eu1.honeycomb.io"
            || Uri.TryCreate(endpoint, UriKind.Absolute, out var telemetry) && uri.Authority == telemetry.Authority);
    }

    internal static bool ShouldTraceRequest(HttpContext context) =>
        !context.Request.Path.StartsWithSegments("/healthz")
        && !(context.Request.Method == "POST" && context.Request.Path.Value?.EndsWith("/audio", StringComparison.OrdinalIgnoreCase) == true);

    internal static Activity? StartOperation(string name, ActivityKind kind = ActivityKind.Internal) => Source.StartActivity(name, kind);
    internal static void Completed(Activity? activity) => activity?.SetTag("app.outcome", "completed");
    internal static void Failed(Activity? activity, Exception error, CancellationToken cancellationToken = default)
    {
        if (activity is null) { return; }
        var code = error is OperationCanceledException
            ? cancellationToken.IsCancellationRequested ? "cancelled" : "timeout"
            : error is TimeoutException ? "timeout"
            : error is HttpRequestException ? "provider_transport" : "operation_failed";
        activity.SetTag("app.outcome", code);
        activity.SetTag("error.type", code);
        if (code != "cancelled") { activity.SetStatus(ActivityStatusCode.Error); }
    }
}
