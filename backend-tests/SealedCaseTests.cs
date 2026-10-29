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
        dream_and_echo_actions_require_the_matching_state();
        preparation_is_consumed_or_expires_in_the_next_chapter();
        delay_changes_only_future_access_and_records_own_results();
        guard_blocks_delay_only_in_its_resolved_room_and_chapter();
        competing_delays_have_one_deterministic_winner();
        preparation_for_steal_changes_the_real_modifier();
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
        new SealedAction("sabotage-valid", "A", 1, SealedIntent.Sabotage, Method: "DELAY", ResourceId: "source").ValidateForSubmission();
        new SealedAction("help-valid", "A", 1, SealedIntent.Help, TargetId: "B").ValidateForSubmission();
    }

    private static void Confirmed_orders_replace_and_retry_is_idempotent()
    {
        var book = new SealedActionBook();
        var first = Action("a1", "A", 1, SealedIntent.Rest);
        book.Confirm(first, "A", 10, 20, 0);
        Equal(first, book.Confirm(first, "A", 21, 20, 0));
        var replacement = Action("a2", "A", 1, SealedIntent.Prepare, revision: 2, preparedIntent: SealedIntent.Steal);
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

    private static void delay_changes_only_future_access_and_records_own_results()
    {
        var clue = new SealedClue("source-A", "chain-A", "A", ["A"], "SALON", 1, "case:prelude");
        var ids = new[] { "A", "B", "C", "D", "E", "F", "G", "H" };
        var players = ids.Select(id => new SealedPlayer(id, "SALON", knownPlayers: ids.Where(other => other != id))).ToArray();
        var initialCopies = new Dictionary<string, string[]>(StringComparer.Ordinal) { ["C"] = [clue.Id] };
        var scenario = new SealedScenarioState("POD_PECETI_POSLEDNI_ZVON_V1", "first", [clue], initialCopies,
            new(StringComparer.Ordinal), new(StringComparer.Ordinal), new(StringComparer.Ordinal),
            new(StringComparer.Ordinal), new(StringComparer.Ordinal), "case", []);
        var game = new SealedGame(players, "delay-seed") { Scenario = scenario };

        game.Resolve(1,
        [
            new("delay-1", "A", 1, SealedIntent.Sabotage, Method: "DELAY", ResourceId: clue.Id),
            new("search-1", "B", 1, SealedIntent.Investigate)
        ]);
        var afterFirst = game.Scenario!;
        Equal(2, afterFirst.Clues.Single().DelayedUntil);
        Equal(true, afterFirst.KnownClues.GetValueOrDefault("C", []).Contains(clue.Id));
        Equal(false, afterFirst.KnownClues.GetValueOrDefault("B", []).Contains(clue.Id));
        var firstExperience = afterFirst.Experiences!["A"].Single(experience => experience.ActionRef == "delay-1");
        Equal("DELAY_SUCCEEDED", firstExperience.ActionObservation);
        Equal(1, firstExperience.ActionChapter);
        var checkpoint = JsonSerializer.Deserialize<SealedGameCheckpoint>(JsonSerializer.Serialize(game.Checkpoint(), new JsonSerializerOptions(JsonSerializerDefaults.Web)), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        game = SealedGame.Restore(checkpoint);

        game.Resolve(2,
        [
            new("delay-2", "A", 2, SealedIntent.Sabotage, Method: "DELAY", ResourceId: clue.Id),
            new("search-2", "B", 2, SealedIntent.Investigate)
        ]);
        var afterSecond = game.Scenario!;
        Equal("DELAY_NOT_OBSERVED", afterSecond.Experiences!["A"].Single(experience => experience.ActionRef == "delay-2").ActionObservation);
        Equal(true, afterSecond.KnownClues.GetValueOrDefault("B", []).Contains(clue.Id));
        Equal(1, afterSecond.Experiences["A"].Single(experience => experience.ActionRef == "delay-1").ActionChapter);

        game.Resolve(3,
        [
            new("delay-3", "A", 3, SealedIntent.Sabotage, Method: "DELAY", ResourceId: clue.Id),
            new("search-3", "D", 3, SealedIntent.Investigate)
        ]);
        var final = game.Scenario!;
        Equal(4, final.Clues.Single().DelayedUntil);
        Equal(false, final.KnownClues.GetValueOrDefault("D", []).Contains(clue.Id));
        Equal("DELAY_SUCCEEDED", final.Experiences!["A"].Single(experience => experience.ActionRef == "delay-3").ActionObservation);
        Equal(clue.Chain, final.Clues.Single().Chain);
        Equal(clue.SourceEvent, final.Clues.Single().SourceEvent);
        Equal(string.Join(',', clue.Candidates), string.Join(',', final.Clues.Single().Candidates));
        Equal(true, final.Notes["A"].Last().Contains("do závěrečného hlasování", StringComparison.Ordinal));
    }

    private static void guard_blocks_delay_only_in_its_resolved_room_and_chapter()
    {
        foreach (var arrives in new[] { true, false })
        {
            var game = DelayGame(arrives ? "ARCHIV" : "SALON");
            var results = game.Resolve(1,
            [
                new("delay", "A", 1, SealedIntent.Sabotage, Method: "DELAY", ResourceId: "source"),
                new("search", "B", 1, SealedIntent.Investigate),
                new("guard", "G", 1, SealedIntent.Guard, Destination: arrives ? "SALON" : "ARCHIV")
            ]);
            Equal(arrives ? SealedResult.Hold : SealedResult.Success, results.Single(item => item.ActorId == "A").Result);
            Equal(arrives, game.Scenario!.KnownClues.GetValueOrDefault("B", []).Contains("source"));
            Equal(arrives ? 0 : 2, game.Scenario.Clues.Single().DelayedUntil);
            Equal(1, game.Players["G"].Ward);
            Equal(0, game.Scenario.Experiences!.GetValueOrDefault("G", []).Count(item => item.ActionObservation is not null));
            Equal(arrives ? "DELAY_NOT_OBSERVED" : "DELAY_SUCCEEDED",
                game.Scenario.Experiences!["A"].Single(item => item.ActionRef == "delay").ActionObservation);
            if (arrives)
            {
                game.Resolve(2, [new("delay-next", "A", 2, SealedIntent.Sabotage, Method: "DELAY", ResourceId: "source")]);
                Equal(0, game.Players["G"].Ward);
                Equal(3, game.Scenario.Clues.Single().DelayedUntil);
            }
        }
    }

    private static void competing_delays_have_one_deterministic_winner()
    {
        string? winner = null;
        foreach (var reverse in new[] { false, true })
        {
            var game = DelayGame("ARCHIV");
            SealedAction[] actions =
            [
                new("delay-A", "A", 1, SealedIntent.Sabotage, Method: "DELAY", ResourceId: "source"),
                new("delay-C", "C", 1, SealedIntent.Sabotage, Method: "DELAY", ResourceId: "source"),
                new("search", "B", 1, SealedIntent.Investigate)
            ];
            var results = game.Resolve(1, reverse ? actions.Reverse().ToArray() : actions);
            var delays = results.Where(item => item.Intent == SealedIntent.Sabotage).ToArray();
            Equal(1, delays.Count(item => item.Result == SealedResult.Success));
            var resolvedWinner = delays.Single(item => item.Result == SealedResult.Success).ActorId;
            winner ??= resolvedWinner;
            Equal(winner, resolvedWinner);
            Equal(2, game.Scenario!.Clues.Single().DelayedUntil);
            Equal(false, game.Scenario.KnownClues.GetValueOrDefault("B", []).Contains("source"));
            Equal(1, game.Scenario.Experiences!.Values.SelectMany(items => items)
                .Count(item => item.ActionObservation == "DELAY_SUCCEEDED"));
            Equal(1, game.Scenario.Experiences.Values.SelectMany(items => items)
                .Count(item => item.ActionObservation == "DELAY_NOT_OBSERVED"));
        }
    }

    private static SealedGame DelayGame(string guardLocation)
    {
        var ids = new[] { "A", "B", "C", "D", "E", "F", "G", "H" };
        var players = ids.Select(id => new SealedPlayer(id, id == "G" ? guardLocation : "SALON",
            knownPlayers: ids.Where(other => other != id))).ToArray();
        var state = new SealedScenarioState("POD_PECETI_POSLEDNI_ZVON_V1", "first",
            [new("source", "chain", "A", ["A"], "SALON", 1, "case:prelude")],
            new(StringComparer.Ordinal), new(StringComparer.Ordinal), new(StringComparer.Ordinal), new(StringComparer.Ordinal),
            new(StringComparer.Ordinal), new(StringComparer.Ordinal), "case", []);
        return new SealedGame(players, "guard-and-collision-seed") { Scenario = state };
    }

    private static void preparation_for_steal_changes_the_real_modifier()
    {
        var ids = new[] { "A", "B", "C", "D", "E", "F", "G", "H" };
        var players = ids.Select(id => new SealedPlayer(id, "SALON", knownPlayers: ids.Where(other => other != id))).ToArray();
        var state = new SealedScenarioState("POD_PECETI_POSLEDNI_ZVON_V1", "full", [],
            new(StringComparer.Ordinal), new(StringComparer.Ordinal),
            new(StringComparer.Ordinal) { ["token"] = new SealedItem("token", "token", "B") },
            new(StringComparer.Ordinal));
        var game = new SealedGame(players, "prepared-steal-seed") { Scenario = state };
        game.Resolve(1, [new("prepare-steal", "A", 1, SealedIntent.Prepare, PreparedIntent: SealedIntent.Steal)]);
        var steal = game.Resolve(2, [new("steal", "A", 2, SealedIntent.Steal, ResourceId: "token")]).Single();
        Equal(1, steal.Modifiers!.Preparation);
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
