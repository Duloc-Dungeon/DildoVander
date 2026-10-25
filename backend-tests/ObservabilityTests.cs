using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text;
using Kufr.Backend.Features.Customers;
using Kufr.Backend.Features.Diagnostics;
using Kufr.Backend.Features.Observability;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Trace;

internal static class ObservabilityTests
{
    internal static async Task RunAsync()
    {
        DisabledAndPrivacy();
        await DurableLinksAsync();
        await ExportHttpAsync(false);
        await ExportHttpAsync(true);
        Console.WriteLine("Observability: disabled, privacy, durable links, W3C HTTP/DB OTLP and exporter outage passed.");
    }

    private static void DisabledAndPrivacy()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddBackendTelemetry(builder.Configuration, builder.Environment);
        Check(!builder.Services.Any(service => service.ServiceType == typeof(TracerProvider)), "disabled provider absent");
        using var activity = new Activity("private/secret").Start();
        activity.SetTag("prompt", "SECRET-PROMPT");
        activity.SetTag("app.operation_id", "SECRET-ACCOUNT");
        activity.SetTag("gen_ai.request.model", "gpt-test");
        activity.SetStatus(ActivityStatusCode.Error, "SECRET-EXCEPTION");
        activity.TraceStateString = "vendor=SECRET-STATE";
        activity.AddBaggage("private", "SECRET-BAGGAGE");
        new SafeSpanProcessor().OnEnd(activity);
        Check(activity.GetTagItem("prompt") is null && activity.GetTagItem("app.operation_id") is null, "private attributes removed");
        Check(activity.StatusDescription is null && activity.TraceStateString is null && !activity.Baggage.Any(), "status and propagation cleared");
        using var contaminated = new Activity("content").Start();
        contaminated.ActivityTraceFlags = ActivityTraceFlags.Recorded;
        contaminated.AddEvent(new ActivityEvent("SECRET-EVENT"));
        new SafeSpanProcessor().OnEnd(contaminated);
        Check(!contaminated.Recorded, "content events suppress export");
    }

    private static async Task DurableLinksAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "kufr-otel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var listener = new ActivityListener
            {
                ShouldListenTo = source => source.Name == BackendTelemetry.SourceName,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded
            };
            ActivitySource.AddActivityListener(listener);
            using var producer = BackendTelemetry.StartOperation("character.request")!;
            var store = new CharacterProfileStore(root);
            await store.SaveAsync("private-owner", new(0, new("PRIVATE-NAME", "PRIVATE-WORLD"), OpenAiConsent: true));
            // Reopen the real SQLite store: correlation must survive process restart.
            store = new CharacterProfileStore(root);
            var job = await store.ClaimAsync() ?? throw new Exception("job missing");
            Check(job.TraceParent is not null && job.CreatedAt > 0, "durable producer context");
            using (var trace = new JobTelemetry("character.enrichment.process", job.Id, job.Attempt, job.TraceParent, job.CreatedAt))
            {
                var consumer = trace.Activity!;
                Check(consumer.TraceId != producer.TraceId && consumer.ParentSpanId == default, "new consumer trace");
                Check(consumer.Links.Single().Context.TraceId == producer.TraceId, "consumer linked to producer");
                using var child = BackendTelemetry.StartOperation("character.context.persist")!;
                Check(child.ParentSpanId == consumer.SpanId && child.TraceId == consumer.TraceId, "consumer child context");
            }
            Check(Activity.Current == producer, "ambient context restored");
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    private static async Task ExportHttpAsync(bool outage)
    {
        var root = Path.Combine(Path.GetTempPath(), "kufr-otel-http-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var receiver = new OtlpReceiver(outage);
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Backend:Telemetry:Enabled"] = "true", ["Backend:Telemetry:SampleRatio"] = "1",
            ["Backend:DatabasePath"] = Path.Combine(root, "backend.sqlite"),
            ["OTEL_EXPORTER_OTLP_TRACES_ENDPOINT"] = "http://127.0.0.1:4318/v1/traces"
        });
        builder.Services.AddBackendTelemetry(builder.Configuration, builder.Environment,
            options => options.HttpClientFactory = () => new HttpClient(receiver, false));
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<ServerLogStore>();
        await using var app = builder.Build();
        string? serviceAddress = null;
        var dependencyHeaders = new ConcurrentQueue<(string Parent, bool Private)>();
        app.UseRouting();
        app.Use((context, next) => ServerLogEvents.RequestAsync(context, _ => next(), app.Services.GetRequiredService<ServerLogStore>()));
        app.MapPost("/test/{id}", async () =>
        {
            Check(!Activity.Current!.Baggage.Any(), "ingress excludes baggage");
            Check(Activity.Current.TraceStateString is null, "ingress excludes trace state");
            using var operation = BackendTelemetry.StartOperation("testing.session.create");
            using var ai = GenAiOperation.Start("responses", "gpt-test");
            ai.SetUsage(11, 7);
            Activity.Current!.AddBaggage("private", "SECRET-OUTBOUND-BAGGAGE");
            Activity.Current.TraceStateString = "vendor=SECRET-OUTBOUND-STATE";
            using var outbound = new HttpClient();
            using var dependency = await outbound.GetAsync(serviceAddress + "/dependency");
            Check(dependency.IsSuccessStatusCode, "real outbound dependency request");
            using var database = new ProbeDatabase();
            await database.Database.OpenConnectionAsync();
            await database.Database.EnsureCreatedAsync();
            database.Probes.Add(new Probe { Value = "SECRET-SQL-PARAMETER" });
            await database.SaveChangesAsync();
            _ = await database.Probes.Where(probe => probe.Value == "SECRET-SQL-PARAMETER").CountAsync();
            using (var unsafeSpan = BackendTelemetry.StartOperation("unsafe.event"))
            { unsafeSpan?.AddEvent(new ActivityEvent("SECRET-EVENT")); }
            return Results.Ok();
        });
        app.MapGet("/dependency", (HttpContext context) =>
        {
            dependencyHeaders.Enqueue((context.Request.Headers["traceparent"].ToString(),
                context.Request.Headers.ContainsKey("baggage") || context.Request.Headers.ContainsKey("tracestate")
                || context.Request.Headers.ContainsKey("Correlation-Context")));
            return Results.Ok();
        });
        app.MapPost("/test/{id}/audio", () => Results.Ok());
        app.MapGet("/export-probe", () => Results.Ok());
        app.MapGet("/failure", (Func<IResult>)(() => throw new InvalidOperationException("SECRET-EXCEPTION")));
        try
        {
            await app.StartAsync();
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            serviceAddress = address;
            using var client = new HttpClient();
            // The test driver is an external caller, not an instrumented backend dependency.
            using var driver = SuppressInstrumentationScope.Begin();
            const string traceId = "12345678901234567890123456789012";
            const string parentId = "1234567890123456";
            using var request = new HttpRequestMessage(HttpMethod.Post, address + "/test/SECRET-IDENTIFIER?secret=SECRET-QUERY");
            request.Headers.Add("traceparent", $"00-{traceId}-{parentId}-00");
            request.Headers.Add("tracestate", "vendor=SECRET-STATE");
            request.Headers.Add("baggage", "private=SECRET-BAGGAGE");
            request.Headers.Add("Authorization", "Bearer SECRET-TOKEN");
            using var response = await client.SendAsync(request);
            Check(response.IsSuccessStatusCode, "request succeeds even during exporter outage");
            Check(response.Headers.GetValues("X-Trace-Id").Single() == traceId, "W3C trace response");
            var requestId = response.Headers.GetValues("X-Request-Id").Single();
            Check(Guid.TryParse(requestId, out _) && requestId != traceId, "legacy request ID preserved");
            Check(dependencyHeaders.TryPeek(out var forwarded) && !forwarded.Private
                && forwarded.Parent.Contains(traceId, StringComparison.Ordinal), "native HTTP propagates trace ID without private state");
            for (var index = 0; index < 100; index++)
            { using var audio = await client.PostAsync(address + "/test/stream/audio", null); Check(audio.IsSuccessStatusCode, "audio upload"); }
            using var failed = await client.GetAsync(address + "/failure");
            Check(failed.StatusCode == HttpStatusCode.InternalServerError, "original exception behavior");
            var logs = app.Services.GetRequiredService<ServerLogStore>().Read(null, 200, "info", requestId, null);
            Check(logs.Entries.Any(entry => entry.TraceId == requestId && entry.DistributedTraceId == traceId), "legacy log correlation");
            var provider = app.Services.GetRequiredService<TracerProvider>();
            if (outage)
            {
                var flush = Task.Run(() => provider.ForceFlush(5000));
                await receiver.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
                // Hold a real serializer/export send in progress while another request
                // completes. Export work must stay off the request/audio execution path.
                using var probe = await client.GetAsync(address + "/export-probe").WaitAsync(TimeSpan.FromSeconds(3));
                Check(probe.IsSuccessStatusCode, "requests complete while exporter is stalled");
                receiver.Release.Set();
                await flush;
            }
            provider.ForceFlush(5000);
            Check(receiver.Payloads.Count > 0, "real OTLP payload generated");
            var payload = Encoding.UTF8.GetString(receiver.Payloads.SelectMany(bytes => bytes).ToArray());
            Check(!payload.Contains("SECRET", StringComparison.Ordinal), "serialized OTLP excludes sensitive input/events/SQL/status");
            var spans = receiver.Payloads.SelectMany(Proto.Spans).ToArray();
            var http = spans.Single(span => span.Name == "POST /test/{id}");
            Check(Convert.ToHexString(http.TraceId).Equals(traceId, StringComparison.OrdinalIgnoreCase), "exported trace ID");
            Check(Convert.ToHexString(http.ParentId).Equals(parentId, StringComparison.OrdinalIgnoreCase), "exported remote parent");
            var business = spans.Single(span => span.Name == "testing.session.create");
            Check(business.ParentId.SequenceEqual(http.SpanId), "business parent");
            var ai = spans.Single(span => span.Name == "gen_ai.responses");
            Check(ai.ParentId.SequenceEqual(business.SpanId) && payload.Contains("gen_ai.usage.input_tokens"), "safe AI metadata and parent");
            Check(spans.Any(span => span.Name == "db.query"), "EF SQLite spans exported");
            Check(spans.Any(span => span.Name == "GET /failure") && payload.Contains("unhandled_error", StringComparison.Ordinal),
                "failed requests retain discoverable safe error classification");
            Check(spans.Length < 30 && !spans.Any(span => span.Name.Contains("audio") || span.Name == "unsafe.event"), "no audio/event telemetry flood");
            await app.StopAsync();
        }
        finally
        {
            receiver.Release.Set();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(root, true);
        }
    }

    private static void Check(bool condition, string message)
    { if (!condition) { throw new InvalidOperationException("Observability assertion: " + message); } }

    private sealed class OtlpReceiver(bool outage) : HttpMessageHandler
    {
        internal ConcurrentQueue<byte[]> Payloads { get; } = new();
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal ManualResetEventSlim Release { get; } = new(!outage);
        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken token) =>
            SendAsync(request, token).GetAwaiter().GetResult();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Check(request.Content?.Headers.ContentType?.MediaType == "application/x-protobuf", "OTLP protobuf content type");
            Payloads.Enqueue(await request.Content!.ReadAsByteArrayAsync(token));
            Started.TrySetResult();
            Release.Wait(token);
            if (outage) { throw new HttpRequestException("SECRET-EXPORTER-FAILURE"); }
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent([]) };
        }
    }

    private sealed class ProbeDatabase : DbContext
    {
        internal DbSet<Probe> Probes => Set<Probe>();
        protected override void OnConfiguring(DbContextOptionsBuilder options) => options.UseSqlite("Data Source=:memory:");
    }
    private sealed class Probe { public int Id { get; set; } public string Value { get; set; } = ""; }

    // Decode the relevant official OTLP wire fields; no extra protobuf test dependency.
    private static class Proto
    {
        internal sealed record Span(byte[] TraceId, byte[] SpanId, byte[] ParentId, string Name);
        internal static IEnumerable<Span> Spans(byte[] payload)
        {
            foreach (var resource in Fields(payload).Where(field => field.Number == 1))
            foreach (var scope in Fields(resource.Value).Where(field => field.Number == 2))
            foreach (var span in Fields(scope.Value).Where(field => field.Number == 2))
            {
                var fields = Fields(span.Value).ToArray();
                byte[] Get(int number) => fields.FirstOrDefault(field => field.Number == number).Value ?? [];
                yield return new(Get(1), Get(2), Get(4), Encoding.UTF8.GetString(Get(5)));
            }
        }
        private static IEnumerable<(int Number, byte[] Value)> Fields(byte[] data)
        {
            var offset = 0;
            ulong Read()
            {
                ulong value = 0;
                for (var shift = 0; shift < 64; shift += 7)
                {
                    var next = data[offset++]; value |= (ulong)(next & 127) << shift;
                    if (next < 128) { return value; }
                }
                throw new InvalidDataException("Invalid protobuf varint.");
            }
            while (offset < data.Length)
            {
                var tag = Read(); var wire = tag & 7; var number = (int)(tag >> 3);
                if (wire == 0) { Read(); continue; }
                var length = wire switch { 1 => 8, 2 => checked((int)Read()), 5 => 4, _ => throw new InvalidDataException("Invalid protobuf wire type.") };
                var value = data.AsSpan(offset, length).ToArray(); offset += length;
                yield return (number, value);
            }
        }
    }
}
