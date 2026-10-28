using Kufr.Backend.Features.Customers;
using Kufr.Backend.Features.Identity;
using Microsoft.EntityFrameworkCore;

namespace Kufr.Backend.Features.Game;

internal static class GameDisplayNames
{
    internal static async Task<GameLobby> DecorateAsync(GameLobby lobby, CharacterProfileStore profiles,
        AccountDatabase accounts, CancellationToken token)
    {
        var names = await ResolveAsync(Players(lobby), profiles, accounts, token);
        return Decorate(lobby, names);
    }

    internal static async Task<GameMatchView> DecorateAsync(GameMatchView match, CharacterProfileStore profiles,
        AccountDatabase accounts, CancellationToken token)
    {
        var names = await ResolveAsync([(match.Opponent.Id, match.Opponent.DisplayName)], profiles, accounts, token);
        return Decorate(match, names);
    }

    internal static async Task<GameProgressView> DecorateAsync(GameProgressView progress,
        CharacterProfileStore profiles, AccountDatabase accounts, CancellationToken token)
    {
        var rankings = progress.CombatRanking.Concat(progress.YoloRanking)
            .Select(row => (row.Id, row.DisplayName));
        var names = await ResolveAsync(rankings, profiles, accounts, token);
        return Decorate(progress, names);
    }

    internal static GameProgressView Decorate(GameProgressView progress,
        IReadOnlyDictionary<string, string> displayNames) => progress with
    {
        CombatRanking = progress.CombatRanking.Select(row => row with
        {
            DisplayName = Resolve(row.Id, row.DisplayName, displayNames)
        }).ToArray(),
        YoloRanking = progress.YoloRanking.Select(row => row with
        {
            DisplayName = Resolve(row.Id, row.DisplayName, displayNames)
        }).ToArray()
    };

    internal static string Resolve(string accountId, string fallbackName,
        IReadOnlyDictionary<string, string> displayNames) =>
        displayNames.TryGetValue(accountId, out var displayName) ? displayName : fallbackName;

    internal static string DisplayName(string fallbackName, CharacterProfile? profile) =>
        profile is { Label: { Length: > 0 } } ? profile.DisplayName : fallbackName;

    internal static GameLobby Decorate(GameLobby lobby, IReadOnlyDictionary<string, string> displayNames) => lobby with
    {
        Player = lobby.Player with
        {
            DisplayName = Resolve(lobby.Player.Id, lobby.Player.DisplayName, displayNames)
        },
        Opponents = lobby.Opponents.Select(opponent => opponent with
        {
            DisplayName = Resolve(opponent.Id, opponent.DisplayName, displayNames)
        }).ToArray(),
        Incoming = lobby.Incoming.Select(match => Decorate(match, displayNames)).ToArray(),
        Active = lobby.Active.Select(match => Decorate(match, displayNames)).ToArray(),
        History = lobby.History.Select(match => Decorate(match, displayNames)).ToArray()
    };

    internal static GameMatchView Decorate(GameMatchView match,
        IReadOnlyDictionary<string, string> displayNames) => match with
    {
        Opponent = Decorate(match.Opponent, displayNames)
    };

    private static IEnumerable<(string Id, string DisplayName)> Players(GameLobby lobby) =>
        new[] { (lobby.Player.Id, lobby.Player.DisplayName) }
            .Concat(lobby.Opponents.Select(opponent => (opponent.Id, opponent.DisplayName)))
            .Concat(lobby.Incoming.Concat(lobby.Active).Concat(lobby.History)
                .Select(match => (match.Opponent.Id, match.Opponent.DisplayName)));

    private static async Task<IReadOnlyDictionary<string, string>> ResolveAsync(
        IEnumerable<(string Id, string DisplayName)> players, CharacterProfileStore profiles, AccountDatabase accounts, CancellationToken token)
    {
        var resolved = new Dictionary<string, string>(StringComparer.Ordinal);
        var uniquePlayers = players.DistinctBy(player => player.Id).ToArray();
        var ids = uniquePlayers.Select(player => player.Id).ToArray();
        var owners = await (from membership in accounts.UserRoles
            join role in accounts.Roles on membership.RoleId equals role.Id
            where ids.Contains(membership.UserId) && role.Name == AccountRoles.Owner
            select membership.UserId).ToHashSetAsync(token);
        foreach (var player in uniquePlayers)
        {
            if (owners.Contains(player.Id))
            {
                resolved[player.Id] = AccountRoles.OwnerDisplayName;
                continue;
            }
            var profile = await profiles.ReadAsync(player.Id, token);
            if (profile is { Label: { Length: > 0 } })
            {
                resolved[player.Id] = DisplayName(player.DisplayName, profile);
            }
        }
        return resolved;
    }

    private static GameOpponentView Decorate(GameOpponentView opponent,
        IReadOnlyDictionary<string, string> displayNames) => opponent with
    {
        DisplayName = Resolve(opponent.Id, opponent.DisplayName, displayNames)
    };

}
