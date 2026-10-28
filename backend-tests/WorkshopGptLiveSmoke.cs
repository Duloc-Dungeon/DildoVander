using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Kufr.Backend.Features.Customers;
using Kufr.Backend.Features.GptLive;
using Kufr.Backend.Features.Show;
using Kufr.Backend.Features.Speech;
using Kufr.Backend.Features.Wands;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

// Explicit paid smoke, fictional data only. Audio uses the production Live startup
// and wire protocol; it does not claim a phone microphone or browser was tested.
internal static class WorkshopGptLiveSmoke
{
    internal static async Task RunAsync()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENAI_API_KEY")))
            throw new InvalidOperationException("Configure a test API key before this explicit smoke.");
        var root = Path.GetFullPath(Path.Combine("ai-eval", "results", "magicky-kufr-gpt-live"));
        Directory.CreateDirectory(root);
        var configuration = new ConfigurationBuilder().AddEnvironmentVariables().AddInMemoryCollection(new Dictionary<string, string?>
        { ["Backend:DatabasePath"] = Path.Combine(root, "workshop.sqlite") }).Build();
        var profile = new CharacterConversationSnapshot("fictional-clockwork-live-v2", 1,
            new("Tereza Klinková", "Město hodinových mostů", "Vymyšlená postava", "Opravuje mosty a věří modré niti.", "cs"),
            new("Tereza Klinková", "Město hodinových mostů", "Opravářka hodinových mostů.", [
                new("Nosí modrou nit na rukavici a opravuje hodinové mosty.", []),
                new("Při selhání plánu raději improvizuje než rozkazuje.", [])
            ], [], [], false, [], "fixture", "fictional-v2", 1, 1, "synthetic"), 1);
        var owner = "fictional-live-v2"; var operation = Guid.NewGuid().ToString("N");
        var store = new WandGenerationStore(configuration, TimeProvider.System);
        // A rerun never creates another manufacture without inspecting the saved operation.
        var prior = store.FindWorkshop(owner, profile.ProfileId, profile.Revision, 1);
        if (prior is not null) { Console.WriteLine("Existing fictional Live operation retained; inspect its receipt instead of paid redispatch."); return; }
        store.BeginMobile(owner, operation, profile, 1);
        store.BeginWorkshop(owner, operation, profile, 1, "fictional-live-group", 1);
        var admission = new MobileWandAdmission(owner, operation, "fictional-session", "", "https://fictional.test", DateTimeOffset.UtcNow.AddMinutes(2), profile);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(100));
        using var client = new GptLiveClient(configuration);
        using var speech = new ShowSpeechProvider(configuration, new Lifetime(), NullLogger<ShowCueSpeechClient>.Instance);
        using var dialogue = new OpenAiMobileWandQuestionProvider(configuration);
        using var wandProvider = new OpenAiWandGenerationProvider(configuration, store);
        var service = new WandGenerationService(store, wandProvider);
        var wav = await speech.SpeakAsync("Když se most zasekne, nejdřív zastavím kola modrou nití. Od hůlky chci bezpečnou cestu domů a snesu, že občas tiše cvakne. To stačí, už ji vyrob.", SpeechOptions.Default, deadline.Token);
        // Existing cue WAV is mono 32 kHz; resample actual speech into Live's 24 kHz PCM.
        var samples = (wav.Length - 44) / 2; var pcm = new byte[(int)(samples * .75) * 2];
        for (var index = 0; index < pcm.Length / 2; index++)
        { var source = Math.Min(samples - 1, index * 4 / 3); pcm[index * 2] = wav[44 + source * 2]; pcm[index * 2 + 1] = wav[45 + source * 2]; }
        await using var live = await client.ConnectAsync(deadline.Token);
        await live.SendAsync(MobileWorkshopLiveRuntime.Start(admission, [], "marin", "fictional-start"), deadline.Token);
        using (var started = await live.ReceiveAsync(deadline.Token))
        { if (started.RootElement.GetProperty("type").GetString() != "session.started") throw new InvalidDataException("Live startup rejected."); }
        live.AudioReady();
        await live.SendAsync(MobileWorkshopLiveRuntime.Opening(admission, false), deadline.Token);
        var clock = Stopwatch.StartNew(); var inputAt = 0; var output = new MemoryStream();
        var transcript = new StringBuilder(); var input = new StringBuilder();
        var counts = new Dictionary<string, int>(); var finish = false; var manufactured = false;
        using var uploadingLife = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        var uploading = Task.Run(async () => {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
            while (await timer.WaitForNextTickAsync(uploadingLife.Token)) {
                var chunk = new byte[4800];
                if (clock.Elapsed.TotalSeconds > 7 && inputAt < pcm.Length) { var length = Math.Min(chunk.Length, pcm.Length - inputAt); pcm.AsSpan(inputAt, length).CopyTo(chunk); inputAt += length; }
                await live.SendAudioAsync(chunk, uploadingLife.Token);
            }
        }, uploadingLife.Token);
        try
        {
            while (clock.Elapsed.TotalSeconds < 65)
            {
                using var document = await live.ReceiveAsync(deadline.Token); var value = document.RootElement;
                var type = value.GetProperty("type").GetString()!; counts[type] = counts.GetValueOrDefault(type) + 1;
                if (type == "session.output_audio.delta") output.Write(Convert.FromBase64String(value.GetProperty("delta").GetString()!));
                if (type == "session.output_transcript.delta") transcript.Append(value.GetProperty("delta").GetString());
                if (type == "session.input_transcript.delta") input.Append(value.GetProperty("delta").GetString());
                if (type == "error") throw new InvalidDataException("Live emitted a provider error; no automatic retry.");
                if (type == "session.delegation.created" && !finish && input.Length > 0)
                {
                    var delegationId = value.GetProperty("delegation").GetProperty("id").GetString();
                    store.AppendWorkshopMessage(owner, operation, Guid.NewGuid().ToString("N"), "user", input.ToString(), "voice");
                    var draft = await dialogue.ReplyAsync(profile, store.ReadWorkshopMessages(owner, operation), deadline.Token);
                    if (draft.ReadyToFinish)
                    {
                        store.FinishWorkshopConversation(owner, operation, profile.ProfileId, profile.Revision); finish = true;
                        var job = store.PendingWorkshops().Single(item => item.OperationId == operation);
                        store.Begin(owner, operation, profile, 1, openAiConsent: true, outputLanguage: "cs");
                        var generated = await service.GenerateAsync(owner, operation, _ => Task.FromResult(true), deadline.Token,
                            evidence: MobileWandWorkshopWorker.GenerationEvidence(job));
                        if (generated.Status != "ready") throw new InvalidDataException("Wand not saved.");
                        var wand = store.Select(owner, operation, profile.ProfileId, profile.Revision, 1, 1); manufactured = true;
                        await live.SendAsync(new { type = "session.commentary.append", event_id = "stored-result", delegation_id = delegationId,
                            content = "Backend právě potvrdil uloženou hůlku: " + wand.Name + ". Představ ji krátce." }, deadline.Token);
                    }
                    else await live.SendAsync(new { type = "session.commentary.append", event_id = Guid.NewGuid().ToString("N"), delegation_id = delegationId, content = draft.Reply }, deadline.Token);
                }
                if (manufactured && counts.GetValueOrDefault("session.output_audio.delta") > 10 && clock.Elapsed.TotalSeconds > 40) break;
            }
        }
        finally { uploadingLife.Cancel(); try { await uploading; } catch (OperationCanceledException) { } }
        await live.CloseAsync(deadline.Token);
        var receipt = new { operation, model = GptLiveProtocol.Model, input = input.ToString(), output = transcript.ToString(), counts,
            audioBytes = output.Length, finish, manufactured, wand = store.Read(owner, operation)?.SelectedWand?.ToPublic(), phoneTested = false };
        await File.WriteAllTextAsync(Path.Combine(root, "receipt.json"), JsonSerializer.Serialize(receipt), deadline.Token);
        if (output.Length is > 0 and < PcmSpeechAudio.MaxProviderBytes)
            await File.WriteAllBytesAsync(Path.Combine(root, "live-output.wav"), PcmSpeechAudio.ToDeviceWav(output.ToArray()), deadline.Token);
        if (!manufactured || input.Length == 0 || output.Length == 0) throw new InvalidDataException("Live smoke did not complete; inspect local receipt, no automatic retry.");
        Console.WriteLine("Real GPT Live audio/transcript/delegation and one saved fictional wand verified. Evidence: " + root);
    }
    private sealed class Lifetime : IHostApplicationLifetime
    { public CancellationToken ApplicationStarted => default; public CancellationToken ApplicationStopping => default; public CancellationToken ApplicationStopped => default; public void StopApplication() { } }
}
