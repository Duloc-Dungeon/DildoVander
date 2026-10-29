using System.Net;
using System.Security.Cryptography;
using Kufr.Backend.Features.Game;
using Kufr.Backend.Features.Identity;
using Kufr.Backend.Features.Launch;
using Kufr.Backend.Features.Push;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

internal static class PushTests
{
    internal static async Task RunAsync()
    {
        Validation();
        var root = Path.Combine(Path.GetTempPath(), "dv-push-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { await VerifyWorkerAsync(root); }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, recursive: true); }
        Console.WriteLine("Push notification checks passed (offline providers).");
    }

    private static void Validation()
    {
        using var key = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var parameters = key.ExportParameters(false);
        var publicKey = new byte[] { 4 }.Concat(parameters.Q.X!).Concat(parameters.Q.Y!).ToArray();
        static string Base64(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var registration = new PushRegistration("web", Endpoint: "https://fcm.googleapis.com/fcm/send/synthetic",
            P256dh: Base64(publicKey), Auth: Base64(RandomNumberGenerator.GetBytes(16)));
        Check(PushValidation.Valid(registration), "real P256 web key accepted");
        foreach (var endpoint in new[] { "http://fcm.googleapis.com/a", "https://localhost/a", "https://127.0.0.1/a",
            "https://fcm.googleapis.com.evil.invalid/a", "https://fcm.googleapis.com:444/a", "https://user@fcm.googleapis.com/a",
            "https://169.254.169.254/a", "https://fcm.googleapis.com/a#fragment" })
        { Check(!PushValidation.Valid(registration with { Endpoint = endpoint }), "unsafe endpoint rejected"); }
        Check(!PushValidation.Valid(registration with { P256dh = Base64(new byte[65]) }), "invalid curve rejected");
        Check(!PushValidation.Valid(registration with { Auth = "short" }), "bad auth key rejected");
        Check(!PushValidation.PublicAddress(IPAddress.Parse("::ffff:127.0.0.1"))
            && !PushValidation.PublicAddress(IPAddress.Parse("10.2.3.4"))
            && !PushValidation.PublicAddress(IPAddress.Parse("fd00::1")), "private DNS answers blocked");
        Check(!PushValidation.Valid(new("android", "short")) && !PushValidation.Valid(new("ios", new string('z', 64))),
            "invalid native tokens rejected");
        Check(PushValidation.Valid(new("ios", new string('a', 64)))
            && PushValidation.Valid(new("ios", new string('a', 80))), "APNs tokens are variable-length hex");
    }

    private static async Task VerifyWorkerAsync(string root)
    {
        var clock = new Clock(DateTimeOffset.FromUnixTimeSeconds(1_800_000_000));
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Push:Enabled"] = "true", ["Push:Android:ProjectId"] = "offline-project",
            ["Push:Android:ServiceAccountPath"] = Path.Combine(root, "never-read.json")
        }).Build();
        var pushConfiguration = new PushConfiguration(configuration);
        var protection = new EphemeralDataProtectionProvider();
        var store = new PushStore(root, protection);
        var launch = new LaunchPolicyStore(root);
        var games = new GameStore(root);
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(clock);
        services.AddSingleton(launch);
        services.AddSingleton(games);
        services.AddDbContext<AccountDatabase>(options => options.UseSqlite("Data Source=" + Path.Combine(root, "accounts.sqlite")));
        services.AddScoped<PushEvents>();
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AccountDatabase>();
        await db.Database.EnsureCreatedAsync();
        var group = launch.CreateGroup("operator", new("Offline evening"), clock.GetUtcNow().ToUnixTimeMilliseconds());
        db.Users.AddRange(new AccountUser { Id = "recipient", UserName = "recipient", LaunchGroupId = group.Id },
            new AccountUser { Id = "challenger", UserName = "challenger", LaunchGroupId = group.Id },
            new AccountUser { Id = "other", UserName = "other", LaunchGroupId = group.Id });
        db.Sessions.AddRange(new AccountSession { Id = "session", UserId = "recipient", ExpiresAt = clock.Seconds + 3600 },
            new AccountSession { Id = "other-session", UserId = "other", ExpiresAt = clock.Seconds + 3600 });
        db.Roles.Add(new IdentityRole { Id = "customer", Name = AccountRoles.Customer });
        db.UserRoles.AddRange(new IdentityUserRole<string> { UserId = "recipient", RoleId = "customer" },
            new IdentityUserRole<string> { UserId = "challenger", RoleId = "customer" });
        await db.SaveChangesAsync();
        var events = scope.ServiceProvider.GetRequiredService<PushEvents>();
        var registration = new PushRegistration("android", new string('a', 80));
        var baseline = await events.ReadAsync("recipient", default);
        Check(store.Register("recipient", "session", registration, baseline), "subscription registered with baseline");
        Check(!store.Register("other", "other-session", registration, new Dictionary<string, PushMessage>()),
            "token cannot cross active account/session boundary");
        var sender = new Sender();
        var worker = new PushWorker(provider.GetRequiredService<IServiceScopeFactory>(), store, sender,
            pushConfiguration, clock, NullLogger<PushWorker>.Instance);
        await worker.RunOnceAsync(default);
        Check(sender.Messages.Count == 0, "registration does not replay old evening state");
        LaunchTestSchedule.Set(launch, group.Id, clock.Milliseconds + 2000, clock.Milliseconds);
        await worker.RunOnceAsync(default);
        Check(sender.Messages.Single().Message.Tag == "evening", "countdown change queued and delivered");
        clock.Advance(3);
        await worker.RunOnceAsync(default);
        Check(sender.Messages.Last().Message.Url == "/play" && launch.ReadGroup(group.Id)!.OpenedAt is not null,
            "forced countdown starts and notifies with no HTTP request");
        var count = sender.Messages.Count;
        var match = games.Challenge(group.Id, "challenger", "Private name", "recipient", "Other private name",
            new("recipient", "quick", Guid.NewGuid().ToString("N")), clock.Seconds);
        sender.Result = PushSendResult.Retry;
        await worker.RunOnceAsync(default);
        Check(sender.Messages.Count == count + 1 && sender.Messages.Last().Message.Tag == "challenge"
            && !sender.Messages.Last().Message.Body.Contains("Private", StringComparison.Ordinal),
            "challenge goes to recipient with generic private content");
        Check(store.Due(clock.Seconds).Count == 0, "transient retry waits for backoff");
        clock.Advance(16);
        Check(new PushStore(root, protection).Due(clock.Seconds).Single().Message.Tag == "challenge",
            "pending delivery survives store reconstruction");
        sender.Result = PushSendResult.Sent;
        await worker.RunOnceAsync(default);
        Check(sender.Messages.Count == count + 2 && store.Due(clock.Seconds).Count == 0,
            "durable transient retry delivers once due");
        await worker.RunOnceAsync(default);
        Check(sender.Messages.Count == count + 2, "unchanged durable challenge does not resend");
        count = sender.Messages.Count;
        db.Invitations.Add(new AccountInvitation
        { TokenHash = "hashed-invitation", Purpose = "invite", UserId = null, LaunchGroupId = group.Id, ExpiresAt = clock.Seconds + 200 });
        await db.SaveChangesAsync();
        await worker.RunOnceAsync(default);
        Check(sender.Messages.Count == count, "new identity invitation never pushes to an unrelated enrolled account");
        var nextGroup = launch.CreateGroup("operator", new("Next offline evening"), clock.Milliseconds);
        var recipient = await db.Users.SingleAsync(user => user.Id == "recipient");
        recipient.LaunchGroupId = nextGroup.Id;
        recipient.LaunchGroupAccessRevision++;
        recipient.LaunchGroupJoinedAt = clock.Seconds;
        await db.SaveChangesAsync();
        await worker.RunOnceAsync(default);
        Check(sender.Messages.Skip(count).Any(sent => sent.Message.Tag == "invitation"),
            "evening group assignment invites an existing enrolled identity");
        var subscription = store.Subscriptions().Single();
        store.Test(subscription, clock.Seconds);
        configuration["Push:Enabled"] = "false";
        count = sender.Messages.Count;
        await worker.RunOnceAsync(default);
        Check(sender.Messages.Count == count, "disabled provider never sends");
        clock.Advance(601);
        configuration["Push:Enabled"] = "true";
        await worker.RunOnceAsync(default);
        Check(sender.Messages.Count == count && store.Due(clock.Seconds).Count == 0, "expired notification discarded");
        store.Test(subscription, clock.Seconds);
        sender.Result = PushSendResult.InvalidSubscription;
        await worker.RunOnceAsync(default);
        Check(store.Subscriptions().Count == 0, "invalid provider token removes subscription and outbox");
        Check(store.Register("recipient", "session", registration, await events.ReadAsync("recipient", default)), "registration can recover");
        subscription = store.Subscriptions().Single();
        store.Test(subscription, clock.Seconds);
        db.Sessions.Remove(await db.Sessions.SingleAsync(session => session.Id == "session"));
        await db.SaveChangesAsync();
        count = sender.Messages.Count;
        await worker.RunOnceAsync(default);
        Check(sender.Messages.Count == count && store.Subscriptions().Count == 0, "revoked session suppresses pending send and removes token");
        Check(store.Register("other", "other-session", registration, await events.ReadAsync("other", default)), "released token can change account");
        var replacement = store.Subscriptions().Single();
        var replacementEvents = await events.ReadAsync("other", default);
        store.Observe(subscription, new Dictionary<string, PushMessage>
        { ["stale-account-event"] = new("Private old account", "Must not send", "/play", "challenge") }, clock.Seconds);
        store.Remove(subscription);
        store.Test(subscription, clock.Seconds);
        store.Observe(replacement, replacementEvents, clock.Seconds);
        Check(store.Subscriptions().Single().UserId == "other" && store.Due(clock.Seconds).Count == 0,
            "stale worker cannot replace new owner baseline, enqueue old events, or remove replacement");
        var other = await db.Users.SingleAsync(user => user.Id == "other");
        other.Disabled = true;
        await db.SaveChangesAsync();
        await worker.RunOnceAsync(default);
        Check(store.Subscriptions().Count == 0, "disabled account subscriptions cleaned");
        other.Disabled = false;
        await db.SaveChangesAsync();
        Check(store.Register("other", "other-session", registration, await events.ReadAsync("other", default)), "other account re-enables");
        subscription = store.Subscriptions().Single();
        store.Test(subscription, clock.Seconds);
        store.Test(subscription, clock.Seconds);
        count = sender.Messages.Count;
        sender.Result = PushSendResult.Sent;
        sender.OnSend = () => clock.Advance(601);
        await worker.RunOnceAsync(default);
        sender.OnSend = null;
        Check(sender.Messages.Count == count + 1 && store.Due(clock.Seconds).Count == 0,
            "slow first delivery cannot send an expired later delivery from the same batch");
        store.Test(subscription, clock.Seconds);
        count = sender.Messages.Count;
        sender.Result = PushSendResult.Retry;
        foreach (var delay in new[] { 0, 16, 31, 61, 121, 241 })
        {
            clock.Advance(delay);
            await worker.RunOnceAsync(default);
        }
        Check(sender.Messages.Count == count + 5 && store.Due(clock.Seconds).Count == 0, "transient retries are bounded to five attempts");
        other.LaunchGroupRevokedAt = clock.Seconds;
        await db.SaveChangesAsync();
        await worker.RunOnceAsync(default);
        Check(store.Subscriptions().Count == 0, "revoked membership removes subscriptions");
    }

    private static void Check(bool condition, string message)
    { if (!condition) { throw new InvalidOperationException("Push check failed: " + message); } }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
        internal long Seconds => now.ToUnixTimeSeconds();
        internal long Milliseconds => now.ToUnixTimeMilliseconds();
        internal void Advance(int seconds) => now += TimeSpan.FromSeconds(seconds);
    }

    private sealed class Sender : IPushSender
    {
        internal PushSendResult Result { get; set; } = PushSendResult.Sent;
        internal Action? OnSend { get; set; }
        internal List<(PushSubscription Subscription, PushMessage Message)> Messages { get; } = [];
        public Task<PushSendResult> SendAsync(PushSubscription subscription, PushMessage message, CancellationToken cancellationToken)
        { Messages.Add((subscription, message)); OnSend?.Invoke(); return Task.FromResult(Result); }
    }
}
