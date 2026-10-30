using System.ClientModel;
using System.Text.Json;

namespace Kufr.Backend.Features.Customers;

// Technical classifications only. Never expose provider messages or bodies.
internal static class CharacterProviderFailure
{
    // Image refusal codes only; never propagate the provider's message, prompt or arbitrary code.
    internal static async Task InspectPortraitFailureAsync(HttpContent content, CancellationToken token)
    {
        const int maximum = 4096;
        if (content.Headers.ContentLength > maximum) { return; }
        var bytes = new byte[maximum + 1];
        try
        {
            using var stream = await content.ReadAsStreamAsync(token);
            var count = await stream.ReadAtLeastAsync(bytes, bytes.Length, throwOnEndOfStream: false, token);
            if (count > maximum) { return; }
            using var document = JsonDocument.Parse(bytes.AsMemory(0, count), new JsonDocumentOptions { MaxDepth = 8 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count(p => p.Name == "error") != 1
                || !root.TryGetProperty("error", out var error) || error.ValueKind != JsonValueKind.Object
                || error.EnumerateObject().Count(p => p.Name == "code") != 1
                || !error.TryGetProperty("code", out var code) || code.ValueKind != JsonValueKind.String) { return; }
            if (code.GetString() is "moderation_blocked" or "content_policy_violation" or "safety_violation")
            {
                var rejection = new CharacterException("portrait_content_rejected", 422);
                if (error.TryGetProperty("moderation_details", out var details) && details.ValueKind == JsonValueKind.Object)
                {
                    if (details.TryGetProperty("moderation_stage", out var stage) && stage.ValueKind == JsonValueKind.String
                        && stage.GetString() is "input" or "output" or "unknown")
                    {
                        rejection.Data["portrait_moderation_stage"] = stage.GetString();
                    }
                    if (details.TryGetProperty("categories", out var categories) && categories.ValueKind == JsonValueKind.Array)
                    {
                        rejection.Data["portrait_moderation_categories"] = string.Join(",", categories.EnumerateArray()
                            .Where(item => item.ValueKind == JsonValueKind.String)
                            .Select(item => item.GetString()).Where(value => value is "harassment" or "self-harm" or "sexual" or "violence")
                            .Distinct().Take(4));
                    }
                }
                throw rejection;
            }
        }
        catch (JsonException) { }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes); }
    }

    internal static bool IsTransient(Exception error) => error is HttpRequestException or OperationCanceledException
        || error is ClientResultException response && (response.Status is 408 or 429 || response.Status >= 500);

    internal static int? Status(Exception error) => error is ClientResultException { Status: >= 400 and <= 599 } response
        ? response.Status : null;

    internal static string Code(Exception error) => error switch
    {
        CharacterException character => character.Code,
        OperationCanceledException => "character_provider_timeout",
        HttpRequestException => "character_provider_transport_error",
        ClientResultException { Status: >= 400 and <= 599 } response => "openai_http_" + response.Status.ToString(System.Globalization.CultureInfo.InvariantCulture),
        JsonException or InvalidDataException or KeyNotFoundException or FormatException or OverflowException => "character_provider_invalid_response",
        _ => "character_provider_error"
    };
}
