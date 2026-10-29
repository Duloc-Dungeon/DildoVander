using System.Security.Claims;
using Kufr.Backend.Features.Access;
using Kufr.Backend.Features.Identity;
using Kufr.Backend.Features.Launch;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace Kufr.Backend.Features.Game;

internal sealed class GameLiveHub(AccountDatabase database, LaunchPolicyStore launch,
    LaunchAccessService access, IConfiguration configuration) : Hub
{
    internal const string Path = "/app/v1/game/live";

    public override async Task OnConnectedAsync()
    {
        var context = Context.GetHttpContext();
        var user = Context.User;
        var userId = user?.FindFirstValue(ClaimTypes.NameIdentifier);
        if (context is null || userId is null
            || !user!.HasClaim(claim => claim.Type == AccountIdentity.SessionClaim)
            || !context.Request.IsHttps
            || context.WebSockets.IsWebSocketRequest
                && !AllowedOrigin(context.Request.Headers.Origin.ToString(), configuration)
            || !context.WebSockets.IsWebSocketRequest
                && context.Request.Headers.Origin.Count > 0
                && !AllowedOrigin(context.Request.Headers.Origin.ToString(), configuration))
        {
            Context.Abort();
            return;
        }

        await OwnerPlayerGroups.EnsureSelectionAsync(context, database, launch);
        var account = await database.Users.AsNoTracking().SingleOrDefaultAsync(candidate => candidate.Id == userId
            && !candidate.Disabled && candidate.LaunchGroupId != null && candidate.LaunchGroupRevokedAt == null
            && !database.DeletionRequests.Any(deletion => deletion.UserId == userId), Context.ConnectionAborted);
        if (account?.LaunchGroupId is not { } groupId || !AccountRoles.IsPlayer(user))
        {
            Context.Abort();
            return;
        }

        var settings = launch.ReadGroup(groupId);
        var permission = await access.EvaluateAsync(user);
        if (settings?.OpenedAt is null || !permission.Allowed)
        {
            Context.Abort();
            return;
        }

        await base.OnConnectedAsync();
    }

    private static bool AllowedOrigin(string origin, IConfiguration configuration)
    {
        var allowed = configuration.GetSection("ApplicationIdentity:AllowedOrigins").Get<string[]>() ?? [];
        return allowed.Contains(origin, StringComparer.Ordinal);
    }
}

internal sealed class GameChangedEndpointFilter(IHubContext<GameLiveHub> hub, ILogger<GameChangedEndpointFilter> logger)
    : IEndpointFilter
{
    private static readonly string[] GroupKeys = ["dv-game-group", "team-group", "boss-group", "progress-group"];

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext invocation, EndpointFilterDelegate next)
    {
        var result = await next(invocation);
        var context = invocation.HttpContext;
        // Narration reads an existing revision; it cannot change gameplay.
        // Broadcasting it as a move would make every reply trigger another refresh.
        if (context.Request.Path.StartsWithSegments("/app/v1/game/boss/attempts")
            && (context.Request.Path.Value?.EndsWith("/narration", StringComparison.Ordinal) == true
                || context.Request.Path.Value?.EndsWith("/audio", StringComparison.Ordinal) == true))
        { return result; }
        if (!HttpMethods.IsPost(context.Request.Method)
            || result is not IStatusCodeHttpResult { StatusCode: >= 200 and < 300 })
        {
            return result;
        }

        var groupId = GroupKeys.Select(key => context.Items.TryGetValue(key, out var value) ? value as string : null)
            .FirstOrDefault(value => value is not null);
        if (groupId is null)
        {
            return result;
        }

        try
        {
            var database = context.RequestServices.GetRequiredService<AccountDatabase>();
            var actorId = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
            var affectedIds = result is IValueHttpResult { Value: GameMatchView match }
                ? new[] { actorId, match.Opponent.Id }
                : result is IValueHttpResult { Value: GameTeamView team }
                    ? team.TeamMembers.Concat(team.Opponents).Append(actorId).ToArray()
                    : [actorId];
            var affected = affectedIds.Where(id => id is not null).Distinct(StringComparer.Ordinal).ToArray();
            var recipients = await (from user in database.Users.AsNoTracking()
                where affected.Contains(user.Id) && user.LaunchGroupId == groupId
                    && user.LaunchGroupRevokedAt == null && !user.Disabled
                    && !database.DeletionRequests.Any(deletion => deletion.UserId == user.Id)
                    && database.UserRoles.Any(membership => membership.UserId == user.Id
                        && database.Roles.Any(role => role.Id == membership.RoleId
                            && (role.Name == AccountRoles.Customer || role.Name == AccountRoles.Owner)))
                select user.Id).ToArrayAsync(CancellationToken.None);

            if (recipients.Length > 0)
            {
                await hub.Clients.Users(recipients).SendAsync("gameChanged", cancellationToken: CancellationToken.None);
            }
        }
        catch (Exception error)
        {
            logger.LogWarning("Could not publish a game state change notification ({ErrorType}).", error.GetType().Name);
        }

        return result;
    }
}

internal static class GameLiveEndpoints
{
    internal static IServiceCollection AddGameLive(this IServiceCollection services)
    {
        services.AddSignalR();
        services.AddScoped<GameChangedEndpointFilter>();
        return services;
    }

    internal static void MapGameLive(this WebApplication app)
    {
        if (!app.Services.GetRequiredService<AccountConfiguration>().Enabled)
        {
            return;
        }

        app.MapHub<GameLiveHub>(GameLiveHub.Path, options =>
        {
            options.Transports = HttpTransportType.WebSockets | HttpTransportType.LongPolling;
            options.LongPolling.PollTimeout = TimeSpan.FromSeconds(5);
            options.CloseOnAuthenticationExpiration = true;
        })
            .RequireAuthorization()
            .RequireRateLimiting(GameRateLimit.Policy);
    }
}
