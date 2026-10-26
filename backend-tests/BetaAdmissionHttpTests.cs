using System.Net;
using System.Net.Http.Json;
using Kufr.Backend.Features.Identity;
using Kufr.Backend.Features.Customers;
using Kufr.Backend.Features.Launch;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

internal static partial class AccountIdentityTests
{
    internal static async Task RunBetaAdmissionHttpAsync()
    {
        await using var fixture = await Fixture.StartAsync(customerFeatures: true, betaRecruitmentOpen: false,
            configureServices: services => services.AddSingleton<ICharacterPortraitProvider>(new PrelaunchTestPortraitProvider()));
        using var owner = fixture.Client();
        using var guest = fixture.Client();
        using var secondPhone = fixture.Client();
        using var unassigned = fixture.Client();
        using var ownerKey = new VirtualPasskey();
        using var guestKey = new VirtualPasskey();
        using var unassignedKey = new VirtualPasskey();

        await EnrollAsync(owner, ownerKey, Fixture.Bootstrap, "Owner");
        await CsrfAsync(owner);
        var launch = fixture.App.Services.GetRequiredService<LaunchPolicyStore>();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var selected = launch.CreateOrSelectBetaGroup("fixture-owner",
            new(launch.ReadBetaAdmission().Revision), now);
        await CsrfAsync(guest);
        var closedStatus = await guest.GetAsync("/app/v1/enrollment/default");
        Check(closedStatus.IsSuccessStatusCode
            && !(await JsonAsync(closedStatus)).GetProperty("available").GetBoolean(),
            "default enrollment reports closed until owner explicitly opens it");
        Check((int)(await guest.PostAsJsonAsync("/app/v1/customer/enrollment/options",
            new { displayName = "Guest" })).StatusCode == 423,
            "closed recruitment rejects public customer enrollment");

        var group = launch.ReadGroup(selected.Group.Id)!;
        launch.UpdateGroup("fixture-owner", group.Id, new(group.Revision, group.Name, now + 3_600_000), now);
        var opened = launch.UpdateBetaAdmission("fixture-owner",
            new(selected.Admission.Revision, group.Id, true, "The Shire"), now);
        var optionsResponse = await guest.PostAsJsonAsync("/app/v1/customer/enrollment/options",
            new { displayName = "Guest" });
        var options = await JsonAsync(optionsResponse);
        var closedBeforeFinish = launch.UpdateBetaAdmission("fixture-owner",
            new(opened.Revision, group.Id, false, "The Shire"), now + 1);
        var rejectedAfterClose = await guest.PostAsJsonAsync("/app/v1/customer/enrollment/complete",
            new { credential = guestKey.Create(options) });
        Check((int)rejectedAfterClose.StatusCode == 409
            && (await ReadJsonAsync(rejectedAfterClose)).GetProperty("code").GetString() == "beta_enrollment_closed_or_changed",
            "closing recruitment invalidates an already-issued passkey ceremony");

        var other = launch.CreateGroup("fixture-owner", new("Other group", null), now + 2);
        var reopened = launch.UpdateBetaAdmission("fixture-owner",
            new(closedBeforeFinish.Revision, group.Id, true, "The Shire"), now + 3);
        var changedOptions = await JsonAsync(await guest.PostAsJsonAsync("/app/v1/customer/enrollment/options",
            new { displayName = "Changed target" }));
        var retargeted = launch.UpdateBetaAdmission("fixture-owner",
            new(reopened.Revision, other.Id, true, null), now + 4);
        using var rejectedAfterRetarget = await guest.PostAsJsonAsync("/app/v1/customer/enrollment/complete",
            new { credential = guestKey.Create(changedOptions) });
        Check((int)rejectedAfterRetarget.StatusCode == 409,
            "changing the default group invalidates an already-issued passkey ceremony");

        var finalConfig = launch.UpdateBetaAdmission("fixture-owner",
            new(retargeted.Revision, group.Id, true, "The Shire"), now + 5);
        var finalOptions = await JsonAsync(await guest.PostAsJsonAsync("/app/v1/customer/enrollment/options",
            new { displayName = "Guest" }));
        using var completed = await guest.PostAsJsonAsync("/app/v1/customer/enrollment/complete",
            new { credential = guestKey.Create(finalOptions) });
        Check(completed.IsSuccessStatusCode, "open recruitment accepts new customer through the selected group");
        var guestSession = await CsrfAsync(guest);
        var accountId = guestSession.GetProperty("account").GetProperty("id").GetString()!;
        var account = await fixture.App.Services.GetRequiredService<AccountDatabase>().Users
            .SingleAsync(user => user.Id == accountId);
        Check(account.LaunchGroupId == group.Id && account.AdmissionUniverse == "The Shire"
            && account.LaunchGroupAccessRevision == 1,
            "membership and fixed universe are saved on the account using server-selected values");
        Check(finalConfig.RecruitmentOpen, "owner can reopen enrollment after a rejected stale ceremony");

        await CsrfAsync(owner);
        var invitation = await JsonAsync(await owner.PostAsJsonAsync("/app/v1/admin/invitations",
            new { role = AccountRoles.Customer, recoveryUserId = (string?)null, launchGroupId = (string?)null }));
        await EnrollAsync(unassigned, unassignedKey, invitation.GetProperty("token").GetString()!, "Unassigned");
        var unassignedSession = await CsrfAsync(unassigned);
        var unassignedId = unassignedSession.GetProperty("account").GetProperty("id").GetString()!;
        var beforeAdmission = await ReadJsonAsync(await unassigned.GetAsync("/app/v1/customer/access"));
        Check(beforeAdmission.GetProperty("state").GetString() == "unassigned",
            "existing account remains unassigned until it explicitly requests beta access");
        using var admissionResponse = await unassigned.PostAsJsonAsync("/app/v1/enrollment/default/admit", new { });
        var admitted = await JsonAsync(admissionResponse);
        Check(admitted.GetProperty("state").GetString() == "active"
            && admitted.GetProperty("groupId").GetString() == group.Id
            && admitted.GetProperty("fixedUniverse").GetString() == "The Shire",
            "explicit request joins an existing account to the configured beta group");
        using var duplicateAdmission = await unassigned.PostAsJsonAsync("/app/v1/enrollment/default/admit", new { });
        Check((int)duplicateAdmission.StatusCode == 409,
            "repeated explicit request cannot create duplicate membership or silently move an account");
        var admittedAccount = await fixture.App.Services.GetRequiredService<AccountDatabase>().Users
            .SingleAsync(user => user.Id == unassignedId);
        Check(admittedAccount.LaunchGroupId == group.Id && admittedAccount.LaunchGroupAccessRevision == 1,
            "explicit admission commits group membership and revision on the account");

        Check((await guest.PutAsJsonAsync("/app/v1/prelaunch/character",
            new { expectedRevision = 0, name = "Guest", universe = "The Shire" })).StatusCode == HttpStatusCode.Conflict,
            "membership does not imply AI consent");
        Check((await guest.PutAsJsonAsync("/app/v1/prelaunch/character",
            new { expectedRevision = 0, name = "Guest", universe = "Forged", openAiConsent = true })).StatusCode == HttpStatusCode.Conflict,
            "direct character writes cannot forge the fixed universe");
        var prepared = await JsonAsync(await guest.PutAsJsonAsync("/app/v1/prelaunch/character",
            new { expectedRevision = 0, name = "Guest", universe = "The Shire", openAiConsent = true }));
        var preparedId = prepared.GetProperty("character").GetProperty("profileId").GetString();
        Check((await guest.PutAsJsonAsync("/app/v1/prelaunch/character/review",
            new { expectedRevision = 1, expectedProfileId = preparedId, userInput = new { name = "Guest", world = "Forged" }, regenerate = true, openAiConsent = true }))
            .StatusCode == HttpStatusCode.Conflict, "review regeneration cannot bypass fixed universe admission");
        launch.UpdateBetaAdmission("fixture-owner",
            new(finalConfig.Revision, group.Id, false, "Another universe"), now + 6);
        Check((await guest.GetAsync("/app/v1/prelaunch/character")).IsSuccessStatusCode,
            "closing recruitment does not block an existing member's waiting room");

        var memberPath = $"/app/v1/admin/launch/groups/{group.Id}/members/{accountId}";
        await CsrfAsync(owner);
        var revokedResponse = await owner.PutAsJsonAsync(memberPath, new { expectedRevision = 1, active = false });
        var revoked = await JsonAsync(revokedResponse);
        Check(!revoked.GetProperty("accessActive").GetBoolean()
            && revoked.GetProperty("accessRevision").GetInt64() == 2,
            "owner revokes membership with a revisioned server-side status");
        var guestAccess = await JsonAsync(await guest.GetAsync("/app/v1/customer/access"));
        Check(guestAccess.GetProperty("state").GetString() == "revoked",
            "existing session reads explicit revoked access state");
        using var rejectedGameRequest = await guest.GetAsync("/app/v1/customer/character");
        Check((int)rejectedGameRequest.StatusCode == 403
            && (await ReadJsonAsync(rejectedGameRequest)).GetProperty("code").GetString() == "customer_access_revoked",
            "revoked existing session cannot enter a direct customer endpoint");

        await SignInAsync(secondPhone, guestKey);
        using var rejectedSecondPhone = await secondPhone.GetAsync("/app/v1/customer/character");
        Check((int)rejectedSecondPhone.StatusCode == 403,
            "revocation applies to a second phone signing in after the change");
        Check((await guest.GetAsync("/app/v1/account")).IsSuccessStatusCode,
            "revoked member retains own account and privacy surface");
        Check((await guest.GetAsync("/app/v1/prelaunch/character")).StatusCode == HttpStatusCode.Forbidden,
            "prelaunch-safe character routes cannot bypass revocation");
        Check((await guest.PostAsJsonAsync("/app/v1/enrollment/default/admit", new { })).StatusCode == HttpStatusCode.Forbidden,
            "revoked member cannot self-admit again");
        var restoredResponse = await owner.PutAsJsonAsync(memberPath, new { expectedRevision = 2, active = true });
        var restored = await JsonAsync(restoredResponse);
        Check(restored.GetProperty("accessActive").GetBoolean()
            && restored.GetProperty("accessRevision").GetInt64() == 3
            && restored.GetProperty("userId").GetString() == accountId,
            "restoration retains the same account and increments its access revision");
        var restoredCharacter = await JsonAsync(await guest.GetAsync("/app/v1/prelaunch/character"));
        Check(restoredCharacter.GetProperty("character").GetProperty("profileId").GetString() == preparedId,
            "restoring during closed recruitment returns the same saved character");

        Console.WriteLine("Beta enrollment configuration, ceremony fencing and member access HTTP tests passed.");
    }

    private static async Task<System.Text.Json.JsonElement> ReadJsonAsync(HttpResponseMessage response) =>
        System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
}
