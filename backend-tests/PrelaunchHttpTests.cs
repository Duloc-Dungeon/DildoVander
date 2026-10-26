using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Kufr.Backend.Features.Customers;
using Kufr.Backend.Features.Identity;
using Kufr.Backend.Features.Launch;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

internal static partial class AccountIdentityTests
{
    internal static async Task RunPrelaunchHttpAsync()
    {
        await using var fixture = await Fixture.StartAsync(customerFeatures: true, betaRecruitmentOpen: false,
            configureServices: services => services.AddSingleton<ICharacterPortraitProvider>(new PrelaunchTestPortraitProvider()));
        using var owner = fixture.Client();
        using var customer = fixture.Client();
        using var unassignedClient = fixture.Client();
        using var stranger = fixture.Client();
        using var beta = fixture.Client();
        using var groupOneClient = fixture.Client();
        using var groupTwoClient = fixture.Client();
        using var secondGroupTwoClient = fixture.Client();
        using var groupTesterClient = fixture.Client();
        using var recoveryClient = fixture.Client();
        using var ownerKey = new VirtualPasskey();
        using var customerKey = new VirtualPasskey();
        using var unassignedKey = new VirtualPasskey();
        using var betaKey = new VirtualPasskey();
        using var groupOneKey = new VirtualPasskey();
        using var groupTwoKey = new VirtualPasskey();
        using var secondGroupTwoKey = new VirtualPasskey();
        using var groupTesterKey = new VirtualPasskey();
        using var recoveryKey = new VirtualPasskey();
        await EnrollAsync(owner, ownerKey, Fixture.Bootstrap, "Owner");
        await CsrfAsync(owner);
        var future = DateTimeOffset.UtcNow.AddDays(2).ToUnixTimeMilliseconds();
        var update = new LaunchConfigurationUpdate(0, true, future, false, true, future, false,
            "Europe/Prague", "Hvězdy drží tajemství.", "Někdo právě leští souhvězdí.", true, "One mysterious sentence.");
        Check((await owner.PutAsJsonAsync("/app/v1/admin/launch", update)).IsSuccessStatusCode, "administrator can configure launch over real authenticated HTTPS");
        var invitation = (await JsonAsync(await owner.PostAsJsonAsync("/app/v1/admin/invitations", new { role = "Customer" })))
            .GetProperty("token").GetString()!;
        await EnrollAsync(customer, customerKey, invitation, "Invited guest");
        await CsrfAsync(customer);
        var access = await JsonAsync(await customer.GetAsync("/app/v1/launch"));
        Check(!access.GetProperty("allowed").GetBoolean() && access.GetProperty("audience").GetString() == "production",
            "invited regular account receives production policy");
        Check((await stranger.GetAsync("/app/v1/admin/launch/groups")).StatusCode == HttpStatusCode.Unauthorized,
            "unauthenticated visitor cannot enumerate launch groups");
        var groupOneAt = future;
        var groupTwoAt = future + 60_000;
        var groupOne = (await JsonAsync(await owner.PostAsJsonAsync("/app/v1/admin/launch/groups",
            new { name = "Night one" }))).GetProperty("group");
        var groupOneId = groupOne.GetProperty("id").GetString()!;
        Check(groupOne.GetProperty("launchAt").ValueKind == JsonValueKind.Null,
            "new group has no effective schedule before its countdown is confirmed");
        var groupOneDraft = await owner.PutAsJsonAsync($"/app/v1/admin/launch/groups/{groupOneId}/countdown",
            new { expectedRevision = 1, durationSeconds = 172800, forceStartAtEnd = false });
        var groupOneDraftGroup = (await JsonAsync(groupOneDraft)).GetProperty("group");
        var confirmedGroupOne = await owner.PostAsJsonAsync($"/app/v1/admin/launch/groups/{groupOneId}/countdown/confirm",
            new { expectedRevision = groupOneDraftGroup.GetProperty("revision").GetInt64() });
        groupOne = (await JsonAsync(confirmedGroupOne)).GetProperty("group");
        groupOneAt = groupOne.GetProperty("launchAt").GetInt64();
        Check(groupOneAt > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            "explicit countdown confirmation starts a server-owned future deadline");
        var legacyDraft = (await JsonAsync(await owner.PutAsJsonAsync($"/app/v1/admin/launch/groups/{groupOneId}",
            new { expectedRevision = groupOne.GetProperty("revision").GetInt64(), name = "Night one", launchAt = groupOneAt + 60_000 }))).GetProperty("group");
        Check(legacyDraft.GetProperty("launchAt").ValueKind == JsonValueKind.Null
            && legacyDraft.GetProperty("countdownConfirmedAt").ValueKind == JsonValueKind.Null,
            "legacy absolute schedule edits save a closed draft rather than starting the countdown");
        groupOne = (await JsonAsync(await owner.PostAsJsonAsync($"/app/v1/admin/launch/groups/{groupOneId}/countdown/confirm",
            new { expectedRevision = legacyDraft.GetProperty("revision").GetInt64() }))).GetProperty("group");
        groupOneAt = groupOne.GetProperty("launchAt").GetInt64();
        var staleGroup = await owner.PutAsJsonAsync($"/app/v1/admin/launch/groups/{groupOneId}",
            new { expectedRevision = legacyDraft.GetProperty("revision").GetInt64(), name = "Night one", launchAt = groupOneAt });
        Check(staleGroup.StatusCode == HttpStatusCode.Conflict, "legacy schedule update cannot bypass countdown confirmation or revision checks");
        var groupTwo = (await JsonAsync(await owner.PostAsJsonAsync("/app/v1/admin/launch/groups",
            new { name = "Night two" }))).GetProperty("group");
        var groupTwoId = groupTwo.GetProperty("id").GetString()!;
        var groupTwoDraft = (await JsonAsync(await owner.PutAsJsonAsync($"/app/v1/admin/launch/groups/{groupTwoId}/countdown",
            new { expectedRevision = 1, durationSeconds = 259200, forceStartAtEnd = false }))).GetProperty("group");
        groupTwo = (await JsonAsync(await owner.PostAsJsonAsync($"/app/v1/admin/launch/groups/{groupTwoId}/countdown/confirm",
            new { expectedRevision = groupTwoDraft.GetProperty("revision").GetInt64() }))).GetProperty("group");
        groupTwoAt = groupTwo.GetProperty("launchAt").GetInt64();
        var groupList = await JsonAsync(await owner.GetAsync("/app/v1/admin/launch/groups"));
        Check(groupList.GetProperty("groups").GetArrayLength() == 2, "owner can list launch groups for invitation selection");
        Check((await customer.GetAsync("/app/v1/admin/launch/groups")).StatusCode == HttpStatusCode.Forbidden,
            "ordinary invited member cannot enumerate or manage launch groups");
        Check((await owner.PostAsJsonAsync("/app/v1/admin/invitations",
            new { role = "Customer", launchGroupId = "unknown-group" })).StatusCode == HttpStatusCode.BadRequest,
            "invitation cannot bind an unknown launch group");
        Check((await owner.PostAsJsonAsync("/app/v1/admin/invitations",
            new { role = "Customer", recoveryUserId = "missing-account", launchGroupId = groupOneId })).StatusCode == HttpStatusCode.BadRequest,
            "recovery invitation cannot assign a launch group");
        var groupOneInviteA = (await JsonAsync(await owner.PostAsJsonAsync("/app/v1/admin/invitations",
            new { role = "Customer", launchGroupId = groupOneId }))).GetProperty("token").GetString()!;
        var groupOneInviteB = (await JsonAsync(await owner.PostAsJsonAsync("/app/v1/admin/invitations",
            new { role = "Tester", launchGroupId = groupOneId }))).GetProperty("token").GetString()!;
        var groupTwoInvite = (await JsonAsync(await owner.PostAsJsonAsync("/app/v1/admin/invitations",
            new { role = "Customer", launchGroupId = groupTwoId }))).GetProperty("token").GetString()!;
        await EnrollAsync(groupOneClient, groupOneKey, groupOneInviteA, "Group one member");
        var groupOneSession = await CsrfAsync(groupOneClient);
        await EnrollAsync(groupTesterClient, groupTesterKey, groupOneInviteB, "Group tester");
        await CsrfAsync(groupTesterClient);
        await EnrollAsync(groupTwoClient, groupTwoKey, groupTwoInvite, "Group two member");
        var groupTwoSession = await CsrfAsync(groupTwoClient);
        var secondGroupTwoInvite = (await JsonAsync(await owner.PostAsJsonAsync("/app/v1/admin/invitations",
            new { role = "Customer", launchGroupId = groupTwoId }))).GetProperty("token").GetString()!;
        await EnrollAsync(secondGroupTwoClient, secondGroupTwoKey, secondGroupTwoInvite, "Second group two member");
        var secondGroupTwoSession = await CsrfAsync(secondGroupTwoClient);
        var unassignedInvite = (await JsonAsync(await owner.PostAsJsonAsync("/app/v1/admin/invitations",
            new { role = "Customer" }))).GetProperty("token").GetString()!;
        await EnrollAsync(unassignedClient, unassignedKey, unassignedInvite, "Unassigned guest");
        var unassignedIdentity = await CsrfAsync(unassignedClient);
        var unassignedUserId = unassignedIdentity.GetProperty("account").GetProperty("id").GetString()!;
        var groupOneAccess = await JsonAsync(await groupOneClient.GetAsync("/app/v1/launch"));
        var groupTwoAccess = await JsonAsync(await groupTwoClient.GetAsync("/app/v1/launch"));
        var groupTesterAccess = await JsonAsync(await groupTesterClient.GetAsync("/app/v1/launch"));
        Check(!groupOneAccess.GetProperty("allowed").GetBoolean()
            && groupOneAccess.GetProperty("launchAt").GetInt64() == groupOneAt
            && !groupTwoAccess.GetProperty("allowed").GetBoolean()
            && groupTwoAccess.GetProperty("launchAt").GetInt64() == groupTwoAt,
            "invited accounts receive only their group-specific launch schedules");
        var groupTwoAccessState = await JsonAsync(await groupTwoClient.GetAsync("/app/v1/customer/access"));
        var groupTwoUserId = groupTwoSession.GetProperty("account").GetProperty("id").GetString()!;
        var secondGroupTwoUserId = secondGroupTwoSession.GetProperty("account").GetProperty("id").GetString()!;
        var waitingRoom = await JsonAsync(await owner.GetAsync("/app/v1/admin/launch/waiting-room"));
        var roster = waitingRoom.GetProperty("members").EnumerateArray().ToArray();
        Check(roster.Any(member => member.GetProperty("userId").GetString() == groupTwoUserId
                && member.GetProperty("groupId").GetString() == groupTwoId)
            && roster.Any(member => member.GetProperty("userId").GetString() == secondGroupTwoUserId
                && member.GetProperty("groupId").GetString() == groupTwoId)
            && roster.Any(member => member.GetProperty("userId").GetString() == unassignedUserId
                && member.GetProperty("groupId").ValueKind == JsonValueKind.Null)
            && roster.All(member => member.GetProperty("state").GetString() is "waiting" or "revoked"),
            "owner roster lists waiting and revoked customers across groups, including unassigned accounts");
        var cursorPage = await JsonAsync(await owner.GetAsync("/app/v1/admin/launch/waiting-room?after="
            + Uri.EscapeDataString(unassignedUserId)));
        Check(cursorPage.GetProperty("members").EnumerateArray().All(member =>
                string.CompareOrdinal(member.GetProperty("userId").GetString(), unassignedUserId) > 0)
            && cursorPage.GetProperty("nextCursor").ValueKind is JsonValueKind.Null or JsonValueKind.String,
            "owner roster accepts a stable user-ID cursor for fetching subsequent pages");
        Check((await customer.GetAsync("/app/v1/admin/launch/waiting-room")).StatusCode == HttpStatusCode.Forbidden,
            "ordinary guests cannot enumerate the waiting room");
        var unassignedAccess = await JsonAsync(await unassignedClient.GetAsync("/app/v1/customer/access"));
        Check(unassignedAccess.GetProperty("accessRevision").GetInt64() == 0,
            "unassigned guest starts with membership revision zero");
        var assigned = await owner.PutAsJsonAsync($"/app/v1/admin/launch/waiting-room/{unassignedUserId}/group",
            new { expectedMembershipRevision = 0, expectedSourceGroupId = (string?)null, targetGroupId = groupTwoId });
        var assignedMember = await JsonAsync(assigned);
        Check(assigned.IsSuccessStatusCode && assignedMember.GetProperty("groupId").GetString() == groupTwoId
            && assignedMember.GetProperty("accessRevision").GetInt64() == 1
            && assignedMember.GetProperty("joinedAt").GetInt64() > 0,
            "owner can assign an unassigned revision-zero customer and establish its first group-join time");
        var unassignAttempt = await owner.PutAsJsonAsync($"/app/v1/admin/launch/waiting-room/{unassignedUserId}/group",
            new { expectedMembershipRevision = 1, expectedSourceGroupId = groupTwoId, targetGroupId = (string?)null });
        Check(unassignAttempt.StatusCode == HttpStatusCode.BadRequest,
            "group assignment cannot remove a customer from group launch policy");
        var move = await owner.PutAsJsonAsync($"/app/v1/admin/launch/waiting-room/{groupTwoUserId}/group",
            new
            {
                expectedMembershipRevision = groupTwoAccessState.GetProperty("accessRevision").GetInt64(),
                expectedSourceGroupId = groupTwoId,
                targetGroupId = groupOneId
            });
        var moved = await JsonAsync(move);
        Check(move.IsSuccessStatusCode && moved.GetProperty("groupId").GetString() == groupOneId
            && moved.GetProperty("accessRevision").GetInt64() == groupTwoAccessState.GetProperty("accessRevision").GetInt64() + 1
            && moved.GetProperty("joinedAt").GetInt64() == groupTwoAccessState.GetProperty("joinedAt").GetInt64(),
            "owner can move a waiting member with expected source/revision while preserving the original group-join time");
        var staleMove = await owner.PutAsJsonAsync($"/app/v1/admin/launch/waiting-room/{groupTwoUserId}/group",
            new
            {
                expectedMembershipRevision = groupTwoAccessState.GetProperty("accessRevision").GetInt64(),
                expectedSourceGroupId = groupTwoId,
                targetGroupId = groupTwoId
            });
        Check(staleMove.StatusCode == HttpStatusCode.Conflict,
            "stale membership revision or source group cannot overwrite a concurrent move");
        var attemptedClientClock = await JsonAsync(await groupOneClient.GetAsync("/app/v1/launch?now=253402300799999"));
        Check(!attemptedClientClock.GetProperty("allowed").GetBoolean()
            && attemptedClientClock.GetProperty("launchAt").GetInt64() == groupOneAt,
            "client-supplied clock values cannot open a group schedule");
        await SeedAsync(fixture.App, db => db.Users.Where(user => user.Id == groupTwoUserId)
            .ExecuteUpdateAsync(updateUser => updateUser.SetProperty(user => user.LaunchGroupId, "missing-group")));
        var unresolvedGroupAccess = await JsonAsync(await groupTwoClient.GetAsync("/app/v1/launch"));
        Check(!unresolvedGroupAccess.GetProperty("allowed").GetBoolean()
            && unresolvedGroupAccess.GetProperty("launchAt").ValueKind == JsonValueKind.Null,
            "a stored group membership that cannot resolve fails closed instead of using the global policy");
        await SeedAsync(fixture.App, db => db.Users.Where(user => user.Id == groupTwoUserId)
            .ExecuteUpdateAsync(updateUser => updateUser.SetProperty(user => user.LaunchGroupId, groupTwoId)));
        Check(groupTesterAccess.GetProperty("allowed").GetBoolean()
            && groupTesterAccess.GetProperty("audience").GetString() == "privileged",
            "tester role bypasses its assigned launch group schedule");
        var groupOneUserId = groupOneSession.GetProperty("account").GetProperty("id").GetString()!;
        var recoveryInvite = (await JsonAsync(await owner.PostAsJsonAsync("/app/v1/admin/invitations",
            new { role = "Customer", recoveryUserId = groupOneUserId }))).GetProperty("token").GetString()!;
        await EnrollAsync(recoveryClient, recoveryKey, recoveryInvite, "Ignored recovery name");
        var recoveredSession = await CsrfAsync(recoveryClient);
        Check(recoveredSession.GetProperty("account").GetProperty("id").GetString() == groupOneUserId,
            "recovery retains the original account rather than creating a replacement identity");
        var recoveredAccess = await JsonAsync(await recoveryClient.GetAsync("/app/v1/launch"));
        Check(!recoveredAccess.GetProperty("allowed").GetBoolean()
            && recoveredAccess.GetProperty("launchAt").GetInt64() == groupOneAt,
            "recovery preserves the account's original launch group membership");
        string? recoveredGroupId = null;
        await SeedAsync(fixture.App, async db =>
        {
            recoveredGroupId = await db.Users.Where(user => user.Id == groupOneUserId)
                .Select(user => user.LaunchGroupId).SingleAsync();
        });
        Check(recoveredGroupId == groupOneId, "recovery leaves stored membership bound to its original group");
        Check((await owner.GetAsync("/app/v1/launch/resource-access")).StatusCode == HttpStatusCode.NoContent,
            "static model and download resources remain available to real administrators");
        const string readbackCredential = "offline-model-readback-credential-123456";
        fixture.App.Configuration["Launch:ResourceReadbackTokenSha256"] = Kufr.Backend.Features.Identity.AccountEndpoints.Hash(readbackCredential);
        using (var readback = new HttpRequestMessage(HttpMethod.Get, "/app/v1/launch/resource-access"))
        {
            readback.Headers.Authorization = new("Bearer", readbackCredential);
            Check((await stranger.SendAsync(readback)).StatusCode == HttpStatusCode.NoContent,
                "narrow resource credential allows deployment readback without enabling legacy administrative credentials");
        }
        using (var adminEscalation = new HttpRequestMessage(HttpMethod.Get, "/v1/admin/test-account-read"))
        {
            adminEscalation.Headers.Authorization = new("Bearer", readbackCredential);
            Check(!(await stranger.SendAsync(adminEscalation)).IsSuccessStatusCode, "resource readback credential grants no admin or guest policy bypass");
        }
        Check((await customer.GetAsync("/app/v1/customer/character")).StatusCode == (HttpStatusCode)423, "legacy enrichment API is gated");
        Check((await customer.GetAsync("/app/v1/admin/launch")).StatusCode == HttpStatusCode.Forbidden, "guest cannot view launch configuration or prompt");
        Check((await customer.PostAsJsonAsync("/app/v1/admin/launch/personalization-preview", new { name = "R", universe = "F", prompt = "test" }))
            .StatusCode == HttpStatusCode.Forbidden, "guest cannot incur admin preview generation");
        var initial = await JsonAsync(await customer.GetAsync("/app/v1/prelaunch/character"));
        Check(initial.GetProperty("character").ValueKind == JsonValueKind.Null, "new guest can reach character creation while gated");
        var response = await customer.PutAsJsonAsync("/app/v1/prelaunch/character",
            new { expectedRevision = 0, name = "Raven", universe = "Original fiction", openAiConsent = true });
        var saved = await JsonAsync(response);
        var character = saved.GetProperty("character");
        var revision = character.GetProperty("revision").GetInt64();
        var profileId = character.GetProperty("profileId").GetString()!;
        Check(character.GetProperty("name").GetString() == "Raven" && saved.GetProperty("artwork").GetProperty("status").GetString() == "queued",
            "two attributes save immediately with artwork pending");
        Check((await customer.PutAsJsonAsync("/app/v1/prelaunch/character",
            new { expectedRevision = 0, name = "Other", universe = "Fiction", openAiConsent = true })).StatusCode == HttpStatusCode.Conflict,
            "second concurrent client cannot create a second character");
        for (var attempt = 0; attempt < 25; attempt++)
        {
            saved = await JsonAsync(await customer.GetAsync("/app/v1/prelaunch/character"));
            if (saved.GetProperty("artwork").GetProperty("status").GetString() == "ready") { break; }
            await Task.Delay(300);
        }
        var text = saved.GetRawText();
        Check(saved.GetProperty("artwork").GetProperty("status").GetString() == "ready", "durable enrichment then fake image provider completes automatically");
        Check(saved.GetProperty("artwork").GetProperty("loadingMessage").GetString() == "Hvězdy se domlouvají.",
            "optional personalized progress message is retained alongside the final result");
        var finalMessage = saved.GetProperty("artwork").GetProperty("finalMessage").GetString()!;
        Check(finalMessage == "Tvá pověst dorazila před tebou. Už jsme se jí omluvili."
            && saved.GetProperty("artwork").GetProperty("welcome").GetProperty("identity").GetString() == "",
            "failed welcome text uses stable fallback, omits unverified identity and preserves the portrait");
        Check(!text.Contains("enrichment", StringComparison.OrdinalIgnoreCase) && !text.Contains("conversation", StringComparison.OrdinalIgnoreCase)
            && !text.Contains("generatedWand", StringComparison.OrdinalIgnoreCase) && !text.Contains("source", StringComparison.OrdinalIgnoreCase)
            && !text.Contains("usedFactIds", StringComparison.Ordinal) && !text.Contains("teaserId", StringComparison.Ordinal),
            "minimal user API contains no enrichment or protected product data");
        var image = await customer.GetAsync($"/app/v1/prelaunch/character/portrait?profileId={profileId}&revision={revision}");
        Check(image.IsSuccessStatusCode && image.Content.Headers.ContentType?.MediaType == "image/webp", "owner gets private artwork");
        Check(image.Headers.CacheControl?.NoStore == true, "private artwork cannot be reused from shared cache");
        Check((await customer.GetAsync($"/app/v1/prelaunch/character/portrait?profileId=stale-profile&revision={revision}")).StatusCode == HttpStatusCode.NotFound,
            "matching revision cannot serve artwork from a different profile identity");
        Check(!(await stranger.GetAsync($"/app/v1/prelaunch/character/portrait?profileId={profileId}&revision={revision}")).IsSuccessStatusCode,
            "signed-out visitor cannot download private artwork");
        var review = await JsonAsync(await customer.GetAsync("/app/v1/prelaunch/character/review"));
        Check(review.GetProperty("profile").GetProperty("enrichment").ValueKind == JsonValueKind.Object,
            "owner can review findings before launch");
        Check(!(await stranger.GetAsync("/app/v1/prelaunch/character/review")).IsSuccessStatusCode,
            "review requires an account");
        var manual = new CharacterReviewWrite(revision, profileId, new("Raven", "Original fiction", Description: "Correct context"),
            Findings: new("Correct Raven", "Original fiction", "Correct summary", ["User supplied fact"], ["Ask about home"], []));
        var corrected = await JsonAsync(await customer.PutAsJsonAsync("/app/v1/prelaunch/character/review", manual));
        var correctedProfile = corrected.GetProperty("profile");
        Check(correctedProfile.GetProperty("revision").GetInt64() == revision + 1
            && correctedProfile.GetProperty("status").GetString() == "ready"
            && correctedProfile.GetProperty("enrichment").GetProperty("evidenceKind").GetString() == "user_edited"
            && correctedProfile.GetProperty("enrichment").GetProperty("sources").GetArrayLength() == 0,
            "manual correction persists a new revision without false provider provenance");
        Check((await customer.GetAsync($"/app/v1/prelaunch/character/portrait?profileId={profileId}&revision={revision}")).StatusCode == HttpStatusCode.NotFound,
            "manual correction invalidates the old portrait");
        Check((await customer.PutAsJsonAsync("/app/v1/prelaunch/character/review", manual)).StatusCode == HttpStatusCode.Conflict,
            "stale corrections cannot overwrite a newer revision");
        Check((await customer.PutAsJsonAsync("/app/v1/prelaunch/character/review",
            manual with { ExpectedRevision = revision + 1, Regenerate = true })).StatusCode == HttpStatusCode.Conflict,
            "regeneration requires affirmative consent");
        var regenerated = await JsonAsync(await customer.PutAsJsonAsync("/app/v1/prelaunch/character/review",
            manual with { ExpectedRevision = revision + 1, Regenerate = true, OpenAiConsent = true }));
        Check(regenerated.GetProperty("profile").GetProperty("revision").GetInt64() == revision + 2
            && regenerated.GetProperty("profile").GetProperty("status").GetString() == "queued",
            "consented regeneration queues revised input");
        var betaInvitation = (await JsonAsync(await owner.PostAsJsonAsync("/app/v1/admin/invitations", new { role = "Beta" })))
            .GetProperty("token").GetString()!;
        await EnrollAsync(beta, betaKey, betaInvitation, "Invited guest");
        await CsrfAsync(beta);
        var betaAccess = await JsonAsync(await beta.GetAsync("/app/v1/launch"));
        Check(betaAccess.GetProperty("audience").GetString() == "beta" && !betaAccess.GetProperty("allowed").GetBoolean(),
            "verified invitation assigns beta audience consistently");
        Check((await beta.GetAsync($"/app/v1/prelaunch/character/portrait?profileId={profileId}&revision={revision}")).StatusCode == HttpStatusCode.NotFound,
            "another authenticated account cannot read portrait by guessing revision");
        Check((await beta.GetAsync("/app/v1/admin/launch/artwork")).StatusCode == HttpStatusCode.Forbidden,
            "beta install entitlement grants no artwork administration");
        await owner.PutAsJsonAsync("/app/v1/admin/launch", update with { ExpectedRevision = 1, BetaUnlocked = true });
        Check((await JsonAsync(await beta.GetAsync("/app/v1/launch"))).GetProperty("allowed").GetBoolean()
            && !(await JsonAsync(await customer.GetAsync("/app/v1/launch"))).GetProperty("allowed").GetBoolean(),
            "manual beta launch leaves production gated");
        await owner.PutAsJsonAsync("/app/v1/admin/launch", update with { ExpectedRevision = 2, BetaUnlocked = true, ProductionUnlocked = true });
        Check((await customer.GetAsync("/app/v1/customer/character")).IsSuccessStatusCode, "server unlock restores existing character experience");
        Check((await beta.PostAsJsonAsync("/app/v1/admin/invitations",
                new { role = "Customer", launchGroupId = groupOneId })).StatusCode == HttpStatusCode.Forbidden
            && (await beta.PostAsJsonAsync("/app/v1/admin/launch/groups",
                new { name = "Unauthorized", launchAt = groupOneAt })).StatusCode == HttpStatusCode.Forbidden,
            "beta members cannot create launch groups or issue group invitations");
        var groupTwoAfterUnlock = await JsonAsync(await groupTwoClient.GetAsync("/app/v1/launch"));
        var recoveredAfterUnlock = await JsonAsync(await recoveryClient.GetAsync("/app/v1/launch"));
        Check(!groupTwoAfterUnlock.GetProperty("allowed").GetBoolean()
            && !recoveredAfterUnlock.GetProperty("allowed").GetBoolean(),
            $"global unlock cannot bypass future group schedules (now {recoveredAfterUnlock.GetProperty("serverNow")}; group2 {groupTwoAfterUnlock.GetProperty("allowed")}/{groupTwoAfterUnlock.GetProperty("launchAt")}, recovered {recoveredAfterUnlock.GetProperty("allowed")}/{recoveredAfterUnlock.GetProperty("launchAt")}; membership {recoveredGroupId})");
        Check((await groupTwoClient.GetAsync("/app/v1/launch/resource-access")).StatusCode == HttpStatusCode.Forbidden
            && (await recoveryClient.GetAsync("/app/v1/launch/resource-access")).StatusCode == HttpStatusCode.Forbidden,
            "group-specific launch gates also protect the resource-access boundary after a global unlock");
        Check((await stranger.GetAsync("/app/v1/launch/resource-access")).StatusCode == HttpStatusCode.NoContent,
            "production launch restores public resource access");
        await SeedAsync(fixture.App, db => db.Users.Where(user => user.Id == groupTwoUserId)
            .ExecuteUpdateAsync(updateUser => updateUser
                .SetProperty(user => user.LaunchGroupRevokedAt, DateTimeOffset.UtcNow.ToUnixTimeSeconds())
                .SetProperty(user => user.AdmissionUniverse, "Persistent universe")));
        var revokedAccess = await JsonAsync(await groupTwoClient.GetAsync("/app/v1/customer/access"));
        var revokedMove = await owner.PutAsJsonAsync($"/app/v1/admin/launch/waiting-room/{groupTwoUserId}/group",
            new
            {
                expectedMembershipRevision = revokedAccess.GetProperty("accessRevision").GetInt64(),
                expectedSourceGroupId = groupTwoId,
                targetGroupId = groupOneId
            });
        Check(revokedMove.IsSuccessStatusCode,
            "owner can reorganize a revoked customer without reactivating them");
        var retainedRevocation = await fixture.App.Services.GetRequiredService<AccountDatabase>().Users
            .AsNoTracking().Where(user => user.Id == groupTwoUserId)
            .Select(user => new { user.LaunchGroupId, user.LaunchGroupRevokedAt,
                user.LaunchGroupAccessRevision, user.AdmissionUniverse }).SingleAsync();
        var rosterAfterRevokedMove = await JsonAsync(await owner.GetAsync("/app/v1/admin/launch/waiting-room"));
        Check(retainedRevocation.LaunchGroupId == groupOneId && retainedRevocation.LaunchGroupRevokedAt is not null
            && retainedRevocation.LaunchGroupAccessRevision == revokedAccess.GetProperty("accessRevision").GetInt64() + 1
            && retainedRevocation.AdmissionUniverse == "Persistent universe"
            && rosterAfterRevokedMove.GetProperty("members").EnumerateArray().Any(member =>
                member.GetProperty("userId").GetString() == groupTwoUserId
                && member.GetProperty("state").GetString() == "revoked"),
            "moving a revoked account preserves revocation and fixed universe while advancing its membership revision");
        var groupOneCountdown = await owner.PutAsJsonAsync($"/app/v1/admin/launch/groups/{groupOneId}/countdown",
            new { expectedRevision = groupOne.GetProperty("revision").GetInt64(), durationSeconds = 1, forceStartAtEnd = false });
        var groupOneCountdownGroup = (await JsonAsync(groupOneCountdown)).GetProperty("group");
        var groupOneCountdownConfirmed = await owner.PostAsJsonAsync($"/app/v1/admin/launch/groups/{groupOneId}/countdown/confirm",
            new { expectedRevision = groupOneCountdownGroup.GetProperty("revision").GetInt64() });
        groupOneCountdownGroup = (await JsonAsync(groupOneCountdownConfirmed)).GetProperty("group");
        await Task.Delay(TimeSpan.FromMilliseconds(1100));
        var groupOneStarted = await owner.PostAsJsonAsync($"/app/v1/admin/launch/groups/{groupOneId}/start",
            new { expectedRevision = groupOneCountdownGroup.GetProperty("revision").GetInt64() });
        Check(groupOneStarted.IsSuccessStatusCode,
            "owner can explicitly start a non-Force group after its countdown ends");
        var waitingRoomAfterStart = await JsonAsync(await owner.GetAsync("/app/v1/admin/launch/waiting-room"));
        var remainingWaiters = waitingRoomAfterStart.GetProperty("members").EnumerateArray().ToArray();
        Check(remainingWaiters.Any(member => member.GetProperty("userId").GetString() == unassignedUserId
                && member.GetProperty("groupId").GetString() == groupTwoId)
            && !remainingWaiters.Any(member => member.GetProperty("userId").GetString() == groupOneUserId)
            && remainingWaiters.Any(member => member.GetProperty("userId").GetString() == secondGroupTwoUserId
                && member.GetProperty("groupId").GetString() == groupTwoId)
            && remainingWaiters.Any(member => member.GetProperty("userId").GetString() == groupTwoUserId
                && member.GetProperty("state").GetString() == "revoked"),
            "started group members leave the waiting roster while independent group waiters and revoked accounts remain visible");
        Console.WriteLine("Prelaunch invitation, audience, immediate save, background artwork, private asset and manual launch HTTPS checks passed.");
    }

    private sealed class PrelaunchTestPortraitProvider : ICharacterPortraitProvider
    {
        public Task<byte[]> GeneratePortraitAsync(CharacterPortraitJob job, CancellationToken token) => Task.FromResult(new byte[] { 1, 2, 3 });
        public Task<string?> GenerateMessageAsync(CharacterUserInput input, CharacterEnrichment? enrichment,
            string creativePrompt, CancellationToken token) => creativePrompt == PortraitContentPolicy.FinalMessagePrompt
                ? Task.FromException<string?>(new InvalidOperationException("synthetic optional text failure"))
                : Task.FromResult<string?>("Hvězdy se domlouvají.");
        public Task<CharacterWelcome?> GenerateWelcomeAsync(CharacterPortraitJob job, CancellationToken token) =>
            Task.FromException<CharacterWelcome?>(new InvalidOperationException("synthetic optional welcome failure"));
    }
}
