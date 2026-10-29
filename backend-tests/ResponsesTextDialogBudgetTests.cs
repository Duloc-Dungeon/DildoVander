using Kufr.Backend.Features.Conversations;
using Kufr.Backend.Features.OpenAi;
using System.Text.Json;

internal static class ResponsesTextDialogBudgetTests
{
    internal static void Run()
    {
        ServiceTierPolicy();
        var response = new TextDialogResponse(
            "Odpověď", "ask", "", null, ResponsesTextDialogBudget.Model, 621, 102, "default");
        var actual = ResponsesTextDialogBudget.CalculateActualMicroUsd(response);
        Require(actual == 1287, "Terra input is charged at the cache-write rate and rounded up");
        Require(actual < ResponsesTextDialogBudget.ReservedMicroUsd,
            "measured test case fits the pre-call reservation");

        var fast = ResponsesTextDialogBudget.CalculateActualMicroUsd(response with { ServiceTier = "fast" },
            requestedTier: OpenAiServiceTierPolicy.Fast);
        Require(fast == 2573, "Fast uses double list rates and rounds once, not twice the rounded Standard amount");
        Require(ResponsesTextDialogBudget.CalculateActualMicroUsd(response,
            requestedTier: OpenAiServiceTierPolicy.Fast) == actual, "Fast downgrade settles at returned Standard rates");
        Require(ResponsesTextDialogBudget.CalculateActualMicroUsd(response with { ServiceTier = "priority" },
            requestedTier: OpenAiServiceTierPolicy.Fast) == fast, "documented legacy Fast response alias is priced");
        foreach (var model in new[] { ResponsesTextDialogBudget.Model, ResponsesTextDialogBudget.LegacyModel })
        {
            var maximumFast = response with
            {
                ReturnedModel = model, ServiceTier = "fast", InputTokens = ResponsesTextDialogBudget.MaxRequestBytes,
                OutputTokens = 1200
            };
            Require(ResponsesTextDialogBudget.CalculateActualMicroUsd(maximumFast, model, OpenAiServiceTierPolicy.Fast)
                <= ResponsesTextDialogBudget.ReservedMicroUsd, "Fast at each supported model rate fits the reservation");
            Require(ResponsesTextDialogBudget.CalculateActualMicroUsd(maximumFast with
                { InputTokens = ResponsesTextDialogBudget.RequestByteLimit(new string('x', 20_000)) }, model, OpenAiServiceTierPolicy.Fast)
                <= ResponsesTextDialogBudget.ReserveForContext(new string('x', 20_000), model),
                "Fast with extra context still fits its pre-dispatch reservation");
        }

        var maximumRequest = response with { InputTokens = 15_200, OutputTokens = 1_200 };
        Require(2 * ResponsesTextDialogBudget.CalculateActualMicroUsd(maximumRequest)
            <= ResponsesTextDialogBudget.ReservedMicroUsd,
            "reservation covers Terra Fast rates if the provider disregards the requested Standard tier");
        var serializedRequestCap = response with
        {
            InputTokens = ResponsesTextDialogBudget.MaxRequestBytes,
            OutputTokens = 1_200
        };
        Require(ResponsesTextDialogBudget.CalculateActualMicroUsd(serializedRequestCap)
            <= ResponsesTextDialogBudget.ReservedMicroUsd,
            "serialized request cap stays below the per-call reservation");

        var context = new string('\u0001', 1000) + new string('\u4e00', 3000);
        var withMemory = response with
        {
            InputTokens = ResponsesTextDialogBudget.RequestByteLimit(context),
            OutputTokens = 1200
        };
        Require(ResponsesTextDialogBudget.CalculateActualMicroUsd(withMemory)
            <= ResponsesTextDialogBudget.ReserveForContext(context),
            "escaped Unicode memory fits its pre-call reservation");
        Require(ResponsesTextDialogBudget.ReserveForContext(context)
            > ResponsesTextDialogBudget.ReservedMicroUsd,
            "memory increases reservation before provider dispatch");

        foreach (var tier in new string?[] { null, "fast", "priority", "unknown" })
        {
            try
            {
                ResponsesTextDialogBudget.CalculateActualMicroUsd(response with { ServiceTier = tier });
                throw new Exception("Unconfirmed service tier was priced at Standard rates.");
            }
            catch (TextDialogUnavailableException)
            {
            }
        }

        Require(ResponsesTextDialogBudget.IsSupportedModel(ResponsesTextDialogBudget.Model), "Terra is supported");
        Require(ResponsesTextDialogBudget.IsSupportedModel(ResponsesTextDialogBudget.LegacyModel), "explicit legacy Sol is supported");
        Require(!ResponsesTextDialogBudget.IsSupportedModel("gpt-6-luna"), "Luna is disabled after live evaluation");

        var legacySol = response with { ReturnedModel = ResponsesTextDialogBudget.LegacyModel };
        Require(ResponsesTextDialogBudget.CalculateActualMicroUsd(legacySol, ResponsesTextDialogBudget.LegacyModel) == 2573,
            "explicit legacy Sol retains its Standard price calculation");

        var wrongModel = response with { ReturnedModel = "other-model" };
        try
        {
            ResponsesTextDialogBudget.CalculateActualMicroUsd(wrongModel);
            throw new Exception("Unknown model was priced as GPT-6.1 Terra.");
        }
        catch (TextDialogUnavailableException)
        {
        }
    }

    private static void ServiceTierPolicy()
    {
        foreach (var operation in new[] { OpenAiOperation.CharacterEnrichment, OpenAiOperation.CharacterWelcome,
            OpenAiOperation.WandWorkshop, OpenAiOperation.WandGeneration })
        {
            Require(OpenAiServiceTierPolicy.For(operation) == "fast", "user-waiting preparation explicitly requests Fast");
            foreach (var tier in new[] { "fast", "priority", "default" })
            {
                using var response = JsonDocument.Parse(JsonSerializer.Serialize(new { service_tier = tier }));
                OpenAiServiceTierPolicy.ValidateResponse(response.RootElement, operation);
            }
            foreach (var invalid in new[] { "{}", "{\"service_tier\":null}", "{\"service_tier\":42}",
                "{\"service_tier\":\"ultrafast\"}", "{\"service_tier\":\"unknown\"}" })
            {
                using var response = JsonDocument.Parse(invalid);
                try
                {
                    OpenAiServiceTierPolicy.ValidateResponse(response.RootElement, operation);
                    throw new Exception("Unconfirmed preparation tier accepted.");
                }
                catch (InvalidDataException) { }
            }
        }
        Require(OpenAiServiceTierPolicy.For(OpenAiOperation.Ordinary) == "default", "ordinary work pins Standard");
        foreach (var tier in new[] { "fast", "priority", "unknown" })
        {
            using var response = JsonDocument.Parse(JsonSerializer.Serialize(new { service_tier = tier }));
            try
            {
                OpenAiServiceTierPolicy.ValidateResponse(response.RootElement, OpenAiOperation.Ordinary);
                throw new Exception("Unrequested premium tier accepted.");
            }
            catch (InvalidDataException) { }
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new Exception(message);
        }
    }
}
