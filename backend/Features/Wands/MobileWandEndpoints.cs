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
            catch (GameFault e) { return ApiProblem.Create(invocation.HttpContext, e.Status, e.Code); }
        });
        group.MapGet("", async (HttpContext h, CharacterProfileStore profiles, WandGenerationStore wands) =>
        {
            var owner = h.User.FindFirstValue(ClaimTypes.NameIdentifier)!;
            var profile = await profiles.ReadAsync(owner, h.RequestAborted);
            var wand = profile is null ? null : MobileWandSessions.Current(wands, owner, profile.ProfileId, profile.Revision);
            return Results.Ok(new { hasCharacter = profile is not null, wand, tier = profile is null ? 0
                : Math.Max(wand is null ? 0 : 1, wands.MobileTier(owner, profile.ProfileId, profile.Revision)) });
        });
        group.MapPost("/sessions", async (MobileWandStart request, HttpContext h, CharacterProfileStore profiles,
            WandGenerationStore wands, GameStore games) =>
        {
            if (!Guid.TryParseExact(request.OperationId, "N", out var op) || op == Guid.Empty)
                throw new WandGenerationException("invalid_wand_operation", 400);
            var owner = h.User.FindFirstValue(ClaimTypes.NameIdentifier)!;
            var profile = await profiles.ReadAsync(owner, h.RequestAborted)
                ?? throw new WandGenerationException("character_required", 409);
            var captured = await profiles.CaptureAsync(owner, profile.ProfileId, profile.Revision, h.RequestAborted)
                ?? throw new WandGenerationException("wand_profile_revision_mismatch");
            var current = MobileWandSessions.Current(wands, owner, profile.ProfileId, profile.Revision);
            var tier = request.Upgrade ? 2 : 1;
            var existing = wands.MobileOperation(owner, profile.ProfileId, profile.Revision, tier);
            if (wands.HasOperation(owner, request.OperationId) && wands.MobileOperationTier(owner, request.OperationId) != tier)
                throw new WandGenerationException("wand_operation_conflict");
            if (existing is null)
            {
                if (request.Upgrade)
                {
                    var groupId = await GameBossEndpoints.ParticipantGroupAsync(h);
                    if (current is null || games.Stats(groupId, owner, "").Xp < 30)
                        throw new WandGenerationException("wand_upgrade_locked", 409);
                    if (wands.MobileTier(owner, profile.ProfileId, profile.Revision) >= 2)
                        throw new WandGenerationException("wand_upgrade_completed", 409);
                }
                else if (current is not null) throw new WandGenerationException("wand_already_created", 409);
            }
            var operation = existing ?? request.OperationId;
            wands.BeginMobile(owner, operation, captured, tier);
            wands.Begin(owner, operation, captured, 63, true);
            return Results.Ok(new { id = operation, profileId = profile.ProfileId, revision = profile.Revision });
        });
        group.MapPost("/sessions/{id}/ticket", async (string id, MobileWandTicketRequest request, HttpContext h,
            AccountDatabase db, CharacterProfileStore profiles, WandGenerationStore wands, MobileWandRuntime runtime) =>
        {
            var config = h.RequestServices.GetRequiredService<AccountConfiguration>();
            var origin = h.Request.Headers.Origin.ToString();
            if (!h.Request.IsHttps || !config.AllowedOrigins.Contains(origin, StringComparer.Ordinal)
                || request.SocketOrigin is not ("capacitor://localhost" or "https://localhost") && !config.AllowedOrigins.Contains(request.SocketOrigin, StringComparer.Ordinal))
                throw new WandGenerationException("account_origin_required", 403);
            var owner = h.User.FindFirstValue(ClaimTypes.NameIdentifier)!;
            if (wands.MobileOperationTier(owner, id) is not (1 or 2)) throw new WandGenerationException("wand_session_unavailable", 404);
            var session = wands.Read(owner, id) ?? throw new WandGenerationException("wand_session_unavailable", 404);
            var profile = await profiles.CaptureAsync(owner, session.ProfileId, session.ProfileRevision, h.RequestAborted)
                ?? throw new WandGenerationException("wand_profile_revision_mismatch");
            var user = await db.Users.SingleAsync(u => u.Id == owner, h.RequestAborted);
            var token = runtime.Ticket(new(owner, id, h.User.FindFirstValue(AccountIdentity.SessionClaim)!,
                user.SecurityStamp ?? "", request.SocketOrigin, DateTimeOffset.UtcNow.AddSeconds(30), profile));
            var path = $"/app/v1/customer/wand/sessions/{id}/live";
            var url = new UriBuilder(origin) { Scheme = "wss", Path = path, Query = "ticket=" + token }.Uri.AbsoluteUri;
            return Results.Ok(new { url, path, expiresAt = DateTimeOffset.UtcNow.AddSeconds(30) });
        });
        app.MapGet("/app/v1/customer/wand/sessions/{id}/live", async (string id, HttpContext h, MobileWandRuntime runtime) =>
        {
            try
            {
                if (!h.Request.IsHttps || !h.WebSockets.IsWebSocketRequest)
                    throw new WandGenerationException("websocket_required", 400);
                var admission = runtime.Take(h.Request.Query["ticket"].ToString(), id, h.Request.Headers.Origin.ToString());
                if (!await runtime.Authorized(admission, h.RequestAborted)) throw new WandGenerationException("account_required", 401);
                using var socket = await h.WebSockets.AcceptWebSocketAsync();
                await runtime.Run(admission, socket, h.RequestAborted);
            }
            catch (WandGenerationException e) when (!h.Response.HasStarted)
            { await ApiProblem.Create(h, e.Status, e.Code).ExecuteAsync(h); }
        }).AllowAnonymous().RequireRateLimiting("account");
    }
}
