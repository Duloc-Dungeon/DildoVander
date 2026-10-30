using System.Security.Claims;
using Kufr.Backend.Features.Access;
using Kufr.Backend.Features.Game;
using Kufr.Backend.Features.Identity;
using Kufr.Backend.Features.Launch;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Kufr.Backend.Features.Push;

internal sealed class PushEvents(AccountDatabase db, LaunchPolicyStore launch, TimeProvider clock, GameStore? games = null)
{
    internal async Task<bool> AuthorizedAsync(PushSubscription subscription, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow().ToUnixTimeSeconds();
        return await db.Sessions.AsNoTracking().AnyAsync(session => session.Id == subscription.SessionId
                && session.UserId == subscription.UserId && session.ExpiresAt > now, cancellationToken)
            && await db.Users.AsNoTracking().AnyAsync(user => user.Id == subscription.UserId
                && !user.Disabled && user.LaunchGroupRevokedAt == null, cancellationToken)
            && !await db.DeletionRequests.AnyAsync(request => request.UserId == subscription.UserId, cancellationToken);
    }

    internal async Task<IReadOnlyDictionary<string, PushMessage>> ReadAsync(string userId, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, PushMessage>(StringComparer.Ordinal);
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(user => user.Id == userId, cancellationToken);
        if (user is null || user.Disabled || user.LaunchGroupRevokedAt is not null) { return result; }
        // New invitation links have no account/device identity. Push starts only after
        // enrollment and explicit consent; existing guests are invited by group assignment.
        if (user.LaunchGroupId is not { } groupId) { return result; }
        result[$"membership:{groupId}:{user.LaunchGroupAccessRevision}"] =
            Message("Tvoje pozvánka na večer byla aktualizována.", "/launch", "invitation");
        var group = launch.OpenDueGroup(groupId, clock.GetUtcNow().ToUnixTimeMilliseconds());
        if (group is null) { return result; }
        // A forced countdown must progress even if nobody has the app open.
        var started = group.OpenedAt is not null;
        var key = $"evening:{groupId}:{group.Revision}:{group.LaunchAt}:{started}";
        result[key] = started
            ? Message("Večer právě začíná. Opona jde nahoru.", "/play", "evening")
            : Message(group.LaunchAt is null ? "Přípravy večera se změnily." : "Odpočet večera byl aktualizován.", "/launch", "evening");
        var customer = await (from membership in db.UserRoles
            join role in db.Roles on membership.RoleId equals role.Id
            where membership.UserId == userId && role.Name == AccountRoles.Customer
            select membership.UserId).AnyAsync(cancellationToken);
        if (started && games is not null && customer)
        {
            foreach (var match in games.IncomingPushChallenges(groupId, userId))
            {
                var eligible = await (from peer in db.Users.AsNoTracking()
                    join membership in db.UserRoles on peer.Id equals membership.UserId
                    join role in db.Roles on membership.RoleId equals role.Id
                    where peer.Id == match.ChallengerId && role.Name == AccountRoles.Customer
                        && peer.LaunchGroupId == groupId && peer.LaunchGroupRevokedAt == null && !peer.Disabled
                        && !db.DeletionRequests.Any(deletion => deletion.UserId == peer.Id)
                    select peer.Id).AnyAsync(cancellationToken);
                if (eligible)
                { result["challenge:" + match.Id] = Message("Někdo tě vyzval ke hře. Tah je na tobě.", "/play", "challenge"); }
            }
        }
        return result;
    }

    private static PushMessage Message(string body, string url, string tag) => new("Wand Night Stand", body, url, tag);
}

internal sealed class PushWorker(IServiceScopeFactory scopes, PushStore store, IPushSender sender,
    PushConfiguration configuration, TimeProvider clock, ILogger<PushWorker> logger) : BackgroundService
{
    internal async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var events = scope.ServiceProvider.GetRequiredService<PushEvents>();
        var now = clock.GetUtcNow().ToUnixTimeSeconds();
        foreach (var subscription in store.Subscriptions())
        {
            if (!await events.AuthorizedAsync(subscription, cancellationToken))
            { store.Remove(subscription); continue; }
            if (!configuration.Enabled(subscription.Registration.Platform)) { continue; }
            store.Observe(subscription, await events.ReadAsync(subscription.UserId, cancellationToken), now);
        }
        foreach (var delivery in store.Due(now))
        {
            now = clock.GetUtcNow().ToUnixTimeSeconds();
            if (delivery.ExpiresAt <= now)
            { store.Complete(delivery, PushSendResult.Rejected, now); continue; }
            var subscription = delivery.Subscription;
            if (!await events.AuthorizedAsync(subscription, cancellationToken))
            { store.Remove(subscription); continue; }
            if (!configuration.Enabled(subscription.Registration.Platform)) { continue; }
            var current = await events.ReadAsync(subscription.UserId, cancellationToken);
            if (!delivery.EventKey.StartsWith("test:", StringComparison.Ordinal) && !current.ContainsKey(delivery.EventKey))
            { store.Complete(delivery, PushSendResult.Rejected, now); continue; }
            // Recheck after building the projection, immediately before handing data to a provider.
            if (!store.IsCurrent(subscription)) { continue; }
            if (!await events.AuthorizedAsync(subscription, cancellationToken))
            { store.Remove(subscription); continue; }
            now = clock.GetUtcNow().ToUnixTimeSeconds();
            if (delivery.ExpiresAt <= now)
            { store.Complete(delivery, PushSendResult.Rejected, now); continue; }
            PushSendResult result;
            try { result = await sender.SendAsync(subscription, delivery.Message, cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception error) when (error is HttpRequestException or OperationCanceledException)
            { result = PushSendResult.Retry; }
            catch (Exception error)
            {
                logger.LogWarning("Push delivery rejected ({FailureType}).", error.GetType().Name);
                result = PushSendResult.Rejected;
            }
            store.Complete(delivery, result, clock.GetUtcNow().ToUnixTimeSeconds());
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15), clock);
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunOnceAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error)
            {
                // Provider exceptions can contain endpoint/token material; log only the type.
                logger.LogWarning("Push processing failed ({FailureType}).", error.GetType().Name);
            }
            try { if (!await timer.WaitForNextTickAsync(stoppingToken)) { break; } }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}

internal static class PushFeature
{
    internal static void AddPushNotifications(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton(new PushConfiguration(configuration));
        var accounts = AccountConfiguration.Read(configuration);
        if (!accounts.Enabled) { return; }
        services.AddSingleton(provider => new PushStore(accounts.DataRoot,
            provider.GetRequiredService<Microsoft.AspNetCore.DataProtection.IDataProtectionProvider>()));
        services.AddScoped<PushEvents>();
        services.TryAddSingleton<IPushSender, PushSender>();
        services.AddHostedService<PushWorker>();
    }

    internal static void MapPushEndpoints(this WebApplication app)
    {
        if (!app.Services.GetRequiredService<AccountConfiguration>().Enabled) { return; }
        var group = app.MapGroup("/app/v1/push").RequireAuthorization().RequireRateLimiting("account");
        group.MapGet("", (HttpContext context, PushStore store, PushConfiguration configuration) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var session = context.User.FindFirstValue(AccountIdentity.SessionClaim);
            if (session is null) { return ApiProblem.Create(context, 401, "account_session_required"); }
            return Results.Ok(new
            {
                web = configuration.Enabled("web"), android = configuration.Enabled("android"),
                ios = configuration.Enabled("ios"), publicKey = configuration.Enabled("web") ? configuration.PublicKey : null,
                enabled = store.Subscriptions(sessionId: session).Count != 0
            });
        });
        group.MapPut("", async (PushRegistration registration, HttpContext context,
            PushStore store, PushConfiguration configuration, PushEvents events) =>
        {
            var user = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
            var session = context.User.FindFirstValue(AccountIdentity.SessionClaim);
            if (user is null || session is null) { return ApiProblem.Create(context, 401, "account_session_required"); }
            if (!PushValidation.Valid(registration)) { return ApiProblem.Create(context, 400, "invalid_push_subscription"); }
            if (!configuration.Enabled(registration.Platform)) { return ApiProblem.Create(context, 503, "push_not_configured"); }
            if (!await events.AuthorizedAsync(new("", user, session, registration), context.RequestAborted))
            { return ApiProblem.Create(context, 403, "push_account_unavailable"); }
            var previous = store.FindRegistration(registration);
            if (previous is not null && !await events.AuthorizedAsync(previous, context.RequestAborted))
            { store.Remove(previous); }
            var baseline = await events.ReadAsync(user, context.RequestAborted);
            return store.Register(user, session, registration, baseline)
                ? Results.NoContent() : ApiProblem.Create(context, 409, "push_subscription_in_use");
        });
        group.MapDelete("", (HttpContext context, PushStore store) =>
        {
            var session = context.User.FindFirstValue(AccountIdentity.SessionClaim);
            if (session is null) { return ApiProblem.Create(context, 401, "account_session_required"); }
            store.RemoveSession(session);
            return Results.NoContent();
        });
        group.MapPost("/test", (HttpContext context, PushStore store, PushConfiguration configuration, TimeProvider clock) =>
        {
            var session = context.User.FindFirstValue(AccountIdentity.SessionClaim);
            if (session is null) { return ApiProblem.Create(context, 401, "account_session_required"); }
            var subscriptions = store.Subscriptions(sessionId: session).Where(subscription =>
                configuration.Enabled(subscription.Registration.Platform)).ToArray();
            if (subscriptions.Length == 0) { return ApiProblem.Create(context, 409, "push_subscription_required"); }
            foreach (var subscription in subscriptions) { store.Test(subscription, clock.GetUtcNow().ToUnixTimeSeconds()); }
            return Results.Accepted();
        });
    }
}
