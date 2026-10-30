using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Kufr.Backend.Features.Customers;
using Kufr.Backend.Features.Images;

// Explicit paid asset generation, never invoked by CI. Each item submits once;
// successful files are reused and failures require an explicit later invocation.
internal static class PortraitBackupGenerator
{
    internal sealed record Scene(string Id, string Name, string[] Aliases, string Appearance, string Joke, string? Style = null);
    internal static async Task RunAsync(string source, string destination, string model = GeminiImageGenerator.Model)
    {
        var key = Environment.GetEnvironmentVariable("GEMINI_API_KEY");
        if (string.IsNullOrWhiteSpace(key)) { throw new InvalidOperationException("GEMINI_API_KEY required."); }
        var scenes = JsonSerializer.Deserialize<Scene[]>(await File.ReadAllTextAsync(source),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? throw new InvalidDataException("Scenes missing.");
        if (scenes.Any(item => !Regex.IsMatch(item.Id, "\\A[a-z0-9-]{1,60}\\z"))
            || scenes.Select(item => item.Id).Distinct().Count() != scenes.Length) { throw new InvalidDataException("Invalid scene ids."); }
        Directory.CreateDirectory(destination);
        await Parallel.ForEachAsync(scenes, new ParallelOptions { MaxDegreeOfParallelism = 2 }, async (scene, token) =>
        {
            var path = Path.Combine(destination, scene.Id + ".webp");
            if (File.Exists(path)) { Console.WriteLine(scene.Id + ": existing output retained"); return; }
            var prompt = (scene.Style ?? "") + $"A richly detailed oil painting of {scene.Name} from the Harry Potter stories. {scene.Appearance} {scene.Joke} "
                + "One gentle absurd visual punchline played with a solemn face. Rich pigments, layered brushwork, finely painted details and warm sculptural chiaroscuro, "
                + "like an enchanted portrait in an old wizarding castle. Clear recognizable face or silhouette, vertical 2:3 composition, fully clothed and age-appropriate. No lettering or logos. Hands hold only the described everyday props.";
            try
            {
                using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                deadline.CancelAfter(TimeSpan.FromMinutes(3));
                var image = await new GeminiImageGenerator(http, key, model).GenerateAsync(prompt, "2:3", null, deadline.Token);
                var bytes = OpenAiCharacterPortraitProvider.NormalizePortrait(image);
                var receipt = new { scene.Id, scene.Name, scene.Aliases, world = "Harry Potter", model,
                    prompt, sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), reviewed = false };
                await File.WriteAllTextAsync(path + ".receipt.json", JsonSerializer.Serialize(receipt, new JsonSerializerOptions { WriteIndented = true }), token);
                await File.WriteAllBytesAsync(path, bytes, token);
                Console.WriteLine(scene.Id + ": generated");
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                Console.WriteLine(scene.Id + ": " + CharacterProviderFailure.Code(error));
            }
        });
    }
}
