using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Kufr.Backend.Features.Customers;
using Kufr.Backend.Features.Identity;
using Kufr.Backend.Features.SealedCase;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

internal static partial class SealedCaseHttpTests
{
    internal static async Task FrozenPrivacyAndLegacyAcceptanceAsync()
    {
        await FrozenContentAndRestartableErasureAsync();
        await LegacyReadyRequiresExplicitReselectionAsync();
        await CaptureWaitAcrossEditAsync();
        await FreezeWaitAcrossPeerDeletionAsync();
        await DisabledFeatureStillErasesAsync();
        await PeerDeletionDeniesCreationReplayAsync();
        Console.WriteLine("PASS C1 frozen synthetic content, peer fences, failed erase/restart/ack, legacy reselect.");
    }

    private static async Task FrozenContentAndRestartableErasureAsync()
    {
        await using var fixture = await Fixture.StartAsync();
        var profiles = fixture.Services.GetRequiredService<CharacterProfileStore>();
        while (await profiles.ClaimAsync() is { } job)
        {
            Check(await profiles.CompleteAsync(job, new(job.UserInput.Name, job.UserInput.World,
                "Synthetic C1 enrichment, never production content.", [], ["Synthetic conversation"], [], false,
                [], "synthetic", "c1-regression", 1234, job.Revision, "synthetic"), null), "synthetic enrichment completes");
        }
        while (profiles.ClaimPortrait() is { } portrait)
        {
            Check(profiles.CompletePortrait(portrait, portrait.AccountId == "p1" ? [17, 23, 42] : null,
                portrait.AccountId == "p1" ? null : "synthetic_failure",
                portrait.AccountId == "p1" ? "Syntetický portrét pro C1." : null), "synthetic portrait completes");
        }
        await fixture.AdvanceCharacterAsync("p2"); // Explicit queued state.
        // Remove only this synthetic fixture asset to represent a profile with no portrait.
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = profiles.CustomerDataDatabasePath }.ToString()))
        {
            connection.Open(); using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM character_portraits WHERE account_id='p4'"; command.ExecuteNonQuery();
        }
        var selected = (await profiles.ReadAsync("p1"))!;
        var selectedPortrait = profiles.ReadPortrait("p1", selected.ProfileId, selected.Revision);
        using var owner = fixture.Client();
        await fixture.LoginAsync(owner, "p1"); await fixture.CsrfAsync(owner);
        var created = await owner.PostAsJsonAsync("/app/v1/sealed-cases",
            new { operationId = "privacy-create", expectedPlayers = 8, variant = "first" });
        created.EnsureSuccessStatusCode();
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        var cases = fixture.Services.GetRequiredService<SealedCaseStore>();
        var group = cases.Read(id).GroupId;
        long revision = 1;
        for (var i = 1; i <= 8; i++)
        {
            using var player = fixture.Client(); await fixture.LoginAsync(player, $"p{i}"); await fixture.CsrfAsync(player);
            Equal(HttpStatusCode.OK, (await player.PostAsJsonAsync($"/app/v1/sealed-cases/{id}/lobby",
                new { operationId = $"privacy-ready-{i}", expectedRevision = revision++, ready = true, consent = true })).StatusCode);
            if (i == 1) { await fixture.AdvanceCharacterAsync("p1"); }
        }
        var frozen = cases.ReadRoster(id, group);
        await fixture.AdvanceCharacterAsync("p1");
        var reopened = ReopenCases(fixture);
        var resolved = await ResolveAsync(fixture, reopened, id, group, "p1");
        Equal(JsonSerializer.Serialize(selected), JsonSerializer.Serialize(resolved.Profile));
        Equal("ready", resolved.Portrait!.Status);
        Equal(JsonSerializer.Serialize(selectedPortrait), JsonSerializer.Serialize(resolved.Portrait));
        Check(resolved.Image!.SequenceEqual(new byte[] { 17, 23, 42 }), "exact synthetic image survives edits/freeze/reopen");
        Equal(frozen.Members.Single(m => m.AccountId == "p1"), resolved.Member);
        Equal(frozen.ServerSeed, reopened.ReadRoster(id, group).ServerSeed);
        var queued = await ResolveAsync(fixture, reopened, id, group, "p2");
        Equal("queued", queued.Portrait!.Status); Check(queued.Image is null, "queued has no invented image");
        Equal("failed", (await ResolveAsync(fixture, reopened, id, group, "p3")).Portrait!.Status);
        var absent = await ResolveAsync(fixture, reopened, id, group, "p4");
        Check(absent.Portrait is null && absent.Image is null, "absent portrait stays absent");
        using var outsider = fixture.Client(); await fixture.LoginAsync(outsider, "p9");
        await ExpectDeniedAsync(() => ResolveAsync(fixture, reopened, id, group, "p9"));
        await ExpectDeniedAsync(() => ResolveAsync(fixture, reopened, id, "foreign-group", "p1"));

        // Preserve another case even when cleanup fails and is retried.
        var unaffected = cases.Create(group, "p10", 100, operationId: "unaffected-case");
        var cachedAuthority = cases.ReadRosterAuthority(id, group);
        var current = (await profiles.ReadAsync("p1"))!;
        await profiles.DeleteAsync("p1", current.Revision, current.ProfileId);
        var restartedProfiles = new CharacterProfileStore(fixture.DataRoot);
        var pending = await restartedProfiles.PendingDeletionsAsync();
        Equal(1, pending.Length);
        // Persisted fence precedes erase and blocks peer reads/replay, not only the owner.
        reopened = ReopenCases(fixture);
        ExpectDenied(() => reopened.Read(id));
        ExpectDenied(() => reopened.ReadRoster(id, group));
        ExpectDenied(() => reopened.ReadRosterAuthority(id, group));
        ExpectDenied(() => reopened.ReadWorld(id, group));
        ExpectDenied(() => reopened.ReadWorldHistory(id, group));
        ExpectDenied(() => reopened.ReadContext(id, group, "p2", SealedContextRole.Player, cachedAuthority));
        ExpectDenied(() => reopened.ReadReplay(id, group, "p2", "lobby", "privacy-ready-2", 2, "anything"));
        ExpectDenied(() => reopened.Append(id, group, "p2", "lobby", "after-fence", revision, 200,
            new SealedCaseLobbyEntry("p2", true, true)));
        await ExpectDeniedAsync(() => ResolveAsync(fixture, reopened, id, group, "p2"));
        using var peer = fixture.Client(); await fixture.LoginAsync(peer, "p2"); await fixture.CsrfAsync(peer);
        Equal(HttpStatusCode.NotFound, (await peer.GetAsync($"/app/v1/sealed-cases/{id}")).StatusCode);
        Equal(HttpStatusCode.NotFound, (await peer.PostAsJsonAsync($"/app/v1/sealed-cases/{id}/lobby",
            new { operationId = "privacy-ready-2", expectedRevision = 2, ready = true, consent = true })).StatusCode);

        // A deterministic failure after terminal update must roll the entire erase back.
        using (var connection = OpenCaseDatabase(fixture))
        {
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TRIGGER c1_fail_erase BEFORE DELETE ON sealed_case_private_snapshots BEGIN SELECT RAISE(ABORT,'synthetic erase interruption'); END";
            command.ExecuteNonQuery();
        }
        try
        {
            await CharacterEndpoints.ReconcileDeletionsAsync(fixture.Services, restartedProfiles, CancellationToken.None);
            throw new InvalidOperationException("Expected interrupted erase");
        }
        catch (SqliteException) { }
        Equal(1, (await restartedProfiles.PendingDeletionsAsync()).Length);
        using (var connection = OpenCaseDatabase(fixture))
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT status FROM sealed_cases WHERE id=$id";
            command.Parameters.AddWithValue("$id", id); Equal("lobby", (string)command.ExecuteScalar()!);
            command.CommandText = "DROP TRIGGER c1_fail_erase"; command.ExecuteNonQuery();
        }
        Equal(1, ReopenCases(fixture).EraseAccount("p1"));
        Equal(1, (await restartedProfiles.PendingDeletionsAsync()).Length); // Crash after erase, before ack.
        // Reconciliation has no OperationJournal in this real fixture.
        await CharacterEndpoints.ReconcileDeletionsAsync(fixture.Services, new CharacterProfileStore(fixture.DataRoot), CancellationToken.None);
        Equal(0, (await restartedProfiles.PendingDeletionsAsync()).Length);
        reopened = ReopenCases(fixture);
        ExpectDenied(() => reopened.Read(id));
        ExpectDenied(() => reopened.Create(group, "p1", 300, operationId: "privacy-create"));
        Equal(unaffected.Id, reopened.Read(unaffected.Id).Id);
        Equal(0, reopened.EraseAccount("p1"));
        using (var connection = OpenCaseDatabase(fixture))
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT status,setup_json,lobby_json FROM sealed_cases WHERE id=$id";
            command.Parameters.AddWithValue("$id", id);
            using (var row = command.ExecuteReader())
            {
                Check(row.Read(), "terminal anti-resurrection marker exists");
                Equal("privacy_terminated", row.GetString(0)); Equal("{}", row.GetString(1)); Equal("{}", row.GetString(2));
            }
            foreach (var table in new[] { "sealed_case_events", "sealed_case_rosters", "sealed_case_private_snapshots", "sealed_case_world", "sealed_case_resolutions" })
            {
                command.CommandText = $"SELECT COUNT(*) FROM {table} WHERE case_id=$id";
                Equal(0L, (long)command.ExecuteScalar()!);
            }
        }
        // Finished deletion permits a deliberate new character/case, never resurrection of the old selection.
        await profiles.SaveAsync("p1", new(0, new("Nová syntetická postava", "Test"), OpenAiConsent: true));
        Equal("setup", reopened.Create(group, "p1", 301, operationId: "fresh-after-delete").Status);
        ExpectDenied(() => reopened.Create(group, "p1", 302, operationId: "privacy-create"));
        var newcomer = (await profiles.ReadAsync("p11"))!;
        await profiles.DeleteAsync("p11", newcomer.Revision, newcomer.ProfileId);
        await profiles.SaveAsync("p11", new(0, new("Nový profil před cleanup", "Test"), OpenAiConsent: true));
        ExpectDenied(() => reopened.Append(unaffected.Id, group, "p11", "lobby", "pending-newcomer", 1, 303,
            new SealedCaseLobbyEntry("p11", true, true)));
        ExpectDenied(() => reopened.ReadReplay(unaffected.Id, group, "p11", "lobby", "pending-newcomer", 1, "synthetic"));
        await CharacterEndpoints.ReconcileDeletionsAsync(fixture.Services, profiles, CancellationToken.None);
        Equal(unaffected.Id, reopened.Read(unaffected.Id).Id);
        using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AccountDatabase>();
            db.DeletionRequests.Add(new AccountDeletionRequest { UserId = "p10", ReceiptId = "synthetic-account-delete", RequestedAt = 400 });
            await db.SaveChangesAsync();
        }
        // Identity fence denies even a setup owner with no snapshot or character tombstone.
        ExpectDenied(() => ReopenCases(fixture).Read(unaffected.Id));
    }

    private static async Task LegacyReadyRequiresExplicitReselectionAsync()
    {
        await using var fixture = await Fixture.StartAsync();
        using var owner = fixture.Client(); await fixture.LoginAsync(owner, "p1"); await fixture.CsrfAsync(owner);
        var response = await owner.PostAsJsonAsync("/app/v1/sealed-cases", new { operationId = "legacy-case", expectedPlayers = 8, variant = "first" });
        response.EnsureSuccessStatusCode();
        var id = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        var cases = fixture.Services.GetRequiredService<SealedCaseStore>(); var group = cases.Read(id).GroupId;
        for (var i = 1; i <= 8; i++)
        {
            cases.Append(id, group, $"p{i}", "lobby", $"legacy-ready-{i}", i, 100 + i,
                new SealedCaseLobbyEntry($"p{i}", true, true));
        }
        var reopened = ReopenCases(fixture);
        Equal(17L, reopened.Read(id).Revision); // Eight durable invalidations, no implicit captures.
        Equal(17L, ReopenCases(fixture).Read(id).Revision);
        var view = await owner.GetFromJsonAsync<JsonElement>($"/app/v1/sealed-cases/{id}");
        Equal(0, view.GetProperty("readyPlayers").GetInt32()); Equal(false, view.GetProperty("ownReady").GetBoolean());
        Equal("sealed_case_selection_required", view.GetProperty("startBlocker").GetString());
        long revision = 17;
        // A ninth member can explicitly select; legacy ready entries do not consume capacity.
        for (var i = 2; i <= 9; i++)
        {
            using var player = fixture.Client(); await fixture.LoginAsync(player, $"p{i}"); await fixture.CsrfAsync(player);
            Equal(HttpStatusCode.OK, (await player.PostAsJsonAsync($"/app/v1/sealed-cases/{id}/lobby",
                new { operationId = $"fresh-legacy-{i}", expectedRevision = revision++, ready = true, consent = true })).StatusCode);
        }
        var frozen = ReopenCases(fixture).ReadRoster(id, group);
        Equal(8, frozen.Members.Length); Check(frozen.Members.All(m => m.SnapshotRef is not null && m.AccountId != "p1"), "only new explicit selections freeze");
        Equal(revision, ReopenCases(fixture).Read(id).Revision);
    }

    private static async Task CaptureWaitAcrossEditAsync()
    {
        await using var fixture = await Fixture.StartAsync();
        using var owner = fixture.Client(); await fixture.LoginAsync(owner, "p1"); await fixture.CsrfAsync(owner);
        var response = await owner.PostAsJsonAsync("/app/v1/sealed-cases", new { operationId = "edit-wait-create", expectedPlayers = 8, variant = "first" });
        response.EnsureSuccessStatusCode();
        var id = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        var path = $"/app/v1/sealed-cases/{id}/lobby";
        using var blocker = OpenCaseDatabase(fixture);
        using var held = blocker.BeginTransaction(deferred: false);
        var observed = fixture.WatchWrite(path);
        var pending = owner.PostAsJsonAsync(path, new { operationId = "edit-wait-ready", expectedRevision = 1, ready = true, consent = true });
        try
        {
            await observed.WaitAsync(TimeSpan.FromSeconds(5));
            await fixture.AdvanceCharacterAsync("p1").WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { held.Commit(); }
        Equal(HttpStatusCode.OK, (await pending.WaitAsync(TimeSpan.FromSeconds(10))).StatusCode);
        using var query = blocker.CreateCommand();
        query.CommandText = "SELECT profile_json FROM sealed_case_private_snapshots WHERE case_id=$case AND subject_account='p1'";
        query.Parameters.AddWithValue("$case", id);
        var captured = JsonSerializer.Deserialize<CharacterProfile>((string)query.ExecuteScalar()!, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Equal(JsonSerializer.Serialize(await fixture.CharacterAsync("p1")), JsonSerializer.Serialize(captured));
        Equal(2L, captured.Revision);
    }

    private static async Task FreezeWaitAcrossPeerDeletionAsync()
    {
        await using var fixture = await Fixture.StartAsync();
        using var owner = fixture.Client(); await fixture.LoginAsync(owner, "p1"); await fixture.CsrfAsync(owner);
        var response = await owner.PostAsJsonAsync("/app/v1/sealed-cases", new { operationId = "delete-wait-create", expectedPlayers = 8, variant = "first" });
        response.EnsureSuccessStatusCode();
        var id = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        for (var i = 1; i <= 7; i++)
        {
            using var player = fixture.Client(); await fixture.LoginAsync(player, $"p{i}"); await fixture.CsrfAsync(player);
            Equal(HttpStatusCode.OK, (await player.PostAsJsonAsync($"/app/v1/sealed-cases/{id}/lobby",
                new { operationId = $"delete-wait-ready-{i}", expectedRevision = i, ready = true, consent = true })).StatusCode);
        }
        using var last = fixture.Client(); await fixture.LoginAsync(last, "p8"); await fixture.CsrfAsync(last);
        var path = $"/app/v1/sealed-cases/{id}/lobby";
        using var blocker = OpenCaseDatabase(fixture);
        using var held = blocker.BeginTransaction(deferred: false);
        var observed = fixture.WatchWrite(path);
        var pending = last.PostAsJsonAsync(path, new { operationId = "delete-wait-eighth", expectedRevision = 8, ready = true, consent = true });
        try
        {
            await observed.WaitAsync(TimeSpan.FromSeconds(5));
            var profiles = fixture.Services.GetRequiredService<CharacterProfileStore>();
            var current = (await profiles.ReadAsync("p1"))!;
            await profiles.DeleteAsync("p1", current.Revision, current.ProfileId).WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { held.Commit(); }
        Equal(HttpStatusCode.NotFound, (await pending.WaitAsync(TimeSpan.FromSeconds(10))).StatusCode);
        using var query = blocker.CreateCommand();
        query.CommandText = "SELECT revision,(SELECT COUNT(*) FROM sealed_case_rosters WHERE case_id=$case) FROM sealed_cases WHERE id=$case";
        query.Parameters.AddWithValue("$case", id);
        using var row = query.ExecuteReader(); Check(row.Read(), "fenced case exists pending cleanup");
        Equal(8L, row.GetInt64(0)); Equal(0L, row.GetInt64(1));
    }

    private static async Task DisabledFeatureStillErasesAsync()
    {
        await using var fixture = await Fixture.StartAsync();
        using var owner = fixture.Client(); await fixture.LoginAsync(owner, "p1"); await fixture.CsrfAsync(owner);
        var created = await owner.PostAsJsonAsync("/app/v1/sealed-cases", new { operationId = "disabled-erase", expectedPlayers = 8, variant = "first" });
        created.EnsureSuccessStatusCode();
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync($"/app/v1/sealed-cases/{id}/lobby",
            new { operationId = "disabled-ready", expectedRevision = 1, ready = true, consent = true })).StatusCode);
        var profiles = fixture.Services.GetRequiredService<CharacterProfileStore>();
        var selected = (await profiles.ReadAsync("p1"))!;
        await profiles.DeleteAsync("p1", selected.Revision, selected.ProfileId);
        // Simulate a restart with feature/journal services absent, while account data root remains.
        using var disabledServices = new ServiceCollection()
            .AddSingleton(fixture.Services.GetRequiredService<AccountConfiguration>()).BuildServiceProvider();
        await CharacterEndpoints.ReconcileDeletionsAsync(disabledServices,
            new CharacterProfileStore(fixture.DataRoot), CancellationToken.None);
        Equal(0, (await profiles.PendingDeletionsAsync()).Length);
        ExpectDenied(() => new SealedCaseStore(Path.Combine(fixture.DataRoot, "sealed-case.sqlite")).Read(id));
    }

    private static async Task PeerDeletionDeniesCreationReplayAsync()
    {
        await using var fixture = await Fixture.StartAsync();
        var cases = fixture.Services.GetRequiredService<SealedCaseStore>();
        var setup = cases.Create("synthetic-peer-group", "p1", 100, operationId: "peer-fenced-create");
        cases.Append(setup.Id, setup.GroupId, "p2", "lobby", "peer-left-lobby", 1, 101,
            new SealedCaseLobbyEntry("p2", false, false));
        var profiles = fixture.Services.GetRequiredService<CharacterProfileStore>();
        var peer = (await profiles.ReadAsync("p2"))!;
        await profiles.DeleteAsync("p2", peer.Revision, peer.ProfileId);
        ExpectDenied(() => ReopenCases(fixture).Create(setup.GroupId, "p1", 102, operationId: "peer-fenced-create"));
    }

    private static SealedCaseStore ReopenCases(Fixture fixture)
    {
        using var scope = fixture.Services.CreateScope();
        return new(Path.Combine(fixture.DataRoot, "sealed-case.sqlite"), new SealedCasePrivacyAuthority(
            scope.ServiceProvider.GetRequiredService<AccountDatabase>().Database.GetConnectionString()!,
            new CharacterProfileStore(fixture.DataRoot)));
    }

    private static async Task<SealedFrozenCharacter> ResolveAsync(Fixture fixture, SealedCaseStore cases,
        string id, string group, string account)
    {
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AccountDatabase>();
        var user = await db.Users.AsNoTracking().SingleAsync(u => u.Id == account);
        var session = await db.Sessions.AsNoTracking().FirstAsync(s => s.UserId == account);
        return cases.ResolveFrozenCharacter(id, group, account, () => CustomerAdmissionGuard.AcquireCookie(
            db.Database.GetConnectionString()!, fixture.Services.GetRequiredService<CharacterProfileStore>(),
            TimeProvider.System, account, session.Id, user.SecurityStamp!, CancellationToken.None));
    }

    private static SqliteConnection OpenCaseDatabase(Fixture fixture)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(fixture.DataRoot, "sealed-case.sqlite") }.ToString());
        connection.Open(); return connection;
    }

    private static void ExpectDenied(Action action)
    {
        try { action(); }
        catch (SealedCasePersistenceException error) when (error.Code == "case_not_found") { return; }
        throw new InvalidOperationException("Expected case privacy denial");
    }

    private static async Task ExpectDeniedAsync(Func<Task<SealedFrozenCharacter>> action)
    {
        try { await action(); }
        catch (SealedCasePersistenceException error) when (error.Code == "case_not_found") { return; }
        throw new InvalidOperationException("Expected frozen content denial");
    }
}
