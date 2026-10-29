using System.ClientModel;
using System.ClientModel.Primitives;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Globalization;
using Kufr.Backend.Features.Customers;
using Kufr.Backend.Features.Conversations;
using Kufr.Backend.Features.OpenAi;
using Kufr.Backend.Features.Observability;
using OpenAI.Responses;

#pragma warning disable OPENAI001
namespace Kufr.Backend.Features.Wands;

internal sealed record MobileWandDialogueDraft(string Reply, bool ReadyToFinish);
internal interface IMobileWandQuestionProvider
{
    Task<MobileWandDialogueDraft> ReplyAsync(CharacterConversationSnapshot profile,
        MobileWandWorkshopMessage[] messages, CancellationToken token) =>
        throw new NotSupportedException("This question provider does not support workshop dialogue.");
    Task<MobileWandDialogueDraft> ReplyAsync(CharacterConversationSnapshot profile,
        MobileWandWorkshopMessage[] messages, CancellationToken token, string? instructions) =>
        ReplyAsync(profile, messages, token);
    async Task<MobileWandDialogueDraft> ReplyStreamingAsync(CharacterConversationSnapshot profile,
        MobileWandWorkshopMessage[] messages, Func<string, CancellationToken, Task> clause,
        CancellationToken token, string? instructions = null)
    {
        var draft = await ReplyAsync(profile, messages, token, instructions);
        if (!draft.ReadyToFinish) { await clause(draft.Reply, token); }
        return draft;
    }
    Task<MobileWandDialogueDraft> ReplyStreamingAsync(CharacterConversationSnapshot profile,
        MobileWandWorkshopMessage[] messages, Func<string, CancellationToken, Task> clause,
        Func<string, CancellationToken, Task> text, CancellationToken token, string? instructions = null)
    {
        var snapshot = new StringBuilder();
        return ReplyStreamingAsync(profile, messages, async (part, cancellation) =>
        {
            if (snapshot.Length + part.Length > 600)
            {
                throw new MobileWandQuestionAuthoringException("dialogue_response_invalid", false);
            }
            snapshot.Append(part);
            await text(snapshot.ToString(), cancellation);
            await clause(part, cancellation);
        }, token, instructions);
    }
}

internal sealed class MobileWandQuestionAuthoringException(string code, bool safeFallback) : Exception(code)
{
    internal string Code { get; } = code;
    internal bool SafeFallback { get; } = safeFallback;
}

internal sealed class OpenAiMobileWandQuestionProvider : IMobileWandQuestionProvider, IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    private readonly string? key;
    private readonly string model;
    private readonly HttpClient http;

    internal static string DefaultInstructions => WorkshopPrompts.Dialogue;

    public OpenAiMobileWandQuestionProvider(IConfiguration configuration)
        : this(configuration, new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
            { Timeout = Timeout.InfiniteTimeSpan }) { }

    internal OpenAiMobileWandQuestionProvider(IConfiguration configuration, HttpClient http)
    {
        this.http = http;
        key = configuration["OPENAI_API_KEY"];
        model = configuration["Wands:Model"] ?? ResponsesTextDialogBudget.Model;
        if (!WandGenerationValidation.Text(model, 100)) throw new InvalidOperationException("Invalid Wands:Model.");
    }

    public Task<MobileWandDialogueDraft> ReplyStreamingAsync(CharacterConversationSnapshot profile,
        MobileWandWorkshopMessage[] messages, Func<string, CancellationToken, Task> clause,
        CancellationToken token, string? instructions = null) =>
        ReplyStreamingAsync(profile, messages, clause, (_, _) => Task.CompletedTask, token, instructions);

    public async Task<MobileWandDialogueDraft> ReplyStreamingAsync(CharacterConversationSnapshot profile,
        MobileWandWorkshopMessage[] messages, Func<string, CancellationToken, Task> clause,
        Func<string, CancellationToken, Task> text, CancellationToken token, string? instructions = null)
    {
        if (string.IsNullOrWhiteSpace(key)) { throw new MobileWandQuestionAuthoringException("dialogue_provider_unavailable", true); }
        if (!IsPreparedProfile(profile) || messages.Length > 160)
        {
            throw new MobileWandQuestionAuthoringException("dialogue_context_invalid", false);
        }
        using var telemetry = GenAiOperation.Start("responses", model);
        try
        {
            using var transport = new HttpClient(new MobileWandStreamingTransport(http));
            var client = new ResponsesClient(new ApiKeyCredential(key), new ResponsesClientOptions
            { Transport = BoundedSdkTransport.Pipeline(transport), RetryPolicy = new ClientRetryPolicy(0) });
            var options = ModelReaderWriter.Read<CreateResponseOptions>(BinaryData.FromString(
                BuildReplyRequest(profile, messages, model, instructions, stream: true)))
                ?? throw new MobileWandQuestionAuthoringException("dialogue_context_invalid", false);
            var incremental = new MobileWandReplyStream();
            var previousText = "";
            var updates = 0;
            await foreach (var update in client.CreateResponseStreamingAsync(options, token))
            {
                if (++updates > 1024) { throw new MobileWandQuestionAuthoringException("dialogue_response_invalid", false); }
                using var document = JsonDocument.Parse(ModelReaderWriter.Write(update).ToMemory());
                var root = document.RootElement;
                var type = root.GetProperty("type").GetString();
                if (type == "response.output_text.delta")
                {
                    var clauses = incremental.Append(root.GetProperty("delta").GetString() ?? "");
                    if (incremental.Text != previousText)
                    {
                        previousText = incremental.Text;
                        await text(previousText, token);
                    }
                    foreach (var part in clauses)
                    {
                        await clause(part, token);
                    }
                }
                else if (type == "response.completed")
                {
                    var response = root.GetProperty("response");
                    telemetry.ReadUsage(response);
                    OpenAiServiceTierPolicy.ValidateResponse(response, OpenAiOperation.WandWorkshop);
                    var draft = ParseReply(response);
                    if (!string.Equals(incremental.Json, ReadResponseText(response), StringComparison.Ordinal))
                    {
                        throw new MobileWandQuestionAuthoringException("dialogue_response_invalid", false);
                    }
                    return draft;
                }
                else if (type is "response.refusal.delta" or "response.refusal.done")
                {
                    throw new MobileWandQuestionAuthoringException("dialogue_response_refused", true);
                }
                else if (type is "response.failed" or "response.incomplete" or "error")
                {
                    throw new MobileWandQuestionAuthoringException("dialogue_response_incomplete", false);
                }
            }
            throw new MobileWandQuestionAuthoringException("dialogue_response_incomplete", false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (ClientResultException error) when (error.Status is >= 400 and < 500)
        {
            telemetry.Fail(error, token);
            throw new MobileWandQuestionAuthoringException("dialogue_provider_rejected", true);
        }
        catch (MobileWandQuestionAuthoringException) { throw; }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            telemetry.Fail(error, token);
            throw new MobileWandQuestionAuthoringException("dialogue_provider_uncertain", false);
        }
    }

    public Task<MobileWandDialogueDraft> ReplyAsync(CharacterConversationSnapshot profile,
        MobileWandWorkshopMessage[] messages, CancellationToken token) => ReplyAsync(profile, messages, token, null);

    public async Task<MobileWandDialogueDraft> ReplyAsync(CharacterConversationSnapshot profile,
        MobileWandWorkshopMessage[] messages, CancellationToken token, string? instructions)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new MobileWandQuestionAuthoringException("dialogue_provider_unavailable", true);
        if (!IsPreparedProfile(profile) || messages.Length > 160)
            throw new MobileWandQuestionAuthoringException("dialogue_context_invalid", false);
        using var telemetry = GenAiOperation.Start("responses", model);
        try
        {
            using var bounded = BoundedSdkTransport.Create(http, 131_072, new[] { "application/json" });
            var client = new ResponsesClient(new ApiKeyCredential(key), new ResponsesClientOptions
            { Transport = BoundedSdkTransport.Pipeline(bounded), RetryPolicy = new ClientRetryPolicy(0) });
            using var body = BinaryContent.Create(BinaryData.FromString(BuildReplyRequest(profile, messages, model, instructions)));
            var response = await client.CreateResponseAsync(body, new RequestOptions { CancellationToken = token });
            using var document = JsonDocument.Parse(response.GetRawResponse().Content.ToMemory());
            telemetry.ReadUsage(document.RootElement);
            OpenAiServiceTierPolicy.ValidateResponse(document.RootElement, OpenAiOperation.WandWorkshop);
            return ParseReply(document.RootElement);
        }
        catch (ClientResultException error) when (error.Status is >= 400 and < 500)
        {
            telemetry.Fail(error, token);
            throw new MobileWandQuestionAuthoringException("dialogue_provider_rejected", true);
        }
        catch (MobileWandQuestionAuthoringException) { throw; }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            telemetry.Fail(error, token);
            throw new MobileWandQuestionAuthoringException("dialogue_provider_uncertain", false);
        }
    }

    internal static string BuildReplyRequest(CharacterConversationSnapshot profile,
        MobileWandWorkshopMessage[] messages, string model, string? instructions = null, bool stream = false)
    {
        if (!IsPreparedProfile(profile) || messages.Length > 160)
            throw new ArgumentException("A prepared profile and bounded conversation are required.");
        var facts = profile.Enrichment!.Facts;
        var context = JsonSerializer.Serialize(new
            {
                character = profile.UserInput,
                enrichment = new
                {
                    profile.Enrichment!.RecognizedName, profile.Enrichment.World, profile.Enrichment.Summary,
                    facts = facts.Select(fact => new { id = StableFactId(profile, fact), fact.Text, fact.SourceUrls }),
                    profile.Enrichment.ConversationIdeas, profile.Enrichment.Ambiguities,
                    profile.Enrichment.NeedsClarification, profile.Enrichment.Sources,
                    profile.Enrichment.EvidenceKind, profile.Enrichment.InputRevision
                },
                conversation = messages.OrderBy(message => message.CreatedAt).Select(message => new
                { message.MessageId, message.Revision, message.Role, message.Text, message.SourceMode })
            }, Json);
        var payload = JsonSerializer.Serialize(new
        {
            model, stream, store = false, service_tier = OpenAiServiceTierPolicy.For(OpenAiOperation.WandWorkshop), max_output_tokens = 420,
            reasoning = new { effort = "none" },
            instructions = instructions ?? DefaultInstructions,
            input = stream ? (object)new[] { new { type = "message", role = "user",
                content = new[] { new { type = "input_text", text = context } } } } : context,
            text = new { format = new { type = "json_schema", name = "mobile_wand_dialogue", strict = true,
                schema = new { type = "object", additionalProperties = false,
                    properties = new { readyToFinish = new { type = "boolean" },
                        reply = new { type = "string", minLength = 1, maxLength = 600 } },
                    required = new[] { "readyToFinish", "reply" } } } }
        }, Json);
        if (Encoding.UTF8.GetByteCount(payload) > 65_536)
            throw new MobileWandQuestionAuthoringException("dialogue_context_too_large", false);
        return payload;
    }

    internal static MobileWandDialogueDraft ParseReply(JsonElement root)
    {
        if (!root.TryGetProperty("status", out var status) || status.GetString() != "completed")
            throw new MobileWandQuestionAuthoringException("dialogue_response_incomplete", false);
        var text = ReadResponseText(root);
        using var shape = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 4 });
        if (shape.RootElement.ValueKind != JsonValueKind.Object
            || shape.RootElement.EnumerateObject().Count() != 2
            || !shape.RootElement.TryGetProperty("reply", out var reply) || reply.ValueKind != JsonValueKind.String
            || !shape.RootElement.TryGetProperty("readyToFinish", out var ready)
            || ready.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
            || shape.RootElement.EnumerateObject().Select(property => property.Name).Distinct(StringComparer.Ordinal).Count() != 2)
        {
            throw new MobileWandQuestionAuthoringException("dialogue_response_invalid", false);
        }
        var result = JsonSerializer.Deserialize<MobileWandDialogueDraft>(text, Json)
            ?? throw new MobileWandQuestionAuthoringException("dialogue_response_invalid", false);
        if (!WandGenerationValidation.Text(result.Reply, 600) || result.Reply.Contains('\n'))
            throw new MobileWandQuestionAuthoringException("dialogue_response_invalid", false);
        return result with { Reply = result.Reply.Trim() };
    }

    private static string ReadResponseText(JsonElement root)
    {
        var text = new StringBuilder();
        foreach (var item in root.GetProperty("output").EnumerateArray())
        {
            if (item.GetProperty("type").GetString() != "message") continue;
            foreach (var content in item.GetProperty("content").EnumerateArray())
            {
                if (content.GetProperty("type").GetString() != "output_text")
                    throw new MobileWandQuestionAuthoringException("dialogue_response_refused", true);
                text.Append(content.GetProperty("text").GetString());
            }
        }
        return text.ToString();
    }

    private static bool IsPreparedProfile(CharacterConversationSnapshot profile) =>
        profile.Revision > 0 && profile.Enrichment is { NeedsClarification: false } enrichment
        && enrichment.InputRevision == profile.Revision;

    private static string StableFactId(CharacterConversationSnapshot profile, CharacterFact fact) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(profile.ProfileId + "\n" + profile.Revision
            + "\n" + fact.Text + "\n" + string.Join("\n", fact.SourceUrls.Order(StringComparer.Ordinal)))))
            .ToLowerInvariant();

    public void Dispose() => http.Dispose();
}
