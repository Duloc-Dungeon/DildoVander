using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Kufr.Backend.Features.SealedCase;

internal sealed record SealedClue(string Id, string Chain, string Family, string[] Candidates,
    string Location, int ReleaseChapter, string SourceEvent, int DelayedUntil = 0, int LastDelayedChapter = -2,
    string? RedirectTo = null);
internal sealed record SealedItem(string Id, string Name, string OwnerId, bool Transferable = true);
internal sealed record SealedObservation(string PlayerId, string Location, string Appearance, int Chapter, string SourceEvent, string? Mask);
internal sealed record SealedScenarioState(string ScenarioId, string Variant, SealedClue[] Clues,
    Dictionary<string, string[]> KnownClues, Dictionary<string, string[]> Notes,
    Dictionary<string, SealedItem> Items, Dictionary<string, string> Masks,
    Dictionary<string, SealedObservation[]>? Observations = null, Dictionary<string, SealedExperience[]>? Experiences = null, string? CaseId = null,
    Dictionary<int, string?>? Oversight = null)
{
    internal SealedScenarioState Copy() => new(ScenarioId, Variant, Clues.Select(c => c with { Candidates = [.. c.Candidates] }).ToArray(),
        KnownClues.ToDictionary(p => p.Key, p => p.Value.ToArray(), StringComparer.Ordinal),
        Notes.ToDictionary(p => p.Key, p => p.Value.ToArray(), StringComparer.Ordinal),
        new(Items, StringComparer.Ordinal), new(Masks, StringComparer.Ordinal),
        Observations?.ToDictionary(p => p.Key, p => p.Value.ToArray(), StringComparer.Ordinal),
        Experiences?.ToDictionary(p => p.Key, p => p.Value.ToArray(), StringComparer.Ordinal), CaseId, Oversight is null ? [] : new(Oversight));
}

// Embedded server content. Never return a catalog, target assignment or candidate
// recipe as a player payload; only individual legitimately acquired facts leave it.
internal static class SealedScenario
{
    private static readonly JsonDocument Document = Load("scenario.json");
    internal static JsonElement Definition => Document.RootElement;
    internal static JsonDocument Load(string name)
    {
        var assembly = typeof(SealedScenario).Assembly;
        var resource = assembly.GetManifestResourceNames().Single(n => n.EndsWith(".SealedCase.Content." + name, StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resource)!;
        return JsonDocument.Parse(stream);
    }
    internal static string Digest(string seed, string purpose, string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(seed + "|" + purpose + "|" + value)));

    internal static SealedGame Create(SealedStoredRoster roster, string variant)
    {
        var zones = Definition.GetProperty("map").GetProperty("zones").EnumerateArray().ToArray();
        var adjacency = zones.ToDictionary(z => z.GetProperty("id").GetString()!,
            z => z.GetProperty("adjacent").EnumerateArray().Select(x => x.GetString()!).ToArray(), StringComparer.Ordinal);
        var zoneIds = adjacency.Keys.ToArray();
        var members = roster.Members.OrderBy(m => Digest(roster.ServerSeed, "locations", m.SeatId), StringComparer.Ordinal).ToArray();
        using var roles = Load("roles.json");
        var kits = roles.RootElement.GetProperty("kits").EnumerateArray().ToDictionary(k => k.GetProperty("id").GetString()!, StringComparer.Ordinal);
        var players = members.Select((m, i) => new SealedPlayer(m.PlayerId, zoneIds[i % zoneIds.Length],
            members.Where(other => other.PlayerId != m.PlayerId).Select(other => other.PlayerId),
            expertise: kits[m.Kit.ToString().ToUpperInvariant()].GetProperty("intents").EnumerateArray().Select(intent => intent.GetString()!))).ToArray();
        var graph = Definition.GetProperty("evidence_graph_template");
        var recipe = graph.GetProperty("candidate_recipes").EnumerateArray().Single(r =>
            players.Length >= r.GetProperty("min_players").GetInt32() && players.Length <= r.GetProperty("max_players").GetInt32());
        var releases = graph.GetProperty(variant == "first" ? "tutorial_release_chapters" : "full_release_chapters");
        var targets = members.Where(m => m.Team == SealedSecretTeam.Conspirator)
            .OrderBy(m => Digest(roster.ServerSeed, "chains", m.SeatId), StringComparer.Ordinal).ToArray();
        var clues = new List<SealedClue>();
        for (var index = 0; index < targets.Length; index++)
        {
            var chain = "R" + (index + 1).ToString("D2");
            var others = members.Where(m => m.PlayerId != targets[index].PlayerId)
                .OrderBy(m => Digest(roster.ServerSeed, chain + "-bindings", m.SeatId), StringComparer.Ordinal).ToArray();
            var binding = new Dictionary<string, string>(StringComparer.Ordinal) { ["target"] = targets[index].PlayerId };
            var tokens = recipe.GetProperty("distinct_other_tokens").EnumerateArray().Select(t => t.GetString()!).ToArray();
            for (var token = 0; token < tokens.Length; token++) { binding[tokens[token]] = others[token].PlayerId; }
            foreach (var family in new[] { "A", "B", "C" })
            {
                var candidates = recipe.GetProperty(family).EnumerateArray().Select(t => binding[t.GetString()!])
                    .OrderBy(id => Digest(roster.ServerSeed, chain + "-display-" + family, id), StringComparer.Ordinal).ToArray();
                clues.Add(new(chain + "-" + family, chain, family, candidates,
                    graph.GetProperty("release_locations").GetProperty(family).GetString()!, releases.GetProperty(family).GetInt32(),
                    roster.CaseId + ":prelude"));
            }
        }
        var state = new SealedScenarioState(Definition.GetProperty("scenario_id").GetString()!, variant, clues.ToArray(),
            players.ToDictionary(p => p.Id, _ => Array.Empty<string>(), StringComparer.Ordinal),
            players.ToDictionary(p => p.Id, _ => new[] { "Přijal jsi scénářovou minulost. Pečeť byla narušena před začátkem tvých tahů." }, StringComparer.Ordinal),
            players.ToDictionary(p => "token-" + members.Single(m => m.PlayerId == p.Id).SeatId,
                p => new SealedItem("token-" + members.Single(m => m.PlayerId == p.Id).SeatId, "Scénářový žeton", p.Id), StringComparer.Ordinal), new(StringComparer.Ordinal));
        state = state with { CaseId = roster.CaseId, Observations = [], Experiences = [], Oversight = [] };
        foreach (var player in players) { player.ObservedPlayers.UnionWith(players.Where(other => other.Id != player.Id && other.Location == player.Location).Select(other => other.Id)); }
        SealedGame.CaptureObservations(state, players.ToDictionary(p => p.Id, StringComparer.Ordinal), 0);
        var game = new SealedGame(players, roster.ServerSeed, adjacency) { Scenario = state };
        return game;
    }
}

internal sealed partial class SealedGame
{
    internal SealedScenarioState? Scenario { get; set; }
    internal static void CaptureObservations(SealedScenarioState state, Dictionary<string, SealedPlayer> players, int chapter)
    {
        if (state.Observations is null) { return; }
        foreach (var observer in players.Values.Where(p => p.State == SealedPlayerState.Awake))
        {
            state.Observations[observer.Id] = players.Values.Where(p => p.Id != observer.Id && p.Location == observer.Location)
                .Select(p => new SealedObservation(p.Id, p.Location,
                    p.FeigningSleep || p.State == SealedPlayerState.Sleeping ? "Zdá se, že spí" : p.State switch
                    { SealedPlayerState.Echo => "Ozvěna", SealedPlayerState.Unconscious => "V bezvědomí", _ => "Bdělý" },
                    chapter, chapter == 0 ? state.CaseId + ":prelude" : state.CaseId + ":chapter-" + chapter + "-commit", state.Oversight?.GetValueOrDefault(chapter) == p.Id ? null : state.Masks.GetValueOrDefault(p.Id))).ToArray();
        }
    }

    private void ResolvePassiveDefense(SealedScenarioState state, Dictionary<string, SealedPlayer> snapshot,
        Dictionary<string, SealedPlayer> working, Dictionary<string, SealedAction> actions,
        Dictionary<string, SealedEvent> events, int chapter)
    {
        foreach (var encounter in events.Values.Where(e => e.TargetId is not null && e.Rolls is not null
            && e.Intent is SealedIntent.Steal or SealedIntent.Stun or SealedIntent.Lethal).ToArray())
        {
            var target = encounter.TargetId!;
            if (snapshot[target].State != SealedPlayerState.Awake || !actions.TryGetValue(target, out var own)) { continue; }
            var source = state.CaseId + ":" + encounter.Id;
            void Experience(string recipient, string text)
            {
                state.Notes[recipient] = [.. state.Notes.GetValueOrDefault(recipient, []), text];
                if (state.Experiences is not null)
                { state.Experiences[recipient] = [.. state.Experiences.GetValueOrDefault(recipient, []), new(source + ":defense:" + recipient, recipient, source, text, "CONFIRMED")]; }
            }
            if (own.DefensePolicy == SealedDefensePolicy.CallForHelp)
            {
                foreach (var witness in snapshot.Values.Where(p => p.State == SealedPlayerState.Awake && p.Location == snapshot[target].Location))
                { Experience(witness.Id, "Postava " + target + " ve společné scéně zavolala o pomoc. Samotné volání nepotvrzuje útočníka ani vinu."); }
            }
            else if (own.DefensePolicy == SealedDefensePolicy.Withdraw && working[target].State == SealedPlayerState.Awake
                && working[target].Location == Players[target].Location && own.Destination is null)
            {
                // A reaction may use the actor's still-unused single adjacent move,
                // only after all simultaneous contacts. It cannot undo a consequence.
                var destination = _adjacency.GetValueOrDefault(working[target].Location, [])
                    .OrderBy(place => SealedScenario.Digest(_seed, "withdraw-" + chapter, target + place), StringComparer.Ordinal).FirstOrDefault();
                if (destination is not null)
                { working[target].Location = destination; working[target].Cover = 0; Experience(target, "Po střetu jsi využil zbývající sousední přesun a ustoupil do místa " + destination + "."); }
            }
            // TAKE_COVER uses only already available Cover in the common snapshot.
            // No policy creates a free shield or changes the accepted main action.
        }
    }

    private static void RecordOversightTrace(SealedScenarioState state, Dictionary<string, SealedPlayer> snapshot,
        Dictionary<string, SealedAction> actions, Dictionary<string, SealedEvent> events, int chapter)
    {
        var overseer = state.Oversight?.GetValueOrDefault(chapter);
        if (overseer is null || !actions.TryGetValue(overseer, out var action)
            || action.Intent is not (SealedIntent.Steal or SealedIntent.Sabotage or SealedIntent.Stun or SealedIntent.Lethal)
            || !events.TryGetValue(overseer, out var result) || result.Phase != 3
            || result.Result is not (SealedResult.Success or SealedResult.Defended)) { return; }
        events[overseer] = result with { TraceCreated = true };
        var location = snapshot[overseer].Location;
        var source = state.CaseId + ":" + result.Id;
        // Only witnesses who actually shared this scene receive this extra trace.
        // It confirms a physical intervention, never faction, intent or hidden target.
        foreach (var witness in snapshot.Values.Where(p => p.State == SealedPlayerState.Awake && p.Location == location))
        {
            var text = "Dohled zanechal ověřitelnou stopu fyzického zásahu postavy " + overseer + " v místě " + location + ". Příslušnost ani vina z ní neplyne.";
            state.Notes[witness.Id] = [.. state.Notes.GetValueOrDefault(witness.Id, []), text];
            if (state.Experiences is not null)
            {
                state.Experiences[witness.Id] = [.. state.Experiences.GetValueOrDefault(witness.Id, []),
                    new(source + ":oversight:" + witness.Id, witness.Id, source, text, "CONFIRMED")];
            }
        }
    }

    private void ResolveScenarioActions(int chapter, Dictionary<string, SealedAction> actions,
        Dictionary<string, SealedPlayer> working, Dictionary<string, SealedPlayer> snapshot,
        Dictionary<string, SealedEvent> events, HashSet<string> denied, SealedScenarioState? state)
    {
        if (state is null) { return; }
        void Note(string actor, string text)
        {
            state.Notes[actor] = [.. state.Notes.GetValueOrDefault(actor, []), text];
            if (state.Experiences is not null)
            {
                var source = state.CaseId + ":" + EventId(_seed, chapter, actor);
                state.Experiences[actor] = [.. state.Experiences.GetValueOrDefault(actor, []),
                    new(source + ":experience-" + SealedScenario.Digest(_seed, "experience", text)[..8], actor, source, text,
                        actions[actor].Intent == SealedIntent.Dream ? "UNCERTAIN" : "CONFIRMED")];
            }
        }
        void Finish(SealedAction a, bool success, string? reason = null, bool trace = false)
            => events[a.ActorId] = Event(a, success ? SealedResult.Success : SealedResult.Hold, 3, reason) with { TraceCreated = trace };
        var ordered = actions.Values.Where(a => !denied.Contains(a.ActorId) && !events.ContainsKey(a.ActorId))
            .OrderBy(a => SealedScenario.Digest(_seed, "scene-priority-" + chapter, a.Id), StringComparer.Ordinal).ToArray();
        foreach (var action in ordered.Where(a => a.Condition is not null))
        {
            var target = action.TargetId is { } targetId ? snapshot.GetValueOrDefault(targetId) : null;
            if (target is null || !Trigger(snapshot[action.ActorId], target, action.Condition!))
            { Finish(action, false, "TRIGGER_NOT_OBSERVED"); }
        }
        ordered = ordered.Where(a => !events.ContainsKey(a.ActorId)).ToArray();
        // Sabotage changes at most one delivery/availability, never the fact or log.
        var sabotaged = new HashSet<string>(StringComparer.Ordinal);
        foreach (var action in ordered.Where(a => a.Intent == SealedIntent.Sabotage))
        {
            var actor = snapshot[action.ActorId];
            var index = Array.FindIndex(state.Clues, c => c.Id == action.ResourceId && c.Location == actor.Location && c.ReleaseChapter <= chapter);
            var valid = index >= 0 && sabotaged.Add(action.ResourceId!) && !snapshot.Values.Any(p => p.Id != actor.Id && p.Location == actor.Location && p.Ward > 0);
            if (valid)
            {
                var clue = state.Clues[index];
                if (action.Method == "REDIRECT" && action.TargetId is { } target && snapshot.TryGetValue(target, out var recipient)
                    && recipient.State == SealedPlayerState.Awake && recipient.Location == actor.Location)
                { state.Clues[index] = clue with { RedirectTo = target }; }
                else if (action.Method is null or "DELAY" && clue.LastDelayedChapter < chapter - 1 && clue.DelayedUntil <= chapter)
                { state.Clues[index] = clue with { DelayedUntil = chapter + 1, LastDelayedChapter = chapter }; }
                else { valid = false; }
            }
            Finish(action, valid, valid ? null : "SABOTAGE_NOT_REACHED", valid);
            if (valid) { Note(actor.Id, "Zasáhl jsi do jediného doručení stopy. Původní důkaz a historie zůstaly zachované."); }
        }
        // Theft claims compete against one snapshot of ownership. One item has
        // exactly one winner, including when its owner simultaneously transfers it.
        var claimed = new HashSet<string>(StringComparer.Ordinal);
        var thefts = new List<(SealedAction Action, SealedItem Item, SealedModifiers Modifiers, SealedRolls Rolls)>();
        foreach (var action in ordered.Where(a => a.Intent == SealedIntent.Steal))
        {
            var actor = snapshot[action.ActorId];
            var item = action.ResourceId is { } resource ? state.Items.GetValueOrDefault(resource) : null;
            var owner = item is null ? null : snapshot.GetValueOrDefault(item.OwnerId);
            if (item is not { Transferable: true } || owner is null || owner.Id == actor.Id || owner.Location != actor.Location || owner.State == SealedPlayerState.Echo)
            { Finish(action, false, "ITEM_NOT_ACQUIRED"); continue; }
            var expertise = actor.Expertise.Contains("STEAL") ? 1 : 0;
            var prepared = actor.Preparation == 1 && actor.PreparationChapter == chapter && (actor.PreparedIntent is null || actor.PreparedIntent == SealedIntent.Steal) ? 1 : 0;
            var attackDie = _dice(EventId(_seed, chapter, actor.Id), "attack"); var defenseDie = _dice(EventId(_seed, chapter, actor.Id), "defense");
            var defenseBase = owner.State == SealedPlayerState.Awake ? 2 : 0;
            thefts.Add((action, item, new(2, expertise, prepared, defenseBase, owner.Ward, owner.Cover),
                new(attackDie, defenseDie, attackDie + 2 + expertise + prepared, defenseDie + defenseBase + owner.Ward + owner.Cover)));
            if (prepared == 1) { working[actor.Id].Preparation = 0; working[actor.Id].PreparedIntent = null; working[actor.Id].PreparationChapter = 0; }
        }
        foreach (var group in thefts.GroupBy(theft => theft.Item.Id, StringComparer.Ordinal))
        {
            var winning = group.Where(theft => theft.Rolls.AttackTotal > theft.Rolls.DefenseTotal)
                .OrderByDescending(theft => theft.Rolls.AttackTotal).ThenBy(theft => SealedScenario.Digest(_seed, "item-tie", theft.Action.Id), StringComparer.Ordinal).ToArray();
            var winner = winning.FirstOrDefault();
            foreach (var theft in group)
            {
                var success = winning.Length > 0 && theft.Action.Id == winner.Action.Id;
                events[theft.Action.ActorId] = new(EventId(_seed, chapter, theft.Action.ActorId), chapter, 3, theft.Action.ActorId,
                    theft.Item.OwnerId, SealedIntent.Steal, success ? SealedResult.Success : SealedResult.Defended, null, theft.Modifiers, theft.Rolls, true, []);
            }
            if (winning.Length > 0) { claimed.Add(winner.Item.Id); state.Items[winner.Item.Id] = winner.Item with { OwnerId = winner.Action.ActorId }; }
        }
        var previouslyKnown = state.KnownClues.Values.SelectMany(ids => ids).ToHashSet(StringComparer.Ordinal);
        var introduced = new HashSet<string>(StringComparer.Ordinal);
        var informationBudget = SealedCaseRules.Conspirators(working.Count);
        foreach (var action in ordered.Where(a => a.Intent != SealedIntent.Sabotage && a.Intent != SealedIntent.Steal))
        {
            var actor = snapshot[action.ActorId];
            switch (action.Intent)
            {
                case SealedIntent.Transfer:
                    var item = action.ResourceId is { } resource ? state.Items.GetValueOrDefault(resource) : null;
                    var recipient = action.TargetId is { } target ? snapshot.GetValueOrDefault(target) : null;
                    var transferable = item is { Transferable: true } && item.OwnerId == actor.Id && !claimed.Contains(item.Id)
                        && recipient is not null && recipient.Id != actor.Id && recipient.Location == actor.Location && recipient.State != SealedPlayerState.Echo;
                    if (transferable) { state.Items[item!.Id] = item with { OwnerId = recipient!.Id }; claimed.Add(item.Id); }
                    Finish(action, transferable, transferable ? null : "TRANSFER_NOT_REACHED"); break;
                case SealedIntent.Follow:
                    var followed = action.TargetId is { } follow ? snapshot.GetValueOrDefault(follow) : null;
                    var reached = followed is not null && followed.Location == actor.Location && actor.ObservedPlayers.Contains(followed.Id);
                    if (reached) { Note(actor.Id, "Při sledování jsi dorazil do místa " + followed!.Location + "."); }
                    Finish(action, reached, reached ? null : "FOLLOW_NOT_OBSERVED"); break;
                case SealedIntent.Mask:
                    state.Masks[actor.Id] = string.IsNullOrWhiteSpace(action.Method) ? "Nenápadný host" : action.Method;
                    Note(actor.Id, "Nasadil jsi masku. Je to tvoje prezentace, nikoli změna pravé identity."); Finish(action, true); break;
                case SealedIntent.Investigate:
                case SealedIntent.EchoScene:
                    var location = action.Intent == SealedIntent.EchoScene && _adjacency.ContainsKey(action.ResourceId ?? "") ? action.ResourceId! : actor.Location;
                    var known = state.KnownClues.GetValueOrDefault(actor.Id, []);
                    var available = state.Clues.Where(c => c.Location == location && c.ReleaseChapter <= chapter && c.DelayedUntil <= chapter
                        && (action.ResourceId is null || action.Intent == SealedIntent.EchoScene || c.Id == action.ResourceId))
                        .Where(c => previouslyKnown.Contains(c.Id) || introduced.Contains(c.Id) || introduced.Count < informationBudget)
                        .OrderBy(c => previouslyKnown.Contains(c.Id) || introduced.Contains(c.Id) ? 1 : 0)
                        .ThenBy(c => known.Contains(c.Id) ? 1 : 0)
                        .ThenBy(c => SealedScenario.Digest(_seed, "clue-" + chapter, c.Id), StringComparer.Ordinal).FirstOrDefault();
                    if (available is not null)
                    {
                        if (!previouslyKnown.Contains(available.Id)) { introduced.Add(available.Id); }
                        var receiver = available.RedirectTo ?? actor.Id;
                        state.KnownClues[receiver] = state.KnownClues.GetValueOrDefault(receiver, []).Append(available.Id).Distinct(StringComparer.Ordinal).ToArray();
                        if (available.RedirectTo is not null)
                        {
                            var clueIndex = Array.FindIndex(state.Clues, c => c.Id == available.Id); state.Clues[clueIndex] = available with { RedirectTo = null };
                        }
                        Note(actor.Id, receiver == actor.Id ? "Prozkoumal jsi doloženou stopu " + available.Id + "." : "Při pátrání došlo k přesměrovanému doručení.");
                    }
                    else { Note(actor.Id, "Prozkoumal jsi místo. Nová doložená stopa tu zatím dostupná není."); }
                    Finish(action, true); break;
                case SealedIntent.Dream:
                    Note(actor.Id, "Ve snu se vrací ozvěna tvých vlastních vzpomínek. Sen nepotvrzuje cizí tajný čin."); Finish(action, true); break;
                case SealedIntent.EchoReview:
                    Note(actor.Id, "Jako Ozvěna jsi znovu prošel vlastní zkušenosti a již získané stopy."); Finish(action, true); break;
                case SealedIntent.EchoDeliver:
                    var deliver = action.TargetId;
                    var owned = state.KnownClues.GetValueOrDefault(actor.Id, []);
                    var canDeliver = deliver is not null && actor.KnownPlayers.Contains(deliver) && action.ResourceId is { } clueId && owned.Contains(clueId);
                    if (canDeliver) { state.KnownClues[deliver!] = state.KnownClues.GetValueOrDefault(deliver!, []).Append(action.ResourceId!).Distinct(StringComparer.Ordinal).ToArray(); }
                    Finish(action, canDeliver, canDeliver ? null : "NO_OWN_ECHO_MESSAGE"); break;
            }
        }
    }
}
