using Kufr.Backend.Features.SealedCase;

// Focused durable-store acceptance checks. HTTP auth and app route composition
// remain with the lead because Program.cs and existing auth adapters are owned there.
internal static class SealedCasePersistenceTests
{
    internal static void Run()
    {
        creates_durable_setup_and_event_history();
        confirmed_commands_are_idempotent_and_revision_checked();
        action_outcomes_are_not_reported_as_resolved();
        invalid_transition_and_late_commands_are_rejected();
        retry_is_bound_to_group_kind_and_expected_revision();
        revision_and_history_are_one_read_snapshot();
    }

    private static void creates_durable_setup_and_event_history()
    {
        var path = TempPath();
        try
        {
            var store = new SealedCaseStore(path);
            var setup = store.Create("group-1", "account-1", 1_000, 8);
            Equal("setup", setup.Status);
            var lobby = store.Append(setup.Id, setup.GroupId, "account-1", "lobby", "lobby-once", setup.Revision, 1_001, new { ready = true });
            Equal("lobby", lobby.Status);
            var restarted = new SealedCaseStore(path).Read(setup.Id);
            Equal(2L, restarted.Revision);
            Equal(1, restarted.Events.Count);
            Equal("lobby", restarted.Status);
        }
        finally { Delete(path); }
    }

    private static void confirmed_commands_are_idempotent_and_revision_checked()
    {
        var path = TempPath();
        try
        {
            var store = new SealedCaseStore(path);
            var setup = store.Create("group", "owner", 10);
            var lobby = store.Append(setup.Id, "group", "owner", "lobby", "lobby", 1, 11, new { ready = true });
            var started = store.Append(setup.Id, "group", "owner", "start", "start", lobby.Revision, 12, new { chapterSeconds = 600 });
            var command = new { intent = "REST", chapter = 1, revision = 1 };
            var first = store.Append(setup.Id, "group", "owner", "confirmed-action", "order-1", started.Revision, 13, command);
            var retry = store.Append(setup.Id, "group", "owner", "confirmed-action", "order-1", started.Revision, 99, command);
            Equal(false, first.Replayed);
            Equal(true, retry.Replayed);
            Equal(first.EventId, retry.EventId);
            Throws<SealedCasePersistenceException>(() => store.Append(setup.Id, "group", "owner", "confirmed-action", "another", started.Revision, 14, command), "revision_conflict");
        }
        finally { Delete(path); }
    }

    private static void action_outcomes_are_not_reported_as_resolved()
    {
        var path = TempPath();
        try
        {
            var store = new SealedCaseStore(path);
            var setup = store.Create("group", "owner", 1);
            Throws<SealedCasePersistenceException>(() => store.Append(setup.Id, "group", "owner", "resolve", "resolve", setup.Revision, 2, new { result = "SUCCESS" }), "unsupported_action");
        }
        finally { Delete(path); }
    }

    private static void invalid_transition_and_late_commands_are_rejected()
    {
        var path = TempPath();
        try
        {
            var store = new SealedCaseStore(path);
            var setup = store.Create("group", "owner", 1);
            Throws<SealedCasePersistenceException>(() => store.Append(setup.Id, "group", "owner", "start", "early", 1, 2, new { chapterSeconds = 600 }), "invalid_transition");
            var lobby = store.Append(setup.Id, "group", "owner", "lobby", "lobby", 1, 3, new { ready = true });
            var start = store.Append(setup.Id, "group", "owner", "start", "start", lobby.Revision, 4, new { chapterSeconds = 600 });
            Throws<SealedCasePersistenceException>(() => store.Append(setup.Id, "group", "owner", "confirmed-action", "late", start.Revision, 604, new { intent = "REST", chapter = 1, revision = 1 }), "chapter_locked");
        }
        finally { Delete(path); }
    }

    private static void retry_is_bound_to_group_kind_and_expected_revision()
    {
        var path = TempPath();
        try
        {
            var store = new SealedCaseStore(path);
            var setup = store.Create("group", "owner", 1);
            var payload = new { ready = true };
            store.Append(setup.Id, "group", "owner", "lobby", "same", 1, 2, payload);
            Throws<SealedCasePersistenceException>(() => store.Append(setup.Id, "different", "owner", "lobby", "same", 1, 3, payload), "case_not_found");
            Throws<SealedCasePersistenceException>(() => store.Append(setup.Id, "group", "owner", "start", "same", 1, 3, payload), "idempotency_key_reused");
            Throws<SealedCasePersistenceException>(() => store.Append(setup.Id, "group", "owner", "lobby", "same", 2, 3, payload), "idempotency_key_reused");
        }
        finally { Delete(path); }
    }

    private static void revision_and_history_are_one_read_snapshot()
    {
        var path = TempPath();
        try
        {
            var store = new SealedCaseStore(path); var setup = store.Create("group", "owner", 1);
            using var barrier = new Barrier(2);
            var writer = Task.Run(() =>
            {
                barrier.SignalAndWait();
                for (var i = 0; i < 200; i++) store.Append(setup.Id, "group", "owner", "lobby", "op-" + i, i + 1, i + 2, new { ready = i % 2 == 0 });
            });
            barrier.SignalAndWait();
            try
            {
                for (var i = 0; i < 200; i++)
                {
                    var snapshot = store.Read(setup.Id);
                    Equal(snapshot.Revision - 1, (long)snapshot.Events.Count);
                }
            }
            finally { writer.GetAwaiter().GetResult(); }
        }
        finally { Delete(path); }
    }

    private static string TempPath() => Path.Combine(Path.GetTempPath(), "sealed-case-" + Guid.NewGuid().ToString("N") + ".sqlite");
    private static void Delete(string path) { foreach (var file in new[] { path, path + "-wal", path + "-shm" }) if (File.Exists(file)) File.Delete(file); }
    private static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"SealedCase HTTP/store: expected {expected}, got {actual}"); }
    private static void Throws<T>(Action action, string code) where T : Exception
    {
        try { action(); } catch (T error) when (error is SealedCasePersistenceException persistence && persistence.Code == code) { return; }
        throw new InvalidOperationException($"SealedCase HTTP/store: expected {code}");
    }
}
