using Kufr.Backend.Features.SealedCase;
using Microsoft.Data.Sqlite;

internal static class SealedCasePlanTests
{
    internal static void Run()
    {
        ExplicitConfirmationRevisionAndResolutionSurviveRestart();
        CompetingRevisionsAcceptOnePlan();
        OverdueChaptersResolveInOrderFromCanonicalPlans();
        DeadlineCrossedWhileWaitingForWriterRejectsConfirmation();
        Console.WriteLine("PASS explicit durable plan confirmation/revision, deadline, authorized targets, replay and canonical resolution.");
    }

    private static void ExplicitConfirmationRevisionAndResolutionSurviveRestart()
    {
        using var fixture = new SealedCaseTimingTests.Fixture("first", 8);
        var start = fixture.Start();
        fixture.Store.InitializeWorld(fixture.Id, "group", start.Revision, 1000, World());
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
        var first = new SealedPlanConfirmation(1, 0, true, SealedIntent.Hold);
        var receipt = fixture.Store.ConfirmPlan(fixture.Id, "group", "p1", "first-plan", revision, first);
        Denied(() => fixture.Store.PauseTimeline(fixture.Id, "group", "p1", "first-plan", revision), "idempotency_key_reused");
        Denied(() => fixture.Store.ConfirmPlan(fixture.Id, "group", "p1", "start-timeline", 9, first), "idempotency_key_reused");
        var original = fixture.Reopen().ReadOwnPlan(fixture.Id, "group", "p1", 1);
        Equal(1, original.Action!.Revision); Equal("p1", original.Action.ActorId); Equal(false, original.Locked);
        Equal<SealedAction?>(null, fixture.Store.ReadOwnPlan(fixture.Id, "group", "p2", 1).Action);
        Denied(() => fixture.Store.ConfirmPlan(fixture.Id, "group", "p1", "stale-revise", receipt.Revision,
            new(1, 0, true, SealedIntent.Rest)), "action_revision_conflict");
        var replacement = fixture.Store.ConfirmPlan(fixture.Id, "group", "p1", "replace-plan", receipt.Revision,
            new(1, 1, true, SealedIntent.Rest));
        Equal(2, fixture.Store.ReadOwnPlan(fixture.Id, "group", "p1", 1).Action!.Revision);
        Denied(() => fixture.Store.ResolveConfirmedChapter(fixture.Id, "group", 1, replacement.Revision), "chapter_not_closed");
        fixture.Clock.Now = start.Timing.PlanningEndsAt;
        Denied(() => fixture.Store.ConfirmPlan(fixture.Id, "group", "p1", "late-revise", replacement.Revision,
            new(1, 2, true, SealedIntent.Hold)), "chapter_locked");
        var retry = fixture.Reopen().ConfirmPlan(fixture.Id, "group", "p1", "first-plan", revision, first);
        Equal(receipt.EventId, retry.EventId); Equal(receipt.Revision, retry.Revision); Equal(true, retry.Replayed);
        Equal(true, fixture.Store.ReadOwnPlan(fixture.Id, "group", "p1", 1).Locked);
        var resolved = fixture.Reopen().ResolveConfirmedChapter(fixture.Id, "group", 1, replacement.Revision);
        Equal(1, resolved.Results.Count(result => result.ActorId == "p1" && result.Intent == SealedIntent.Rest));
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
        fixture.Store.InitializeWorld(fixture.Id, "group", start.Revision, 1000, World());
        fixture.Clock.Now = start.Timing.IntroductionEndsAt;
        var revision = fixture.Read().Revision;
        using var barrier = new Barrier(2);
        var attempts = Enumerable.Range(0, 2).Select(index => Task.Run(() =>
        {
            barrier.SignalAndWait();
            try
            {
                fixture.Reopen().ConfirmPlan(fixture.Id, "group", "p1", "parallel-plan-" + index, revision,
                    new(1, 0, true, index == 0 ? SealedIntent.Hold : SealedIntent.Rest));
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
        fixture.Store.InitializeWorld(fixture.Id, "group", start.Revision, 1000, World());
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
        fixture.Store.InitializeWorld(fixture.Id, "group", start.Revision, 1000, World());
        fixture.Clock.Now = start.Timing.PlanningEndsAt - 1;
        var revision = fixture.Read().Revision;
        using var connection = new SqliteConnection("Data Source=" + fixture.Path); connection.Open();
        using var held = connection.BeginTransaction(deferred: false);
        using var observed = new ManualResetEventSlim(); fixture.Clock.OnRead = observed.Set;
        var pending = Task.Run(() =>
        {
            try
            {
                fixture.Store.ConfirmPlan(fixture.Id, "group", "p1", "waiting-confirm", revision, new(1, 0, true, SealedIntent.Hold));
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

    private static SealedGame World()
    {
        var players = Enumerable.Range(1, 8).Select(i => "p" + i).ToArray();
        return new(players.Select(id => new SealedPlayer(id, knownPlayers: players.Where(other => other != id))).ToArray(), "synthetic-plan-seed");
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
