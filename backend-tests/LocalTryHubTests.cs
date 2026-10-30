using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Kufr.Backend.Features.Game;
using Kufr.Backend.Features.GptLive;
using Kufr.Backend.Features.Identity;
using Kufr.Backend.Features.Launch;
using Kufr.Backend.Features.Wands;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

internal static partial class AccountIdentityTests
{
    private const string LocalTrySecret = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    internal static async Task RunLocalTryHubAsync()
    {
        LocalTryHubFences();
        await LocalTryHubProductionOffAsync();
        await LocalTryHubSessionAsync();
        await LocalTryHubKeepsExistingAccountAsync();
        await LocalTryHubExperimentsAsync();
        Console.WriteLine("Local try hub HTTPS bootstrap, normal session/workshop authorization, consent and security fences passed (offline).");
    }

    private static void ConfigureLocalTryHub(IConfiguration configuration)
    {
        configuration["LocalTryHub:Enabled"] = "true";
        configuration["LocalTryHub:SessionSecret"] = LocalTrySecret;
        configuration["LocalTryHub:Origin"] = "https://localhost";
        configuration["LocalTryHub:DataRoot"] = Path.GetDirectoryName(configuration["ApplicationIdentity:DataRoot"]);
    }

    private static void LocalTryHubFences()
    {
        var root = Path.Combine(Path.GetTempPath(), "local-try-fences-" + Guid.NewGuid().ToString("N"));
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["LocalTryHub:SessionSecret"] = LocalTrySecret, ["LocalTryHub:Origin"] = "https://localhost",
            ["LocalTryHub:DataRoot"] = root, ["Backend:DatabasePath"] = Path.Combine(root, "backend.sqlite")
        }).Build();
        var identity = new AccountConfiguration(true, Path.Combine(root, "accounts"), Path.Combine(root, "keys"),
            "localhost", ["https://localhost"]);
        var settings = LocalTryHubConfiguration.Read(configuration, identity);
        var http = new DefaultHttpContext();
        http.Request.Scheme = "https";
        http.Request.Headers.Origin = "https://localhost";
        http.Request.Headers["X-DV-Local-Try"] = LocalTrySecret;
        Check(settings.Allows(http, IPAddress.Loopback, IPAddress.Loopback), "explicit isolated loopback HTTPS bootstrap is allowed");
        http.Request.Headers["X-Forwarded-For"] = "127.0.0.1";
        Check(!settings.Allows(http, IPAddress.Parse("203.0.113.10"), IPAddress.Loopback),
            "spoofed forwarded loopback cannot replace the actual transport peer");
        Check(!settings.Allows(http, IPAddress.Loopback, IPAddress.Parse("192.0.2.1")), "nonloopback local interface is rejected");
        http.Request.Scheme = "http";
        Check(!settings.Allows(http, IPAddress.Loopback, IPAddress.Loopback), "cleartext bootstrap is rejected");
        http.Request.Scheme = "https";
        http.Request.Headers.Origin = "https://localhost:9999";
        Check(!settings.Allows(http, IPAddress.Loopback, IPAddress.Loopback), "origin port is exact");
        http.Request.Headers.Origin = "https://localhost";
        http.Request.Headers["X-DV-Local-Try"] = new string('f', 64);
        Check(!settings.Allows(http, IPAddress.Loopback, IPAddress.Loopback), "wrong per-run secret is rejected");
        foreach (var escaped in new[] { Path.Combine(root + "-other", "database.sqlite"), Path.Combine(root, "..", "other.sqlite") })
        {
            configuration["Backend:DatabasePath"] = escaped;
            try { LocalTryHubConfiguration.Read(configuration, identity); throw new Exception("Escaped local data accepted"); }
            catch (InvalidOperationException error) { Check(error.Message.Contains("isolated root"), "configured backend database cannot escape isolation"); }
        }
        configuration["Backend:DatabasePath"] = Path.Combine(root, "backend.sqlite");
        configuration["LocalTryHub:SessionSecret"] = "short";
        try { LocalTryHubConfiguration.Read(configuration, identity); throw new Exception("Weak local secret accepted"); }
        catch (InvalidOperationException) { }
    }

    private static async Task LocalTryHubProductionOffAsync()
    {
        await using var fixture = await Fixture.StartAsync(configureConfiguration: configuration =>
        {
            configuration["LocalTryHub:Enabled"] = "true";
            configuration["LocalTryHub:SessionSecret"] = "deliberately-invalid-production-setting";
        }, configureEndpoints: app =>
        {
            app.Environment.EnvironmentName = "Production";
            app.MapLocalTryHubSession();
        });
        using var client = fixture.Client();
        Check((await client.PostAsJsonAsync("/local-try-api/session", new { })).StatusCode == HttpStatusCode.NotFound,
            "production endpoint stays absent even when its opt-in flag is accidentally enabled");
        Check((await client.PostAsJsonAsync("/local-try-api/experiments/read", new { })).StatusCode == HttpStatusCode.NotFound,
            "production never exposes experiment prompts");
    }

    private static async Task LocalTryHubExperimentsAsync()
    {
        await using var fixture = await Fixture.StartAsync(
            environmentName: "Development",
            configureServices: services => services.AddSingleton<LocalTryHubExperimentStore>(),
            configureConfiguration: ConfigureLocalTryHub,
            configureEndpoints: app =>
            {
                app.Environment.EnvironmentName = "Development";
                app.Use(async (context, next) =>
                {
                    context.Features.Get<IHttpMaxRequestBodySizeFeature>()!.MaxRequestBodySize = 4096;
                    await next(context);
                });
                app.MapLocalTryHubSession();
            });
        using var client = fixture.Client();
        Check((await client.PostAsJsonAsync("/local-try-api/experiments/read", new { })).StatusCode == HttpStatusCode.Forbidden,
            "prompt reads require the per-run local capability");
        client.DefaultRequestHeaders.Add("X-DV-Local-Try", LocalTrySecret);
        var initial = await JsonAsync(await client.PostAsJsonAsync("/local-try-api/experiments/read", new { }));
        var original = initial.GetProperty("settings");
        Check(original.GetProperty("revision").GetInt64() == 0
            && original.GetProperty("dialoguePrompt").GetString() == OpenAiMobileWandQuestionProvider.DefaultInstructions
            && original.GetProperty("livePrompt").GetString() == MobileWorkshopLiveRuntime.DefaultInstructions,
            "editor starts with the canonical prompts actually used by both runtimes");
        var update = new LocalTryHubExperimentUpdate(0, "gpt-live",
            OpenAiMobileWandQuestionProvider.DefaultInstructions, MobileWorkshopLiveRuntime.DefaultInstructions);
        var saved = await JsonAsync(await client.PostAsJsonAsync("/local-try-api/experiments", update));
        Check(saved.GetProperty("settings").GetProperty("revision").GetInt64() == 1,
            "a successful save advances the experiment revision");
        Check((await client.PostAsJsonAsync("/local-try-api/experiments", update)).StatusCode == HttpStatusCode.Conflict,
            "a stale editor cannot overwrite another tab's settings");
        Check((await client.PostAsJsonAsync("/local-try-api/experiments", update with { ExpectedRevision = 1, Provider = "unknown" })).StatusCode == HttpStatusCode.BadRequest
            && (await client.PostAsJsonAsync("/local-try-api/experiments", update with { ExpectedRevision = 1, LivePrompt = new string('x', 12001) })).StatusCode == HttpStatusCode.BadRequest,
            "unsupported providers and oversized prompts are rejected");
        var reloaded = new LocalTryHubExperimentStore(fixture.App.Configuration, fixture.App.Environment);
        var snapshot = reloaded.Snapshot();
        Check(snapshot is { Revision: 1, Provider: "gpt-live" } && snapshot.DialoguePrompt == update.DialoguePrompt,
            "local provider and prompt edits survive a server restart");
        var latest = await JsonAsync(await client.PostAsJsonAsync("/local-try-api/experiments", update with { ExpectedRevision = 1, Provider = "elevenlabs" }));
        Check(latest.GetProperty("settings").GetProperty("revision").GetInt64() == 2 && snapshot!.Provider == "gpt-live",
            "previous admissions retain an immutable snapshot when settings change");
        Check((await client.PostAsJsonAsync("/local-try-api/experiments", update with
            { ExpectedRevision = 2, DialoguePrompt = new string('ž', 12000), LivePrompt = new string('ž', 12000) })).IsSuccessStatusCode,
            "both maximum Czech prompts fit the bounded local API despite the normal 4096-byte server limit");
        client.DefaultRequestHeaders.Remove("Origin");
        client.DefaultRequestHeaders.Add("Origin", "https://untrusted.example");
        Check((await client.PostAsJsonAsync("/local-try-api/experiments", update with { ExpectedRevision = 2 })).StatusCode == HttpStatusCode.Forbidden,
            "an untrusted origin cannot edit local prompts even with the capability");
        Check(new LocalTryHubExperimentStore(fixture.App.Configuration,
            new Microsoft.Extensions.Hosting.Internal.HostingEnvironment { EnvironmentName = "Production" }).Snapshot() is null,
            "production admissions cannot consume saved local experiments");
        var savedPath = Path.Combine(fixture.App.Configuration["LocalTryHub:DataRoot"]!, "experiments.json");
        foreach (var invalid in new[] { "{\"Revision\":-1,\"Provider\":\"unknown\",\"DialoguePrompt\":\"a\",\"LivePrompt\":\"b\"}", new string('x', 256 * 1024 + 1) })
        {
            await File.WriteAllTextAsync(savedPath, invalid);
            var invalidStore = new LocalTryHubExperimentStore(fixture.App.Configuration, fixture.App.Environment);
            var invalidView = JsonSerializer.SerializeToElement(invalidStore.View());
            Check(invalidView.GetProperty("loadError").GetString() == "local_try_hub_settings_unreadable"
                && invalidStore.Snapshot()!.Revision == 0 && await File.ReadAllTextAsync(savedPath) == invalid,
                "invalid saved settings show an explicit error and preserve the original file without breaking startup");
        }
    }

    private static async Task LocalTryHubSessionAsync()
    {
        await using var fixture = await Fixture.StartAsync(customerFeatures: true, betaRecruitmentOpen: false,
            configureConfiguration: ConfigureLocalTryHub,
            configureServices: services =>
            {
                services.AddSingleton(sp => new GameStore(sp.GetRequiredService<AccountConfiguration>().DataRoot));
                services.AddSingleton<WandGenerationStore>();
                services.AddSingleton<IWandGenerationProvider, PrelaunchWandProvider>();
                services.AddSingleton<IMobileWandQuestionProvider, PrelaunchWorkshopDialogue>();
                services.AddSingleton<WandGenerationService>();
                services.AddSingleton<MobileWandRuntime>();
                services.AddSingleton<MobileWorkshopLiveRuntime>();
                services.AddSingleton<GptLiveClient>();
                services.AddSingleton(new GptLiveOptions());
                services.AddSingleton(new ElevenLabsWorkshopOptions());
                services.AddSingleton<IWorkshopSpeechProvider, ElevenLabsWorkshopProvider>();
                services.AddSingleton<ElevenLabsWorkshopRuntime>();
            }, configureEndpoints: app =>
            {
                app.Environment.EnvironmentName = "Development";
                app.MapLocalTryHubSession();
                app.MapMobileWands();
                app.MapWorkshopLive();
            });
        using var client = fixture.Client();
        Check((await client.PostAsJsonAsync("/local-try-api/session", new { })).StatusCode == HttpStatusCode.Forbidden,
            "no bootstrap session is issued without proxy-held secret");
        client.DefaultRequestHeaders.Add("X-DV-Local-Try", LocalTrySecret);
        client.DefaultRequestHeaders.Remove("Origin");
        client.DefaultRequestHeaders.Add("Origin", "https://evil.example");
        Check((await client.PostAsJsonAsync("/local-try-api/session", new { })).StatusCode == HttpStatusCode.Forbidden,
            "actual bootstrap HTTP rejects an unexpected origin");
        client.DefaultRequestHeaders.Remove("Origin");
        client.DefaultRequestHeaders.Add("Origin", "https://localhost");
        using var response = await client.PostAsJsonAsync("/local-try-api/session", new { });
        Check(response.IsSuccessStatusCode && (await JsonAsync(response)).GetProperty("ready").GetBoolean(), "local bootstrap returns ready");
        var setCookies = string.Join(";", response.Headers.GetValues("Set-Cookie"));
        Check(setCookies.Contains("__Host-") && setCookies.Contains("httponly", StringComparison.OrdinalIgnoreCase)
            && setCookies.Contains("secure", StringComparison.OrdinalIgnoreCase)
            && !setCookies.Contains("expires=", StringComparison.OrdinalIgnoreCase), "bootstrap reuses the secure ephemeral Identity cookie");
        var session = await CsrfAsync(client);
        var account = session.GetProperty("account");
        var id = account.GetProperty("id").GetString();
        var roles = account.GetProperty("roles").EnumerateArray().Select(item => item.GetString()).ToArray();
        Check(roles.Order().SequenceEqual(new[] { AccountRoles.Customer, AccountRoles.Tester }.Order())
            && !account.GetProperty("openAiConsent").GetBoolean(), "dedicated guest has only Customer+Tester roles and no automatic processing consent");
        using var sensitive = await client.PutAsJsonAsync("/app/v1/installations/unknown", new { name = "Local", trusted = true });
        Check(sensitive.StatusCode == HttpStatusCode.Forbidden
            && (await sensitive.Content.ReadAsStringAsync()).Contains("recent_passkey_required"),
            "a freshly issued shortcut cookie cannot pass normal recent-passkey authorization");
        var membership = await JsonAsync(await client.GetAsync("/app/v1/customer/access"));
        Check(membership.GetProperty("state").GetString() == "active" && membership.GetProperty("groupId").GetString() is { Length: > 0 },
            "normal customer membership refresh sees an active assigned local group");
        Check((await JsonAsync(await client.GetAsync("/app/v1/launch"))).GetProperty("allowed").GetBoolean(),
            "normal launch refresh allows the tester without frontend bypasses");
        Check((await client.GetAsync("/app/v1/customer/wand/workshop")).StatusCode == HttpStatusCode.Forbidden,
            "workshop still requires explicit normal OpenAI consent after automatic local login");
        Check((await client.PostAsJsonAsync("/app/v1/account/ai-consent", new { openAiConsent = true, consentVersion = Kufr.Backend.Features.Identity.AccountAiConsent.CurrentVersion })).IsSuccessStatusCode,
            "normal consent action remains available with normal CSRF token");
        var workshop = await JsonAsync(await client.GetAsync("/app/v1/customer/wand/workshop"));
        Check(workshop.GetProperty("reason").GetString() == "character_required",
            "normal workshop route reaches its character preparation flow without signin or launch diversion");
        await using (var scope = fixture.App.Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<AccountUser>>();
            var user = await users.FindByIdAsync(id!) ?? throw new Exception("Missing local guest");
            Check(await MobileWandWorkshopAccess.IsAllowedAsync(user, 1, scope.ServiceProvider, default),
                "the existing first-wand membership/consent authorization accepts the local group");
            var db = scope.ServiceProvider.GetRequiredService<AccountDatabase>();
            Check(await db.Users.CountAsync() == 1 && !(await db.Sessions.AnyAsync(item => item.UserId == id && item.AuthenticatedAt > 0)),
                "shortcut does not mint a recent-passkey authentication claim");
        }
        Check((await client.PostAsJsonAsync("/local-try-api/session", new { })).IsSuccessStatusCode,
            "subsequent local bootstrap keeps the existing account cookie");
        Check((await JsonAsync(await client.GetAsync("/app/v1/session"))).GetProperty("account").GetProperty("id").GetString() == id,
            "subsequent bootstrap never rebinds the current user");
    }

    private static async Task LocalTryHubKeepsExistingAccountAsync()
    {
        await using var fixture = await Fixture.StartAsync(configureConfiguration: ConfigureLocalTryHub,
            configureEndpoints: app =>
            {
                app.Environment.EnvironmentName = "Development";
                app.MapLocalTryHubSession();
            });
        using var client = fixture.Client();
        using var key = new VirtualPasskey();
        await EnrollAsync(client, key, Fixture.Bootstrap, "Existing local account");
        var before = (await JsonAsync(await client.GetAsync("/app/v1/session"))).GetProperty("account");
        client.DefaultRequestHeaders.Add("X-DV-Local-Try", LocalTrySecret);
        Check((await client.PostAsJsonAsync("/local-try-api/session", new { })).IsSuccessStatusCode,
            "an existing authenticated local account can continue");
        var after = (await JsonAsync(await client.GetAsync("/app/v1/session"))).GetProperty("account");
        Check(before.GetProperty("id").GetString() == after.GetProperty("id").GetString()
            && before.GetProperty("roles").GetRawText() == after.GetProperty("roles").GetRawText(),
            "existing account identity and roles are preserved exactly");
    }
}
