using Kufr.Backend.Features.Images;

namespace Kufr.Backend.Features.Customers;

// Only the public fictional identity crosses this boundary. Welcome text keeps
// its existing provider; selecting Gemini never changes speech or wand artwork.
internal sealed class GeminiCharacterPortraitProvider : ICharacterPortraitProvider, IDisposable
{
    public bool RequiresCurrentAiConsent => true;
    private readonly HttpClient http;
    private readonly string? key;
    private readonly string model;
    private readonly OpenAiCharacterPortraitProvider textProvider;

    public GeminiCharacterPortraitProvider(IConfiguration configuration) : this(configuration,
        new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
        { Timeout = Timeout.InfiniteTimeSpan }) { }

    internal GeminiCharacterPortraitProvider(IConfiguration configuration, HttpClient http)
    {
        this.http = http;
        key = configuration["GEMINI_API_KEY"];
        model = configuration["Gemini:ImageModel"] ?? GeminiImageGenerator.Model;
        textProvider = new OpenAiCharacterPortraitProvider(configuration);
    }

    public async Task<byte[]> GeneratePortraitAsync(CharacterPortraitJob job, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(key)) { throw new CharacterException("portrait_provider_unavailable", 503); }
        using var sceneDeadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        sceneDeadline.CancelAfter(TimeSpan.FromSeconds(25));
        var scene = await textProvider.GenerateVisualSceneAsync(job, sceneDeadline.Token);
        var bytes = await new GeminiImageGenerator(http, key, model).GenerateAsync(
            OpenAiCharacterPortraitProvider.BuildPortraitPrompt(job, scene), "2:3", null, token);
        return OpenAiCharacterPortraitProvider.NormalizePortrait(bytes);
    }

    public Task<string?> GenerateMessageAsync(CharacterUserInput input, CharacterEnrichment? enrichment,
        string creativePrompt, CancellationToken token) => textProvider.GenerateMessageAsync(input, enrichment, creativePrompt, token);

    public Task<CharacterWelcome?> GenerateWelcomeAsync(CharacterPortraitJob job, CancellationToken token) =>
        ((ICharacterPortraitProvider)textProvider).GenerateWelcomeAsync(job, token);

    public Task<CharacterPortraitExperience> GenerateExperienceAsync(CharacterPortraitJob job,
        string loadingPrompt, bool includeLoading, CancellationToken token) =>
        textProvider.GenerateExperienceAsync(job, loadingPrompt, includeLoading, token);

    public void Dispose() { http.Dispose(); textProvider.Dispose(); }
}
