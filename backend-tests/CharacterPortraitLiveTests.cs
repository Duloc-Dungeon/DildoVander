using Kufr.Backend.Features.Customers;
using Microsoft.Extensions.Configuration;

// Explicit, one-request paid regression. Never included in the offline suite.
internal static class CharacterPortraitLiveTests
{
    internal static async Task RunChronicleAsync(string referencePath, string output)
    {
        var configuration = new ConfigurationBuilder().AddEnvironmentVariables().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Chronicle:ProviderMode"] = "openai", ["Chronicle:ImageProvider"] = "gemini" }).Build();
        using var reference = SkiaSharp.SKBitmap.Decode(await File.ReadAllBytesAsync(referencePath));
        using var image = SkiaSharp.SKImage.FromBitmap(reference);
        using var png = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
        var visual = System.Text.Json.JsonSerializer.Serialize(new
        {
            character = new { name = "Harry Potter", world = "Harry Potter" },
            portraitReferenceBase64 = Convert.ToBase64String(png.ToArray())
        });
        var scene = new Kufr.Backend.Features.Chronicle.ChronicleEventInput("duel", "synthetic", "synthetic", 1,
            "Slavnostní portrét", "Harry se slavnostně postavil vedle Hedviky. Hedvika měla stejné kulaté brýle a tvářila se ještě vážněji.",
            [new("synthetic", "Harry", "participant", "Harry Potter", VisualSnapshotJson: visual)],
            Result: "Slavnostní portrét dokončen.", IllustrationRequested: true);
        using var illustrator = new Kufr.Backend.Features.Keepsakes.ChronicleIllustrator(configuration);
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var result = await illustrator.GenerateAsync(scene, deadline.Token);
        await File.WriteAllBytesAsync(output, result.Bytes, deadline.Token);
        Console.WriteLine("Gemini chronicle with frozen reference saved: " + Path.GetFullPath(output));
    }

    internal static async Task RunGeminiAsync(string output, string name, string world, string publicTraits)
    {
        var configuration = new ConfigurationBuilder().AddEnvironmentVariables().Build();
        var input = new CharacterUserInput(name, world);
        var enrichment = new CharacterEnrichment(name, world, publicTraits,
            [], [], [], false, [], "fixture", "fixture", 1, 1, "synthetic");
        var job = new CharacterPortraitJob("synthetic", "portrait-live", 1, "synthetic", 1, input, enrichment);
        using var provider = new GeminiCharacterPortraitProvider(configuration,
            new HttpClient(new LiveFailureHandler(output + ".error.txt", configuration["GEMINI_API_KEY"] ?? ""))
            { Timeout = Timeout.InfiniteTimeSpan });
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        try
        {
            var image = await provider.GeneratePortraitAsync(job, deadline.Token);
            await File.WriteAllBytesAsync(output, image, deadline.Token);
            Console.WriteLine("Gemini application portrait saved: " + Path.GetFullPath(output));
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("Gemini live check failed: " + CharacterProviderFailure.Code(error));
            Environment.ExitCode = 1;
        }
    }

    // Explicit synthetic diagnostic only. Never used by the application or CI.
    private sealed class LiveFailureHandler(string path, string key) : DelegatingHandler(new HttpClientHandler { AllowAutoRedirect = false })
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            // Synthetic fixtures only: retain the exact paid-test prompt for editorial comparison.
            await File.WriteAllTextAsync(path + ".request.json", await request.Content!.ReadAsStringAsync(token), token);
            var response = await base.SendAsync(request, token);
            if (!response.IsSuccessStatusCode)
            {
                var bytes = new byte[4096];
                using var stream = await response.Content.ReadAsStreamAsync(token);
                var count = await stream.ReadAtLeastAsync(bytes, bytes.Length, false, token);
                var detail = System.Text.Encoding.UTF8.GetString(bytes, 0, count);
                if (key.Length > 0) { detail = detail.Replace(key, "[redacted]", StringComparison.Ordinal); }
                await File.WriteAllTextAsync(path, detail, token);
                response.Content = new ByteArrayContent(bytes.AsSpan(0, count).ToArray());
                response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            }
            return response;
        }
    }

    internal static async Task RunAsync(string output, bool originalCharacter = false, bool adultCharacter = false)
    {
        var configuration = new ConfigurationBuilder().AddEnvironmentVariables().Build();
        if (string.IsNullOrWhiteSpace(configuration["OPENAI_API_KEY"]))
        {
            throw new InvalidOperationException("OPENAI_API_KEY is required for the explicit paid portrait check.");
        }
        var input = originalCharacter ? new CharacterUserInput("Mira Aster", "Original fiction") : new CharacterUserInput("Harry Potter", "Harry Potter");
        var enrichment = new CharacterEnrichment(input.Name, input.World,
            originalCharacter ? "Mira is an adult fictional librarian with red curly hair and a blue coat."
                : adultCharacter ? "Harry Potter as an adult in the story epilogue: a father and experienced wizard, with messy black hair, round glasses, green eyes and the familiar lightning-shaped forehead scar."
                : "Harry is a young fictional wizard with messy black hair, round glasses, green eyes and a lightning-shaped forehead scar.",
            [], [], [], false, [], "fixture", "fixture", 1, 1, "synthetic");
        var job = new CharacterPortraitJob("synthetic", "portrait-live", 1, "synthetic", 1, input, enrichment);
        using var provider = new OpenAiCharacterPortraitProvider(configuration);
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        try
        {
            var image = await provider.GeneratePortraitAsync(job, deadline.Token);
            await File.WriteAllBytesAsync(output, image, deadline.Token);
            Console.WriteLine(input.Name + " portrait generated through the application provider: " + Path.GetFullPath(output));
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("Portrait live check failed: " + CharacterProviderFailure.Code(error));
            Console.Error.WriteLine("Moderation stage: " + error.Data["portrait_moderation_stage"]);
            Console.Error.WriteLine("Moderation categories: " + error.Data["portrait_moderation_categories"]);
            Environment.ExitCode = 1;
        }
    }
}
