using System.Security.Claims;
using Kufr.Backend.Features.Access;
using Kufr.Backend.Features.Identity;
using Microsoft.EntityFrameworkCore;

namespace Kufr.Backend.Features.Launch;

internal sealed record OwnerPlayerGroupSelection(string GroupId, long ExpectedRevision);

internal static class OwnerPlayerGroups
{
    internal static async Task EnsureSelectionAsync(HttpContext context, AccountDatabase db, LaunchPolicyStore store)
    {
        if (!context.User.IsInRole(AccountRoles.Owner)) { return; }
        var id = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(candidate => candidate.Id == id,
            context.RequestAborted);
        if (user is null || user.Disabled) { return; }
        var currentGroup = user.LaunchGroupId is null ? null : store.ReadGroup(user.LaunchGroupId);
        if (currentGroup is not null && user.LaunchGroupRevokedAt is null) { return; }
        var group = currentGroup ?? store.ReadGroups().FirstOrDefault();
        if (group is null) { return; }
        // Conditional update keeps concurrent explicit selections authoritative.
        await db.Users.Where(candidate => candidate.Id == id && !candidate.Disabled
                && candidate.LaunchGroupAccessRevision == user.LaunchGroupAccessRevision
                && candidate.LaunchGroupId == user.LaunchGroupId
                && !db.DeletionRequests.Any(deletion => deletion.UserId == candidate.Id)
                && db.UserRoles.Any(member => member.UserId == candidate.Id
                    && db.Roles.Any(role => role.Id == member.RoleId && role.Name == AccountRoles.Owner)))
            .ExecuteUpdateAsync(update => update
                .SetProperty(candidate => candidate.LaunchGroupId, group.Id)
                .SetProperty(candidate => candidate.LaunchGroupJoinedAt, AccountEndpoints.Now())
                .SetProperty(candidate => candidate.LaunchGroupRevokedAt, (long?)null)
                .SetProperty(candidate => candidate.LaunchGroupAccessRevision, candidate => candidate.LaunchGroupAccessRevision + 1),
                context.RequestAborted);
    }

    internal static void MapOwnerPlayerGroups(this WebApplication app)
    {
        var routes = app.MapGroup("/app/v1/customer/groups").RequireAuthorization().RequireRateLimiting("account");
        routes.AddEndpointFilter(async (invocation, next) =>
        {
            var context = invocation.HttpContext;
            if (!context.User.IsInRole(AccountRoles.Owner)
                || !context.User.HasClaim(claim => claim.Type == AccountIdentity.SessionClaim))
            { return ApiProblem.Create(context, 403, "account_owner_required"); }
            context.Response.Headers.CacheControl = "no-store";
            return await next(invocation);
        });
        routes.MapGet("", async (HttpContext context, AccountDatabase db, LaunchPolicyStore store) =>
        {
            await EnsureSelectionAsync(context, db, store);
            return await ViewAsync(context, db, store);
        });
        routes.MapPut("/active", async (OwnerPlayerGroupSelection request, HttpContext context,
            AccountDatabase db, LaunchPolicyStore store) =>
        {
            if (request.ExpectedRevision < 0 || string.IsNullOrWhiteSpace(request.GroupId)
                || request.GroupId.Length > 64)
            { return ApiProblem.Create(context, 400, "invalid_owner_player_group"); }
            using var reservation = store.ReserveWaitingRoomMove(null, request.GroupId);
            if (reservation is null) { return ApiProblem.Create(context, 404, "launch_group_unavailable"); }
            var id = context.User.FindFirstValue(ClaimTypes.NameIdentifier)!;
            var changed = await db.Users.Where(user => user.Id == id && !user.Disabled
                    && user.LaunchGroupAccessRevision == request.ExpectedRevision
                    && !db.DeletionRequests.Any(deletion => deletion.UserId == id)
                    && db.UserRoles.Any(member => member.UserId == id
                        && db.Roles.Any(role => role.Id == member.RoleId && role.Name == AccountRoles.Owner)))
                .ExecuteUpdateAsync(update => update
                    .SetProperty(user => user.LaunchGroupId, request.GroupId)
                    .SetProperty(user => user.LaunchGroupJoinedAt, AccountEndpoints.Now())
                    .SetProperty(user => user.LaunchGroupRevokedAt, (long?)null)
                    .SetProperty(user => user.LaunchGroupAccessRevision, user => user.LaunchGroupAccessRevision + 1),
                    context.RequestAborted);
            if (changed == 0) { return ApiProblem.Create(context, 409, "beta_member_revision_conflict"); }
            return await ViewAsync(context, db, store);
        });
    }

    private static async Task<IResult> ViewAsync(HttpContext context, AccountDatabase db, LaunchPolicyStore store)
    {
        var id = context.User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(candidate => candidate.Id == id
            && !candidate.Disabled && !db.DeletionRequests.Any(deletion => deletion.UserId == id), context.RequestAborted);
        return user is null ? ApiProblem.Create(context, 401, "account_access_unavailable")
            : Results.Ok(new { groups = store.ReadGroups(), groupId = user.LaunchGroupId,
                accessRevision = user.LaunchGroupAccessRevision });
    }
}
