namespace Kufr.Backend.Features.SealedCase;

// Server storage DTOs, never player-facing contracts. Explicitly include every
// mechanical field; serializing SealedPlayer directly would silently store {}.
internal sealed record SealedPlayerCheckpoint(string Id, string Location, SealedPlayerState State,
    string[] KnownPlayers, string[] ObservedPlayers, string[] Methods, string[] Expertise,
    int ExtremeToken, bool FeigningSleep, int Ward, int Cover, int Preparation,
    int PreparationChapter, SealedIntent? PreparedIntent, int UnconsciousUntilEnd,
    int SleepingUntilStart, Dictionary<string, string> VerifiedSleepEvidence)
{
    internal static SealedPlayerCheckpoint Capture(SealedPlayer p) => new(p.Id, p.Location, p.State,
        p.KnownPlayers.Order(StringComparer.Ordinal).ToArray(), p.ObservedPlayers.Order(StringComparer.Ordinal).ToArray(),
        p.Methods.Order(StringComparer.Ordinal).ToArray(), p.Expertise.Order(StringComparer.Ordinal).ToArray(),
        p.ExtremeToken, p.FeigningSleep, p.Ward, p.Cover, p.Preparation, p.PreparationChapter,
        p.PreparedIntent, p.UnconsciousUntilEnd, p.SleepingUntilStart,
        p.VerifiedSleepEvidence.OrderBy(x => x.Key, StringComparer.Ordinal).ToDictionary());

    internal SealedPlayer Restore()
    {
        var p = new SealedPlayer(Id, Location, KnownPlayers, ObservedPlayers, State, Methods, Expertise, ExtremeToken)
        { FeigningSleep = FeigningSleep, Ward = Ward, Cover = Cover, Preparation = Preparation,
            PreparationChapter = PreparationChapter, PreparedIntent = PreparedIntent,
            UnconsciousUntilEnd = UnconsciousUntilEnd, SleepingUntilStart = SleepingUntilStart };
        foreach (var pair in VerifiedSleepEvidence) p.VerifiedSleepEvidence.Add(pair.Key, pair.Value);
        return p;
    }
}

internal sealed record SealedGameCheckpoint(int StorageVersion, string Seed,
    Dictionary<string, string[]> Adjacency, SealedPlayerCheckpoint[] Players);

internal sealed partial class SealedGame
{
    internal SealedGameCheckpoint Checkpoint() => new(1, _seed,
        _adjacency.OrderBy(x => x.Key, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.Value.Order(StringComparer.Ordinal).ToArray()),
        Players.Values.OrderBy(x => x.Id, StringComparer.Ordinal).Select(SealedPlayerCheckpoint.Capture).ToArray());

    internal static SealedGame Restore(SealedGameCheckpoint checkpoint)
    {
        if (checkpoint.StorageVersion != 1) throw new SealedCasePersistenceException("unsupported_world_storage_version", 409);
        return new(checkpoint.Players.Select(x => x.Restore()), checkpoint.Seed, checkpoint.Adjacency);
    }
}
