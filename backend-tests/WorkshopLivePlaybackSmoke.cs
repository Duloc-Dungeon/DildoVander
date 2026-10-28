using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Kufr.Backend.Features.GptLive;
using Kufr.Backend.Features.Speech;
using Kufr.Backend.Features.Wands;
using Microsoft.Extensions.Configuration;

internal static class WorkshopLivePlaybackSmoke
{
    // Reuses the one already-saved fictional result. No question authoring,
    // Responses decision, image job or second wand manufacture is dispatched.
    internal static async Task RunAsync()
    {
        var root = Path.GetFullPath(Path.Combine("ai-eval", "results", "magicky-kufr-gpt-live"));
        using var receipt = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root, "receipt.json")));
        var operation = receipt.RootElement.GetProperty("operation").GetString()!;
        var config = new ConfigurationBuilder().AddEnvironmentVariables().AddInMemoryCollection(new Dictionary<string, string?>
        { ["Backend:DatabasePath"] = Path.Combine(root, "workshop.sqlite") }).Build();
        var store = new WandGenerationStore(config, TimeProvider.System);
        var saved = store.Read("fictional-live-v2", operation)?.SelectedWand ?? throw new InvalidOperationException("Original fictional result missing.");
        var profile = store.ReadWorkshopProfile("fictional-live-v2", operation);
        var admission = new MobileWandAdmission("fictional-live-v2", operation, "fictional", "", "https://fictional.test", DateTimeOffset.UtcNow.AddMinutes(1), profile);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(50));
        using var provider = new GptLiveClient(config);
        await using var live = await provider.ConnectAsync(deadline.Token);
        await live.SendAsync(MobileWorkshopLiveRuntime.Start(admission, [], "marin", "playback-check"), deadline.Token);
        using (var started = await live.ReceiveAsync(deadline.Token))
        { if (started.RootElement.GetProperty("type").GetString() != "session.started") throw new InvalidDataException("Live startup rejected."); }
        live.AudioReady();
        await live.SendAsync(MobileWorkshopLiveRuntime.Opening(admission, false), deadline.Token);
        var clock = Stopwatch.StartNew(); var text = new StringBuilder(); var audio = new MemoryStream(); var finalSent = false;
        using var inputLife = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        var input = Task.Run(async () => {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
            while (await timer.WaitForNextTickAsync(inputLife.Token)) await live.SendAudioAsync(new byte[4800], inputLife.Token);
        }, inputLife.Token);
        try {
            while (clock.Elapsed.TotalSeconds < 28) {
                using var document = await live.ReceiveAsync(deadline.Token); var value = document.RootElement;
                var type = value.GetProperty("type").GetString();
                if (type == "session.output_transcript.delta") text.Append(value.GetProperty("delta").GetString());
                if (type == "session.output_audio.delta") audio.Write(Convert.FromBase64String(value.GetProperty("delta").GetString()!));
                if (type == "error") throw new InvalidDataException("Live provider error.");
                if (!finalSent && clock.Elapsed.TotalSeconds > 12) {
                    finalSent = true;
                    await live.SendAsync(new { type = "session.commentary.append", event_id = "confirmed-result", delegation_id = (string?)null,
                        content = "Backend potvrdil už uloženou osobní hůlku: " + saved.Name + ". Materiál: " + saved.Material + ". Představ krátce její jméno a vlastnost, potom nech prostor hostovi." }, deadline.Token);
                }
            }
        } finally { inputLife.Cancel(); try { await input; } catch (OperationCanceledException) { } }
        await live.CloseAsync(deadline.Token);
        var transcript = text.ToString();
        await File.WriteAllTextAsync(Path.Combine(root, "playback-receipt.json"), JsonSerializer.Serialize(new { model = GptLiveProtocol.Model, transcript, bytes = audio.Length, wandId = saved.WandId, manufactureCalls = 0 }), deadline.Token);
        if (audio.Length > 0 && audio.Length <= PcmSpeechAudio.MaxProviderBytes)
            await File.WriteAllBytesAsync(Path.Combine(root, "live-opening-and-verdict.wav"), PcmSpeechAudio.ToDeviceWav(audio.ToArray()), deadline.Token);
        if (!transcript.Contains(saved.Name, StringComparison.OrdinalIgnoreCase) || !GptLiveProtocol.HasSpeech(audio.ToArray()))
            throw new InvalidDataException("Actual Live output did not verify the saved result; inspect playback receipt, no automatic retry.");
        Console.WriteLine("Actual GPT Live opening and stored-result speech verified; no new manufacture. " + root);
    }
}
