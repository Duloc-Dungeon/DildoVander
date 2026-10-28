using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Kufr.Backend.Features.Conversations;
using Kufr.Backend.Features.GptLive;
using Kufr.Backend.Features.Memory;
using Kufr.Backend.Features.Observability;
using Kufr.Backend.Features.Realtime;
using Kufr.Backend.Features.Show;
using Kufr.Backend.Features.Speech;
using Kufr.Backend.Features.VoiceTurns;

internal static class AiObservabilityTests
{
    private const string Private = "private-visitor-secret";

    internal static async Task RunAsync()
    {
        var completed = new ConcurrentQueue<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == BackendTelemetry.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = completed.Enqueue
        };
        ActivitySource.AddActivityListener(listener);

        await VerifyHttpProviderAsync(completed);
        await VerifyFailureClassificationAsync(completed);
        await VerifyStreamingAsync(completed);
        await VerifyLifecycleFailuresAsync(completed);
        VerifySafeMetadata(completed);
        Console.WriteLine("AI telemetry metadata, privacy, cancellation, timeout and streaming tests passed (offline).");
    }

    private static async Task VerifyHttpProviderAsync(ConcurrentQueue<Activity> completed)
    {
        using var parent = BackendTelemetry.Source.StartActivity("offline-parent");
        using var http = new HttpClient(new Handler(_ => new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                text = Private, model = OpenAiTranscriptionClient.Model,
                usage = new { input_tokens = 11, output_tokens = 7 }
            }), Encoding.UTF8, "application/json")
        }));
        var client = new OpenAiTranscriptionClient(http, "offline-key");
        var result = await client.TranscribeAsync(PcmSpeechAudio.ToDeviceWav(new byte[4800]), CancellationToken.None);
        Require(result == Private, "instrumentation preserves the actual transcription result");
        var activity = completed.Single(item => item.OperationName == "gen_ai.transcription");
        Require(activity.Kind == ActivityKind.Client && activity.ParentSpanId == parent!.SpanId,
            "provider scope is a child of the current operation");
        Require(Tag(activity, "gen_ai.usage.input_tokens") == "11"
            && Tag(activity, "gen_ai.usage.output_tokens") == "7", "only actual provider usage is recorded");

        var providerFailure = await BoundedSdkTransportTests.FailureAsync(429);
        var speech = new ShowCueSpeechClient("offline-key", null,
            _ => Task.FromException<WebSocket>(providerFailure));
        try
        {
            await speech.GenerateAsync(Private, CancellationToken.None);
            throw new InvalidOperationException("Provider failure was accepted.");
        }
        catch (System.ClientModel.ClientResultException) { }
        var failed = completed.Single(item => item.OperationName == "gen_ai.speech");
        Require(failed.Status == ActivityStatusCode.Error && Tag(failed, "app.outcome") == "error"
            && Tag(failed, "http.response.status_code") == "429", "provider status survives without its error body");
    }

    private static async Task VerifyFailureClassificationAsync(ConcurrentQueue<Activity> completed)
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        using (var telemetry = GenAiOperation.Start("cancel-fixture"))
        { telemetry.Fail(new OperationCanceledException(Private), cancelled.Token); }
        using (var telemetry = GenAiOperation.Start("timeout-fixture"))
        { telemetry.Fail(new OperationCanceledException(Private), CancellationToken.None); }
        Require(Tag(completed.Single(item => item.OperationName == "gen_ai.cancel-fixture"), "app.outcome") == "cancelled",
            "caller cancellation is distinguishable from provider timeout");
        Require(Tag(completed.Single(item => item.OperationName == "gen_ai.timeout-fixture"), "app.outcome") == "timeout",
            "provider deadline is classified as timeout");

        using var timeoutHttp = new HttpClient(new Handler(_ => throw new OperationCanceledException(Private)));
        using var provider = new OpenAiVectorMemoryProvider(timeoutHttp, "offline-key", new HybridMemoryOptions());
        try
        {
            await provider.CreateStoreAsync(MemoryScope.Personal, CancellationToken.None);
            throw new InvalidOperationException("Vector timeout was accepted.");
        }
        catch (MemoryProviderException error) when (error.Code == "timeout") { }
        Require(Tag(completed.Single(item => item.OperationName == "gen_ai.vector_store"), "app.outcome") == "timeout",
            "transport timeout remains classified after mapping to a safe domain exception");
    }

    private static async Task VerifyStreamingAsync(ConcurrentQueue<Activity> completed)
    {
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var heard = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var events = new[]
        {
            JsonSerializer.Serialize(new { type = "session.created", session = new { type = "realtime", model = RealtimeProtocol.Model } }),
            JsonSerializer.Serialize(new { type = "session.updated", session = new { type = "realtime", model = RealtimeProtocol.Model } }),
            JsonSerializer.Serialize(new { type = "response.created", response = new { id = "response-1" } }),
            JsonSerializer.Serialize(new { type = "response.output_audio.delta", response_id = "response-1", delta = Convert.ToBase64String(new byte[4800]) }),
            JsonSerializer.Serialize(new
            {
                type = "response.done",
                response = new
                {
                    id = "response-1", status = "completed",
                    output = new[] { new { type = "message", role = "assistant", status = "completed",
                        content = new[] { new { type = "output_audio", transcript = Private } } } },
                    usage = new { input_tokens = 123, output_tokens = 200,
                        input_token_details = new { text_tokens = 123, audio_tokens = 0 } }
                }
            })
        };
        using var socket = new ScriptSocket(events, released.Task);
        using var provider = new OpenAiRealtimeClient(_ => Task.FromResult<WebSocket>(socket));
        var request = new RealtimeTurnRequest(Guid.NewGuid(), Guid.NewGuid(), 0, 63, 0,
            PartyWandCatalog.Version, true, "", null, "marin", 1);
        var pending = provider.GenerateAsync(new(request, true, Private, []), null,
            (_, _) => { heard.TrySetResult(); return Task.CompletedTask; },
            (_, _) => Task.CompletedTask, CancellationToken.None);
        await heard.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Require(!pending.IsCompleted && !completed.Any(item => item.OperationName == "gen_ai.realtime"),
            "stream scope remains open after first output until final usage arrives");
        released.TrySetResult();
        await pending;
        var activity = completed.Single(item => item.OperationName == "gen_ai.realtime");
        Require(Tag(activity, "app.stream.output_chunks") == "1"
            && Tag(activity, "app.stream.output_bytes") == "4800"
            && activity.GetTagItem("app.stream.first_output_ms") is double first && first >= 0,
            "stream lifecycle records aggregate metadata without per-chunk spans");
        Require(Tag(activity, "gen_ai.usage.input_tokens") == "123"
            && Tag(activity, "gen_ai.usage.output_tokens") == "200", "final stream usage is observed");
        Require(Tag(activity, "gen_ai.operation.name") == "chat"
            && Tag(activity, "app.phase") == "completed"
            && activity.GetTagItem("app.stream.ready_ms") is double ready && ready >= 0
            && activity.GetTagItem("app.stream.handshake_ms") is double handshake && handshake >= 0
            && activity.GetTagItem("app.stream.configuration_ms") is double configuration && configuration >= 0,
            "stream readiness and configuration timings use actual lifecycle transitions");
    }

    private static async Task VerifyLifecycleFailuresAsync(ConcurrentQueue<Activity> completed)
    {
        var invalid = JsonSerializer.Serialize(new
        {
            type = "session.created", session = new { type = "realtime", model = "invalid-model" }
        });
        using var socket = new ScriptSocket([invalid], Task.CompletedTask);
        using var provider = new OpenAiRealtimeClient(_ => Task.FromResult<WebSocket>(socket));
        var request = new RealtimeTurnRequest(Guid.NewGuid(), Guid.NewGuid(), 0, 63, 0,
            PartyWandCatalog.Version, true, "", null, "marin", 1);
        try
        {
            await provider.GenerateAsync(new(request, true, Private, []), null,
                (_, _) => Task.CompletedTask, (_, _) => Task.CompletedTask, CancellationToken.None);
            throw new InvalidOperationException("Invalid provider session was accepted.");
        }
        catch (InvalidDataException) { }
        var failed = completed.Single(item => item.OperationName == "gen_ai.realtime" && item.Status == ActivityStatusCode.Error);
        Require(Tag(failed, "app.phase") == "provider_handshake"
            && Tag(failed, "app.stream.disconnect_cause") == "protocol_error"
            && failed.GetTagItem("gen_ai.usage.input_tokens") is null,
            "failed startup retains its actual phase without fabricating usage");

        await using (var connection = new GptLiveConnection(new ScriptSocket([], Task.CompletedTask),
            _ => Task.FromResult<WebSocket>(new ScriptSocket([], Task.CompletedTask))))
        {
            await connection.RestartAsync(1, CancellationToken.None);
        }
        var reconnect = completed.Single(item => item.OperationName == "gen_ai.live.reconnect");
        Require(Tag(reconnect, "app.attempt") == "1" && Tag(reconnect, "app.phase") == "ready"
            && Tag(reconnect, "app.stream.disconnect_cause") == "visual_revoked",
            "provider reconnection records a bounded cause and attempt");
        Require(completed.Any(item => item.OperationName == "gen_ai.live.disconnect"
            && Tag(item, "app.stream.disconnect_cause") == "disposed"), "provider teardown has an explicit lifecycle scope");
    }

    private static void VerifySafeMetadata(IEnumerable<Activity> activities)
    {
        using (var telemetry = GenAiOperation.Start("metadata-fixture", "private model input"))
        {
            using var malformed = JsonDocument.Parse("{\"usage\":{\"input_tokens\":\"private\",\"output_tokens\":-1}}");
            telemetry.ReadUsage(malformed.RootElement);
            telemetry.Tool(Private);
        }
        var metadata = activities.Single(item => item.OperationName == "gen_ai.metadata-fixture");
        Require(metadata.GetTagItem("gen_ai.request.model") is null
            && metadata.GetTagItem("gen_ai.usage.input_tokens") is null
            && metadata.GetTagItem("gen_ai.usage.output_tokens") is null
            && metadata.GetTagItem("app.tool.name") is null,
            "invalid model, untrusted tool names and malformed usage metadata are omitted");
        using (var telemetry = GenAiOperation.Start("image-metadata-fixture"))
        {
            telemetry.ReadUsage(BinaryData.FromString("{\"data\":[{\"b64_json\":\"private-visitor-secret\"}],\"usage\":{\"input_tokens\":3,\"output_tokens\":5}}"));
        }
        var image = activities.Single(item => item.OperationName == "gen_ai.image-metadata-fixture");
        Require(Tag(image, "gen_ai.usage.input_tokens") == "3"
            && Tag(image, "gen_ai.usage.output_tokens") == "5", "image metadata scanning skips base64 content");
        foreach (var activity in activities)
        {
            Require(!activity.TagObjects.Any(pair => pair.Value?.ToString()?.Contains(Private, StringComparison.Ordinal) == true),
                "provider inputs and exception messages never enter tags");
            Require(!activity.Events.Any() && activity.StatusDescription is null,
                "telemetry creates no content-bearing events or exception descriptions");
        }
    }

    private static string? Tag(Activity activity, string name) => activity.GetTagItem(name)?.ToString();
    private static void Require(bool condition, string message)
    {
        if (!condition) { throw new InvalidOperationException(message); }
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(response(request));
    }

    private sealed class ScriptSocket(string[] events, Task released) : WebSocket
    {
        private int index;
        private int offset;
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => WebSocketState.Open;
        public override string? SubProtocol => null;
        public override void Abort() { }
        public override void Dispose() { }
        public override Task CloseAsync(WebSocketCloseStatus status, string? description, CancellationToken token) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus status, string? description, CancellationToken token) => Task.CompletedTask;
        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType type, bool complete, CancellationToken token) => Task.CompletedTask;
        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken token)
        {
            if (index == events.Length - 1) { await released.WaitAsync(token); }
            if (index >= events.Length) { throw new InvalidDataException("Offline script ended."); }
            var data = Encoding.UTF8.GetBytes(events[index]);
            var count = Math.Min(buffer.Count, data.Length - offset);
            data.AsSpan(offset, count).CopyTo(buffer.AsSpan());
            offset += count;
            var complete = offset == data.Length;
            if (complete) { index++; offset = 0; }
            return new(count, WebSocketMessageType.Text, complete);
        }
    }
}
