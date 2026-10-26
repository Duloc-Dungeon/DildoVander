using System.Security.Claims;
using System.Net;
using Kufr.Backend.Features.Customers;
using Kufr.Backend.Features.Identity;
using Kufr.Backend.Features.Launch;
using Kufr.Backend.Features.Access;
using Kufr.Backend.Features.DigitalTwin;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;

internal static class LaunchPolicyTests
{
    internal static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "dildovander-launch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await VerifyConsumerDefaultsAsync(Path.Combine(root, "consumer-defaults"));
            var store = new LaunchPolicyStore(root);
            var clock = new TestClock(DateTimeOffset.FromUnixTimeMilliseconds(1_000));
            var policy = new LaunchAccessService(store, clock);
            var customer = Principal(AccountRoles.Customer);
            var beta = Principal(AccountRoles.Beta);
            var tester = Principal(AccountRoles.Tester);
            Check(!AccountRoles.IsStaff(beta) && !AccountRoles.IsAdmin(beta),
                "beta participant has no administrative or operator permission");
            Check(AccountRoles.AssignedRoles(AccountRoles.Beta).SequenceEqual([AccountRoles.Customer, AccountRoles.Beta])
                && AccountRoles.AssignedRoles(AccountRoles.Customer).SequenceEqual([AccountRoles.Customer]),
                "beta assignment retains customer capabilities without widening regular customer roles");

            Check(policy.Evaluate(customer).Allowed && policy.Evaluate(customer).Audience == "production",
                "default policy preserves existing customer access");

            var saved = store.Update("admin-1", new(0, true, 2_000, false, true, 3_000, false,
                "Europe/Prague", "Ještě chvilku…", "Mícháme hvězdný prach…", true, "safe prompt"), 1_000);
            Check(saved.Revision == 1 && !policy.Evaluate(beta).Allowed && !policy.Evaluate(customer).Allowed,
                "each audience remains gated until its own launch");
            Check(policy.Evaluate(tester).Allowed && policy.Evaluate(tester).Audience == "privileged",
                "internal tester keeps privileged access independent of beta schedule");
            Check(policy.Evaluate(beta).LaunchAt == 2_000 && policy.Evaluate(customer).LaunchAt == 3_000,
                "audiences use separate server schedules");

            clock.Set(DateTimeOffset.FromUnixTimeMilliseconds(2_000));
            Check(policy.Evaluate(beta).Allowed && !policy.Evaluate(customer).Allowed,
                "beta launch does not unlock production");
            saved = store.Update("admin-1", new(1, true, 2_000, false, true, 3_000, true,
                "Europe/Prague", "Ještě chvilku…", "Mícháme hvězdný prach…", true, "safe prompt"), 2_000);
            Check(saved.Revision == 2 && policy.Evaluate(customer).Allowed,
                "manual production unlock takes effect immediately");

            try
            {
                store.Update("admin-2", new(1, true, 2_000, false, true, 3_000, true,
                    "Europe/Prague", "x", "Loading", false, "prompt"), 2_000);
                throw new InvalidOperationException("Launch policy: stale revision was accepted.");
            }
            catch (LaunchConfigurationException error) when (error.Status == 409) { }

            try
            {
                store.Update("admin-2", new(2, true, 2_000, false, true, 3_000, true,
                    "Not/A-Timezone", "x", "Loading", false, "prompt"), 2_000);
                throw new InvalidOperationException("Launch policy: invalid timezone was accepted.");
            }
            catch (LaunchConfigurationException error) when (error.Status == 400) { }

            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "launch.sqlite") }.ToString());
            connection.Open();
            using var query = connection.CreateCommand();
            query.CommandText = "SELECT COUNT(*) FROM launch_audit WHERE actor_id='admin-1'";
            Check(Convert.ToInt32(query.ExecuteScalar()) == 2, "successful changes are durably audited; rejected writes are not");

            var reloaded = new LaunchPolicyStore(root).Read();
            Check(reloaded.Revision == 2 && reloaded.ProductionUnlocked && reloaded.PersonalizedLoadingPrompt == "safe prompt",
                "configuration survives store restart");

            var nightOne = store.CreateGroup("admin-1", new("Night one", 4_000), 3_000);
            var nightTwo = store.CreateGroup("admin-1", new("Night two", 5_000), 3_000);
            var noLaunchTime = store.CreateGroup("admin-1", new("Unscheduled", null), 3_000);
            Check(store.ReadGroups().Length == 3 && new LaunchPolicyStore(root).ReadGroup(nightOne.Id) == nightOne,
                "group names and schedules persist independently");
            Check(!policy.Evaluate(customer, nightOne.Id, nightOne).Allowed
                && policy.Evaluate(customer, nightOne.Id, nightOne).LaunchAt == 4_000
                && !policy.Evaluate(customer, nightTwo.Id, nightTwo).Allowed
                && !policy.Evaluate(customer, noLaunchTime.Id, noLaunchTime).Allowed,
                "future and unscheduled groups override an already unlocked global audience");
            Check(!policy.Evaluate(customer, "missing-group", null).Allowed
                && policy.Evaluate(tester, nightOne.Id, nightOne).Allowed,
                "unresolved memberships fail closed while tester retains its bypass");
            clock.Set(DateTimeOffset.FromUnixTimeMilliseconds(4_000));
            Check(policy.Evaluate(customer, nightOne.Id, nightOne).Allowed
                && !policy.Evaluate(customer, nightTwo.Id, nightTwo).Allowed,
                "group schedules open independently according to server time");
            var closed = store.UpdateGroup("admin-1", nightOne.Id,
                new(nightOne.Revision, nightOne.Name, null), 4_000);
            Check(closed.Revision == 2 && !policy.Evaluate(customer, closed.Id, closed).Allowed,
                "clearing a group's launch time keeps its members closed");

            await RunAccessBoundaryAsync(root);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }

        Console.WriteLine("PASS launch policy: audience schedules, manual unlock, internal tester exception, optimistic concurrency, audit and persistence");
    }

    private static async Task VerifyConsumerDefaultsAsync(string root)
    {
        Directory.CreateDirectory(root);
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(root, "launch.sqlite")
        }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE launch_configuration (
                    id INTEGER PRIMARY KEY CHECK(id=1), revision INTEGER NOT NULL,
                    beta_enabled INTEGER NOT NULL, beta_launch_at INTEGER NULL, beta_unlocked INTEGER NOT NULL,
                    production_enabled INTEGER NOT NULL, production_launch_at INTEGER NULL, production_unlocked INTEGER NOT NULL,
                    time_zone TEXT NOT NULL, teaser TEXT NOT NULL, initial_loading_message TEXT NOT NULL,
                    personalized_loading_enabled INTEGER NOT NULL, personalized_loading_prompt TEXT NOT NULL);
                INSERT INTO launch_configuration VALUES
                    (1,0,0,NULL,0,0,NULL,0,'Europe/Prague','A little something is getting ready for you.',
                     'A peculiar little surprise is taking shape…',0,
                     'Write one brief, playful, mysterious loading message inspired by the supplied fictional character context. Do not reveal product features or repeat research facts.');
                """;
            command.ExecuteNonQuery();
        }
        var store = new LaunchPolicyStore(root);
        var defaults = store.Read();
        Check(defaults.Teaser == LaunchConfiguration.DefaultTeaser
            && defaults.InitialLoadingMessage == LaunchConfiguration.DefaultInitialLoadingMessage
            && defaults.PersonalizedLoadingEnabled
            && PortraitContentPolicy.ValidLoadingMessage(defaults.Teaser)
            && defaults.Teaser.Contains("kocovinu", StringComparison.OrdinalIgnoreCase)
            && PortraitContentPolicy.ValidLoadingMessage(defaults.InitialLoadingMessage)
            && defaults.InitialLoadingMessage.Contains("proroctví", StringComparison.OrdinalIgnoreCase)
            && defaults.PersonalizedLoadingPrompt.Contains("aniž prozradíš, co se chystá", StringComparison.OrdinalIgnoreCase)
            && !new[] { "portrét", "portrait", "malb", "obraz", "avatar", "výzkum", "research", "krok" }
                .Any(term => defaults.PersonalizedLoadingPrompt.Contains(term, StringComparison.OrdinalIgnoreCase)),
            "untouched legacy defaults migrate to Czech consumer copy and optional personalization");
        var previousRoot = Path.Combine(root, "previous-defaults");
        Directory.CreateDirectory(previousRoot);
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(previousRoot, "launch.sqlite")
        }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE launch_configuration (
                    id INTEGER PRIMARY KEY CHECK(id=1), revision INTEGER NOT NULL,
                    beta_enabled INTEGER NOT NULL, beta_launch_at INTEGER NULL, beta_unlocked INTEGER NOT NULL,
                    production_enabled INTEGER NOT NULL, production_launch_at INTEGER NULL, production_unlocked INTEGER NOT NULL,
                    time_zone TEXT NOT NULL, teaser TEXT NOT NULL, initial_loading_message TEXT NOT NULL,
                    personalized_loading_enabled INTEGER NOT NULL, personalized_loading_prompt TEXT NOT NULL);
                INSERT INTO launch_configuration VALUES
                    (1,0,0,NULL,0,0,NULL,0,'Europe/Prague',
                     'Noční stolek pro velká očekávání. Olivander má kocovinu; na noční zázrak si chvíli počkáš.',
                     'Olivander má kocovinu; dnes stíháme připravit jen noční stolek. Na velké zázraky si ještě chvíli počkej.',1,
                     'Napiš jednu krátkou českou větu, která postavu pozná přes jediný neškodný veřejný fikční rys z bezpečného shrnutí. Přidej suchý britský humor, slovní hříčku o nočním stolku a připomeň, že Olivander má kocovinu, takže se čeká. Neuváděj zdroje, citace, rešerše ani soukromé údaje.');
                """;
            command.ExecuteNonQuery();
        }
        var migratedPrevious = new LaunchPolicyStore(previousRoot).Read();
        Check(migratedPrevious.Teaser == LaunchConfiguration.DefaultTeaser
            && migratedPrevious.InitialLoadingMessage == LaunchConfiguration.DefaultInitialLoadingMessage
            && migratedPrevious.PersonalizedLoadingPrompt == LaunchConfiguration.DefaultPersonalizedLoadingPrompt,
            "only untouched revision-zero prior defaults migrate to the new suspense copy");
        try
        {
            store.Update("admin", new(defaults.Revision, false, null, false, false, null, false,
                defaults.TimeZone, "A portrait is coming.", defaults.InitialLoadingMessage, defaults.PersonalizedLoadingEnabled,
                defaults.PersonalizedLoadingPrompt), 1);
            throw new InvalidOperationException("Pre-reveal artifact reference should be rejected.");
        }
        catch (LaunchConfigurationException error) when (error.Status == 400) { }
        var saved = store.Update("admin", new(defaults.Revision, false, null, false, false, null, false,
            defaults.TimeZone, defaults.Teaser, defaults.InitialLoadingMessage, false, "explicitly saved prompt"), 1);
        var reopened = new LaunchPolicyStore(root).Read();
        Check(!reopened.PersonalizedLoadingEnabled && reopened.PersonalizedLoadingPrompt == "explicitly saved prompt"
            && reopened.Revision == saved.Revision,
            "explicit admin personalization settings survive restart without being replaced by new defaults");
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(root, "launch.sqlite")
        }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE launch_configuration SET revision=revision+1,production_enabled=1,production_launch_at=9000000000000,production_unlocked=0,teaser='Malujeme tvůj portrét.',initial_loading_message='Teď připravujeme tvůj avatar.' WHERE id=1";
            Check(command.ExecuteNonQuery() == 1, "seed a revisioned legacy reveal string for the access projection regression");
        }
        var rawAdminConfiguration = store.Read();
        var launchAccess = new LaunchAccessService(store, TimeProvider.System);
        var guestProjection = launchAccess.Evaluate(new ClaimsPrincipal());
        Check(rawAdminConfiguration.Revision == saved.Revision + 1
            && rawAdminConfiguration.Teaser == "Malujeme tvůj portrét."
            && rawAdminConfiguration.InitialLoadingMessage == "Teď připravujeme tvůj avatar."
            && guestProjection.Teaser == LaunchConfiguration.DefaultTeaser
            && guestProjection.InitialLoadingMessage == LaunchConfiguration.DefaultInitialLoadingMessage
            && PortraitContentPolicy.ValidLoadingMessage(guestProjection.Teaser)
            && PortraitContentPolicy.ValidLoadingMessage(guestProjection.InitialLoadingMessage)
            && !guestProjection.Allowed && guestProjection.LaunchAt == 9_000_000_000_000,
            "public access projection uses secret defaults for stored reveal copy while raw admin read and gate remain unchanged");
        await Task.CompletedTask;
    }

    private static async Task RunAccessBoundaryAsync(string root)
    {
        var gateRoot = Path.Combine(root, "gate");
        Directory.CreateDirectory(gateRoot);
        var store = new LaunchPolicyStore(gateRoot);
        store.Update("admin", new(0, false, null, false, true, 9_000_000_000_000, false,
            "Europe/Prague", "", "waiting", false, "prompt"), 1_000);
        Check(new LaunchAccessService(store, TimeProvider.System).Evaluate(new ClaimsPrincipal(new ClaimsIdentity())).Teaser == "",
            "an explicitly empty optional teaser stays empty in the public projection");
        var config = new AccountConfiguration(true, gateRoot, gateRoot, "localhost", ["https://localhost"]);
        var builder = WebApplication.CreateBuilder();
        const string deviceCredential = "offline-test-device-credential-123456";
        const string sourceCredential = "offline-test-source-credential-123456";
        const string observerCredential = "offline-test-observer-credential-123456";
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ApplicationIdentity:Enabled"] = "true", ["DigitalTwin:LegacyServiceSessionsEnabled"] = "true",
            ["Devices:0:DeviceId"] = "test-device", ["Devices:0:CredentialSha256"] = AccountEndpoints.Hash(deviceCredential),
            ["DigitalTwin:Credentials:0:Role"] = "ingest", ["DigitalTwin:Credentials:0:Mode"] = "live",
            ["DigitalTwin:Credentials:0:InstanceId"] = "test-device", ["DigitalTwin:Credentials:0:Sha256"] = AccountEndpoints.Hash(sourceCredential),
            ["DigitalTwin:Credentials:1:Role"] = "observe", ["DigitalTwin:Credentials:1:Sha256"] = AccountEndpoints.Hash(observerCredential)
        });
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(config);
        builder.Services.AddSingleton(store);
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<LaunchAccessService>();
        builder.Services.AddSingleton<DeviceAccessStore>();
        builder.Services.AddSingleton<TwinAccess>();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        await using var app = builder.Build();
        app.Use(async (context, next) =>
        {
            var role = context.Request.Headers["X-Test-Role"].ToString();
            var claims = new List<Claim>();
            if (!string.IsNullOrEmpty(role)) { claims.Add(new(ClaimTypes.Role, role)); }
            if (context.Request.Headers.ContainsKey("X-Test-Session")) { claims.Add(new(AccountIdentity.SessionClaim, "session")); }
            context.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test", ClaimTypes.Name, ClaimTypes.Role));
            await next(context);
        });
        app.UseLaunchAccessPolicy();
        app.MapGet("/app/v1/customer/character", () => Results.Ok());
        app.MapGet("/app/v1/prelaunch/character", () => Results.Ok());
        app.MapGet("/app/v1/launch", () => Results.Ok());
        app.MapGet("/app/v1/account/deletion", () => Results.Ok());
        app.MapGet("/v1/conversations/private", () => Results.Ok());
        app.MapPost("/app/v1/customer/enrollment/options", () => Results.Ok());
        app.MapGet("/guest/v1/evening", () => Results.Ok());
        app.MapGet("/twin", () => Results.Ok());
        app.MapGet("/v1/twin/instances", () => Results.Ok());
        app.MapPost("/v1/twin/device-token", () => Results.Ok());
        app.MapPost("/v1/twin/ingest/{id}", () => Results.Ok());
        await app.StartAsync();
        try
        {
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var client = new HttpClient { BaseAddress = new Uri(address) };
            using var regular = new HttpRequestMessage(HttpMethod.Get, "/app/v1/customer/character");
            regular.Headers.Add("X-Test-Role", AccountRoles.Customer);
            regular.Headers.Add("X-Test-Session", "yes");
            regular.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "arbitrary");
            using var hidden = await client.SendAsync(regular);
            Check((int)hidden.StatusCode == 423, "locked account cannot call existing character API");

            using var prelaunch = new HttpRequestMessage(HttpMethod.Get, "/app/v1/prelaunch/character");
            prelaunch.Headers.Add("X-Test-Role", AccountRoles.Customer);
            prelaunch.Headers.Add("X-Test-Session", "yes");
            using var visible = await client.SendAsync(prelaunch);
            Check(visible.IsSuccessStatusCode, "locked account can reach only the dedicated prelaunch API");

            using var legacy = await client.GetAsync("/v1/conversations/private");
            Check((int)legacy.StatusCode == 423, "anonymous users cannot inspect legacy protected APIs during production lock");
            using var deletionStatus = await client.GetAsync("/app/v1/account/deletion");
            Check(deletionStatus.IsSuccessStatusCode, "signed-out account deletion receipt remains available during launch lock");
            using var spoofedLegacy = new HttpRequestMessage(HttpMethod.Get, "/v1/conversations/private");
            spoofedLegacy.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "arbitrary");
            using var spoofedLegacyResponse = await client.SendAsync(spoofedLegacy);
            Check((int)spoofedLegacyResponse.StatusCode == 423, "arbitrary bearer cannot bypass a legacy API launch lock");
            using var signup = new HttpRequestMessage(HttpMethod.Post, "/app/v1/customer/enrollment/options");
            signup.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "arbitrary");
            using var signupResponse = await client.SendAsync(signup);
            Check((int)signupResponse.StatusCode == 423, "open customer enrollment is disabled while production is locked");
            using var guest = await client.GetAsync("/guest/v1/evening");
            Check((int)guest.StatusCode == 423, "anonymous users cannot inspect public guest surfaces during production lock");
            using var spoofedGuest = new HttpRequestMessage(HttpMethod.Get, "/guest/v1/evening");
            spoofedGuest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "arbitrary");
            using var spoofedGuestResponse = await client.SendAsync(spoofedGuest);
            Check((int)spoofedGuestResponse.StatusCode == 423, "arbitrary bearer cannot bypass public guest surface lock");
            using var claim = new HttpRequestMessage(HttpMethod.Post, "/v1/keepsakes/11111111-1111-1111-1111-111111111111/claim");
            claim.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "arbitrary");
            using var claimResponse = await client.SendAsync(claim);
            Check((int)claimResponse.StatusCode == 423, "arbitrary bearer cannot bypass keepsake claim lock");

            using var testerRequest = new HttpRequestMessage(HttpMethod.Get, "/app/v1/customer/character");
            testerRequest.Headers.Add("X-Test-Role", AccountRoles.Tester);
            testerRequest.Headers.Add("X-Test-Session", "yes");
            using var testerResponse = await client.SendAsync(testerRequest);
            Check(testerResponse.IsSuccessStatusCode, "internal tester can access protected app during launch lock");

            using var rawToken = new HttpRequestMessage(HttpMethod.Post, "/v1/twin/device-token");
            rawToken.Headers.Authorization = new("Bearer", deviceCredential);
            Check((await client.SendAsync(rawToken)).IsSuccessStatusCode, "real device credential can obtain its twin source token while guests are gated");
            using var source = new HttpRequestMessage(HttpMethod.Post, "/v1/twin/ingest/test-device");
            source.Headers.Authorization = new("Bearer", sourceCredential);
            Check((await client.SendAsync(source)).IsSuccessStatusCode, "verified twin source continues scoped ingestion");
            using var wrongSource = new HttpRequestMessage(HttpMethod.Post, "/v1/twin/ingest/other-device");
            wrongSource.Headers.Authorization = new("Bearer", sourceCredential);
            Check((int)(await client.SendAsync(wrongSource)).StatusCode == 423, "twin source cannot override another instance launch gate");
            Check((int)(await client.GetAsync("/twin")).StatusCode == 423, "anonymous visitor cannot inspect legacy viewer UI");
            var session = app.Services.GetRequiredService<TwinAccess>().CreateSession(observerCredential)!;
            using var observer = new HttpRequestMessage(HttpMethod.Get, "/v1/twin/instances");
            observer.Headers.Add("Cookie", "__Host-kufr-twin=" + session.Id);
            Check((await client.SendAsync(observer)).IsSuccessStatusCode, "authorized legacy observer session retains access");
            using var ordinaryWithObserverCookie = new HttpRequestMessage(HttpMethod.Get, "/v1/twin/instances");
            ordinaryWithObserverCookie.Headers.Add("Cookie", "__Host-kufr-twin=" + session.Id);
            ordinaryWithObserverCookie.Headers.Add("X-Test-Role", AccountRoles.Customer);
            ordinaryWithObserverCookie.Headers.Add("X-Test-Session", "yes");
            Check((int)(await client.SendAsync(ordinaryWithObserverCookie)).StatusCode == 423, "ordinary account cannot replace audience with a second service cookie");
        }
        finally
        {
            await app.StopAsync();
        }
    }

    private static ClaimsPrincipal Principal(string role) => new(new ClaimsIdentity(
        [new Claim(ClaimTypes.NameIdentifier, "user-1"), new Claim(ClaimTypes.Role, role)], "test", ClaimTypes.Name, ClaimTypes.Role));

    private static void Check(bool condition, string message)
    {
        if (!condition) { throw new InvalidOperationException("Launch policy: " + message); }
    }

    private sealed class TestClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        internal void Set(DateTimeOffset value) => _now = value;
    }
}
