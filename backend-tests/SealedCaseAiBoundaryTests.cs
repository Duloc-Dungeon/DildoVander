using System.Net;
using System.Text;
using System.Text.Json;
using Kufr.Backend.Features.SealedCase;

internal static partial class SealedCaseHttpTests
{
    private static async Task AiBoundariesAsync(SealedPlayView play)
    {
        foreach (var role in Enum.GetValues<SealedAiRole>())
        {
            Check(SealedCaseAi.Prompt(role).Length > 100, "all six original role prompts are embedded server resources");
        }
        var publicContext = SealedCaseAi.ForPlayer(SealedAiRole.PublicNarrator, play);
        var publicJson = publicContext.Projection.GetRawText();
        Check(!publicJson.Contains("ownTeam", StringComparison.Ordinal) && !publicJson.Contains("ownPlan", StringComparison.Ordinal)
            && !publicJson.Contains("teamMembers", StringComparison.Ordinal), "public narrator has no private player fields");
        foreach (var role in new[] { SealedAiRole.Adjudicator, SealedAiRole.Epilogue })
        {
            try { SealedCaseAi.ForPlayer(role, play); throw new InvalidOperationException("privileged AI context granted"); }
            catch (SealedCasePersistenceException error) { Equal(403, error.Status); }
        }
        var disabled = await new SealedCaseAiPipeline().NarrateAsync(publicContext, CancellationToken.None);
        Check(disabled.Fallback && disabled.Failure == "provider_disabled", "disabled provider uses stable Czech fallback");
        using var http = new HttpClient(new SyntheticAiHandler()) { BaseAddress = new Uri("https://api.openai.com/v1/") };
        var pipeline = new SealedCaseAiPipeline(new SealedCaseAiClient(http, "synthetic-test-token", "synthetic-test-model"));
        var narration = await pipeline.NarrateAsync(publicContext, CancellationToken.None);
        Check(narration.Fallback && narration.Failure == "invalid_output", "invented source reference cannot become narration");
        Check(!narration.Text.Contains("INVENTED_SECRET", StringComparison.Ordinal), "invalid model output cannot reach fallback");
        var html = SealedCaseAi.ValidateNarration(publicContext, new("<script>untrusted</script>", [], false));
        Check(html.Fallback, "model HTML is rejected");
        Console.WriteLine("PASS six server AI roles, public/private authority, disabled-provider fallback and untrusted model sources.");
    }

    private sealed class SyntheticAiHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var text = JsonSerializer.Serialize(new { text = "INVENTED_SECRET", usedRefs = new[] { "foreign-event" } });
            var response = JsonSerializer.Serialize(new { status = "completed", output = new[] { new { type = "message",
                content = new[] { new { type = "output_text", text } } } }, usage = new { input_tokens = 12, output_tokens = 6 } });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response, Encoding.UTF8, "application/json") });
        }
    }
}
