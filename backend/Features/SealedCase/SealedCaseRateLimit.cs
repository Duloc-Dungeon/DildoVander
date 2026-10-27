using System.Security.Claims;
using System.Threading.RateLimiting;
using Kufr.Backend.Features.Identity;
using Microsoft.AspNetCore.RateLimiting;

namespace Kufr.Backend.Features.SealedCase;

internal static class SealedCaseRateLimit
{
    internal const string Policy = "sealed-case";

    internal static IServiceCollection AddSealedCaseRateLimits(this IServiceCollection services)
    {
        services.AddRateLimiter(options => options.AddPolicy(Policy, context =>
        {
            var userId = context.User.Identity?.IsAuthenticated == true
                && context.User.HasClaim(c => c.Type == AccountIdentity.SessionClaim)
                ? context.User.FindFirstValue(ClaimTypes.NameIdentifier) : null;
            var principal = userId is null ? $"ip:{context.Connection.RemoteIpAddress}" : $"account:{userId}";
            var read = HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method);
            return RateLimitPartition.GetFixedWindowLimiter($"{principal}:{(read ? "read" : "write")}",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = read ? 120 : 40, Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0, AutoReplenishment = true
                });
        }));
        return services;
    }

    // Resolve the real account before partitioning, not the venue's shared IP.
    // AccountIdentity still owns session validation, origin and mutation CSRF.
    internal static void UseSealedCaseAuthentication(this WebApplication app) =>
        app.UseWhen(context => context.Request.Path.StartsWithSegments("/app/v1/sealed-cases"),
            branch => branch.UseAuthentication());
}
