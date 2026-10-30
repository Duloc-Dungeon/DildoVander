using System.Net.Http.Json;
using System.Text.Json;
using Kufr.Backend.Features.Customers;
using SkiaSharp;

namespace Kufr.Backend.Features.Images;

// Bounded single-request Gemini transport shared by character portraits and
// keepsakes. References are frozen images, never URLs or entire customer profiles.
internal sealed class GeminiImageGenerator(HttpClient http, string? key, string model = GeminiImageGenerator.Model)
{
    internal const string Model = "gemini-nano-banana-2.1";
    private const int MaximumResponseBytes = 28 * 1024 * 1024;
    public async Task<byte[]> GenerateAsync(string prompt, string aspectRatio, byte[]? reference, CancellationToken token)
    {
        if (model is not (Model or "gemini-3-pro-image")) { throw new InvalidDataException("Unsupported Gemini image model."); }
        if (string.IsNullOrWhiteSpace(key)) { throw new CharacterException("portrait_provider_unavailable", 503); }
        using var request = new HttpRequestMessage(HttpMethod.Post,
            "https://generativelanguage.googleapis.com/v1beta/interactions");
        request.Headers.Add("x-goog-api-key", key);
        request.Content = JsonContent.Create(new
        {
            model, input = BuildInput(prompt, reference), store = false,
            response_format = new { type = "image", mime_type = "image/jpeg", aspect_ratio = aspectRatio, image_size = "1K" }
        });
        // One paid submission. A timeout or refusal must not start another billable request.
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        if (!response.IsSuccessStatusCode)
        {
            await InspectRejectionAsync(response.Content, token);
            throw new CharacterException("gemini_http_" + ((int)response.StatusCode).ToString(
                System.Globalization.CultureInfo.InvariantCulture), 502);
        }
        if (response.Content.Headers.ContentLength > MaximumResponseBytes
            || response.Content.Headers.ContentType?.MediaType != "application/json")
        {
            throw new InvalidDataException("Invalid portrait response.");
        }
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int count;
        while ((count = await stream.ReadAsync(chunk, token)) > 0)
        {
            if (buffer.Length + count > MaximumResponseBytes) { throw new InvalidDataException("Portrait response too large."); }
            buffer.Write(chunk, 0, count);
        }
        using var document = JsonDocument.Parse(buffer.GetBuffer().AsMemory(0, (int)buffer.Length),
            new JsonDocumentOptions { MaxDepth = 32 });
        return ReadImage(document.RootElement);
    }

    private static async Task InspectRejectionAsync(HttpContent content, CancellationToken token)
    {
        if (content.Headers.ContentLength > 4096) { return; }
        var bytes = new byte[4097];
        try
        {
            using var stream = await content.ReadAsStreamAsync(token);
            var count = await stream.ReadAtLeastAsync(bytes, bytes.Length, false, token);
            if (count > 4096) { return; }
            using var document = JsonDocument.Parse(bytes.AsMemory(0, count), new JsonDocumentOptions { MaxDepth = 8 });
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object
                && error.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.String
                && code.GetString() is "image_other" or "prohibited_content" or "image_safety" or "safety" or "blocklist" or "jailbreak")
            {
                throw new CharacterException("portrait_content_rejected", 422);
            }
        }
        catch (JsonException) { }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes); }
    }

    internal static byte[] ReadImage(JsonElement root)
    {
        if (root.GetProperty("status").GetString() != "completed")
        {
            throw new CharacterException("portrait_generation_failed", 502);
        }
        byte[]? image = null;
        foreach (var step in root.GetProperty("steps").EnumerateArray())
        {
            // Thought blocks and signatures are never logged, persisted or treated as output.
            if (step.GetProperty("type").GetString() != "model_output") { continue; }
            foreach (var part in step.GetProperty("content").EnumerateArray())
            {
                var type = part.GetProperty("type").GetString();
                if (type == "refusal") { throw new CharacterException("portrait_content_rejected", 422); }
                if (type != "image") { continue; }
                if (image is not null || part.GetProperty("mime_type").GetString() != "image/jpeg")
                {
                    throw new InvalidDataException("Unexpected portrait output.");
                }
                image = part.GetProperty("data").GetBytesFromBase64();
            }
        }
        if (image is null || image.Length > 20 * 1024 * 1024) { throw new InvalidDataException("Portrait missing or too large."); }
        using var data = SKData.CreateCopy(image);
        using var codec = SKCodec.Create(data);
        if (codec is null || codec.Info.Width is < 128 or > 4096 || codec.Info.Height is < 128 or > 4096)
        {
            throw new InvalidDataException("Image dimensions invalid.");
        }
        using var bitmap = SKBitmap.Decode(codec) ?? throw new InvalidDataException("Image undecodable.");
        using var decoded = SKImage.FromBitmap(bitmap);
        using var png = decoded.Encode(SKEncodedImageFormat.Png, 100);
        return png.ToArray();
    }

    private static object BuildInput(string prompt, byte[]? reference)
    {
        if (reference is null) { return prompt; }
        if (reference.Length is 0 or > 8 * 1024 * 1024) { throw new InvalidDataException("Invalid reference size."); }
        return new object[] { new { type = "text", text = prompt },
            new { type = "image", mime_type = "image/png", data = Convert.ToBase64String(reference) } };
    }
}
