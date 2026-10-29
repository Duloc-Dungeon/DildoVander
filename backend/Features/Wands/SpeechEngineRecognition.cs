using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using Kufr.Backend.Features.PersonaLab;

namespace Kufr.Backend.Features.Wands;

internal sealed class SpeechEngineRecognition : IWorkshopRecognitionConnection
{
    private readonly WebSocket upstream;
    private readonly LabSocketSender sender;
    private readonly Channel<WorkshopRecognition> incoming = Channel.CreateBounded<WorkshopRecognition>(
        new BoundedChannelOptions(16) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = false });
    private readonly CancellationTokenSource life;
    private readonly Task reader;
    private long newestEvent;
    private readonly object eventGate = new();
    private readonly SemaphoreSlim speechSendGate = new(1, 1);
    private long finalizedThrough = -1;
    private int disposed;
    private long activityEvent;
    private long playbackSequence;
    internal long NextPlaybackSequence() => Interlocked.Increment(ref playbackSequence);
    internal TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal SpeechEngineRecognition(WebSocket upstream, CancellationToken token)
    {
        this.upstream = upstream;
        sender = new LabSocketSender(upstream);
        life = CancellationTokenSource.CreateLinkedTokenSource(token);
        reader = ReadAsync();
    }

    public Task SendAudioAsync(byte[] pcm, CancellationToken token) =>
        throw new InvalidDataException("Speech Engine audio must use WebRTC.");

    public async Task<WorkshopRecognition?> ReceiveAsync(CancellationToken token) =>
        await incoming.Reader.WaitToReadAsync(token) ? await incoming.Reader.ReadAsync(token) : null;

    internal void Activity(long eventId)
    {
        lock (eventGate)
        {
            if (eventId <= newestEvent || eventId <= activityEvent || eventId > 9007199254740991L) return;
            activityEvent = eventId;
            if (!incoming.Writer.TryWrite(new("", false, eventId.ToString(System.Globalization.CultureInfo.InvariantCulture), SpeechStarted: true)))
                life.Cancel();
        }
    }

    internal bool HasPendingAfter(string? id) { lock (eventGate) return activityEvent > SpeechEngineProtocol.EventId(id); }
    internal bool IsPendingActivity(string? id) { lock (eventGate) return SpeechEngineProtocol.EventId(id) > newestEvent; }
    internal bool IsCurrent(long eventId) { lock (eventGate) return newestEvent == eventId && activityEvent <= eventId; }
    internal WorkshopReplyLease? EnterReply(long eventId)
    {
        Monitor.Enter(eventGate);
        if (newestEvent == eventId && activityEvent <= eventId) return new(() => Monitor.Exit(eventGate));
        Monitor.Exit(eventGate);
        return null;
    }

    internal async Task SendAsync(long eventId, string content, bool final, CancellationToken token)
    {
        // Never relabel an old reply with a newer turn's ID. ElevenLabs also
        // discards old IDs, but this fence avoids sending stale synthesis work.
        await speechSendGate.WaitAsync(token);
        try
        {
            lock (eventGate) { if (eventId <= finalizedThrough || newestEvent != eventId || activityEvent > eventId) return; }
            await sender.JsonAsync(SpeechEngineProtocol.Response(eventId, content, final), token);
            if (final) { lock (eventGate) { finalizedThrough = Math.Max(finalizedThrough, eventId); } }
        }
        finally { speechSendGate.Release(); }
    }

    private async Task ReadAsync()
    {
        Exception? failure = null;
        try
        {
            var initialized = false;
            long received = 0;
            while (!life.IsCancellationRequested)
            {
                var frame = await MobileWorkshopLiveRuntime.ReceiveClientMessageAsync(upstream, life.Token, maximumControlMessageBytes: 65536);
                if (frame is null) break;
                received += frame.Value.Payload.Length;
                if (frame.Value.Type != WebSocketMessageType.Text || received > 2 * 1024 * 1024)
                    throw new InvalidDataException("Speech Engine input exceeded its bound.");
                using var message = JsonDocument.Parse(frame.Value.Payload);
                var root = message.RootElement;
                switch (root.GetProperty("type").GetString())
                {
                    case "init":
                        if (initialized || root.GetProperty("conversation_id").GetString() is not { Length: > 5 and < 150 })
                            throw new InvalidDataException("Invalid Speech Engine initialization.");
                        initialized = true;
                        Ready.TrySetResult();
                        break;
                    case "ping":
                        await sender.JsonAsync(new { type = "pong" }, life.Token);
                        break;
                    case "close": return;
                    case "error": throw new WandGenerationException("speech_engine_connection_failed", 502);
                    case "user_transcript":
                        if (!initialized || !root.GetProperty("event_id").TryGetInt64(out var eventId) || eventId < 1)
                            throw new InvalidDataException("Invalid Speech Engine turn.");
                        if (eventId <= Interlocked.Read(ref newestEvent)) break;
                        var transcript = root.GetProperty("user_transcript");
                        if (transcript.ValueKind != JsonValueKind.Array || transcript.GetArrayLength() is < 1 or > 128)
                            throw new InvalidDataException("Invalid Speech Engine history.");
                        // Provider history is context, never authority. Only the
                        // newest user turn enters our existing validated runtime.
                        var last = transcript.EnumerateArray().LastOrDefault(item => item.ValueKind == JsonValueKind.Object
                            && item.TryGetProperty("role", out var role) && role.ValueKind == JsonValueKind.String && role.GetString() == "user");
                        if (last.ValueKind != JsonValueKind.Object
                            || last.GetProperty("content").GetString() is not { Length: > 0 and <= 1800 } text)
                            throw new InvalidDataException("Invalid Speech Engine transcript.");
                        lock (eventGate)
                        {
                            if (eventId <= newestEvent) break;
                            newestEvent = eventId;
                            if (!incoming.Writer.TryWrite(new(text, true, eventId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                                MoreSpeechPending: activityEvent > eventId)))
                                throw new InvalidDataException("Speech Engine queue exceeded its bound.");
                        }
                        break;
                    default: throw new InvalidDataException("Unknown Speech Engine message.");
                }
            }
        }
        catch (OperationCanceledException) when (life.IsCancellationRequested) { }
        catch (Exception error) { failure = error; }
        finally
        {
            Ready.TrySetException(failure ?? new IOException("Speech Engine closed before initialization."));
            incoming.Writer.TryComplete(failure);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        life.Cancel();
        upstream.Abort();
        try { await reader; } catch (Exception) { }
        life.Dispose();
    }
}

internal sealed class SpeechEngineSpeechProvider(SpeechEngineRecognition recognition, LabSocketSender client,
    long eventId = 0) : IWorkshopSpeechProvider
{
    private bool started;
    public bool IsCurrentReply => recognition.IsCurrent(eventId);
    public WorkshopReplyLease? EnterReply() => recognition.EnterReply(eventId);
    public async Task<IWorkshopRecognitionConnection> ConnectAsync(CancellationToken token)
    {
        await recognition.Ready.Task.WaitAsync(token);
        return recognition;
    }
    public IWorkshopSpeechProvider ForReply(string? id) => new SpeechEngineSpeechProvider(recognition, client, SpeechEngineProtocol.EventId(id));
    public async Task SpeakAsync(string text, Func<byte[], CancellationToken, Task> audio, CancellationToken token)
    {
        if (!started)
        {
            await client.JsonAsync(new { type = "speech_engine_speaking", eventId, sequence = recognition.NextPlaybackSequence() }, token);
            started = true;
        }
        await recognition.SendAsync(eventId, text, false, token);
    }
    public Task CompleteReplyAsync(CancellationToken token) => recognition.SendAsync(eventId, "", true, token);
}
