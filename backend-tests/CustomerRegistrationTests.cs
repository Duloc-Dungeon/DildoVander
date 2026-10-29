using System.Net;
using System.Net.Http.Json;
using Kufr.Backend.Features.Customers;
using Kufr.Backend.Features.Identity;
using Kufr.Backend.Features.Launch;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

internal static partial class AccountIdentityTests
{
    internal static async Task RunCustomerRegistrationAsync()
    {
        await using var fixture = await Fixture.StartAsync(customerFeatures: true, betaRecruitmentOpen: false);
        using var customer = fixture.Client();
        using var owner = fixture.Client();
        using var ownerKey = new VirtualPasskey();
        using var key = new VirtualPasskey();
        var path = "/app/v1/customer/enrollment";
        Check((await customer.PostAsJsonAsync(path + "/options", new { displayName = "Guest", openAiConsent = true })).StatusCode
            == HttpStatusCode.BadRequest, "public customer enrollment requires CSRF");
        await CsrfAsync(customer);
        Check((await customer.PostAsJsonAsync(path + "/options", new { displayName = "Guest", openAiConsent = true })).StatusCode
            == HttpStatusCode.ServiceUnavailable, "public signup cannot preempt owner bootstrap");
        await EnrollAsync(owner, ownerKey, Fixture.Bootstrap, "Owner");
        var launch = fixture.App.Services.GetRequiredService<LaunchPolicyStore>();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var initialAdmission = launch.ReadBetaAdmission();
        var selected = launch.CreateOrSelectBetaGroup("fixture-owner", new(initialAdmission.Revision), now);
        launch.UpdateBetaAdmission("fixture-owner",
            new(selected.Admission.Revision, selected.Group.Id, true, null, selected.Group.Revision, now + 3_600_000), now);
        customer.DefaultRequestHeaders.Remove("Origin");
        customer.DefaultRequestHeaders.Add("Origin", "https://wrong.example");
        Check((await customer.PostAsJsonAsync(path + "/options", new { displayName = "Guest", openAiConsent = true })).StatusCode
            == HttpStatusCode.Forbidden, "customer registration checks exact origin");
        customer.DefaultRequestHeaders.Remove("Origin");
        customer.DefaultRequestHeaders.Add("Origin", "https://localhost");
        foreach (var consent in new[] { (bool?)null, false })
        {
            var response = consent is null
                ? await customer.PostAsJsonAsync(path + "/options", new { displayName = "Guest" })
                : await customer.PostAsJsonAsync(path + "/options", new { displayName = "Guest", openAiConsent = consent.Value });
            Check(response.StatusCode == HttpStatusCode.Conflict
                && System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("code").GetString() == "ai_consent_required",
                "server rejects missing and false registration consent before issuing a credential ceremony");
        }
        var options = await JsonAsync(await customer.PostAsJsonAsync(path + "/options",
            new { displayName = "Guest", role = "Owner", userId = "forged-owner", openAiConsent = true }));
        var credential = key.Create(options);
        Check((await customer.PostAsJsonAsync(path + "/complete", new { credential, role = "Owner" })).IsSuccessStatusCode,
            "customer completes verified passkey signup");
        var identity = await CsrfAsync(customer);
        var account = identity.GetProperty("account");
        var customerId = account.GetProperty("id").GetString()!;
        Check(account.GetProperty("openAiConsent").GetBoolean(), "registration consent is projected from saved account evidence");
        Check(account.GetProperty("roles").EnumerateArray().Select(role => role.GetString()).SequenceEqual(["Customer"]),
            "registration role is exclusively server-selected Customer");
        var member = await fixture.App.Services.GetRequiredService<AccountDatabase>().Users
            .SingleAsync(user => user.Id == customerId);
        Check(member.OpenAiConsentAt is > 0 && member.OpenAiConsentVersion == AccountAiConsent.CurrentVersion,
            "verified enrollment persists its server-side consent evidence");
        Check(member.LaunchGroupId == selected.Group.Id && member.LaunchGroupAccessRevision == 1
            && member.LaunchGroupRevokedAt is null,
            "customer joins only the configured default group and starts active even while group launch is future");
        Check((await customer.GetAsync("/app/v1/prelaunch/character")).IsSuccessStatusCode,
            "new member can prepare a character before the group's launch");
        LaunchTestSchedule.Set(launch, selected.Group.Id, now - 1000, now);
        Check((await customer.GetAsync("/app/v1/admin/users")).StatusCode == HttpStatusCode.Forbidden,
            "customer has no account administration permission");
        await SeedAsync(fixture.App, async db =>
        {
            db.ResourceGrants.Add(new AccountResourceGrant
            {
                Id = Guid.NewGuid().ToString("N"), UserId = customerId,
                ResourceType = "device", ResourceId = "granted-device", Permission = "manage"
            });
            await db.SaveChangesAsync();
        });
        var details = await JsonAsync(await customer.GetAsync("/app/v1/account"));
        Check(details.GetProperty("devices").GetArrayLength() == 0,
            "Customer cannot inherit staff device capabilities even from erroneous resource grants");
        var initial = await JsonAsync(await customer.PutAsJsonAsync("/app/v1/customer/character",
            new CharacterWriteRequest(0, new("  Gandalf  ", "The Lord of the Rings", Description: "My own version"))));
        var profile = initial.GetProperty("profile");
        Check(profile.GetProperty("status").GetString() == "queued",
            "persisted registration consent creates the character and queues enrichment without another checkbox");
        var profileId = profile.GetProperty("profileId").GetString()!;
        var revision = profile.GetProperty("revision").GetInt64();
        Check(profile.GetProperty("userInput").GetProperty("name").GetString() == "  Gandalf  ",
            "UserInput preserves exact customer text");
        await CsrfAsync(owner);
        var ownerProfile = await JsonAsync(await owner.GetAsync("/app/v1/customer/character"));
        Check(ownerProfile.GetProperty("profile").ValueKind == System.Text.Json.JsonValueKind.Null,
            "account-derived profile read cannot access another account");
        Check((await customer.PutAsJsonAsync("/app/v1/customer/character",
            new CharacterWriteRequest(0, new("Frodo", "Middle-earth"), OpenAiConsent: true))).StatusCode == HttpStatusCode.Conflict,
            "stale profile edits are rejected");
        Check((await customer.PutAsJsonAsync("/app/v1/customer/character",
            new CharacterWriteRequest(revision, new("Frodo", "Middle-earth"), profileId, OpenAiConsent: true))).StatusCode == HttpStatusCode.Forbidden,
            "customer cannot edit an existing character through direct API");
        Check((await customer.PostAsJsonAsync("/app/v1/customer/character/correction",
            new CharacterCorrectionRequest(revision, profileId, OpenAiConsent: true))).StatusCode == HttpStatusCode.Forbidden,
            "customer cannot refine the interpretation through direct API");
        Check((await customer.PutAsJsonAsync("/app/v1/customer/character",
            new CharacterWriteRequest(revision, new("Second", "Original"), profileId, NewCharacter: true, OpenAiConsent: true))).StatusCode == HttpStatusCode.Conflict,
            "one account cannot replace its character with a second identity");
        Check((await customer.PutAsJsonAsync("/app/v1/prelaunch/character",
            new PrelaunchCharacterWrite(revision, "Other", "Original", profileId, OpenAiConsent: true))).StatusCode == HttpStatusCode.Forbidden,
            "prelaunch API cannot bypass administrator-only character editing");
        customer.DefaultRequestHeaders.Remove("X-DV-CSRF");
        Check((await customer.DeleteAsync($"/app/v1/customer/character?expectedRevision={revision}&expectedProfileId={profileId}")).StatusCode
            == HttpStatusCode.BadRequest, "profile deletion requires CSRF");
        await CsrfAsync(customer);
        Check((await customer.DeleteAsync($"/app/v1/customer/character?expectedRevision={revision}&expectedProfileId={profileId}")).IsSuccessStatusCode,
            "own profile deletion succeeds");
        Check((await customer.DeleteAsync("/app/v1/session")).IsSuccessStatusCode, "customer logout");
        await CsrfAsync(customer);
        Check((await customer.PostAsJsonAsync(path + "/complete", new { credential })).StatusCode == HttpStatusCode.BadRequest,
            "customer attestation ceremony cannot be replayed");
        await SignInAsync(customer, key);
        var signedBackIn = await CsrfAsync(customer);
        Check(signedBackIn.GetProperty("account").GetProperty("id").GetString() == customerId,
            "customer passkey uses existing durable sign-in flow");
        await SeedAsync(fixture.App, db => db.Users.Where(user => user.Id == customerId)
            .ExecuteUpdateAsync(update => update.SetProperty(user => user.OpenAiConsentAt, (long?)null)
                .SetProperty(user => user.OpenAiConsentVersion, 0)));
        Check(!(await CsrfAsync(customer)).GetProperty("account").GetProperty("openAiConsent").GetBoolean(),
            "legacy account has no implied processing consent");
        Check((await customer.PostAsJsonAsync("/app/v1/account/ai-consent", new { openAiConsent = false })).StatusCode
            == HttpStatusCode.Conflict, "legacy consent cannot be granted by a false choice");
        Check((await customer.PostAsJsonAsync("/app/v1/account/ai-consent", new { openAiConsent = true })).StatusCode
            == HttpStatusCode.NoContent, "legacy account can explicitly complete consent");
        Check((await CsrfAsync(customer)).GetProperty("account").GetProperty("openAiConsent").GetBoolean(),
            "explicit legacy choice is durable through a fresh session projection");
        Console.WriteLine("Customer passkey registration, CSRF/origin, role and account isolation passed.");
    }

    private sealed class CustomerTestEnrichmentProvider : ICharacterEnrichmentProvider
    {
        public Task<CharacterEnrichment> EnrichAsync(CharacterJob job, CancellationToken token) => Task.FromResult(
            new CharacterEnrichment(job.UserInput.Name, job.UserInput.World, job.UserInput.Description ?? "Synthetic character fixture.",
                [], ["Synthetic conversation idea."], [], false, [], "fake", "fake-v1",
                DateTimeOffset.UtcNow.ToUnixTimeSeconds(), job.Revision, "synthetic"));
    }
}
