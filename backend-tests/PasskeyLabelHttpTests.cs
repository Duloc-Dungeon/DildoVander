using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;

internal static partial class AccountIdentityTests
{
    internal static async Task RunPasskeyLabelsAsync()
    {
        await using var fixture = await Fixture.StartAsync();
        using var owner = fixture.Client();
        using var ownerKey = new VirtualPasskey();
        await CsrfAsync(owner);
        var options = await JsonAsync(await owner.PostAsJsonAsync("/app/v1/enrollment/options",
            new { token = Fixture.Bootstrap, displayName = "Vojta", openAiConsent = true, consentVersion = Kufr.Backend.Features.Identity.AccountAiConsent.CurrentVersion }));
        Check(options.GetProperty("user").GetProperty("name").GetString() == "Wand Night Stand Owner · Olivander",
            "web and native registration receive a readable account and role instead of a UUID");
        Check(options.GetProperty("user").GetProperty("displayName").GetString() == "Wand Night Stand Owner · Olivander", "both provider labels identify the account");
        var handle = options.GetProperty("user").GetProperty("id").GetString()!;
        Check((await owner.PostAsJsonAsync("/app/v1/enrollment/complete", new { credential = ownerKey.Create(options) })).IsSuccessStatusCode,
            "readable labels preserve actual WebAuthn attestation");
        var session = await CsrfAsync(owner);
        Check(Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(handle)) == session.GetProperty("account").GetProperty("id").GetString(),
            "opaque stable WebAuthn user handle remains the persistent account ID");
        Check(session.GetProperty("account").GetProperty("displayName").GetString() == "Olivander", "Owner keeps its fixed display name despite a supplied enrollment name or custom credential label");
        var details = await JsonAsync(await owner.GetAsync("/app/v1/account"));
        var originalName = details.GetProperty("passkeys")[0].GetProperty("name").GetString()!;
        Check(originalName.StartsWith("Wand Night Stand Owner · Olivander · ") && originalName.EndsWith(" UTC") && originalName.Length <= 80,
            "new app credential metadata has a bounded readable label and actual creation timestamp");

        using var extraKey = new VirtualPasskey();
        var extraOptions = await JsonAsync(await owner.PostAsJsonAsync("/app/v1/passkeys/options", new { }));
        Check(extraOptions.GetProperty("user").GetProperty("id").GetString() == handle
            && extraOptions.GetProperty("user").GetProperty("name").GetString() == "Wand Night Stand Owner · Olivander", "adding a key keeps the stable handle and readable name");
        Check((await owner.PostAsJsonAsync("/app/v1/passkeys/complete", new { credential = extraKey.Create(extraOptions) })).IsSuccessStatusCode, "additional real credential verifies");
        var keyId = details.GetProperty("passkeys")[0].GetProperty("id").GetString();
        Check((await owner.PutAsJsonAsync($"/app/v1/passkeys/{keyId}", new { name = "Osobní iPhone" })).StatusCode == HttpStatusCode.NoContent, "custom device label remains supported");
        using var resumed = fixture.Client();
        await SignInAsync(resumed, ownerKey);
        var renamed = await JsonAsync(await resumed.GetAsync("/app/v1/account"));
        Check(renamed.GetProperty("passkeys").EnumerateArray().Any(key => key.GetProperty("name").GetString() == "Osobní iPhone"), "sign-in never overwrites a user's custom key label");

        using var customer = fixture.Client();
        using var customerKey = new VirtualPasskey();
        await CsrfAsync(customer);
        var customerOptions = await JsonAsync(await customer.PostAsJsonAsync("/app/v1/customer/enrollment/options", new { displayName = "Alena", openAiConsent = true, consentVersion = Kufr.Backend.Features.Identity.AccountAiConsent.CurrentVersion }));
        Check(customerOptions.GetProperty("user").GetProperty("name").GetString() == "Wand Night Stand Customer · Alena", "public customer signup also has recognizable provider labels");
        Check((await customer.PostAsJsonAsync("/app/v1/customer/enrollment/complete", new { credential = customerKey.Create(customerOptions) })).IsSuccessStatusCode, "customer label preserves normal signup");
        Console.WriteLine("PASS readable passkey provider/app labels, stable handles, real attestation/add/sign-in, custom names, and recovery role binding");
    }
}
