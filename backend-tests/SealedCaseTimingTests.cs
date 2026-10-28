using System.Text.Json;
using Kufr.Backend.Features.Customers;
using Kufr.Backend.Features.SealedCase;
using Microsoft.Data.Sqlite;

internal static class SealedCaseTimingTests
{
    internal static void Run()
    {
        ScheduleBoundariesSurviveRestart();
        PauseAndResumeAreDurableAuditedAndIdempotent();
        ConfirmationsUseServerClockAndNeverReopenClosedPlanning();
        CompetingPausesCommitOnce();
        QuietAndFinalePauseDoNotReopenPlanning();
        Console.WriteLine("PASS durable server chapter timing, pause/resume, replay, CAS and confirmation deadlines.");
    }

    private static void ScheduleBoundariesSurviveRestart()
    {
        foreach (var (variant, players) in new[] { ("first", 8), ("first", 12), ("full", 8), ("full", 13), ("full", 21), ("full", 30) })
        {
            using var fixture = new Fixture(variant, players);
            var started = fixture.Start();
            var schedule = SealedCaseRules.Schedule(variant, players);
            Equal("introduction", started.Timing.Phase); Equal(0, started.Timing.Chapter);
            Equal(schedule.TotalSeconds + 1000L, started.Timing.FinaleEndsAt);
            fixture.Clock.Now = started.Timing.IntroductionEndsAt - 1; Equal("introduction", fixture.Read().Phase);
            fixture.Clock.Now++; Equal("planning", fixture.Read().Phase); Equal(1, fixture.Read().Chapter);
            for (var chapter = 1; chapter <= schedule.Chapters; chapter++)
            {
                var chapterStart = 1000 + schedule.IntroductionSeconds + (chapter - 1) * (schedule.PlanningSeconds + schedule.QuietSeconds);
                fixture.Clock.Now = chapterStart + schedule.PlanningSeconds - 1;
                var planning = fixture.Read(); Equal(chapter, planning.Chapter); Equal(true, planning.CanSubmit);
                fixture.Clock.Now++; Equal("quiet", fixture.Read().Phase); Equal(false, fixture.Read().CanSubmit);
                fixture.Clock.Now = chapterStart + schedule.PlanningSeconds + schedule.QuietSeconds - 1;
                Equal("quiet", fixture.Reopen().ReadTimeline(fixture.Id, "group").Phase);
            }
            fixture.Clock.Now++; Equal("finale", fixture.Read().Phase);
            fixture.Clock.Now = started.Timing.FinaleEndsAt; Equal("finished", fixture.Read().Phase);
            Equal(started.Revision, fixture.Read().Revision); // Polling does not append or extend a chapter.
            Equal(players + 1, fixture.Store.Read(fixture.Id).Events.Count);
            Equal(1, fixture.Store.EraseAccount("p1"));
            using var connection = new SqliteConnection("Data Source=" + fixture.Path); connection.Open();
            using var query = connection.CreateCommand(); query.CommandText = "SELECT COUNT(*) FROM sealed_case_timing";
            Equal(0L, (long)query.ExecuteScalar()!);
        }
    }

    private static void PauseAndResumeAreDurableAuditedAndIdempotent()
    {
        using var fixture = new Fixture("first", 8); var started = fixture.Start();
        fixture.Clock.Now = started.Timing.IntroductionEndsAt + 120;
        var pause = fixture.Store.PauseTimeline(fixture.Id, "group", "p1", "pause-once", started.Revision);
        Equal("paused", pause.Timing.Phase);
        fixture.Clock.Now += 600;
        Equal("paused", fixture.Reopen().ReadTimeline(fixture.Id, "group").Phase);
        var replay = fixture.Reopen().PauseTimeline(fixture.Id, "group", "p1", "pause-once", started.Revision);
        Equal(true, replay.Replayed); Equal(pause.EventId, replay.EventId);
        Equal(JsonSerializer.Serialize(pause.Timing), JsonSerializer.Serialize(replay.Timing));
        Denied(() => fixture.Store.PauseTimeline(fixture.Id, "group", "p2", "foreign-pause", pause.Revision), "sealed_case_host_required");
        Denied(() => fixture.Store.ResumeTimeline(fixture.Id, "group", "p1", "stale-resume", started.Revision), "revision_conflict");
        var resumed = fixture.Reopen().ResumeTimeline(fixture.Id, "group", "p1", "resume-once", pause.Revision);
        Equal("planning", resumed.Timing.Phase); Equal(600L, resumed.Timing.PausedSeconds);
        Equal(started.Timing.PlanningEndsAt + 600, resumed.Timing.PlanningEndsAt);
        Equal(started.Timing.FinaleEndsAt + 600, resumed.Timing.FinaleEndsAt);
        Equal(true, fixture.Store.ResumeTimeline(fixture.Id, "group", "p1", "resume-once", pause.Revision).Replayed);
        Equal(600L, fixture.Read().PausedSeconds);
        Denied(() => fixture.Store.PauseTimeline(fixture.Id, "group", "p1", "resume-once", pause.Revision), "idempotency_key_reused");
        Equal(11, fixture.Store.Read(fixture.Id).Events.Count);
    }

    private static void ConfirmationsUseServerClockAndNeverReopenClosedPlanning()
    {
        using var fixture = new Fixture("first", 8); var started = fixture.Start();
        fixture.Clock.Now = started.Timing.IntroductionEndsAt;
        Denied(() => fixture.Store.Append(fixture.Id, "group", "p2", "confirmed-action", "legacy-confirm", started.Revision, 1,
            new { chapter = 1, intent = "HOLD" }), "sealed_case_action_contract_required");
    }

    private static void CompetingPausesCommitOnce()
    {
        using var fixture = new Fixture("first", 8); var started = fixture.Start();
        using var barrier = new Barrier(2);
        var attempts = Enumerable.Range(0, 2).Select(index => Task.Run(() =>
        {
            barrier.SignalAndWait();
            try { fixture.Reopen().PauseTimeline(fixture.Id, "group", "p1", "parallel-pause-" + index, started.Revision); return "committed"; }
            catch (SealedCasePersistenceException error) { return error.Code; }
        })).ToArray();
        Task.WaitAll(attempts); Equal(1, attempts.Count(attempt => attempt.Result == "committed"));
        Equal(1, attempts.Count(attempt => attempt.Result == "revision_conflict"));
        Equal(started.Revision + 1, fixture.Read().Revision);
    }

    private static void QuietAndFinalePauseDoNotReopenPlanning()
    {
        foreach (var phase in new[] { "quiet", "finale" })
        {
            using var fixture = new Fixture("first", 8); var started = fixture.Start();
            fixture.Clock.Now = phase == "quiet" ? started.Timing.PlanningEndsAt + 30 : started.Timing.FinaleEndsAt - 300;
            Equal(phase, fixture.Read().Phase);
            var pause = fixture.Store.PauseTimeline(fixture.Id, "group", "p1", "phase-pause", started.Revision);
            fixture.Clock.Now += 300;
            var resumed = fixture.Reopen().ResumeTimeline(fixture.Id, "group", "p1", "phase-resume", pause.Revision);
            Equal(phase, resumed.Timing.Phase); Equal(false, resumed.Timing.CanSubmit);
            fixture.Clock.Now = resumed.Timing.FinaleEndsAt;
            Denied(() => fixture.Store.PauseTimeline(fixture.Id, "group", "p1", "finished-pause", resumed.Revision), "invalid_transition");
        }
    }

    internal sealed class TimingClock : TimeProvider
    {
        private long now = 1000;
        internal Action? OnRead { get; set; }
        internal long Now { get => Interlocked.Read(ref now); set => Interlocked.Exchange(ref now, value); }
        public override DateTimeOffset GetUtcNow()
        {
            var sampled = Now; OnRead?.Invoke(); return DateTimeOffset.FromUnixTimeSeconds(sampled);
        }
    }

    internal sealed class Fixture : IDisposable
    {
        internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sealed-timing-" + Guid.NewGuid().ToString("N") + ".sqlite");
        internal TimingClock Clock { get; } = new();
        internal SealedCaseStore Store { get; }
        internal string Id { get; }
        private readonly int players;
        internal Fixture(string variant, int players)
        {
            this.players = players; Store = Reopen();
            var setup = Store.Create("group", "p1", 900, players, variant); Id = setup.Id;
            for (var i = 1; i <= players; i++)
            {
                var account = "p" + i; var reference = "snapshot-" + account;
                var profile = new CharacterProfile("profile-" + account, 1, new("Synthetic " + account, "Test"), "ready", null, null, 900);
                Store.Append(Id, "group", account, "lobby", "ready-" + account, i, 901,
                    new SealedCaseLobbyEntry(account, true, true, reference), lobbyCapacity: players,
                    capturePrivateSnapshot: () => SealedCasePrivateSnapshot.From(reference, account, profile, null));
            }
        }
        internal SealedCaseStore Reopen() => new(Path, clock: Clock);
        internal SealedTimingSnapshot Read() => Store.ReadTimeline(Id, "group");
        internal SealedTimingReceipt Start() => Store.StartTimeline(Id, "group", "p1", "start-timeline", players + 1);
        public void Dispose() { SqliteConnection.ClearAllPools(); File.Delete(Path); }
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) { throw new InvalidOperationException($"Sealed timing: expected {expected}, got {actual}"); }
    }
    private static void Denied(Action action, string code)
    {
        try { action(); }
        catch (SealedCasePersistenceException error) when (error.Code == code) { return; }
        throw new InvalidOperationException("Expected timing denial: " + code);
    }
}
