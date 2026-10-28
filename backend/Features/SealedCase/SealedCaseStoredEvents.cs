using System.Text.Json;

namespace Kufr.Backend.Features.SealedCase;

internal sealed partial class SealedCaseStore
{
    // Storage format follows event.schema.json 1.0. Until B2 supplies authorized
    // projections, all truth stays POSTGAME_ONLY with empty public/private text.
    // Simultaneous effects are recorded ONCE by a phase-four commit event, not
    // spuriously attributed to the order in which phase-three actions are read.
    private static JsonElement[] BuildWorldEvents(string caseId, int chapter, long revision, long now,
        SealedAction[] orders, SealedEvent[] results, SealedGameCheckpoint before, SealedGameCheckpoint after)
    {
        var events = new List<JsonElement>();
        var timestamp = DateTimeOffset.FromUnixTimeSeconds(now).ToString("O");
        string Id(string local) => caseId + ":" + local;
        foreach (var e in results)
        {
            var order = orders.Single(x => x.ActorId == e.ActorId);
            var id = Id(e.Id);
            bool? condition = order.Condition is null ? null : e.Reason == "TRIGGER_NOT_OBSERVED" ? false
                : e.Result is SealedResult.Success or SealedResult.Defended ? true : null;
            var modifiers = e.Modifiers is { } m ? new Dictionary<string, int>
            {
                ["attack_base"] = m.AttackBase, ["expertise"] = m.Expertise, ["preparation"] = m.Preparation,
                ["defense_base"] = m.DefenseBase, ["protection"] = m.Protection, ["cover"] = m.Cover
            } : [];
            object? rolls = e.Rolls is { } r ? new { attack = r.Attack, defense = r.Defense, attack_total = r.AttackTotal, defense_total = r.DefenseTotal } : null;
            var intent = e.Intent switch
            {
                SealedIntent.FeignSleep => "FEIGN_SLEEP", SealedIntent.EchoReview => "ECHO_REVIEW",
                SealedIntent.EchoDeliver => "ECHO_DELIVER", SealedIntent.EchoScene => "ECHO_SCENE", _ => e.Intent.ToString().ToUpperInvariant()
            };
            var result = e.Result == SealedResult.RequestValid ? "REQUEST_VALID" : e.Result.ToString().ToUpperInvariant();
            events.Add(Envelope(id, e.Phase, e.ActorId, e.TargetId, intent, result, condition,
                modifiers, rolls, e.CausalParentIds?.Select(Id).ToArray() ?? [],
                e.TraceCreated ? [id + ":trace"] : [], [], [],
                new[] { new { check = e.Reason ?? "ENGINE_ACCEPTED", passed = e.Reason is null, fact_refs = Array.Empty<string>() } }));
        }
        var resources = new List<object>(); var states = new List<object>();
        foreach (var p in after.Players)
        {
            var prior = before.Players.Single(x => x.Id == p.Id);
            if (prior.State != p.State) states.Add(new { player = p.Id, before = prior.State.ToString().ToUpperInvariant(), after = p.State.ToString().ToUpperInvariant() });
            Delta("EXTREME_TOKEN", prior.ExtremeToken, p.ExtremeToken);
            Delta("PREPARATION", prior.Preparation, p.Preparation);
            Delta("WARD", prior.Ward, p.Ward);
            Delta("COVER", prior.Cover, p.Cover);
            void Delta(string resource, int old, int value) { if (old != value) resources.Add(new { player = p.Id, resource, delta = value - old }); }
        }
        events.Add(Envelope(Id($"chapter-{chapter}-commit"), 4, null, null, "CHAPTER_COMMIT", "SUCCESS", null,
            new Dictionary<string, int>(), null, results.Select(x => Id(x.Id)).ToArray(), [], resources, states, Array.Empty<object>()));
        return events.ToArray();

        JsonElement Envelope(string id, int phase, string? actor, string? target, string intent, string result,
            bool? condition, Dictionary<string, int> modifiers, object? rolls, string[] parents, string[] traces,
            IEnumerable<object> resources, IEnumerable<object> states, object prerequisites) => JsonSerializer.SerializeToElement(new
            {
                schema_version = "1.0", id, game_id = caseId, chapter, phase, actor, target, intent,
                prerequisites, condition_result = condition, modifiers, rolls, result,
                causal_parent_ids = parents, resource_changes = resources, state_changes = states, trace_ids = traces,
                context_links = new { world_truth_event_ref = id, experience_refs = Array.Empty<string>(), presentation_refs = Array.Empty<string>(), covert_action_refs = Array.Empty<string>() },
                visibility = "POSTGAME_ONLY", public_projection = new { facts = Array.Empty<object>() },
                private_projections = new Dictionary<string, object>(), reveal_projection = new { facts = Array.Empty<object>() },
                created_at = timestamp, state_version = revision
            }, Json);
    }
}
