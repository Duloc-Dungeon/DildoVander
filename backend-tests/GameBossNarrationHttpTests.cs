using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Kufr.Backend.Features.Game;
using Kufr.Backend.Features.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

internal static partial class MobileGameTests
{
    internal static async Task RunBossNarrationHttpAsync()
    {
        var narrator = new BossHttpNarrator();
        var speech = new BossHttpSpeech();
        await using var fixture = await HttpFixture.StartAsync(withKeepsakes: true,
            bossNarrator: narrator, bossSpeech: speech);
        using var alice = fixture.Client();
        using var foreign = fixture.Client();
        using var anonymous = fixture.Client();
        await fixture.LoginAsync(alice, fixture.AliceId);
        await fixture.LoginAsync(foreign, fixture.ForeignId);
        var id = await fixture.CreateBossAttemptAsync();
        var path = $"/app/v1/game/boss/attempts/{id}";
        using var snapshotResponse = await alice.GetAsync(path);
        var state = await snapshotResponse.Content.ReadFromJsonAsync<JsonElement>();
        var revision = state.GetProperty("revision").GetInt64();
        using var unauthenticated = await anonymous.PostAsJsonAsync(path + "/narration", new { expectedRevision = revision });
        Check(unauthenticated.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden,
            "Baron narration requires authenticated account and CSRF");
        using var missingCsrf = await alice.PostAsJsonAsync(path + "/narration", new { expectedRevision = revision });
        Check(missingCsrf.StatusCode == HttpStatusCode.BadRequest
            && (await missingCsrf.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString() == "csrf_required",
            "Baron generation retains CSRF protection");
        alice.DefaultRequestHeaders.Add("X-DV-CSRF", await fixture.CsrfAsync(alice));
        foreign.DefaultRequestHeaders.Add("X-DV-CSRF", await fixture.CsrfAsync(foreign));
        foreach (var suffix in new[] { "/narration", "/audio" })
        {
            using var denied = await foreign.PostAsJsonAsync(path + suffix, new { expectedRevision = revision });
            Check(denied.StatusCode == HttpStatusCode.NotFound, "foreign group cannot access Baron text/audio");
        }
        Check(narrator.Calls == 0 && speech.Calls == 0, "denied requests cannot bill either provider");
        await fixture.RemoveWandAsync();
        using var reply = await alice.PostAsJsonAsync(path + "/narration", new { expectedRevision = revision });
        Check(reply.StatusCode == HttpStatusCode.OK, "boss narration accepts the no-wand rescue path");
        var text = await reply.Content.ReadFromJsonAsync<JsonElement>();
        Check(text.GetProperty("revision").GetInt64() == revision
            && text.GetProperty("text").GetString() == "Zkušební replika Barona."
            && reply.Headers.CacheControl?.NoStore == true,
            "HTTP text response is revision-bound and never publicly cached");
        using var audio = await alice.PostAsJsonAsync(path + "/audio", new { expectedRevision = revision });
        Check(audio.StatusCode == HttpStatusCode.OK && audio.Content.Headers.ContentType?.MediaType == "audio/mpeg"
            && (await audio.Content.ReadAsByteArrayAsync()).SequenceEqual(new byte[] { 1, 2, 3 }),
            "HTTP audio voices the owned reply after wand removal");
        using var replay = await alice.PostAsJsonAsync(path + "/audio", new { expectedRevision = revision });
        Check(replay.StatusCode == HttpStatusCode.OK && speech.Calls == 1 && narrator.Calls == 1,
            "HTTP replay uses durable text/audio caches");
        using var choose = await alice.PostAsJsonAsync(path + "/choose", new
        { operationId = Guid.NewGuid().ToString(), expectedRevision = revision, choice = "strike" });
        Check(choose.StatusCode == HttpStatusCode.OK, "narration does not block the next gameplay move");
        revision = (await choose.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("revision").GetInt64();
        narrator.Delay = true;
        var pending = alice.PostAsJsonAsync(path + "/narration", new { expectedRevision = revision });
        await narrator.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await fixture.RevokeAsync(fixture.AliceId);
        narrator.Release.SetResult();
        using var revoked = await pending;
        Check(revoked.StatusCode == HttpStatusCode.Forbidden,
            "membership revoked during GPT I/O prevents publishing/returning the reply");
        Console.WriteLine("Baron HTTP auth, CSRF, ownership, no-wand, cache and mid-generation revocation passed.");
        await RunBossConsentFencesHttpAsync();
    }

    private static async Task RunBossConsentFencesHttpAsync()
    {
        var narrator = new BossHttpNarrator { Delay = true };
        var speech = new BossHttpSpeech();
        await using var fixture = await HttpFixture.StartAsync(withKeepsakes: true, bossNarrator: narrator, bossSpeech: speech);
        using var client = fixture.Client();
        await fixture.LoginAsync(client, fixture.AliceId);
        client.DefaultRequestHeaders.Add("X-DV-CSRF", await fixture.CsrfAsync(client));
        var id = await fixture.CreateBossAttemptAsync();
        var path = $"/app/v1/game/boss/attempts/{id}";
        var state = await (await client.GetAsync(path)).Content.ReadFromJsonAsync<JsonElement>();
        var revision = state.GetProperty("revision").GetInt64();
        await ConsentAsync(1);
        foreach (var suffix in new[] { "/narration", "/audio" })
        {
            using var denied = await client.PostAsJsonAsync(path + suffix, new { expectedRevision = revision });
            Check(denied.StatusCode == HttpStatusCode.Conflict
                && (await denied.Content.ReadAsStringAsync()).Contains("ai_consent_required"),
                "legacy v1 evidence cannot authorize either Baron provider");
        }
        Check(narrator.Calls == 0 && speech.Calls == 0, "old consent is rejected before any provider call");
        await ConsentAsync(AccountAiConsent.CurrentVersion);
        var pendingText = client.PostAsJsonAsync(path + "/narration", new { expectedRevision = revision });
        await narrator.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await ConsentAsync(1);
        narrator.Release.TrySetResult();
        using var staleText = await pendingText;
        Check(staleText.StatusCode == HttpStatusCode.Conflict
            && fixture.BossStore.ReadBossNarration(fixture.ActiveGroupId, fixture.AliceId, id, revision, narrator.LastInput!.Key)?.Text is null,
            "consent downgraded during GPT I/O prevents publication");
        await ConsentAsync(AccountAiConsent.CurrentVersion);
        narrator.Delay = false;
        Check((await client.PostAsJsonAsync(path + "/narration", new { expectedRevision = revision })).IsSuccessStatusCode,
            "current unified consent permits Baron narration");
        speech.Delay = true;
        var pendingAudio = client.PostAsJsonAsync(path + "/audio", new { expectedRevision = revision });
        await speech.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await ConsentAsync(1);
        speech.Release.TrySetResult();
        using var staleAudio = await pendingAudio;
        Check(staleAudio.StatusCode == HttpStatusCode.Conflict
            && fixture.BossStore.ReadBossNarration(fixture.ActiveGroupId, fixture.AliceId, id, revision, narrator.LastInput!.Key)?.Audio is null,
            "consent downgraded during ElevenLabs I/O prevents audio publication");
        await ConsentAsync(AccountAiConsent.CurrentVersion);
        speech.Delay = false;
        Check((await client.PostAsJsonAsync(path + "/audio", new { expectedRevision = revision })).IsSuccessStatusCode,
            "current unified consent permits Baron speech");
        var publishedKey = speech.CacheKey;
        speech.VoiceKey = "new-http-voice:eleven_v4:delivery-v1";
        speech.Delay = true;
        speech.Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        speech.Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var revokedAudio = client.PostAsJsonAsync(path + "/audio", new { expectedRevision = revision });
        await speech.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        using (var scope = fixture.ChronicleServices.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<AccountDatabase>().Sessions.Where(session => session.UserId == fixture.AliceId)
                .ExecuteUpdateAsync(update => update.SetProperty(session => session.ExpiresAt, 1));
        }
        speech.Release.TrySetResult();
        using var revoked = await revokedAudio;
        Check(revoked.StatusCode == HttpStatusCode.Unauthorized
            && fixture.BossStore.ReadBossNarration(fixture.ActiveGroupId, fixture.AliceId, id, revision, narrator.LastInput!.Key)?.AudioKey == publishedKey,
            "an account session revoked during speech cannot publish under a new audio key");
        Console.WriteLine("Baron unified-consent v1/v2, mid-GPT/TTS downgrade and active-session publication fences passed (offline).");

        async Task ConsentAsync(int version)
        {
            using var scope = fixture.ChronicleServices.CreateScope();
            await scope.ServiceProvider.GetRequiredService<AccountDatabase>().Users.Where(user => user.Id == fixture.AliceId)
                .ExecuteUpdateAsync(update => update.SetProperty(user => user.OpenAiConsentVersion, version));
        }
    }

    private sealed class BossHttpNarrator : IGameBossNarrator
    {
        internal int Calls;
        internal bool Delay;
        internal GameBossNarrationInput? LastInput;
        internal TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<string> NarrateBossAsync(GameBossNarrationInput input, CancellationToken token)
        {
            Calls++;
            LastInput = input;
            if (Delay) { Entered.SetResult(); await Release.Task.WaitAsync(token); }
            return "Zkušební replika Barona.";
        }
    }

    private sealed class BossHttpSpeech : IGameBossSpeech
    {
        public bool Enabled => true;
        internal string VoiceKey = "http-voice:eleven_v4:delivery-v1";
        public string CacheKey => VoiceKey;
        internal int Calls;
        internal bool Delay;
        internal TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<byte[]> SpeakAsync(string text, CancellationToken token)
        {
            Calls++;
            if (Delay) { Entered.TrySetResult(); await Release.Task.WaitAsync(token); }
            return [1, 2, 3];
        }
    }
}
