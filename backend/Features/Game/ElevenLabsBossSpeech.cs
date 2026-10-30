using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Kufr.Backend.Features.Voices;
using Kufr.Backend.Features.Wands;

namespace Kufr.Backend.Features.Game;

internal interface IGameBossSpeech
{
    IGameBossSpeech Capture() => this;
    bool Enabled { get; }
    string CacheKey { get; }
    Task<byte[]> SpeakAsync(string text, CancellationToken token);
}

internal sealed class ElevenLabsBossSpeech : IGameBossSpeech, IDisposable
{
    internal const int MaxAudioBytes = 512 * 1024;
    internal const int MaxSpeechCharacters = 384;
    private readonly HttpClient http;
    private readonly string? key;
    private readonly string voiceId;
    private readonly string model;
    private readonly ActorVoiceSettings? voices;

    public ElevenLabsBossSpeech(IConfiguration configuration, ActorVoiceSettings? voices = null) : this(configuration,
        new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false }), voices) { }

    internal ElevenLabsBossSpeech(IConfiguration configuration, HttpClient http, ActorVoiceSettings? voices = null)
    {
        this.http = http;
        this.voices = voices;
        key = configuration["ELEVENLABS_API_KEY"];
        voiceId = configuration["Game:BossVoice:VoiceId"] ?? WorkshopPrompts.VoiceId;
        model = configuration["Game:BossVoice:Model"] ?? "eleven_v4";
        if (!Regex.IsMatch(voiceId, "\\A[A-Za-z0-9_-]{1,128}\\z", RegexOptions.CultureInvariant)
            || !Regex.IsMatch(model, "\\A[A-Za-z0-9_-]{1,128}\\z", RegexOptions.CultureInvariant))
        { throw new InvalidOperationException("Invalid Baron voice configuration."); }
        if (model != "eleven_v4")
        { throw new InvalidOperationException("Baron emotional delivery requires eleven_v4."); }
    }

    public bool Enabled => !string.IsNullOrWhiteSpace(key);
    public string CacheKey => Capture().CacheKey;

    public IGameBossSpeech Capture()
    {
        try { return new Captured(this, voices?.Snapshot().BaronVoiceId ?? voiceId); }
        catch (ActorVoiceSettingsException) { throw new GameFault(503, "boss_voice_unavailable"); }
    }

    public Task<byte[]> SpeakAsync(string text, CancellationToken token) => Capture().SpeakAsync(text, token);

    private async Task<byte[]> SpeakCapturedAsync(string text, CancellationToken token, string selectedVoice)
    {
        if (!Enabled) { throw new GameFault(503, "boss_voice_unavailable"); }
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaxSpeechCharacters)
        { throw new InvalidDataException("Boss speech exceeds its text bound."); }
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"https://api.elevenlabs.io/v1/text-to-speech/{selectedVoice}?output_format=mp3_44100_128");
        request.Headers.Add("xi-api-key", key);
        request.Headers.Accept.ParseAdd("audio/mpeg");
        request.Content = JsonContent.Create(new
        {
            text, model_id = model,
            voice_settings = new { stability = 0.5, similarity_boost = 0.75 }
        });
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        // Do not propagate provider bodies, which may contain customer text or account details.
        if (!response.IsSuccessStatusCode) { throw new GameFault(503, "boss_voice_unavailable"); }
        if (response.Content.Headers.ContentType?.MediaType != "audio/mpeg"
            || response.Content.Headers.ContentLength is > MaxAudioBytes)
        { throw new InvalidDataException("Invalid boss audio response."); }
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        using var audio = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(buffer, token)) > 0)
        {
            if (audio.Length + count > MaxAudioBytes) { throw new InvalidDataException("Boss audio exceeds its bound."); }
            audio.Write(buffer, 0, count);
        }
        if (audio.Length == 0) { throw new InvalidDataException("Empty boss audio."); }
        return audio.ToArray();
    }

    public void Dispose() => http.Dispose();

    private sealed class Captured(ElevenLabsBossSpeech parent, string voiceId) : IGameBossSpeech
    {
        public bool Enabled => parent.Enabled;
        public string CacheKey => $"{voiceId}:{parent.model}:delivery-v1";
        public Task<byte[]> SpeakAsync(string text, CancellationToken token) => parent.SpeakCapturedAsync(text, token, voiceId);
    }
}
