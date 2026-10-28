using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text.Json;
using Kufr.Backend.Features.Customers;
using Kufr.Backend.Features.CustomerData;
using Kufr.Backend.Features.GptLive;
using Kufr.Backend.Features.Identity;
using Kufr.Backend.Features.Game;
using Kufr.Backend.Features.Launch;
using Microsoft.AspNetCore.Identity;
using Kufr.Backend.Features.Observability;
using Kufr.Backend.Features.PersonaLab;
using Microsoft.EntityFrameworkCore;

namespace Kufr.Backend.Features.Wands;

internal sealed record MobileWandAdmission(string Owner, string Operation, string AccountSession,
    string Stamp, string Origin, DateTimeOffset ExpiresAt, CharacterConversationSnapshot Profile);

// The phone owns its microphone; the account socket never impersonates a device
// and cannot issue a physical presentation or create a fabricated keepsake.
internal sealed class MobileWandRuntime(IServiceScopeFactory scopes, WandGenerationStore wands)
{
    private readonly ConcurrentDictionary<string, MobileWandAdmission> tickets = new();
    private readonly ConcurrentDictionary<string, byte> active = new();
    internal string Ticket(MobileWandAdmission admission)
    {
        foreach (var item in tickets.Where(t => t.Value.ExpiresAt < DateTimeOffset.UtcNow)) tickets.TryRemove(item.Key, out _);
        if (tickets.Count >= 1000) throw new WandGenerationException("wand_capacity", 429);
        var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        tickets[Hash(token)] = admission;
        return token;
    }
    internal MobileWandAdmission Take(string token, string operation, string origin)
    {
        if (token.Length != 64 || !tickets.TryRemove(Hash(token), out var admission)
            || admission.Operation != operation || admission.Origin != origin || admission.ExpiresAt <= DateTimeOffset.UtcNow)
            throw new WandGenerationException("invalid_wand_ticket", 401);
        return admission;
    }
    private static string Hash(string token) => Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token)));

    internal async Task<bool> Authorized(MobileWandAdmission a, CancellationToken token)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AccountDatabase>();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == a.Owner && !u.Disabled
            && u.SecurityStamp == a.Stamp && !db.DeletionRequests.Any(d => d.UserId == a.Owner), token);
        if (user is null || user.LaunchGroupRevokedAt != null || !AccountAiConsent.HasConsent(user)
            || !await db.Sessions.AsNoTracking().AnyAsync(s => s.Id == a.AccountSession && s.UserId == a.Owner && s.ExpiresAt > now, token)) return false;
        var tier = wands.MobileOperationTier(a.Owner, a.Operation);
        if (tier is not (1 or 2)) return false;
        if (!await MobileWandWorkshopAccess.IsAllowedAsync(user, tier, scope.ServiceProvider, token)
            || user.LaunchGroupId is null || !wands.WorkshopMatchesLaunchSnapshot(a.Owner,
                a.Operation, user.LaunchGroupId, user.LaunchGroupAccessRevision)) return false;
        if (tier == 2)
        {
            if (user.LaunchGroupId is null || scope.ServiceProvider.GetRequiredService<LaunchPolicyStore>().ReadGroup(user.LaunchGroupId)?.OpenedAt is null
                || scope.ServiceProvider.GetRequiredService<GameStore>().Stats(user.LaunchGroupId, a.Owner, "").Xp < 30) return false;
            var principal = await scope.ServiceProvider.GetRequiredService<IUserClaimsPrincipalFactory<AccountUser>>().CreateAsync(user);
            if (!principal.IsInRole(AccountRoles.Customer) || !(await scope.ServiceProvider.GetRequiredService<LaunchAccessService>().EvaluateAsync(principal)).Allowed) return false;
        }
        var profile = await scope.ServiceProvider.GetRequiredService<CharacterProfileStore>().ReadAsync(a.Owner, token);
        return profile?.ProfileId == a.Profile.ProfileId && profile.Revision == a.Profile.Revision;
    }

}
