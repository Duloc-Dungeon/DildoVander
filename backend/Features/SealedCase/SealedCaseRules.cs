using System.Security.Cryptography;
using System.Text;

namespace Kufr.Backend.Features.SealedCase;

internal sealed record SealedSchedule(int Chapters, int PlanningSeconds, int QuietSeconds, int IntroductionSeconds, int FinaleSeconds)
{
    internal int TotalSeconds => checked(IntroductionSeconds + Chapters * (PlanningSeconds + QuietSeconds) + FinaleSeconds);
}
internal sealed record SealedVoteTally(IReadOnlyList<string> Selected, bool NeedsRunoff, IReadOnlyList<string> TiedCandidates, int OpenPlaces);

internal static class SealedCaseRules
{
    internal static SealedSchedule Schedule(string variant, int originalPlayers)
    {
        if (originalPlayers is < 8 or > 30) throw new SealedCaseRuleException("Počet hráčů musí být 8 až 30.");
        if (variant == "first")
        {
            if (originalPlayers > 12) throw new SealedCaseRuleException("První případ je pro 8 až 12 hráčů.");
            return new(3, 8 * 60, 2 * 60, 5 * 60, 10 * 60);
        }
        if (variant != "full") throw new SealedCaseRuleException("Neznámá varianta případu.");
        return originalPlayers <= 12 ? new(6, 25 * 60, 15 * 60, 15 * 60, 20 * 60)
            : originalPlayers <= 20 ? new(6, 30 * 60, 20 * 60, 15 * 60, 20 * 60)
            : new(6, 40 * 60, 20 * 60, 15 * 60, 20 * 60);
    }

    internal static int Conspirators(int originalPlayers)
    {
        if (originalPlayers is < 8 or > 30) throw new SealedCaseRuleException("Neplatný původní počet hráčů.");
        return originalPlayers <= 12 ? 2 : originalPlayers <= 17 ? 3 : originalPlayers <= 22 ? 4 : originalPlayers <= 27 ? 5 : 6;
    }

    internal static void ValidateVote(IReadOnlyList<string> originalPlayers, string voter, IReadOnlyList<string> nominees)
    {
        ValidateRoster(originalPlayers);
        if (!originalPlayers.Contains(voter, StringComparer.Ordinal)) throw new SealedCaseRuleException("Hlasující není původním hráčem.");
        if (nominees.Count == 0) return; // explicit abstention; no substitute vote
        if (nominees.Count != Conspirators(originalPlayers.Count) || nominees.Distinct(StringComparer.Ordinal).Count() != nominees.Count
            || nominees.Any(id => id == voter || !originalPlayers.Contains(id, StringComparer.Ordinal)))
            throw new SealedCaseRuleException("Vyber přesný počet různých ostatních hráčů, nebo se zdrž hlasu.");
    }

    internal static SealedVoteTally Tally(IReadOnlyList<string> originalPlayers, IReadOnlyDictionary<string, string[]> ballots, string seed, bool afterRunoff = false)
    {
        ValidateRoster(originalPlayers);
        foreach (var ballot in ballots) ValidateVote(originalPlayers, ballot.Key, ballot.Value);
        var required = Conspirators(originalPlayers.Count);
        var counts = originalPlayers.ToDictionary(id => id, _ => 0, StringComparer.Ordinal);
        foreach (var target in ballots.Values.SelectMany(x => x)) counts[target]++;
        var cutoff = counts.Values.OrderDescending().ElementAt(required - 1);
        var certain = counts.Where(x => x.Value > cutoff).Select(x => x.Key).Order(StringComparer.Ordinal).ToArray();
        var tied = counts.Where(x => x.Value == cutoff).Select(x => x.Key).Order(StringComparer.Ordinal).ToArray();
        var open = required - certain.Length;
        if (tied.Length > open && !afterRunoff) return new(certain, true, tied, open);
        // Only invoke the seeded tie breaker AFTER the explicit runoff has closed.
        // Caller must persist both ballot sets and this deterministic selection.
        var chosen = tied.OrderBy(id => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{seed}|final-runoff|{id}"))), StringComparer.Ordinal).Take(open);
        return new(certain.Concat(chosen).ToArray(), false, [], 0);
    }

    internal static bool GuardiansWin(IReadOnlyList<string> selected, IReadOnlyList<string> conspirators, int fixedTeamCount)
    {
        if (fixedTeamCount is < 2 or > 6 || selected.Count != fixedTeamCount || conspirators.Count != fixedTeamCount
            || selected.Distinct(StringComparer.Ordinal).Count() != selected.Count || conspirators.Distinct(StringComparer.Ordinal).Count() != conspirators.Count)
            throw new SealedCaseRuleException("Neplatný konečný seznam.");
        return selected.Intersect(conspirators, StringComparer.Ordinal).Count() >= (2 * fixedTeamCount + 2) / 3;
    }

    private static void ValidateRoster(IReadOnlyList<string> originalPlayers)
    {
        _ = Conspirators(originalPlayers.Count);
        if (originalPlayers.Any(string.IsNullOrWhiteSpace) || originalPlayers.Distinct(StringComparer.Ordinal).Count() != originalPlayers.Count)
            throw new SealedCaseRuleException("Původní seznam hráčů není platný.");
    }
}

// Server-side clock; UI may display it but never grants authority at local zero.
internal sealed class SealedChapterClock(long startedAt, int planningSeconds, int quietSeconds)
{
    internal long PlanningEndsAt { get; private set; } = checked(startedAt + planningSeconds);
    internal long QuietEndsAt { get; private set; } = checked(startedAt + planningSeconds + quietSeconds);
    internal long? PausedAt { get; private set; }
    internal string Phase(long now) => PausedAt is not null ? "paused" : now < startedAt ? "scheduled" : now < PlanningEndsAt ? "planning" : now < QuietEndsAt ? "quiet" : "locked";
    internal void Pause(long now)
    {
        if (PausedAt is not null || now < startedAt || now >= QuietEndsAt) throw new SealedCaseRuleException("Kapitolu nyní nelze pozastavit.");
        PausedAt = now;
    }
    internal void Resume(long now)
    {
        if (PausedAt is null || now < PausedAt) throw new SealedCaseRuleException("Kapitolu nyní nelze obnovit.");
        var duration = now - PausedAt.Value;
        PlanningEndsAt = checked(PlanningEndsAt + duration); QuietEndsAt = checked(QuietEndsAt + duration); PausedAt = null;
    }
}
