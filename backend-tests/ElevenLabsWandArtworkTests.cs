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
        await CompatibilityRejectionsAsync(request, output);
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

    private static async Task CompatibilityRejectionsAsync(KeepsakeProviderRequest request, KeepsakeProviderOutput fixtureOutput)
    {
        const string unsupportedBackground = """{"error":{"type":"invalid_request_error","code":"invalid_value","param":"background","message":"Transparent backgrounds are not supported for this model. PRIVATE_PROVIDER_BODY"}}""";
        var diagnostics = new List<string>();
        using var rejectedHttp = new RejectedImageHandler(HttpStatusCode.BadRequest, unsupportedBackground);
        using var secondaryHttp = new ImageHandler();
        using var secondary = new ElevenLabsWandArtwork(new HttpClient(secondaryHttp), "offline-secret");
        using var primary = new OpenAiKeepsakeProvider(new HttpClient(rejectedHttp), "offline-primary", "unused-story", "gpt-image-2");
        using var fallback = new WandArtworkFallbackProvider(primary, secondary.GenerateAsync);
        var actual = await fallback.GenerateAsync(request with
        {
            RecordStage = (_, phase, content) => { if (phase == "rejected") { diagnostics.Add(content); } return true; }
        }, default);
        Check(rejectedHttp.Requests == 1 && secondaryHttp.Creates == 1 && actual.Provider == "elevenlabs"
            && actual.Model == "gpt-image-1.5", "an explicit preview-model parameter rejection makes one stable-model secondary request");
        using (var diagnostic = JsonDocument.Parse(diagnostics.Single()))
        {
            Check(diagnostic.RootElement.GetProperty("category").GetString() == "unsupported_parameter"
                && diagnostic.RootElement.GetProperty("parameter").GetString() == "background"
                && !diagnostics.Single().Contains("PRIVATE_PROVIDER_BODY"),
                "compatibility diagnostics retain only fixed classification and allowlisted parameter, never provider text");
        }

        foreach (var supported in new[]
        {
            """{"error":{"type":"invalid_request_error","code":"unsupported_parameter","param":"output_format","message":"PRIVATE_PROVIDER_BODY"}}""",
            """{"error":{"type":"invalid_request_error","code":"model_not_found","param":"model","message":"Unavailable model. PRIVATE_PROVIDER_BODY"}}""",
            """{"error":{"type":"invalid_request_error","code":null,"param":"background","message":"Invalid value transparent. Supported values are auto and opaque."}}"""
        })
        {
            var calls = 0;
            using var http = new RejectedImageHandler(HttpStatusCode.BadRequest, supported);
            using var sdk = new OpenAiKeepsakeProvider(new HttpClient(http), "offline-primary", "unused-story", "gpt-image-2");
            using var provider = new WandArtworkFallbackProvider(sdk, (_, _) => { calls++; return Task.FromResult(fixtureOutput); });
            _ = await provider.GenerateAsync(request, default);
            Check(http.Requests == 1 && calls == 1, "allowlisted unsupported parameter/model receives one compatibility fallback");
        }

        foreach (var rejected in new (HttpStatusCode Status, string Body)[]
        {
            (HttpStatusCode.BadRequest, """{"error":{"type":"invalid_request_error","code":"content_policy_violation","param":"background","message":"not supported"}}"""),
            (HttpStatusCode.BadRequest, """{"error":{"type":"invalid_request_error","code":"unsupported_value","param":"background","message":"not supported due to safety policy"}}"""),
            (HttpStatusCode.BadRequest, """{"error":{"type":"invalid_request_error","code":"unsupported_parameter","param":"prompt","message":"not supported"}}"""),
            (HttpStatusCode.BadRequest, """{"error":{"type":"invalid_request_error","code":"invalid_value","param":"background","message":"Invalid request. PRIVATE_PROVIDER_BODY"}}"""),
            (HttpStatusCode.BadRequest, """{"error":{"type":"invalid_request_error","code":3,"param":"background","message":"not supported"}}"""),
            (HttpStatusCode.BadRequest, """{"error":{"type":"invalid_request_error","code":"content_policy_violation","code":"unsupported_value","param":"background"}}"""),
            (HttpStatusCode.BadRequest, new string('x', 16 * 1024 + 1)),
            (HttpStatusCode.BadRequest, "not-json PRIVATE_PROVIDER_BODY"),
            (HttpStatusCode.Unauthorized, unsupportedBackground), (HttpStatusCode.Forbidden, unsupportedBackground)
        })
        {
            var calls = 0;
            var stages = new List<string>();
            using var http = new RejectedImageHandler(rejected.Status, rejected.Body);
            using var sdk = new OpenAiKeepsakeProvider(new HttpClient(http), "offline-primary", "unused-story", "gpt-image-2");
            using var provider = new WandArtworkFallbackProvider(sdk, (_, _) => { calls++; return Task.FromResult(fixtureOutput); });
            try
            {
                _ = await provider.GenerateAsync(request with
                { RecordStage = (_, phase, content) => { if (phase == "rejected") { stages.Add(content); } return true; } }, default);
                throw new Exception("An unclassified or denied request unexpectedly generated secondary artwork.");
            }
            catch (System.ClientModel.ClientResultException error)
            {
                Check(error.Status == (int)rejected.Status && !error.Message.Contains("PRIVATE_PROVIDER_BODY"),
                    "non-compatible failures retain the original status with sanitized SDK error content");
            }
            Check(calls == 0 && http.Requests == 1 && stages.All(content => !content.Contains("PRIVATE_PROVIDER_BODY")),
                "policy/auth/unknown/malformed/oversized failures never dispatch fallback or persist raw provider bodies");
        }

        var revokedCalls = 0;
        using var revokedHttp = new RejectedImageHandler(HttpStatusCode.BadRequest, unsupportedBackground);
        using var revokedSdk = new OpenAiKeepsakeProvider(new HttpClient(revokedHttp), "offline-primary", "unused-story", "gpt-image-2");
        using var revoked = new WandArtworkFallbackProvider(revokedSdk, (_, _) => { revokedCalls++; return Task.FromResult(fixtureOutput); });
        await ExpectCodeAsync(() => revoked.GenerateAsync(request with { RecordStage = (_, phase, _) => phase != "rejected" }, default),
            "publication_epoch_changed");
        Check(revokedCalls == 0, "publication authority revoked during compatibility classification blocks secondary dispatch");
        Check(!WandArtworkFallbackProvider.CanFallback(new HttpRequestException("unauthorized", null, HttpStatusCode.Unauthorized))
            && !WandArtworkFallbackProvider.CanFallback(new HttpRequestException("bad request", null, HttpStatusCode.BadRequest)),
            "HTTP transport exceptions cannot bypass authentication or unclassified bad requests");
        using var cancellation = new CancellationTokenSource();
        using var canceledHttp = new RejectedImageHandler(HttpStatusCode.BadRequest, unsupportedBackground, cancellation.Cancel);
        using var canceledSdk = new OpenAiKeepsakeProvider(new HttpClient(canceledHttp), "offline-primary", "unused-story", "gpt-image-2");
        var canceledFallbacks = 0;
        using var canceled = new WandArtworkFallbackProvider(canceledSdk, (_, _) => { canceledFallbacks++; return Task.FromResult(fixtureOutput); });
        try
        {
            await canceled.GenerateAsync(request, cancellation.Token);
            throw new Exception("Cancellation during rejection classification was ignored.");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        Check(canceledHttp.Requests == 1 && canceledFallbacks == 0, "cancellation while reading the primary rejection never starts secondary image work");
    }

    private sealed class RejectedImageHandler(HttpStatusCode status, string body, Action? beforeReturn = null) : HttpMessageHandler
    {
        internal int Requests;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requests++;
            Check(request.RequestUri!.Host == "api.openai.com" && request.RequestUri.AbsolutePath == "/v1/images/generations",
                "compatibility checks use the actual image SDK endpoint");
            using var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            Check(payload.RootElement.GetProperty("model").GetString() == "gpt-image-2"
                && payload.RootElement.GetProperty("background").GetString() == "transparent",
                "primary keeps its configured model and real transparent PNG request");
            beforeReturn?.Invoke();
            return new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
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
