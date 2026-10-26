using System.Security.Claims;
using System.Threading.RateLimiting;
using Kufr.Backend.Features.Identity;
using Microsoft.AspNetCore.RateLimiting;

namespace Kufr.Backend.Features.Game;

internal static class GameRateLimit
{
    internal const string Policy = "mobile-game";

    internal static IServiceCollection AddGameRateLimits(this IServiceCollection services)
    {
        services.AddRateLimiter(options => options.AddPolicy(Policy, context =>
        {
            var userId = context.User.Identity?.IsAuthenticated == true
                && context.User.HasClaim(claim => claim.Type == AccountIdentity.SessionClaim)
                ? context.User.FindFirstValue(ClaimTypes.NameIdentifier) : null;
            var principal = userId is null ? $"ip:{context.Connection.RemoteIpAddress}" : $"account:{userId}";
            var read = HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method);
            return RateLimitPartition.GetFixedWindowLimiter($"{principal}:{(read ? "read" : "write")}",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = read ? 120 : 40, Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0, AutoReplenishment = true,
                });
        }));
        return services;
    }

    // Account identity normally runs after the shared IP limiter. Authenticate
    // game requests first so each guest at the same venue gets an independent,
    // bounded polling/command allowance. The normal middleware still owns CSRF.
    internal static void UseMobileGameAuthentication(this WebApplication app) =>
        app.UseWhen(context => context.Request.Path.StartsWithSegments("/app/v1/game"),
            branch => branch.UseAuthentication());
}
