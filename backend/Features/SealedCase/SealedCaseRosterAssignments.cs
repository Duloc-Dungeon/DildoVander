using System.Security.Cryptography;
using System.Text;

namespace Kufr.Backend.Features.SealedCase;

internal enum SealedSecretTeam { Guardian, Conspirator }
internal enum SealedCharacterKit { Badatel, Strazce, Stopar, Prostrednik, Iluzionista, Rezonator }
internal sealed record SealedRosterAssignment(string PlayerId, string SeatId, SealedCharacterKit Kit, SealedSecretTeam Team);
internal sealed record SealedPublicSeat(string PlayerId, string SeatId, SealedCharacterKit Kit);
internal sealed record SealedStoredRosterMember(string AccountId, string PlayerId, string SeatId, string ProfileId, long ProfileRevision,
    string? PortraitStatus, SealedCharacterKit Kit, SealedSecretTeam Team, string? SnapshotRef = null, bool IsAi = false, string? DisplayName = null);
internal sealed record SealedStoredRoster(string CaseId, string ServerSeed, SealedStoredRosterMember[] Members, long FrozenAt);

// Keep character identity, public capability set, and confidential case allegiance
// as independent values. The seed is server-only and must never enter a client DTO.
internal static class SealedCaseRosterAssignments
{
    private static readonly SealedCharacterKit[] KitCycle = Enum.GetValues<SealedCharacterKit>();

    internal static IReadOnlyList<SealedRosterAssignment> Assign(IReadOnlyList<string> playerIds, string serverSeed)
    {
        if (playerIds.Count is < 8 or > 30 || playerIds.Any(string.IsNullOrWhiteSpace)
            || playerIds.Distinct(StringComparer.Ordinal).Count() != playerIds.Count || string.IsNullOrWhiteSpace(serverSeed))
            throw new SealedCaseRuleException("Invalid roster assignment input.");

        var seats = playerIds.Order(StringComparer.Ordinal)
            .Select((playerId, index) => (PlayerId: playerId, SeatId: $"P{index + 1:D2}"))
            .ToArray();
        var kits = new Dictionary<string, SealedCharacterKit>(StringComparer.Ordinal);
        var kitPool = Enumerable.Range(0, seats.Length).Select(index => KitCycle[index % KitCycle.Length]).ToArray();
        var kitOrder = seats.OrderBy(seat => Digest(serverSeed, "kits", seat.SeatId), StringComparer.Ordinal)
            .ThenBy(seat => seat.SeatId, StringComparer.Ordinal).ToArray();
        for (var index = 0; index < kitOrder.Length; index++) kits.Add(kitOrder[index].PlayerId, kitPool[index]);

        var conspirators = SealedCaseRules.Conspirators(seats.Length);
        var teamOrder = seats.OrderBy(seat => Digest(serverSeed, "teams", seat.SeatId), StringComparer.Ordinal)
            .ThenBy(seat => seat.SeatId, StringComparer.Ordinal).ToArray();
        var teamByPlayer = teamOrder.Select((seat, index) => (seat.PlayerId,
                Team: index < conspirators ? SealedSecretTeam.Conspirator : SealedSecretTeam.Guardian))
            .ToDictionary(item => item.PlayerId, item => item.Team, StringComparer.Ordinal);

        return seats.Select(seat => new SealedRosterAssignment(seat.PlayerId, seat.SeatId, kits[seat.PlayerId], teamByPlayer[seat.PlayerId])).ToArray();
    }

    internal static IReadOnlyList<SealedPublicSeat> Public(IReadOnlyList<SealedRosterAssignment> assignments) =>
        assignments.Select(assignment => new SealedPublicSeat(assignment.PlayerId, assignment.SeatId, assignment.Kit)).ToArray();

    private static string Digest(string seed, string purpose, string seatId) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes($"{seed}|{purpose}|{seatId}")));
}
