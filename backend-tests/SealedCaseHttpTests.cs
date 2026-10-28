using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Kufr.Backend.Features.Game;
using Kufr.Backend.Features.Customers;
using Kufr.Backend.Features.Identity;
using Kufr.Backend.Features.Launch;
using Kufr.Backend.Features.SealedCase;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

internal static partial class SealedCaseHttpTests
{
    internal static async Task RunAsync()
    {
        await FrozenPrivacyAndLegacyAcceptanceAsync();
        await PollingDoesNotConsumeAccountBudgetAsync();
        await CapacityRejectsThirtyFirstReadyAsync();
        await RevocationWhileLobbyWriteWaitsAsync();
        await RevokedMemberDoesNotFreezeRosterAsync();
        await ReplayReceiptSurvivesFullLobbyAsync();
        await using var fixture = await Fixture.StartAsync();
        using var anonymous = fixture.Client();
        Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/app/v1/sealed-cases/unknown")).StatusCode);
        using var owner = fixture.Client(); await fixture.LoginAsync(owner, "p1");
        var create = new { operationId = "create-one", expectedPlayers = 8, variant = "first", actorId = "p2", groupId = "foreign" };
        Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync("/app/v1/sealed-cases", create)).StatusCode);
        await fixture.CsrfAsync(owner);
        Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync("/app/v1/sealed-cases",
            new { operationId = "below-player-limit", expectedPlayers = 7, variant = "full" })).StatusCode);
        Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync("/app/v1/sealed-cases",
            new { operationId = "above-player-limit", expectedPlayers = 31, variant = "full" })).StatusCode);
        using var response = await owner.PostAsJsonAsync("/app/v1/sealed-cases", create);
        Equal(HttpStatusCode.Created, response.StatusCode);
        var view = await response.Content.ReadFromJsonAsync<JsonElement>(); var id = view.GetProperty("id").GetString()!;
        Equal("p1", view.GetProperty("playerId").GetString());
        Equal("setup", view.GetProperty("status").GetString());
        var again = await (await owner.PostAsJsonAsync("/app/v1/sealed-cases", create)).Content.ReadFromJsonAsync<JsonElement>();
        Equal(id, again.GetProperty("id").GetString());
        Equal(HttpStatusCode.Conflict, (await owner.PostAsJsonAsync("/app/v1/sealed-cases", new { operationId = "create-one", expectedPlayers = 9, variant = "first" })).StatusCode);
        Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync("/app/v1/sealed-cases", new { operationId = "bad-variant", expectedPlayers = 13, variant = "first" })).StatusCode);
        using var foreign = fixture.Client(); await fixture.LoginAsync(foreign, "foreign");
        Equal(HttpStatusCode.NotFound, (await foreign.GetAsync($"/app/v1/sealed-cases/{id}")).StatusCode);
        using var peer = fixture.Client(); await fixture.LoginAsync(peer, "p2"); await fixture.CsrfAsync(peer);
        Equal(HttpStatusCode.Forbidden, (await peer.PostAsJsonAsync($"/app/v1/sealed-cases/{id}/start", new { operationId = "peer-start", expectedRevision = 1 })).StatusCode);
        Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync($"/app/v1/sealed-cases/{id}/lobby", new { operationId = "no-consent", expectedRevision = 1, ready = true, consent = false })).StatusCode);
        var join = new { operationId = "ready-p1", expectedRevision = 1, ready = true, consent = true, actorId = "p2" };
        var selectedBeforeEdit = (await fixture.CharacterAsync("p1"))!;
        Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync($"/app/v1/sealed-cases/{id}/lobby", join)).StatusCode);
        await fixture.AdvanceCharacterAsync("p1");
        using (var privateCopy = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(fixture.DataRoot, "sealed-case.sqlite") }.ToString()))
        {
            privateCopy.Open(); using var query = privateCopy.CreateCommand();
            query.CommandText = "SELECT profile_id,profile_revision,profile_json FROM sealed_case_private_snapshots WHERE case_id=$case AND subject_account='p1'";
            query.Parameters.AddWithValue("$case", id); using var row = query.ExecuteReader();
            Check(row.Read() && row.GetString(0) == selectedBeforeEdit.ProfileId && row.GetInt64(1) == selectedBeforeEdit.Revision
                && JsonDocument.Parse(row.GetString(2)).RootElement.GetProperty("userInput").GetProperty("name").GetString() == selectedBeforeEdit.UserInput.Name,
                "profile edit after readiness leaves the exact selected profile snapshot resolvable before freeze");
        }
        var retry = await (await owner.PostAsJsonAsync($"/app/v1/sealed-cases/{id}/lobby", join)).Content.ReadFromJsonAsync<JsonElement>();
        Equal(true, retry.GetProperty("replayed").GetBoolean());
        Equal(1, new SealedCaseStore(Path.Combine(fixture.DataRoot, "sealed-case.sqlite")).Read(id).Events.Count);
        Equal(HttpStatusCode.Conflict, (await owner.PostAsJsonAsync($"/app/v1/sealed-cases/{id}/lobby",
            new { operationId = "ready-p1", expectedRevision = 1, ready = false, consent = true })).StatusCode);
        var current = await owner.GetFromJsonAsync<JsonElement>($"/app/v1/sealed-cases/{id}");
        Equal(2L, current.GetProperty("revision").GetInt64()); Equal(true, current.GetProperty("ownReady").GetBoolean());
        Equal(1, current.GetProperty("readyPlayers").GetInt32());
        Equal(HttpStatusCode.Conflict, (await owner.PostAsJsonAsync($"/app/v1/sealed-cases/{id}/lobby", new { operationId = "stale-order", expectedRevision = 1, ready = false, consent = false })).StatusCode);
        var revision = 2L;
        for (var i = 2; i <= 8; i++)
        {
            using var player = fixture.Client(); await fixture.LoginAsync(player, $"p{i}"); await fixture.CsrfAsync(player);
            var ready = await player.PostAsJsonAsync($"/app/v1/sealed-cases/{id}/lobby", new { operationId = $"ready-p{i}", expectedRevision = revision, ready = true, consent = true });
            Equal(HttpStatusCode.OK, ready.StatusCode); revision++;
        }
        var complete = await owner.GetFromJsonAsync<JsonElement>($"/app/v1/sealed-cases/{id}");
        Equal(8, complete.GetProperty("readyPlayers").GetInt32());
        using (var ninth = fixture.Client())
        {
            await fixture.LoginAsync(ninth, "p9"); await fixture.CsrfAsync(ninth);
            var overflow = await ninth.PostAsJsonAsync($"/app/v1/sealed-cases/{id}/lobby", new { operationId = "ninth-at-eight", expectedRevision = revision, ready = true, consent = true });
            Equal(HttpStatusCode.Conflict, overflow.StatusCode);
        }
        Equal(8, (await owner.GetFromJsonAsync<JsonElement>($"/app/v1/sealed-cases/{id}")).GetProperty("readyPlayers").GetInt32());
        Equal(false, complete.GetProperty("canStart").GetBoolean()); // legacy selections lack explicit scenario consent
        var blocked = await owner.PostAsJsonAsync($"/app/v1/sealed-cases/{id}/start", new { operationId = "start-one", expectedRevision = revision });
        Equal(HttpStatusCode.Conflict, blocked.StatusCode);
        var problem = await blocked.Content.ReadAsStringAsync(); Check(problem.Contains("sealed_case_scenario_consent_required", StringComparison.Ordinal), "explicit scenario consent required");
        Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync($"/app/v1/sealed-cases/{id}/actions", new { operationId = "order", expectedRevision = revision, intent = "DREAM" })).StatusCode);
        var persisted = new SealedCaseStore(Path.Combine(fixture.DataRoot, "sealed-case.sqlite")).Read(id);
        Equal(revision, persisted.Revision); Equal("lobby", persisted.Status);
        var json = await (await owner.GetAsync($"/app/v1/sealed-cases/{id}?viewerId=p2")).Content.ReadAsStringAsync();
        Check(!json.Contains("setupJson", StringComparison.Ordinal) && !json.Contains("payload", StringComparison.Ordinal) && !json.Contains("events", StringComparison.Ordinal), "no raw server journal");
        using var doc = JsonDocument.Parse(json); Equal("p1", doc.RootElement.GetProperty("playerId").GetString());
        await fixture.RevokeAsync("p1");
        Check((await owner.GetAsync($"/app/v1/sealed-cases/{id}")).StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized, "live revocation denies read");
        await using var disabled = await Fixture.StartAsync(enabled: false);
        using var disabledClient = disabled.Client(); await disabled.LoginAsync(disabledClient, "p1");
        Equal(HttpStatusCode.NotFound, (await disabledClient.GetAsync("/app/v1/sealed-cases/unknown")).StatusCode);
        Check(!File.Exists(Path.Combine(disabled.DataRoot, "sealed-case.sqlite")), "disabled feature has no persistent side effects");
        Console.WriteLine("SealedCase real HTTPS account/CSRF/group isolation and durable lobby and explicit scenario-consent checks passed.");
    }

    private static async Task RevokedMemberDoesNotFreezeRosterAsync()
    {
        await using var fixture = await Fixture.StartAsync();
        using var owner = fixture.Client(); await fixture.LoginAsync(owner, "p1"); await fixture.CsrfAsync(owner);
        var created = await owner.PostAsJsonAsync("/app/v1/sealed-cases", new { operationId = "revoke-roster-create", expectedPlayers = 8, variant = "first" });
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        long revision = 1;
        for (var i = 1; i <= 7; i++)
        {
            using var player = fixture.Client(); await fixture.LoginAsync(player, $"p{i}"); await fixture.CsrfAsync(player);
            Equal(HttpStatusCode.OK, (await player.PostAsJsonAsync($"/app/v1/sealed-cases/{id}/lobby", new { operationId = $"revoke-ready-{i}", expectedRevision = revision++, ready = true, consent = true })).StatusCode);
        }
        await fixture.RevokeAsync("p7");
        using (var p8 = fixture.Client())
        {
            await fixture.LoginAsync(p8, "p8"); await fixture.CsrfAsync(p8);
            Equal(HttpStatusCode.OK, (await p8.PostAsJsonAsync($"/app/v1/sealed-cases/{id}/lobby", new { operationId = "revoke-ready-8", expectedRevision = revision++, ready = true, consent = true })).StatusCode);
        }
        using (var p9 = fixture.Client())
        {
            await fixture.LoginAsync(p9, "p9"); await fixture.CsrfAsync(p9);
            Equal(HttpStatusCode.OK, (await p9.PostAsJsonAsync($"/app/v1/sealed-cases/{id}/lobby", new { operationId = "revoke-ready-9", expectedRevision = revision, ready = true, consent = true })).StatusCode);
        }
        var store = new SealedCaseStore(Path.Combine(fixture.DataRoot, "sealed-case.sqlite"));
        var frozenCase = store.Read(id);
        Check(store.ReadRoster(id, frozenCase.GroupId).Members.All(m => m.AccountId != "p7"), "revoked pre-freeze account excluded from roster");
    }

    private static async Task ReplayReceiptSurvivesFullLobbyAsync()
    {
        await using var fixture = await Fixture.StartAsync();
        using var owner = fixture.Client(); await fixture.LoginAsync(owner, "p1"); await fixture.CsrfAsync(owner);
        var created = await owner.PostAsJsonAsync("/app/v1/sealed-cases", new { operationId = "replay-full-create", expectedPlayers = 8, variant = "first" });
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        var original = new { operationId = "replay-full-p1", expectedRevision = 1, ready = true, consent = true };
        var receipt = await (await owner.PostAsJsonAsync($"/app/v1/sealed-cases/{id}/lobby", original)).Content.ReadFromJsonAsync<JsonElement>();
        Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync($"/app/v1/sealed-cases/{id}/lobby", new { operationId = "replay-full-unready", expectedRevision = 2, ready = false, consent = true })).StatusCode);
        long revision = 3;
        for (var i = 2; i <= 9; i++)
        {
            using var player = fixture.Client(); await fixture.LoginAsync(player, $"p{i}"); await fixture.CsrfAsync(player);
            Equal(HttpStatusCode.OK, (await player.PostAsJsonAsync($"/app/v1/sealed-cases/{id}/lobby", new { operationId = $"replay-full-ready-{i}", expectedRevision = revision++, ready = true, consent = true })).StatusCode);
        }
        var replayResponse = await owner.PostAsJsonAsync($"/app/v1/sealed-cases/{id}/lobby", original);
        Equal(HttpStatusCode.OK, replayResponse.StatusCode);
        var replay = await replayResponse.Content.ReadFromJsonAsync<JsonElement>();
        Equal(true, replay.GetProperty("replayed").GetBoolean()); Equal(receipt.GetProperty("eventId").GetString(), replay.GetProperty("eventId").GetString());
        Equal(revision, new SealedCaseStore(Path.Combine(fixture.DataRoot, "sealed-case.sqlite")).Read(id).Revision);
    }

    private static async Task CapacityRejectsThirtyFirstReadyAsync()
    {
        await using var fixture = await Fixture.StartAsync();
        using var owner = fixture.Client(); await fixture.LoginAsync(owner, "p1"); await fixture.CsrfAsync(owner);
        var created = await owner.PostAsJsonAsync("/app/v1/sealed-cases", new { operationId = "capacity-create", expectedPlayers = 30, variant = "full" });
        Equal(HttpStatusCode.Created, created.StatusCode);
        var view = await created.Content.ReadFromJsonAsync<JsonElement>(); var id = view.GetProperty("id").GetString()!;
        using (var unconfigured = fixture.Client())
        {
            await fixture.LoginAsync(unconfigured, "p32"); await fixture.CsrfAsync(unconfigured);
            var missingCharacter = await unconfigured.PostAsJsonAsync($"/app/v1/sealed-cases/{id}/lobby",
                new { operationId = "capacity-no-character", expectedRevision = 1, ready = true, consent = true });
            Equal(HttpStatusCode.Conflict, missingCharacter.StatusCode);
            Check((await missingCharacter.Content.ReadAsStringAsync()).Contains("sealed_case_character_required", StringComparison.Ordinal), "ready requires the account's existing character");
        }
        long revision = 1;
        for (var i = 1; i <= 30; i++)
        {
            using var player = fixture.Client(); await fixture.LoginAsync(player, $"p{i}"); await fixture.CsrfAsync(player);
            var response = await player.PostAsJsonAsync($"/app/v1/sealed-cases/{id}/lobby",
                new { operationId = $"capacity-ready-{i}", expectedRevision = revision, ready = true, consent = true });
            Equal(HttpStatusCode.OK, response.StatusCode); revision++;
        }
        var full = await owner.GetFromJsonAsync<JsonElement>($"/app/v1/sealed-cases/{id}");
        Equal(30, full.GetProperty("readyPlayers").GetInt32());
        var persisted = new SealedCaseStore(Path.Combine(fixture.DataRoot, "sealed-case.sqlite")).Read(id);
        Equal(revision, persisted.Revision);
        var durableStore = new SealedCaseStore(Path.Combine(fixture.DataRoot, "sealed-case.sqlite"));
        var roster = durableStore.ReadRoster(id, persisted.GroupId);
        Equal(30, roster.Members.Length);
        Equal(30, roster.Members.Select(x => x.AccountId).Distinct(StringComparer.Ordinal).Count());
        Check(roster.Members.All(x => x.ProfileRevision == 1 && !string.IsNullOrWhiteSpace(x.ProfileId)), "roster freezes current profile references");
        Equal(SealedCaseRules.Conspirators(30), roster.Members.Count(x => x.Team == SealedSecretTeam.Conspirator));
        Equal(30, roster.Members.Select(x => x.SeatId).Distinct(StringComparer.Ordinal).Count());
        var rosterAgain = new SealedCaseStore(Path.Combine(fixture.DataRoot, "sealed-case.sqlite")).ReadRoster(id, persisted.GroupId);
        Equal(roster.ServerSeed, rosterAgain.ServerSeed); Equal(JsonSerializer.Serialize(roster.Members), JsonSerializer.Serialize(rosterAgain.Members));
        var grants = durableStore.ReadRosterAuthority(id, persisted.GroupId);
        Equal(30, grants.Members.Length); Check(grants.Members.All(x => x.Roles.Contains(SealedContextRole.Player)), "frozen roster issues player grants");
        Check(HasExactProperties(full, "id", "playerId", "status", "revision", "variant", "expectedPlayers", "readyPlayers",
            "ownReady", "canManage", "canStart", "startBlocker", "serverNow", "chapters", "planningSeconds", "quietSeconds"),
            "HTTP lobby wire projection contains only its public allowlist");
        Equal(id, full.GetProperty("id").GetString());
        Equal("p1", full.GetProperty("playerId").GetString());
        Check(!JsonSerializer.Serialize(full).Contains(roster.ServerSeed, StringComparison.Ordinal), "server seed is not in lobby projection");
        var publicSeats = SealedCaseRosterAssignments.Public(
            roster.Members.Select(member => new SealedRosterAssignment(member.PlayerId, member.SeatId, member.Kit, member.Team)).ToArray());
        var publicSeatsJson = JsonSerializer.Serialize(publicSeats, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using var publicSeatsDocument = JsonDocument.Parse(publicSeatsJson);
        var publicSeatElements = publicSeatsDocument.RootElement.EnumerateArray().ToArray();
        Equal(roster.Members.Length, publicSeatElements.Length);
        for (var index = 0; index < publicSeatElements.Length; index++)
        {
            var seat = publicSeatElements[index];
            Check(HasExactProperties(seat, "playerId", "seatId", "kit"), "public seat wire projection contains only its public allowlist");
            Equal(roster.Members[index].PlayerId, seat.GetProperty("playerId").GetString());
            Equal(roster.Members[index].SeatId, seat.GetProperty("seatId").GetString());
            Check(seat.GetProperty("kit").ValueKind == JsonValueKind.Number, "legitimate public kit survives wire projection");
        }
        Check(!publicSeatsJson.Contains(roster.ServerSeed, StringComparison.Ordinal), "public roster projection excludes server seed");
        using (var leakedTeam = JsonDocument.Parse("{\"playerId\":\"p1\",\"seatId\":\"P01\",\"kit\":0,\"team\":1}"))
            Check(!HasExactProperties(leakedTeam.RootElement, "playerId", "seatId", "kit"), "allowlist rejects a numeric team wire leak");
        using (var leakedServerField = JsonDocument.Parse("{\"playerId\":\"p1\",\"seatId\":\"P01\",\"kit\":0,\"foreignIntent\":\"synthetic\"}"))
            Check(!HasExactProperties(leakedServerField.RootElement, "playerId", "seatId", "kit"), "allowlist rejects additional server-only fields");
        var latest = JsonSerializer.Deserialize<SealedCaseLobbyEntry>(persisted.Events[^1], new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Check(latest.SnapshotRef is not null && persisted.Events[^1].Contains(latest.SnapshotRef, StringComparison.Ordinal),
            "immutable lobby history contains only the opaque character snapshot reference");
        var selectedProfile = (await fixture.CharacterAsync("p30"))!;
        Check(!persisted.Events[^1].Contains(selectedProfile.ProfileId, StringComparison.Ordinal)
            && !persisted.Events[^1].Contains("queued", StringComparison.Ordinal),
            "immutable lobby history excludes profile identity and portrait state");
        using (var snapshotDb = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(fixture.DataRoot, "sealed-case.sqlite") }.ToString()))
        {
            snapshotDb.Open(); using var snapshotQuery = snapshotDb.CreateCommand();
            snapshotQuery.CommandText = "SELECT profile_id,profile_revision,portrait_json,image FROM sealed_case_private_snapshots WHERE case_id=$case AND snapshot_ref=$ref AND subject_account='p30'";
            snapshotQuery.Parameters.AddWithValue("$case", id); snapshotQuery.Parameters.AddWithValue("$ref", latest.SnapshotRef);
            using var snapshotRow = snapshotQuery.ExecuteReader();
            Check(snapshotRow.Read() && snapshotRow.GetString(0) == selectedProfile.ProfileId && snapshotRow.GetInt64(1) == 1
                && snapshotRow.GetString(2).Contains("queued", StringComparison.Ordinal) && snapshotRow.IsDBNull(3),
                "private sidecar retains the exact selected profile revision and explicit queued state without inventing an image");
        }
        await fixture.AdvanceCharacterAsync("p30");
        using (var frozenCopy = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(fixture.DataRoot, "sealed-case.sqlite") }.ToString()))
        {
            frozenCopy.Open(); using var query = frozenCopy.CreateCommand();
            query.CommandText = "SELECT profile_json FROM sealed_case_private_snapshots WHERE case_id=$case AND snapshot_ref=$ref AND subject_account='p30'";
            query.Parameters.AddWithValue("$case", id); query.Parameters.AddWithValue("$ref", latest.SnapshotRef);
            using var row = query.ExecuteReader();
            Check(row.Read() && JsonDocument.Parse(row.GetString(0)).RootElement.GetProperty("userInput").GetProperty("name").GetString() == selectedProfile.UserInput.Name,
                "post-freeze profile edits cannot replace the roster's private selected character copy");
        }
        using var overflow = fixture.Client(); await fixture.LoginAsync(overflow, "p31"); await fixture.CsrfAsync(overflow);
        var denied = await overflow.PostAsJsonAsync($"/app/v1/sealed-cases/{id}/lobby",
            new { operationId = "capacity-overflow", expectedRevision = revision, ready = true, consent = true });
        Equal(HttpStatusCode.Conflict, denied.StatusCode);
        Check((await denied.Content.ReadAsStringAsync()).Contains("sealed_case_lobby_full", StringComparison.Ordinal), "overflow receives stable full-lobby problem");
        Equal(30, (await owner.GetFromJsonAsync<JsonElement>($"/app/v1/sealed-cases/{id}")).GetProperty("readyPlayers").GetInt32());
        var frozenChange = await owner.PostAsJsonAsync($"/app/v1/sealed-cases/{id}/lobby",
            new { operationId = "after-roster-freeze", expectedRevision = revision, ready = false, consent = false });
        Equal(HttpStatusCode.Conflict, frozenChange.StatusCode);
        Check((await frozenChange.Content.ReadAsStringAsync()).Contains("case_roster_frozen", StringComparison.Ordinal), "frozen roster cannot be mutated");
        Equal(30, durableStore.Read(id).Events.Count);
    }

    private static async Task PollingDoesNotConsumeAccountBudgetAsync()
    {
        await using var fixture = await Fixture.StartAsync(requestsPerMinute: 40);
        var clients = new List<HttpClient>();
        try
        {
            for (var i = 1; i <= 8; i++) { var client = fixture.Client(); clients.Add(client); await fixture.LoginAsync(client, $"p{i}"); }
            var owner = clients[0]; await fixture.CsrfAsync(owner);
            var created = await owner.PostAsJsonAsync("/app/v1/sealed-cases", new { operationId = "poll-create", expectedPlayers = 8, variant = "first" });
            created.EnsureSuccessStatusCode(); var view = await created.Content.ReadFromJsonAsync<JsonElement>(); var id = view.GetProperty("id").GetString();
            for (var poll = 0; poll < 6; poll++) foreach (var client in clients)
                Equal(HttpStatusCode.OK, (await client.GetAsync($"/app/v1/sealed-cases/{id}")).StatusCode);
            Equal(HttpStatusCode.OK, (await owner.GetAsync("/test/account-budget")).StatusCode);
            Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync($"/app/v1/sealed-cases/{id}/lobby", new { operationId = "poll-ready", expectedRevision = 1, ready = true, consent = true })).StatusCode);
            var limited = false;
            for (var i = 0; i < 120; i++)
            {
                var status = (await owner.GetAsync($"/app/v1/sealed-cases/{id}")).StatusCode;
                if (status == HttpStatusCode.TooManyRequests) { limited = true; break; }
                Equal(HttpStatusCode.OK, status);
            }
            Check(limited, "one account still has a finite polling allowance");
            Equal(HttpStatusCode.OK, (await clients[1].GetAsync($"/app/v1/sealed-cases/{id}")).StatusCode);
            Equal(HttpStatusCode.OK, (await owner.GetAsync("/test/account-budget")).StatusCode);
            Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync($"/app/v1/sealed-cases/{id}/lobby", new { operationId = "after-read-limit", expectedRevision = 2, ready = false, consent = false })).StatusCode);
        }
        finally { foreach (var client in clients) client.Dispose(); }
    }

    private static async Task RevocationWhileLobbyWriteWaitsAsync()
    {
        foreach (var creating in new[] { false, true })
        {
        await using var fixture = await Fixture.StartAsync();
        using var owner = fixture.Client(); await fixture.LoginAsync(owner, "p1"); await fixture.CsrfAsync(owner);
        var created = await owner.PostAsJsonAsync("/app/v1/sealed-cases", new { operationId = "revoke-create", expectedPlayers = 8, variant = "first" });
        created.EnsureSuccessStatusCode(); var view = await created.Content.ReadFromJsonAsync<JsonElement>(); var id = view.GetProperty("id").GetString()!;
        using var blocker = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(fixture.DataRoot, "sealed-case.sqlite") }.ToString()); blocker.Open();
        using var held = blocker.BeginTransaction(deferred: false);
        var observed = fixture.WatchWrite(creating ? "/app/v1/sealed-cases" : $"/app/v1/sealed-cases/{id}/lobby");
        var pending = creating
            ? owner.PostAsJsonAsync("/app/v1/sealed-cases", new { operationId = "revoked-create", expectedPlayers = 8, variant = "first" })
            : owner.PostAsJsonAsync($"/app/v1/sealed-cases/{id}/lobby", new { operationId = "revoked-ready", expectedRevision = 1, ready = true, consent = true });
        try
        {
            await observed.WaitAsync(TimeSpan.FromSeconds(5));
            await fixture.RevokeAsync("p1").WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { held.Commit(); }
        var response = await pending.WaitAsync(TimeSpan.FromSeconds(10));
        Check(response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized, "revoked in-flight writer is denied after acquiring lobby lock");
        var persisted = new SealedCaseStore(Path.Combine(fixture.DataRoot, "sealed-case.sqlite")).Read(id);
        Equal(1L, persisted.Revision); Equal(0, persisted.Events.Count);
        using var count = blocker.CreateCommand(); count.CommandText = "SELECT COUNT(*) FROM sealed_cases"; Equal(1L, (long)count.ExecuteScalar()!);
        }
    }

    private sealed class LobbyProbeClock(IHttpContextAccessor contexts) : TimeProvider
    {
        internal string? ObservedPath { get; set; }
        internal TaskCompletionSource Observed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal long OffsetSeconds;
        internal long? FrozenAt;
        public override DateTimeOffset GetUtcNow()
        {
            var h = contexts.HttpContext;
            if (h is not null && HttpMethods.IsPost(h.Request.Method) && h.Request.Path == ObservedPath && h.Items.ContainsKey("dv-sealed-group")) Observed.TrySetResult();
            return (FrozenAt is { } frozen ? DateTimeOffset.FromUnixTimeSeconds(frozen) : DateTimeOffset.UtcNow).AddSeconds(Interlocked.Read(ref OffsetSeconds));
        }
    }

    private sealed class Fixture(WebApplication app, string root, X509Certificate2 certificate) : IAsyncDisposable
    {
        internal IServiceProvider Services => app.Services;
        internal void FreezeClock() => ((LobbyProbeClock)app.Services.GetRequiredService<TimeProvider>()).FrozenAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        internal void AdvanceSeconds(long seconds) => Interlocked.Add(ref ((LobbyProbeClock)app.Services.GetRequiredService<TimeProvider>()).OffsetSeconds, seconds);
        internal string DataRoot => Path.Combine(root, "accounts");
        internal Task<CharacterProfile?> CharacterAsync(string id) => app.Services.GetRequiredService<CharacterProfileStore>().ReadAsync(id);
        internal async Task AdvanceCharacterAsync(string id)
        {
            var store = app.Services.GetRequiredService<CharacterProfileStore>();
            var profile = await store.ReadAsync(id) ?? throw new InvalidOperationException("fixture profile missing");
            await store.SaveAsync(id, new CharacterWriteRequest(profile.Revision,
                new CharacterUserInput(profile.UserInput.Name + " aktualizace", profile.UserInput.World, profile.UserInput.Version),
                ExpectedProfileId: profile.ProfileId, OpenAiConsent: true, PortraitConsent: true), canEditExisting: true);
        }
        internal Task WatchWrite(string path) { var clock = (LobbyProbeClock)app.Services.GetRequiredService<TimeProvider>(); clock.ObservedPath = path; return clock.Observed.Task; }
        internal HttpClient Client() => new(new HttpClientHandler { CookieContainer = new CookieContainer(), UseCookies = true, UseProxy = false, ServerCertificateCustomValidationCallback = (_, _, _, _) => true }) { BaseAddress = new Uri(app.Urls.Single()) };
        internal async Task LoginAsync(HttpClient client, string user)
        {
            (await client.PostAsync("/test/login/" + user, new StringContent(""))).EnsureSuccessStatusCode();
            client.DefaultRequestHeaders.Add("Origin", "https://localhost");
        }
        internal async Task CsrfAsync(HttpClient client)
        {
            var token = await client.GetFromJsonAsync<JsonElement>("/test/csrf");
            client.DefaultRequestHeaders.Add("X-DV-CSRF", token.GetProperty("token").GetString());
        }
        internal async Task RevokeAsync(string id)
        {
            await using var scope = app.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<AccountDatabase>();
            var user = await db.Users.SingleAsync(u => u.Id == id); user.LaunchGroupRevokedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(); user.LaunchGroupAccessRevision++;
            await db.SaveChangesAsync();
        }
        internal static async Task<Fixture> StartAsync(bool enabled = true, int requestsPerMinute = 120, ISealedCaseAiPlayers? aiPlayers = null)
        {
            var root = Path.Combine(Path.GetTempPath(), "sealed-http-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            using var rsa = RSA.Create(2048); var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var san = new SubjectAlternativeNameBuilder(); san.AddDnsName("localhost"); request.CertificateExtensions.Add(san.Build());
            using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
            var cert = X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.UserKeySet);
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" }); builder.Logging.ClearProviders(); builder.Configuration.Sources.Clear();
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ApplicationIdentity:Enabled"] = "true", ["ApplicationIdentity:DataRoot"] = Path.Combine(root, "accounts"),
                ["ApplicationIdentity:DataProtectionRoot"] = Path.Combine(root, "keys"), ["ApplicationIdentity:RelyingPartyId"] = "localhost",
                ["ApplicationIdentity:AllowedOrigins:0"] = "https://localhost", ["ApplicationIdentity:Bootstrap:TokenSha256"] = AccountEndpoints.Hash("sealed-test-bootstrap"),
                ["ApplicationIdentity:RequestsPerMinute"] = requestsPerMinute.ToString(System.Globalization.CultureInfo.InvariantCulture), ["Backend:DatabasePath"] = Path.Combine(root, "journal.sqlite"), ["SealedCase:Enabled"] = enabled ? "true" : "false"
            });
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(cert)));
            builder.Services.AddAccountIdentity(builder.Configuration); builder.Services.AddLaunchManagement(builder.Configuration); builder.Services.AddSealedCase(builder.Configuration);
            builder.Services.AddSingleton(sp => new CharacterProfileStore(sp.GetRequiredService<AccountConfiguration>().DataRoot));
            builder.Services.AddSingleton<CustomerAdmissionAuthority>();
            if (aiPlayers is not null) { builder.Services.AddSingleton<ISealedCaseAiPlayers>(aiPlayers); }
            builder.Services.AddHttpContextAccessor(); builder.Services.AddSingleton<TimeProvider, LobbyProbeClock>();
            var app = builder.Build(); await AccountProvisioning.MigrateAsync(app.Services); await app.InitializeAccountsAsync();
            app.UseMobileGameAuthentication(); app.UseSealedCaseAuthentication(); app.UseRateLimiter(); app.UseAccountIdentity(); app.UseLaunchAccessPolicy(); app.MapSealedCase();
            app.MapGet("/test/account-budget", () => Results.Ok()).RequireRateLimiting("account");
            app.MapGet("/test/csrf", (HttpContext h, IAntiforgery csrf) => Results.Json(new { token = csrf.GetAndStoreTokens(h).RequestToken }));
            app.MapPost("/test/login/{id}", async (string id, HttpContext h, AccountDatabase db, SignInManager<AccountUser> signIn) =>
            {
                var user = await db.Users.SingleAsync(u => u.Id == id); var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                var session = new AccountSession { Id = Guid.NewGuid().ToString("N"), UserId = id, ExpiresAt = now + 24 * 3600, AuthenticatedAt = now, LastSeenAt = now };
                db.Sessions.Add(session); await db.SaveChangesAsync();
                await signIn.SignInWithClaimsAsync(user, new AuthenticationProperties { IsPersistent = false, ExpiresUtc = DateTimeOffset.FromUnixTimeSeconds(session.ExpiresAt) }, [new Claim(AccountIdentity.SessionClaim, session.Id)]);
                return Results.NoContent();
            });
            await app.StartAsync(); var fixture = new Fixture(app, root, cert); await fixture.SeedAsync(); return fixture;
        }
        private async Task SeedAsync()
        {
            await using var scope = app.Services.CreateAsyncScope(); var users = scope.ServiceProvider.GetRequiredService<UserManager<AccountUser>>();
            var launch = app.Services.GetRequiredService<LaunchPolicyStore>(); var config = launch.Read();
            launch.Update("fixture", new(config.Revision, true, null, true, false, null, false, config.TimeZone, config.Teaser, config.InitialLoadingMessage, config.PersonalizedLoadingEnabled, config.PersonalizedLoadingPrompt), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var group = launch.CreateGroup("fixture", new("Pod pečetí HTTP", null), now);
            var countdown = launch.SaveCountdown("fixture", group.Id, new(group.Revision, 1, true), now - 2000); launch.ConfirmCountdown("fixture", group.Id, new(countdown.Revision), now - 2000);
            var foreign = launch.CreateGroup("fixture", new("Jiná skupina", null), now);
            var other = launch.SaveCountdown("fixture", foreign.Id, new(foreign.Revision, 1, true), now - 2000); launch.ConfirmCountdown("fixture", foreign.Id, new(other.Revision), now - 2000);
            foreach (var id in Enumerable.Range(1, 32).Select(i => $"p{i}").Append("foreign"))
            {
                var user = new AccountUser { Id = id, UserName = id + "@example.invalid", Email = id + "@example.invalid", EmailConfirmed = true, DisplayName = id, LaunchGroupId = id == "foreign" ? foreign.Id : group.Id, LaunchGroupJoinedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), LaunchGroupAccessRevision = 1, CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds() };
                Check((await users.CreateAsync(user)).Succeeded, "seed user"); Check((await users.AddToRoleAsync(user, AccountRoles.Customer)).Succeeded, "seed customer role");
            }
            var characters = app.Services.GetRequiredService<CharacterProfileStore>();
            foreach (var i in Enumerable.Range(1, 31))
            {
                await characters.SaveAsync($"p{i}", new CharacterWriteRequest(0,
                    new CharacterUserInput($"Postava {i}", "Testovací svět"), OpenAiConsent: true, PortraitConsent: true));
            }
        }
        public async ValueTask DisposeAsync() { await app.StopAsync(); await app.DisposeAsync(); certificate.Dispose(); SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
    private static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"SealedCase HTTP: expected {expected}, got {actual}"); }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException("SealedCase HTTP: " + message); }
    private static bool HasExactProperties(JsonElement element, params string[] expected)
    {
        if (element.ValueKind != JsonValueKind.Object) return false;
        var actual = element.EnumerateObject().Select(property => property.Name).ToHashSet(StringComparer.Ordinal);
        return actual.Count == expected.Length && actual.SetEquals(expected);
    }
}
