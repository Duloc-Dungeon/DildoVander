using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Kufr.Backend.Features.Identity;
using Kufr.Backend.Features.Launch;
using Kufr.Backend.Features.Push;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

internal static partial class AccountIdentityTests
{
    internal static async Task RunPushHttpAsync()
    {
        IConfiguration? configuration = null;
        await using var fixture = await Fixture.StartAsync(
            configureConfiguration: config =>
            {
                configuration = config;
                config["Push:Enabled"] = "true";
                config["Push:Android:ProjectId"] = "offline-project";
                config["Push:Android:ServiceAccountPath"] = "unused-offline.json";
            },
            configureServices: services =>
            {
                services.AddSingleton<IPushSender, OfflinePushSender>();
                services.AddPushNotifications(configuration!);
            },
            configureEndpoints: app => app.MapPushEndpoints());
        using var anonymous = fixture.Client();
        Check((await anonymous.GetAsync("/app/v1/push")).StatusCode == HttpStatusCode.Unauthorized,
            "anonymous cannot inspect push subscriptions");
        using var owner = fixture.Client();
        using var ownerKey = new VirtualPasskey();
        await EnrollAsync(owner, ownerKey, Fixture.Bootstrap, "Push owner");
        var identity = await CsrfAsync(owner);
        var userId = identity.GetProperty("account").GetProperty("id").GetString()!;
        var status = await JsonAsync(await owner.GetAsync("/app/v1/push"));
        Check(status.GetProperty("android").GetBoolean() && !status.GetProperty("web").GetBoolean()
            && !status.GetProperty("enabled").GetBoolean(), "capabilities match configured providers");
        var registration = new PushRegistration("android", new string('a', 80));
        owner.DefaultRequestHeaders.Remove("X-DV-CSRF");
        Check((await owner.PutAsJsonAsync("/app/v1/push", registration)).StatusCode == HttpStatusCode.BadRequest,
            "registration requires CSRF");
        await CsrfAsync(owner);
        owner.DefaultRequestHeaders.Remove("Origin");
        owner.DefaultRequestHeaders.Add("Origin", "https://attacker.invalid");
        Check((await owner.PutAsJsonAsync("/app/v1/push", registration)).StatusCode == HttpStatusCode.Forbidden,
            "registration requires same configured origin");
        owner.DefaultRequestHeaders.Remove("Origin");
        owner.DefaultRequestHeaders.Add("Origin", "https://localhost");
        Check((await owner.PutAsJsonAsync("/app/v1/push", new PushRegistration("ios", "invalid"))).StatusCode == HttpStatusCode.BadRequest,
            "invalid native token rejected before persistence");
        Check((await owner.PutAsJsonAsync("/app/v1/push", registration)).StatusCode == HttpStatusCode.NoContent,
            "authenticated session registers");
        Check((await owner.PostAsJsonAsync("/app/v1/push/test", new { userId = "arbitrary-user", body = "arbitrary-payload" })).StatusCode
            == HttpStatusCode.Accepted, "test request queues fixed self message");
        var store = fixture.App.Services.GetRequiredService<PushStore>();
        var deliveries = store.Due(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        Check(deliveries.Count == 1 && deliveries[0].Subscription.UserId == userId
            && !deliveries[0].Message.Body.Contains("arbitrary", StringComparison.Ordinal), "test cannot select recipient or payload");
        using var secondSession = fixture.Client();
        await SignInAsync(secondSession, ownerKey);
        await CsrfAsync(secondSession);
        Check((await secondSession.PutAsJsonAsync("/app/v1/push", registration)).StatusCode == HttpStatusCode.Conflict,
            "another active session cannot take token");
        var secondStatus = await JsonAsync(await secondSession.GetAsync("/app/v1/push"));
        Check(!secondStatus.GetProperty("enabled").GetBoolean(), "session cannot observe another session subscription state");
        Check((await secondSession.DeleteAsync("/app/v1/push")).StatusCode == HttpStatusCode.NoContent
            && store.Subscriptions().Count == 1, "delete affects current session only");
        await SeedAsync(fixture.App, async db =>
        {
            var firstSession = store.Subscriptions().Single().SessionId;
            var session = await db.Sessions.FindAsync(firstSession);
            db.Sessions.Remove(session!);
            await db.SaveChangesAsync();
        });
        Check((await secondSession.PutAsJsonAsync("/app/v1/push", registration)).StatusCode == HttpStatusCode.NoContent,
            "revoked prior session token reclaimed immediately");
        Check((await secondSession.DeleteAsync("/app/v1/push")).StatusCode == HttpStatusCode.NoContent
            && store.Subscriptions().Count == 0 && store.Due(DateTimeOffset.UtcNow.ToUnixTimeSeconds()).Count == 0,
            "unsubscribe removes pending outbox too");
        configuration!["Push:Enabled"] = "false";
        Check((await secondSession.PutAsJsonAsync("/app/v1/push", registration)).StatusCode == HttpStatusCode.ServiceUnavailable,
            "missing/disabled provider rejects registration");
        Console.WriteLine("Push HTTPS checks passed (identity, origin, CSRF, session isolation, reclaim, fixed self-test).");
    }

    private sealed class OfflinePushSender : IPushSender
    {
        public Task<PushSendResult> SendAsync(PushSubscription subscription, PushMessage message, CancellationToken cancellationToken)
            => Task.FromResult(PushSendResult.Sent);
    }
}
