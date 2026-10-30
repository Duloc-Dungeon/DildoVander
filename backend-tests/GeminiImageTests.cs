using System.Net;
using System.Text.Json;
using Kufr.Backend.Features.Images;
using Kufr.Backend.Features.Customers;
using SkiaSharp;
using Kufr.Backend.Features.Keepsakes;
using Kufr.Backend.Features.Chronicle;
using Microsoft.Extensions.Configuration;

internal static class GeminiImageTests
{
    internal static async Task RunAsync()
    {
        using var bitmap = new SKBitmap(128, 192);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.CornflowerBlue);
        using var image = SKImage.FromBitmap(bitmap);
        using var jpeg = image.Encode(SKEncodedImageFormat.Jpeg, 85);
        using var png = image.Encode(SKEncodedImageFormat.Png, 100);
        var output = new { status = "completed", steps = new object[]
        {
            new { type = "thought", signature = "private-thought" },
            new { type = "model_output", content = new[] { new { type = "image", mime_type = "image/jpeg", data = Convert.ToBase64String(jpeg.ToArray()) } } }
        } };
        var response = JsonSerializer.Serialize(output);
        var handler = new Handler(response);
        using var http = new HttpClient(handler);
        var generator = new GeminiImageGenerator(http, "offline-key");
        var bytes = await generator.GenerateAsync("Fictional character only", "2:3", null, default);
        using var decoded = SKBitmap.Decode(bytes);
        Check(decoded?.Width == 128 && decoded.Height == 192, "decoded image dimensions survive normalization");
        Check(bytes.AsSpan(0, 4).SequenceEqual(new byte[] { 137, 80, 78, 71 }), "keepsake PNG contract preserved");
        using (var request = JsonDocument.Parse(handler.Body!))
        {
            Check(!request.RootElement.GetProperty("store").GetBoolean(), "provider retention disabled");
            Check(request.RootElement.GetProperty("input").GetString() == "Fictional character only", "text-only request uses documented string input without reference upload");
            Check(request.RootElement.GetProperty("response_format").GetProperty("aspect_ratio").GetString() == "2:3", "portrait aspect ratio");
        }
        await generator.GenerateAsync("Keep frozen participants", "1:1", png.ToArray(), default);
        using (var request = JsonDocument.Parse(handler.Body!))
        {
            var reference = request.RootElement.GetProperty("input")[1];
            Check(reference.GetProperty("mime_type").GetString() == "image/png"
                && reference.GetProperty("data").GetBytesFromBase64().SequenceEqual(png.ToArray()), "frozen reference sent unchanged");
        }
        foreach (var invalid in new[] { "{}", "{\"status\":\"completed\",\"steps\":[]}",
            "{\"status\":\"completed\",\"steps\":[{\"type\":\"model_output\",\"content\":[{\"type\":\"image\",\"mime_type\":\"image/jpeg\",\"data\":\"aW52YWxpZA==\"}]}]}" })
        {
            using var document = JsonDocument.Parse(invalid);
            try { GeminiImageGenerator.ReadImage(document.RootElement); throw new Exception("Invalid image accepted"); }
            catch (Exception error) when (error is InvalidDataException or KeyNotFoundException) { }
        }
        var failedHandler = new Handler("private upstream details", HttpStatusCode.TooManyRequests);
        using var failedHttp = new HttpClient(failedHandler);
        try
        {
            await new GeminiImageGenerator(failedHttp, "offline-key").GenerateAsync("synthetic", "2:3", null, default);
            throw new Exception("HTTP failure accepted");
        }
        catch (CharacterException error)
        {
            Check(error.Code == "gemini_http_429" && failedHandler.Calls == 1 && !error.Message.Contains("private"), "paid requests never retried; provider details stay private");
        }
        var portraitHandler = new Handler(response);
        using (var keepsake = new OpenAiKeepsakeProvider(new HttpClient(portraitHandler), "offline-openai",
            "gpt-6-sol", "gpt-image-2.5-flare", "offline-key"))
        {
            var request = new KeepsakeProviderRequest("private-keepsake",
                new("private-outcome", "private-device", 1, "catalog", "Wand", "facts", "evidence"),
                new("private-name", "private-motif", true), "private-job", "edition", "upload", "portrait_only", png.ToArray(), null)
                { GoogleImageConsent = true };
            var result = await keepsake.GenerateAsync(request, default);
            Check(result.Provider == "gemini" && result.Model == GeminiImageGenerator.Model && result.MediaType == "image/png",
                "photo keepsake routes through Gemini and keeps PNG contract");
            Check(portraitHandler.Calls == 1 && !portraitHandler.Body!.Contains("private-"), "photo edit sends no account or personal brief");
        }
        var chronicleHandler = new Handler(response);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["Chronicle:ProviderMode"] = "openai", ["Chronicle:ImageProvider"] = "gemini", ["GEMINI_API_KEY"] = "offline-key" }).Build();
        using (var chronicle = new ChronicleIllustrator(config, new HttpClient(chronicleHandler)))
        {
            var snapshot = new ChronicleEventInput("duel", "private-source", "private-group", 1, "A cup of tea", "The cup froze.",
                [new("private-account", "Dementor", "participant", "Dementor", VisualSnapshotJson:
                    JsonSerializer.Serialize(new { portraitReferenceBase64 = Convert.ToBase64String(png.ToArray()) }))]);
            var result = await chronicle.GenerateAsync(snapshot, default);
            using var card = SKBitmap.Decode(result.Bytes);
            Check(card?.Width == 1024 && card.Height == 1408 && result.MediaType == "image/png", "actual chronicle renders Gemini scene into existing card");
            Check(chronicleHandler.Calls == 1 && !chronicleHandler.Body!.Contains("private-"), "chronicle sends frozen visual facts without owner ids");
        }
        var refusedHandler = new Handler("{\"error\":{\"code\":\"image_other\"}}", HttpStatusCode.BadRequest);
        using (var chronicle = new ChronicleIllustrator(config, new HttpClient(refusedHandler)))
        {
            var snapshot = new ChronicleEventInput("duel", "private-source", "private-group", 1, "Confirmed title", "Confirmed result",
                [new("private-account", "Harry", "participant", "Harry Potter", VisualSnapshotJson:
                    JsonSerializer.Serialize(new { portraitReferenceBase64 = Convert.ToBase64String(png.ToArray()) }))]);
            var result = await chronicle.GenerateAsync(snapshot, default);
            using var card = SKBitmap.Decode(result.Bytes);
            Check(card?.Width == 1024 && card.Height == 1408 && !result.Synthetic && refusedHandler.Calls == 1,
                "refusal composes frozen portraits and confirmed facts without another paid request");
            try
            {
                await chronicle.GenerateAsync(snapshot with { Participants = [] }, default);
                throw new Exception("Refusal without frozen portraits was hidden");
            }
            catch (CharacterException error) when (error.Code == "portrait_content_rejected") { }
        }
        Console.WriteLine("Gemini image transport tests passed.");
    }

    private static void Check(bool passed, string message)
    { if (!passed) { throw new Exception("Gemini: " + message); } }

    private sealed class Handler(string response, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        internal string? Body;
        internal int Calls;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++;
            Check(request.RequestUri?.AbsoluteUri == "https://generativelanguage.googleapis.com/v1beta/interactions", "fixed API destination");
            Check(request.Headers.GetValues("x-goog-api-key").Single() == "offline-key"
                && !request.RequestUri!.Query.Contains("key"), "credential only in header");
            Body = await request.Content!.ReadAsStringAsync(token);
            return new(status) { Content = new StringContent(response, System.Text.Encoding.UTF8, "application/json") };
        }
    }
}
