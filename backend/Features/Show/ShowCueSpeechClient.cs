using System.Diagnostics;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text.Json;
using Kufr.Backend.Features.Observability;
using Kufr.Backend.Features.Realtime;
using Kufr.Backend.Features.Speech;
using OpenAI.Realtime;

#pragma warning disable OPENAI002
namespace Kufr.Backend.Features.Show;

internal sealed class ShowCueSpeechClient
{
    internal const string Model = "gpt-realtime-2.1-mini";
    private const int MaxAudioDeltaBytes = PcmSpeechAudio.MaxProviderBytes;
    private const string SpeechInstructions = "Přečti přesně zadaný text v češtině. Nepřidávej, nevynechávej ani nepřeformulovávej žádná slova. "
        + "Nevytvářej úvod, závěr, komentář ani zvukové efekty. Mluv přirozeně a srozumitelně.";

    private readonly ILogger<ShowCueSpeechClient>? logger;
    private readonly Func<CancellationToken, Task<WebSocket>> connect;

    public ShowCueSpeechClient(string apiKey, ILogger<ShowCueSpeechClient>? logger = null)
        : this(apiKey, logger, token => ConnectAsync(apiKey, token))
    {
    }

    internal ShowCueSpeechClient(
        string apiKey,
        ILogger<ShowCueSpeechClient>? logger,
        Func<CancellationToken, Task<WebSocket>> connect)
    {
        this.logger = logger;
        this.connect = connect;
    }

    public Task<byte[]> GenerateAsync(string reply, CancellationToken cancellationToken) =>
        GenerateAsync(reply, SpeechOptions.Default, cancellationToken);

    public async Task<byte[]> GenerateAsync(
        string reply,
        SpeechOptions options,
        CancellationToken cancellationToken)
    {
        using var telemetry = GenAiOperation.Start("speech", Model, streaming: true);
        try
        {
            ValidateRequest(reply, options);
            cancellationToken.ThrowIfCancellationRequested();

            var providerTimer = Stopwatch.StartNew();
            await using var audio = new MemoryStream();
            try
            {
                using (var socket = await connect(cancellationToken))
                using (var session = new BackendRealtimeSession(socket))
                {
                    var updates = session.ReceiveUpdatesAsync(cancellationToken).GetAsyncEnumerator(cancellationToken);
                    try
                    {
                        await session.ConfigureConversationSessionAsync(CreateSessionOptions(options), cancellationToken);
                        await AwaitSessionUpdatedAsync(updates, cancellationToken);
                        await session.StartResponseAsync(CreateResponseOptions(reply), cancellationToken);
                        await ReadAudioAsync(updates, audio, cancellationToken);
                    }
                    finally
                    {
                        await updates.DisposeAsync();
                    }
                }

                providerTimer.Stop();
                cancellationToken.ThrowIfCancellationRequested();
                var pcm = audio.ToArray();
                try
                {
                    var conversionTimer = Stopwatch.StartNew();
                    var wav = PcmSpeechAudio.ToDeviceWav(pcm);
                    conversionTimer.Stop();
                    logger?.LogInformation(
                        "Speech completed: providerMs={ProviderMs}, conversionMs={ConversionMs}, outputBytes={OutputBytes}.",
                        providerTimer.ElapsedMilliseconds,
                        conversionTimer.ElapsedMilliseconds,
                        wav.Length);
                    return wav;
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(pcm);
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(audio.GetBuffer().AsSpan(0, checked((int)audio.Length)));
            }
        }
        catch (Exception telemetryError)
        {
            telemetry.Fail(telemetryError, cancellationToken);
            throw;
        }
    }

    private static void ValidateRequest(string reply, SpeechOptions options)
    {
        if (string.IsNullOrWhiteSpace(reply) || reply.EnumerateRunes().Count() > 500)
        {
            throw new ArgumentException("Speech requires a validated short reply.");
        }

        if (!SpeechOptions.IsValid(options.Voice, options.Speed)
            || options.Voice is not ("marin" or "cedar" or "coral" or "onyx"))
        {
            throw new ArgumentException("Speech options are unsupported by the Realtime voice service.");
        }
    }

    private static RealtimeConversationSessionOptions CreateSessionOptions(SpeechOptions options)
    {
        var session = new RealtimeConversationSessionOptions
        {
            Model = Model,
            Instructions = SpeechInstructions,
            MaxOutputTokenCount = 1024,
            AudioOptions = new RealtimeConversationSessionAudioOptions
            {
                OutputAudioOptions = new RealtimeConversationSessionOutputAudioOptions
                {
                    AudioFormat = new RealtimePcmAudioFormat { Rate = 24_000 },
                    Voice = options.Voice switch
                    {
                        "marin" => RealtimeVoice.Marin,
                        "cedar" => RealtimeVoice.Cedar,
                        // Keep the existing device-facing choice while using a supported Realtime voice.
                        "onyx" => RealtimeVoice.Cedar,
                        "coral" => RealtimeVoice.Coral,
                        _ => throw new ArgumentException("Speech voice is unsupported by the Realtime voice service.")
                    },
                    Speed = (float)options.Speed
                }
            }
        };
        session.OutputModalities.Add(RealtimeOutputModality.Audio);
        return session;
    }

    private static RealtimeResponseOptions CreateResponseOptions(string reply)
    {
        var response = new RealtimeResponseOptions
        {
            Instructions = "Say exactly the following text, with no other spoken words: " + JsonSerializer.Serialize(reply)
        };
        response.InputItems.Clear();
        response.OutputModalities.Add(RealtimeOutputModality.Audio);
        return response;
    }

    private static async Task AwaitSessionUpdatedAsync(
        IAsyncEnumerator<RealtimeServerUpdate> updates,
        CancellationToken token)
    {
        while (await updates.MoveNextAsync())
        {
            switch (updates.Current)
            {
                case RealtimeServerUpdateSessionUpdated:
                    return;
                case RealtimeServerUpdateError:
                    throw new InvalidDataException("Realtime speech session configuration failed.");
            }
        }

        throw new InvalidDataException("Realtime speech session ended before configuration completed.");
    }

    private static async Task ReadAudioAsync(
        IAsyncEnumerator<RealtimeServerUpdate> updates,
        MemoryStream audio,
        CancellationToken token)
    {
        var completed = false;
        while (await updates.MoveNextAsync())
        {
            switch (updates.Current)
            {
                case RealtimeServerUpdateResponseOutputAudioDelta delta:
                {
                    var bytes = delta.Delta.ToMemory();
                    if (bytes.Length == 0 || audio.Length + bytes.Length > MaxAudioDeltaBytes)
                    {
                        throw new InvalidDataException("Realtime speech audio exceeded its limit.");
                    }

                    audio.Write(bytes.Span);
                    break;
                }
                case RealtimeServerUpdateResponseDone done:
                    if (done.Response?.Status != RealtimeResponseStatus.Completed)
                    {
                        throw new InvalidDataException("Realtime speech response did not complete successfully.");
                    }
                    completed = true;
                    break;
                case RealtimeServerUpdateError:
                    throw new InvalidDataException("Realtime speech generation failed.");
            }

            if (completed)
            {
                break;
            }
        }

        token.ThrowIfCancellationRequested();
        if (!completed || audio.Length == 0 || audio.Length % 2 != 0)
        {
            throw new InvalidDataException("Realtime speech returned incomplete PCM audio.");
        }
    }

    private static async Task<WebSocket> ConnectAsync(string apiKey, CancellationToken token)
    {
        var socket = new ClientWebSocket();
        socket.Options.AddSubProtocol("realtime");
        socket.Options.SetRequestHeader("Authorization", "Bearer " + apiKey);
        try
        {
            await socket.ConnectAsync(
                new Uri("wss://api.openai.com/v1/realtime?model=" + Model),
                token);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
#pragma warning restore OPENAI002
