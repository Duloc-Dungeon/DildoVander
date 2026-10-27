using System.Text.Json;

namespace Kufr.Backend.Features.SealedCase;

internal enum SealedContextRole { Player, PrivateNarrator, PublicNarrator, ChoiceGenerator, Engine, Moderator, Epilogue }
// Server-owned grants, not an HTTP request contract. Account and character IDs
// are intentionally separate. Owning a lobby never grants a truth-reader role.
internal sealed record SealedContextMember(string AccountId, string? PlayerId, SealedContextRole[] Roles);
internal sealed record SealedContextNotification(string ExperienceId, string Text);
internal sealed record SealedContextFrame(SealedPlayerProjection Context, string ProjectionFor,
    IReadOnlyList<string> AllowedTargetIds, IReadOnlyList<SealedContextNotification> Notifications)
{
    // Explicit allowlist at the serialization boundary, compatible with the v1
    // four-context schema. Neither the world object nor grant metadata is serialized.
    internal JsonElement ToSchema() => JsonSerializer.SerializeToElement(new
    {
        schema_version = "1.0", player_id = Context.PlayerId, projection_for = ProjectionFor,
        WORLD_TRUTH = new { source = Context.WorldTruth.Source, access = Context.WorldTruth.Access, event_refs = Context.WorldTruth.EventRefs },
        EXPERIENCE = Context.Experience.Select(x => new
        {
            id = x.Id, observer = x.ObserverId, source_event_ref = x.SourceEventId,
            perceived_at_phase = x.Phase, modality = x.Modality, classification = x.Classification,
            confidence = x.Confidence, text = x.Text, perceived_state = x.PerceivedState
        }),
        PRESENTATION = Context.Presentations.Select(x => new
        {
            id = x.Id, author = x.AuthorId,
            kind = x.Kind switch { SealedPresentationKind.PhysicalPerformance => "PHYSICAL_PERFORMANCE", SealedPresentationKind.DisplayedEvidence => "DISPLAYED_EVIDENCE", _ => x.AudienceKind == "PUBLIC" ? "PUBLIC_STATEMENT" : "TARGETED_STATEMENT" },
            content = x.Text, audience = new { kind = x.AudienceKind, recipients = x.Recipients },
            delivery_receipts = x.DeliveryReceipts.Select(r => new { recipient = r, status = x.Kind == SealedPresentationKind.PhysicalPerformance ? "OBSERVED" : "DELIVERED", event_ref = (string?)null })
                .Concat(x.Published && x.AudienceKind == "PUBLIC" ? new[] { new { recipient = "PUBLIC", status = "PUBLISHED", event_ref = (string?)null } } : []),
            source_action_ref = (string?)null, epilogue_policy = "KEEP_PRIVATE"
        }),
        COVERT_ACTION = Context.CovertActions.Select(x => new
        {
            action_ref = x.ActionRef, intent_summary = x.Intent,
            confirmation_status = x.Confirmed ? "CONFIRMED" : "DRAFT", execution_status = x.ExecutionStatus,
            world_event_refs = x.WorldEventRefs, perceived_result_refs = x.PerceivedResultRefs
        })
    });
}

// One projection policy reused for players, AI inputs and notification frames.
// This is a server-side boundary; callers must obtain the source and grants from
// case authority, never deserialize them from guest input. C1 owns durable roster
// provisioning; this class deliberately does not create a second roster store.
internal sealed class SealedContextService
{
    private readonly string _caseId;
    private readonly string _groupId;
    private readonly SealedWorldLog _world;
    private readonly SealedExperience[] _experiences;
    private readonly SealedPresentation[] _presentations;
    private readonly SealedCovertOrder[] _orders;
    private readonly SealedContextMember[] _members;
    private readonly Dictionary<string, string[]> _knownTargets;
    private readonly HashSet<string> _releasedRefs;
    private readonly bool _finaleReleased;

    internal SealedContextService(string caseId, string groupId, SealedWorldLog world,
        IReadOnlyList<SealedExperience> experiences, IReadOnlyList<SealedPresentation> presentations,
        IReadOnlyList<SealedCovertOrder> orders, IReadOnlyList<SealedContextMember> members,
        IReadOnlyDictionary<string, string[]> knownTargets, bool finaleReleased = false,
        IEnumerable<string>? releasedEventRefs = null)
    {
        _caseId = caseId; _groupId = groupId; _world = world;
        _experiences = experiences.ToArray();
        _presentations = presentations.Select(x => x with { Audience = new(x.Audience.Kind, x.Audience.Recipients.ToArray()), DeliveryReceipts = x.DeliveryReceipts.ToArray() }).ToArray();
        _orders = orders.Select(x => x with { WorldEventRefs = x.WorldEventRefs.ToArray(), AuthorizedResultRefs = x.AuthorizedResultRefs.ToArray() }).ToArray();
        _members = members.Select(x => x with { Roles = x.Roles.ToArray() }).ToArray();
        _knownTargets = knownTargets.ToDictionary(x => x.Key, x => x.Value.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);
        _finaleReleased = finaleReleased;
        _releasedRefs = new(releasedEventRefs ?? [], StringComparer.Ordinal);
    }

    internal static SealedContextMember DemandAccess(string caseId, string groupId, string authenticatedAccountId,
        SealedContextRole role, SealedContextAuthority authority)
    {
        var grants = authority.Members.Where(x => x.AccountId == authenticatedAccountId).ToArray();
        var matching = grants.Where(x => x.Roles.Contains(role)).ToArray();
        var member = matching.Length == 1 ? matching[0] : null;
        if (caseId != authority.CaseId || groupId != authority.GroupId || string.IsNullOrWhiteSpace(authenticatedAccountId)
            || !Enum.IsDefined(role) || member is null
            || role is SealedContextRole.Engine or SealedContextRole.Moderator && grants.Any(x => x.PlayerId is not null)
            || role is SealedContextRole.Player or SealedContextRole.PrivateNarrator or SealedContextRole.ChoiceGenerator or SealedContextRole.Epilogue && string.IsNullOrWhiteSpace(member.PlayerId)
            || role == SealedContextRole.Epilogue && !authority.FinaleReleased)
            throw new SealedCasePersistenceException("case_not_found", 404);
        return member;
    }

    internal SealedContextFrame Project(string caseId, string groupId, string authenticatedAccountId, SealedContextRole role)
    {
        var member = DemandAccess(caseId, groupId, authenticatedAccountId, role,
            new(_caseId, _groupId, _members, _finaleReleased));
        var player = member.PlayerId ?? "";
        var projection = SealedProjection.ForPlayer(player, _world, _experiences, _presentations, _orders, false);
        var purpose = role switch
        {
            SealedContextRole.PrivateNarrator => "PRIVATE_NARRATOR", SealedContextRole.PublicNarrator => "PUBLIC_NARRATOR",
            SealedContextRole.Engine or SealedContextRole.Moderator => "ENGINE", SealedContextRole.Epilogue => "EPILOGUE", _ => "PLAYER"
        };
        if (role == SealedContextRole.PublicNarrator)
            projection = new("", new("GLOBAL_EVENT_LOG", "WITHHELD", []), [], SealedProjection.PublicFeed(_presentations), []);
        else if (role == SealedContextRole.PrivateNarrator)
            projection = projection with
            {
                Presentations = projection.Presentations.Where(x => x.AuthorId == player).ToArray(),
                CovertActions = projection.CovertActions.Where(x => x.Confirmed).ToArray()
            };
        else if (role is SealedContextRole.Engine or SealedContextRole.Moderator)
            projection = new("", new("GLOBAL_EVENT_LOG", "ENGINE_ONLY", _world.Events.Select(x => x.Id).ToArray()), [], [], []);
        else if (role == SealedContextRole.Epilogue)
        {
            // Release is an explicit server-approved subset, never an end-of-game
            // boolean that silently reveals every event or other people's chats.
            var revealed = SealedProjection.ForPlayer(player, _world, _experiences, _presentations, _orders, true);
            var permitted = revealed.WorldTruth.EventRefs.Where(_releasedRefs.Contains).ToHashSet(StringComparer.Ordinal);
            projection = revealed with
            {
                WorldTruth = new("GLOBAL_EVENT_LOG", "REVEALED_REFERENCES", permitted.Order(StringComparer.Ordinal).ToArray()),
                Experience = revealed.Experience.Select(x => x with { SourceEventId = x.SourceEventId is { } id && permitted.Contains(id) ? id : null }).ToArray(),
                CovertActions = revealed.CovertActions.Select((x, index) => x with
                {
                    WorldEventRefs = x.WorldEventRefs.Where(permitted.Contains).ToArray(),
                    ExecutionStatus = x.WorldEventRefs.Any(permitted.Contains) ? x.ExecutionStatus
                        : projection.CovertActions[index].ExecutionStatus
                }).ToArray()
            };
        }
        // Targets come from knowledge, NOT the hidden roster, physical state,
        // location, eligibility checks or number of pending opposing orders.
        var targets = role is SealedContextRole.Player or SealedContextRole.ChoiceGenerator
            ? _knownTargets.GetValueOrDefault(player, []).ToArray() : Array.Empty<string>();
        var notifications = projection.Experience.Select(x => new SealedContextNotification(x.Id, "Máš nový zápis v deníku.")).ToArray();
        return new(projection, purpose, targets, notifications);
    }
}
