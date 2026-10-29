using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Kufr.Backend.Features.Game;
using Kufr.Backend.Features.Identity;
using Kufr.Backend.Features.Voices;
using Kufr.Backend.Features.Wands;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

internal static class ActorVoiceSettingsTests
{
    internal static async Task RunAsync()
    {
        await DurableSettingsAsync();
        await CapturedProviderVoicesAsync();
        await AccountIdentityTests.RunActorVoicesHttpAsync();
        Console.WriteLine("Actor voice settings: durable CAS, corruption fences, role/session authorization and stable provider captures passed (offline).");
    }

    private static async Task DurableSettingsAsync()
    {
        using var fixture = new VoiceFixture();
        var first = fixture.Store.Snapshot();
        Check(first == new ActorVoiceSnapshot(0, "ConfiguredKufr", "ConfiguredBaron"), "explicit config defaults remain independent");
        var saved = fixture.Store.Save(new(0, "SelectedKufr", "ConfiguredBaron"));
        var restarted = fixture.NewStore();
        Check(restarted.Snapshot() == saved, "persisted choices survive a fresh store instance");
        var next = restarted.Save(new(saved.Revision, saved.KufrVoiceId, "SelectedBaron"));
        Check(fixture.Store.Snapshot() == next && next.KufrVoiceId == "SelectedKufr", "Baron updates do not change Kufr and existing instances reread durable state");
        Expect("actor_voice_settings_conflict", () => fixture.Store.Save(new(0, "StaleKufr", "StaleBaron")));
        foreach (var invalid in new[] { "", "../escape", "voice_with_underscore", "voice-id", "žluťoučký", new string('a', 101) })
        {
            Expect("actor_voice_settings_invalid", () => fixture.Store.Save(new(next.Revision, invalid, "ValidBaron")));
            Expect("actor_voice_settings_invalid", () => fixture.Store.Save(new(next.Revision, "ValidKufr", invalid)));
        }
        var successes = 0;
        var conflicts = 0;
        await Task.WhenAll(new[] { fixture.Store, restarted }.Select((store, index) => Task.Run(() =>
        {
            try { store.Save(new(next.Revision, "Concurrent" + index, "SelectedBaron")); Interlocked.Increment(ref successes); }
            catch (ActorVoiceSettingsException error) when (error.Code == "actor_voice_settings_conflict") { Interlocked.Increment(ref conflicts); }
        })));
        Check(successes == 1 && conflicts == 1 && Directory.GetFiles(fixture.Root, "*.tmp").Length == 0,
            "separate store instances cannot both commit the same revision and leave no temporary artifacts");
        var path = Path.Combine(fixture.Root, "actor-voices.json");
        var corrupt = "{\"revision\":4,\"kufrVoiceId\":\"Ok\",\"baronVoiceId\":\"Bad/Voice\"}";
        File.WriteAllText(path, corrupt);
        Expect("actor_voice_settings_corrupt", () => fixture.NewStore().Snapshot());
        Expect("actor_voice_settings_corrupt", () => fixture.Store.Save(new(4, "GoodKufr", "GoodBaron")));
        Check(File.ReadAllText(path) == corrupt, "corrupt settings fail closed without being reset or overwritten");
        var noOverrides = new ConfigurationBuilder().Build();
        var defaults = new ActorVoiceSettings(noOverrides, new(false, "", "", "", [])).Snapshot();
        Check(defaults.KufrVoiceId == "U48DQ1c9SVmD2BVCSiHL" && defaults.BaronVoiceId == defaults.KufrVoiceId,
            "both unconfigured actors default to the explicitly requested voice");
    }

    private static async Task CapturedProviderVoicesAsync()
    {
        using var fixture = new VoiceFixture();
        var workshopRequests = new ConcurrentQueue<string>();
        var baronRequests = new ConcurrentQueue<string>();
        using var workshop = new ElevenLabsWorkshopProvider(
            ElevenLabsWorkshopOptions.Read(fixture.Configuration), new HttpClient(new VoiceHandler(workshopRequests, pcm: true)), fixture.Store);
        using var baron = new ElevenLabsBossSpeech(fixture.Configuration,
            new HttpClient(new VoiceHandler(baronRequests, pcm: false)), fixture.Store);
        var oldWorkshop = workshop.Capture();
        var oldBaron = baron.Capture();
        var oldKey = oldBaron.CacheKey;
        fixture.Store.Save(new(0, "NewKufr", "NewBaron"));
        await oldWorkshop.SpeakAsync("First clause.", (_, _) => Task.CompletedTask, default);
        await oldWorkshop.SpeakAsync("Second clause.", (_, _) => Task.CompletedTask, default);
        await oldBaron.SpeakAsync("Baron text.", default);
        Check(workshopRequests.All(path => path.Contains("/ConfiguredKufr/", StringComparison.Ordinal))
            && baronRequests.Single().Contains("/ConfiguredBaron?", StringComparison.Ordinal)
            && oldBaron.CacheKey == oldKey, "a saved admin change cannot mutate an in-flight connection or Baron rendering identity");
        await workshop.Capture().SpeakAsync("New reply.", (_, _) => Task.CompletedTask, default);
        var newBaron = baron.Capture();
        await newBaron.SpeakAsync("New Baron text.", default);
        Check(workshopRequests.Last().Contains("/NewKufr/", StringComparison.Ordinal)
            && baronRequests.Last().Contains("/NewBaron?", StringComparison.Ordinal)
            && newBaron.CacheKey != oldKey, "subsequent captures use independently selected voices and a distinct Baron cache key");
    }

    private static void Expect(string code, Action action)
    {
        try { action(); throw new InvalidOperationException("Expected " + code); }
        catch (ActorVoiceSettingsException error) { Check(error.Code == code, "Expected " + code + ", got " + error.Code); }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) { throw new InvalidOperationException(message); }
    }

    private sealed class VoiceHandler(ConcurrentQueue<string> requests, bool pcm) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            requests.Enqueue(request.RequestUri!.PathAndQuery);
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2]) };
            response.Content.Headers.ContentType = new(pcm ? "audio/pcm" : "audio/mpeg");
            return Task.FromResult(response);
        }
    }

    private sealed class VoiceFixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "actor-voice-tests-" + Guid.NewGuid().ToString("N"));
        internal IConfiguration Configuration { get; }
        internal ActorVoiceSettings Store { get; }
        private readonly AccountConfiguration accounts;
        internal VoiceFixture()
        {
            Configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Wands:Voice:VoiceId"] = "ConfiguredKufr", ["Game:BossVoice:VoiceId"] = "ConfiguredBaron",
                ["ELEVENLABS_API_KEY"] = "synthetic-offline-key"
            }).Build();
            accounts = new(true, Root, Path.Combine(Root, "keys"), "localhost", ["https://localhost"]);
            Store = NewStore();
        }
        internal ActorVoiceSettings NewStore() => new(Configuration, accounts);
        public void Dispose()
        {
            var allowed = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(Root).StartsWith(allowed, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            { throw new InvalidOperationException("Actor voice fixture escaped its temporary root."); }
            if (Directory.Exists(Root)) { Directory.Delete(Root, recursive: true); }
        }
    }
}

internal static partial class AccountIdentityTests
{
    internal static async Task RunActorVoicesHttpAsync()
    {
        await using var fixture = await Fixture.StartAsync(
            configureServices: services => services.AddSingleton<ActorVoiceSettings>(),
            configureEndpoints: app => app.MapActorVoiceSettings());
        using var owner = fixture.Client();
        using var anonymous = fixture.Client();
        using var ownerKey = new VirtualPasskey();
        const string path = "/app/v1/admin/actor-voices";
        Check((await anonymous.GetAsync(path)).StatusCode == HttpStatusCode.Unauthorized, "anonymous voice administration is denied");
        await EnrollAsync(owner, ownerKey, Fixture.Bootstrap, "Voice owner");
        var session = await CsrfAsync(owner);
        var ownerId = session.GetProperty("account").GetProperty("id").GetString()!;
        var initial = await JsonAsync(await owner.GetAsync(path));
        var revision = initial.GetProperty("revision").GetInt64();
        var change = new ActorVoiceChange(revision, "OwnerKufr", "OwnerBaron");
        owner.DefaultRequestHeaders.Remove("X-DV-Confirm");
        Check((await owner.PutAsJsonAsync(path, change)).StatusCode == HttpStatusCode.Conflict, "voice updates require explicit confirmation");
        owner.DefaultRequestHeaders.Add("X-DV-Confirm", "yes");
        owner.DefaultRequestHeaders.Remove("X-DV-CSRF");
        Check((await owner.PutAsJsonAsync(path, change)).StatusCode == HttpStatusCode.BadRequest, "voice updates retain normal CSRF protection");
        await CsrfAsync(owner);
        Check((await owner.PutAsJsonAsync(path, change with { KufrVoiceId = "../bad" })).StatusCode == HttpStatusCode.BadRequest,
            "voice endpoint rejects invalid ID syntax");
        var saved = await JsonAsync(await owner.PutAsJsonAsync(path, change));
        Check(saved.GetProperty("kufrVoiceId").GetString() == "OwnerKufr" && saved.GetProperty("baronVoiceId").GetString() == "OwnerBaron",
            "owner can persist two independently selected voices");
        Check((await owner.PutAsJsonAsync(path, change)).StatusCode == HttpStatusCode.Conflict, "stale voice revisions cannot overwrite newer choices");
        foreach (var role in new[] { AccountRoles.Tester, AccountRoles.Administrator })
        {
            var invitation = await JsonAsync(await owner.PostAsJsonAsync("/app/v1/admin/invitations", new { role, recoveryUserId = (string?)null }));
            using var member = fixture.Client();
            using var memberKey = new VirtualPasskey();
            await EnrollAsync(member, memberKey, invitation.GetProperty("token").GetString()!, role);
            await CsrfAsync(member);
            var read = await member.GetAsync(path);
            Check(read.StatusCode == (role == AccountRoles.Administrator ? HttpStatusCode.OK : HttpStatusCode.Forbidden),
                "voice settings follow actual administrative roles");
            if (role == AccountRoles.Tester)
            {
                Check((await member.PutAsJsonAsync(path, change with { ExpectedRevision = revision + 1 })).StatusCode == HttpStatusCode.Forbidden,
                    "a development-style Tester session cannot edit actor voices");
            }
        }
        using (var scope = fixture.App.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AccountDatabase>();
            await db.Sessions.Where(item => item.UserId == ownerId).ExecuteUpdateAsync(update => update.SetProperty(item => item.AuthenticatedAt, 1));
        }
        var stale = await owner.PutAsJsonAsync(path, change with { ExpectedRevision = revision + 1 });
        Check(stale.StatusCode == HttpStatusCode.Forbidden && (await stale.Content.ReadAsStringAsync()).Contains("recent_passkey_required"),
            "voice updates require a recently presented passkey");
        Check((await owner.GetAsync(path)).IsSuccessStatusCode, "reading voice settings does not require a fresh passkey");
        var root = fixture.App.Services.GetRequiredService<AccountConfiguration>().DataRoot;
        var file = Path.Combine(root, "actor-voices.json");
        File.WriteAllText(file, "{invalid settings");
        var corrupt = await owner.GetAsync(path);
        Check(corrupt.StatusCode == HttpStatusCode.ServiceUnavailable
            && (await corrupt.Content.ReadAsStringAsync()).Contains("actor_voice_settings_corrupt")
            && File.ReadAllText(file) == "{invalid settings", "corrupt durable settings produce an explicit error without being overwritten");
        using (var scope = fixture.App.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AccountDatabase>();
            var user = await db.Users.SingleAsync(user => user.Id == ownerId);
            var principal = await scope.ServiceProvider.GetRequiredService<IUserClaimsPrincipalFactory<AccountUser>>().CreateAsync(user);
            var sessionId = await db.Sessions.Where(session => session.UserId == ownerId).Select(session => session.Id).FirstAsync();
            ((ClaimsIdentity)principal.Identity!).AddClaim(new Claim(AccountIdentity.SessionClaim, sessionId));
            user.Disabled = true;
            await db.SaveChangesAsync();
            var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider, User = principal };
            context.Request.Method = "GET";
            var denied = await ActorVoiceSettingsEndpoints.AuthorizeAsync(context);
            Check((denied as IStatusCodeHttpResult)?.StatusCode == 401,
                "endpoint authorization rechecks disabled accounts even with an otherwise valid administrative principal and session");
            user.Disabled = false;
            await db.SaveChangesAsync();
        }
        using (var scope = fixture.App.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AccountDatabase>();
            await db.Sessions.Where(item => item.UserId == ownerId).ExecuteUpdateAsync(update => update.SetProperty(item => item.ExpiresAt, 1));
        }
        Check((await owner.GetAsync(path)).StatusCode == HttpStatusCode.Unauthorized, "expired account sessions cannot read administrative voice settings");
    }
}
