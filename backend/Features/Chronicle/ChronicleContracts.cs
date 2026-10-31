using System.Text.Json.Serialization;

namespace Kufr.Backend.Features.Chronicle;

internal sealed record ChronicleParticipant(string AccountId, string DisplayName, string Role,
    string? CharacterName = null, string? WandName = null, string? VisualSnapshotJson = null,
    string VisualReferenceVersion = "unknown");

internal sealed record ChronicleDecision(string AccountId, string Choice, string Consequence,
    string? Evidence = null);

internal sealed record ChronicleAward(string Id, string Title, string Scope, string Evidence,
    string EventId = "", long AcquiredAt = 0);

internal sealed record ChronicleParticipantView(string AccountId, string DisplayName, string Role,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? CharacterName,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? WandName,
    string VisualReferenceVersion);

internal sealed record ChronicleDecisionView(string AccountId, string Choice, string Consequence,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Evidence);

/// <summary>Immutable, public-to-participants facts captured at the source event.</summary>
internal sealed record ChronicleEventInput(string SourceKind, string SourceId, string GroupId,
    long OccurredAt, string Title, string Summary, IReadOnlyList<ChronicleParticipant> Participants,
    string? Result = null, string? WinnerId = null, IReadOnlyList<string>? Story = null,
    IReadOnlyList<ChronicleDecision>? Decisions = null, string? ShareText = null,
    bool IllustrationRequested = false, string? Visibility = null, string? DecisiveChoice = null,
    IReadOnlyList<string>? VisualMotifs = null);

internal sealed record ChronicleIllustration(byte[] Bytes, string MediaType, bool Synthetic = false);

internal interface IChronicleIllustrator
{
    bool RequiresCurrentAiConsent => false;
    Task<ChronicleIllustration> GenerateAsync(ChronicleEventInput snapshot, CancellationToken cancellationToken);
}

internal sealed record ChronicleEventView(string Id, string SourceKind, string SourceId, string GroupId,
    long OccurredAt, string Title, string Summary, IReadOnlyList<ChronicleParticipantView> Participants,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Result,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? WinnerId,
    IReadOnlyList<string> Story, IReadOnlyList<ChronicleDecisionView> Decisions,
    IReadOnlyList<ChronicleAward> Awards, string IllustrationState, bool Revealed,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ShareText,
    int Revision,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? DecisiveChoice = null,
    bool IllustrationSynthetic = false, bool ShareAllowed = false, string? GroupName = null);

internal sealed record ChroniclePage(IReadOnlyList<ChronicleEventView> Items, string? NextCursor,
    IReadOnlyList<ChronicleAward> Badges);

internal sealed record ChronicleOutboxEnvelope(ChronicleEventInput Event,
    IReadOnlyList<string> OwnerIds, IReadOnlyDictionary<string, IReadOnlyList<ChronicleAward>> AwardsByOwner);

internal sealed record ChronicleImage(string MediaType, byte[] Bytes);

internal sealed record ChronicleJobLease(string Id, ChronicleEventInput Event, int Fence, int Attempts);

internal sealed record ChronicleShareProjection(string Title,
    IReadOnlyList<ChronicleParticipantView> Participants, string? Result, string? ShareText,
    ChronicleImage? Image);
