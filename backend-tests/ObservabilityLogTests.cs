using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text;
using Kufr.Backend.Features.Diagnostics;
using Kufr.Backend.Features.Observability;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Exporter;

internal static class ObservabilityLogTests
{
    internal static async Task RunAsync()
    {
        Disabled();
        ExportAndPrivacy();
        ApplicationLogsPrivacy();
        ExceptionClassifications();
        CharacterFailureCodes();
        StructuredWorkerDeduplication();
        ExporterConfiguration();
        FrameworkMinimumLevel();
        MinimumLevels();
        await RealHttpPipelineAsync();
        await OutageAsync();
        Console.WriteLine("Observability logs: opt-in, protobuf correlation/privacy, safe application/exception/scope metadata, level defaults, frame suppression and nonblocking outage passed.");
    }

    private static void Disabled()
    {
        foreach (var flags in new[] { (false, false), (false, true), (true, false) })
        {
            using var fixture = new Fixture(flags.Item1, flags.Item2);
            Check(fixture.Sink is null, "both telemetry and logs must be enabled");
            fixture.Store.Write("error", "http_request", status: 500);
            fixture.Services.GetRequiredService<ILoggerFactory>().CreateLogger("application").LogError("SECRET-DISABLED-MESSAGE");
            Check(fixture.Receiver.Payloads.IsEmpty, "disabled logs have no exporter traffic");
        }
    }

    private static void ExportAndPrivacy()
    {
        using var fixture = new Fixture();
        var requestId = Guid.NewGuid().ToString();
        var operationId = Guid.NewGuid().ToString();
        var conversationId = Guid.NewGuid().ToString();
        using var activity = new Activity("SECRET-ACTIVITY").SetIdFormat(ActivityIdFormat.W3C).Start();
        activity.AddBaggage("private", "SECRET-BAGGAGE");
        activity.SetTag("prompt", "SECRET-PROMPT");
        activity.SetStatus(ActivityStatusCode.Error, "SECRET-STATUS");
        fixture.Store.Write("warning", "http_request", requestId, "/app/v1/session", "GET", 401,
            12.5, operationId, conversationId, "anonymous", 2);
        fixture.Store.Write("warning", "http_request", "SECRET-REQUEST", "/SECRET-USER-ROUTE",
            "SECRET-METHOD", 400, operationId: "SECRET-OPERATION", conversationId: "SECRET-CONVERSATION", code: "SECRET-CODE");
        fixture.Store.Write("error", "SECRET-UNKNOWN-EVENT");
        var context = new DefaultHttpContext { RequestServices = fixture.Services };
        for (var index = 0; index < 100; index++)
        {
            ServerLogEvents.Stream(context, new { type = "audio_delta", delta = "SECRET-AUDIO", text = "SECRET-TEXT" });
        }
        Check(fixture.Sink!.ForceFlush(), "safe export flush");
        var records = fixture.Receiver.Payloads.SelectMany(Proto.Records).ToArray();
        var correlated = records.Single(record => record.Attributes.GetValueOrDefault("app.request_id") == requestId);
        Check(Convert.ToHexString(correlated.TraceId).Equals(activity.TraceId.ToHexString(), StringComparison.OrdinalIgnoreCase), "W3C trace correlation");
        Check(Convert.ToHexString(correlated.SpanId).Equals(activity.SpanId.ToHexString(), StringComparison.OrdinalIgnoreCase), "W3C span correlation");
        Check(correlated.Attributes["app.event.name"] == "http_request", "stable event name");
        Check(correlated.Attributes["app.operation_id"] == operationId && correlated.Attributes["app.conversation_id"] == conversationId, "business correlation");
        Check(correlated.Attributes["http.route"] == "/app/v1/session" && correlated.Attributes["http.request.method"] == "GET", "safe route and method");
        Check(correlated.Attributes["app.failure_code"] == "anonymous", "known safe failure classification");
        Check(correlated.Attributes.ContainsKey("http.response.status_code") && correlated.Attributes.ContainsKey("app.duration_ms")
            && correlated.Attributes.ContainsKey("app.retry_attempt"), "numeric diagnostic metadata retained");
        var allowed = new HashSet<string>
        {
            "app.event.name", "app.request_id", "app.operation_id", "app.conversation_id", "app.failure_code",
            "http.route", "http.request.method", "http.response.status_code", "app.duration_ms", "app.retry_attempt"
        };
        Check(records.All(record => record.Attributes.Keys.All(allowed.Contains)), "only explicit safe attributes");
        Check(records.Length <= 3, "audio frames cannot flood export");
        var wireText = string.Concat(fixture.Receiver.Payloads.Select(Encoding.UTF8.GetString));
        Check(!wireText.Contains("SECRET", StringComparison.Ordinal), "serialized payload excludes user content, scopes, activity content and exceptions");
        Check(fixture.Receiver.Endpoints.All(endpoint => endpoint.AbsolutePath == "/custom/logs"), "logs-specific endpoint is used exactly");
    }

    private static void ApplicationLogsPrivacy()
    {
        using var fixture = new Fixture();
        var requestId = Guid.NewGuid().ToString();
        var operationId = Guid.NewGuid().ToString();
        var conversationId = Guid.NewGuid().ToString();
        var logger = fixture.Services.GetRequiredService<ILoggerFactory>().CreateLogger("SECRET-UNTRUSTED-CATEGORY");
        using var activity = new Activity("SECRET-APPLICATION-ACTIVITY").SetIdFormat(ActivityIdFormat.W3C).Start();
        activity.ActivityTraceFlags = ActivityTraceFlags.None;
        using (logger.BeginScope(new Dictionary<string, object>
        {
            ["RequestId"] = requestId,
            ["OperationId"] = operationId,
            ["ConversationId"] = Guid.Parse(conversationId),
            ["TraceId"] = activity.TraceId.ToHexString(),
            ["app.operation_id"] = "SECRET-INVALID-ID",
            ["Prompt"] = "SECRET-SCOPE-PROMPT"
        }))
        using (logger.BeginScope("SECRET-SCOPE-STRING"))
        {
            logger.LogError(new EventId(42, "SECRET-EVENT-NAME"), new InvalidOperationException("SECRET-EXCEPTION"),
                "SECRET-FORMAT {Prompt}", "SECRET-PROMPT");
            logger.Log(LogLevel.Warning, new EventId(43), "SECRET-STATE", null,
                static (_, _) => throw new InvalidOperationException("SECRET-FORMATTER-INVOKED"));
        }
        Check(fixture.Sink!.ForceFlush(), "application export flush");
        var records = fixture.Receiver.Payloads.SelectMany(Proto.Records).ToArray();
        Check(records.Length == 2, "general application warnings and errors export safely without invoking formatter");
        var error = records.Single(record => record.Attributes.ContainsKey("error.type"));
        Check(error.Attributes["app.request_id"] == requestId && error.Attributes["app.operation_id"] == operationId
            && error.Attributes["app.conversation_id"] == conversationId, "validated scope UUIDs retained without W3C TraceId overwrite");
        Check(error.Attributes["app.log.event_id"] == "42", "numeric application event identity retained");
        Check(error.Attributes["error.type"] == "invalid_operation", "finite exception classification");
        Check(error.Attributes["app.log.category"] == "application", "untrusted category becomes fixed application family");
        Check(Convert.ToHexString(error.TraceId).Equals(activity.TraceId.ToHexString(), StringComparison.OrdinalIgnoreCase), "application W3C correlation");
        Check(!activity.Recorded && records.All(record => (record.Flags & 1) == 0), "unsampled logs retain correlation without a sampled flag");
        var allowed = new HashSet<string>
        {
            "app.event.name", "app.log.category", "app.log.event_id", "error.type",
            "app.request_id", "app.operation_id", "app.conversation_id"
        };
        Check(records.All(record => record.Attributes.Keys.All(allowed.Contains)), "only safe application metadata exported");
        Check(!string.Concat(fixture.Receiver.Payloads.Select(Encoding.UTF8.GetString)).Contains("SECRET", StringComparison.Ordinal),
            "application payload excludes message, state, arbitrary category, event name, invalid scope IDs and exception contents");
    }

    private static void ExceptionClassifications()
    {
        using var fixture = new Fixture();
        var logger = fixture.Services.GetRequiredService<ILoggerFactory>().CreateLogger("application");
        var scenarios = new (Exception Error, string Classification)[]
        {
            (new OperationCanceledException("SECRET-CANCELLED"), "cancelled"),
            (new TimeoutException("SECRET-TIMEOUT"), "timeout"),
            (new HttpRequestException("SECRET-TRANSPORT"), "provider_transport")
        };
        for (var index = 0; index < scenarios.Length; index++)
        {
            logger.LogWarning(new EventId(index + 100), scenarios[index].Error, "SECRET-EXCEPTION-MESSAGE");
        }
        Check(fixture.Sink!.ForceFlush(), "exception classification flush");
        var records = fixture.Receiver.Payloads.SelectMany(Proto.Records).ToArray();
        Check(records.Length == scenarios.Length, "each exception produces one log");
        for (var index = 0; index < scenarios.Length; index++)
        {
            var eventId = (index + 100).ToString(System.Globalization.CultureInfo.InvariantCulture);
            var record = records.Single(record => record.Attributes["app.log.event_id"] == eventId);
            Check(record.Attributes["error.type"] == scenarios[index].Classification, "finite cancellation, timeout and transport classifications");
        }
        Check(!string.Concat(fixture.Receiver.Payloads.Select(Encoding.UTF8.GetString)).Contains("SECRET", StringComparison.Ordinal),
            "exception messages stay absent from classification payloads");
    }

    private static void CharacterFailureCodes()
    {
        using var fixture = new Fixture();
        var codes = new[]
        {
            "character_provider_output_limit", "character_provider_incomplete", "character_provider_refused",
            "character_provider_configuration_error", "character_provider_unavailable", "openai_consent_required", "character_not_found"
        };
        foreach (var code in codes)
        {
            fixture.Store.Write("warning", "testing_character_job_failed", code: code);
        }
        var liveCodes = new[]
        {
            "provider_capacity", "provider_moderation", "decision_provider_auth", "live_finalization_timeout", "realtime_input_buffer_limit"
        };
        foreach (var code in liveCodes)
        {
            fixture.Store.Write("warning", "lab_provider_failure", code: code);
        }
        Check(fixture.Sink!.ForceFlush(), "character failure codes flush");
        var records = fixture.Receiver.Payloads.SelectMany(Proto.Records).ToArray();
        var characterRecords = records.Where(record => record.Attributes["app.event.name"] == "testing_character_job_failed").ToArray();
        Check(characterRecords.Length == codes.Length,
            "character failures retain their stable event name");
        Check(characterRecords.Select(record => record.Attributes["app.failure_code"]).Order().SequenceEqual(codes.Order()),
            "all finite character failure codes survive serialized export exactly");
        var liveRecords = records.Where(record => record.Attributes["app.event.name"] == "lab_provider_failure").ToArray();
        Check(records.Length == codes.Length + liveCodes.Length && liveRecords.Length == liveCodes.Length,
            "live provider failures retain their stable event name");
        Check(liveRecords.Select(record => record.Attributes["app.failure_code"]).Order().SequenceEqual(liveCodes.Order()),
            "representative live and persona lab failure codes survive serialized export exactly");
    }

    private static void StructuredWorkerDeduplication()
    {
        using var fixture = new Fixture(captureStructuredFailures: true);
        var operationId = Guid.NewGuid().ToString();
        var logger = fixture.Services.GetRequiredService<ILoggerFactory>()
            .CreateLogger("Kufr.Backend.Features.Conversations.ConversationStore");
        logger.LogError(new InvalidOperationException("SECRET-WORKER-EXCEPTION"),
            "Text dialog failed for operation {OperationId}.", operationId);
        logger.LogWarning("SECRET-UNKNOWN-TEMPLATE {Prompt}", "SECRET-PROMPT");
        Check(fixture.Sink!.ForceFlush(), "structured worker flush");
        var records = fixture.Receiver.Payloads.SelectMany(Proto.Records).ToArray();
        Check(records.Length == 2, "known structured failure is exported once alongside unknown general log");
        var structured = records.Single(record => record.Attributes["app.event.name"] == "dialog_failed");
        Check(structured.Attributes["app.operation_id"] == operationId, "deduplicated structured failure retains operation correlation");
        Check(records.Count(record => record.Attributes["app.event.name"] == "application_log") == 1,
            "unknown worker template remains available as sanitized application log");
        Check(!string.Concat(fixture.Receiver.Payloads.Select(Encoding.UTF8.GetString)).Contains("SECRET", StringComparison.Ordinal),
            "deduplicated and unknown worker payloads exclude private content");
    }

    private static void ExporterConfiguration()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["OTEL_EXPORTER_OTLP_ENDPOINT"] = "https://collector.example/base/",
            ["OTEL_EXPORTER_OTLP_HEADERS"] = "x-test=generic",
            ["OTEL_EXPORTER_OTLP_LOGS_ENDPOINT"] = "https://logs.example/custom/path",
            ["OTEL_EXPORTER_OTLP_LOGS_HEADERS"] = "x-test=logs"
        }).Build();
        var exact = new OtlpExporterOptions();
        BackendTelemetry.ConfigureExporter(exact, configuration, "LOGS");
        Check(exact.Endpoint.AbsoluteUri == "https://logs.example/custom/path" && exact.Headers == "x-test=logs",
            "signal-specific log endpoint and headers take precedence");
        Check(exact.Protocol == OtlpExportProtocol.HttpProtobuf, "log exporter uses HTTP protobuf");
        configuration["OTEL_EXPORTER_OTLP_LOGS_ENDPOINT"] = null;
        configuration["OTEL_EXPORTER_OTLP_LOGS_HEADERS"] = null;
        var generic = new OtlpExporterOptions();
        BackendTelemetry.ConfigureExporter(generic, configuration, "LOGS");
        Check(generic.Endpoint.AbsoluteUri == "https://collector.example/base/v1/logs" && generic.Headers == "x-test=generic",
            "generic endpoint appends log signal path and generic headers are inherited");
    }

    private static void FrameworkMinimumLevel()
    {
        using var fixture = new Fixture(minimumLevel: "Information");
        var logger = fixture.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Microsoft.AspNetCore.Server.Kestrel");
        logger.LogInformation(new EventId(700), "SECRET-PER-FRAME-INFO");
        logger.LogWarning(new EventId(701), "SECRET-FRAMEWORK-WARNING");
        Check(fixture.Sink!.ForceFlush(), "framework minimum flush");
        var records = fixture.Receiver.Payloads.SelectMany(Proto.Records).ToArray();
        Check(records.Length == 1 && records[0].Attributes["app.log.event_id"] == "701"
            && records[0].Attributes["app.log.category"] == "framework", "framework floor suppresses per-request info and retains warnings");
    }

    private static async Task RealHttpPipelineAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "kufr-otel-logs-http-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var receiver = new Receiver(false, logsOnly: true);
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Backend:Telemetry:Enabled"] = "true",
            ["Backend:Telemetry:Logs:Enabled"] = "true",
            ["Backend:Telemetry:Logs:MinimumLevel"] = "Warning",
            ["Backend:Telemetry:SampleRatio"] = "0",
            ["Backend:DatabasePath"] = Path.Combine(root, "backend.sqlite"),
            ["OTEL_EXPORTER_OTLP_LOGS_ENDPOINT"] = "http://127.0.0.1:4318/custom/logs",
            ["OTEL_EXPORTER_OTLP_TRACES_ENDPOINT"] = "http://127.0.0.1:4318/v1/traces"
        });
        builder.Services.AddBackendTelemetry(builder.Configuration, builder.Environment,
            options => options.HttpClientFactory = () => new HttpClient(receiver, false));
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<ServerLogStore>();
        builder.Services.AddSingleton<ILoggerProvider, ServerLogProvider>();
        await using var app = builder.Build();
        app.UseRouting();
        app.Use((context, next) => ServerLogEvents.RequestAsync(context, _ => next(), app.Services.GetRequiredService<ServerLogStore>()));
        app.MapGet("/probe", (HttpContext context, ILoggerFactory factory) =>
        {
            var logger = factory.CreateLogger("application");
            using (logger.BeginScope(new Dictionary<string, object> { ["RequestId"] = context.TraceIdentifier }))
            {
                logger.LogWarning(new EventId(900), new TimeoutException("SECRET-HTTP-EXCEPTION"), "SECRET-HTTP-MESSAGE");
            }
            return Results.Ok();
        });
        app.MapPost("/v1/streams/{streamId:guid}/audio", () => Results.NoContent());
        try
        {
            await app.StartAsync();
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var client = new HttpClient();
            using var request = new HttpRequestMessage(HttpMethod.Get, address + "/probe?prompt=SECRET-QUERY");
            const string traceId = "12345678901234567890123456789012";
            request.Headers.Add("traceparent", $"00-{traceId}-1234567890123456-00");
            using var response = await client.SendAsync(request);
            Check(response.StatusCode == HttpStatusCode.OK, "real probe response");
            Check(response.Headers.GetValues("X-Trace-Id").Single() == traceId, "real HTTP trace response correlation");
            var requestId = response.Headers.GetValues("X-Request-Id").Single();
            for (var index = 0; index < 100; index++)
            {
                using var audio = await client.PostAsync(address + "/v1/streams/" + Guid.NewGuid() + "/audio", null);
                Check(audio.StatusCode == HttpStatusCode.NoContent, "real PCM upload response");
            }
            Check(app.Services.GetRequiredService<SafeServerLogTelemetry>().ForceFlush(), "real HTTP log flush");
            var records = receiver.Payloads.SelectMany(Proto.Records).ToArray();
            Check(records.Length == 1, "real web pipeline exports probe warning without successful audio or framework info flood");
            var warning = records.Single();
            Check(warning.Attributes["app.log.event_id"] == "900" && warning.Attributes["app.request_id"] == requestId,
                "real web logger retains response request identity");
            Check(Convert.ToHexString(warning.TraceId).Equals(traceId, StringComparison.OrdinalIgnoreCase)
                && warning.SpanId.Length == 8 && (warning.Flags & 1) == 0, "real web warning retains unsampled W3C trace and span correlation");
            Check(warning.Attributes["error.type"] == "timeout", "real web exception classification");
            Check(!string.Concat(receiver.Payloads.Select(Encoding.UTF8.GetString)).Contains("SECRET", StringComparison.Ordinal),
                "real web payload excludes query, exception message and log content");
            await app.StopAsync();
        }
        finally
        {
            receiver.Release.Set();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(root, true);
        }
    }

    private static void MinimumLevels()
    {
        foreach (var scenario in new[] { ("Production", (string?)null, 1), ("Development", (string?)null, 2), ("Production", "Information", 2) })
        {
            using var fixture = new Fixture(environment: scenario.Item1, minimumLevel: scenario.Item2);
            fixture.Store.Write("info", "account_session_checked", code: "anonymous");
            fixture.Store.Write("warning", "http_request", status: 400);
            var logger = fixture.Services.GetRequiredService<ILoggerFactory>().CreateLogger("application");
            logger.LogInformation("SECRET-INFO");
            logger.LogWarning("SECRET-WARNING");
            Check(fixture.Sink!.ForceFlush(), "level export flush");
            Check(fixture.Receiver.Payloads.SelectMany(Proto.Records).Count() == scenario.Item3 * 2, "environment default and explicit minimum level apply to diagnostics and application logs");
        }
    }

    private static async Task OutageAsync()
    {
        using var fixture = new Fixture(outage: true);
        var elapsed = Stopwatch.StartNew();
        fixture.Store.Write("error", "http_request", status: 500);
        Check(elapsed.Elapsed < TimeSpan.FromSeconds(1), "request logging does not wait for collector");
        var flush = Task.Run(() => fixture.Sink!.ForceFlush(3000));
        await fixture.Receiver.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            var write = Task.Run(() => fixture.Store.Write("warning", "http_request", status: 400));
            await write.WaitAsync(TimeSpan.FromSeconds(1));
        }
        finally
        {
            fixture.Receiver.Release.Set();
        }
        await flush.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) { throw new InvalidOperationException("Observability log assertion: " + message); }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "kufr-otel-logs-" + Guid.NewGuid().ToString("N"));
        private readonly IHost host;
        internal Receiver Receiver { get; }
        internal IServiceProvider Services => host.Services;
        internal SafeServerLogTelemetry? Sink => Services.GetService<SafeServerLogTelemetry>();
        internal ServerLogStore Store => Services.GetRequiredService<ServerLogStore>();

        internal Fixture(bool enabled = true, bool logsEnabled = true, string environment = "Development", string? minimumLevel = null,
            bool outage = false, bool captureStructuredFailures = false)
        {
            Directory.CreateDirectory(root);
            Receiver = new Receiver(outage);
            var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = environment });
            builder.Logging.ClearProviders();
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Backend:Telemetry:Enabled"] = enabled.ToString(),
                ["Backend:Telemetry:Logs:Enabled"] = logsEnabled.ToString(),
                ["Backend:Telemetry:Logs:MinimumLevel"] = minimumLevel,
                ["Backend:DatabasePath"] = Path.Combine(root, "backend.sqlite"),
                ["OTEL_EXPORTER_OTLP_LOGS_ENDPOINT"] = "http://127.0.0.1:4318/custom/logs",
                ["OTEL_EXPORTER_OTLP_TRACES_ENDPOINT"] = "http://127.0.0.1:4318/v1/traces"
            });
            builder.Services.AddBackendTelemetry(builder.Configuration, builder.Environment,
                options => options.HttpClientFactory = () => new HttpClient(Receiver, false));
            builder.Services.AddSingleton(TimeProvider.System);
            builder.Services.AddSingleton<EndpointDataSource>(new DefaultEndpointDataSource(
                new RouteEndpoint(_ => Task.CompletedTask, RoutePatternFactory.Parse("/app/v1/session"), 0,
                    new EndpointMetadataCollection(), "test session")));
            builder.Services.AddSingleton<ServerLogStore>();
            if (captureStructuredFailures)
            {
                builder.Services.AddSingleton<ILoggerProvider, ServerLogProvider>();
            }
            host = builder.Build();
        }

        public void Dispose()
        {
            Receiver.Release.Set();
            host.Dispose();
            Receiver.Dispose();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(root, true);
        }
    }

    private sealed class Receiver(bool outage, bool logsOnly = false) : HttpMessageHandler
    {
        internal ConcurrentQueue<byte[]> Payloads { get; } = new();
        internal ConcurrentQueue<Uri> Endpoints { get; } = new();
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal ManualResetEventSlim Release { get; } = new(!outage);
        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken) =>
            SendAsync(request, cancellationToken).GetAwaiter().GetResult();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Check(request.Content?.Headers.ContentType?.MediaType == "application/x-protobuf", "HTTP protobuf protocol");
            if (logsOnly && request.RequestUri?.AbsolutePath != "/custom/logs")
            {
                return new(HttpStatusCode.OK) { Content = new ByteArrayContent([]) };
            }
            Endpoints.Enqueue(request.RequestUri!);
            Payloads.Enqueue(await request.Content!.ReadAsByteArrayAsync(cancellationToken));
            Started.TrySetResult();
            Release.Wait(cancellationToken);
            if (outage) { throw new HttpRequestException("SECRET-COLLECTOR-FAILURE"); }
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent([]) };
        }
    }

    // Decode the official OTLP log/attribute wire fields without a protobuf test dependency.
    private static class Proto
    {
        internal sealed record Log(byte[] TraceId, byte[] SpanId, uint Flags, Dictionary<string, string> Attributes);
        internal static IEnumerable<Log> Records(byte[] payload)
        {
            foreach (var resource in Fields(payload).Where(field => field.Number == 1))
            foreach (var scope in Fields(resource.Value).Where(field => field.Number == 2))
            foreach (var record in Fields(scope.Value).Where(field => field.Number == 2))
            {
                var fields = Fields(record.Value).ToArray();
                var attributes = new Dictionary<string, string>();
                foreach (var attribute in fields.Where(field => field.Number == 6))
                {
                    var pair = Fields(attribute.Value).ToArray();
                    var key = Encoding.UTF8.GetString(pair.Single(field => field.Number == 1).Value);
                    var encoded = pair.Single(field => field.Number == 2).Value;
                    attributes.Add(key, Scalar(encoded));
                }
                var flags = fields.FirstOrDefault(field => field.Number == 8).Value;
                yield return new(fields.FirstOrDefault(field => field.Number == 9).Value ?? [], fields.FirstOrDefault(field => field.Number == 10).Value ?? [],
                    flags is null ? 0 : System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(flags), attributes);
            }
        }

        private static string Scalar(byte[] encoded)
        {
            if (encoded.Length > 0 && encoded[0] == 24) // AnyValue.int_value
            {
                ulong value = 0;
                var shift = 0;
                foreach (var next in encoded.Skip(1))
                {
                    value |= (ulong)(next & 127) << shift;
                    if (next < 128) { break; }
                    shift += 7;
                }
                return value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            var text = Fields(encoded).FirstOrDefault(field => field.Number == 1).Value;
            return text is null ? "" : Encoding.UTF8.GetString(text);
        }

        private static IEnumerable<(int Number, byte[] Value)> Fields(byte[] data)
        {
            var offset = 0;
            ulong Read()
            {
                ulong value = 0;
                for (var shift = 0; shift < 64; shift += 7)
                {
                    var next = data[offset++];
                    value |= (ulong)(next & 127) << shift;
                    if (next < 128) { return value; }
                }
                throw new InvalidDataException("Invalid protobuf varint.");
            }
            while (offset < data.Length)
            {
                var tag = Read();
                var wire = tag & 7;
                if (wire == 0) { Read(); continue; }
                var length = wire switch { 1 => 8, 2 => checked((int)Read()), 5 => 4, _ => throw new InvalidDataException("Invalid protobuf wire type.") };
                var value = data.AsSpan(offset, length).ToArray();
                offset += length;
                yield return ((int)(tag >> 3), value);
            }
        }
    }
}
