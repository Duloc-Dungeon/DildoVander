using Kufr.Backend.Features.OpenAi;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Kufr.Backend.Features.SealedCase;

internal enum SealedAiRole { Orchestrator, Adjudicator, PrivateNarrator, PublicNarrator, ThreeChoices, Epilogue }
internal sealed record SealedAiContext(SealedAiRole Role, string CaseId, int Chapter, JsonElement Projection, string[] AllowedRefs);
internal sealed record SealedNarration(string Text, string[] UsedRefs, bool Fallback, string? Failure = null);
internal sealed record SealedAiReply(JsonElement Output, int InputTokens, int OutputTokens, long ElapsedMilliseconds);

// Product prompts are embedded server resources, never player assets. Engine
// proposals and prose are untrusted data and cannot execute or alter a world.
internal static class SealedCaseAi
{
    private static readonly string[] PromptNames = ["01_orchestrator.system.md", "02_adjudicator.system.md", "03_private_narrator.system.md",
        "04_public_narrator.system.md", "05_three_choices.system.md", "06_epilogue.system.md"];
    internal static string Prompt(SealedAiRole role)
    {
        string Read(string file)
        {
            var assembly = typeof(SealedCaseAi).Assembly;
            var name = assembly.GetManifestResourceNames().Single(n => n.EndsWith(".Content." + file, StringComparison.Ordinal));
            using var stream = assembly.GetManifestResourceStream(name)!; using var reader = new StreamReader(stream); return reader.ReadToEnd();
        }
        return Read("00_protokol_a_integrace.md") + "\n" + Read(PromptNames[(int)role]);
    }

    internal static SealedAiContext ForPlayer(SealedAiRole role, SealedPlayView play)
    {
        if (role == SealedAiRole.Adjudicator || role == SealedAiRole.Epilogue && play.Finale?.Phase != "released")
        { throw new SealedCasePersistenceException("sealed_case_ai_role_denied", 403); }
        object projection = role switch
        {
            SealedAiRole.PublicNarrator => new { play.Id, play.Chapter, play.Phase,
                publicFacts = SealedScenario.Definition.GetProperty("prelude").GetProperty("public_facts"),
                statements = play.Statements.Where(s => s.Public).Select(s => new { s.Actor, s.Body, classification = "UNVERIFIED_STATEMENT" }) },
            SealedAiRole.Epilogue => new { play.Id, play.Finale!.Winner, play.Finale.Selected, play.Finale.Conspirators, play.Finale.Reconstruction },
            _ => new { play.Id, play.Chapter, play.Phase, play.OwnId, play.OwnState, play.OwnTeam, play.OwnKit,
                play.TeamMembers, play.Location, play.Players, play.Clues, play.Notes, play.OwnPlan, play.OwnOutcomes, play.OwnItems, play.Choices,
                statements = play.Statements.TakeLast(20).Select(s => new { s.Actor, s.Body, classification = "UNVERIFIED_STATEMENT" }) }
        };
        var refs = role == SealedAiRole.PublicNarrator ? Array.Empty<string>() : role == SealedAiRole.Epilogue
            ? play.Finale!.Reconstruction!.Select(r => r.EventRef).Distinct().ToArray() : play.Clues.Select(c => c.SourceEvent).Distinct().ToArray();
        return new(role, play.Id, play.Chapter, JsonSerializer.SerializeToElement(projection, new JsonSerializerOptions(JsonSerializerDefaults.Web)), refs);
    }

    internal static SealedAiContext ForEngine(string caseId, int chapter, SealedGameCheckpoint checkpoint, bool chapterClosed)
    {
        if (!chapterClosed) { throw new SealedCasePersistenceException("chapter_not_closed", 409); }
        return new(SealedAiRole.Adjudicator, caseId, chapter, JsonSerializer.SerializeToElement(new { checkpoint, chapter, readonlyWorld = true }), []);
    }

    internal static SealedNarration ValidateNarration(SealedAiContext context, SealedNarration output)
    {
        if (string.IsNullOrWhiteSpace(output.Text) || output.Text.Length > 1600 || output.Text.Any(char.IsControl)
            || output.Text.Contains('<') || output.Text.Contains('>') || output.UsedRefs.Any(id => !context.AllowedRefs.Contains(id, StringComparer.Ordinal)))
        { return Fallback(context, "invalid_output"); }
        return output;
    }
    internal static SealedNarration Factual(SealedPlayView play, bool isPublic)
    {
        if (isPublic)
        {
            var facts = DefinitionPublicFacts();
            return new(string.Join(" ", facts).Replace(" T ", " " + SealedCaseRules.Conspirators(play.ExpectedPlayers) + " ", StringComparison.Ordinal), [], true);
        }
        var text = play.Notes.LastOrDefault() ?? "Zatím máš jen vlastní místo u stolu. Dům další svědectví nedodal.";
        return new(text, play.Clues.Select(c => c.SourceEvent).Distinct(StringComparer.Ordinal).ToArray(), true);
    }

    private static string[] DefinitionPublicFacts()
        => SealedScenario.Definition.GetProperty("prelude").GetProperty("public_facts").EnumerateArray().Select(f => f.GetString()!).ToArray();

    internal static SealedNarration Fallback(SealedAiContext context, string? failure = null)
        => new(context.Role == SealedAiRole.Epilogue
            ? "Zvon dozněl. Rekonstrukce níže ukazuje skutečně zapsané příkazy a jejich následky; soukromé rozhovory zůstávají soukromé."
            : "Potvrzené plány zůstávají uložené. Rozhoduje doložený záznam; vypravěč si výsledek nedoplní podle nálady.", [], true, failure);
}

// Explicit adapter: constructing/reading a game never calls a paid provider.
// The application uses factual fallbacks by default. Live synthetic acceptance
// calls this adapter only under the independently persisted ten-game test ledger.
internal sealed class SealedCaseAiClient(HttpClient http, string apiKey, string model)
{
    internal async Task<SealedAiReply> GenerateAsync(string instructions, object input, object schema, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(90));
        var watch = Stopwatch.StartNew();
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(http.BaseAddress ?? new Uri("https://api.openai.com/v1/"), "responses"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Content = JsonContent.Create(new { model, store = false, reasoning = new { effort = "low" }, max_output_tokens = 1200,
            instructions, input = JsonSerializer.Serialize(input), text = new { format = new { type = "json_schema", name = "sealed_test_output", strict = true, schema } } });
        using var bounded = BoundedSdkTransport.Create(http, 128 * 1024, ["application/json"]);
        using var response = await bounded.SendAsync(request, deadline.Token);
        if (!response.IsSuccessStatusCode) { throw new InvalidOperationException("AI provider HTTP " + (int)response.StatusCode); }
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(deadline.Token), cancellationToken: deadline.Token);
        var root = json.RootElement;
        if (root.GetProperty("status").GetString() != "completed") { throw new InvalidOperationException("AI response incomplete"); }
        var text = root.GetProperty("output").EnumerateArray().Where(item => item.GetProperty("type").GetString() == "message")
            .SelectMany(item => item.GetProperty("content").EnumerateArray()).Where(item => item.GetProperty("type").GetString() == "output_text")
            .Select(item => item.GetProperty("text").GetString()).FirstOrDefault() ?? throw new InvalidOperationException("AI output refused or absent");
        using var parsed = JsonDocument.Parse(text);
        var usage = root.GetProperty("usage");
        return new(parsed.RootElement.Clone(), usage.GetProperty("input_tokens").GetInt32(), usage.GetProperty("output_tokens").GetInt32(), watch.ElapsedMilliseconds);
    }
}

// Shared role adapter. The caller supplies an already authorized projection;
// model output is a read-only proposal, never a command to the case store.
// No provider is registered by normal app startup, so polling cannot spend tokens.
internal sealed class SealedCaseAiPipeline(SealedCaseAiClient? provider = null)
{
    internal async Task<SealedNarration> NarrateAsync(SealedAiContext context, CancellationToken token)
    {
        if (context.Role is not (SealedAiRole.PrivateNarrator or SealedAiRole.PublicNarrator or SealedAiRole.Epilogue))
        { throw new SealedCasePersistenceException("sealed_case_ai_role_denied", 403); }
        if (provider is null) { return SealedCaseAi.Fallback(context, "provider_disabled"); }
        try
        {
            var reply = await ProposeAsync(context, new { type = "object", additionalProperties = false,
                properties = new { text = new { type = "string" }, usedRefs = new { type = "array", items = new { type = "string" } } },
                required = new[] { "text", "usedRefs" } }, token);
            var text = reply.Output.GetProperty("text").GetString()!;
            var refs = reply.Output.GetProperty("usedRefs").EnumerateArray().Select(r => r.GetString()!).ToArray();
            return SealedCaseAi.ValidateNarration(context, new(text, refs, false));
        }
        catch (Exception error) when (error is HttpRequestException or JsonException or InvalidOperationException or InvalidDataException or OperationCanceledException)
        { return SealedCaseAi.Fallback(context, token.IsCancellationRequested ? "cancelled" : "provider_failed"); }
    }

    internal Task<SealedAiReply> ProposeAsync(SealedAiContext context, object schema, CancellationToken token)
    {
        if (provider is null) { throw new InvalidOperationException("Sealed case AI provider is disabled."); }
        return provider.GenerateAsync(SealedCaseAi.Prompt(context.Role), new { context.Projection, context.AllowedRefs,
            outputAuthority = "UNTRUSTED_READ_ONLY_PROPOSAL" }, schema, token);
    }
}
