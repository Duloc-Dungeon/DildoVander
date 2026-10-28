using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

internal static partial class MobileGameTests
{
    internal static async Task RunOwnerPlayerHttpAsync()
    {
        await using var fixture = await HttpFixture.StartAsync();
        await fixture.MakeOwnerAsync(fixture.ForeignId);
        await fixture.MarkForeignCharacterPreparedAsync();
        await fixture.LabelCharacterAsync(fixture.ForeignId);
        using var owner = fixture.Client();
        using var customer = fixture.Client();
        using var paused = fixture.Client();
        await fixture.LoginAsync(owner, fixture.ForeignId);
        await fixture.LoginAsync(customer, fixture.AliceId);
        await fixture.LoginAsync(paused, fixture.PausedId);
        owner.DefaultRequestHeaders.Add("X-DV-CSRF", await fixture.CsrfAsync(owner));
        customer.DefaultRequestHeaders.Add("X-DV-CSRF", await fixture.CsrfAsync(customer));

        using var initialResponse = await owner.GetAsync("/app/v1/customer/groups");
        var initial = await initialResponse.Content.ReadFromJsonAsync<JsonElement>();
        Check(initialResponse.StatusCode == HttpStatusCode.OK
            && initial.GetProperty("groups").GetArrayLength() == 3
            && initial.GetProperty("groupId").GetString() is not null,
            "Owner without Customer role receives every group and an automatic active context");
        Check((await owner.GetAsync("/app/v1/admin/launch/groups")).StatusCode == HttpStatusCode.OK,
            "owner participation preserves administration authority");
        Check((await customer.GetAsync("/app/v1/customer/groups")).StatusCode == HttpStatusCode.Forbidden
            && (await customer.PutAsJsonAsync("/app/v1/customer/groups/active",
                new { groupId = fixture.PausedGroupId, expectedRevision = 1 })).StatusCode == HttpStatusCode.Forbidden,
            "ordinary players cannot enumerate or select arbitrary groups");

        using var selectedResponse = await owner.PutAsJsonAsync("/app/v1/customer/groups/active",
            new { groupId = fixture.PausedGroupId, expectedRevision = initial.GetProperty("accessRevision").GetInt64() });
        var selected = await selectedResponse.Content.ReadFromJsonAsync<JsonElement>();
        Check(selectedResponse.StatusCode == HttpStatusCode.OK
            && selected.GetProperty("groupId").GetString() == fixture.PausedGroupId,
            "owner can select a group before its launch");
        var revision = selected.GetProperty("accessRevision").GetInt64();
        using var membersResponse = await owner.GetAsync("/app/v1/admin/launch/groups/" + fixture.ActiveGroupId + "/members");
        var members = await membersResponse.Content.ReadFromJsonAsync<JsonElement>();
        Check(members.GetProperty("members").EnumerateArray().Any(member =>
            member.GetProperty("userId").GetString() == fixture.ForeignId
            && member.GetProperty("displayName").GetString() == "Olivander"),
            "owner appears as a member of another group while playing in the selected group");
        using var missing = await owner.PutAsJsonAsync("/app/v1/customer/groups/active",
            new { groupId = "missing", expectedRevision = revision });
        Check(missing.StatusCode == HttpStatusCode.NotFound, "owner cannot select an unknown group");
        Check((await owner.GetAsync("/app/v1/game")).StatusCode == HttpStatusCode.Forbidden,
            "prelaunch preparation does not open actual gameplay");

        const string workshop = "/app/v1/customer/wand/workshop/sessions";
        using var eligibility = await owner.GetAsync("/app/v1/customer/wand/workshop");
        Check((await eligibility.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("eligible").GetBoolean(),
            "owner prepared character is eligible for the initial wand before launch");
        using var ordinaryEligibility = await paused.GetAsync("/app/v1/customer/wand/workshop");
        Check(!(await ordinaryEligibility.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("eligible").GetBoolean(),
            "ordinary player retains the closed-group preparation restriction");
        var operation = Guid.NewGuid().ToString("N");
        Check((await owner.PostAsJsonAsync(workshop, new { operationId = operation })).StatusCode == HttpStatusCode.OK,
            "owner starts initial wand workshop before group launch");
        Check((await owner.PostAsJsonAsync(workshop,
            new { operationId = Guid.NewGuid().ToString("N"), upgrade = true })).StatusCode == HttpStatusCode.Conflict,
            "owner prelaunch exception does not grant a tier-two upgrade");
        for (var turn = 0; turn < 4; turn++)
        {
            Check((await owner.PostAsJsonAsync(workshop + "/" + operation + "/messages",
                new { messageId = Guid.NewGuid().ToString("N"), text = $"Ověřím mapu, sdělení {turn}." })).StatusCode == HttpStatusCode.OK,
                "owner can complete a normal workshop dialogue before launch");
        }
        Check((await owner.PostAsJsonAsync(workshop + "/" + operation + "/finish", new { })).StatusCode == HttpStatusCode.OK,
            "owner explicitly finishes the initial prelaunch workshop");

        var switches = await Task.WhenAll(
            owner.PutAsJsonAsync("/app/v1/customer/groups/active", new { groupId = fixture.ActiveGroupId, expectedRevision = revision }),
            owner.PutAsJsonAsync("/app/v1/customer/groups/active", new { groupId = fixture.ActiveGroupId, expectedRevision = revision }));
        Check(switches.Count(response => response.StatusCode == HttpStatusCode.OK) == 1
            && switches.Count(response => response.StatusCode == HttpStatusCode.Conflict) == 1,
            "concurrent owner selections accept exactly one expected revision");
        foreach (var response in switches) { response.Dispose(); }
        await fixture.RunWorkshopWorkerAsync(fixture.ForeignId, operation);
        Check((await owner.GetAsync(workshop + "/" + operation)).StatusCode == HttpStatusCode.OK,
            "owner generation and result survive a selected-group switch");
        using var lobbyResponse = await owner.GetAsync("/app/v1/game");
        var lobby = await lobbyResponse.Content.ReadFromJsonAsync<JsonElement>();
        Check(lobbyResponse.StatusCode == HttpStatusCode.OK
            && lobby.GetProperty("player").GetProperty("displayName").GetString() == "Olivander",
            "owner plays in the selected launched group with fixed name Olivander");
        using var customerLobbyResponse = await customer.GetAsync("/app/v1/game");
        var customerLobby = await customerLobbyResponse.Content.ReadFromJsonAsync<JsonElement>();
        Check(customerLobby.GetProperty("opponents").EnumerateArray().Any(player =>
            player.GetProperty("id").GetString() == fixture.ForeignId
            && player.GetProperty("displayName").GetString() == "Olivander"),
            "other players see the owner as a normal opponent with the fixed name");
        using var challenge = await customer.PostAsJsonAsync("/app/v1/game/matches",
            new { opponentId = fixture.ForeignId, mode = "quick", operationId = "owner-opponent-0001" });
        Check(challenge.StatusCode == HttpStatusCode.Created, "a normal player can challenge an owner without Customer role");
        var createdDuel = await challenge.Content.ReadFromJsonAsync<JsonElement>();
        var duelId = createdDuel.GetProperty("id").GetString()!;
        using var acceptedResponse = await owner.PostAsJsonAsync("/app/v1/game/matches/" + duelId + "/accept",
            new { operationId = "owner-switch-duel-accept", expectedRevision = createdDuel.GetProperty("revision").GetInt64() });
        var acceptedDuel = await acceptedResponse.Content.ReadFromJsonAsync<JsonElement>();
        Check(acceptedResponse.StatusCode == HttpStatusCode.OK && acceptedDuel.GetProperty("status").GetString() == "active",
            "owner accepts a normal duel before changing groups");
        var team = await fixture.SeedOwnerTeamAsync();

        await SelectOwnerGroupAsync(fixture.OtherActiveGroupId);
        Check((await owner.GetAsync("/app/v1/game/matches/" + duelId)).StatusCode == HttpStatusCode.NotFound
            && (await owner.GetAsync("/app/v1/game/teams/" + team.Id)).StatusCode == HttpStatusCode.NotFound,
            "owner selected group prevents reading another group's duel and team context");
        using var awayLobbyResponse = await customer.GetAsync("/app/v1/game");
        var awayLobby = await awayLobbyResponse.Content.ReadFromJsonAsync<JsonElement>();
        Check(awayLobby.GetProperty("opponents").EnumerateArray().Any(player => player.GetProperty("id").GetString() == fixture.ForeignId)
            && awayLobby.GetProperty("active").EnumerateArray().Any(match => match.GetProperty("id").GetString() == duelId),
            "regular player lobby keeps the owner visible and preserves their active duel while owner views another group");
        using var awayTeamsResponse = await customer.GetAsync("/app/v1/game/teams");
        var awayTeams = await awayTeamsResponse.Content.ReadFromJsonAsync<JsonElement>();
        Check(awayTeamsResponse.StatusCode == HttpStatusCode.OK
            && awayTeams.GetProperty("teams").EnumerateArray().Any(match =>
                match.GetProperty("id").GetString() == team.Id && match.GetProperty("status").GetString() == "active"),
            "regular player team lobby does not retire an owner's active team after a context switch");
        using var regularDuel = await customer.GetAsync("/app/v1/game/matches/" + duelId);
        using var regularTeam = await customer.GetAsync("/app/v1/game/teams/" + team.Id);
        Check(regularDuel.StatusCode == HttpStatusCode.OK && regularTeam.StatusCode == HttpStatusCode.OK,
            "regular participants still access active owner duel and team while owner views another group");

        await SelectOwnerGroupAsync(fixture.ActiveGroupId);
        using var returnedDuelResponse = await owner.GetAsync("/app/v1/game/matches/" + duelId);
        var returnedDuel = await returnedDuelResponse.Content.ReadFromJsonAsync<JsonElement>();
        using var returnedTeamResponse = await owner.GetAsync("/app/v1/game/teams/" + team.Id);
        var returnedTeam = await returnedTeamResponse.Content.ReadFromJsonAsync<JsonElement>();
        Check(returnedDuelResponse.StatusCode == HttpStatusCode.OK && returnedDuel.GetProperty("status").GetString() == "active"
            && returnedDuel.GetProperty("revision").GetInt64() == acceptedDuel.GetProperty("revision").GetInt64()
            && returnedTeamResponse.StatusCode == HttpStatusCode.OK && returnedTeam.GetProperty("status").GetString() == "active"
            && returnedTeam.GetProperty("revision").GetInt64() == team.Revision,
            "A-to-B-to-A selection preserves active duel and team revisions despite intervening regular-player reads");

        await fixture.BondOwnerWandAsync(fixture.ForeignId);
        var ownerNarrative = await fixture.ReadYoloProfileAsync(fixture.ForeignId);
        var character = (await fixture.Profiles.ReadAsync(fixture.ForeignId))!;
        var wand = fixture.Wands.LatestBonded(fixture.ForeignId, character.ProfileId, character.Revision)!;
        Check(character.UserInput.Name != "Olivander" && ownerNarrative.Name == "Olivander"
            && ownerNarrative.Universe == character.UserInput.World && ownerNarrative.WandName == wand.Name
            && ownerNarrative.Traits.Contains(wand.Temperament),
            "YOLO provider projection fixes owner narrative name while preserving fictional world, wand and traits without a provider call");
        Console.WriteLine("Owner player HTTPS checks passed.");

        async Task SelectOwnerGroupAsync(string groupId)
        {
            using var currentResponse = await owner.GetAsync("/app/v1/customer/groups");
            var current = await currentResponse.Content.ReadFromJsonAsync<JsonElement>();
            using var response = await owner.PutAsJsonAsync("/app/v1/customer/groups/active",
                new { groupId, expectedRevision = current.GetProperty("accessRevision").GetInt64() });
            Check(response.StatusCode == HttpStatusCode.OK, "owner changes selected group with the current access revision");
        }
    }
}
