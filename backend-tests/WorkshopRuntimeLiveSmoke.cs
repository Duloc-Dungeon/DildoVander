using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Kufr.Backend.Features.GptLive;
using Kufr.Backend.Features.Show;
using Kufr.Backend.Features.Speech;
using Kufr.Backend.Features.Wands;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

internal static partial class MobileGameTests
{
    // Explicit paid diagnostic only. The real Live provider runs behind the actual
    // customer bridge; accounts, dialogue decisions and manufacture stay isolated.
    internal static async Task RunWorkshopRuntimeLiveSmokeAsync(string evidenceCase = "")
    {
        var configuration = new ConfigurationBuilder().AddEnvironmentVariables().Build();
        if (string.IsNullOrWhiteSpace(configuration["OPENAI_API_KEY"]))
            throw new InvalidOperationException("Configure a test key before the explicit Live diagnostic.");
        if (evidenceCase is not ("" or "v2" or "v3")) throw new ArgumentException("Unknown diagnostic case.");
        var output = Path.GetFullPath(Path.Combine("ai-eval", "results", "workshop-reliability-live" + (evidenceCase.Length == 0 ? "" : "-" + evidenceCase)));
        Directory.CreateDirectory(output);
        var attempt = Path.Combine(output, "attempt.json");
        if (File.Exists(attempt))
            throw new InvalidOperationException("A diagnostic attempt already exists; inspect it before another paid session.");
        await File.WriteAllTextAsync(attempt, JsonSerializer.Serialize(new { startedAt = DateTimeOffset.UtcNow, targetLiveSeconds = 65, maximumLiveSeconds = 90, fictionalData = true }));

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(150));
        using var speech = new ShowSpeechProvider(configuration, new WorkshopDiagnosticLifetime(),
            NullLogger<ShowCueSpeechClient>.Instance);
        var first = await speech.SpeakAsync("Nejdřív zajistím most, aby se nikdo nezranil. Potom bych prohlédla jeho ozubená kola.", SpeechOptions.Default, deadline.Token);
        var second = await speech.SpeakAsync("Od hůlky chci bezpečnou cestu domů. Trochu cvakání mi nevadí, na mostech jsem zvyklá.", SpeechOptions.Default, deadline.Token);
        var inputs = new[] { DiagnosticPcm(first), DiagnosticPcm(second) };
        using var provider = new GptLiveClient(configuration);
        await using var fixture = await HttpFixture.StartAsync(withLiveTransport: true, liveClient: provider);
        using var client = fixture.Client();
        await fixture.LoginAsync(client, fixture.ForeignId);
        client.DefaultRequestHeaders.Add("X-DV-CSRF", await fixture.CsrfAsync(client));
        await fixture.MarkForeignCharacterPreparedAsync();
        const string path = "/app/v1/customer/wand/workshop/sessions";
        var operation = Guid.NewGuid().ToString("N");
        using var created = await client.PostAsJsonAsync(path, new { operationId = operation }, deadline.Token);
        Check(created.StatusCode == HttpStatusCode.OK, "fictional diagnostic opens a prepared workshop");
        using var socket = await ConnectWorkshopSocketAsync(client, fixture, path, operation, deadline.Token);
        var ready = await ReceiveSocketJsonAsync(socket, deadline.Token);
        Check(ready.GetProperty("type").GetString() == "ready", "real customer bridge reaches Live ready");
        deadline.CancelAfter(TimeSpan.FromSeconds(90));

        var clock = Stopwatch.StartNew();
        using var audio = new MemoryStream();
        var user = new StringBuilder(); var assistant = new StringBuilder();
        var userBeforeSecond = 0; var userAfterSecond = 0; var audioAfterSecond = 0;
        var polls = 0; var stopInputRequested = 0;
        using var inputLife = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        var input = Task.Run(async () =>
        {
            var index = 0; var offset = 0;
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
            while (await timer.WaitForNextTickAsync(deadline.Token))
            {
                if (Volatile.Read(ref stopInputRequested) != 0) break;
                var pcm = new byte[4800];
                if (index < inputs.Length && clock.Elapsed.TotalSeconds >= (index == 0 ? 10 : 32))
                {
                    var length = Math.Min(pcm.Length, inputs[index].Length - offset);
                    inputs[index].AsSpan(offset, length).CopyTo(pcm);
                    offset += length;
                    if (offset == inputs[index].Length) { index++; offset = 0; }
                }
                await SendFragmentedAsync(socket, WebSocketMessageType.Binary, pcm, deadline.Token);
            }
        }, deadline.Token);
        var polling = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
            while (await timer.WaitForNextTickAsync(inputLife.Token))
            {
                using var response = await client.GetAsync("/app/v1/customer/wand/workshop", inputLife.Token);
                Check(response.StatusCode == HttpStatusCode.OK, "real Live status polling remains available");
                polls++;
            }
        }, inputLife.Token);
        var stop = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(65), deadline.Token);
            // Cancelling an in-flight .NET WebSocket send aborts its entire
            // socket. Finish capture's current send before ending the session.
            Volatile.Write(ref stopInputRequested, 1);
            await input;
            inputLife.Cancel();
            try { await polling; } catch (OperationCanceledException) { }
            await SendFragmentedAsync(socket, WebSocketMessageType.Text, Encoding.UTF8.GetBytes("{\"type\":\"end\"}"), deadline.Token);
            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "diagnostic complete", deadline.Token);
        }, deadline.Token);

        try
        {
            while (true)
            {
                var read = MobileWorkshopLiveRuntime.ReceiveClientMessageAsync(socket, deadline.Token);
                await Task.WhenAny(read, stop);
                if (stop.IsCompleted && !stop.IsCompletedSuccessfully) await stop;
                var received = await read;
                if (received is null) break;
                if (received.Value.Type == WebSocketMessageType.Binary)
                {
                    audio.Write(received.Value.Payload);
                    if (clock.Elapsed.TotalSeconds > 32) audioAfterSecond++;
                    continue;
                }
                using var json = JsonDocument.Parse(received.Value.Payload);
                var value = json.RootElement;
                var type = value.GetProperty("type").GetString();
                if (type == "error") throw new InvalidOperationException("Customer Live bridge reported " + value.GetProperty("code").GetString());
                if (type != "transcript") continue;
                var delta = value.GetProperty("delta").GetString();
                if (value.GetProperty("role").GetString() == "user")
                {
                    user.Append(delta);
                    if (clock.Elapsed.TotalSeconds < 30) userBeforeSecond++; else userAfterSecond++;
                }
                else assistant.Append(delta);
            }
            await stop;
            await WaitUntilAsync(() => !fixture.LiveRuntime.IsActive(fixture.ForeignId, operation), deadline.Token);
            var messages = fixture.Wands.ReadWorkshopMessages(fixture.ForeignId, operation);
            var receipt = new
            {
                model = GptLiveProtocol.Model, transport = "customer-http-ticket-websocket-MobileWorkshopLiveRuntime",
                elapsedSeconds = clock.Elapsed.TotalSeconds, audioBytes = audio.Length, audioAfterSecond,
                userBeforeSecond, userAfterSecond, polls, userTranscript = user.ToString(), assistantTranscript = assistant.ToString(),
                savedMessages = messages.Length, backendDecisions = fixture.TextDialogueCalls,
                decisionProvider = "isolated-offline-test-provider",
                generatedWands = fixture.Wands.Read(fixture.ForeignId, operation)?.SelectedWand is null ? 0 : 1,
                pendingManufactures = fixture.Wands.PendingWorkshops().Count(job => job.OperationId == operation), physicalPhone = false
            };
            SaveDiagnosticWave(Path.Combine(output, "output.wav"), audio.ToArray());
            await File.WriteAllTextAsync(Path.Combine(output, "receipt.json"), JsonSerializer.Serialize(receipt, new JsonSerializerOptions { WriteIndented = true }));
            Check(userBeforeSecond > 0 && userAfterSecond > 0 && audioAfterSecond > 0 && audio.Length > 48000,
                "real Live keeps receiving and speaking beyond the first sentence across two spoken exchanges");
            Check(polls >= 5 && messages.Any(message => message.Role == "user")
                && fixture.Wands.PendingWorkshops().All(job => job.OperationId != operation),
                "real runtime preserves dialogue and does not manufacture in this diagnostic");
            Check(!System.Text.RegularExpressions.Regex.IsMatch(assistant.ToString(),
                @"\b(hotovo|hůlka je hotová|mám pro tebe hůlku)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase),
                "Live does not announce a completed wand when the backend has not manufactured one");
            Console.WriteLine("Real workshop runtime diagnostic passed; receipt: " + Path.Combine(output, "receipt.json"));
        }
        catch (Exception failure)
        {
            SaveDiagnosticWave(Path.Combine(output, "partial-output.wav"), audio.ToArray());
            await File.WriteAllTextAsync(Path.Combine(output, "partial-receipt.json"), JsonSerializer.Serialize(new
            {
                elapsedSeconds = clock.Elapsed.TotalSeconds, audioBytes = audio.Length,
                userTranscript = user.ToString(), assistantTranscript = assistant.ToString(),
                failure = LiveFailureDiagnostics.Code(failure), serverFailureCodes = fixture.LiveFailureCodes,
                inputStatus = input.Status.ToString(), pollingStatus = polling.Status.ToString(), stopStatus = stop.Status.ToString(),
                deadlineExpired = deadline.IsCancellationRequested, polls, physicalPhone = false
            }, new JsonSerializerOptions { WriteIndented = true }));
            throw;
        }
        finally
        {
            Volatile.Write(ref stopInputRequested, 1);
            inputLife.Cancel(); deadline.Cancel();
            try { await Task.WhenAll(input, polling, stop); } catch (Exception) { }
        }
    }

    private static byte[] DiagnosticPcm(byte[] wav)
    {
        if (wav.Length < 44 || BitConverter.ToInt32(wav, 24) != 32000
            || BitConverter.ToInt16(wav, 22) != 1 || BitConverter.ToInt16(wav, 34) != 16)
            throw new InvalidDataException("Expected the existing speech provider's mono 32 kHz PCM WAV.");
        var samples = (wav.Length - 44) / 2;
        var pcm = new byte[(int)(samples * .75) * 2];
        for (var index = 0; index < pcm.Length / 2; index++)
        {
            var source = Math.Min(samples - 1, index * 4 / 3);
            pcm[index * 2] = wav[44 + source * 2]; pcm[index * 2 + 1] = wav[45 + source * 2];
        }
        return pcm;
    }

    private static void SaveDiagnosticWave(string path, byte[] pcm)
    {
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + pcm.Length);
        writer.Write(Encoding.ASCII.GetBytes("WAVEfmt ")); writer.Write(16); writer.Write((short)1); writer.Write((short)1);
        writer.Write(24000); writer.Write(48000); writer.Write((short)2); writer.Write((short)16);
        writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(pcm.Length); writer.Write(pcm);
    }

    private sealed class WorkshopDiagnosticLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }
}
