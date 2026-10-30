using System.Security.Cryptography;
using System.Text.Json;
using Kufr.Backend.Features.Customers;
using SkiaSharp;

internal static class CharacterPortraitBackupTests
{
    internal static void Run()
    {
        var assembly = typeof(CharacterPortraitFallbackProvider).Assembly;
        using var stream = assembly.GetManifestResourceStream("Kufr.Backend.CharacterPortraits.catalog.json")
            ?? throw new Exception("Generated portrait catalog was not packaged.");
        var catalog = JsonSerializer.Deserialize<CharacterPortraitFallbackProvider.Entry[]>(stream,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        Check(catalog.Length == 29 && catalog.Count(item => item.Name == "Dementor") == 3,
            "reviewed roster includes three different Dementors; Hermione generation remains refused");
        Check(CharacterPortraitFallbackProvider.FindGenerated(Job("Hermiona")) is null,
            "missing Hermione backup must never resolve to another character");
        foreach (var entry in catalog)
        {
            Check(entry.Reviewed, "only reviewed portraits are shipped");
            using var resource = assembly.GetManifestResourceStream("Kufr.Backend.CharacterPortraits." + entry.Id + ".webp")
                ?? throw new Exception("Missing packaged portrait " + entry.Id);
            using var image = SKBitmap.Decode(resource);
            Check(image is not null && image.Width >= 128 && image.Height > image.Width, "packaged portrait decodes in vertical format");
            foreach (var name in entry.Aliases.Append(entry.Name))
            {
                var bytes = CharacterPortraitFallbackProvider.FindGenerated(Job(name));
                Check(bytes is not null && catalog.Where(item => item.Name == entry.Name)
                    .Any(item => item.Sha256.Equals(Convert.ToHexString(SHA256.HashData(bytes)), StringComparison.OrdinalIgnoreCase)),
                    "canonical name and aliases resolve to the reviewed character only");
            }
        }
        Check(CharacterPortraitFallbackProvider.FindGenerated(Job("Harry Potter") with { Input = new("Harry Potter", "Unrelated world") }) is null,
            "different worlds cannot borrow a familiar character's image");
        Check(CharacterPortraitFallbackProvider.FindGenerated(Job("Harry Potter") with { Input = new("Harry Potter", "Harry Potter", "adult epilogue") }) is null,
            "explicit alternate appearance is never replaced by the default backup");
        var variants = Enumerable.Range(0, 60).Select(index => CharacterPortraitFallbackProvider.FindGenerated(Job("Mozkomor") with { ProfileId = "synthetic-" + index }))
            .Select(bytes => Convert.ToHexString(SHA256.HashData(bytes!))).Distinct().Count();
        Check(variants == 3, "all three Dementor paintings are reachable across profiles");
        Console.WriteLine("Generated portrait backups: roster, aliases, packaged pixels, hashes and version fences passed.");
    }

    private static CharacterPortraitJob Job(string name) => new("synthetic", "synthetic", 1, "synthetic", 1,
        new(name, "Harry Potter"), new(name, "Harry Potter", "Public fictional character.",
            [], [], [], false, [], "fixture", "fixture", 1, 1, "synthetic"));
    private static void Check(bool value, string message) { if (!value) { throw new Exception(message); } }
}
