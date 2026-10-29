using Kufr.Backend.Features.Wands;

internal static class WorkshopPresentationTests
{
    internal static void Run()
    {
        var spells = new[] {
            new WandSpell("shield", "Pevný val", "Zadrží ránu. Potom se rozpadne."),
            new WandSpell("strike", "Jiskra", "Zasáhne cíl; stojí kapku síly."),
            new WandSpell("trick", "Mlžný krok", "Skryje stopu! Jen na chvíli.") };
        var wand = new MobileWandPublicResult("wand", "Mostní větev", "jasan", "jiskra", "klidná", "Příběh", "Větev", "mosty", "spěch", spells, "balanced", "wand-archetypes-v1");
        var state = new MobileWandWorkshopState("operation", "profile", 1, 1, "complete", null, [], wand,
            Messages: [new("answer", 1, "user", "Chci chránit most.", "voice", 1)]);
        var text = state.VerdictText();
        if (text.Count(character => character == '.') != 4 || !text.Contains("Chci chránit most", StringComparison.Ordinal)
            || spells.Any(spell => !text.Contains(spell.Name, StringComparison.Ordinal))
            || !text.Contains("Potom se rozpadne", StringComparison.Ordinal) || text.Contains('?') || text.Length > 1800)
        { throw new Exception("Workshop finale must use four sentences, saved spells and guest evidence without a follow-up question."); }
        var maximum = state with { Wand = wand with { Name = new('a', 100), Material = new('b', 200), Core = new('c', 200),
            Spells = spells.Select(spell => spell with { Name = new('d', 100), Description = new('e', 300) }).ToArray() },
            Messages = [new("answer", 1, "user", new('f', 1800), "voice", 1)] };
        if (maximum.VerdictText().Length > 1800) { throw new Exception("Saved finale exceeds the voice/transcript bound."); }
        Console.WriteLine("Workshop four-sentence saved-result presentation passed.");
    }
}
