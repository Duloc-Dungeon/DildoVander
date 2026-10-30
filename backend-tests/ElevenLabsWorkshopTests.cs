using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Kufr.Backend.Features.Customers;
using Kufr.Backend.Features.Identity;
using Kufr.Backend.Features.Wands;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

internal static class ElevenLabsWorkshopTests
{
    internal static async Task RunAsync()
    {
        await ProtocolAsync();
        await MobileWandDialogueStreamingTests.RunAsync();
        await SpeechTransportAsync();
        await FullDuplexStreamingAsync();
        await VerdictSnapshotAsync();
        await LateRecognitionAfterCompletionAsync();
        await CompletionWhileRecognitionAwaitsAuthorityAsync();
        await ClauseContextAsync();
        await DurableDecisionAsync(revoke: false, changeEvidence: false);
        await DurableDecisionAsync(revoke: true, changeEvidence: false);
        await DurableDecisionAsync(revoke: false, changeEvidence: true);
        await DurableDecisionAsync(revoke: false, changeEvidence: false, capture: true);
        await PendingSpeechBlocksManufactureAsync();
        await MissingConsentAsync();
        Console.WriteLine("ElevenLabs workshop offline checks passed.");
    }

    private static async Task SpeechTransportAsync()
    {
        var handler = new SpeechHandler();
        using var provider = new ElevenLabsWorkshopProvider(new() { ApiKey = "synthetic-server-only" }, new HttpClient(handler));
        var samples = new List<byte>();
        await provider.SpeakAsync("A short reply", (bytes, _) => { samples.AddRange(bytes); return Task.CompletedTask; }, default);
        Check(samples.SequenceEqual(new byte[] { 1, 2, 3, 4 }), "TTS HTTP stream forwards only raw even PCM");
        Check(handler.ServerKey && handler.PcmFormat, "TTS key is a server header and output is PCM24");
        await provider.SpeakAsync("Next clause.", (_, _) => Task.CompletedTask, default, "Prior clause.");
        Check(handler.PreviousText == "Prior clause.", "documented previous_text carries prior speech without becoming spoken text");
        handler.Reject = true;
        try { await provider.SpeakAsync("reply", (_, _) => Task.CompletedTask, default); throw new Exception("Rejected TTS accepted"); }
        catch (WandGenerationException error) { Check(error.Code == "elevenlabs_speech_failed", "TTS HTTP error body never escapes"); }
    }

    private sealed class SpeechHandler : HttpMessageHandler
    {
        internal bool ServerKey;
        internal bool PcmFormat;
        internal bool Reject;
        internal string? PreviousText;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            ServerKey = request.Headers.GetValues("xi-api-key").Single() == "synthetic-server-only";
            PcmFormat = request.RequestUri!.Query == "?output_format=pcm_24000";
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            Check(body.RootElement.GetProperty("language_code").GetString() == "cs", "HTTP speech payload stays Czech");
            PreviousText = body.RootElement.TryGetProperty("previous_text", out var previous) ? previous.GetString() : null;
            Check(!body.RootElement.TryGetProperty("instructions", out _) && !body.RootElement.TryGetProperty("prompt", out _),
                "TTS never sends an unsupported instruction field or speaks the delivery guide");
            return Reject
                ? new(System.Net.HttpStatusCode.Unauthorized) { Content = new StringContent("secret rejection body") }
                : new(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3, 4]) };
        }
    }

    private static async Task ProtocolAsync()
    {
        var options = ElevenLabsWorkshopOptions.Read(new ConfigurationBuilder().Build());
        Check(OpenAiMobileWandQuestionProvider.DefaultInstructions == WorkshopPrompts.Dialogue
            && WorkshopPrompts.Dialogue == File.ReadAllText("backend/Features/Wands/Prompts/dialogue.cs-CZ.txt").Trim()
            && WorkshopPrompts.SpeechDirections == File.ReadAllText("backend/Features/Wands/Prompts/speech.cs-CZ.txt").Trim(),
            "embedded canonical Czech dialogue and delivery guide match the approved source files");
        Check(!options.UseLegacyGptLive && options.Speed == 1.0 && options.Model == "eleven_v4",
            "ElevenLabs v4 is the explicit default with normal speed");
        Check(options.VoiceId == WorkshopPrompts.VoiceId && options.Model == WorkshopPrompts.SpeechModel
            && options.Speed == WorkshopPrompts.SpeechSpeed && WorkshopPrompts.SpeechLanguage == "cs",
            "canonical voice, model, language and speed settings remain the actual defaults");
        Check(ElevenLabsWorkshopProvider.RecognitionUri.Query.Contains("audio_format=pcm_24000")
            && ElevenLabsWorkshopProvider.RecognitionUri.Query.Contains("commit_strategy=vad"), "Czech recognition uses bounded PCM24 VAD protocol");
        using var audio = JsonDocument.Parse(ElevenLabsWorkshopProvider.AudioMessage([1, 2, 3, 4]));
        Check(audio.RootElement.GetProperty("audio_base_64").GetString() == "AQIDBA=="
            && audio.RootElement.GetProperty("sample_rate").GetInt32() == 24000, "audio chunks retain raw PCM and sample rate");
        using var partial = JsonDocument.Parse("""{"message_type":"partial_transcript","text":"hello"}""");
        using var committed = JsonDocument.Parse("""{"message_type":"committed_transcript","text":"hello","event_id":"one"}""");
        Check(ElevenLabsWorkshopProvider.ParseRecognition(partial.RootElement) is { Committed: false }
            && ElevenLabsWorkshopProvider.ParseRecognition(committed.RootElement) is { Committed: true, EventId: "one" }, "only committed speech becomes guest evidence");
        using var error = JsonDocument.Parse("""{"message_type":"auth_error","error":"secret upstream body"}""");
        try { ElevenLabsWorkshopProvider.ParseRecognition(error.RootElement); throw new Exception("Error event accepted"); }
        catch (WandGenerationException failure) { Check(failure.Code == "elevenlabs_transcription_failed", "upstream failures are sanitized"); }
        using var request = JsonDocument.Parse(JsonSerializer.Serialize(ElevenLabsWorkshopProvider.SpeechRequest("hello", 1.0)));
        Check(request.RootElement.GetProperty("model_id").GetString() == "eleven_v4"
            && request.RootElement.GetProperty("language_code").GetString() == "cs"
            && request.RootElement.GetProperty("voice_settings").GetProperty("speed").GetDouble() == 1.0
            && request.RootElement.GetProperty("voice_settings").EnumerateObject().Count() == 1,
            "speech request uses normal v4 voice settings without custom style, stability or speaker boost");
        using var provider = new ElevenLabsWorkshopProvider(options);
        try { await provider.ConnectAsync(default); throw new Exception("Missing key accepted"); }
        catch (WandGenerationException failure) { Check(failure.Code == "elevenlabs_not_configured", "missing credentials fail without a provider call or silent fallback"); }
        using var fixture = new Fixture();
        var experiment = new LocalTryHubExperimentSnapshot(7, "gpt-live", "Custom dialogue instructions", "Custom live instructions");
        using var startup = JsonDocument.Parse(JsonSerializer.Serialize(MobileWorkshopLiveRuntime.Start(
            fixture.Admission with { LocalExperiment = experiment }, [], "fixture-voice", "fixture-start", "Warm fixture voice")));
        var liveSession = startup.RootElement.GetProperty("session");
        Check(liveSession.GetProperty("instructions").GetString()!.StartsWith(experiment.LivePrompt, StringComparison.Ordinal)
            && liveSession.GetProperty("instructions").GetString()!.Contains("Warm fixture voice", StringComparison.Ordinal)
            && liveSession.GetProperty("input")[0].GetProperty("content")[0].GetProperty("text").GetString()!
                .Contains(fixture.Admission.Profile.ProfileId, StringComparison.Ordinal),
            "captured live prompt overrides dialogue instructions while trusted context and voice description remain attached");
    }

    private static async Task DurableDecisionAsync(bool revoke, bool changeEvidence, bool capture = false)
    {
        using var fixture = new Fixture();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        using var requestLife = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        var authorized = true;
        var captured = false;
        Task RequireAuthority(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (!authorized) { throw new WandGenerationException("account_session_revoked", 401); }
            if (captured) { throw new WandGenerationException("wand_workshop_boss_captured", 409); }
            return Task.CompletedTask;
        }
        var run = fixture.Runtime.RunAsync(fixture.Admission, fixture.Socket, RequireAuthority, requestLife.Token);
        await fixture.Speech.Started.Task.WaitAsync(timeout.Token);
        Check(fixture.Wands.ReadWorkshopMessages("owner", "operation").All(message => message.Role != "user")
            && fixture.Wands.ReadWorkshopState("owner", "operation").Status == "asking", "even an opening ready flag cannot manufacture without guest evidence");
        fixture.Socket.Control("cancel");
        await fixture.Speech.Canceled.Task.WaitAsync(timeout.Token);
        fixture.Speech.Recognition.Events.Writer.TryWrite(new("partial", false));
        fixture.Speech.Recognition.Events.Writer.TryWrite(new("I protect my friends.", true, "turn-one"));
        fixture.Speech.Recognition.Events.Writer.TryWrite(new("I protect my friends.", true, "turn-one"));
        await fixture.Dialogue.Deciding.Task.WaitAsync(timeout.Token);
        Check(fixture.Wands.ReadWorkshopMessages("owner", "operation").Count(message => message.Role == "user") == 1,
            "partials and duplicate commits never become duplicate evidence");
        if (changeEvidence)
        {
            fixture.Wands.AppendWorkshopMessage("owner", "operation", Guid.NewGuid().ToString("N"),
                "user", "A new correction.", "text");
        }
        authorized = !revoke;
        if (capture)
        {
            fixture.Wands.PauseWorkshopForBoss("owner", "operation");
            captured = true;
        }
        fixture.Socket.Control("end");
        requestLife.Cancel();
        fixture.Dialogue.Release.TrySetResult();
        await run.WaitAsync(timeout.Token);
        Check(fixture.Wands.ReadWorkshopState("owner", "operation").Status
            == (capture ? "paused" : revoke || changeEvidence ? "asking" : "generating"),
            "disconnect preserves an accepted decision while revocation and changed evidence block manufacture");
        if (capture)
        {
            fixture.Wands.ResumeWorkshopAfterBoss("owner", "operation");
            Check(fixture.Wands.ReadWorkshopState("owner", "operation").Status == "generating",
                "boss rescue resumes the accepted deferred finish without repeating the guest decision");
        }
        Check(fixture.Speech.Calls == 1, "Stop and disconnect prevent late GPT replies from restarting speech");
        Check(fixture.Socket.Events.Any(item => item.Contains("ended", StringComparison.Ordinal)), "normal closure reports ended explicitly");
    }

    private static async Task PendingSpeechBlocksManufactureAsync()
    {
        using var fixture = new Fixture();
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var run = fixture.Runtime.RunAsync(fixture.Admission, fixture.Socket, _ => Task.CompletedTask, lifetime.Token);
        await fixture.Speech.Started.Task.WaitAsync(lifetime.Token);
        fixture.Speech.Recognition.Events.Writer.TryWrite(new("I protect my friends.", true, "first"));
        await fixture.Dialogue.Deciding.Task.WaitAsync(lifetime.Token);
        var interruptions = fixture.Socket.Events.Count(item => item.Contains("interrupted", StringComparison.Ordinal));
        fixture.Speech.Recognition.Events.Writer.TryWrite(new("But I also", false));
        await WaitAsync(() => fixture.Socket.Events.Count(item => item.Contains("interrupted", StringComparison.Ordinal)) > interruptions);
        fixture.Dialogue.Release.TrySetResult();
        await Task.Delay(150, lifetime.Token);
        Check(fixture.Wands.ReadWorkshopState("owner", "operation").Status == "asking",
            "an uncommitted guest continuation blocks an otherwise ready GPT draft");
        fixture.Speech.Recognition.Events.Writer.TryWrite(new("But I also need to keep the bridge safe.", true, "second"));
        await WaitAsync(() => fixture.Wands.ReadWorkshopState("owner", "operation").Status == "generating");
        lifetime.Cancel();
        await run;
        Check(fixture.Wands.ReadWorkshopMessages("owner", "operation").Count(message => message.Role == "user") == 2,
            "the newly committed continuation is retained before the fresh decision starts manufacture");

        async Task WaitAsync(Func<bool> condition)
        {
            while (!condition()) { await Task.Delay(10, lifetime.Token); }
        }
    }

    private static async Task MissingConsentAsync()
    {
        using var fixture = new Fixture();
        await fixture.Runtime.RunAsync(fixture.Admission with { ElevenLabsConsent = false }, fixture.Socket,
            _ => Task.CompletedTask, default);
        Check(fixture.Speech.Connections == 0 && fixture.Socket.Events.Any(item => item.Contains("elevenlabs_consent_required", StringComparison.Ordinal)),
            "forged or old admission without explicit consent never opens ElevenLabs");
    }

    private static async Task FullDuplexStreamingAsync()
    {
        var dialogue = new StreamingDialogue();
        using var fixture = new Fixture(dialogue);
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var admission = fixture.Admission with
        { LocalExperiment = new(7, "elevenlabs", "Custom stream instructions", "Custom live instructions") };
        var run = fixture.Runtime.RunAsync(admission, fixture.Socket, _ => Task.CompletedTask, lifetime.Token);
        await fixture.Speech.Started.Task.WaitAsync(lifetime.Token);
        Check(dialogue.ObservedInstructions == admission.LocalExperiment.DialoguePrompt,
            "streaming uses the connection's captured developer prompt snapshot");
        Check(fixture.Speech.Captures == 1, "one immutable speech provider is captured for the entire workshop connection");
        Check(!dialogue.Release.Task.IsCompleted
            && fixture.Wands.ReadWorkshopMessages("owner", "operation").Length == 0,
            "first PCM is sent while GPT is still producing its structured reply");
        fixture.Socket.Audio([1, 2, 3, 4]);
        await fixture.Speech.Recognition.AudioReceived.Task.WaitAsync(lifetime.Token);
        Check(fixture.Speech.Calls == 1, "microphone PCM keeps reaching recognition during speech synthesis");
        fixture.Speech.Recognition.Events.Writer.TryWrite(new("Actual", false));
        await dialogue.Superseded.Task.WaitAsync(lifetime.Token);
        await WaitAsync(() => Snapshots(fixture.Socket, "user").Length == 1);
        fixture.Speech.Recognition.Events.Writer.TryWrite(new("Actually", false));
        await WaitAsync(() => Snapshots(fixture.Socket, "user").Length == 2);
        Check(!fixture.Wands.ReadWorkshopMessages("owner", "operation").Any(message => message.Role == "user"),
            "visible corrected interim recognition is not durable guest evidence");
        fixture.Speech.Recognition.Events.Writer.TryWrite(new("Actually", false));
        fixture.Speech.Recognition.Events.Writer.TryWrite(new("Actually I protect the bridge.", true, "fresh"));
        fixture.Speech.Recognition.Events.Writer.TryWrite(new("Actually I protect the bridge.", true, "fresh"));
        await dialogue.SecondStarted.Task.WaitAsync(lifetime.Token);
        await WaitAsync(() => fixture.Speech.Calls == 2);
        var interruptions = fixture.Socket.Events.Count(item => item.Contains("interrupted", StringComparison.Ordinal));
        fixture.Socket.Control("cancel");
        await WaitAsync(() => fixture.Socket.Events.Count(item => item.Contains("interrupted", StringComparison.Ordinal)) > interruptions);
        Check(!dialogue.SecondCanceled.Task.IsCompleted, "Stop cancels speech without canceling the current GPT decision");
        dialogue.Release.TrySetResult();
        await WaitAsync(() => fixture.Wands.ReadWorkshopMessages("owner", "operation").Any(message => message.Role == "assistant"));
        await WaitAsync(() => Snapshots(fixture.Socket, "assistant").Any(snapshot => snapshot.GetProperty("final").GetBoolean()));
        Check(fixture.Speech.Calls == 2 && fixture.Wands.ReadWorkshopState("owner", "operation").Status == "asking",
            "no old or stopped audio restarts and provisional clauses never trigger manufacture");
        var users = Snapshots(fixture.Socket, "user");
        Check(users.Select(snapshot => snapshot.GetProperty("text").GetString()).SequenceEqual(
                new[] { "Actual", "Actually", "Actually I protect the bridge." })
            && users.Select(snapshot => snapshot.GetProperty("sequence").GetInt64()).Distinct().Count() == 1
            && users.Count(snapshot => snapshot.GetProperty("final").GetBoolean()) == 1,
            "revised full user snapshots replace one turn and a duplicate commit never repeats final text");
        var assistants = Snapshots(fixture.Socket, "assistant");
        var finalSequence = assistants.Single(snapshot => snapshot.GetProperty("final").GetBoolean()).GetProperty("sequence").GetInt64();
        Check(assistants.Count(snapshot => snapshot.GetProperty("sequence").GetInt64() == finalSequence) == 2,
            "one assistant turn keeps its ID through streamed text and the authoritative final snapshot");
        var all = Snapshots(fixture.Socket);
        Check(all.Select(snapshot => snapshot.GetProperty("sequence").GetInt64()).SequenceEqual(
                all.Select(snapshot => snapshot.GetProperty("sequence").GetInt64()).Order())
            && !fixture.Socket.Events.Any(item =>
            {
                using var document = JsonDocument.Parse(item);
                return document.RootElement.GetProperty("type").GetString() == "transcript";
            }),
            "superseded assistant text cannot follow a newer user turn and legacy transcript events do not duplicate snapshots");
        lifetime.Cancel();
        await run;

        async Task WaitAsync(Func<bool> condition)
        {
            while (!condition()) { await Task.Delay(10, lifetime.Token); }
        }
    }

    private static async Task VerdictSnapshotAsync()
    {
        using var fixture = new Fixture();
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = fixture.Runtime.RunAsync(fixture.Admission, fixture.Socket, _ => Task.CompletedTask, lifetime.Token);
        await fixture.Speech.Started.Task.WaitAsync(lifetime.Token);
        fixture.Socket.Control("cancel");
        await fixture.Speech.Canceled.Task.WaitAsync(lifetime.Token);
        var profile = fixture.Admission.Profile;
        fixture.Wands.AppendWorkshopMessage("owner", "operation", Guid.NewGuid().ToString("N"), "user", "Protect the bridge.", "voice");
        fixture.Wands.FinishWorkshopConversation("owner", "operation", profile.ProfileId, profile.Revision);
        fixture.Wands.Begin("owner", "operation", profile, 1, true);
        var job = fixture.Wands.Claim("owner", "operation", false) ?? throw new InvalidOperationException("Missing synthetic job");
        fixture.Wands.Complete(job, MobileWandTests.Candidates().Take(1).ToArray());
        var wand = fixture.Wands.Select("owner", "operation", profile.ProfileId, profile.Revision, 1);
        while (fixture.Speech.Calls < 2) { await Task.Delay(10, lifetime.Token); }
        var snapshots = Snapshots(fixture.Socket, "assistant");
        Check(snapshots.Last().GetProperty("final").GetBoolean()
            && snapshots.Last().GetProperty("text").GetString()!.Contains(wand.Name, StringComparison.Ordinal)
            && snapshots.Last().GetProperty("sequence").GetInt64() > snapshots.First().GetProperty("sequence").GetInt64(),
            "the real saved-wand verdict uses a new final snapshot before its speech");
        Check(fixture.Speech.Captures == 1, "the saved-wand verdict retains the connection's original voice capture");
        lifetime.Cancel();
        await run;
    }

    private static async Task LateRecognitionAfterCompletionAsync()
    {
        using var fixture = new Fixture();
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = fixture.Runtime.RunAsync(fixture.Admission, fixture.Socket, _ => Task.CompletedTask, lifetime.Token);
        await fixture.Speech.Started.Task.WaitAsync(lifetime.Token);
        fixture.Socket.Control("cancel");
        await fixture.Speech.Canceled.Task.WaitAsync(lifetime.Token);
        var profile = fixture.Admission.Profile;
        fixture.Wands.AppendWorkshopMessage("owner", "operation", Guid.NewGuid().ToString("N"), "user", "Protect the bridge.", "voice");
        fixture.Wands.FinishWorkshopConversation("owner", "operation", profile.ProfileId, profile.Revision);
        fixture.Wands.Begin("owner", "operation", profile, 1, true);
        var job = fixture.Wands.Claim("owner", "operation", false) ?? throw new InvalidOperationException("Missing synthetic job");
        fixture.Wands.Complete(job, MobileWandTests.Candidates().Take(1).ToArray());
        fixture.Wands.Select("owner", "operation", profile.ProfileId, profile.Revision, 1);
        await WaitAsync(() =>
        {
            var snapshots = Snapshots(fixture.Socket, "assistant");
            return fixture.Speech.Calls == 2 && snapshots.Length > 0
                && snapshots[^1].GetProperty("final").GetBoolean();
        });
        var interruptedCalls = fixture.Speech.CanceledCalls;
        fixture.Speech.Recognition.Events.Writer.TryWrite(new("", false, SpeechStarted: true));
        fixture.Speech.Recognition.Events.Writer.TryWrite(new("Late partial.", false));
        fixture.Speech.Recognition.Events.Writer.TryWrite(new("Late committed words.", true, "late-event"));
        await Task.Delay(100, lifetime.Token);
        Check(fixture.Speech.Calls == 2 && fixture.Speech.CanceledCalls == interruptedCalls
            && fixture.Wands.ReadWorkshopMessages("owner", "operation").Count(message => message.Role == "user") == 1
            && !Snapshots(fixture.Socket, "user").Any(snapshot => snapshot.GetProperty("text").GetString()!.StartsWith("Late", StringComparison.Ordinal)),
            "late start, partial and committed recognition after completion neither cancel verdict audio nor enter history");
        lifetime.Cancel();
        await run;

        async Task WaitAsync(Func<bool> condition)
        {
            while (!condition()) { await Task.Delay(10, lifetime.Token); }
        }
    }

    private static async Task CompletionWhileRecognitionAwaitsAuthorityAsync()
    {
        using var fixture = new Fixture();
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var releaseAuthority = new ManualResetEventSlim();
        var blockAuthority = 0;
        var blockedAuthorities = 0;
        var authorityBlocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task RequireAuthority(CancellationToken token)
        {
            if (Volatile.Read(ref blockAuthority) == 0) { return; }
            Interlocked.Increment(ref blockedAuthorities);
            authorityBlocked.TrySetResult();
            await Task.Run(() => releaseAuthority.Wait(token), token);
        }
        var run = fixture.Runtime.RunAsync(fixture.Admission, fixture.Socket, RequireAuthority, lifetime.Token);
        await fixture.Speech.Started.Task.WaitAsync(lifetime.Token);
        fixture.Socket.Control("cancel");
        await fixture.Speech.Canceled.Task.WaitAsync(lifetime.Token);
        var profile = fixture.Admission.Profile;
        fixture.Wands.AppendWorkshopMessage("owner", "operation", Guid.NewGuid().ToString("N"), "user", "Protect the bridge.", "voice");
        Volatile.Write(ref blockAuthority, 1);
        var interruptedBefore = fixture.Socket.Events.Count(item => item.Contains("interrupted", StringComparison.Ordinal));
        fixture.Speech.Recognition.Events.Writer.TryWrite(new("Late committed words.", true, "late-authority-event"));
        await authorityBlocked.Task.WaitAsync(lifetime.Token);
        await WaitAsync(() => fixture.Socket.Events.Count(item => item.Contains("interrupted", StringComparison.Ordinal)) > interruptedBefore
            && Volatile.Read(ref blockedAuthorities) >= 2);
        fixture.Wands.FinishWorkshopConversation("owner", "operation", profile.ProfileId, profile.Revision);
        fixture.Wands.Begin("owner", "operation", profile, 1, true);
        var job = fixture.Wands.Claim("owner", "operation", false) ?? throw new InvalidOperationException("Missing synthetic job");
        fixture.Wands.Complete(job, MobileWandTests.Candidates().Take(1).ToArray());
        var wand = fixture.Wands.Select("owner", "operation", profile.ProfileId, profile.Revision, 1);
        releaseAuthority.Set();
        await WaitAsync(() => fixture.Speech.Calls == 2 && Snapshots(fixture.Socket, "assistant").Any(snapshot =>
            snapshot.GetProperty("final").GetBoolean() && snapshot.GetProperty("text").GetString()!.Contains(wand.Name, StringComparison.Ordinal)));
        var interruptedCalls = fixture.Speech.CanceledCalls;
        await Task.Delay(100, lifetime.Token);
        Check(fixture.Wands.ReadWorkshopMessages("owner", "operation").Count(message => message.Role == "user") == 1
            && !Snapshots(fixture.Socket, "user").Any(snapshot => snapshot.GetProperty("text").GetString()!.StartsWith("Late", StringComparison.Ordinal))
            && fixture.Speech.CanceledCalls == interruptedCalls,
            "recognition released from authority after completion cannot append late evidence, replace accepted input, or cancel verdict speech");
        lifetime.Cancel();
        await run;

        async Task WaitAsync(Func<bool> condition)
        {
            while (!condition()) { await Task.Delay(10, lifetime.Token); }
        }
    }

    private static JsonElement[] Snapshots(TestSocket socket, string? role = null) => socket.Events.Select(item =>
    {
        using var document = JsonDocument.Parse(item);
        return document.RootElement.Clone();
    }).Where(snapshot => snapshot.GetProperty("type").GetString() == "workshop_transcript"
        && (role is null || snapshot.GetProperty("role").GetString() == role)).ToArray();

    private static async Task ClauseContextAsync()
    {
        using var fixture = new Fixture(new ClauseDialogue());
        fixture.Speech.Block = false;
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = fixture.Runtime.RunAsync(fixture.Admission, fixture.Socket, _ => Task.CompletedTask, lifetime.Token);
        while (fixture.Speech.Calls < 3) { await Task.Delay(10, lifetime.Token); }
        fixture.Speech.Recognition.Events.Writer.TryWrite(new("My response.", true, "context-user"));
        while (fixture.Speech.Calls < 6) { await Task.Delay(10, lifetime.Token); }
        var contexts = fixture.Speech.Contexts.ToArray();
        Check(contexts.Take(3).SequenceEqual(new[] { "", "First clause.", "First clause. Second clause." })
            && contexts.Skip(3).Take(3).SequenceEqual(contexts.Take(3)),
            "TTS clauses receive exact prior reply text and continuity resets for the next GPT reply");
        lifetime.Cancel();
        await run;
    }

    private sealed class ClauseDialogue : IMobileWandQuestionProvider
    {
        public async Task<MobileWandDialogueDraft> ReplyStreamingAsync(CharacterConversationSnapshot profile,
            MobileWandWorkshopMessage[] messages, Func<string, CancellationToken, Task> clause,
            CancellationToken token, string? instructions = null)
        {
            foreach (var part in new[] { "First clause.", " Second clause.", " Third clause." }) { await clause(part, token); }
            return new("First clause. Second clause. Third clause.", false);
        }
    }

    private sealed class StreamingDialogue : IMobileWandQuestionProvider
    {
        private int calls;
        internal string? ObservedInstructions;
        internal TaskCompletionSource Superseded { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource SecondStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource SecondCanceled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<MobileWandDialogueDraft> ReplyStreamingAsync(CharacterConversationSnapshot profile,
            MobileWandWorkshopMessage[] messages, Func<string, CancellationToken, Task> clause,
            CancellationToken token, string? instructions = null)
        {
            ObservedInstructions = instructions;
            if (Interlocked.Increment(ref calls) == 1)
            {
                await clause("Tell me your story.", token);
                try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
                catch (OperationCanceledException) { Superseded.TrySetResult(); throw; }
                throw new InvalidOperationException("Unreachable pending stream.");
            }
            SecondStarted.TrySetResult();
            await clause("A fresh answer.", token);
            try { await Release.Task.WaitAsync(token); }
            catch (OperationCanceledException) { SecondCanceled.TrySetResult(); throw; }
            return new("A fresh answer.", false);
        }
    }

    private static void Check(bool condition, string description)
    {
        if (!condition) { throw new InvalidOperationException(description); }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "elevenlabs-workshop-" + Guid.NewGuid().ToString("N"));
        internal WandGenerationStore Wands { get; }
        internal TestSocket Socket { get; } = new();
        internal FakeSpeech Speech { get; } = new();
        internal FakeDialogue Dialogue { get; } = new();
        internal ElevenLabsWorkshopRuntime Runtime { get; }
        internal MobileWandAdmission Admission { get; }
        internal Fixture(IMobileWandQuestionProvider? dialogue = null)
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["Backend:DatabasePath"] = Path.Combine(root, "wands.sqlite") }).Build();
            Wands = new(configuration, TimeProvider.System);
            var profile = new CharacterConversationSnapshot("profile", 1, new("Raven", "Synthetic"),
                new("Raven", "Synthetic", "A prepared fixture", [new("Finds maps", ["https://example.test"])],
                    [], [], false, [], "synthetic", "test", 1, 1, "synthetic"), 1);
            Wands.BeginMobile("owner", "operation", profile, 0);
            Wands.BeginWorkshop("owner", "operation", profile, 0, "group", 1);
            Admission = new("owner", "operation", "session", "stamp", "https://localhost", DateTimeOffset.UtcNow.AddSeconds(30), profile)
            { ElevenLabsConsent = true };
            Runtime = new(Wands, Speech, dialogue ?? Dialogue, NullLogger<ElevenLabsWorkshopRuntime>.Instance);
        }
        public void Dispose()
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class FakeDialogue : IMobileWandQuestionProvider
    {
        internal TaskCompletionSource Deciding { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<MobileWandDialogueDraft> ReplyAsync(CharacterConversationSnapshot profile,
            MobileWandWorkshopMessage[] messages, CancellationToken token)
        {
            if (!messages.Any(message => message.Role == "user")) { return new("Which map did you lose?", true); }
            Deciding.TrySetResult();
            await Release.Task.WaitAsync(token);
            return new("I will make your wand.", true);
        }
    }

    private sealed class FakeSpeech : IWorkshopSpeechProvider
    {
        internal FakeRecognition Recognition { get; } = new();
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Canceled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Connections;
        internal int Captures;
        internal int Calls;
        internal int CanceledCalls;
        internal bool Block = true;
        internal ConcurrentQueue<string> Contexts { get; } = new();
        public IWorkshopSpeechProvider Capture() { Captures++; return this; }
        public Task<IWorkshopRecognitionConnection> ConnectAsync(CancellationToken token)
        { Connections++; return Task.FromResult<IWorkshopRecognitionConnection>(Recognition); }
        public Task SpeakAsync(string text, Func<byte[], CancellationToken, Task> audio, CancellationToken token) =>
            SpeakAsync(text, audio, token, null);
        public async Task SpeakAsync(string text, Func<byte[], CancellationToken, Task> audio, CancellationToken token,
            string? previousText)
        {
            Contexts.Enqueue(previousText ?? "");
            Calls++;
            await audio([1, 2], token);
            Started.TrySetResult();
            try { if (Block) { await Task.Delay(Timeout.InfiniteTimeSpan, token); } }
            finally
            {
                if (token.IsCancellationRequested) Interlocked.Increment(ref CanceledCalls);
                Canceled.TrySetResult();
            }
        }
    }

    private sealed class FakeRecognition : IWorkshopRecognitionConnection
    {
        internal Channel<WorkshopRecognition> Events { get; } = Channel.CreateUnbounded<WorkshopRecognition>();
        internal TaskCompletionSource AudioReceived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task SendAudioAsync(byte[] pcm, CancellationToken token) { AudioReceived.TrySetResult(); return Task.CompletedTask; }
        public async Task<WorkshopRecognition?> ReceiveAsync(CancellationToken token) => await Events.Reader.ReadAsync(token);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class TestSocket : WebSocket
    {
        private readonly Channel<(WebSocketMessageType Type, byte[] Bytes)> controls = Channel.CreateUnbounded<(WebSocketMessageType, byte[])>();
        internal ConcurrentQueue<string> Events { get; } = new();
        internal void Control(string type) => controls.Writer.TryWrite((WebSocketMessageType.Text, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { type }))));
        internal void Audio(byte[] bytes) => controls.Writer.TryWrite((WebSocketMessageType.Binary, bytes));
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => WebSocketState.Open;
        public override string? SubProtocol => null;
        public override void Abort() { }
        public override void Dispose() { }
        public override Task CloseAsync(WebSocketCloseStatus status, string? description, CancellationToken token) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus status, string? description, CancellationToken token) => Task.CompletedTask;
        public override Task SendAsync(ArraySegment<byte> bytes, WebSocketMessageType type, bool endOfMessage, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (type == WebSocketMessageType.Text) { Events.Enqueue(Encoding.UTF8.GetString(bytes)); }
            return Task.CompletedTask;
        }
        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> bytes, CancellationToken token)
        {
            var control = await controls.Reader.ReadAsync(token);
            control.Bytes.AsSpan().CopyTo(bytes.AsSpan());
            return new(control.Bytes.Length, control.Type, true);
        }
    }
}
