using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text.Json;
using Kufr.Backend.Features.Identity;
using Kufr.Backend.Features.PersonaLab;
using Kufr.Backend.Features.Voices;

namespace Kufr.Backend.Features.Wands;

// Upstream init carries only a conversation ID, not the authenticated customer.
// Give each admitted call its own short-lived engine and unguessable callback.
// Never infer the owner from provider history, a browser ID or an engine name.
internal sealed class SpeechEngineSessions : BackgroundService
{
    private readonly ConcurrentDictionary<string, Entry> entries = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim preparation = new(1, 1);
    private readonly HttpClient http;
    private readonly ElevenLabsWorkshopOptions options;
    private readonly ActorVoiceSettings voices;
    private readonly string? publicOrigin;
    private readonly ILogger<SpeechEngineSessions> logger;
    private readonly SpeechEngineCleanupStore cleanup;

    public SpeechEngineSessions(IConfiguration config, ElevenLabsWorkshopOptions options,
        ActorVoiceSettings voices, ILogger<SpeechEngineSessions> logger)
        : this(config, options, voices, logger, new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
            { Timeout = TimeSpan.FromSeconds(20) }) { }

    internal SpeechEngineSessions(IConfiguration config, ElevenLabsWorkshopOptions options,
        ActorVoiceSettings voices, ILogger<SpeechEngineSessions> logger, HttpClient http)
    {
        this.options = options; this.voices = voices; this.logger = logger; this.http = http;
        cleanup = new(config);
        var origin = config["Wands:Voice:SpeechEnginePublicOrigin"]
            ?? config.GetSection("ApplicationIdentity:AllowedOrigins").GetChildren().Select(child => child.Value)
                .FirstOrDefault(value => Uri.TryCreate(value, UriKind.Absolute, out var candidate)
                    && candidate.Scheme == "https" && !candidate.IsLoopback && candidate.UserInfo.Length == 0
                    && candidate.GetLeftPart(UriPartial.Authority) == value);
        if (!string.IsNullOrWhiteSpace(origin))
        {
            if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) || uri.Scheme != "https"
                || uri.IsLoopback || uri.UserInfo.Length != 0 || uri.GetLeftPart(UriPartial.Authority) != origin)
                throw new InvalidOperationException("Speech Engine requires an exact public HTTPS callback origin.");
            publicOrigin = origin;
        }
    }

    internal bool Available => publicOrigin is not null && !string.IsNullOrWhiteSpace(options.ApiKey);
    internal sealed record Prepared(string Key, string ConversationToken, string FirstMessage);

    internal async Task<Prepared> PrepareAsync(MobileWandAdmission admission, CancellationToken token, string firstMessage = "Poslouchám.")
    {
        if (!Available) throw new WandGenerationException("speech_engine_public_endpoint_required", 503);
        if (!WandGenerationValidation.Text(firstMessage, 1800)) throw new InvalidDataException("Invalid Speech Engine opening.");
        _ = cleanup.Read(); // Fail closed before creating anything if the durable queue is corrupt.
        await preparation.WaitAsync(token);
        var key = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        var entry = new Entry(admission);
        try
        {
            if (entries.Count >= 100 || entries.Values.Any(existing => existing.Admission.Owner == admission.Owner
                && existing.Deadline > DateTimeOffset.UtcNow)) throw new WandGenerationException("wand_session_busy", 409);
            entries[key] = entry;
        }
        finally { preparation.Release(); }
        try
        {
            var voice = voices.Snapshot().KufrVoiceId;
            var callback = new UriBuilder(publicOrigin!) { Scheme = "wss", Path = "/app/v1/customer/wand/speech-engine/" + key }.Uri.AbsoluteUri;
            using (var created = await RequestAsync(HttpMethod.Post, "/v1/speech-engine", new
            {
                name = "WNS workshop " + key[..12], language = "cs",
                speech_engine = new { ws_url = callback },
                overrides = new { first_message = true },
                asr = new { provider = "scribe_realtime" },
                tts = new { model_id = options.Model, voice_id = voice, speed = 1.0 },
                turn = new { turn_eagerness = "normal", speculative_turn = false },
                conversation = new { max_duration_seconds = 180, client_events = new[] {
                    "audio", "interruption", "user_transcript", "tentative_user_transcript", "agent_response", "agent_response_correction" } },
                privacy = new { record_voice = false, retention_days = 0, delete_audio = true, delete_transcript_and_pii = true },
                call_limits = new { agent_concurrency_limit = 1, daily_limit = 1, bursting_enabled = false }
            }, token))
            {
                entry.EngineId = created.RootElement.GetProperty("speech_engine_id").GetString();
                if (entry.EngineId is not { Length: > 5 and < 100 } id || !id.StartsWith("seng_", StringComparison.Ordinal)
                    || id.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_')) throw new InvalidDataException("Invalid Speech Engine ID.");
                cleanup.Record(entry.EngineId, entry.Deadline);
            }
            using var issued = await RequestAsync(HttpMethod.Get,
                "/v1/convai/conversation/token?agent_id=" + entry.EngineId, null, token);
            var conversationToken = issued.RootElement.GetProperty("token").GetString();
            if (conversationToken is not { Length: > 20 and <= 16384 }) throw new InvalidDataException("Invalid conversation token.");
            return new(key, conversationToken, firstMessage);
        }
        catch (Exception error)
        {
            await RemoveAsync(key);
            if (error is OperationCanceledException && token.IsCancellationRequested) throw;
            if (error is WandGenerationException) throw;
            throw new WandGenerationException("speech_engine_preparation_failed", 502);
        }
    }

    internal Entry Require(MobileWandAdmission admission)
    {
        if (admission.SpeechEngineKey is not { } key || !entries.TryGetValue(key, out var entry)
            || entry.Deadline <= DateTimeOffset.UtcNow || entry.Admission.Owner != admission.Owner
            || entry.Admission.Operation != admission.Operation || entry.Admission.AccountSession != admission.AccountSession
            || Interlocked.CompareExchange(ref entry.ControlClaimed, 1, 0) != 0)
            throw new WandGenerationException("speech_engine_session_expired", 401);
        return entry;
    }

    internal async Task AttachAsync(string key, HttpContext context, MobileWorkshopLiveRuntime runtime)
    {
        // Check proof before dictionary lookup to avoid exposing valid capabilities.
        if (!context.WebSockets.IsWebSocketRequest
            || !SpeechEngineProtocol.Verify(context.Request.Headers["X-Elevenlabs-Speech-Engine-Authorization"].ToString(),
                options.ApiKey, DateTimeOffset.UtcNow.ToUnixTimeSeconds()))
        { context.Response.StatusCode = 401; return; }
        if (!entries.TryGetValue(key, out var entry) || entry.Deadline <= DateTimeOffset.UtcNow)
        { context.Response.StatusCode = 401; return; }
        await runtime.RequireAuthorityAsync(entry.Admission, context.RequestAborted);
        if (Interlocked.CompareExchange(ref entry.UpstreamClaimed, 1, 0) != 0)
        { context.Response.StatusCode = 409; return; }
        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        entry.Connected.TrySetResult(socket);
        try { await entry.Closed.Task.WaitAsync(context.RequestAborted); }
        finally { await RemoveAsync(key); }
    }

    internal async Task RunAsync(MobileWandAdmission admission, WebSocket client,
        ElevenLabsWorkshopRuntime runtime, Func<CancellationToken, Task> requireAuthority, CancellationToken token)
    {
        var entry = Require(admission);
        try
        {
            using var opening = CancellationTokenSource.CreateLinkedTokenSource(token);
            opening.CancelAfter(TimeSpan.FromSeconds(25));
            var upstream = await entry.Connected.Task.WaitAsync(opening.Token);
            await using var recognition = new SpeechEngineRecognition(upstream, token);
            await runtime.RunAsync(admission, client, requireAuthority, token,
                sender => new SpeechEngineSpeechProvider(recognition, sender));
        }
        finally { await RemoveAsync(admission.SpeechEngineKey!); }
    }

    private async Task<JsonDocument> RequestAsync(HttpMethod method, string path, object? body, CancellationToken token)
    {
        using var request = new HttpRequestMessage(method, "https://api.elevenlabs.io" + path);
        request.Headers.Add("xi-api-key", options.ApiKey);
        if (body is not null) request.Content = JsonContent.Create(body);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        if (!response.IsSuccessStatusCode) throw new WandGenerationException("speech_engine_preparation_failed", 502);
        await using var input = await response.Content.ReadAsStreamAsync(token);
        using var bytes = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await input.ReadAsync(buffer, token)) != 0)
        {
            if (bytes.Length + count > 65536) throw new InvalidDataException("Speech Engine response exceeded its bound.");
            bytes.Write(buffer, 0, count);
        }
        return JsonDocument.Parse(bytes.ToArray());
    }

    private async Task RemoveAsync(string key)
    {
        if (!entries.TryRemove(key, out var entry)) return;
        entry.Connected.TrySetCanceled();
        entry.Closed.TrySetResult();
        if (entry.Connected.Task.IsCompletedSuccessfully) entry.Connected.Task.Result.Abort();
        if (entry.EngineId is not null) await DeleteResourceAsync(entry.EngineId);
    }

    private async Task DeleteResourceAsync(string engineId)
    {
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var request = new HttpRequestMessage(HttpMethod.Delete, "https://api.elevenlabs.io/v1/speech-engine/" + engineId);
            request.Headers.Add("xi-api-key", options.ApiKey);
            using var response = await http.SendAsync(request, deadline.Token);
            if (!response.IsSuccessStatusCode && response.StatusCode != System.Net.HttpStatusCode.NotFound)
                logger.LogWarning("Speech Engine resource cleanup failed.");
            else cleanup.Forget(engineId);
        }
        catch (Exception) { logger.LogWarning("Speech Engine resource cleanup failed."); }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                foreach (var expired in entries.Where(item => item.Value.Deadline <= DateTimeOffset.UtcNow).Select(item => item.Key))
                    await RemoveAsync(expired);
                if (Available)
                {
                    try
                    {
                        foreach (var pending in cleanup.Read().Where(resource => resource.Deadline <= DateTimeOffset.UtcNow).Take(5))
                            await DeleteResourceAsync(pending.Id);
                    }
                    catch (Exception) { logger.LogWarning("Speech Engine cleanup queue is unavailable."); }
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally { foreach (var key in entries.Keys) await RemoveAsync(key); }
    }

    public override void Dispose() { base.Dispose(); http.Dispose(); preparation.Dispose(); }

    internal sealed class Entry(MobileWandAdmission admission)
    {
        internal MobileWandAdmission Admission { get; } = admission;
        internal DateTimeOffset Deadline { get; } = DateTimeOffset.UtcNow.AddSeconds(210);
        internal string? EngineId;
        internal int UpstreamClaimed;
        internal int ControlClaimed;
        internal TaskCompletionSource<WebSocket> Connected { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Closed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

internal static class SpeechEngineEndpoints
{
    internal static void MapWorkshopSpeechEngine(this WebApplication app)
    {
        if (!app.Services.GetRequiredService<AccountConfiguration>().Enabled) return;
        app.MapGet("/app/v1/customer/wand/speech-engine/{key:regex(^[0-9a-f]{{64}}$)}", async (string key, HttpContext http,
            SpeechEngineSessions engines, MobileWorkshopLiveRuntime runtime) =>
        {
            try { await engines.AttachAsync(key, http, runtime); }
            catch (WandGenerationException error) when (!http.Response.HasStarted)
            { http.Response.StatusCode = error.Status; }
        }).AllowAnonymous();
    }
}
