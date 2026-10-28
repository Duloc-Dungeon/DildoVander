using System.Security.Claims;
using Kufr.Backend.Features.Access;
using System.Security.Cryptography;
using Kufr.Backend.Features.Customers;
using Kufr.Backend.Features.Game;
using Kufr.Backend.Features.Identity;
using Microsoft.EntityFrameworkCore;

namespace Kufr.Backend.Features.Wands;

internal sealed record MobileWandStart(string OperationId, bool Upgrade = false);
internal sealed record MobileWandTicketRequest(string SocketOrigin);

internal static class MobileWandEndpoints
{
    internal static void MapMobileWands(this WebApplication app)
    {
        if (!app.Services.GetRequiredService<AccountConfiguration>().Enabled) return;
        var group = app.MapGroup("/app/v1/customer/wand").RequireAuthorization().RequireRateLimiting("account");
        group.AddEndpointFilter(async (invocation, next) =>
        {
            try
            {
                var h = invocation.HttpContext;
                var owner = h.User.FindFirstValue(ClaimTypes.NameIdentifier);
                if (owner is null || !h.User.HasClaim(c => c.Type == AccountIdentity.SessionClaim))
                    throw new WandGenerationException("account_required", 401);
                var db = h.RequestServices.GetRequiredService<AccountDatabase>();
                var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == owner && !u.Disabled
                    && !db.DeletionRequests.Any(d => d.UserId == owner), h.RequestAborted);
                if (user is null || !AccountAiConsent.HasConsent(user))
                    throw new WandGenerationException("openai_consent_required", 403);
                h.Response.Headers.CacheControl = "no-store";
                return await next(invocation);
            }
            catch (WandGenerationException e) { return ApiProblem.Create(invocation.HttpContext, e.Status, e.Code); }
            catch (Kufr.Backend.Features.Keepsakes.KeepsakeException e) { return ApiProblem.Create(invocation.HttpContext, e.Status, e.Code); }
            catch (GameFault e) { return ApiProblem.Create(invocation.HttpContext, e.Status, e.Code); }
        });
        group.MapGet("", async (HttpContext h, CharacterProfileStore profiles, WandGenerationStore wands) =>
        {
            var owner = h.User.FindFirstValue(ClaimTypes.NameIdentifier)!;
            var profile = await profiles.ReadAsync(owner, h.RequestAborted);
            var wand = profile is null ? null : MobileWandSessions.Current(wands, owner, profile.ProfileId, profile.Revision);
            return Results.Ok(new { hasCharacter = profile is not null, wand,
                characterDisplayName = profile?.Label is null ? null : profile.DisplayName, tier = profile is null ? 0
                : Math.Max(wand is null ? 0 : 1, wands.MobileTier(owner, profile.ProfileId, profile.Revision)) });
        });
        group.MapMobileWandWorkshop();
        group.MapPost("/sessions", (HttpContext http) => ApiProblem.Create(http, 410, "wand_workshop_required"));
        group.MapPost("/sessions/{id}/ticket", (HttpContext http) => ApiProblem.Create(http, 410, "wand_workshop_required"));
        app.MapGet("/app/v1/customer/wand/sessions/{id}/live", (HttpContext http) =>
            ApiProblem.Create(http, 410, "wand_workshop_required")).RequireRateLimiting("account");
    }
}
