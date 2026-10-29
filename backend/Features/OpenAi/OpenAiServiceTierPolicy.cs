using System.Text.Json;

namespace Kufr.Backend.Features.OpenAi;

internal enum OpenAiOperation
{
    CharacterEnrichment,
    CharacterWelcome,
    WandWorkshop,
    WandGeneration,
    Ordinary
}

// Explicit per-request selection avoids inheriting a project's paid tier setting.
// This policy applies to Responses, not the separate Images or Realtime APIs.
internal static class OpenAiServiceTierPolicy
{
    internal const string Standard = "default";
    internal const string Fast = "fast";

    internal static string For(OpenAiOperation operation) => operation switch
    {
        OpenAiOperation.CharacterEnrichment or OpenAiOperation.CharacterWelcome
            or OpenAiOperation.WandWorkshop or OpenAiOperation.WandGeneration => Fast,
        OpenAiOperation.Ordinary => Standard,
        _ => throw new ArgumentOutOfRangeException(nameof(operation))
    };

    internal static bool TryRateMultiplier(string requestedTier, string? returnedTier, out int multiplier)
    {
        multiplier = returnedTier switch
        {
            Standard when requestedTier is Standard or Fast => 1,
            // Priority is the documented Fast response alias for older models.
            Fast or "priority" when requestedTier == Fast => 2,
            _ => 0
        };
        return multiplier != 0;
    }

    internal static void ValidateResponse(JsonElement response, OpenAiOperation operation)
    {
        var tier = response.TryGetProperty("service_tier", out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;
        if (!TryRateMultiplier(For(operation), tier, out _))
        {
            throw new InvalidDataException("Unexpected OpenAI response service tier.");
        }
    }
}
