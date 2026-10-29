using System.Net;
using System.Text;
using System.Text.Json;
using Kufr.Backend.Features.Game;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

internal static class GameBossNarrationTests
{
    internal static async Task RunAsync()
    {
        await CacheAndContextAsync();
        await StaleAndCancelledAsync();
        await AccessAndClaimsAsync();
        await DeliveryAsync();
        await SpeechTransportAsync();
        await CapturedVoiceCacheAsync();
        Console.WriteLine("Baron narration, context, leases and ElevenLabs transport passed.");
    }

    private static async Task CacheAndContextAsync()
    {
        using var fixture = new Fixture();
        var narrator = new Narrator();
        var speech = new Speech();
        var service = fixture.Service(narrator, speech);
        await FaultAsync(() => service.AudioAsync("g", "player", fixture.Id, 1, fixture.InputAsync, default),
            "boss_narration_not_ready");
        var first = await fixture.TextAsync(service);
        Check(first.Text == "Baronova replika." && first.VoiceAvailable && narrator.Calls == 1,
            "text works independently of audio");
        var second = await fixture.TextAsync(fixture.Service(narrator, speech));
        Check(second == first && narrator.Calls == 1, "reconnect/server service restart reuses durable text");
        var audio = await service.AudioAsync("g", "player", fixture.Id, 1, fixture.InputAsync, default);
        var replay = await service.AudioAsync("g", "player", fixture.Id, 1, fixture.InputAsync, default);
        Check(audio.SequenceEqual(replay) && speech.Calls == 1 && speech.Text == "[haughty] " + first.Text,
            "audio voices the server-owned words with authored delivery once, while display text stays clean");
        var changedVoice = new Speech { CacheKey = "different-voice:eleven_v4:delivery-v1" };
        var changedVoiceService = fixture.Service(narrator, changedVoice);
        await changedVoiceService.AudioAsync("g", "player", fixture.Id, 1, fixture.InputAsync, default);
        Check(changedVoice.Calls == 1 && narrator.Calls == 1 && changedVoice.Text == speech.Text,
            "an open page can request a new voice without regenerating or changing its displayed words");
        Check((await fixture.TextAsync(changedVoiceService)).Text == first.Text && narrator.Calls == 1,
            "another client sees the same immutable words after a voice change");
        using (var legacy = new SqliteConnection($"Data Source={Path.Combine(fixture.Root, "mobile-game.sqlite")}"))
        {
            legacy.Open();
            using var drop = legacy.CreateCommand();
            drop.CommandText = "ALTER TABLE game_boss_narration DROP COLUMN audio_key";
            drop.ExecuteNonQuery();
        }
        await changedVoiceService.AudioAsync("g", "player", fixture.Id, 1, fixture.InputAsync, default);
        Check(changedVoice.Calls == 2 && narrator.Calls == 1,
            "old cache schema upgrades in place and refreshes only audio without a known rendering identity");
        var input = await fixture.InputAsync(default);
        using var request = JsonDocument.Parse(OpenAiGameYoloProvider.BuildBossRequest(input));
        var payload = request.RootElement.GetProperty("input").GetString()!;
        using var profilePayload = JsonDocument.Parse(payload);
        var player = profilePayload.RootElement.GetProperty("player");
        Check(player.GetProperty("Name").GetString() == "Sherlock"
            && player.GetProperty("Universe").GetString() == "Baker Street"
            && player.GetProperty("WandName").GetString() == "Hůlka",
            "prompt includes fictional identity and wand");
        Check(!payload.Contains("private-account-id") && !payload.Contains("Seed", StringComparison.OrdinalIgnoreCase)
            && !payload.Contains("BossChoice") && !payload.Contains(fixture.Id),
            "prompt excludes account/attempt IDs, random seed and committed move");
        fixture.Advance();
        var next = await fixture.InputAsync(default);
        using var facts = JsonDocument.Parse(next.ToJson());
        var duel = facts.RootElement.GetProperty("duel");
        Check(duel.GetProperty("Rounds").GetArrayLength() == 1
            && duel.GetProperty("CompletedRounds").GetInt32() == 1,
            "context includes real completed rounds and current energy");
        await fixture.TextAsync(service);
        Check(narrator.Calls == 2 && fixture.Store.ReadBossAttempt("g", "player", fixture.Id).CompletedRounds == 1,
            "new revision gets new narration without modifying the duel");
        fixture.Store.DeleteAccount("player");
        using var connection = new SqliteConnection($"Data Source={Path.Combine(fixture.Root, "mobile-game.sqlite")}");
        connection.Open();
        using var query = connection.CreateCommand();
        query.CommandText = "SELECT COUNT(*) FROM game_boss_narration";
        Check(Convert.ToInt32(query.ExecuteScalar()) == 0, "account deletion removes text and audio via cascade");
    }

    private static async Task StaleAndCancelledAsync()
    {
        using var fixture = new Fixture();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var result = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var narrator = new Narrator { Generate = _ => { entered.SetResult(); return result.Task; } };
        var service = fixture.Service(narrator, new Speech());
        var pending = fixture.TextAsync(service);
        await entered.Task;
        await FaultAsync(() => fixture.TextAsync(service), "boss_narration_busy");
        fixture.Advance();
        result.SetResult("Pozdní replika.");
        await FaultAsync(() => pending, "boss_narration_revision_conflict");
        var current = await fixture.InputAsync(default);
        Check(fixture.Store.ReadBossNarration("g", "player", fixture.Id, fixture.Revision, current.Key) is null,
            "late old reply cannot publish into a new round");

        narrator.Generate = _ => Task.FromResult("Aktuální replika.");
        await fixture.TextAsync(service);
        var changed = false;
        var newContextService = fixture.Service(new Narrator { Generate = _ =>
        {
            changed = true;
            return Task.FromResult("Zastaralá postava.");
        } }, new Speech());
        // A changed profile at the same gameplay revision invalidates both caches.
        async Task<GameBossNarrationInput> ReadChangedAsync(CancellationToken token)
        {
            var input = await fixture.InputAsync(token);
            return input with { Player = input.Player with { Name = changed ? "Watson" : "Moriarty" } };
        }
        await FaultAsync(() => newContextService.NarrateAsync("g", "player", fixture.Id, fixture.Revision,
            ReadChangedAsync, default), "boss_narration_revision_conflict");

        using var cancellation = new CancellationTokenSource();
        var cancelNarrator = new Narrator { Generate = async token =>
        {
            cancellation.Cancel();
            await Task.Delay(Timeout.Infinite, token);
            return "Never";
        } };
        await RequireCancelledAsync(() => fixture.TextAsync(fixture.Service(cancelNarrator, new Speech()), cancellation.Token));
        var recovered = await fixture.TextAsync(fixture.Service(new Narrator(), new Speech()));
        Check(recovered.Text.Length > 0, "cancelled request releases its durable lease for retry");

        var audioEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var audioResult = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var slowSpeech = new Speech { Generate = _ => { audioEntered.SetResult(); return audioResult.Task; } };
        var audioService = fixture.Service(new Narrator(), slowSpeech);
        var pendingAudio = audioService.AudioAsync("g", "player", fixture.Id, fixture.Revision, fixture.InputAsync, default);
        await audioEntered.Task;
        fixture.Store.CancelBoss("g", "player", fixture.Id, new(Guid.NewGuid().ToString(), fixture.Revision));
        audioResult.SetResult([1, 2, 3]);
        await FaultAsync(() => pendingAudio, "boss_narration_revision_conflict");
    }

    private static async Task AccessAndClaimsAsync()
    {
        using var fixture = new Fixture();
        var input = await fixture.InputAsync(default);
        var key = input.Key;
        var claim = fixture.Store.ClaimBossNarration("g", "player", fixture.Id, 1, key)!;
        var anotherStore = new GameStore(fixture.Root);
        Check(anotherStore.ClaimBossNarration("g", "player", fixture.Id, 1, key) is null,
            "SQLite lease deduplicates across store instances");
        fixture.Store.ReleaseBossNarration(fixture.Id, "wrong-token");
        Check(anotherStore.ClaimBossNarration("g", "player", fixture.Id, 1, key) is null,
            "another worker cannot release the lease");
        fixture.Store.ReleaseBossNarration(fixture.Id, claim);
        var newClaim = anotherStore.ClaimBossNarration("g", "player", fixture.Id, 1, key)!;
        await FaultAsync(() => Task.Run(() => fixture.Store.PublishBossNarration("g", "player", fixture.Id,
            1, key, claim, "Old", null)), "boss_narration_revision_conflict");
        anotherStore.ReleaseBossNarration(fixture.Id, newClaim);
        var narrator = new Narrator();
        var service = fixture.Service(narrator, new Speech());
        await FaultAsync(() => service.NarrateAsync("other", "player", fixture.Id, 1, fixture.InputAsync, default),
            "boss_attempt_unavailable");
        await FaultAsync(() => service.NarrateAsync("g", "other", fixture.Id, 1, fixture.InputAsync, default),
            "boss_attempt_unavailable");
        await FaultAsync(() => service.NarrateAsync("g", "player", fixture.Id, 0, fixture.InputAsync, default),
            "boss_narration_revision_invalid");
        Check(narrator.Calls == 0, "bad owner/group/revision never invokes a provider");
        var noVoice = fixture.Service(narrator, new Speech { Enabled = false });
        Check(!(await fixture.TextAsync(noVoice)).VoiceAvailable, "missing ElevenLabs still permits GPT text");
    }

    private static async Task DeliveryAsync()
    {
        using var fixture = new Fixture();
        var input = await fixture.InputAsync(default);
        var start = input.Match;
        Check(GameBossDelivery.For(start).Mood == GameBossMood.Arrogant, "Baron begins arrogant");
        var hit = new GameBossRound(1, "strike", "trick", "player", 0, 0, 1, 0);
        var angry = start with { CompletedRounds = 1, OpponentEnergy = 4, Rounds = [hit] };
        Check(GameBossDelivery.For(angry).Speak("To bylo drahé.") == "[angry] To bylo drahé.",
            "a real player hit angers the Baron");
        Check(GameBossDelivery.For(angry with { OpponentEnergy = 1 }).Mood == GameBossMood.Desperate,
            "imminent defeat overrides anger");
        var draw = start with { Rounds = [hit with { Winner = "draw", PlayerDamage = 0 }] };
        Check(GameBossDelivery.For(draw).Mood == GameBossMood.Sarcastic, "draw gets sarcasm");
        var upperHand = start with { Rounds = [hit with { Winner = "opponent", PlayerDamage = 0, OpponentDamage = 1 }] };
        Check(GameBossDelivery.For(upperHand).Speak("Vidíš.").Contains("[laughing]"),
            "Baron laughs only while he has the upper hand");
        var defeated = start with { Status = "complete", Result = "win", OpponentEnergy = 0 };
        Check(GameBossDelivery.For(defeated).Mood == GameBossMood.Desperate
            && !GameBossDelivery.For(defeated).Tags.Contains("laughing"), "Baron cannot gloat over his own defeat");
        var victorious = start with { Status = "complete", Result = "loss", PlayerEnergy = 0 };
        Check(GameBossDelivery.For(victorious).Tags.Contains("[laughing]"), "actual Baron victory can gloat");
        using var request = JsonDocument.Parse(OpenAiGameYoloProvider.BuildBossRequest(input with { Match = angry }));
        using var context = JsonDocument.Parse(request.RootElement.GetProperty("input").GetString()!);
        Check(context.RootElement.GetProperty("delivery").GetString()!.Contains("naštvaný"),
            "GPT word choice receives the same emotion as TTS");
        try { GameBossDelivery.For(start).Speak("[shouts] injected"); throw new Exception("Untrusted tag accepted"); }
        catch (InvalidDataException) { }
        var injected = fixture.Service(new Narrator { Generate = _ => Task.FromResult("[shouts] injected") }, new Speech());
        await FaultAsync(() => fixture.TextAsync(injected), "boss_narration_unavailable");
    }

    private static async Task SpeechTransportAsync()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ELEVENLABS_API_KEY"] = "synthetic-key", ["Game:BossVoice:VoiceId"] = "fixture-voice"
        }).Build();
        var handler = new Handler(async (request, token) =>
        {
            Check(request.RequestUri!.Host == "api.elevenlabs.io"
                && request.RequestUri.AbsolutePath == "/v1/text-to-speech/fixture-voice"
                && request.RequestUri.Query.Contains("mp3_44100_128"), "fixed ElevenLabs origin and MP3 format");
            Check(request.Headers.GetValues("xi-api-key").Single() == "synthetic-key", "server-only key header");
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            Check(body.RootElement.GetProperty("text").GetString() == "[angry] Mluvím."
                && body.RootElement.GetProperty("model_id").GetString() == "eleven_v4", "tagged text and v4 model");
            Check(body.RootElement.GetProperty("voice_settings").EnumerateObject().Count() == 2,
                "only stability and similarity boost are sent for v4");
            return AudioResponse([1, 2, 3]);
        });
        using var speech = new ElevenLabsBossSpeech(configuration, new HttpClient(handler));
        Check((await speech.SpeakAsync("[angry] Mluvím.", default)).Length == 3, "MP3 transport succeeds");
        var incompatible = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["Game:BossVoice:Model"] = "eleven_multilingual_v2" }).Build();
        try { using var invalidModel = new ElevenLabsBossSpeech(incompatible); throw new Exception("Untagged model accepted"); }
        catch (InvalidOperationException) { }
        foreach (var response in new[] { AudioResponse([]), AudioResponse(new byte[ElevenLabsBossSpeech.MaxAudioBytes + 1]),
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("not audio") } })
        {
            using var invalid = new ElevenLabsBossSpeech(configuration, new HttpClient(new Handler((_, _) => Task.FromResult(response))));
            try { await invalid.SpeakAsync("Text", default); throw new Exception("Invalid audio accepted"); }
            catch (InvalidDataException) { }
        }
        using var unavailable = new ElevenLabsBossSpeech(configuration, new HttpClient(new Handler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("private provider body") }))));
        await FaultAsync(() => unavailable.SpeakAsync("Text", default), "boss_voice_unavailable");
    }

    private static async Task CapturedVoiceCacheAsync()
    {
        using var fixture = new Fixture();
        var speech = new ChangingSpeech();
        var narrator = new Narrator();
        var service = fixture.Service(narrator, speech);
        await fixture.TextAsync(service);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var audio = service.AudioAsync("g", "player", fixture.Id, 1, fixture.InputAsync, timeout.Token);
        await speech.Started.Task.WaitAsync(timeout.Token);
        speech.Voice = "VoiceTwo";
        speech.Release.TrySetResult();
        Check((await audio).SequenceEqual(new byte[] { 1 }), "in-flight Baron audio retains its captured voice");
        var input = await fixture.InputAsync(default);
        var cached = fixture.Store.ReadBossNarration("g", "player", fixture.Id, 1, input.Key);
        Check(cached?.AudioKey == "VoiceOne:eleven_v4:delivery-v1", "audio is published under the same captured key used for synthesis");
        var refreshed = await service.AudioAsync("g", "player", fixture.Id, 1, fixture.InputAsync, timeout.Token);
        Check(refreshed.SequenceEqual(new byte[] { 2 }) && speech.Calls == 2 && narrator.Calls == 1,
            "the next Baron request refreshes only audio for the new voice");
        await service.AudioAsync("g", "player", fixture.Id, 1, fixture.InputAsync, timeout.Token);
        Check(speech.Calls == 2, "subsequent requests reuse the correctly labeled new-voice cache");
    }

    private sealed class ChangingSpeech : IGameBossSpeech
    {
        internal string Voice = "VoiceOne";
        internal int Calls;
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Enabled => true;
        public string CacheKey => Voice + ":eleven_v4:delivery-v1";
        public IGameBossSpeech Capture() => new Bound(this, Voice);
        public Task<byte[]> SpeakAsync(string text, CancellationToken token) => throw new InvalidOperationException("Uncaptured Baron voice used");
        private sealed class Bound(ChangingSpeech parent, string voice) : IGameBossSpeech
        {
            public bool Enabled => true;
            public string CacheKey => voice + ":eleven_v4:delivery-v1";
            public async Task<byte[]> SpeakAsync(string text, CancellationToken token)
            {
                if (Interlocked.Increment(ref parent.Calls) == 1)
                {
                    parent.Started.TrySetResult();
                    await parent.Release.Task.WaitAsync(token);
                }
                return voice == "VoiceOne" ? [1] : [2];
            }
        }
    }

    private static HttpResponseMessage AudioResponse(byte[] bytes)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        response.Content.Headers.ContentType = new("audio/mpeg");
        return response;
    }

    private static async Task FaultAsync(Func<Task> run, string code)
    {
        try { await run(); throw new Exception($"Expected {code}"); }
        catch (GameFault fault) { Check(fault.Code == code, $"Expected {code}, got {fault.Code}"); }
    }

    private static async Task RequireCancelledAsync(Func<Task> run)
    {
        try { await run(); throw new Exception("Expected cancellation"); }
        catch (OperationCanceledException) { }
    }

    private static void Check(bool valid, string message)
    { if (!valid) { throw new InvalidOperationException(message); } }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token);
    }

    private sealed class Narrator : IGameBossNarrator
    {
        internal int Calls;
        internal Func<CancellationToken, Task<string>> Generate = _ => Task.FromResult("Baronova replika.");
        public Task<string> NarrateBossAsync(GameBossNarrationInput input, CancellationToken token)
        { Calls++; return Generate(token); }
    }

    private sealed class Speech : IGameBossSpeech
    {
        public bool Enabled { get; init; } = true;
        public string CacheKey { get; init; } = "fixture-voice:eleven_v4:delivery-v1";
        internal int Calls;
        internal string? Text;
        internal Func<CancellationToken, Task<byte[]>> Generate = _ => Task.FromResult<byte[]>([1, 2, 3]);
        public Task<byte[]> SpeakAsync(string text, CancellationToken token)
        { Calls++; Text = text; return Generate(token); }
    }

    private sealed class Fixture : IDisposable
    {
        internal string Root = Path.Combine(Path.GetTempPath(), "boss-narration-" + Guid.NewGuid().ToString("N"));
        internal GameStore Store;
        internal string Id;
        internal long Revision => Store.ReadBossAttempt("g", "player", Id).Revision;
        internal Fixture()
        {
            Store = new GameStore(Root);
            using var connection = new SqliteConnection($"Data Source={Path.Combine(Root, "mobile-game.sqlite")}");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO game_players(group_id,user_id,xp,matches) VALUES('g','player',30,4)";
            command.ExecuteNonQuery();
            var profile = Store.RevealBoss("g", "admin", new HashSet<string> { "player" }, new(Guid.NewGuid().ToString(), 0));
            Id = Store.StartBoss("g", "player", new(Guid.NewGuid().ToString(), profile.Revision)).Id;
        }
        internal Task<GameBossNarrationInput> InputAsync(CancellationToken token) => Task.FromResult(new GameBossNarrationInput(
            Store.ReadBossAttempt("g", "player", Id), new("private-account-id", "Sherlock", "Baker Street", "Hůlka", ["Detektiv"]),
            Store.ReadBoss("g").Rescued));
        internal GameBossNarration Service(Narrator narrator, IGameBossSpeech speech) => new(Store, narrator, speech, NullLogger<GameBossNarration>.Instance);
        internal Task<GameBossNarrationView> TextAsync(GameBossNarration service, CancellationToken token = default) =>
            service.NarrateAsync("g", "player", Id, Revision, InputAsync, token);
        internal void Advance() => Store.ChooseBoss("g", "player", Id, new(Guid.NewGuid().ToString(), Revision, "strike"));
        public void Dispose() { SqliteConnection.ClearAllPools(); Directory.Delete(Root, true); }
    }
}
