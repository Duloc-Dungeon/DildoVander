using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Kufr.Backend.Features.Keepsakes;

// One secondary attempt for an already saved, fictional wand. No photograph,
// character profile, account identifier or transcript enters this provider.
internal sealed class ElevenLabsWandArtwork(HttpClient http, string apiKey) : IDisposable
{
    internal const string Model = "gpt-image-1.5";
    private const int MaximumImageBytes = 20 * 1024 * 1024;

    internal async Task<KeepsakeProviderOutput> GenerateAsync(KeepsakeProviderRequest request, CancellationToken token)
    {
        var wand = request.Snapshot.GeneratedWand ?? throw new KeepsakeException("personal_wand_unavailable", 409);
        var prompt = OpenAiKeepsakeProvider.BuildWandImagePrompt(wand);
        var parameters = new { model_id = Model, prompt, aspect_ratio = "2:3", background = "transparent", quality = "high" };
        Record(request, "dispatch", JsonSerializer.Serialize(parameters));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(110));
        var lifetime = timeout.Token;
        using var created = await ApiAsync(HttpMethod.Post, "https://api.elevenlabs.io/v1/flows/image", parameters, lifetime);
        var id = created.RootElement.GetProperty("id").GetString();
        if (id is null || !Regex.IsMatch(id, "\\A[A-Za-z0-9_-]{1,128}\\z"))
        {
            throw new KeepsakeException("elevenlabs_artwork_invalid_response", 502);
        }

        while (true)
        {
            lifetime.ThrowIfCancellationRequested();
            using var result = await ReadGenerationAsync(id, lifetime);
            if (result.RootElement.GetProperty("id").GetString() != id)
            {
                throw new KeepsakeException("elevenlabs_artwork_invalid_response", 502);
            }
            var status = result.RootElement.GetProperty("status").GetString();
            if (status == "completed")
            {
                var mime = result.RootElement.GetProperty("content_mime_type").GetString();
                var url = result.RootElement.GetProperty("content_url").GetString();
                if (mime != "image/png" || !IsAllowedDownload(url))
                {
                    throw new KeepsakeException("elevenlabs_artwork_invalid_response", 502);
                }
                // Never forward xi-api-key to the signed media host.
                using var download = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, lifetime);
                if (!download.IsSuccessStatusCode || download.Content.Headers.ContentLength > MaximumImageBytes
                    || download.Content.Headers.ContentType?.MediaType != "image/png")
                {
                    throw new KeepsakeException("elevenlabs_artwork_download_failed", 502);
                }
                await using var stream = await download.Content.ReadAsStreamAsync(lifetime);
                using var bytes = new MemoryStream();
                var buffer = new byte[16 * 1024];
                int count;
                while ((count = await stream.ReadAsync(buffer, lifetime)) > 0)
                {
                    if (bytes.Length + count > MaximumImageBytes)
                    {
                        throw new KeepsakeException("elevenlabs_artwork_download_failed", 502);
                    }
                    bytes.Write(buffer, 0, count);
                }
                var receipt = JsonSerializer.Serialize(new { provider = "elevenlabs", model = Model, generationId = id, status = "completed" });
                var output = new KeepsakeProviderOutput(null, bytes.ToArray(), "image/png", "elevenlabs", Model, prompt, receipt, false)
                {
                    GenerationParameters = JsonSerializer.Serialize(new { model = Model, aspect_ratio = "2:3", background = "transparent", fallback = "openai" })
                };
                KeepsakeStore.ValidateWandArtwork(output);
                Record(request, "completed", receipt);
                return output;
            }
            if (status == "failed")
            {
                throw new KeepsakeException("elevenlabs_artwork_failed", 502);
            }
            if (status is not ("pending" or "generating"))
            {
                throw new KeepsakeException("elevenlabs_artwork_invalid_response", 502);
            }
            await Task.Delay(TimeSpan.FromSeconds(10), lifetime);
        }
    }

    internal static bool IsAllowedDownload(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0
        && (uri.Host == "storage.googleapis.com" || uri.Host == "api.elevenlabs.io"
            || uri.Host.EndsWith(".elevenlabs.io", StringComparison.Ordinal));

    private async Task<JsonDocument> ReadGenerationAsync(string id, CancellationToken token)
    {
        while (true)
        {
            try
            {
                return await ApiAsync(HttpMethod.Get, "https://api.elevenlabs.io/v1/flows/image/" + id, null, token);
            }
            catch (KeepsakeException error) when (error.Code == "elevenlabs_artwork_rate_limited")
            {
                // Retrying a read never submits another paid generation.
                await Task.Delay(TimeSpan.FromSeconds(10), token);
            }
        }
    }

    private async Task<JsonDocument> ApiAsync(HttpMethod method, string url, object? body, CancellationToken token)
    {
        using var message = new HttpRequestMessage(method, url);
        message.Headers.Add("xi-api-key", apiKey);
        if (body is not null) { message.Content = JsonContent.Create(body); }
        using var response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, token);
        if (method == HttpMethod.Get && response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
        {
            throw new KeepsakeException("elevenlabs_artwork_rate_limited", 429);
        }
        if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > 64 * 1024)
        {
            throw new KeepsakeException("elevenlabs_artwork_provider_failed", 502);
        }
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        using var memory = new MemoryStream();
        var buffer = new byte[4096];
        int count;
        while ((count = await stream.ReadAsync(buffer, token)) > 0)
        {
            if (memory.Length + count > 64 * 1024) { throw new KeepsakeException("elevenlabs_artwork_invalid_response", 502); }
            memory.Write(buffer, 0, count);
        }
        return JsonDocument.Parse(memory.ToArray());
    }

    private static void Record(KeepsakeProviderRequest request, string phase, string content)
    {
        if (request.RecordStage is not null && !request.RecordStage("wand_generate_fallback", phase, content))
        {
            throw new KeepsakeException("publication_epoch_changed", 409);
        }
    }

    public void Dispose() => http.Dispose();
}

internal sealed class WandArtworkFallbackProvider(IKeepsakeProvider primary,
    Func<KeepsakeProviderRequest, CancellationToken, Task<KeepsakeProviderOutput>> fallback,
    IDisposable? fallbackLifetime = null) : IKeepsakeProvider, IDisposable
{
    public async Task<KeepsakeProviderOutput> GenerateAsync(KeepsakeProviderRequest request, CancellationToken token)
    {
        try
        {
            var output = await primary.GenerateAsync(request, token);
            if (request.GenerationKind == "wand_artwork") { KeepsakeStore.ValidateWandArtwork(output); }
            return output;
        }
        catch (Exception error) when (!token.IsCancellationRequested && request.GenerationKind == "wand_artwork"
            && request.NormalizedPhoto is null && CanFallback(error))
        {
            var output = await fallback(request, token);
            KeepsakeStore.ValidateWandArtwork(output);
            return output;
        }
    }

    internal static bool CanFallback(Exception error) => error switch
    {
        System.ClientModel.ClientResultException { Status: 401 or 404 or 408 or 429 or >= 500 } => true,
        HttpRequestException => true,
        InvalidDataException or JsonException => true,
        KeepsakeException { Code: "provider_image_missing" or "wand_artwork_transparent_model_required"
            or "wand_artwork_invalid" or "wand_artwork_opaque" or "wand_artwork_transparency_required" } => true,
        _ => false
    };

    public void Dispose()
    {
        (primary as IDisposable)?.Dispose();
        fallbackLifetime?.Dispose();
    }
}
