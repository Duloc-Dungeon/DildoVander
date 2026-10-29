using System.Security.Claims;
using Kufr.Backend.Features.Game;
using Kufr.Backend.Features.Identity;
using Kufr.Backend.Features.Keepsakes;
using Kufr.Backend.Features.Launch;
using Microsoft.EntityFrameworkCore;

namespace Kufr.Backend.Features.Chronicle;

internal static class ChronicleEndpoints
{
    internal static void MapMobileChronicle(this WebApplication app)
    {
        if (!app.Services.GetRequiredService<AccountConfiguration>().Enabled) { return; }
        var group = app.MapGroup("/app/v1/chronicle").RequireAuthorization()
            .RequireRateLimiting(GameRateLimit.Policy);
        group.AddEndpointFilter(ChroniclePrivacyResult.Filter);
        group.MapGet("/export", async (HttpContext context, AccountDatabase database, ChronicleStore store) =>
        {
            var owner = await Owner(context, database);
            return owner is null ? Unauthorized() : new ChronicleExportResult(owner);
        });
        group.MapGet("", async (HttpContext context, AccountDatabase database, ChronicleStore store, LaunchPolicyStore launch,
            string? kind, string? participant, string? badge, string? before, int? limit) =>
        {
            var owner = await Owner(context, database);
            if (owner is null) { return Unauthorized(); }
            if (kind is { Length: > 20 } || participant is { Length: > 100 }
                || badge is { Length: > 100 } || before is { Length: > 100 })
            {
                return Results.Json(new { code = "chronicle_filter_invalid" }, statusCode: 400);
            }
            try
            {
                var page = store.List(owner, kind, participant, badge, before, limit ?? 30);
                return Results.Json(page with { Items = page.Items.Select(item => WithGroupName(item, launch)).ToArray() });
            }
            catch (ArgumentException)
            {
                return Results.Json(new { code = "chronicle_cursor_invalid" }, statusCode: 400);
            }
        });
        group.MapGet("/source/{kind}/{sourceId}", async (HttpContext context, AccountDatabase database,
            ChronicleStore store, LaunchPolicyStore launch, string kind, string sourceId) =>
        {
            var owner = await Owner(context, database);
            if (owner is null) { return Unauthorized(); }
            if (!ValidSourceKind(kind) || sourceId.Length is < 1 or > 100) { return NotFound(); }
            var item = store.FindSource(owner, kind, sourceId);
            return item is null ? NotFound() : Results.Json(WithGroupName(item, launch, context));
        });
        group.MapGet("/{id:guid}", async (HttpContext context, AccountDatabase database,
            ChronicleStore store, LaunchPolicyStore launch, string id) =>
        {
            var owner = await Owner(context, database);
            if (owner is null) { return Unauthorized(); }
            var item = store.Read(owner, id);
            return item is null ? NotFound() : Results.Json(WithGroupName(item, launch, context));
        });
        group.MapPost("/{id:guid}/reveal", async (HttpContext context, AccountDatabase database,
            ChronicleStore store, LaunchPolicyStore launch, string id) =>
        {
            var owner = await Owner(context, database);
            if (owner is null) { return Unauthorized(); }
            if (!store.Reveal(owner, id)) { return NotFound(); }
            var item = store.Read(owner, id);
            return Results.Json(new { revealed = true, item = item is null ? null : WithGroupName(item, launch, context) });
        });
        group.MapPost("/{id:guid}/illustration", async (HttpContext context, AccountDatabase database,
            ChronicleStore store, string id) =>
        {
            var owner = await Owner(context, database);
            if (owner is null) { return Unauthorized(); }
            if (!store.RequestIllustration(owner, id)) { return NotFound(); }
            return Results.Accepted(value: new { accepted = true });
        });
        group.MapGet("/{id:guid}/image", async (HttpContext context, AccountDatabase database,
            ChronicleStore store, string id) =>
        {
            var owner = await Owner(context, database);
            if (owner is null) { return Unauthorized(); }
            var image = store.ReadImage(owner, id);
            return image is null ? NotFound() : Results.File(image.Bytes, image.MediaType);
        });
        group.MapGet("/{id:guid}/share", async (HttpContext context, AccountDatabase database,
            ChronicleStore store, string id) =>
        {
            var owner = await Owner(context, database);
            if (owner is null) { return Unauthorized(); }
            var image = store.ReadShareImage(owner, id);
            return image is null ? NotFound() : Results.File(image.Bytes, image.MediaType);
        });
        group.MapGet("/{id:guid}/badges/{badgeId}/image", async (HttpContext context, AccountDatabase database,
            ChronicleStore store, string id, string badgeId) =>
        {
            var owner = await Owner(context, database);
            if (owner is null) { return Unauthorized(); }
            if (badgeId.Length is < 1 or > 100) { return NotFound(); }
            var image = store.ReadAwardImage(owner, id, badgeId);
            return image is null ? NotFound()
                : Results.File(ChronicleBadgeRenderer.Render(image, badgeId), "image/png");
        });
    }

    private static async Task<string?> Owner(HttpContext context, AccountDatabase database)
    {
        context.Response.Headers.CacheControl = "no-store";
        var ownerId = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (ownerId is null || !context.User.HasClaim(claim => claim.Type == AccountIdentity.SessionClaim)) { return null; }
        var enabled = await database.Users.AsNoTracking()
            .AnyAsync(user => user.Id == ownerId && !user.Disabled,
                context.RequestAborted);
        if (!enabled || await database.DeletionRequests.AsNoTracking()
                .AnyAsync(request => request.UserId == ownerId, context.RequestAborted)) { return null; }
        return ownerId;
    }

    private static bool ValidSourceKind(string kind) => kind is "yolo" or "duel" or "team" or "boss"
        or "case" or "wand" or "evening" or "conversation";
    private static ChronicleEventView WithGroupName(ChronicleEventView item, LaunchPolicyStore launch, HttpContext? context = null)
    {
        var name = item.GroupId.StartsWith("account:", StringComparison.Ordinal)
            ? "Osobní sbírka"
            : launch.ReadGroup(item.GroupId)?.Name ?? "Dřívější skupina";
        if (context is not null && item.Revealed && item.SourceKind == "duel"
            && context.User.FindFirstValue(ClaimTypes.NameIdentifier) is { } owner
            && context.RequestServices.GetService<GameStore>()?.ReadChronicleNarration(item.SourceId, item.GroupId, owner) is { } narration)
        {
            return item with { GroupName = name, Story = new[] { narration }.Concat(item.Story).Distinct(StringComparer.Ordinal).ToArray() };
        }
        return item with { GroupName = name };
    }
    private static IResult Unauthorized() => Results.Json(new { code = "account_required" }, statusCode: 401);
    private static IResult NotFound() => Results.Json(new { code = "chronicle_entry_unavailable" }, statusCode: 404);
}
