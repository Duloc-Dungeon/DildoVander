using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Kufr.Backend.Features.Customers;
using Kufr.Backend.Features.GptLive;
using Kufr.Backend.Features.Identity;
using Kufr.Backend.Features.Launch;
using Kufr.Backend.Features.SealedCase;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

internal static partial class SealedCaseHttpTests
{
    private sealed class LobbyAiProvider : ISealedCaseAiPlayers
    {
        public bool Available { get; set; } = true;
        internal readonly ConcurrentDictionary<string, int> Calls = new();
        internal TaskCompletionSource<string> Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal void Arm()
        {
            BlockFirst = true; Interlocked.Exchange(ref blocked, 0);
            Entered = new(TaskCreationOptions.RunContinuationsAsynchronously); Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        internal bool BlockFirst;
        private int blocked;
        public async Task<SealedAiMove> ChooseAsync(SealedPlayView view, CancellationToken token)
        {
            var input = JsonSerializer.Serialize(SealedCaseAiPlayers.BuildRequest(view).Input);
            Check(!input.Contains("HUMAN_NAME_CANARY", StringComparison.Ordinal) && !input.Contains("HUMAN_CHAT_CANARY", StringComparison.Ordinal)
                && !input.Contains("\"p1\"", StringComparison.Ordinal) && !input.Contains(view.OwnId, StringComparison.Ordinal), "provider input contains no human names, chat or raw identity");
            var key = view.OwnId + ":" + (view.Phase == "planning" ? "plan-" + view.Chapter : view.Finale!.Phase);
            Calls.AddOrUpdate(key, 1, (_, previous) => previous + 1);
            if (BlockFirst && Interlocked.CompareExchange(ref blocked, 1, 0) == 0)
            { Entered.TrySetResult(view.OwnId); await Release.Task.WaitAsync(token); }
            return view.Phase == "planning" ? new(view.Choices[0].Plan, null) : new(null, new(view.Finale!.Phase == "runoff" ? 2 : 1, []));
        }
    }

    private static async Task OwnerRoleAsync(Fixture fixture, bool add = true)
    {
        using var scope = fixture.Services.CreateScope(); var users = scope.ServiceProvider.GetRequiredService<UserManager<AccountUser>>();
        var user = (await users.FindByIdAsync("p1"))!;
        var result = add ? await users.AddToRoleAsync(user, AccountRoles.Owner) : await users.RemoveFromRoleAsync(user, AccountRoles.Owner);
        Check(result.Succeeded, "fixture owner role change");
    }

    internal static async Task AiLobbyAsync()
    {
        Check(SealedCaseAiPlayers.DefaultModel == WandDecisionBudget.Luna, "sealed case AI players default to the shared Luna model");
        using (var disabled = new SealedCaseAiPlayers(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["OPENAI_API_KEY"] = "synthetic-not-a-key", ["SealedCase:AiPlayers:Enabled"] = "false" }).Build()))
        { Check(!disabled.Available, "AI disabled even with a credential"); }
        using (var noKey = new SealedCaseAiPlayers(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["SealedCase:AiPlayers:Enabled"] = "true" }).Build()))
        { Check(!noKey.Available, "missing credential cannot create inactive bots"); }
        var provider = new LobbyAiProvider { BlockFirst = true };
        await using var fixture = await Fixture.StartAsync(requestsPerMinute: 4000, aiPlayers: provider);
        await OwnerRoleAsync(fixture);
        // Owner bypass for staff screens must never open a future party early.
        var launch = fixture.Services.GetRequiredService<LaunchPolicyStore>(); var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var futureGroup = launch.CreateGroup("fixture", new("Budoucí skupina", null), now);
        var futureCountdown = launch.SaveCountdown("fixture", futureGroup.Id, new(futureGroup.Revision, 3600, true), now);
        launch.ConfirmCountdown("fixture", futureGroup.Id, new(futureCountdown.Revision), now);
        using (var scope = fixture.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<AccountUser>>(); var futureOwner = (await users.FindByIdAsync("p32"))!;
            futureOwner.LaunchGroupId = futureGroup.Id; futureOwner.LaunchGroupAccessRevision++;
            Check((await users.UpdateAsync(futureOwner)).Succeeded, "future owner group");
            Check((await users.AddToRoleAsync(futureOwner, AccountRoles.Owner)).Succeeded, "future owner role");
        }
        using (var futureOwner = fixture.Client())
        {
            await fixture.LoginAsync(futureOwner, "p32"); await fixture.CsrfAsync(futureOwner);
            Equal(HttpStatusCode.Forbidden, (await futureOwner.PostAsJsonAsync("/app/v1/sealed-cases", new
            { operationId = "owner-future-group", expectedPlayers = 8, variant = "first" })).StatusCode);
        }
        var profiles = fixture.Services.GetRequiredService<CharacterProfileStore>(); var profile = (await profiles.ReadAsync("p1"))!;
        await profiles.SaveAsync("p1", new CharacterWriteRequest(profile.Revision, profile.UserInput with { Name = "HUMAN_NAME_CANARY" },
            ExpectedProfileId: profile.ProfileId, OpenAiConsent: true, PortraitConsent: true), canEditExisting: true);
        using var owner = fixture.Client(); await fixture.LoginAsync(owner, "p1"); await fixture.CsrfAsync(owner);
        using var peer = fixture.Client(); await fixture.LoginAsync(peer, "p2"); await fixture.CsrfAsync(peer);
        var create = await owner.PostAsJsonAsync("/app/v1/sealed-cases", new { operationId = "ai-lobby-create", expectedPlayers = 8, variant = "first" });
        Check(create.IsSuccessStatusCode, "create AI lobby: " + (int)create.StatusCode + " " + await create.Content.ReadAsStringAsync()); var id = (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        var prefix = "/app/v1/sealed-cases/" + id;
        (await owner.PostAsJsonAsync(prefix + "/lobby", new { operationId = "ai-owner-ready", expectedRevision = 1, ready = true, consent = true, scenarioConsent = true })).EnsureSuccessStatusCode();
        Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync(prefix + "/ai-players", new
        { operationId = (string?)null, expectedRevision = 2, count = 1 })).StatusCode);
        var request = new SealedAddAiPlayersRequest("ai-add-seven", 2, 7);
        Equal(HttpStatusCode.Forbidden, (await peer.PostAsJsonAsync(prefix + "/ai-players", request)).StatusCode);
        provider.Available = false;
        Equal(HttpStatusCode.ServiceUnavailable, (await owner.PostAsJsonAsync(prefix + "/ai-players", request)).StatusCode);
        Equal(1, (await owner.GetFromJsonAsync<SealedPlayView>(prefix + "/play"))!.ReadyPlayers);
        provider.Available = true;
        var add = await owner.PostAsJsonAsync(prefix + "/ai-players", request); Equal(HttpStatusCode.OK, add.StatusCode);
        Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync(prefix + "/ai-players", request)).StatusCode);
        var lobby = (await owner.GetFromJsonAsync<SealedPlayView>(prefix + "/play"))!;
        Equal(8, lobby.ReadyPlayers); Equal(7, lobby.Players.Count(p => p.IsAi)); Check(lobby.CanManageAi && lobby.CanStart && lobby.AiAvailable, "owner lobby can start with bots");
        Equal(8, (await owner.GetFromJsonAsync<JsonElement>(prefix)).GetProperty("readyPlayers").GetInt32());
        Check(!(await peer.GetFromJsonAsync<SealedPlayView>(prefix + "/play"))!.CanManageAi, "ordinary member has no AI management");
        Equal(HttpStatusCode.Conflict, (await owner.PostAsJsonAsync(prefix + "/ai-players", new SealedAddAiPlayersRequest("ai-overflow", 3, 1))).StatusCode);
        await OwnerRoleAsync(fixture, false);
        Equal(HttpStatusCode.Forbidden, (await owner.PostAsJsonAsync(prefix + "/ai-players", request)).StatusCode);
        await OwnerRoleAsync(fixture);
        fixture.FreezeClock();
        (await owner.PostAsJsonAsync(prefix + "/start", new { operationId = "ai-game-start", expectedRevision = 3 })).EnsureSuccessStatusCode();
        Equal(HttpStatusCode.Conflict, (await owner.PostAsJsonAsync(prefix + "/ai-players", new SealedAddAiPlayersRequest("ai-too-late", 4, 1))).StatusCode);
        (await owner.PostAsJsonAsync(prefix + "/statements", new { operationId = "human-public", body = "HUMAN_CHAT_CANARY", recipients = Array.Empty<string>(), @public = true })).EnsureSuccessStatusCode();
        var runner = fixture.Services.GetRequiredService<SealedCaseAiRunner>(); var store = fixture.Services.GetRequiredService<SealedCaseStore>();
        var authority = fixture.Services.GetRequiredService<CustomerAdmissionAuthority>(); var game = store.RunningAiCases().Single();
        var schedule = SealedCaseRules.Schedule("first", 8); fixture.AdvanceSeconds(schedule.IntroductionSeconds);
        await OwnerRoleAsync(fixture, false); await runner.RunOnceAsync(CancellationToken.None);
        Equal(0, provider.Calls.Count); await OwnerRoleAsync(fixture);
        // A suspended old worker and a restarted runner must execute one canonical decision.
        var first = runner.RunOnceAsync(CancellationToken.None); var blockedBot = await provider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        fixture.AdvanceSeconds(121); await runner.RunOnceAsync(CancellationToken.None); provider.Release.TrySetResult(); await first;
        var accepted = store.ReadOwnPlan(id, game.Group, blockedBot, 1).Action!;
        Equal(SealedIntent.Hold, accepted.Intent); Equal(1, provider.Calls[blockedBot + ":plan-1"]);
        await runner.RunOnceAsync(CancellationToken.None); Check(provider.Calls.Values.All(count => count == 1), "no paid retry or duplicate dispatch");
        // Stale chapter projections cannot reserve a request in the following chapter.
        fixture.AdvanceSeconds(schedule.PlanningSeconds - 121 + schedule.QuietSeconds);
        store.AdvanceAiCase(id, game.Group, () => authority.AcquireAccount("p1"));
        Check(store.LeaseAiTurn(game, blockedBot, "plan-1", () => authority.AcquireAccount("p1")) is null, "stale phase denied before provider");
        provider.Arm(); var pausedRun = runner.RunOnceAsync(CancellationToken.None);
        var pausedBot = await provider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        (await owner.PostAsJsonAsync(prefix + "/pause", new { operationId = "ai-pause-two" })).EnsureSuccessStatusCode();
        provider.Release.TrySetResult(); await pausedRun;
        var callsAtPause = provider.Calls.Values.Sum(); await runner.RunOnceAsync(CancellationToken.None);
        Equal(callsAtPause, provider.Calls.Values.Sum());
        (await owner.PostAsJsonAsync(prefix + "/resume", new { operationId = "ai-resume-two" })).EnsureSuccessStatusCode();
        await runner.RunOnceAsync(CancellationToken.None); Equal(1, provider.Calls[pausedBot + ":plan-2"]);
        provider.BlockFirst = false;
        for (var chapter = 2; chapter <= 3; chapter++)
        {
            await runner.RunOnceAsync(CancellationToken.None);
            fixture.AdvanceSeconds(schedule.PlanningSeconds + schedule.QuietSeconds);
        }
        await runner.RunOnceAsync(CancellationToken.None); fixture.AdvanceSeconds(schedule.FinaleSeconds * 2 / 3);
        await runner.RunOnceAsync(CancellationToken.None); fixture.AdvanceSeconds(schedule.FinaleSeconds - schedule.FinaleSeconds * 2 / 3);
        await runner.RunOnceAsync(CancellationToken.None);
        var final = (await owner.GetFromJsonAsync<SealedPlayView>(prefix + "/play"))!;
        Equal("released", final.Finale!.Phase); Equal(24, final.Finale.Reconstruction!.Length);
        Check(provider.Calls.Values.All(count => count == 1), "whole game has at most one provider call per bot/phase");
        Equal(0, store.RunningAiCases().Length);
        // Full capacity can consist of thirty bots, without creating any accounts.
        using (var scope = fixture.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<AccountUser>>();
            Check((await users.RemoveFromRoleAsync((await users.FindByIdAsync("p1"))!, AccountRoles.Customer)).Succeeded, "owner-only account");
        }
        var large = await owner.PostAsJsonAsync("/app/v1/sealed-cases", new { operationId = "ai-thirty-create", expectedPlayers = 30, variant = "full" });
        large.EnsureSuccessStatusCode(); var largeId = (await large.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        var largePrefix = "/app/v1/sealed-cases/" + largeId;
        Equal(HttpStatusCode.Forbidden, (await owner.PostAsJsonAsync(largePrefix + "/lobby", new
        { operationId = "owner-no-player-role", expectedRevision = 1, ready = true, consent = true, scenarioConsent = true })).StatusCode);
        (await owner.PostAsJsonAsync(largePrefix + "/ai-players", new SealedAddAiPlayersRequest("ai-thirty-add", 1, 30))).EnsureSuccessStatusCode();
        var largeLobby = (await owner.GetFromJsonAsync<SealedPlayView>(largePrefix + "/play"))!;
        Check(!largeLobby.CanJoin && largeLobby.CanManageAi, "Owner alone can manage a lobby without a customer seat");
        Equal(HttpStatusCode.Forbidden, (await owner.GetAsync(largePrefix + "/context")).StatusCode);
        Equal(30, largeLobby.ReadyPlayers); Check(largeLobby.CanStart, "thirty AI seats are a complete lobby");
        (await owner.PostAsJsonAsync(largePrefix + "/start", new { operationId = "ai-thirty-start", expectedRevision = 2 })).EnsureSuccessStatusCode();
        (await owner.PostAsJsonAsync(largePrefix + "/pause", new { operationId = "owner-only-pause" })).EnsureSuccessStatusCode();
        (await owner.PostAsJsonAsync(largePrefix + "/resume", new { operationId = "owner-only-resume" })).EnsureSuccessStatusCode();
        Equal(HttpStatusCode.Forbidden, (await owner.PostAsJsonAsync(largePrefix + "/actions", new
        { operationId = "owner-cannot-act", plan = new SealedPlanConfirmation(1, 0, true, SealedIntent.Hold) })).StatusCode);
        fixture.AdvanceSeconds(SealedCaseRules.Schedule("full", 30).IntroductionSeconds);
        await runner.RunOnceAsync(CancellationToken.None);
        var operatorView = (await owner.GetFromJsonAsync<SealedPlayView>(largePrefix + "/play?viewerId=" + largeLobby.Players[0].Id))!;
        Equal("Operator", operatorView.OwnState); Check(!operatorView.CanJoin, "running observer never gains a player seat"); Check(operatorView.OwnTeam is null && operatorView.OwnPlan is null, "owner cannot impersonate a bot projection");
        Equal(30, operatorView.Players.Count(p => p.IsAi)); Equal(0, operatorView.Clues.Length); Equal(0, operatorView.TeamMembers.Length); Equal(0, operatorView.OwnOutcomes.Length);
        // Reopening preserves bots, and a human profile fence stops AI before erase.
        var reopened = new SealedCaseStore(Path.Combine(fixture.DataRoot, "sealed-case.sqlite"));
        Equal(7, reopened.ReadRoster(id, game.Group).Members.Count(p => p.IsAi));
        var currentProfile = (await profiles.ReadAsync("p1"))!;
        await profiles.DeleteAsync("p1", currentProfile.Revision, currentProfile.ProfileId);
        var callsBeforeErase = provider.Calls.Values.Sum(); await runner.RunOnceAsync(CancellationToken.None);
        Equal(callsBeforeErase, provider.Calls.Values.Sum());
        await CharacterEndpoints.ReconcileDeletionsAsync(fixture.Services, profiles, CancellationToken.None);
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(fixture.DataRoot, "sealed-case.sqlite") }.ToString()); db.Open();
        foreach (var table in new[] { "sealed_case_ai_players", "sealed_case_ai_turns" })
        { using var query = db.CreateCommand(); query.CommandText = "SELECT COUNT(*) FROM " + table; Equal(0L, (long)query.ExecuteScalar()!); }
        Console.WriteLine("PASS owner-only AI lobby, disabled provider, capacity/freeze/replay, privacy input isolation, restart race, deadlines and full bot game.");
    }
}
