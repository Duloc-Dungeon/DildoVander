using Kufr.Backend.Features.Customers;
using SkiaSharp;
using System.Net;
using System.Text;
using System.Text.Json;

internal static class CharacterPortraitFallbackTests
{
    internal static async Task RunAsync()
    {
        await FindsConservativeCosplayAsync();
        await RejectsKnownWorldHomonymAsync();
        await RejectsUnsafeTransportResponsesAsync();
        await RejectsMalformedAndOversizedDataAsync();
        await CancellationAndClarificationAsync();
        SourcePolicy();
        Console.WriteLine("Character portrait fallback tests passed.");
    }

    private static async Task FindsConservativeCosplayAsync()
    {
        var calls = new List<string>();
        using var http = new HttpClient(new Handler((request, token) =>
        {
            var url = request.RequestUri!.AbsoluteUri;
            calls.Add(url);
            if (url.Contains("api.php", StringComparison.Ordinal))
            {
                Check(request.Headers.UserAgent.ToString().Contains("Wikimedia Commons character portrait fallback", StringComparison.Ordinal),
                    "request identifies the application with a descriptive User-Agent");
                Check(url.Contains("iiextmetadatafilter=", StringComparison.Ordinal)
                    && !url.Contains("iemetadatafilter=", StringComparison.Ordinal)
                    && !url.Contains("iiurlparam=", StringComparison.Ordinal), "Commons API uses supported metadata parameters");
                var second = url.Contains("cosplay", StringComparison.Ordinal);
                Check(Uri.UnescapeDataString(url).Contains("\"Harry Potter\"", StringComparison.Ordinal),
                    "canonical enrichment world is part of both quoted search requests");
                return Task.FromResult(Json(new { query = new { pages = second
                    ? new[] { Page("File:Voldemort cosplay at Brussels.jpg", "https://upload.wikimedia.org/wikipedia/commons/a/ab/portrait.jpg", "image/jpeg", "CC BY-SA 2.0", "<b>Artist</b> &amp; Friend", "Cosplay of Voldemort in Harry Potter", "Characters from Harry Potter") }
                    : new[] { Page("File:Voldemort poster.jpg", "https://upload.wikimedia.org/wikipedia/commons/a/ab/poster.jpg", "image/jpeg", "CC BY-SA 2.0", "Artist"),
                        Page("File:Tom Riddle.jpg", "https://upload.wikimedia.org/wikipedia/commons/a/ab/actor.jpg", "image/jpeg", "CC BY-SA 2.0", "Artist") } } }));
            }
            Check(url.EndsWith("portrait.jpg", StringComparison.Ordinal), "only selected cosplay candidate is downloaded");
            var content = new ByteArrayContent(Image(1600, 2400));
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }));
        using var provider = new WikimediaCharacterPortraitFallbackProvider(http);
        var result = await provider.FindAsync(Job("Voldemort", version: "Brussels"), CancellationToken.None);
        Check(result is not null && calls.Count == 3, "fallback query finds a relevant cosplay after unqualified hits are rejected");
        Check(result!.Source!.Title == "File:Voldemort cosplay at Brussels.jpg"
            && result.Source.Author == "Artist & Friend" && result.Source.License == "CC BY-SA 2.0"
            && result.Source.LicenseUrl.StartsWith("https://creativecommons.org/licenses/", StringComparison.Ordinal), "attribution metadata is plain text and source has usable license link");
        using var decoded = SKBitmap.Decode(result.Image);
        Check(decoded is not null && decoded.Width == 768 && decoded.Height == 1152,
            "large source is cropped to the portrait ratio and scaled to bounded decodable webp dimensions");
        Check(calls.Take(2).All(call => call.StartsWith("https://commons.wikimedia.org/w/api.php?", StringComparison.Ordinal)), "only fixed Commons API is queried");
        Check(calls.Take(2).All(call => call.Contains("Brussels", StringComparison.Ordinal)),
            "explicit version is preserved in both search queries and candidate relevance");
    }

    private static async Task RejectsKnownWorldHomonymAsync()
    {
        var downloads = 0;
        var searches = 0;
        using var http = new HttpClient(new Handler((request, token) =>
        {
            if (request.RequestUri!.AbsolutePath == "/w/api.php")
            {
                searches++;
                var page = Page("File:Alex cosplay portrait.jpg", "https://upload.wikimedia.org/wikipedia/commons/a/ab/alex.jpg",
                    "image/jpeg", "CC BY 4.0", "Artist", "A Fiction-B cosplay costume.", "Characters from Fiction-B");
                return Task.FromResult(Json(new { query = new { pages = new[] { page } } }));
            }
            downloads++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }));
        using var provider = new WikimediaCharacterPortraitFallbackProvider(http);
        var result = await provider.FindAsync(Job("Alex", world: "Fiction-A"), CancellationToken.None);
        Check(result is null && searches == 2 && downloads == 0,
            "matching title is rejected when Commons description and categories identify a different franchise");

        var invalidVariantCalls = 0;
        using var invalidHttp = new HttpClient(new Handler((request, token) =>
        {
            invalidVariantCalls++;
            return Task.FromResult(Json(new { query = new { pages = Array.Empty<object>() } }));
        }));
        using var invalidProvider = new WikimediaCharacterPortraitFallbackProvider(invalidHttp);
        var invalidVariant = await invalidProvider.FindAsync(Job("Hermione", version: "Order of Phoenix?"), CancellationToken.None);
        Check(invalidVariant is null && invalidVariantCalls == 0,
            "invalid nonempty variant is rejected instead of silently ignored in broad name search");
    }

    private static async Task RejectsUnsafeTransportResponsesAsync()
    {
        var searches = 0;
        using (var http = new HttpClient(new Handler((request, token) =>
        {
            if (request.RequestUri!.Host != "commons.wikimedia.org") { throw new InvalidOperationException("unsafe URL sent"); }
            searches++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests));
        })))
        using (var provider = new WikimediaCharacterPortraitFallbackProvider(http))
        {
            Check(await provider.FindAsync(Job("Hermione"), CancellationToken.None) is null && searches == 1, "429 ends without retry or second search");
        }
        Check(!WikimediaCharacterPortraitFallbackProvider.IsPublicAddress(IPAddress.Loopback)
            && !WikimediaCharacterPortraitFallbackProvider.IsPublicAddress(IPAddress.Parse("10.1.2.3"))
            && !WikimediaCharacterPortraitFallbackProvider.IsPublicAddress(IPAddress.Parse("169.254.1.1"))
            && !WikimediaCharacterPortraitFallbackProvider.IsPublicAddress(IPAddress.Parse("::1"))
            && !WikimediaCharacterPortraitFallbackProvider.IsPublicAddress(IPAddress.Parse("::ffff:127.0.0.1"))
            && !WikimediaCharacterPortraitFallbackProvider.IsPublicAddress(IPAddress.Parse("2002::1"))
            && WikimediaCharacterPortraitFallbackProvider.IsPublicAddress(IPAddress.Parse("8.8.8.8")), "DNS address policy blocks local and private destinations");
        Check(!CharacterPortraitSourcePolicy.IsValid(new("https://example.com/wiki/File:X", "X portrait", "A", "CC BY 4.0", "https://creativecommons.org/licenses/by/4.0/")), "persistence policy rejects non-Commons source");
    }

    private static async Task CancellationAndClarificationAsync()
    {
        var calls = 0;
        using var http = new HttpClient(new Handler((request, token) =>
        {
            calls++;
            return Task.FromResult(Json(new { query = new { pages = Array.Empty<object>() } }));
        }));
        using var provider = new WikimediaCharacterPortraitFallbackProvider(http);
        Check(await provider.FindAsync(Job("Ambiguous", needsClarification: true), CancellationToken.None) is null && calls == 0,
            "clarification jobs never query Commons");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var propagated = false;
        try { await provider.FindAsync(Job("Hermione"), cancellation.Token); }
        catch (OperationCanceledException) { propagated = true; }
        Check(propagated, "caller cancellation propagates");
    }

    private static async Task RejectsMalformedAndOversizedDataAsync()
    {
        foreach (var content in new HttpContent[]
        {
            new StringContent("not json"),
            new ByteArrayContent(new byte[1024 * 1024 + 1])
        })
        {
            using var http = new HttpClient(new Handler((request, token) => Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK) { Content = content }))) { Timeout = Timeout.InfiniteTimeSpan };
            using var provider = new WikimediaCharacterPortraitFallbackProvider(http);
            Check(await provider.FindAsync(Job("Hermione"), CancellationToken.None) is null,
                "malformed or oversized metadata response fails closed");
        }

        foreach (var (url, bytes, mediaType) in new[]
        {
            ("https://example.org/wikipedia/commons/a/ab/evil.jpg", Image(), "image/jpeg"),
            ("https://upload.wikimedia.org/wikipedia/commons/a/ab/large.jpg", Image(4097, 32), "image/jpeg"),
            ("https://upload.wikimedia.org/wikipedia/commons/a/ab/oversize.jpg", new byte[8 * 1024 * 1024 + 1], "image/jpeg"),
            ("https://upload.wikimedia.org/wikipedia/commons/a/ab/html.jpg", Encoding.UTF8.GetBytes("<html>not an image</html>"), "text/html")
        })
        {
            var downloads = 0;
            using var http = new HttpClient(new Handler((request, token) =>
            {
                if (request.RequestUri!.AbsolutePath == "/w/api.php")
                {
                    var page = Page("File:Hermione cosplay portrait.jpg", url, "image/jpeg", "CC BY 4.0", "Artist");
                    return Task.FromResult(Json(new { query = new { pages = new[] { page } } }));
                }
                downloads++;
                var content = new ByteArrayContent(bytes);
                content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(mediaType);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
            })) { Timeout = Timeout.InfiniteTimeSpan };
            using var provider = new WikimediaCharacterPortraitFallbackProvider(http);
            var result = await provider.FindAsync(Job("Hermione"), CancellationToken.None);
            if (url.Contains("example.org", StringComparison.Ordinal))
            {
                Check(downloads == 0 && result is null, "hostile image URL is rejected before network request");
            }
            else
            {
                Check(downloads == 1 && result is null, "downloaded invalid image bytes or dimensions are rejected");
            }
        }
    }

    private static void SourcePolicy()
    {
        var good = new CharacterPortraitSource("https://commons.wikimedia.org/wiki/File:Hermione_portrait.jpg",
            "File:Hermione portrait.jpg", "Artist", "CC BY-SA 4.0",
            "https://creativecommons.org/licenses/by-sa/4.0/");
        Check(CharacterPortraitSourcePolicy.IsValid(good), "valid reusable attribution passes policy");
        Check(!CharacterPortraitSourcePolicy.IsValid(good with { Author = "<script>bad</script>" })
            && !CharacterPortraitSourcePolicy.IsValid(good with { License = "Unknown" })
            && !CharacterPortraitSourcePolicy.IsValid(good with { LicenseUrl = "http://creativecommons.org/licenses/by/4.0/" })
            && !CharacterPortraitSourcePolicy.IsValid(good with { Url = "https://commons.wikimedia.org/wiki/File:Portrait.jpg?redirect=1" })
            && !CharacterPortraitSourcePolicy.IsValid(good with { LicenseUrl = "https://commons.wikimedia.org/wiki/Page" }),
            "markup, unknown license, insecure URLs, query strings and non-file Commons pages fail persistence validation");
    }

    private static CharacterPortraitJob Job(string name, bool needsClarification = false, string? version = null,
        string world = "Harry Potter")
    {
        var input = new CharacterUserInput(name, world, version);
        var enrichment = new CharacterEnrichment(name, world, "", [], [], [], needsClarification,
            [], "fixture", "fixture", 0, 1, "fixture");
        return new("account", "profile", 1, "lease", 1, input, enrichment);
    }

    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json")
    };

    private static object Page(string title, string url, string mime, string license, string artist,
        string description = "Harry Potter character", string categories = "") => new
    {
        title,
        imageinfo = new[] { new
        {
            thumburl = url, thumbmime = mime, mime, extmetadata = new
            {
                Artist = new { value = artist }, LicenseShortName = new { value = license },
                LicenseUrl = new { value = "https://creativecommons.org/licenses/by-sa/2.0/" },
                ImageDescription = new { value = description }, Categories = new { value = categories }
            }
        } }
    };

    private static byte[] Image(int width = 180, int height = 270)
    {
        using var bitmap = new SKBitmap(width, height);
        bitmap.Erase(SKColors.DarkSlateBlue);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 85);
        return data.ToArray();
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) { throw new InvalidOperationException(message); }
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
