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
        var user = await accounts.Users.AsNoTracking().SingleOrDefaultAsync(user => user.Id == owner,
            http.RequestAborted);
        if (user?.LaunchGroupId is null || !http.User.IsInRole(AccountRoles.Customer))
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
        IServiceProvider services, CancellationToken token)
    {
        if (tier is not (1 or 2) || user.Disabled || user.LaunchGroupRevokedAt is not null
            || user.LaunchGroupId is not { } groupId || !AccountAiConsent.HasConsent(user))
        {
            return false;
        }
        var roles = await services.GetRequiredService<UserManager<AccountUser>>().GetRolesAsync(user);
        if (!roles.Contains(AccountRoles.Customer, StringComparer.Ordinal))
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
            && !current.Disabled && current.LaunchGroupRevokedAt == null
            && current.LaunchGroupId == groupId
            && current.LaunchGroupAccessRevision == user.LaunchGroupAccessRevision
            && current.OpenAiConsentAt > 0 && current.OpenAiConsentVersion == AccountAiConsent.CurrentVersion
            && !accounts.DeletionRequests.Any(deletion => deletion.UserId == user.Id), token);
    }
}
