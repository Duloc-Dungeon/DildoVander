using System.Security.Cryptography;
using System.Text;
using Kufr.Backend.Features.Conversations;
using Kufr.Backend.Features.Character;

namespace Kufr.Backend.Features.GptLive;

internal enum LiveActorMode { AutonomousActor }
internal sealed record LiveConversationPolicy(int UsualAnswers, int MaximumAnswers, bool AllowEarlySelection);

internal sealed record GptLiveOptions
{
    internal CharacterPolicy Character { get; init; } = new();
    internal GptLiveVoiceOptions Voice { get; init; } = new();
    internal LiveActorMode Mode { get; init; } = LiveActorMode.AutonomousActor;
    internal string DecisionModel { get; init; } = ResponsesTextDialogBudget.Model;
    internal bool AdultLanguage { get; init; } = true;
    internal CharacterPolicy EffectiveCharacter => !AdultLanguage && Character.HostTone > CharacterTone.Dry
        ? Character with { Enabled = true, HostTone = CharacterTone.Dry }
        : Character with { Enabled = true };
    internal int TranscriptCharacterLimit { get; init; } = 128_000;
    internal int DecisionContextBytes { get; init; } = 8_000;
    internal int DelegationContextWaitMs { get; init; } = 350;
    internal int InputQuietMs { get; init; } = 1_200;
    internal int DelegationTimeoutMs { get; init; } = 8_000;
    internal int OutputPauseMs { get; init; } = 900;
    internal int TerminalQuietMs { get; init; } = 4_000;
    internal int TerminalTimeoutSeconds { get; init; } = 35;
    internal int SpeechAmplitudeThreshold { get; init; } = 256;
    internal int? UsualAnswersOverride { get; init; }
    internal int? MaximumAnswersOverride { get; init; }

    internal LiveConversationPolicy Policy(string personality) => new(
        UsualAnswersOverride ?? 2,
        MaximumAnswersOverride ?? 3,
        true);

    internal static GptLiveOptions Read(IConfiguration configuration)
    {
        var section = configuration.GetSection("Backend:GptLive");
        var mode = section["ActorMode"] ?? nameof(LiveActorMode.AutonomousActor);
        if (mode == "LegacyBackendScripted") { mode = nameof(LiveActorMode.AutonomousActor); }
        if (!Enum.TryParse<LiveActorMode>(mode, false, out var parsed) || !Enum.IsDefined(parsed))
            throw new InvalidOperationException("Invalid Backend:GptLive:ActorMode.");
        var options = new GptLiveOptions
        {
            Character = CharacterConfiguration.Read(section.GetSection("Character")),
            Voice = GptLiveVoiceOptions.Read(section.GetSection("Voice")),
            Mode = parsed,
            DecisionModel = section["DecisionModel"] ?? ResponsesTextDialogBudget.Model,
            AdultLanguage = section.GetValue("AdultLanguage", true),
            TranscriptCharacterLimit = section.GetValue("TranscriptCharacterLimit", 128_000),
            DecisionContextBytes = section.GetValue("DecisionContextBytes", 8_000),
            DelegationContextWaitMs = section.GetValue("DelegationContextWaitMs", 350),
            InputQuietMs = section.GetValue("InputQuietMs", 1_200),
            DelegationTimeoutMs = section.GetValue("DelegationTimeoutMs", 8_000),
            OutputPauseMs = section.GetValue("OutputPauseMs", 900),
            TerminalQuietMs = section.GetValue("TerminalQuietMs", 4_000),
            TerminalTimeoutSeconds = section.GetValue("TerminalTimeoutSeconds", 35),
            SpeechAmplitudeThreshold = section.GetValue("SpeechAmplitudeThreshold", 256),
            UsualAnswersOverride = section.GetValue<int?>("ConversationPolicy:UsualAnswers"),
            MaximumAnswersOverride = section.GetValue<int?>("ConversationPolicy:MaximumAnswers")
        };
        if (!WandDecisionBudget.Supports(options.DecisionModel)
            || options.TranscriptCharacterLimit is < 16_000 or > 256_000
            || options.DecisionContextBytes is < 2_000 or > 8_000
            || options.DelegationContextWaitMs is < 0 or > 2_000
            || options.InputQuietMs is < 500 or > 3_000
            || options.DelegationTimeoutMs is < 2_000 or > 20_000
            || options.OutputPauseMs is < 500 or > 3_000
            || options.TerminalQuietMs < options.OutputPauseMs || options.TerminalQuietMs > 10_000
            || options.TerminalTimeoutSeconds is < 15 or > 60
            || options.SpeechAmplitudeThreshold is < 32 or > 4_096
            || options.UsualAnswersOverride is < 1 or > 7
            || options.MaximumAnswersOverride is < 1 or > 8
            || PersonalityCatalog.Inventory.Any(p => options.Policy(p.Id).MaximumAnswers < options.Policy(p.Id).UsualAnswers))
            throw new InvalidOperationException("Invalid Backend:GptLive configuration.");
        if (options.Character.Enabled && options.Mode != LiveActorMode.AutonomousActor)
        {
            throw new InvalidOperationException("DV01 requires AutonomousActor; legacy behavior is unchanged.");
        }
        return options with { Character = options.EffectiveCharacter };
    }
}

internal sealed record LivePromptSnapshot(string PersonalityId, string Version, string Sha256,
    string Instructions, LiveConversationPolicy Policy, GptLiveOptions Options)
{
    internal string CatalogVersion { get; init; } = "";
    internal string LoreVersion { get; init; } = "";
    internal string? PersonaRevisionId { get; init; }
    internal string ConversationLanguage { get; init; } = "cs-CZ";
    internal string? OpeningAsidePrompt { get; init; }
    internal LivePromptSnapshot WithOpeningAside(string prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt) || OpeningAsidePrompt is not null) { return this; }
        var instructions = Instructions + "\nTrusted one-time opening aside: " + prompt;
        if (LiveTextBudget.UpperBound(instructions) > 16_384)
        { throw new InvalidDataException("Opening aside exceeds its instruction bound."); }
        return this with
        {
            Instructions = instructions,
            Sha256 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(instructions))),
            OpeningAsidePrompt = prompt
        };
    }
    internal LivePromptSnapshot WithoutOpeningAside()
    {
        if (OpeningAsidePrompt is not { } prompt) { return this; }
        var suffix = "\nTrusted one-time opening aside: " + prompt;
        if (!Instructions.EndsWith(suffix, StringComparison.Ordinal))
        { throw new InvalidDataException("Opening aside snapshot was changed unexpectedly."); }
        var instructions = Instructions[..^suffix.Length];
        return this with
        {
            Instructions = instructions,
            Sha256 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(instructions))),
            OpeningAsidePrompt = null
        };
    }
    internal LivePromptSnapshot WithConversationPreferences(
        Kufr.Backend.Features.Customers.CustomerConversationPreferenceSnapshot? preferences, string language)
    {
        // Persisted English snapshots are compatibility input only. Runtime
        // guest-facing output always remains Czech; never rewrite stored data.
        language = "cs-CZ";
        var policy = preferences is null ? Options.Character
            : Kufr.Backend.Features.Customers.CustomerConversationPreferencePolicy.Restrict(Options.Character, preferences);
        var suffix = preferences is null ? "Conduct this conversation in Czech."
            : new ConversationGuestToneState(Options.Character, preferences).Read().Instructions;
        var instructions = Instructions + "\nTrusted application guest policy: " + suffix;
        if (LiveTextBudget.UpperBound(instructions) > 16_384)
        { throw new InvalidDataException("Guest conversation instructions exceed their bound."); }
        return this with
        {
            Instructions = instructions,
            Sha256 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(instructions))),
            Options = Options with { Character = policy }, ConversationLanguage = language
        };
    }
    internal static LivePromptSnapshot Create(string personality, GptLiveOptions options)
    {
        // Archives retain their original identity; executing a session always uses DV01.
        personality = PersonalityCatalog.DefaultId;
        options = options with { Character = options.EffectiveCharacter };
        var characterInstructions = CharacterConfiguration.Instructions(options.Character);
        return new(personality, "dv01-adult-v1",
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(characterInstructions))),
            characterInstructions, options.Policy(personality), options)
        { CatalogVersion = PartyWandCatalog.Version, LoreVersion = CharacterLoreProfiles.Version };
    }
}

internal static class CaseVoiceAsideEligibility
{
    internal static bool IsEligible(bool opening, bool productionCaseSession, LivePromptSnapshot? prompt) =>
        opening && productionCaseSession && prompt is { Version: "dv01-adult-v1" };
}
