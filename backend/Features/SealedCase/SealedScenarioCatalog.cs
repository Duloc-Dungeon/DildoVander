using System.Text.Json;

namespace Kufr.Backend.Features.SealedCase;

// Only this allowlisted presentation leaves the server. A scenario never owns
// roster assignments, evidence recipes, character profiles or a rule resolver.
internal sealed record SealedScenarioSummary(string Id, string Version, string Title, string Summary,
    string Universe, string Tone, string GuardianLabel, string ConspiratorLabel,
    string EchoLabel, string EchoExplanation, string ExtremeActionLabel, string ExtremeConsequence);

// A complete, server-authored presentation is frozen when the case is created.
// Existing lobbies and checkpoints therefore do not change when a pack is edited.
internal sealed record SealedScenarioPresentation(string Id, string Version, string Title, string Summary,
    string Universe, string Tone, string GuardianLabel, string ConspiratorLabel,
    string GuardianObjective, string ConspiratorObjective, string[] PublicFacts,
    Dictionary<string, string> Locations, Dictionary<string, string> ClueLabels,
    string ClueDescription, string ItemName, string AcceptedPast, string EchoLabel,
    string EchoExplanation, string ExtremeActionLabel, string ExtremeConsequence,
    string[] ChapterTitles, string[] ChapterMessages, string FinaleGuardian, string FinaleConspirator)
{
    internal SealedScenarioSummary Describe() => new(Id, Version, Title, Summary, Universe, Tone,
        GuardianLabel, ConspiratorLabel, EchoLabel, EchoExplanation, ExtremeActionLabel, ExtremeConsequence);

    internal SealedScenarioPresentation Copy() => this with
    {
        PublicFacts = [.. PublicFacts], Locations = new(Locations, StringComparer.Ordinal),
        ClueLabels = new(ClueLabels, StringComparer.Ordinal), ChapterTitles = [.. ChapterTitles],
        ChapterMessages = [.. ChapterMessages]
    };

    internal int ChapterIndex(int chapter, string duration) => duration == "first"
        ? new[] { 0, 2, 5 }[Math.Clamp(chapter - 1, 0, 2)] : Math.Clamp(chapter - 1, 0, 5);
}

internal static class SealedScenarioCatalog
{
    internal const string DefaultId = "POD_PECETI_POSLEDNI_ZVON_V1";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly Dictionary<string, SealedScenarioPresentation> Packs = LoadPacks();

    internal static SealedScenarioSummary[] Summaries() => Packs.Values.Select(p => p.Describe()).ToArray();

    internal static SealedScenarioPresentation Resolve(string? id = null)
    {
        if (!Packs.TryGetValue(id ?? DefaultId, out var pack))
        { throw new SealedCasePersistenceException("sealed_case_scenario_unknown", 400); }
        return pack.Copy();
    }

    internal static SealedScenarioPresentation FromSetup(JsonElement setup)
    {
        var id = setup.TryGetProperty("scenarioId", out var selected) ? selected.GetString() : DefaultId;
        if (!setup.TryGetProperty("scenarioSnapshot", out var snapshot)) { return Resolve(id); }
        var pack = snapshot.Deserialize<SealedScenarioPresentation>(Json)
            ?? throw new SealedCasePersistenceException("sealed_case_scenario_unavailable", 409);
        Validate(pack);
        if (pack.Id != id) { throw new SealedCasePersistenceException("sealed_case_scenario_unavailable", 409); }
        return pack;
    }

    internal static SealedScenarioPresentation ForState(SealedScenarioState? state)
    {
        if (state?.Presentation is { } presentation)
        {
            if (presentation.Id != state.ScenarioId) { throw new SealedCasePersistenceException("sealed_case_scenario_unavailable", 409); }
            return presentation.Copy();
        }
        return Resolve(state?.ScenarioId);
    }

    private static Dictionary<string, SealedScenarioPresentation> LoadPacks()
    {
        var packs = new Dictionary<string, SealedScenarioPresentation>(StringComparer.Ordinal);
        var original = Original();
        Validate(original); packs.Add(original.Id, original);
        using var document = SealedScenario.Load("scenario-variants.json");
        foreach (var entry in document.RootElement.GetProperty("variants").EnumerateArray())
        {
            var pack = entry.Deserialize<SealedScenarioPresentation>(Json)
                ?? throw new InvalidOperationException("Missing scenario presentation.");
            Validate(pack);
            if (!packs.TryAdd(pack.Id, pack)) { throw new InvalidOperationException("Duplicate sealed case scenario ID."); }
        }
        return packs;
    }

    private static void Validate(SealedScenarioPresentation pack)
    {
        var texts = new[] { pack.Id, pack.Version, pack.Title, pack.Summary, pack.Universe, pack.Tone,
            pack.GuardianLabel, pack.ConspiratorLabel, pack.GuardianObjective, pack.ConspiratorObjective,
            pack.ClueDescription, pack.ItemName, pack.AcceptedPast, pack.EchoLabel, pack.EchoExplanation,
            pack.ExtremeActionLabel, pack.ExtremeConsequence, pack.FinaleGuardian, pack.FinaleConspirator };
        var zones = SealedScenario.Definition.GetProperty("map").GetProperty("zones").EnumerateArray()
            .Select(z => z.GetProperty("id").GetString()!).ToHashSet(StringComparer.Ordinal);
        if (texts.Any(t => string.IsNullOrWhiteSpace(t) || t.Length > 1600 || t.Any(char.IsControl))
            || pack.Id.Length > 128 || pack.Id.Any(c => c is not (>= 'A' and <= 'Z' or >= '0' and <= '9' or '_'))
            || pack.Title.Length > 160 || pack.GuardianLabel.Length > 80 || pack.ConspiratorLabel.Length > 80
            || pack.PublicFacts is not { Length: > 0 and <= 8 }
            || pack.ChapterTitles is not { Length: 6 } || pack.ChapterMessages is not { Length: 6 }
            || pack.Locations is null || !zones.SetEquals(pack.Locations.Keys)
            || pack.ClueLabels is null || !new HashSet<string>(["A", "B", "C"], StringComparer.Ordinal).SetEquals(pack.ClueLabels.Keys)
            || pack.PublicFacts.Concat(pack.ChapterTitles).Concat(pack.ChapterMessages)
                .Concat(pack.Locations.Values).Concat(pack.ClueLabels.Values)
                .Any(t => string.IsNullOrWhiteSpace(t) || t.Length > 1600 || t.Any(char.IsControl)))
        { throw new InvalidOperationException("Invalid sealed case scenario presentation."); }
    }

    private static SealedScenarioPresentation Original()
    {
        var definition = SealedScenario.Definition;
        var facts = definition.GetProperty("prelude").GetProperty("public_facts").EnumerateArray().Select(f => f.GetString()!).ToArray();
        return new(DefaultId, "1.0.0", definition.GetProperty("title").GetString()!,
            "Ve starém domě někdo narušil ochrannou pečeť. Spoj stopy a odhal původní účastníky rituálu.",
            definition.GetProperty("world").GetString()!, "Tajemství starého domu", "Strážci", "Spiklenci",
            "Spoj doložené stopy a v závěrečném hlasování označ potřebný počet původních aktivátorů podle pravidel této partie.",
            "Znáš své spolupracovníky. Zabraň tomu, aby závěrečné hlasování odhalilo potřebný počet původních aktivátorů.",
            facts, definition.GetProperty("map").GetProperty("zones").EnumerateArray()
                .ToDictionary(z => z.GetProperty("id").GetString()!, z => z.GetProperty("name").GetString()!, StringComparer.Ordinal),
            new(StringComparer.Ordinal) { ["A"] = "Archivní záznam", ["B"] = "Průchodový záznam", ["C"] = "Otisk pečetě" },
            "Doložený původní aktivátor tohoto otisku patří mezi uvedené postavy. Samostatná stopa nepotvrzuje tým.",
            "Scénářový žeton", "Přijal jsi scénářovou minulost. Pečeť byla narušena před začátkem tvých tahů.",
            "Ozvěna", "Do konce případu nemáš fyzické akce ani předávání předmětů. Můžeš pátrat, posílat známé stopy a závěrečně hlasovat.",
            "Smrtící čin", "Tento čin může usmrtit postavu a do konce případu ji změnit v Ozvěnu. Zůstane jí pátrání, vlastní vzkazy a závěrečný hlas, ztratí fyzické akce a předávání předmětů.",
            definition.GetProperty("chapters").EnumerateArray().Select(c => c.GetProperty("title").GetString()!).ToArray(),
            // Original scheduled flavor asserts several events without evidence.
            // Preserve its existing factual fallback instead of publishing those claims.
            Enumerable.Repeat("Další kapitola případu je otevřená. Rozhodují vlastní doložené stopy a potvrzené plány.", 6).ToArray(),
            "Strážci v závěrečném hlasování označili potřebný počet původních aktivátorů rituálu. Rekonstrukce ukazuje skutečně zapsané kroky.",
            "Závěrečné hlasování neodhalilo potřebný počet spiklenců. Rekonstrukce nyní odděluje skutečné kroky od tvrzení.");
    }
}
