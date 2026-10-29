using System.Text.Json;
using Kufr.Backend.Features.SealedCase;

internal static class SealedCaseTests
{
    internal static void Run()
    {
        Confirmed_orders_replace_and_retry_is_idempotent();
        simultaneous_contacts_use_one_snapshot_and_seeded_rolls();
        feigned_sleep_triggers_only_as_observed_and_defends_as_awake();
        lethal_token_is_consumed_only_on_reached_contest();
        projections_never_leak_other_player_or_world_secrets();
        presentation_is_not_world_truth_or_belief();
        help_has_no_attack_roll_or_trace();
        simultaneous_lethal_dominates_stun_independent_of_actor_order();
        repeated_stun_does_not_extend_unconsciousness();
        received_presentation_does_not_reveal_other_recipients();
        undefined_enums_and_phase_two_conditions_are_rejected();
        required_action_parameters_are_rejected_when_missing();
        investigation_feedback_uses_actor_observation_deltas();
        dream_and_echo_actions_require_the_matching_state();
        preparation_is_consumed_or_expires_in_the_next_chapter();
        own_presentation_tracks_delivery_without_belief();
        perceived_result_references_point_to_own_experiences();
        simultaneous_help_cannot_resurrect_a_lethal_target();
        state_gate_prevents_movement_before_rejection();
        help_survives_a_defended_attack_but_not_a_successful_stun();
        preparation_modifier_matches_the_declared_intent_or_rest();
    }

    private static void required_action_parameters_are_rejected_when_missing()
    {
        var legacySabotage = new SealedAction("sabotage", "A", 1, SealedIntent.Sabotage);
        var legacyHelp = new SealedAction("help", "A", 1, SealedIntent.Help);
        var selfHelp = new SealedAction("help-self", "A", 1, SealedIntent.Help, TargetId: "A");
        legacySabotage.Validate(); legacyHelp.Validate(); selfHelp.Validate();
        Throws<SealedCaseRuleException>(legacySabotage.ValidateForSubmission);
        Throws<SealedCaseRuleException>(legacyHelp.ValidateForSubmission);
        Throws<SealedCaseRuleException>(selfHelp.ValidateForSubmission);
        Throws<SealedCaseRuleException>(() => new SealedActionBook().Confirm(legacySabotage, "A", 1, 2, 0));
        new SealedAction("sabotage-valid", "A", 1, SealedIntent.Sabotage, ResourceId: "source").ValidateForSubmission();
        new SealedAction("help-valid", "A", 1, SealedIntent.Help, TargetId: "B").ValidateForSubmission();
    }

    private static void investigation_feedback_uses_actor_observation_deltas()
    {
        static SealedScenarioState Snapshot(string[] known, string[] notes) => new("scenario", "full", [],
            new(StringComparer.Ordinal) { ["A"] = known }, new(StringComparer.Ordinal) { ["A"] = notes },
            new(StringComparer.Ordinal), new(StringComparer.Ordinal));

        const string discovered = "Prozkoumal jsi doloženou stopu clue-1.";
        Equal("Znovu jsi narazil na již známou stopu.", SealedCaseFeedback.InvestigationObservation(
            SealedIntent.Investigate, Snapshot(["clue-1"], []), Snapshot(["clue-1"], [discovered]), "A"));
        Equal("Získal jsi novou doloženou stopu.", SealedCaseFeedback.InvestigationObservation(
            SealedIntent.Investigate, Snapshot([], []), Snapshot(["clue-1"], [discovered]), "A"));
        Equal("Na tomto místě nebyla nová doložená stopa dostupná.", SealedCaseFeedback.InvestigationObservation(
            SealedIntent.Investigate, Snapshot([], []), Snapshot(["delivered-by-peer"],
                ["Prozkoumal jsi místo. Nová doložená stopa tu zatím dostupná není."]), "A"));
        Equal<string?>(null, SealedCaseFeedback.InvestigationObservation(SealedIntent.Investigate,
            Snapshot([], []), Snapshot(["clue-1"], ["Při pátrání došlo k přesměrovanému doručení."]), "A"));
    }

    private static void Confirmed_orders_replace_and_retry_is_idempotent()
    {
        var book = new SealedActionBook();
        var first = Action("a1", "A", 1, SealedIntent.Rest);
        book.Confirm(first, "A", 10, 20, 0);
        Equal(first, book.Confirm(first, "A", 21, 20, 0));
        var replacement = Action("a2", "A", 1, SealedIntent.Prepare, revision: 2, preparedIntent: SealedIntent.Investigate);
        book.Confirm(replacement, "A", 19, 20, 1);
        Equal(1, book.Orders.Count);
        Equal(replacement, book.Orders[("A", 1)]);
        Throws<SealedCaseRuleException>(() => book.Confirm(Action("b", "B", 1, SealedIntent.Hold), "A", 1, 20, 0));
    }

    private static void simultaneous_contacts_use_one_snapshot_and_seeded_rolls()
    {
        var game = NewGame(dice: (_, side) => side == "attack" ? 6 : 1);
        var attacks = new[] { Lethal("A", "B", "ab"), Lethal("B", "A", "ba") };
        var results = game.Resolve(2, attacks);
        Equal(2, results.Count(x => x.Result == SealedResult.Success));
        Equal(SealedPlayerState.Echo, game.Players["A"].State);
        Equal(SealedPlayerState.Echo, game.Players["B"].State);
        var replay = game.Resolve(2, attacks.Reverse().ToArray());
        Equal(JsonSerializer.Serialize(results), JsonSerializer.Serialize(replay));
    }

    private static void feigned_sleep_triggers_only_as_observed_and_defends_as_awake()
    {
        var game = NewGame(dice: (_, side) => side == "attack" ? 3 : 3);
        var result = game.Resolve(2, [
            Lethal("A", "B", "attack", new(SealedConditionKind.TargetAppearsAsleep)),
            Action("pose", "B", 2, SealedIntent.FeignSleep)
        ]).Single(x => x.ActorId == "A");
        Equal(SealedResult.Defended, result.Result);
        Equal(2, result.Modifiers!.DefenseBase);
        Equal(SealedPlayerState.Awake, game.Players["B"].State);
        var blind = NewGame(observes: false);
        var failed = blind.Resolve(2, [Lethal("A", "B", "blind", new(SealedConditionKind.TargetAppearsAsleep)), Action("pose", "B", 2, SealedIntent.FeignSleep)]).Single(x => x.ActorId == "A");
        Equal(SealedResult.Hold, failed.Result);
        Equal(1, blind.Players["A"].ExtremeToken);
    }

    private static void lethal_token_is_consumed_only_on_reached_contest()
    {
        var blocked = NewGame();
        var noAccess = Lethal("A", "B", "blocked") with { Destination = "ATTIC" };
        blocked.Resolve(2, [noAccess]);
        Equal(1, blocked.Players["A"].ExtremeToken);
        var reached = NewGame(dice: (_, side) => side == "attack" ? 1 : 6);
        reached.Resolve(2, [Lethal("A", "B", "reached")]);
        Equal(0, reached.Players["A"].ExtremeToken);
    }

    private static void projections_never_leak_other_player_or_world_secrets()
    {
        var log = new SealedWorldLog();
        log.Append(new SealedWorldEvent("secret-event", 2, "A", "B", "LETHAL", "SUCCESS", new Dictionary<string,string>{{"team","CONSPIRATORS"}}, "roll-secret"));
        var projection = SealedProjection.ForPlayer("B", log,
            [new("seen", "B", null, "Záblesk.", "OBSERVED")],
            [new("private-A", "A", "Rest", SealedAudience.Players(["A"]), ["A"], false)],
            [new("order-A", "A", "LETHAL", "B", true, ["secret-event"])], false);
        var json = JsonSerializer.Serialize(projection);
        DoesNotContain("CONSPIRATORS", json);
        DoesNotContain("roll-secret", json);
        DoesNotContain("order-A", json);
        DoesNotContain("secret-event", json);
        Equal(0, projection.WorldTruth.EventRefs.Count);
        Equal(0, projection.CovertActions.Count);
    }

    private static void presentation_is_not_world_truth_or_belief()
    {
        var speech = new SealedPresentation("claim", "A", "Jdu spát.", SealedAudience.Public, [], false);
        Equal(SealedPresentationKind.Statement, speech.Kind);
        var view = SealedProjection.PresentationForPlayer([speech], "B");
        Equal(0, view.Count);
        var delivered = speech with { Audience = SealedAudience.Players(["B"]), DeliveryReceipts = ["B"], Published = false };
        var received = SealedProjection.PresentationForPlayer([delivered], "B");
        Equal(1, received.Count);
        Equal(SealedPresentationKind.Statement, received[0].Kind);
        DoesNotContain("believed", JsonSerializer.Serialize(received));
    }

    private static void help_has_no_attack_roll_or_trace()
    {
        foreach (var attackRoll in new[] { 1, 6 })
        {
            var game = NewGame(dice: (_, side) => side == "attack" ? attackRoll : 7 - attackRoll);
            game.Players["B"].State = SealedPlayerState.Unconscious;
            game.Players["B"].UnconsciousUntilEnd = 3;
            var result = game.Resolve(2, [new("help", "A", 2, SealedIntent.Help, "B")]).Single();
            Equal(SealedPlayerState.Awake, game.Players["B"].State);
            Equal(SealedResult.Success, result.Result); Equal<SealedRolls?>(null, result.Rolls);
            Equal<SealedModifiers?>(null, result.Modifiers); Equal(false, result.TraceCreated);
        }
    }

    private static void simultaneous_lethal_dominates_stun_independent_of_actor_order()
    {
        foreach (var lethalActor in new[] { "A", "C" })
        {
            var game = new SealedGame([new("A", "HALL", knownPlayers: ["B"]), new("B", "HALL"), new("C", "HALL", knownPlayers: ["B"])], "seed", dice: (_, side) => side == "attack" ? 6 : 1);
            var stunActor = lethalActor == "A" ? "C" : "A";
            game.Resolve(2, [Lethal(lethalActor, "B", "lethal"), new("stun", stunActor, 2, SealedIntent.Stun, "B", Method: "IMPACT_SEAL")]);
            Equal(SealedPlayerState.Echo, game.Players["B"].State);
        }
    }

    private static void repeated_stun_does_not_extend_unconsciousness()
    {
        var game = NewGame(dice: (_, side) => side == "attack" ? 6 : 1);
        game.Players["B"].State = SealedPlayerState.Unconscious;
        game.Players["B"].UnconsciousUntilEnd = 3;
        game.Resolve(3, [new("stun", "A", 3, SealedIntent.Stun, "B", Method: "IMPACT_SEAL")]);
        Equal(SealedPlayerState.Awake, game.Players["B"].State);
    }

    private static void received_presentation_does_not_reveal_other_recipients()
    {
        var message = new SealedPresentation("message", "A", "Výpověď", SealedAudience.Players(["B", "C"]), ["B"], false);
        var viewer = SealedProjection.ForPlayer("B", new(), [], [message], [], false);
        DoesNotContain("\"C\"", JsonSerializer.Serialize(viewer));
        var author = SealedProjection.ForPlayer("A", new(), [], [message], [], false);
        Equal(2, author.Presentations.Single().Recipients.Count);
    }

    private static void undefined_enums_and_phase_two_conditions_are_rejected()
    {
        foreach (var value in new[] { -1, 999 })
        {
            Throws<SealedCaseRuleException>(() => new SealedAction("invalid", "A", 1, (SealedIntent)value).Validate());
            Throws<SealedCaseRuleException>(() => new SealedAction("invalid", "A", 1, SealedIntent.Prepare, PreparedIntent: (SealedIntent)value).Validate());
            Throws<SealedCaseRuleException>(() => new SealedAction("invalid", "A", 1, SealedIntent.Stun, Condition: new((SealedConditionKind)value)).Validate());
        }
        foreach (var intent in new[] { SealedIntent.Rest, SealedIntent.Guard, SealedIntent.Prepare, SealedIntent.FeignSleep })
            Throws<SealedCaseRuleException>(() => new SealedAction("conditional", "A", 2, intent, "B", Condition: new(SealedConditionKind.TargetAppearsAsleep), PreparedIntent: intent == SealedIntent.Prepare ? SealedIntent.Stun : null).Validate());
    }

    private static void dream_and_echo_actions_require_the_matching_state()
    {
        foreach (var intent in new[] { SealedIntent.Dream, SealedIntent.EchoReview, SealedIntent.EchoDeliver, SealedIntent.EchoScene })
        {
            var game = NewGame();
            Equal(SealedResult.Hold, game.Resolve(2, [new("invalid", "A", 2, intent)]).Single().Result);
        }
    }

    private static void preparation_is_consumed_or_expires_in_the_next_chapter()
    {
        foreach (var useBonus in new[] { true, false })
        {
            var game = NewGame();
            game.Resolve(1, [new("prepare", "A", 1, SealedIntent.Prepare, PreparedIntent: SealedIntent.Stun)]);
            Equal(1, game.Players["A"].Preparation);
            game.Resolve(2, useBonus ? [new("use", "A", 2, SealedIntent.Stun, "B", Method: "IMPACT_SEAL")] : []);
            Equal(0, game.Players["A"].Preparation); Equal<SealedIntent?>(null, game.Players["A"].PreparedIntent);
        }
    }

    private static void own_presentation_tracks_delivery_without_belief()
    {
        var own = new SealedPresentation("message", "A", "Jdu spát.", SealedAudience.Players(["B", "C"]), ["B"], false);
        var projection = SealedProjection.ForPlayer("A", new(), [], [own], [], false).Presentations.Single();
        Equal(false, projection.Published); Equal("B", projection.DeliveryReceipts.Single());
        var received = SealedProjection.ForPlayer("B", new(), [], [own], [], false).Presentations.Single();
        Equal(0, received.DeliveryReceipts.Count);
        var published = own with { Published = true, Audience = SealedAudience.Public, DeliveryReceipts = ["B", "C"] };
        Equal(true, SealedProjection.ForPlayer("A", new(), [], [published], [], false).Presentations.Single().Published);
        var recipient = SealedProjection.ForPlayer("B", new(), [], [published], [], false);
        DoesNotContain("\"C\"", JsonSerializer.Serialize(recipient));
    }

    private static void perceived_result_references_point_to_own_experiences()
    {
        var world = new SealedWorldLog();
        world.Append(new("event", 2, "A", "B", "STUN", "SUCCESS", new Dictionary<string,string>(), "secret"));
        SealedExperience[] experiences = [new("experience", "A", "event", "Vidíš záblesk.", "OBSERVED"), new("unrelated", "A", "another-event", "Jiná scéna.", "OBSERVED"), new("other-viewer", "B", "event", "Cizí prožitek.", "OBSERVED")];
        // Causal knowledge is explicitly granted by the trusted observer/action
        // writer; sharing an internal source event alone is not authorization.
        var own = SealedProjection.ForPlayer("A", world, experiences, [], [new("action", "A", "STUN", "B", true, ["event"]) { AuthorizedResultRefs = ["experience", "other-viewer"] }], false);
        Equal("experience", own.CovertActions.Single().PerceivedResultRefs.Single());
    }

    private static void simultaneous_help_cannot_resurrect_a_lethal_target()
    {
        foreach (var helperId in new[] { "A", "C" })
        {
            var game = new SealedGame([new("A", "SALON", knownPlayers: ["B"]), new("B", "SALON", state: SealedPlayerState.Unconscious), new("C", "SALON", knownPlayers: ["B"])], "seed", dice: (_, side) => side == "attack" ? 6 : 1);
            game.Players["B"].UnconsciousUntilEnd = 3;
            game.Resolve(2, [new("help", helperId, 2, SealedIntent.Help, "B"), Lethal(helperId == "A" ? "C" : "A", "B", "lethal")]);
            Equal(SealedPlayerState.Echo, game.Players["B"].State);
        }
    }

    private static void state_gate_prevents_movement_before_rejection()
    {
        foreach (var state in Enum.GetValues<SealedPlayerState>())
        foreach (var intent in new[] { SealedIntent.Dream, SealedIntent.EchoReview, SealedIntent.EchoDeliver, SealedIntent.EchoScene })
        {
            var game = NewGame();
            var actor = game.Players["A"];
            actor.State = state; actor.UnconsciousUntilEnd = 3; actor.SleepingUntilStart = 3;
            var validState = intent == SealedIntent.Dream ? state == SealedPlayerState.Unconscious : state == SealedPlayerState.Echo;
            var result = game.Resolve(2, [new("request", "A", 2, intent)]).Single();
            Equal(validState ? SealedResult.RequestValid : SealedResult.Hold, result.Result);
        }
        var moving = NewGame();
        moving.Resolve(2, [new("invalid-move", "A", 2, SealedIntent.Dream, Destination: "ARCHIVE")]);
        Equal("HALL", moving.Players["A"].Location);
        foreach (var state in new[] { SealedPlayerState.Unconscious, SealedPlayerState.Echo })
        {
            var physical = NewGame();
            physical.Players["A"].State = state; physical.Players["A"].UnconsciousUntilEnd = 3;
            Equal(SealedResult.Hold, physical.Resolve(2, [new("physical", "A", 2, SealedIntent.Stun, "B", Destination: "ARCHIVE", Method: "IMPACT_SEAL")]).Single().Result);
            Equal("HALL", physical.Players["A"].Location);
        }
    }

    private static void help_survives_a_defended_attack_but_not_a_successful_stun()
    {
        foreach (var intent in new[] { SealedIntent.Stun, SealedIntent.Lethal })
        foreach (var succeeds in new[] { false, true })
        {
            var game = new SealedGame([new("A", "SALON", knownPlayers: ["B"]), new("B", "SALON"), new("C", "SALON", knownPlayers: ["B"])], "seed", dice: (_, side) => side == "attack" ? succeeds ? 6 : 1 : succeeds ? 1 : 6);
            game.Players["B"].State = SealedPlayerState.Unconscious; game.Players["B"].UnconsciousUntilEnd = 3;
            var attack = intent == SealedIntent.Lethal ? Lethal("C", "B", "attack") : new SealedAction("attack", "C", 2, SealedIntent.Stun, "B", Method: "IMPACT_SEAL");
            game.Resolve(2, [new("help", "A", 2, SealedIntent.Help, "B"), attack]);
            Equal(!succeeds ? SealedPlayerState.Awake : intent == SealedIntent.Lethal ? SealedPlayerState.Echo : SealedPlayerState.Unconscious, game.Players["B"].State);
        }
    }

    private static void preparation_modifier_matches_the_declared_intent_or_rest()
    {
        foreach (var rest in new[] { false, true })
        foreach (var attack in new[] { SealedIntent.Stun, SealedIntent.Lethal })
        {
            var game = NewGame();
            game.Resolve(1, [new("bonus", "A", 1, rest ? SealedIntent.Rest : SealedIntent.Prepare, PreparedIntent: rest ? null : SealedIntent.Stun)]);
            var action = attack == SealedIntent.Lethal ? Lethal("A", "B", "use") : new SealedAction("use", "A", 2, SealedIntent.Stun, "B", Method: "IMPACT_SEAL");
            var result = game.Resolve(2, [action]).Single();
            Equal(rest || attack == SealedIntent.Stun ? 1 : 0, result.Modifiers!.Preparation);
            Equal(0, game.Players["A"].PreparationChapter);
        }
    }

    private static SealedGame NewGame(bool observes = true, Func<string,string,int>? dice = null) => new(
        [new("A", "HALL", knownPlayers: ["B"], observedPlayers: observes ? ["B"] : []),
         new("B", "HALL", knownPlayers: ["A"], observedPlayers: ["A"])],
        "seed", new Dictionary<string, string[]> { ["HALL"] = ["ARCHIVE"], ["ARCHIVE"] = ["HALL"] }, dice);
    private static SealedAction Action(string id, string actor, int chapter, SealedIntent intent, int revision = 1, SealedIntent? preparedIntent = null) => new(id, actor, chapter, intent, Revision: revision, PreparedIntent: preparedIntent);
    private static SealedAction Lethal(string actor, string target, string id, SealedCondition? condition = null) => new(id, actor, 2, SealedIntent.Lethal, target, Method: "BREAKING_SEAL", Condition: condition, LethalConfirmed: true);
    private static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"SealedCase: expected {expected}, got {actual}"); }
    private static void Throws<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new InvalidOperationException($"SealedCase: expected {typeof(T).Name}"); }
    private static void DoesNotContain(string value, string text) { if (text.Contains(value, StringComparison.Ordinal)) throw new InvalidOperationException($"SealedCase: leaked {value}"); }
}
