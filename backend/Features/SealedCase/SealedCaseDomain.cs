using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Kufr.Backend.Features.SealedCase;

internal enum SealedPlayerState { Awake, Sleeping, Unconscious, Echo }
internal enum SealedIntent { Hold, Rest, Guard, Prepare, Investigate, Follow, Steal, Sabotage, Stun, Lethal, Help, EchoReview, EchoDeliver, EchoScene, Dream, Mask, FeignSleep, Transfer }
internal enum SealedConditionKind { TargetAppearsAsleep, VerifiedSleep }
internal enum SealedResult { Success, Defended, Hold, RequestValid }
internal sealed class SealedCaseRuleException(string message) : Exception(message);

internal sealed record SealedCondition(SealedConditionKind Kind, string? EvidenceId = null);
internal sealed record SealedAction(string Id, string ActorId, int Chapter, SealedIntent Intent,
    string? TargetId = null, string? Destination = null, string? Method = null, SealedCondition? Condition = null,
    string Fallback = "HOLD", bool LethalConfirmed = false, int Revision = 1, SealedIntent? PreparedIntent = null)
{
    internal void Validate()
    {
        if (!Enum.IsDefined(Intent) || PreparedIntent is { } prepared && !Enum.IsDefined(prepared)
            || Condition is { } condition && !Enum.IsDefined(condition.Kind))
            throw new SealedCaseRuleException("Unknown action enum.");
        if (Condition is not null && Intent is SealedIntent.Rest or SealedIntent.Prepare or SealedIntent.Guard or SealedIntent.FeignSleep)
            throw new SealedCaseRuleException("Conditional phase-two actions are not supported.");
        if (string.IsNullOrWhiteSpace(Id) || string.IsNullOrWhiteSpace(ActorId) || Chapter < 1 || Revision < 1 || Fallback != "HOLD")
            throw new SealedCaseRuleException("Invalid action envelope.");
        if (Intent == SealedIntent.Prepare && (PreparedIntent is null || PreparedIntent is SealedIntent.Hold or SealedIntent.Rest or SealedIntent.Prepare))
            throw new SealedCaseRuleException("PREPARE requires one declared next-action intent.");
        if (Intent != SealedIntent.Prepare && PreparedIntent is not null)
            throw new SealedCaseRuleException("Only PREPARE may declare prepared intent.");
        if (Condition is { Kind: SealedConditionKind.TargetAppearsAsleep, EvidenceId: not null })
            throw new SealedCaseRuleException("Appearance condition cannot carry hidden-state evidence.");
        if (Condition is { Kind: SealedConditionKind.VerifiedSleep } && string.IsNullOrWhiteSpace(Condition.EvidenceId))
            throw new SealedCaseRuleException("Verified sleep requires evidence id.");
    }
}

internal sealed class SealedActionBook
{
    internal Dictionary<(string Actor, int Chapter), SealedAction> Orders { get; } = [];
    internal SealedAction Confirm(SealedAction action, string authenticatedActor, long now, long deadline, long expectedRevision)
    {
        action.Validate();
        if (authenticatedActor != action.ActorId) throw new SealedCaseRuleException("Actor must match authenticated session.");
        var key = (action.ActorId, action.Chapter);
        Orders.TryGetValue(key, out var previous);
        if (previous == action) return previous;
        if (now >= deadline) throw new SealedCaseRuleException("Chapter locked.");
        var actualRevision = previous?.Revision ?? 0;
        if (actualRevision != expectedRevision || action.Revision != actualRevision + 1)
            throw new SealedCaseRuleException("Action revision conflict.");
        Orders[key] = action;
        return action;
    }
}

internal sealed class SealedPlayer(string id, string location = "SALON", IEnumerable<string>? knownPlayers = null,
    IEnumerable<string>? observedPlayers = null, SealedPlayerState state = SealedPlayerState.Awake,
    IEnumerable<string>? methods = null, IEnumerable<string>? expertise = null, int extremeToken = 1)
{
    internal string Id { get; } = id;
    internal string Location { get; set; } = location;
    internal SealedPlayerState State { get; set; } = state;
    internal HashSet<string> KnownPlayers { get; } = new(knownPlayers ?? [], StringComparer.Ordinal);
    internal HashSet<string> ObservedPlayers { get; } = new(observedPlayers ?? [], StringComparer.Ordinal);
    internal HashSet<string> Methods { get; } = new(methods ?? ["IMPACT_SEAL", "BREAKING_SEAL"], StringComparer.Ordinal);
    internal HashSet<string> Expertise { get; } = new(expertise ?? [], StringComparer.Ordinal);
    internal int ExtremeToken { get; set; } = extremeToken;
    internal bool FeigningSleep { get; set; }
    internal int Ward { get; set; }
    internal int Cover { get; set; }
    internal int Preparation { get; set; }
    internal int PreparationChapter { get; set; }
    internal SealedIntent? PreparedIntent { get; set; }
    internal int UnconsciousUntilEnd { get; set; }
    internal int SleepingUntilStart { get; set; }
    internal Dictionary<string, string> VerifiedSleepEvidence { get; } = new(StringComparer.Ordinal);
    internal SealedPlayer Copy()
    {
        var copy = new SealedPlayer(Id, Location, KnownPlayers, ObservedPlayers, State, Methods, Expertise, ExtremeToken)
        { FeigningSleep = FeigningSleep, Ward = Ward, Cover = Cover, Preparation = Preparation,
          PreparationChapter = PreparationChapter, PreparedIntent = PreparedIntent, UnconsciousUntilEnd = UnconsciousUntilEnd, SleepingUntilStart = SleepingUntilStart };
        foreach (var item in VerifiedSleepEvidence) copy.VerifiedSleepEvidence[item.Key] = item.Value;
        return copy;
    }
}

internal sealed record SealedModifiers(int AttackBase, int Expertise, int Preparation, int DefenseBase, int Protection, int Cover);
internal sealed record SealedRolls(int Attack, int Defense, int AttackTotal, int DefenseTotal);
internal sealed record SealedEvent(string Id, int Chapter, int Phase, string ActorId, string? TargetId,
    SealedIntent Intent, SealedResult Result, string? Reason, SealedModifiers? Modifiers = null, SealedRolls? Rolls = null,
    bool TraceCreated = false, IReadOnlyList<string>? CausalParentIds = null);

internal sealed partial class SealedGame
{
    private readonly string _seed;
    private readonly Dictionary<string, HashSet<string>> _adjacency;
    private readonly Func<string, string, int> _dice;
    private readonly Dictionary<int, (string Fingerprint, IReadOnlyList<SealedEvent> Events)> _resolved = [];
    internal Dictionary<string, SealedPlayer> Players { get; }
    internal SealedGame(IEnumerable<SealedPlayer> players, string seed, IReadOnlyDictionary<string, string[]>? adjacency = null, Func<string,string,int>? dice = null)
    {
        Players = players.ToDictionary(p => p.Id, p => p.Copy(), StringComparer.Ordinal);
        if (Players.Count == 0) throw new SealedCaseRuleException("At least one player required.");
        _seed = seed;
        _adjacency = (adjacency ?? new Dictionary<string,string[]> { ["SALON"] = ["VESTIBUL", "ARCHIV", "POKOJE", "KOMORA"], ["VESTIBUL"] = ["SALON", "ARCHIV"], ["ARCHIV"] = ["SALON", "VESTIBUL"], ["POKOJE"] = ["SALON"], ["KOMORA"] = ["SALON"] }).ToDictionary(x => x.Key, x => x.Value.ToHashSet(StringComparer.Ordinal), StringComparer.Ordinal);
        _dice = dice ?? ((eventId, side) => StableDie(seed, eventId, side));
    }

    internal IReadOnlyList<SealedEvent> Resolve(int chapter, IReadOnlyList<SealedAction> submitted)
    {
        if (chapter < 1 || submitted.Any(a => a.Chapter != chapter)) throw new SealedCaseRuleException("Wrong chapter.");
        if (submitted.Select(a => a.ActorId).Distinct(StringComparer.Ordinal).Count() != submitted.Count) throw new SealedCaseRuleException("Only the latest order per actor is accepted.");
        if (submitted.Any(a => !Players.ContainsKey(a.ActorId))) throw new SealedCaseRuleException("Unknown actor.");
        foreach (var action in submitted) action.Validate();
        var fingerprint = JsonSerializer.Serialize(submitted.OrderBy(a => a.ActorId, StringComparer.Ordinal));
        if (_resolved.TryGetValue(chapter, out var replay))
        {
            if (replay.Fingerprint != fingerprint) throw new SealedCaseRuleException("Resolved chapter cannot be rewritten.");
            return replay.Events;
        }
        if (_resolved.Keys.Any(c => c > chapter)) throw new SealedCaseRuleException("Chapter order must be monotonic.");
        var working = Players.ToDictionary(p => p.Key, p => p.Value.Copy(), StringComparer.Ordinal);
        var actions = submitted.ToDictionary(a => a.ActorId, StringComparer.Ordinal);
        foreach (var player in working.Values)
        {
            player.Ward = 0; player.FeigningSleep = false;
            if (player.State == SealedPlayerState.Sleeping && player.SleepingUntilStart <= chapter) player.State = SealedPlayerState.Awake;
            if (player.State == SealedPlayerState.Unconscious && player.UnconsciousUntilEnd < chapter) player.State = SealedPlayerState.Awake;
            if (player.PreparationChapter < chapter) { player.Preparation = 0; player.PreparedIntent = null; }
        }
        var events = new Dictionary<string, SealedEvent>(StringComparer.Ordinal);
        var denied = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (actorId, action) in actions.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            var actor = working[actorId];
            var stateAllowsAction = actor.State switch
            {
                SealedPlayerState.Awake => action.Intent is not (SealedIntent.Dream or SealedIntent.EchoReview or SealedIntent.EchoDeliver or SealedIntent.EchoScene),
                SealedPlayerState.Sleeping => action.Intent == SealedIntent.Hold,
                SealedPlayerState.Unconscious => action.Intent is SealedIntent.Hold or SealedIntent.Dream,
                SealedPlayerState.Echo => action.Intent is SealedIntent.Hold or SealedIntent.EchoReview or SealedIntent.EchoDeliver or SealedIntent.EchoScene,
                _ => false
            };
            if (!stateAllowsAction)
            { events[actorId] = Event(action, SealedResult.Hold, 1, "ACTION_UNAVAILABLE_IN_STATE"); denied.Add(actorId); continue; }
            if (action.Destination is not null && action.Destination != actor.Location)
            {
                if (actor.State != SealedPlayerState.Awake || action.Intent == SealedIntent.Hold || !_adjacency.GetValueOrDefault(actor.Location, []).Contains(action.Destination))
                { events[actorId] = Event(action, SealedResult.Hold, 1, "NO_ACCESS"); denied.Add(actorId); continue; }
                actor.Location = action.Destination; actor.Cover = 0;
            }
        }
        foreach (var (actorId, action) in actions.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            if (denied.Contains(actorId)) continue;
            var p = working[actorId];
            if (p.State == SealedPlayerState.Echo && action.Intent is not (SealedIntent.Hold or SealedIntent.EchoReview or SealedIntent.EchoDeliver or SealedIntent.EchoScene)) { Reject(action, "BODY_UNAVAILABLE"); continue; }
            if (p.State == SealedPlayerState.Unconscious && action.Intent is not (SealedIntent.Hold or SealedIntent.Dream)) { Reject(action, "UNCONSCIOUS"); continue; }
            if (p.State == SealedPlayerState.Sleeping && action.Intent != SealedIntent.Hold) { Reject(action, "ASLEEP_BEFORE_ACTION"); continue; }
            if (action.Intent == SealedIntent.FeignSleep) { p.FeigningSleep = true; events[actorId] = Event(action, SealedResult.Success, 2); }
            else if (action.Intent is SealedIntent.Rest or SealedIntent.Prepare)
            {
                p.Preparation = 1; p.PreparationChapter = chapter + 1; p.PreparedIntent = action.Intent == SealedIntent.Prepare ? action.PreparedIntent : null;
                if (action.Intent == SealedIntent.Rest) { p.State = SealedPlayerState.Sleeping; p.SleepingUntilStart = chapter + 1; }
                events[actorId] = Event(action, SealedResult.Success, 2);
            }
            else if (action.Intent == SealedIntent.Guard)
            {
                var target = working.GetValueOrDefault(action.TargetId ?? actorId);
                if (target is null || target.State == SealedPlayerState.Echo || target.Location != p.Location) Reject(action, "GUARD_TARGET_UNREACHABLE", 2);
                else { target.Ward = 1; events[actorId] = Event(action, SealedResult.Success, 2); }
            }
        }
        var snapshot = working.ToDictionary(p => p.Key, p => p.Value.Copy(), StringComparer.Ordinal);
        var pending = new Dictionary<string, SealedPlayerState>(StringComparer.Ordinal);
        var spend = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (actorId, action) in actions.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            if (events.ContainsKey(actorId)) continue;
            var actor = snapshot[actorId];
            if (action.Intent == SealedIntent.Hold) { Reject(action, "NO_CONFIRMED_ACTION"); continue; }
            if (action.Intent is SealedIntent.Investigate or SealedIntent.Follow or SealedIntent.Steal or SealedIntent.Sabotage or SealedIntent.Mask or SealedIntent.Transfer or SealedIntent.Dream or SealedIntent.EchoReview or SealedIntent.EchoDeliver or SealedIntent.EchoScene)
            { events[actorId] = Event(action, SealedResult.RequestValid, 3, "SCENARIO_ADAPTER_REQUIRED"); continue; }
            var target = snapshot.GetValueOrDefault(action.TargetId ?? "");
            string? reason = null;
            if (target is null || !actor.KnownPlayers.Contains(target.Id)) reason = "UNKNOWN_TARGET";
            else if (target.State == SealedPlayerState.Echo) reason = "TARGET_HAS_NO_BODY";
            else if (target.Location != actor.Location) reason = "TARGET_UNREACHABLE";
            else if (action.Condition is not null && !Trigger(actor, target, action.Condition)) reason = "TRIGGER_NOT_OBSERVED";
            else if (action.Intent == SealedIntent.Help)
            {
                if (target.State == SealedPlayerState.Unconscious) { AddConsequence(target.Id, SealedPlayerState.Awake); events[actorId] = Event(action, SealedResult.Success, 3); continue; }
                else reason = "NO_CURRENT_HELP_TARGET";
            }
            else if (action.Method is null || !actor.Methods.Contains(action.Method)) reason = "METHOD_UNAVAILABLE";
            else if (action.Intent == SealedIntent.Stun && action.Method != "IMPACT_SEAL" || action.Intent == SealedIntent.Lethal && action.Method != "BREAKING_SEAL") reason = "METHOD_INCOMPATIBLE_WITH_INTENT";
            else if (action.Intent == SealedIntent.Lethal && chapter < 2) reason = "LETHAL_DISABLED_IN_FIRST_CHAPTER";
            else if (action.Intent == SealedIntent.Lethal && !action.LethalConfirmed) reason = "LETHAL_NOT_EXPLICITLY_CONFIRMED";
            else if (action.Intent == SealedIntent.Lethal && actor.ExtremeToken == 0) reason = "EXTREME_TOKEN_UNAVAILABLE";
            else if (action.Intent is not (SealedIntent.Stun or SealedIntent.Lethal)) reason = "REFERENCE_NOT_IMPLEMENTED";
            if (reason is not null) { Reject(action, reason); continue; }
            var eid = EventId(_seed, chapter, actorId);
            var expertise = actor.Expertise.Contains(action.Intent.ToString().ToUpperInvariant()) ? 1 : 0;
            var prepared = actor.Preparation == 1 && actor.PreparationChapter == chapter && (actor.PreparedIntent is null || actor.PreparedIntent == action.Intent) ? 1 : 0;
            var defenseBase = target!.State == SealedPlayerState.Awake ? 2 : 0;
            var attackDie = _dice(eid, "attack"); var defenseDie = _dice(eid, "defense");
            if (attackDie is < 1 or > 6 || defenseDie is < 1 or > 6) throw new SealedCaseRuleException("Dice adapter returned invalid die.");
            var attackTotal = attackDie + 2 + expertise + prepared; var defenseTotal = defenseDie + defenseBase + target.Ward + target.Cover;
            var success = attackTotal > defenseTotal;
            events[actorId] = new(eid, chapter, 3, actorId, target.Id, action.Intent, success ? SealedResult.Success : SealedResult.Defended, null,
                new(2, expertise, prepared, defenseBase, target.Ward, target.Cover), new(attackDie, defenseDie, attackTotal, defenseTotal), true, []);
            if (action.Intent == SealedIntent.Lethal) spend.Add(actorId);
            if (prepared == 1) { working[actorId].Preparation = 0; working[actorId].PreparedIntent = null; working[actorId].PreparationChapter = 0; }
            if (success) AddConsequence(target.Id, action.Intent == SealedIntent.Lethal ? SealedPlayerState.Echo : SealedPlayerState.Unconscious);
        }
        foreach (var actor in spend) working[actor].ExtremeToken = 0;
        foreach (var (id, state) in pending)
        {
            var p = working[id];
            var alreadyUnconscious = p.State == SealedPlayerState.Unconscious;
            p.State = state;
            p.UnconsciousUntilEnd = state == SealedPlayerState.Unconscious ? alreadyUnconscious ? p.UnconsciousUntilEnd : chapter + 1 : 0;
            p.SleepingUntilStart = 0;
        }
        foreach (var p in working.Values)
        {
            if (p.State == SealedPlayerState.Unconscious && p.UnconsciousUntilEnd == chapter) { p.State = SealedPlayerState.Awake; p.UnconsciousUntilEnd = 0; }
            if (p.PreparationChapter <= chapter) { p.Preparation = 0; p.PreparedIntent = null; p.PreparationChapter = 0; }
        }
        Players.Clear(); foreach (var p in working) Players.Add(p.Key, p.Value);
        var output = events.Values.OrderBy(x => x.ActorId, StringComparer.Ordinal).ToArray();
        _resolved[chapter] = (fingerprint, output);
        return output;

        void AddConsequence(string playerId, SealedPlayerState consequence)
        {
            if (!pending.TryGetValue(playerId, out var current) || Rank(consequence) > Rank(current)) pending[playerId] = consequence;
        }
        static int Rank(SealedPlayerState state) => state switch { SealedPlayerState.Echo => 3, SealedPlayerState.Unconscious => 2, _ => 0 };
        void Reject(SealedAction action, string reason, int phase = 3) => events[action.ActorId] = Event(action, SealedResult.Hold, phase, reason);
    }

    private bool Trigger(SealedPlayer actor, SealedPlayer target, SealedCondition condition)
    {
        if (!actor.ObservedPlayers.Contains(target.Id) || actor.Location != target.Location) return false;
        return condition.Kind switch
        {
            SealedConditionKind.TargetAppearsAsleep => target.State == SealedPlayerState.Sleeping || target.FeigningSleep,
            SealedConditionKind.VerifiedSleep => target.State == SealedPlayerState.Sleeping && actor.VerifiedSleepEvidence.GetValueOrDefault(target.Id) == condition.EvidenceId,
            _ => false
        };
    }
    private static SealedEvent Event(SealedAction a, SealedResult result, int phase, string? reason = null) => new(EventId("", a.Chapter, a.ActorId), a.Chapter, phase, a.ActorId, a.TargetId, a.Intent, result, reason);
    private static string EventId(string seed, int chapter, string actor) => "ev_" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{seed}|c{chapter}:{actor}:main"))).ToLowerInvariant()[..24];
    private static int StableDie(string seed, string eventId, string side)
    {
        for (var nonce = 0; nonce < 1000; nonce++)
        {
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{seed}|{eventId}|{side}|{nonce}"));
            foreach (var b in bytes) if (b < 252) return b % 6 + 1;
        }
        throw new InvalidOperationException("RNG exhaustion");
    }
}

internal sealed record SealedWorldEvent(string Id, int Chapter, string ActorId, string? TargetId, string Intent,
    string Result, IReadOnlyDictionary<string,string> PrivateData, string? SecretRoll);
internal sealed class SealedWorldLog
{
    private readonly List<SealedWorldEvent> _events = [];
    internal IReadOnlyList<SealedWorldEvent> Events => _events;
    internal void Append(SealedWorldEvent item) { if (_events.Any(x => x.Id == item.Id)) throw new SealedCaseRuleException("Duplicate world event id."); _events.Add(item); }
}
internal sealed record SealedExperience(string Id, string ObserverId, string? SourceEventId, string Text, string Confidence,
    int Phase = 3, string Modality = "SAW", string? PerceivedState = null)
{
    internal string Classification => Confidence switch
    {
        "REPORTED" => "STATEMENT", "INFERRED" => "INFERENCE", "UNCERTAIN" => "UNCERTAIN_FRAGMENT", _ => "CONFIRMED_OBSERVATION"
    };
}
internal enum SealedPresentationKind { Statement, PhysicalPerformance, DisplayedEvidence }
internal sealed record SealedAudience(string Kind, IReadOnlyList<string> Recipients)
{
    internal static SealedAudience Public { get; } = new("PUBLIC", []);
    internal static SealedAudience Players(IReadOnlyList<string> recipients) => new("PLAYERS", recipients);
}
internal sealed record SealedPresentation(string Id, string AuthorId, string Text, SealedAudience Audience,
    IReadOnlyList<string> DeliveryReceipts, bool Published, SealedPresentationKind Kind = SealedPresentationKind.Statement);
internal sealed record SealedCovertOrder(string Id, string ActorId, string Intent, string? TargetId, bool Confirmed, IReadOnlyList<string> WorldEventRefs)
{
    // Trusted observer/action disclosure, separate from internal event provenance.
    // A source writer must explicitly authorize these for this order's actor;
    // neither hidden event existence nor SourceEventId grants causal knowledge.
    internal IReadOnlyList<string> AuthorizedResultRefs { get; init; } = [];
    internal bool ExecutionAcknowledged { get; init; }
}
internal sealed record SealedWorldTruthProjection(string Source, string Access, IReadOnlyList<string> EventRefs);
internal sealed record SealedPresentationProjection(string Id, string AuthorId, string Text, SealedPresentationKind Kind, string AudienceKind, IReadOnlyList<string> Recipients, bool Published, IReadOnlyList<string> DeliveryReceipts);
internal sealed record SealedCovertProjection(string ActionRef, string Intent, bool Confirmed, string ExecutionStatus, IReadOnlyList<string> WorldEventRefs, IReadOnlyList<string> PerceivedResultRefs);
internal sealed record SealedPlayerProjection(string PlayerId, SealedWorldTruthProjection WorldTruth,
    IReadOnlyList<SealedExperience> Experience, IReadOnlyList<SealedPresentationProjection> Presentations,
    IReadOnlyList<SealedCovertProjection> CovertActions);

internal static class SealedProjection
{
    internal static SealedPlayerProjection ForPlayer(string playerId, SealedWorldLog world,
        IReadOnlyList<SealedExperience> experiences, IReadOnlyList<SealedPresentation> presentations,
        IReadOnlyList<SealedCovertOrder> orders, bool revealed)
    {
        var ownExperience = experiences.Where(x => x.ObserverId == playerId).ToArray();

        var p = PresentationForPlayer(presentations, playerId).Select(x => new SealedPresentationProjection(x.Id, x.AuthorId, x.Text, x.Kind, x.Audience.Kind, x.Audience.Recipients, x.AuthorId == playerId && x.Published, x.DeliveryReceipts)).ToArray();
        var c = orders.Where(x => x.ActorId == playerId).Select(order =>
        {
            var refs = revealed ? order.WorldEventRefs.Where(id => world.Events.Any(e => e.Id == id && e.ActorId == playerId)).ToArray() : [];
            var perceived = order.Confirmed ? ownExperience.Where(x => order.AuthorizedResultRefs.Contains(x.Id, StringComparer.Ordinal))
                .Select(x => x.Id).Distinct(StringComparer.Ordinal).ToArray() : [];
            return new SealedCovertProjection(order.Id, order.Intent, order.Confirmed,
                perceived.Length > 0 || revealed && refs.Length > 0 ? "RESOLVED"
                    : order.Confirmed && order.ExecutionAcknowledged ? "RESOLVED_RESULT_NOT_OBSERVED" : "PLANNED",
                refs, perceived);
        }).ToArray();
        var truthRefs = revealed ? world.Events.Where(e => e.ActorId == playerId || e.TargetId == playerId).Select(e => e.Id).Distinct().ToArray() : [];
        return new(playerId, new("GLOBAL_EVENT_LOG", revealed ? "REVEALED_REFERENCES" : "WITHHELD", truthRefs),
            ownExperience.Select(x => x with { SourceEventId = revealed ? x.SourceEventId : null }).ToArray(), p, c);
    }

    internal static IReadOnlyList<SealedPresentation> PresentationForPlayer(IReadOnlyList<SealedPresentation> records, string viewer) => records.Where(r =>
        r.AuthorId == viewer || r.DeliveryReceipts.Contains(viewer, StringComparer.Ordinal) &&
        (r.Audience.Kind == "PUBLIC" || r.Audience.Recipients.Contains(viewer, StringComparer.Ordinal)))
        .Select(r => r with { Audience = new SealedAudience(r.Audience.Kind, r.AuthorId == viewer ? r.Audience.Recipients.ToArray() : []), DeliveryReceipts = r.AuthorId == viewer ? r.DeliveryReceipts.ToArray() : [] }).ToArray();

    internal static IReadOnlyList<SealedPresentationProjection> PublicFeed(IReadOnlyList<SealedPresentation> records) => records
        .Where(r => r.Audience.Kind == "PUBLIC" && r.Published)
        .Select(r => new SealedPresentationProjection(r.Id, r.AuthorId, r.Text, r.Kind, "PUBLIC", [], true, []))
        .ToArray();
}
