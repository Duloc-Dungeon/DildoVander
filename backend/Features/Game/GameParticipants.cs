using Kufr.Backend.Features.Identity;
using Microsoft.EntityFrameworkCore;

namespace Kufr.Backend.Features.Game;

internal static class GameParticipants
{
    // The owner's selected group is a viewing context, not their membership.
    internal static IQueryable<AccountUser> Members(AccountDatabase db, string groupId) =>
        db.Users.AsNoTracking().Where(user => !user.Disabled
            && !db.DeletionRequests.Any(deletion => deletion.UserId == user.Id)
            && db.UserRoles.Any(membership => membership.UserId == user.Id
                && db.Roles.Any(role => role.Id == membership.RoleId
                    && (role.Name == AccountRoles.Owner
                        || role.Name == AccountRoles.Customer && user.LaunchGroupId == groupId
                            && user.LaunchGroupRevokedAt == null))));
}
