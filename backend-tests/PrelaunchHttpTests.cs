using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Kufr.Backend.Features.Customers;
using Kufr.Backend.Features.Launch;
using Microsoft.Extensions.DependencyInjection;

internal static partial class AccountIdentityTests
{
    internal static async Task RunPrelaunchHttpAsync()
    {
        await using var fixture = await Fixture.StartAsync(customerFeatures: true,
            configureServices: services => services.AddSingleton<ICharacterPortraitProvider>(new PrelaunchTestPortraitProvider()));
        using var owner = fixture.Client();
        using var customer = fixture.Client();
        using var stranger = fixture.Client();
        using var beta = fixture.Client();
        using var ownerKey = new VirtualPasskey();
        using var customerKey = new VirtualPasskey();
        using var betaKey = new VirtualPasskey();
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
        Check((await stranger.GetAsync("/app/v1/launch/resource-access")).StatusCode == HttpStatusCode.Forbidden
            && (await customer.GetAsync("/app/v1/launch/resource-access")).StatusCode == HttpStatusCode.Forbidden,
            "static resource authorizer denies anonymous and gated accounts with nginx-compatible status");
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
        Check(finalMessage.Contains("Raven", StringComparison.Ordinal)
            && finalMessage.Contains("kocovin", StringComparison.OrdinalIgnoreCase)
            && finalMessage.Length <= 250,
            "failed final-text generation uses bounded character-specific prelaunch fallback while preserving the portrait");
        Check(!text.Contains("enrichment", StringComparison.OrdinalIgnoreCase) && !text.Contains("conversation", StringComparison.OrdinalIgnoreCase)
            && !text.Contains("generatedWand", StringComparison.OrdinalIgnoreCase) && !text.Contains("source", StringComparison.OrdinalIgnoreCase),
            "minimal user API contains no enrichment or protected product data");
        var image = await customer.GetAsync($"/app/v1/prelaunch/character/portrait?profileId={profileId}&revision={revision}");
        Check(image.IsSuccessStatusCode && image.Content.Headers.ContentType?.MediaType == "image/webp", "owner gets private artwork");
        Check(image.Headers.CacheControl?.NoStore == true, "private artwork cannot be reused from shared cache");
        Check((await customer.GetAsync($"/app/v1/prelaunch/character/portrait?profileId=stale-profile&revision={revision}")).StatusCode == HttpStatusCode.NotFound,
            "matching revision cannot serve artwork from a different profile identity");
        Check(!(await stranger.GetAsync($"/app/v1/prelaunch/character/portrait?profileId={profileId}&revision={revision}")).IsSuccessStatusCode,
            "signed-out visitor cannot download private artwork");
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
        Check((await stranger.GetAsync("/app/v1/launch/resource-access")).StatusCode == HttpStatusCode.NoContent,
            "production launch restores public resource access");
        Console.WriteLine("Prelaunch invitation, audience, immediate save, background artwork, private asset and manual launch HTTPS checks passed.");
    }

    private sealed class PrelaunchTestPortraitProvider : ICharacterPortraitProvider
    {
        public Task<byte[]> GeneratePortraitAsync(CharacterPortraitJob job, CancellationToken token) => Task.FromResult(new byte[] { 1, 2, 3 });
        public Task<string?> GenerateMessageAsync(CharacterUserInput input, CharacterEnrichment? enrichment,
            string creativePrompt, CancellationToken token) => creativePrompt == PortraitContentPolicy.FinalMessagePrompt
                ? Task.FromException<string?>(new InvalidOperationException("synthetic optional text failure"))
                : Task.FromResult<string?>("Hvězdy se domlouvají.");
    }
}
