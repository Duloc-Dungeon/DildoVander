using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using Kufr.Backend.Features.Voices;

namespace Kufr.Backend.Features.Wands;

internal sealed record WorkshopRecognition(string Text, bool Committed, string? EventId = null, bool SpeechStarted = false,
    bool MoreSpeechPending = false);

internal sealed class WorkshopReplyLease(Action? release = null) : IDisposable
{
    public void Dispose() => Interlocked.Exchange(ref release, null)?.Invoke();
}

internal interface IWorkshopRecognitionConnection : IAsyncDisposable
{
    Task SendAudioAsync(byte[] pcm, CancellationToken token);
    Task<WorkshopRecognition?> ReceiveAsync(CancellationToken token);
}

internal interface IWorkshopSpeechProvider
{
    IWorkshopSpeechProvider Capture() => this;
    IWorkshopSpeechProvider ForReply(string? eventId) => this;
    bool IsCurrentReply => true;
    WorkshopReplyLease? EnterReply() => new();
    Task CompleteReplyAsync(CancellationToken token) => Task.CompletedTask;
    Task<IWorkshopRecognitionConnection> ConnectAsync(CancellationToken token);
    Task SpeakAsync(string text, Func<byte[], CancellationToken, Task> audio, CancellationToken token);
    Task SpeakAsync(string text, Func<byte[], CancellationToken, Task> audio, CancellationToken token, string? previousText) =>
        SpeakAsync(text, audio, token);
}

// All credentials and provider connections stay on the server. The browser keeps
// the existing same-origin PCM24 WebSocket contract, including on loopback.
internal sealed class ElevenLabsWorkshopProvider : IWorkshopSpeechProvider, IDisposable
{
    private readonly ElevenLabsWorkshopOptions options;
    private readonly HttpClient http;
    private readonly ActorVoiceSettings? voices;

    public ElevenLabsWorkshopProvider(ElevenLabsWorkshopOptions options, ActorVoiceSettings? voices = null)
        : this(options, new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
            { Timeout = Timeout.InfiniteTimeSpan }, voices) { }

    internal ElevenLabsWorkshopProvider(ElevenLabsWorkshopOptions options, HttpClient http, ActorVoiceSettings? voices = null)
    {
        this.options = options;
        this.http = http;
        this.voices = voices;
    }

    public IWorkshopSpeechProvider Capture()
    {
        try { return new Captured(this, voices?.Snapshot().KufrVoiceId ?? options.VoiceId); }
        catch (ActorVoiceSettingsException) { throw new WandGenerationException("elevenlabs_voice_settings_unavailable", 503); }
    }

    internal static Uri RecognitionUri => new("wss://api.elevenlabs.io/v1/speech-to-text/realtime"
        + "?model_id=scribe_v2_realtime&audio_format=pcm_24000&language_code=cs"
        + "&commit_strategy=vad&vad_silence_threshold_secs=0.9");

    internal static byte[] AudioMessage(byte[] pcm) => JsonSerializer.SerializeToUtf8Bytes(new
    {
        message_type = "input_audio_chunk", audio_base_64 = Convert.ToBase64String(pcm), sample_rate = 24000
    });

    internal static WorkshopRecognition? ParseRecognition(JsonElement root)
    {
        var type = root.GetProperty("message_type").GetString();
        if (type is "partial_transcript" or "committed_transcript" or "committed_transcript_with_timestamps")
        {
            var text = root.GetProperty("text").GetString() ?? "";
            if (text.Length > 1800) { throw new WandGenerationException("elevenlabs_transcription_failed", 502); }
            var id = root.TryGetProperty("event_id", out var eventId) ? eventId.GetString() : null;
            if (id?.Length > 200) { throw new WandGenerationException("elevenlabs_transcription_failed", 502); }
            return new(text.Trim(), type != "partial_transcript", id);
        }
        if (type != "session_started") { throw new WandGenerationException("elevenlabs_transcription_failed", 502); }
        return null;
    }

    public async Task<IWorkshopRecognitionConnection> ConnectAsync(CancellationToken token)
    {
        RequireKey();
        var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("xi-api-key", options.ApiKey);
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(15));
            await socket.ConnectAsync(RecognitionUri, deadline.Token);
            var connection = new RecognitionConnection(socket);
            using var started = await connection.ReadMessageAsync(deadline.Token);
            if (started.RootElement.GetProperty("message_type").GetString() != "session_started")
            {
                throw new InvalidDataException("Speech recognition did not start.");
            }
            return connection;
        }
        catch (Exception error) when (error is not OutOfMemoryException && !token.IsCancellationRequested)
        {
            socket.Dispose();
            throw new WandGenerationException("elevenlabs_connection_failed", 502);
        }
        catch { socket.Dispose(); throw; }
    }

    internal static object SpeechRequest(string text, double speed, string model = WorkshopPrompts.SpeechModel,
        string? previousText = null)
    {
        var request = new Dictionary<string, object>
        {
            ["text"] = text, ["model_id"] = model, ["language_code"] = WorkshopPrompts.SpeechLanguage,
            ["voice_settings"] = new { speed }
        };
        if (!string.IsNullOrEmpty(previousText)) { request["previous_text"] = previousText; }
        return request;
    }

    public Task SpeakAsync(string text, Func<byte[], CancellationToken, Task> audio, CancellationToken token) =>
        SpeakAsync(text, audio, token, null);

    public Task SpeakAsync(string text, Func<byte[], CancellationToken, Task> audio, CancellationToken token,
        string? previousText) => Capture().SpeakAsync(text, audio, token, previousText);

    private async Task SpeakCapturedAsync(string text, Func<byte[], CancellationToken, Task> audio, CancellationToken token,
        string? previousText, string voiceId)
    {
        RequireKey();
        if (string.IsNullOrWhiteSpace(text) || text.Length > 1800 || previousText?.Length > 1800)
        {
            throw new WandGenerationException("elevenlabs_speech_failed", 502);
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(45));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post,
                "https://api.elevenlabs.io/v1/text-to-speech/" + voiceId + "/stream?output_format=pcm_24000");
            request.Headers.Add("xi-api-key", options.ApiKey);
            request.Content = JsonContent.Create(SpeechRequest(text, options.Speed, options.Model, previousText));
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (!response.IsSuccessStatusCode) { throw new HttpRequestException("Speech provider rejected request."); }
            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
            var buffer = new byte[8192];
            var carry = 0;
            var total = 0;
            while (true)
            {
                var count = await stream.ReadAsync(buffer.AsMemory(carry), deadline.Token);
                if (count == 0) { break; }
                count += carry;
                var even = count & ~1;
                total += even;
                if (total > 24000 * 2 * 90) { throw new InvalidDataException("Speech output exceeded its bound."); }
                if (even > 0) { await audio(buffer.AsSpan(0, even).ToArray(), deadline.Token); }
                carry = count - even;
                if (carry > 0) { buffer[0] = buffer[even]; }
            }
            if (carry != 0 || total == 0) { throw new InvalidDataException("Invalid PCM speech output."); }
        }
        catch (Exception error) when (error is not OutOfMemoryException && !token.IsCancellationRequested)
        {
            throw new WandGenerationException("elevenlabs_speech_failed", 502);
        }
    }

    private void RequireKey()
    {
        if (string.IsNullOrWhiteSpace(options.ApiKey)) { throw new WandGenerationException("elevenlabs_not_configured", 503); }
    }

    public void Dispose() => http.Dispose();

    private sealed class Captured(ElevenLabsWorkshopProvider parent, string voiceId) : IWorkshopSpeechProvider
    {
        public Task<IWorkshopRecognitionConnection> ConnectAsync(CancellationToken token) => parent.ConnectAsync(token);
        public Task SpeakAsync(string text, Func<byte[], CancellationToken, Task> audio, CancellationToken token) =>
            parent.SpeakCapturedAsync(text, audio, token, null, voiceId);
        public Task SpeakAsync(string text, Func<byte[], CancellationToken, Task> audio, CancellationToken token, string? previousText) =>
            parent.SpeakCapturedAsync(text, audio, token, previousText, voiceId);
    }

    private sealed class RecognitionConnection(ClientWebSocket socket) : IWorkshopRecognitionConnection
    {
        public Task SendAudioAsync(byte[] pcm, CancellationToken token) =>
            socket.SendAsync(new ArraySegment<byte>(AudioMessage(pcm)), WebSocketMessageType.Text, true, token);

        internal async Task<JsonDocument> ReadMessageAsync(CancellationToken token)
        {
            using var message = new MemoryStream();
            var buffer = new byte[8192];
            for (var fragments = 0; fragments < 64; fragments++)
            {
                var frame = await socket.ReceiveAsync(buffer, token);
                if (frame.MessageType != WebSocketMessageType.Text || message.Length + frame.Count > 64 * 1024)
                {
                    throw new WandGenerationException("elevenlabs_transcription_failed", 502);
                }
                message.Write(buffer, 0, frame.Count);
                if (frame.EndOfMessage) { return JsonDocument.Parse(message.ToArray()); }
            }
            throw new WandGenerationException("elevenlabs_transcription_failed", 502);
        }

        public async Task<WorkshopRecognition?> ReceiveAsync(CancellationToken token)
        {
            using var message = await ReadMessageAsync(token);
            return ParseRecognition(message.RootElement);
        }

        public ValueTask DisposeAsync() { socket.Dispose(); return ValueTask.CompletedTask; }
    }
}
