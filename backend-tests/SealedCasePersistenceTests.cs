using Kufr.Backend.Features.SealedCase;
using Kufr.Backend.Features.Customers;

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
        retry_returns_original_receipt_after_later_transitions();
        world_checkpoint_and_content_version_survive_reopening();
        resolved_outcomes_survive_restart_and_cannot_be_rewritten();
        event_history_is_append_only_and_failed_commits_are_atomic();
        competing_resolvers_commit_only_once();
        creation_and_initialization_replay_the_original_snapshot();
        rejected_resolution_and_admission_leave_state_unchanged();
        upgrades_legacy_journal_without_rewriting_history();
        privacy_erasure_is_atomic_terminal_and_idempotent();
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

    private static void retry_returns_original_receipt_after_later_transitions()
    {
        var path = TempPath();
        try
        {
            var store = new SealedCaseStore(path);
            var setup = store.Create("group", "owner", 1);
            var lobby = store.Append(setup.Id, "group", "owner", "lobby", "lobby", 1, 2, new { ready = true });
            store.Append(setup.Id, "group", "owner", "start", "start", 2, 3, new { chapterSeconds = 600 });
            var retry = new SealedCaseStore(path).Append(setup.Id, "group", "owner", "lobby", "lobby", 1, 99, new { ready = true });
            Equal(lobby with { Replayed = true }, retry);
        }
        finally { Delete(path); }
    }

    private static void world_checkpoint_and_content_version_survive_reopening()
    {
        var path = TempPath();
        try
        {
            var store = new SealedCaseStore(path);
            var setup = store.Create("group", "owner", 1);
            var player = new SealedPlayer("owner", "ARCHIV", ["other"], ["other"], methods: ["IMPACT_SEAL"], expertise: ["STUN"])
            { Cover = 1, Preparation = 1, PreparationChapter = 2, PreparedIntent = SealedIntent.Stun, Ward = 1, FeigningSleep = true, UnconsciousUntilEnd = 3, SleepingUntilStart = 2 };
            player.VerifiedSleepEvidence["other"] = "evidence";
            var game = new SealedGame([player], "private-seed", new Dictionary<string, string[]> { ["ARCHIV"] = ["SALON"], ["SALON"] = ["ARCHIV"] });
            var initialized = store.InitializeWorld(setup.Id, "group", setup.Revision, 2, game);
            var recovered = new SealedCaseStore(path).ReadWorld(setup.Id, "group");
            Equal(2L, recovered.Revision);
            Equal(0, recovered.LastChapter);
            Equal(System.Text.Json.JsonSerializer.Serialize(initialized), System.Text.Json.JsonSerializer.Serialize(recovered));
            Equal("1.1.0", System.Text.Json.JsonDocument.Parse(recovered.ContentVersionJson).RootElement.GetProperty("rulesVersion").GetString());
            var restored = SealedGame.Restore(recovered.Checkpoint);
            Equal(player.Copy().VerifiedSleepEvidence["other"], restored.Players["owner"].VerifiedSleepEvidence["other"]);
            Equal(System.Text.Json.JsonSerializer.Serialize(game.Checkpoint()), System.Text.Json.JsonSerializer.Serialize(restored.Checkpoint()));
            Throws<SealedCasePersistenceException>(() => store.ReadWorld(setup.Id, "other-group"), "case_not_found");
            Throws<SealedCasePersistenceException>(() => store.InitializeWorld(setup.Id, "group", 2, 3, game), "world_already_initialized");
        }
        finally { Delete(path); }
    }

    private static void resolved_outcomes_survive_restart_and_cannot_be_rewritten()
    {
        var path = TempPath();
        try
        {
            var store = new SealedCaseStore(path);
            var setup = store.Create("group", "owner", 1);
            store.InitializeWorld(setup.Id, "group", 1, 2, new SealedGame([
                new SealedPlayer("owner", knownPlayers: ["other"], observedPlayers: ["other"]),
                new SealedPlayer("other", knownPlayers: ["owner"], observedPlayers: ["owner"])], "persisted-secret"));
            SealedAction[] firstOrders = [new("rest", "owner", 1, SealedIntent.Rest), new("hold", "other", 1, SealedIntent.Hold)];
            var first = store.ResolveWorldChapter(setup.Id, "group", 1, 2, 3, firstOrders);
            var restarted = new SealedCaseStore(path);
            Equal(SealedPlayerState.Sleeping, restarted.ReadWorld(setup.Id, "group").Checkpoint.Players.Single(x => x.Id == "owner").State);
            SealedAction[] orders = [new("attack", "owner", 2, SealedIntent.Lethal, "other", Method: "BREAKING_SEAL", LethalConfirmed: true), new("sleep", "other", 2, SealedIntent.Rest)];
            var resolved = restarted.ResolveWorldChapter(setup.Id, "group", 2, 3, 4, orders);
            var retry = new SealedCaseStore(path).ResolveWorldChapter(setup.Id, "group", 2, 3, 999, orders.Reverse().ToArray());
            Equal(System.Text.Json.JsonSerializer.Serialize(resolved with { Replayed = true }), System.Text.Json.JsonSerializer.Serialize(retry));
            Equal(4L, restarted.ReadWorld(setup.Id, "group").Revision);
            Equal(0, restarted.ReadWorld(setup.Id, "group").Checkpoint.Players.Single(x => x.Id == "owner").ExtremeToken);
            Equal(true, resolved.Results.Single(x => x.ActorId == "owner").Rolls is not null);
            Equal(System.Text.Json.JsonSerializer.Serialize(first with { Replayed = true }), System.Text.Json.JsonSerializer.Serialize(restarted.ResolveWorldChapter(setup.Id, "group", 1, 2, 1000, firstOrders)));
            Throws<SealedCasePersistenceException>(() => restarted.ResolveWorldChapter(setup.Id, "group", 2, 3, 5, [orders[0] with { Intent = SealedIntent.Hold }, orders[1]]), "resolved_chapter_conflict");
            Throws<SealedCasePersistenceException>(() => restarted.ResolveWorldChapter(setup.Id, "group", 2, 4, 5, orders), "resolved_chapter_conflict");
            Throws<SealedCasePersistenceException>(() => restarted.ResolveWorldChapter(setup.Id, "other-group", 2, 3, 5, orders), "case_not_found");
            Throws<SealedCasePersistenceException>(() => restarted.ResolveWorldChapter(setup.Id, "group", 4, 4, 5, []), "wrong_chapter");
            Equal(4L, restarted.ReadWorld(setup.Id, "group").Revision);
            Equal(2, restarted.ReadWorldHistory(setup.Id, "group").Count);
        }
        finally { Delete(path); }
    }

    private static void event_history_is_append_only_and_failed_commits_are_atomic()
    {
        var path = TempPath();
        try
        {
            var store = new SealedCaseStore(path); var setup = store.Create("group", "owner", 1);
            store.Append(setup.Id, "group", "owner", "lobby", "lobby", 1, 2, new { ready = true });
            store.InitializeWorld(setup.Id, "group", 2, 3, new SealedGame([new SealedPlayer("owner")], "seed"));
            store.ResolveWorldChapter(setup.Id, "group", 1, 3, 4, [new("rest", "owner", 1, SealedIntent.Rest)]);
            using var c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}"); c.Open();
            foreach (var table in new[] { "sealed_case_events", "sealed_case_world", "sealed_case_resolutions" })
            foreach (var sql in new[] { $"UPDATE {table} SET created_at=999", $"DELETE FROM {table}", $"INSERT OR REPLACE INTO {table} SELECT * FROM {table}" })
            {
                using var cmd = c.CreateCommand(); cmd.CommandText = sql;
                try { cmd.ExecuteNonQuery(); throw new InvalidOperationException("History mutation was permitted: " + sql); }
                catch (Microsoft.Data.Sqlite.SqliteException e) when (e.SqliteErrorCode == 19) { }
            }
            var before = System.Text.Json.JsonSerializer.Serialize(store.ReadWorld(setup.Id, "group"));
            using (var fail = c.CreateCommand())
            {
                fail.CommandText = "CREATE TRIGGER fail_resolution_revision BEFORE UPDATE OF revision ON sealed_cases BEGIN SELECT RAISE(ABORT,'test interruption'); END";
                fail.ExecuteNonQuery();
            }
            try { store.ResolveWorldChapter(setup.Id, "group", 2, 4, 5, [new("hold", "owner", 2, SealedIntent.Hold)]); throw new InvalidOperationException("Expected injected failure"); }
            catch (Microsoft.Data.Sqlite.SqliteException e) when (e.SqliteErrorCode == 19) { }
            Equal(before, System.Text.Json.JsonSerializer.Serialize(new SealedCaseStore(path).ReadWorld(setup.Id, "group")));
            Equal(1, store.ReadWorldHistory(setup.Id, "group").Count);
            using var drop = c.CreateCommand(); drop.CommandText = "DROP TRIGGER fail_resolution_revision"; drop.ExecuteNonQuery();
            store.ResolveWorldChapter(setup.Id, "group", 2, 4, 6, [new("hold", "owner", 2, SealedIntent.Hold)]);
            Equal(2, store.ReadWorldHistory(setup.Id, "group").Count);
        }
        finally { Delete(path); }
    }

    private static void competing_resolvers_commit_only_once()
    {
        var path = TempPath();
        try
        {
            var store = new SealedCaseStore(path); var setup = store.Create("group", "owner", 1);
            store.InitializeWorld(setup.Id, "group", 1, 2, new SealedGame([new SealedPlayer("owner")], "seed"));
            using var barrier = new Barrier(2);
            var tasks = Enumerable.Range(0, 2).Select(_ => Task.Run(() =>
            {
                var peer = new SealedCaseStore(path); barrier.SignalAndWait();
                return peer.ResolveWorldChapter(setup.Id, "group", 1, 2, 3, [new("rest", "owner", 1, SealedIntent.Rest)]);
            })).ToArray();
            Task.WaitAll(tasks);
            Equal(1, tasks.Count(t => !t.Result.Replayed));
            Equal(System.Text.Json.JsonSerializer.Serialize(tasks[0].Result with { Replayed = true }), System.Text.Json.JsonSerializer.Serialize(tasks[1].Result with { Replayed = true }));
            Equal(3L, store.ReadWorld(setup.Id, "group").Revision);
            Equal(1, store.ReadWorldHistory(setup.Id, "group").Count);
        }
        finally { Delete(path); }
    }

    private static void creation_and_initialization_replay_the_original_snapshot()
    {
        var path = TempPath();
        try
        {
            var store = new SealedCaseStore(path); var setup = store.Create("group", "owner", 1, operationId: "creation-operation");
            var game = new SealedGame([new SealedPlayer("owner")], "seed");
            var initial = store.InitializeWorld(setup.Id, "group", 1, 2, game);
            store.Append(setup.Id, "group", "owner", "lobby", "lobby", 2, 3, new { ready = true });
            Equal(setup, new SealedCaseStore(path).Create("group", "owner", 999, operationId: "creation-operation"));
            Equal(System.Text.Json.JsonSerializer.Serialize(initial), System.Text.Json.JsonSerializer.Serialize(store.InitializeWorld(setup.Id, "group", 1, 999, game)));
            Throws<SealedCasePersistenceException>(() => store.InitializeWorld(setup.Id, "group", 1, 999, new SealedGame([new SealedPlayer("owner")], "changed-seed")), "world_already_initialized");
            Equal(3L, store.ReadWorld(setup.Id, "group").Revision);
        }
        finally { Delete(path); }
    }

    private static void rejected_resolution_and_admission_leave_state_unchanged()
    {
        var path = TempPath();
        try
        {
            var store = new SealedCaseStore(path); var setup = store.Create("group", "owner", 1);
            var game = new SealedGame([new SealedPlayer("owner")], "seed");
            Func<IDisposable> denied = () => throw new SealedCasePersistenceException("admission_denied", 403);
            Throws<SealedCasePersistenceException>(() => store.InitializeWorld(setup.Id, "group", 1, 2, game, denied), "admission_denied");
            Equal(1L, store.Read(setup.Id).Revision);
            store.InitializeWorld(setup.Id, "group", 1, 2, game);
            Throws<SealedCasePersistenceException>(() => store.ResolveWorldChapter(setup.Id, "group", 1, 2, 3, [], denied), "admission_denied");
            Throws<SealedCasePersistenceException>(() => store.ResolveWorldChapter(setup.Id, "group", 1, 1, 3, []), "revision_conflict");
            try { store.ResolveWorldChapter(setup.Id, "group", 1, 2, 3, [new("invalid", "stranger", 1, SealedIntent.Rest)]); throw new InvalidOperationException("Unknown actor accepted"); }
            catch (SealedCaseRuleException) { }
            Equal(2L, store.ReadWorld(setup.Id, "group").Revision);
            Equal(0, store.ReadWorldHistory(setup.Id, "group").Count);
            store.ResolveWorldChapter(setup.Id, "group", 1, 2, 3, [new("valid", "owner", 1, SealedIntent.Rest)]);
            Throws<SealedCasePersistenceException>(() => store.ResolveWorldChapter(setup.Id, "group", 1, 2, 4, [new("valid", "owner", 1, SealedIntent.Rest)], denied), "admission_denied");
            Equal(3L, store.ReadWorld(setup.Id, "group").Revision);
        }
        finally { Delete(path); }
    }

    private static void upgrades_legacy_journal_without_rewriting_history()
    {
        var path = TempPath();
        try
        {
            using (var c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}"))
            {
                c.Open(); using var cmd = c.CreateCommand();
                cmd.CommandText = """
                    CREATE TABLE sealed_cases(id TEXT PRIMARY KEY, group_id TEXT NOT NULL, status TEXT NOT NULL, revision INTEGER NOT NULL,
                      started_at INTEGER NULL, chapter INTEGER NOT NULL, chapter_deadline INTEGER NULL,
                      setup_json TEXT NOT NULL, lobby_json TEXT NOT NULL, created_at INTEGER NOT NULL);
                    CREATE TABLE sealed_case_events(event_id TEXT PRIMARY KEY, case_id TEXT NOT NULL REFERENCES sealed_cases(id),
                      actor_id TEXT NOT NULL, idempotency_key TEXT NOT NULL, expected_revision INTEGER NOT NULL,
                      revision INTEGER NOT NULL, kind TEXT NOT NULL, payload_json TEXT NOT NULL, created_at INTEGER NOT NULL,
                      UNIQUE(case_id,actor_id,idempotency_key));
                    INSERT INTO sealed_cases VALUES('legacy','group','lobby',2,NULL,0,NULL,'{"createdBy":"owner","expectedPlayers":8,"variant":"full"}','{}',1);
                    INSERT INTO sealed_case_events VALUES('legacy-event','legacy','owner','old-key',1,2,'lobby','{"ready":true}',2);
                    """;
                cmd.ExecuteNonQuery();
            }
            var store = new SealedCaseStore(path);
            Equal(2L, store.Read("legacy").Revision);
            Equal("{\"ready\":true}", store.Read("legacy").Events.Single());
            var receipt = store.Append("legacy", "group", "owner", "lobby", "old-key", 1, 999, new { ready = true });
            Equal("legacy-event", receipt.EventId); Equal(true, receipt.Replayed);
            Equal(2L, receipt.Revision); Equal("lobby", receipt.Status);
            Throws<SealedCasePersistenceException>(() => store.ReadWorld("legacy", "group"), "world_not_initialized");
            store.InitializeWorld("legacy", "group", 2, 3, new SealedGame([new SealedPlayer("owner")], "legacy-seed"));
            Equal("{\"ready\":true}", store.Read("legacy").Events.Single());
            Equal(3L, new SealedCaseStore(path).ReadWorld("legacy", "group").Revision);
        }
        finally { Delete(path); }
    }

    private static void privacy_erasure_is_atomic_terminal_and_idempotent()
    {
        var path = TempPath();
        try
        {
            var store = new SealedCaseStore(path);
            var setup = store.Create("group", "deleted-account", 1, operationId: "erase-operation");
            store.Append(setup.Id, "group", "deleted-account", "lobby", "ready-once", 1, 2,
                new { playerId = "deleted-account", ready = true, consent = true });
            Equal(1, store.EraseAccount("deleted-account"));
            Equal(0, new SealedCaseStore(path).EraseAccount("deleted-account"));
            Throws<SealedCasePersistenceException>(() => new SealedCaseStore(path).Read(setup.Id), "case_not_found");
            Throws<SealedCasePersistenceException>(() => store.Append(setup.Id, "group", "deleted-account", "lobby", "ready-once", 1, 3,
                new { playerId = "deleted-account", ready = true, consent = true }), "case_not_found");
            using var c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}"); c.Open();
            using var command = c.CreateCommand();
            command.CommandText = "SELECT status,setup_json,lobby_json,(SELECT COUNT(*) FROM sealed_case_events WHERE case_id=sealed_cases.id),(SELECT COUNT(*) FROM sealed_case_private_snapshots WHERE case_id=sealed_cases.id) FROM sealed_cases WHERE id=$id";
            command.Parameters.AddWithValue("$id", setup.Id);
            using var row = command.ExecuteReader();
            if (!row.Read()) throw new InvalidOperationException("Terminal anti-resurrection row missing");
            Equal("privacy_terminated", row.GetString(0)); Equal("{}", row.GetString(1)); Equal("{}", row.GetString(2));
            Equal(0L, row.GetInt64(3)); Equal(0L, row.GetInt64(4));
        }
        finally { Delete(path); }
    }

    internal static void WriteRestartFixture(string path)
    {
        var clock = new SealedCaseTimingTests.TimingClock { Now = 1_000 };
        var store = new SealedCaseStore(path, clock: clock);
        const string scenarioId = "HP_SPOLECENSTVI_KRBU_V1";
        var created = store.Create("restart-group", "p1", clock.Now, 8, "first", "restart-create", scenarioId: scenarioId);
        for (var i = 1; i <= 8; i++)
        {
            var account = "p" + i; var reference = "restart-snapshot-" + account;
            var profile = new CharacterProfile("restart-profile-" + account, 1, new("Pilot " + account, "Test"), "ready", null, null, clock.Now);
            store.Append(created.Id, created.GroupId, account, "lobby", "restart-ready-" + account, i, clock.Now,
                new SealedCaseLobbyEntry(account, true, true, reference), lobbyCapacity: 8,
                capturePrivateSnapshot: () => SealedCasePrivateSnapshot.From(reference, account, profile, null));
        }
        var start = store.StartTimeline(created.Id, created.GroupId, "p1", "restart-start", store.Read(created.Id).Revision);
        var game = SealedScenario.Create(store.ReadRoster(created.Id, created.GroupId), "first",
            SealedScenarioCatalog.FromSetup(System.Text.Json.JsonDocument.Parse(created.SetupJson).RootElement));
        var initialized = store.InitializeWorld(created.Id, created.GroupId, start.Revision, clock.Now, game);
        clock.Now = start.Timing.IntroductionEndsAt;
        var confirmation = new SealedPlanConfirmation(1, 0, true, SealedIntent.Guard);
        var plan = store.ConfirmPlan(created.Id, created.GroupId, "p1", "restart-plan-ch1", initialized.Revision, confirmation);
        var confirmed = store.ReadOwnPlan(created.Id, created.GroupId, "p1", 1).Action!;
        var retry = store.Create("restart-group", "p1", clock.Now, 8, "first", "restart-create", scenarioId: scenarioId);
        if (retry.Id != created.Id || retry.SetupJson != created.SetupJson) throw new InvalidOperationException("Create retry changed the initialized pilot case.");
        File.WriteAllText(path + ".expected.json", System.Text.Json.JsonSerializer.Serialize(new
        {
            id = created.Id, setupJson = created.SetupJson, groupId = created.GroupId, eventId = plan.EventId, revision = plan.Revision,
            action = confirmed, start = start.Timing, introEndsAt = start.Timing.IntroductionEndsAt,
            chapterOneQuietEndsAt = start.Timing.IntroductionEndsAt + SealedCaseRules.Schedule("first", 8).PlanningSeconds + SealedCaseRules.Schedule("first", 8).QuietSeconds
        }));
        Console.WriteLine("PASS restart writer: created current eight-player Krb short case and committed a confirmed chapter-one self-Guard plan.");
    }

    internal static void VerifyRestartFixture(string path)
    {
        using var expected = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path + ".expected.json"));
        var root = expected.RootElement;
        var id = root.GetProperty("id").GetString()!; var groupId = root.GetProperty("groupId").GetString()!;
        var clock = new SealedCaseTimingTests.TimingClock { Now = root.GetProperty("introEndsAt").GetInt64() };
        var store = new SealedCaseStore(path, clock: clock);
        var world = store.ReadWorld(id, groupId);
        Equal(0, world.LastChapter);
        using var manifest = System.Text.Json.JsonDocument.Parse(world.ContentVersionJson);
        Equal("1.1.0", manifest.RootElement.GetProperty("rulesVersion").GetString());
        Equal("HP_SPOLECENSTVI_KRBU_V1", manifest.RootElement.GetProperty("scenarioId").GetString());
        var ownPlan = store.ReadOwnPlan(id, groupId, "p1", 1);
        Equal(SealedIntent.Guard, ownPlan.Action!.Intent); Equal(null, ownPlan.Action.TargetId);
        Equal(root.GetProperty("action").GetRawText(), System.Text.Json.JsonSerializer.Serialize(ownPlan.Action));
        Equal(root.GetProperty("revision").GetInt64(), world.Revision);
        var setup = store.Create(groupId, "p1", clock.Now, 8, "first", "restart-create", scenarioId: "HP_SPOLECENSTVI_KRBU_V1");
        Equal(id, setup.Id); Equal(root.GetProperty("setupJson").GetString(), setup.SetupJson);
        var replay = store.ConfirmPlan(id, groupId, "p1", "restart-plan-ch1", root.GetProperty("revision").GetInt64() - 1,
            new SealedPlanConfirmation(1, 0, true, SealedIntent.Guard));
        Equal(true, replay.Replayed); Equal(root.GetProperty("eventId").GetString(), replay.EventId);

        clock.Now = root.GetProperty("chapterOneQuietEndsAt").GetInt64();
        var chapterOne = store.ResolveConfirmedChapter(id, groupId, 1, store.Read(id).Revision);
        Equal(1, chapterOne.Chapter);
        var chapterOneRetry = store.ResolveConfirmedChapter(id, groupId, 1, chapterOne.Revision - 1);
        Equal(true, chapterOneRetry.Replayed); Equal(System.Text.Json.JsonSerializer.Serialize(chapterOne.Events), System.Text.Json.JsonSerializer.Serialize(chapterOneRetry.Events));
        var secondPlanning = store.ReadTimeline(id, groupId);
        clock.Now = secondPlanning.IntroductionEndsAt + SealedCaseRules.Schedule("first", 8).PlanningSeconds + SealedCaseRules.Schedule("first", 8).QuietSeconds;
        var chapterTwoPlan = store.ConfirmPlan(id, groupId, "p1", "restart-plan-ch2", chapterOne.Revision,
            new SealedPlanConfirmation(2, 0, true, SealedIntent.Investigate));
        clock.Now = secondPlanning.PlanningEndsAt;
        var chapterTwoResolution = store.ResolveConfirmedChapter(id, groupId, 2, chapterTwoPlan.Revision);
        Equal(2, chapterTwoResolution.Chapter);
        var after = store.ReadWorld(id, groupId);
        Equal(2, after.LastChapter); Equal(SealedIntent.Investigate, store.ReadOwnPlan(id, groupId, "p1", 2).Action!.Intent);
        File.WriteAllText(path + ".events.json", System.Text.Json.JsonSerializer.Serialize(store.ReadWorldHistory(id, groupId).SelectMany(r => r.Events)));
        Console.WriteLine("PASS restart reader: restored confirmed plan, replayed create/plan/resolve idempotently, then resolved chapter two.");
    }

    private static string TempPath() => Path.Combine(Path.GetTempPath(), "sealed-case-" + Guid.NewGuid().ToString("N") + ".sqlite");
    private static void Delete(string path)
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var file in new[] { path, path + "-wal", path + "-shm" })
        {
            if (File.Exists(file)) { File.Delete(file); }
        }
    }
    private static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"SealedCase HTTP/store: expected {expected}, got {actual}"); }
    private static void Throws<T>(Action action, string code) where T : Exception
    {
        try { action(); } catch (T error) when (error is SealedCasePersistenceException persistence && persistence.Code == code) { return; }
        throw new InvalidOperationException($"SealedCase HTTP/store: expected {code}");
    }
}
