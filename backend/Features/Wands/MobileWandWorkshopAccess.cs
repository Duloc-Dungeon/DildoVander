using System.Security.Claims;
using Kufr.Backend.Features.Game;
using Kufr.Backend.Features.Identity;
using Kufr.Backend.Features.Launch;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Kufr.Backend.Features.Wands;

// Preparing the first wand belongs to admission. Upgrades still belong to the
// opened night; game participant authorization must not inherit this exception.
internal static class MobileWandWorkshopAccess
{
    internal static async Task<string> ParticipantGroupAsync(HttpContext http, int tier)
    {
        var owner = http.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (owner is null || !http.User.HasClaim(claim => claim.Type == AccountIdentity.SessionClaim))
        {
            throw new GameFault(401, "account_required");
        }
        var accounts = http.RequestServices.GetRequiredService<AccountDatabase>();
        await OwnerPlayerGroups.EnsureSelectionAsync(http, accounts, http.RequestServices.GetRequiredService<LaunchPolicyStore>());
        var user = await accounts.Users.AsNoTracking().SingleOrDefaultAsync(user => user.Id == owner,
            http.RequestAborted);
        if (user?.LaunchGroupId is null || !AccountRoles.IsPlayer(http.User))
        {
            throw new GameFault(403, "game_group_required");
        }
        if (!await IsAllowedAsync(user, tier, http.RequestServices, http.RequestAborted))
        {
            throw new GameFault(403, "game_group_inactive");
        }
        return user.LaunchGroupId;
    }

    internal static async Task<bool> IsAllowedAsync(AccountUser user, int tier,
        IServiceProvider services, CancellationToken token, string? snapshotGroupId = null, long? snapshotRevision = null)
    {
        if (tier is not (1 or 2) || user.Disabled
            || user.LaunchGroupId is not { } selectedGroupId || !AccountAiConsent.HasConsent(user))
        {
            return false;
        }
        var roles = await services.GetRequiredService<UserManager<AccountUser>>().GetRolesAsync(user);
        var ownerPreparation = tier == 1 && roles.Contains(AccountRoles.Owner, StringComparer.Ordinal);
        if (!roles.Contains(AccountRoles.Customer, StringComparer.Ordinal)
            && !roles.Contains(AccountRoles.Owner, StringComparer.Ordinal))
        {
            return false;
        }
        // Owner membership spans all groups; a viewing-context switch must not revoke first-wand work.
        // Ordinary guests and upgrades retain their saved membership revision fence.
        var groupId = ownerPreparation ? snapshotGroupId ?? selectedGroupId : selectedGroupId;
        if (!ownerPreparation && (user.LaunchGroupRevokedAt is not null
            || snapshotGroupId is not null && (snapshotGroupId != selectedGroupId
                || snapshotRevision != user.LaunchGroupAccessRevision)))
        {
            return false;
        }
        var claims = roles.Select(role => new Claim(ClaimTypes.Role, role)).Append(
            new Claim(ClaimTypes.NameIdentifier, user.Id));
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "wand-workshop"));
        var access = await services.GetRequiredService<LaunchAccessService>().EvaluateAsync(principal);
        var group = services.GetRequiredService<LaunchPolicyStore>().ReadGroup(groupId);
        if (group is null || access.Audience == "revoked"
            || !(access.Allowed && group.OpenedAt is not null || tier == 1 && group.OpenedAt is null))
        {
            return false;
        }
        // Evaluating launch access can complete a countdown. Recheck membership
        // afterwards so a move or revocation cannot use the closed-night exception.
        var accounts = services.GetRequiredService<AccountDatabase>();
        return await accounts.Users.AsNoTracking().AnyAsync(current => current.Id == user.Id
            && !current.Disabled
            && (ownerPreparation
                ? accounts.UserRoles.Any(membership => membership.UserId == current.Id
                    && accounts.Roles.Any(role => role.Id == membership.RoleId && role.Name == AccountRoles.Owner))
                : current.LaunchGroupRevokedAt == null && current.LaunchGroupId == groupId
                    && current.LaunchGroupAccessRevision == user.LaunchGroupAccessRevision)
            && current.OpenAiConsentAt > 0 && current.OpenAiConsentVersion == AccountAiConsent.CurrentVersion
            && !accounts.DeletionRequests.Any(deletion => deletion.UserId == user.Id), token);
    }

    internal static async Task<bool> IsAllowedForWorkshopAsync(AccountUser user, string operationId,
        WandGenerationStore wands, IServiceProvider services, CancellationToken token)
    {
        var snapshot = wands.ReadWorkshopLaunchSnapshot(user.Id, operationId);
        return snapshot is not null && await IsAllowedAsync(user, wands.MobileOperationTier(user.Id, operationId),
            services, token, snapshot.Value.GroupId, snapshot.Value.AccessRevision);
    }

}
