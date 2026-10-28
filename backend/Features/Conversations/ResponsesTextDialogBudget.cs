using System.Text;
using System.Text.Json;

namespace Kufr.Backend.Features.Conversations;

internal static class ResponsesTextDialogBudget
{
    internal const string Model = "gpt-6.1-terra";
    internal const string LegacyModel = "gpt-6.1-sol";
    internal const int MinimumQuestions = 4;
    internal const int MaxRequestBytes = 14_000;

    // The 14 kB serialized request cap bounds input below 14,000 tokens;
    // reserve enough for a Fast response at either the Terra default or
    // explicitly configured legacy Sol rates.
    internal const long ReservedMicroUsd = 100_000;

    internal static int RequestByteLimit(string context, string personalityId = PersonalityCatalog.DefaultId) =>
        MaxRequestBytes + ContextBytes(context) + PersonalityCatalog.ExtraRequestBytes(personalityId);

    internal static bool IsSupportedModel(string model) => model is Model or LegacyModel;

    internal static long ReserveForContext(string context, string model = Model, string personalityId = PersonalityCatalog.DefaultId)
    {
        if (!IsSupportedModel(model))
        {
            throw new InvalidOperationException("Unsupported text dialog model.");
        }
        var extraBytes = ContextBytes(context) + PersonalityCatalog.ExtraRequestBytes(personalityId);
        return checked(ReservedMicroUsd + extraBytes * 5L);
    }

    private static int ContextBytes(string context) => context.Length == 0
        ? 0
        : Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(context));

    internal static long CalculateActualMicroUsd(TextDialogResponse response, string expectedModel = Model)
        => CalculateUsageMicroUsd(response.ReturnedModel, response.ServiceTier, response.InputTokens, response.OutputTokens, expectedModel);

    internal static long CalculateUsageMicroUsd(string returnedModel, string? serviceTier, int inputTokens, int outputTokens,
        string expectedModel = Model)
    {
        if (!IsSupportedModel(expectedModel)
            || returnedModel != expectedModel
            || serviceTier != "default"
            || inputTokens < 0
            || outputTokens < 0)
        {
            throw new TextDialogUnavailableException("Provider usage cannot be priced.");
        }

        // This is a conservative list-price estimate, not verified account billing.
        // Charge all input at the cache-write rate, even when the provider
        // reports cached tokens; round up fractional microdollars once.
        return expectedModel switch
        {
            // Terra Standard: cache writes $1.25/M, output $5/M.
            Model => checked((inputTokens * 5L + 3) / 4 + outputTokens * 5L),
            // Explicit legacy Sol Standard: cache writes $2.50/M, output $10/M.
            LegacyModel => checked((inputTokens * 5L + 1) / 2 + outputTokens * 10L),
            _ => throw new TextDialogUnavailableException("Provider model cannot be priced.")
        };
    }
}
