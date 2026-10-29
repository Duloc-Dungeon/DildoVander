using Kufr.Backend.Features.Customers;
using Microsoft.Extensions.Configuration;

// Explicit, one-request paid regression. Never included in the offline suite.
internal static class CharacterPortraitLiveTests
{
    internal static async Task RunAsync(string output)
    {
        var configuration = new ConfigurationBuilder().AddEnvironmentVariables().Build();
        if (string.IsNullOrWhiteSpace(configuration["OPENAI_API_KEY"]))
        {
            throw new InvalidOperationException("OPENAI_API_KEY is required for the explicit paid portrait check.");
        }
        var input = new CharacterUserInput("Harry Potter", "Harry Potter");
        var enrichment = new CharacterEnrichment("Harry Potter", "Harry Potter",
            "Harry is a young fictional wizard with messy black hair, round glasses, green eyes and a lightning-shaped forehead scar.",
            [], [], [], false, [], "fixture", "fixture", 1, 1, "synthetic");
        var job = new CharacterPortraitJob("synthetic", "portrait-live", 1, "synthetic", 1, input, enrichment);
        using var provider = new OpenAiCharacterPortraitProvider(configuration);
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        try
        {
            var image = await provider.GeneratePortraitAsync(job, deadline.Token);
            await File.WriteAllBytesAsync(output, image, deadline.Token);
            Console.WriteLine("Harry Potter portrait generated through the application provider: " + Path.GetFullPath(output));
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("Portrait live check failed: " + CharacterProviderFailure.Code(error));
            Environment.ExitCode = 1;
        }
    }
}
