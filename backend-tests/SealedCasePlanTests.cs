using Kufr.Backend.Features.SealedCase;
using Microsoft.Data.Sqlite;

internal static class SealedCasePlanTests
{
    internal static void Run()
    {
        ExplicitConfirmationRevisionAndResolutionSurviveRestart();
        ShortGamePlanPolicyIsEnforcedByTheServer();
        FullGameKeepsAnImplementedPreparation();
        CompetingRevisionsAcceptOnePlan();
        OverdueChaptersResolveInOrderFromCanonicalPlans();
        DeadlineCrossedWhileWaitingForWriterRejectsConfirmation();
        Console.WriteLine("PASS explicit durable plan confirmation/revision, deadline, authorized targets, replay and canonical resolution.");
    }

    private static void ShortGamePlanPolicyIsEnforcedByTheServer()
    {
        using var fixture = new SealedCaseTimingTests.Fixture("first", 8);
        var start = fixture.Start();
        var shortGame = SealedScenario.Create(fixture.Store.ReadRoster(fixture.Id, "group"), "first");
        var actor = shortGame.Players["p1"];
        var visibleSource = shortGame.Scenario!.Clues[0] with { Location = actor.Location, ReleaseChapter = 1 };
        shortGame.Scenario = shortGame.Scenario with { Clues = [visibleSource, .. shortGame.Scenario.Clues.Skip(1)] };
        fixture.Store.InitializeWorld(fixture.Id, "group", start.Revision, 1000,
            shortGame);
        fixture.Clock.Now = start.Timing.IntroductionEndsAt;
        var revision = fixture.Read().Revision;
        Denied(() => fixture.Store.ConfirmPlan(fixture.Id, "group", "p1", "short-full-action", revision,
            new(1, 0, true, SealedIntent.Help, "p2")), "sealed_case_action_not_allowed");
        Denied(() => fixture.Store.ConfirmPlan(fixture.Id, "group", "p1", "short-redirect", revision,
            new(1, 0, true, SealedIntent.Sabotage, "p2", Method: "REDIRECT", ResourceId: "source-1")), "sealed_case_action_not_allowed");
        Denied(() => fixture.Store.ConfirmPlan(fixture.Id, "group", "p1", "short-guard-target", revision,
            new(1, 0, true, SealedIntent.Guard, "p2")), "sealed_case_action_not_allowed");
        Denied(() => fixture.Store.ConfirmPlan(fixture.Id, "group", "p1", "short-prepare-search", revision,
            new(1, 0, true, SealedIntent.Prepare, PreparedIntent: SealedIntent.Investigate)), "sealed_case_invalid_plan");
        Denied(() => fixture.Store.ConfirmPlan(fixture.Id, "group", "p1", "guessed-sabotage-source", revision,
            new(1, 0, true, SealedIntent.Sabotage, Method: "DELAY", ResourceId: "not-in-player-view")), "sealed_case_source_unavailable");
        var currentWorld = fixture.Store.ReadWorld(fixture.Id, "group").Checkpoint;
        var departure = currentWorld.Adjacency[currentWorld.Players.Single(player => player.Id == "p1").Location][0];
        Denied(() => fixture.Store.ConfirmPlan(fixture.Id, "group", "p1", "delay-while-leaving-source", revision,
            new(1, 0, true, SealedIntent.Sabotage, Destination: departure, Method: "DELAY", ResourceId: visibleSource.Id)),
            "sealed_case_source_unavailable");
        var selfGuard = fixture.Store.ConfirmPlan(fixture.Id, "group", "p1", "short-self-guard", revision,
            new(1, 0, true, SealedIntent.Guard));
        Equal(SealedIntent.Guard, fixture.Store.ReadOwnPlan(fixture.Id, "group", "p1", 1).Action!.Intent);
        Denied(() => fixture.Store.ConfirmPlan(fixture.Id, "group", "p2", "short-redirect-after-guard", selfGuard.Revision,
            new(1, 0, true, SealedIntent.Sabotage, "p3", Method: "REDIRECT", ResourceId: "source-1")), "sealed_case_action_not_allowed");

        foreach (var expiredState in new[] { SealedPlayerState.Sleeping, SealedPlayerState.Unconscious })
        {
            using var expiredFixture = new SealedCaseTimingTests.Fixture("first", 8);
            var expiredStart = expiredFixture.Start();
            var game = SealedScenario.Create(expiredFixture.Store.ReadRoster(expiredFixture.Id, "group"), "first");
            var expiredActor = game.Players["p1"];
            expiredActor.State = expiredState;
            expiredActor.SleepingUntilStart = 1;
            expiredActor.UnconsciousUntilEnd = 0;
            expiredFixture.Store.InitializeWorld(expiredFixture.Id, "group", expiredStart.Revision, 1000, game);
            expiredFixture.Clock.Now = expiredStart.Timing.IntroductionEndsAt;
            Denied(() => expiredFixture.Store.ConfirmPlan(expiredFixture.Id, "group", "p1", "expired-state-" + expiredState,
                expiredFixture.Read().Revision, new(1, 0, true, SealedIntent.Prepare, PreparedIntent: SealedIntent.Stun)),
                "sealed_case_action_not_allowed");
        }
    }

    private static void FullGameKeepsAnImplementedPreparation()
    {
        using var fixture = new SealedCaseTimingTests.Fixture("full", 8);
        var start = fixture.Start();
        fixture.Store.InitializeWorld(fixture.Id, "group", start.Revision, 1000, World(fixture));
        fixture.Clock.Now = start.Timing.IntroductionEndsAt;
        var revision = fixture.Read().Revision;
        fixture.Store.ConfirmPlan(fixture.Id, "group", "p1", "full-prepare-steal", revision,
            new(1, 0, true, SealedIntent.Prepare, PreparedIntent: SealedIntent.Steal));
        Equal(SealedIntent.Steal, fixture.Store.ReadOwnPlan(fixture.Id, "group", "p1", 1).Action!.PreparedIntent);
    }

    private static void ExplicitConfirmationRevisionAndResolutionSurviveRestart()
    {
        using var fixture = new SealedCaseTimingTests.Fixture("full", 8);
        var start = fixture.Start();
        fixture.Store.InitializeWorld(fixture.Id, "group", start.Revision, 1000, World(fixture));
        fixture.Clock.Now = start.Timing.IntroductionEndsAt;
        var revision = fixture.Read().Revision;
        Denied(() => fixture.Store.ConfirmPlan(fixture.Id, "group", "p1", "proposal-only", revision,
            new(1, 0, false, SealedIntent.Rest)), "sealed_case_confirmation_required");
        Equal(revision, fixture.Read().Revision);
        Equal<SealedAction?>(null, fixture.Store.ReadOwnPlan(fixture.Id, "group", "p1", 1).Action);
        Denied(() => fixture.Store.ConfirmPlan(fixture.Id, "group", "outsider", "foreign-plan", revision,
            new(1, 0, true, SealedIntent.Hold)), "case_not_found");
        Denied(() => fixture.Store.ConfirmPlan(fixture.Id, "group", "p1", "unknown-target", revision,
            new(1, 0, true, SealedIntent.Guard, "hidden-unknown")), "sealed_case_invalid_target");
        Denied(() => fixture.Store.ConfirmPlan(fixture.Id, "group", "p1", "missing-sabotage-source", revision,
            new(1, 0, true, SealedIntent.Sabotage)), "sealed_case_source_required");
        Denied(() => fixture.Store.ConfirmPlan(fixture.Id, "group", "p1", "invalid-sabotage-method", revision,
            new(1, 0, true, SealedIntent.Sabotage, Method: "UNKNOWN", ResourceId: "source-1")), "sealed_case_invalid_sabotage_method");
        Denied(() => fixture.Store.ConfirmPlan(fixture.Id, "group", "p1", "missing-help-target", revision,
            new(1, 0, true, SealedIntent.Help)), "sealed_case_invalid_plan");
        var first = new SealedPlanConfirmation(1, 0, true, SealedIntent.Investigate);
        var receipt = fixture.Store.ConfirmPlan(fixture.Id, "group", "p1", "first-plan", revision, first);
        Denied(() => fixture.Store.PauseTimeline(fixture.Id, "group", "p1", "first-plan", revision), "idempotency_key_reused");
        Denied(() => fixture.Store.ConfirmPlan(fixture.Id, "group", "p1", "start-timeline", 9, first), "idempotency_key_reused");
        var original = fixture.Reopen().ReadOwnPlan(fixture.Id, "group", "p1", 1);
        Equal(1, original.Action!.Revision); Equal("p1", original.Action.ActorId); Equal(false, original.Locked);
        Equal<SealedAction?>(null, fixture.Store.ReadOwnPlan(fixture.Id, "group", "p2", 1).Action);
        Denied(() => fixture.Store.ConfirmPlan(fixture.Id, "group", "p1", "stale-revise", receipt.Revision,
            new(1, 0, true, SealedIntent.Guard)), "action_revision_conflict");
        var replacement = fixture.Store.ConfirmPlan(fixture.Id, "group", "p1", "replace-plan", receipt.Revision,
            new(1, 1, true, SealedIntent.Guard));
        Equal(2, fixture.Store.ReadOwnPlan(fixture.Id, "group", "p1", 1).Action!.Revision);
        Denied(() => fixture.Store.ResolveConfirmedChapter(fixture.Id, "group", 1, replacement.Revision), "chapter_not_closed");
        fixture.Clock.Now = start.Timing.PlanningEndsAt;
        Denied(() => fixture.Store.ConfirmPlan(fixture.Id, "group", "p1", "late-revise", replacement.Revision,
            new(1, 2, true, SealedIntent.Investigate)), "chapter_locked");
        var retry = fixture.Reopen().ConfirmPlan(fixture.Id, "group", "p1", "first-plan", revision, first);
        Equal(receipt.EventId, retry.EventId); Equal(receipt.Revision, retry.Revision); Equal(true, retry.Replayed);
        Equal(true, fixture.Store.ReadOwnPlan(fixture.Id, "group", "p1", 1).Locked);
        var resolved = fixture.Reopen().ResolveConfirmedChapter(fixture.Id, "group", 1, replacement.Revision);
        Equal(1, resolved.Results.Count(result => result.ActorId == "p1" && result.Intent == SealedIntent.Guard));
        var replay = fixture.Reopen().ResolveConfirmedChapter(fixture.Id, "group", 1, replacement.Revision);
        Equal(true, replay.Replayed); Equal(resolved.Revision, replay.Revision);
        Equal(1, fixture.Store.ReadWorldHistory(fixture.Id, "group").Count);
        Equal(1, fixture.Store.ReadWorld(fixture.Id, "group").LastChapter);
        fixture.Clock.Now = start.Timing.QuietEndsAt;
        Denied(() => fixture.Store.ConfirmPlan(fixture.Id, "group", "p1", "old-offline-draft", resolved.Revision,
            new(1, 2, true, SealedIntent.Hold)), "wrong_chapter");
        var lethal = new SealedPlanConfirmation(2, 0, true, SealedIntent.Lethal, "p2", Method: "BREAKING_SEAL", LethalConfirmed: true);
        var lethalReceipt = fixture.Store.ConfirmPlan(fixture.Id, "group", "p1", "extreme-plan", resolved.Revision, lethal);
        Equal(1, fixture.Store.ReadWorld(fixture.Id, "group").Checkpoint.Players.Single(p => p.Id == "p1").ExtremeToken);
        fixture.Clock.Now = fixture.Read().PlanningEndsAt;
        var lethalResult = fixture.Store.ResolveConfirmedChapter(fixture.Id, "group", 2, lethalReceipt.Revision);
        var budget = fixture.Store.ReadWorld(fixture.Id, "group").Checkpoint.Players.Single(p => p.Id == "p1").ExtremeToken;
        Equal(0, budget);
        Equal(true, fixture.Reopen().ResolveConfirmedChapter(fixture.Id, "group", 2, lethalReceipt.Revision).Replayed);
        Equal(budget, fixture.Store.ReadWorld(fixture.Id, "group").Checkpoint.Players.Single(p => p.Id == "p1").ExtremeToken);
        Equal(lethalResult.Revision, fixture.Read().Revision);
    }

    private static void CompetingRevisionsAcceptOnePlan()
    {
        using var fixture = new SealedCaseTimingTests.Fixture("first", 8); var start = fixture.Start();
        fixture.Store.InitializeWorld(fixture.Id, "group", start.Revision, 1000, World(fixture));
        fixture.Clock.Now = start.Timing.IntroductionEndsAt;
        var revision = fixture.Read().Revision;
        using var barrier = new Barrier(2);
        var attempts = Enumerable.Range(0, 2).Select(index => Task.Run(() =>
        {
            barrier.SignalAndWait();
            try
            {
                fixture.Reopen().ConfirmPlan(fixture.Id, "group", "p1", "parallel-plan-" + index, revision,
                    new(1, 0, true, index == 0 ? SealedIntent.Investigate : SealedIntent.Guard));
                return "committed";
            }
            catch (SealedCasePersistenceException error) { return error.Code; }
        })).ToArray();
        Task.WaitAll(attempts); Equal(1, attempts.Count(attempt => attempt.Result == "committed"));
        Equal(1, attempts.Count(attempt => attempt.Result == "revision_conflict"));
        Equal(1, fixture.Reopen().ReadOwnPlan(fixture.Id, "group", "p1", 1).Action!.Revision);
    }

    private static void OverdueChaptersResolveInOrderFromCanonicalPlans()
    {
        using var fixture = new SealedCaseTimingTests.Fixture("first", 8); var start = fixture.Start();
        fixture.Store.InitializeWorld(fixture.Id, "group", start.Revision, 1000, World(fixture));
        fixture.Clock.Now = start.Timing.FinaleEndsAt;
        var revision = fixture.Read().Revision;
        Denied(() => fixture.Store.ResolveWorldChapter(fixture.Id, "group", 1, revision, 1, []), "sealed_case_plan_authority_required");
        Denied(() => fixture.Store.ResolveConfirmedChapter(fixture.Id, "group", 2, revision), "wrong_chapter");
        for (var chapter = 1; chapter <= 3; chapter++)
        {
            var resolved = fixture.Reopen().ResolveConfirmedChapter(fixture.Id, "group", chapter, revision);
            Equal(chapter, fixture.Store.ReadWorld(fixture.Id, "group").LastChapter); revision = resolved.Revision;
        }
        Equal(3, fixture.Store.ReadWorldHistory(fixture.Id, "group").Count);
        Denied(() => fixture.Store.ResolveConfirmedChapter(fixture.Id, "group", 4, revision), "chapter_not_closed");
    }

    private static void DeadlineCrossedWhileWaitingForWriterRejectsConfirmation()
    {
        using var fixture = new SealedCaseTimingTests.Fixture("first", 8); var start = fixture.Start();
        fixture.Store.InitializeWorld(fixture.Id, "group", start.Revision, 1000, World(fixture));
        fixture.Clock.Now = start.Timing.PlanningEndsAt - 1;
        var revision = fixture.Read().Revision;
        using var connection = new SqliteConnection("Data Source=" + fixture.Path); connection.Open();
        using var held = connection.BeginTransaction(deferred: false);
        using var observed = new ManualResetEventSlim(); fixture.Clock.OnRead = observed.Set;
        var pending = Task.Run(() =>
        {
            try
            {
                fixture.Store.ConfirmPlan(fixture.Id, "group", "p1", "waiting-confirm", revision, new(1, 0, true, SealedIntent.Investigate));
                return "committed";
            }
            catch (SealedCasePersistenceException error) { return error.Code; }
        });
        try
        {
            if (!observed.Wait(TimeSpan.FromSeconds(5))) { throw new InvalidOperationException("Confirmation did not reach server clock"); }
            fixture.Clock.Now = start.Timing.PlanningEndsAt;
        }
        finally { held.Commit(); }
        Equal("chapter_locked", pending.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult());
        fixture.Clock.OnRead = null;
        Equal(revision, fixture.Read().Revision);
        Equal<SealedAction?>(null, fixture.Store.ReadOwnPlan(fixture.Id, "group", "p1", 1).Action);
    }

    private static SealedGame World(SealedCaseTimingTests.Fixture fixture)
    {
        var players = Enumerable.Range(1, 8).Select(i => "p" + i).ToArray();
        var game = new SealedGame(players.Select(id => new SealedPlayer(id, knownPlayers: players.Where(other => other != id))).ToArray(), "synthetic-plan-seed");
        game.Scenario = SealedScenario.Create(fixture.Store.ReadRoster(fixture.Id, "group"), fixture.Variant).Scenario;
        return game;
    }
    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) { throw new InvalidOperationException($"Sealed plan: expected {expected}, got {actual}"); }
    }
    private static void Denied(Action action, string code)
    {
        try { action(); }
        catch (SealedCasePersistenceException error) when (error.Code == code) { return; }
        throw new InvalidOperationException("Expected plan denial: " + code);
    }
}
