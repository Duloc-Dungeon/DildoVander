using System.Buffers.Binary;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Kufr.Backend.Features.Show;
using Kufr.Backend.Features.Speech;
using Microsoft.Extensions.Configuration;

internal static class ShowSpeechProviderTests
{
    internal static async Task RunAsync()
    {
        await RealtimeRequestAndDeviceAudioContractAsync();
        await InvalidInputDoesNotConnectAsync();
        await BoundedAndMalformedRealtimeResponsesAsync();
        await FailedResponseDoesNotPublishPartialAudioAsync();
        VerifyLateLeaseReleaseDuringShutdown();
        await CancellationDisposesRealtimeSocketAsync();
        Console.WriteLine("Show speech Realtime tests passed: text/voice/speed, bounded PCM, malformed frames, cancellation and disposal (no network).");
    }

    private static void VerifyLateLeaseReleaseDuringShutdown()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["OPENAI_API_KEY"] = "offline-key" }).Build();
        var provider = new ShowSpeechProvider(configuration, new Lifetime(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ShowCueSpeechClient>.Instance);
        Check(provider.TryAcquire(), "device cue work holds a speech lease before shutdown");
        provider.Dispose();
        provider.Release();
        Check(provider.TryAcquire(), "late cancelled cue cleanup can release its managed speech lease after disposal");
        provider.Release();
    }

    private sealed class Lifetime : Microsoft.Extensions.Hosting.IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }

    private static async Task RealtimeRequestAndDeviceAudioContractAsync()
    {
        const string line = "Ministerstvo uznává zásah.";
        var audio = Pcm();
        var socket = SuccessSocket(audio);
        var client = new ShowCueSpeechClient("fixture-show-key", null, _ => Task.FromResult<WebSocket>(socket));
        var wav = await client.GenerateAsync(line, new SpeechOptions("cedar", 1.15), default);

        Check(wav.AsSpan(0, 4).SequenceEqual("RIFF"u8)
            && wav.AsSpan(8, 4).SequenceEqual("WAVE"u8)
            && BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(24)) == 32_000
            && BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(22)) == 1
            && BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(34)) == 16
            && wav.Length == 44 + 640 * 2,
            "Realtime 24 kHz PCM becomes the existing 32 kHz mono PCM16 device WAV with preserved duration");
        Check(wav.AsSpan(44).ToArray().Any(value => value != 0), "device audio contains converted PCM");
        Check(socket.Disposed, "Realtime socket is disposed after a completed response");

        var commands = socket.SentMessages.Select(message => JsonDocument.Parse(message)).ToArray();
        var update = commands.Select(document => document.RootElement)
            .Single(root => root.GetProperty("type").GetString() == "session.update");
        var outputAudio = update.GetProperty("session").GetProperty("audio").GetProperty("output");
        Check(update.GetProperty("session").GetProperty("model").GetString() == ShowCueSpeechClient.Model
            && update.GetProperty("session").GetProperty("max_output_tokens").GetInt32() == 1024
            && outputAudio.GetProperty("format").GetProperty("rate").GetInt32() == 24_000
            && outputAudio.GetProperty("voice").GetString() == "cedar"
            && Math.Abs(outputAudio.GetProperty("speed").GetSingle() - 1.15f) < 0.001f,
            "Realtime session uses the replacement model, PCM24, selected voice and selected speed");

        var response = commands.Select(document => document.RootElement)
            .Single(root => root.GetProperty("type").GetString() == "response.create");
        Check(response.GetProperty("response").GetProperty("output_modalities")[0].GetString() == "audio"
            && response.GetProperty("response").GetProperty("instructions").GetString()!.Contains(
                JsonSerializer.Serialize(line), StringComparison.Ordinal),
            "response requests audio and passes the exact authored line without adding conversational input");

        var onyxSocket = SuccessSocket(audio);
        var onyxClient = new ShowCueSpeechClient("fixture-show-key", null,
            _ => Task.FromResult<WebSocket>(onyxSocket));
        _ = await onyxClient.GenerateAsync(line, new SpeechOptions("onyx", 1), default);
        using var onyxUpdate = JsonDocument.Parse(onyxSocket.SentMessages
            .Single(message => message.Contains("session.update", StringComparison.Ordinal)));
        Check(onyxUpdate.RootElement.GetProperty("session").GetProperty("audio").GetProperty("output")
            .GetProperty("voice").GetString() == "cedar",
            "legacy onyx selection remains accepted and maps to the supported cedar voice");
    }

    private static async Task InvalidInputDoesNotConnectAsync()
    {
        var connectCalls = 0;
        var client = new ShowCueSpeechClient("fixture-show-key", null, _ =>
        {
            connectCalls++;
            throw new InvalidOperationException("Invalid input must not connect.");
        });
        foreach (var line in new[] { "", " \n\t", new string('a', 501), string.Concat(Enumerable.Repeat("🪄", 501)) })
        {
            await RejectAsync<ArgumentException>(() => client.GenerateAsync(line, default));
        }
        await RejectAsync<ArgumentException>(() => client.GenerateAsync("Ahoj", new SpeechOptions("alloy", 1), default));
        await RejectAsync<ArgumentException>(() => client.GenerateAsync("Ahoj", new SpeechOptions("marin", 1.26), default));
        Check(connectCalls == 0, "invalid text, unsupported voice or speed does not open a paid Realtime session");
    }

    private static async Task BoundedAndMalformedRealtimeResponsesAsync()
    {
        var oversizedFrames = new List<string> { SessionCreated(), SessionUpdated() };
        oversizedFrames.AddRange(Enumerable.Range(0, 9).Select(_ => AudioDelta(new byte[180_000])));
        oversizedFrames.Add(ResponseDone());
        var oversized = new ScriptedSocket(oversizedFrames);
        var oversizedClient = new ShowCueSpeechClient("fixture-show-key", null,
            _ => Task.FromResult<WebSocket>(oversized));
        await RejectAsync<InvalidDataException>(() => oversizedClient.GenerateAsync("Ahoj", default));
        Check(oversized.Disposed, "oversized PCM response disposes its session socket");

        var malformed = new ScriptedSocket(["not-json"]);
        var malformedClient = new ShowCueSpeechClient("fixture-show-key", null,
            _ => Task.FromResult<WebSocket>(malformed));
        await RejectAnyAsync(() => malformedClient.GenerateAsync("Ahoj", default));
        Check(malformed.Disposed, "malformed Realtime frame disposes its session socket");
    }

    private static async Task CancellationDisposesRealtimeSocketAsync()
    {
        using var cancellation = new CancellationTokenSource();
        var socket = new ScriptedSocket([SessionCreated(), SessionUpdated()], cancellation);
        var client = new ShowCueSpeechClient("fixture-show-key", null,
            _ => Task.FromResult<WebSocket>(socket));
        await RejectAsync<OperationCanceledException>(() => client.GenerateAsync("Ahoj", cancellation.Token));
        Check(socket.Disposed, "cancelled Realtime session disposes the WebSocket");
    }

    private static async Task FailedResponseDoesNotPublishPartialAudioAsync()
    {
        foreach (var status in new[] { "failed", "incomplete", "cancelled" })
        {
            var socket = new ScriptedSocket(
                [SessionCreated(), SessionUpdated(), AudioDelta(Pcm()), ResponseDone(status)]);
            var client = new ShowCueSpeechClient("fixture-show-key", null,
                _ => Task.FromResult<WebSocket>(socket));
            await RejectAsync<InvalidDataException>(() => client.GenerateAsync("Ahoj", default));
            Check(socket.Disposed, $"{status} Realtime response disposes the session after partial audio");
        }
    }

    private static ScriptedSocket SuccessSocket(byte[] pcm) => new(
        [SessionCreated(), SessionUpdated(), AudioDelta(pcm), ResponseDone()]);

    private static string SessionCreated() => """
        {"type":"session.created","event_id":"evt_created","session":{"id":"sess_1","object":"realtime.session","model":"gpt-realtime-2.1-mini"}}
        """;

    private static string SessionUpdated() => """
        {"type":"session.updated","event_id":"evt_updated","session":{"id":"sess_1","object":"realtime.session","model":"gpt-realtime-2.1-mini"}}
        """;

    private static string AudioDelta(byte[] bytes) => JsonSerializer.Serialize(new
    {
        type = "response.output_audio.delta", event_id = "evt_audio", response_id = "resp_1",
        item_id = "item_1", output_index = 0, content_index = 0, delta = Convert.ToBase64String(bytes)
    });

    private static string ResponseDone(string status = "completed") => JsonSerializer.Serialize(new
    {
        type = "response.done", event_id = "evt_done",
        response = new { id = "resp_1", @object = "realtime.response", status, output = Array.Empty<object>() }
    });

    private static byte[] Pcm()
    {
        var bytes = new byte[480 * 2];
        for (var sample = 0; sample < 480; sample++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(sample * 2),
                (short)(10_000 * Math.Sin(2 * Math.PI * 800 * sample / 24_000)));
        }
        return bytes;
    }

    private static async Task RejectAsync<T>(Func<Task<byte[]>> action) where T : Exception
    {
        try
        {
            _ = await action();
        }
        catch (T)
        {
            return;
        }

        throw new InvalidOperationException($"Show speech test expected {typeof(T).Name}.");
    }

    private static async Task RejectAnyAsync(Func<Task<byte[]>> action)
    {
        try
        {
            _ = await action();
        }
        catch (Exception)
        {
            return;
        }

        throw new InvalidOperationException("Show speech test expected malformed frame rejection.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"Show speech test failed: {message}");
        }
    }

    private sealed class ScriptedSocket(IReadOnlyList<string> frames, CancellationTokenSource? cancelWhenExhausted = null) : WebSocket
    {
        private readonly Queue<byte[]> incoming = new(frames.Select(Encoding.UTF8.GetBytes));
        private readonly List<string> sentMessages = [];
        private WebSocketState state = WebSocketState.Open;

        internal IReadOnlyList<string> SentMessages => sentMessages;
        internal bool Disposed { get; private set; }

        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => state;
        public override string? SubProtocol => "realtime";
        public override void Abort() => state = WebSocketState.Aborted;
        public override void Dispose()
        {
            Disposed = true;
            state = WebSocketState.Closed;
        }

        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        {
            state = WebSocketState.Closed;
            return Task.CompletedTask;
        }

        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        {
            state = WebSocketState.CloseSent;
            return Task.CompletedTask;
        }

        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (messageType != WebSocketMessageType.Text || !endOfMessage)
            {
                throw new InvalidDataException("Unexpected client WebSocket frame.");
            }

            sentMessages.Add(Encoding.UTF8.GetString(buffer));
            return Task.CompletedTask;
        }

        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer,
            CancellationToken cancellationToken)
        {
            if (incoming.Count == 0)
            {
                cancelWhenExhausted?.Cancel();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            var bytes = incoming.Dequeue();
            if (bytes.Length > buffer.Count)
            {
                throw new InvalidDataException("Scripted Realtime frame does not fit the receive buffer.");
            }

            bytes.CopyTo(buffer.Array!, buffer.Offset);
            return new WebSocketReceiveResult(bytes.Length, WebSocketMessageType.Text, true);
        }
    }
}
