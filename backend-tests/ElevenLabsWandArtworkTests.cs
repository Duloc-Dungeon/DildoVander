using System.Net;
using System.Text;
using System.Text.Json;
using Kufr.Backend.Features.Keepsakes;
using Kufr.Backend.Features.Wands;
using SkiaSharp;

internal static class ElevenLabsWandArtworkTests
{
    internal static async Task RunAsync()
    {
        var request = Request();
        using var handler = new ImageHandler();
        using var secondary = new ElevenLabsWandArtwork(new HttpClient(handler), "offline-secret");
        var primaryCalls = 0;
        using var provider = new WandArtworkFallbackProvider(new Provider((_, _) =>
        {
            primaryCalls++;
            throw new HttpRequestException("offline primary failure");
        }), secondary.GenerateAsync);
        var output = await provider.GenerateAsync(request, default);
        Check(primaryCalls == 1 && handler.Creates == 1 && handler.Polls == 1 && handler.Downloads == 1,
            "one primary image failure dispatches exactly one ElevenLabs generation");
        Check(output.Provider == "elevenlabs" && output.Model == "gpt-image-1.5"
            && !output.ContentResponse.Contains("content_url"), "fallback provenance omits expiring signed media URLs");
        var prompt = handler.Prompt ?? throw new Exception("Image prompt missing.");
        Check(prompt.Contains("Jasan") && prompt.Contains("Jiskra") && prompt.Contains("Vyřezávaná rukojeť")
            && !prompt.Contains('{') && !prompt.Contains('}') && !prompt.Contains("\\u", StringComparison.Ordinal)
            && !prompt.Contains("PRIVATE") && !prompt.Contains("SECRET"),
            "secondary receives a readable visual description without a JSON block, identity or conversation");

        var fallbackCalls = 0;
        using var successful = new WandArtworkFallbackProvider(new Provider((_, _) => Task.FromResult(output)), (_, _) =>
        { fallbackCalls++; return Task.FromResult(output); });
        _ = await successful.GenerateAsync(request, default);
        Check(fallbackCalls == 0, "successful primary artwork does not trigger a second provider");

        using var failing = new WandArtworkFallbackProvider(new Provider((_, _) => throw new HttpRequestException()), (_, _) =>
        { fallbackCalls++; throw new KeepsakeException("elevenlabs_artwork_failed"); });
        await ExpectCodeAsync(() => failing.GenerateAsync(request, default), "elevenlabs_artwork_failed");
        Check(fallbackCalls == 1, "secondary failure never recursively retries");

        foreach (var excluded in new[] { request with { GenerationKind = "base" }, request with { NormalizedPhoto = [1, 2] } })
        {
            try { await failing.GenerateAsync(excluded, default); throw new Exception("Unexpected fallback."); }
            catch (HttpRequestException) { }
        }
        Check(fallbackCalls == 1, "story and visitor-photo failures never share their data with fallback");
        Check(!WandArtworkFallbackProvider.CanFallback(new KeepsakeException("publication_epoch_changed"))
            && !WandArtworkFallbackProvider.CanFallback(new OperationCanceledException())
            && !WandArtworkFallbackProvider.CanFallback(new System.ClientModel.ClientResultException("moderated")),
            "revocation, cancellation and policy refusals cannot authorize secondary image work");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        try { await failing.GenerateAsync(request, cancelled.Token); throw new Exception("Unexpected fallback."); }
        catch (HttpRequestException) { }
        Check(fallbackCalls == 1, "cancelled operation does not dispatch fallback");

        using var fencedHandler = new ImageHandler();
        using var fenced = new ElevenLabsWandArtwork(new HttpClient(fencedHandler), "offline-secret");
        await ExpectCodeAsync(() => fenced.GenerateAsync(request with { RecordStage = (_, _, _) => false }, default), "publication_epoch_changed");
        Check(fencedHandler.Creates == 0, "current publication authority is required before secondary dispatch");
        Check(ElevenLabsWandArtwork.IsAllowedDownload("https://storage.googleapis.com/bucket/image.png?signature=offline")
            && !ElevenLabsWandArtwork.IsAllowedDownload("http://127.0.0.1/image.png")
            && !ElevenLabsWandArtwork.IsAllowedDownload("https://storage.googleapis.com.evil.test/image.png")
            && !ElevenLabsWandArtwork.IsAllowedDownload("https://user@storage.googleapis.com/image.png"),
            "signed media download uses HTTPS allowlisted hosts without credentials or redirects");
        Console.WriteLine("ElevenLabs artwork fallback: one attempt, visual-only context, authority, cancellation and PNG checks passed (offline HTTP).");
    }

    private static KeepsakeProviderRequest Request()
    {
        var wand = new GeneratedWandPublicSnapshot("PRIVATE_WAND", 1, "PRIVATE_NAME", "Jasan", "Jiskra", "PRIVATE_TONE",
            "SECRET_LORE", "Vyřezávaná rukojeť", "SECRET_AFFINITY", "SECRET_WEAKNESS", []);
        var snapshot = new KeepsakeIssueSnapshot("PRIVATE_OUTCOME", "PRIVATE_DEVICE", 1, "PRIVATE_CATALOG", "PRIVATE_NAME",
            "PRIVATE_FACTS", "PRIVATE_EVIDENCE", GeneratedWand: wand);
        return new("PRIVATE_ID", snapshot, new(), "PRIVATE_JOB", null, null, null, null, null) { GenerationKind = "wand_artwork" };
    }

    private static byte[] TransparentPng()
    {
        using var bitmap = new SKBitmap(4, 4);
        bitmap.Erase(SKColors.Transparent); bitmap.SetPixel(1, 1, SKColors.Brown);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private sealed class ImageHandler : HttpMessageHandler
    {
        internal int Creates, Polls, Downloads;
        internal string? Prompt;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.RequestUri!.Host == "storage.googleapis.com")
            {
                Downloads++;
                Check(!request.Headers.Contains("xi-api-key"), "provider credential is not forwarded to media host");
                var media = new ByteArrayContent(TransparentPng());
                media.Headers.ContentType = new("image/png");
                return new(HttpStatusCode.OK) { Content = media };
            }
            Check(request.Headers.GetValues("xi-api-key").Single() == "offline-secret", "API uses server-side credential");
            if (request.Method == HttpMethod.Post)
            {
                Creates++;
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
                Prompt = body.RootElement.GetProperty("prompt").GetString();
                Check(body.RootElement.GetProperty("background").GetString() == "transparent"
                    && body.RootElement.GetProperty("aspect_ratio").GetString() == "2:3", "fallback preserves true alpha and portrait composition");
                return Json("""{"id":"offline_generation","status":"pending"}""");
            }
            Polls++;
            return Json("""{"id":"offline_generation","status":"completed","content_mime_type":"image/png","content_url":"https://storage.googleapis.com/offline/wand.png?signature=offline"}""");
        }
        private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
        { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    private sealed class Provider(Func<KeepsakeProviderRequest, CancellationToken, Task<KeepsakeProviderOutput>> run) : IKeepsakeProvider
    {
        public Task<KeepsakeProviderOutput> GenerateAsync(KeepsakeProviderRequest request, CancellationToken token) => run(request, token);
    }
    private static async Task ExpectCodeAsync(Func<Task<KeepsakeProviderOutput>> action, string code)
    {
        try { await action(); throw new Exception("Expected " + code); }
        catch (KeepsakeException error) when (error.Code == code) { }
    }
    private static void Check(bool valid, string message) { if (!valid) { throw new Exception(message); } }
}
