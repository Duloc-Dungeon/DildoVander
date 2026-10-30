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
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
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
            await VerifyRecruitmentScheduleAsync(Path.Combine(root, "schedule"));
            VerifyLegacyMigration(Path.Combine(root, "migration"));
            await VerifyWaitingRoomReservationAsync(Path.Combine(root, "reservation"));
            await VerifyAccessSnapshotRevalidationAsync(Path.Combine(root, "snapshot"));
            await RunAccessBoundaryAsync(root);
            Console.WriteLine("Launch schedule, recruitment isolation, atomic revision, migration and access boundary checks passed.");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, true);
        }
    }

    private static async Task VerifyRecruitmentScheduleAsync(string root)
    {
        var store = new LaunchPolicyStore(root);
        var clock = new TestClock(DateTimeOffset.FromUnixTimeMilliseconds(1000));
        var policy = new LaunchAccessService(store, clock);
        var customer = Principal(AccountRoles.Customer);
        var first = store.CreateGroup("owner", new("First evening"), 1000);
        Check(first.OpenedAt is null && first.LaunchAt is null && !policy.Evaluate(customer).Allowed,
            "new groups and unassigned customers wait by default");
        Check(policy.Evaluate(Principal(AccountRoles.Tester)).Allowed, "tester privilege is preserved");
        Expect("invalid_beta_admission", () => store.UpdateBetaAdmission("owner",
            new(1, first.Id, true, null, first.Revision, null), 1000));
        var admission = store.UpdateBetaAdmission("owner", new(1, first.Id, true, "Fiction", first.Revision, 2000), 1000);
        Check(admission.GroupLaunchAt == 2000 && admission.GroupRevision == first.Revision + 1,
            "schedule and enrollment target save together");
        Check(store.TryReadOpenBetaAdmission(out _, out var selected) && selected.Id == first.Id,
            "recruitment accepts guests while the selected group is waiting");
        using (var enrollment = store.ReserveBetaEnrollment(first.Id, admission.Revision))
        {
            Check(enrollment?.Group.Id == first.Id && enrollment.Group.OpenedAt is null,
                "new enrollment reserves the selected waiting group");
        }
        Check(!policy.Evaluate(customer, first.Id, selected).Allowed, "group waits before its absolute term");
        clock.Set(DateTimeOffset.FromUnixTimeMilliseconds(2000));
        Check(policy.Evaluate(customer, first.Id, selected) is { Allowed: true, OpenedAt: 2000 }, "server evaluation exposes the actual group opening for the Rickroll window");
        first = store.ReadGroup(first.Id)!;
        var second = store.CreateGroup("owner", new("Second evening"), 2001);
        var oldFirst = first;
        admission = store.UpdateBetaAdmission("owner",
            new(admission.Revision, second.Id, true, null, second.Revision, 9000), 2001);
        Check(store.ReadGroup(first.Id) == oldFirst && policy.Evaluate(customer, first.Id, oldFirst).Allowed
            && !policy.Evaluate(customer, second.Id, store.ReadGroup(second.Id)).Allowed,
            "scheduling a later recruitment group preserves the previously opened evening");
        Check(store.ReserveBetaEnrollment(first.Id, admission.Revision) is null,
            "old target cannot receive an enrollment after selection changes");
        using (var enrollment = store.ReserveBetaEnrollment(second.Id, admission.Revision))
        {
            Check(enrollment?.Group.Id == second.Id && enrollment.Group.LaunchAt == 9000,
                "all new enrollments reserve the new target and its future term");
        }
        var before = store.ReadBetaAdmission();
        var beforeSecond = store.ReadGroup(second.Id)!;
        Expect("launch_group_revision_conflict", () => store.UpdateBetaAdmission("owner",
            new(before.Revision, second.Id, false, null, beforeSecond.Revision - 1, 8000), 2002));
        Check(store.ReadBetaAdmission() == before && store.ReadGroup(second.Id) == beforeSecond,
            "group revision conflict rolls back recruitment and schedule together");
        Expect("beta_admission_revision_conflict", () => store.UpdateBetaAdmission("owner",
            new(before.Revision - 1, second.Id, false, null, beforeSecond.Revision, 8000), 2002));
        Check(store.ReadGroup(second.Id) == beforeSecond, "admission revision conflict does not alter the term");
        Expect("launch_group_already_open", () => store.UpdateBetaAdmission("owner",
            new(before.Revision, first.Id, true, null, first.Revision, 12000), 2002));
        Check(store.ReadBetaAdmission() == before && store.ReadGroup(first.Id) == first,
            "an opened evening cannot be rescheduled or reclosed");
        var configuration = store.Read();
        var saved = store.Update("owner", new(configuration.Revision, "UTC", false, "saved prompt"), 2003);
        Check(saved.PersonalizedLoadingPrompt == "saved prompt" && !saved.PersonalizedLoadingEnabled
            && store.ReadGroup(first.Id) == first && store.ReadGroup(second.Id) == beforeSecond,
            "personalization settings have no scheduling effect");
        Expect("launch_revision_conflict", () => store.Update("owner", new(0, "UTC", true, "other"), 2003));
        var json = System.Text.Json.JsonSerializer.Serialize(saved);
        Check(!json.Contains("Beta") && !json.Contains("Production") && !json.Contains("Teaser")
            && !json.Contains("InitialLoading"), "obsolete launch plan and manual message fields are absent from the API");
        clock.Set(DateTimeOffset.FromUnixTimeMilliseconds(9000));
        await new LaunchScheduleStartup(store, clock).StartAsync(CancellationToken.None);
        Check(store.ReadGroup(first.Id) == first && store.ReadGroup(second.Id)!.OpenedAt == 9000,
            "startup opens due groups without changing previously opened groups");
    }

    private static void VerifyLegacyMigration(string root)
    {
        var store = new LaunchPolicyStore(root);
        var first = store.CreateGroup("owner", new("Legacy open"), 1000);
        var second = store.CreateGroup("owner", new("Legacy scheduled"), 1000);
        var manual = store.CreateGroup("owner", new("Legacy manual open"), 1000);
        var draft = store.CreateGroup("owner", new("Legacy unconfirmed draft"), 1000);
        using (var connection = new SqliteConnection("Data Source=" + Path.Combine(root, "launch.sqlite")))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                ALTER TABLE launch_groups ADD COLUMN countdown_duration_seconds INTEGER NULL;
                ALTER TABLE launch_groups ADD COLUMN force_start_at_end INTEGER NOT NULL DEFAULT 0;
                ALTER TABLE launch_groups ADD COLUMN countdown_confirmed_at INTEGER NULL;
                UPDATE launch_groups SET launch_at=2000,opened_at=1500 WHERE id=$first;
                UPDATE launch_groups SET launch_at=9000 WHERE id=$second;
                UPDATE launch_groups SET opened_at=1000 WHERE id=$manual;
                UPDATE launch_groups SET countdown_duration_seconds=60 WHERE id=$draft;
                UPDATE launch_configuration SET production_enabled=1,production_launch_at=12000;
                """;
            command.Parameters.AddWithValue("$first", first.Id);
            command.Parameters.AddWithValue("$second", second.Id);
            command.Parameters.AddWithValue("$manual", manual.Id);
            command.Parameters.AddWithValue("$draft", draft.Id);
            command.ExecuteNonQuery();
        }
        store = new LaunchPolicyStore(root);
        store.OpenDueGroups(3000);
        Check(store.ReadGroup(first.Id)!.OpenedAt == 1500 && store.ReadGroup(first.Id)!.LaunchAt == 2000
            && store.ReadGroup(second.Id)!.LaunchAt == 9000 && store.ReadGroup(second.Id)!.OpenedAt is null,
            "legacy opened state and absolute dates survive initialization regardless of obsolete global plan");
        Check(store.ReadGroup(draft.Id)!.OpenedAt is null && store.ReadGroup(draft.Id)!.LaunchAt is null,
            "historical unconfirmed duration drafts cannot open a group without an absolute date");
        var admission = store.UpdateBetaAdmission("owner",
            new(store.ReadBetaAdmission().Revision, manual.Id, true, null, manual.Revision, null), 3000);
        Check(admission.RecruitmentOpen && store.ReadGroup(manual.Id)!.OpenedAt == 1000,
            "historical manually opened groups can accept recruitment without adding or resetting a date");
    }

    private static async Task VerifyWaitingRoomReservationAsync(string root)
    {
        var store = new LaunchPolicyStore(root);
        var source = store.CreateGroup("owner", new("Source"), 1000);
        var target = store.CreateGroup("owner", new("Target"), 1000);
        LaunchTestSchedule.Set(store, source.Id, 5000, 1000);
        var reservation = store.ReserveWaitingRoomMove(source.Id, target.Id);
        Check(reservation is not null && !reservation.SourceStarted(1999), "waiting room reserves pending source state");
        var opening = Task.Run(() => LaunchTestSchedule.Set(store, source.Id, 2000, 2000));
        await Task.Delay(100);
        Check(!opening.IsCompleted, "schedule cannot change during reserved membership movement");
        reservation!.Dispose();
        await opening;
        Check(store.ReadGroup(source.Id)!.OpenedAt == 2000, "schedule proceeds after the reservation releases");
    }

    private static void Expect(string code, Action action)
    {
        try { action(); throw new InvalidOperationException("Expected " + code); }
        catch (LaunchConfigurationException error) when (error.Code == code) { }
    }

    private static async Task VerifyAccessSnapshotRevalidationAsync(string root)
    {
        Directory.CreateDirectory(root);
        var accountPath = Path.Combine(root, "accounts.sqlite");
        var accountConnectionString = new SqliteConnectionStringBuilder { DataSource = accountPath }.ToString();
        using (var connection = new SqliteConnection(accountConnectionString))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE AspNetUsers (
                    Id TEXT NOT NULL PRIMARY KEY, LaunchGroupId TEXT NULL,
                    LaunchGroupAccessRevision INTEGER NOT NULL, LaunchGroupRevokedAt INTEGER NULL);
                INSERT INTO AspNetUsers(Id,LaunchGroupId,LaunchGroupAccessRevision)
                    VALUES('source-user','source-group',1),('global-user',NULL,0);
                """;
            command.ExecuteNonQuery();
        }
        var services = new ServiceCollection();
        services.Configure<IdentityOptions>(options =>
            options.Stores.SchemaVersion = IdentitySchemaVersions.Version3);
        services.AddDbContext<AccountDatabase>(options => options.UseSqlite(accountConnectionString));
        using var provider = services.BuildServiceProvider();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();
        var launch = new LaunchPolicyStore(Path.Combine(root, "launch"));
        var source = launch.CreateGroup("owner", new("Source"), 1000);
        var target = launch.CreateGroup("owner", new("Target"), 1000);
        using (var connection = new SqliteConnection(accountConnectionString))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE AspNetUsers SET LaunchGroupId=$source WHERE Id='source-user'";
            command.Parameters.AddWithValue("$source", source.Id);
            command.ExecuteNonQuery();
        }
        LaunchTestSchedule.Set(launch, source.Id, 5000, 1000);
        using var clock = new BlockingClock(DateTimeOffset.FromUnixTimeMilliseconds(3000));
        var access = new LaunchAccessService(launch, clock, scopeFactory);

        var sourceEvaluation = Task.Run(() => access.EvaluateAsync(Principal(AccountRoles.Customer, "source-user")));
        await clock.WaitUntilBlockedAsync();
        MoveIdentity("source-user", target.Id);
        LaunchTestSchedule.Set(launch, source.Id, 3000, 3000);
        clock.Release();
        var movedSource = await sourceEvaluation.WaitAsync(TimeSpan.FromSeconds(5));
        Check(!movedSource.Allowed && movedSource.LaunchAt is null,
            "revalidation denies a former member when its old source group opens during access evaluation");

        var globalClock = new BlockingClock(DateTimeOffset.FromUnixTimeMilliseconds(3000));
        var globalAccess = new LaunchAccessService(launch, globalClock, scopeFactory);
        var globalEvaluation = Task.Run(() => globalAccess.EvaluateAsync(Principal(AccountRoles.Customer, "global-user")));
        await globalClock.WaitUntilBlockedAsync();
        MoveIdentity("global-user", target.Id);
        globalClock.Release();
        var movedGlobal = await globalEvaluation.WaitAsync(TimeSpan.FromSeconds(5));
        Check(!movedGlobal.Allowed && movedGlobal.LaunchAt is null,
            "revalidation denies a globally unlocked account whose membership changes during evaluation");

        void MoveIdentity(string userId, string targetGroupId)
        {
            using var connection = new SqliteConnection(accountConnectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE AspNetUsers SET LaunchGroupId=$group,LaunchGroupAccessRevision=LaunchGroupAccessRevision+1 WHERE Id=$id";
            command.Parameters.AddWithValue("$group", targetGroupId);
            command.Parameters.AddWithValue("$id", userId);
            Check(command.ExecuteNonQuery() == 1, "race fixture updates an existing membership");
        }
    }

    private static async Task RunAccessBoundaryAsync(string root)
    {
        var gateRoot = Path.Combine(root, "gate");
        Directory.CreateDirectory(gateRoot);
        var store = new LaunchPolicyStore(gateRoot);
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
        app.MapGet("/app/v1/customer/wand/workshop/sessions/{id}/live", () => Results.Unauthorized());
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
            var workshopLive = "/app/v1/customer/wand/workshop/sessions/" + new string('b', 32) + "/live";
            Check((await client.GetAsync(workshopLive)).StatusCode == HttpStatusCode.Unauthorized,
                "cookie-less workshop Live reaches ticket authentication while launch is locked");
            foreach (var blocked in new[] { workshopLive.Replace(new string('b', 32), "invalid"),
                workshopLive + "/extra", workshopLive.Replace("/live", "/ticket") })
            {
                Check((int)(await client.GetAsync(blocked)).StatusCode == 423,
                    "anonymous workshop exception is limited to the exact Live route with a valid operation ID");
            }
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
            Check(signupResponse.IsSuccessStatusCode,
                "only the explicit customer enrollment surface passes the launch gate to its own closed-recruitment check");
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

    private static ClaimsPrincipal Principal(string role) => Principal(role, "user-1");

    private static ClaimsPrincipal Principal(string role, string userId) => new(new ClaimsIdentity(
        [new Claim(ClaimTypes.NameIdentifier, userId), new Claim(ClaimTypes.Role, role)], "test", ClaimTypes.Name, ClaimTypes.Role));

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

    private sealed class BlockingClock(DateTimeOffset now) : TimeProvider, IDisposable
    {
        private readonly ManualResetEventSlim _release = new(false);
        private readonly TaskCompletionSource _blocked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _blockNext = 1;

        public override DateTimeOffset GetUtcNow()
        {
            if (Interlocked.Exchange(ref _blockNext, 0) == 1)
            {
                _blocked.TrySetResult();
                _release.Wait();
            }
            return now;
        }

        internal Task WaitUntilBlockedAsync() => _blocked.Task.WaitAsync(TimeSpan.FromSeconds(5));
        internal void Release() => _release.Set();
        public void Dispose() => _release.Dispose();
    }
}
