using System.Net;
using System.Net.Http.Json;
using Kufr.Backend.Features.Customers;
using Kufr.Backend.Features.Identity;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SkiaSharp;

internal static class CharacterAdministrationTests
{
    internal static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "dv-character-admin-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new CharacterProfileStore(root);
            var initial = await store.SaveAsync("customer", new(0, new("Raven", "Fiction"), OpenAiConsent: true, PortraitConsent: true));
            await store.WriteLabelAsync("customer", new(initial.ProfileId, "Display-only label"));
            var job = (await store.ClaimAsync())!;
            var edited = store.EditAdminCharacter(initial.ProfileId, new(initial.Revision, new("Robin", "Fiction")));
            Check(edited.Revision == 2 && edited.Status == "saved" && edited.Enrichment is null && edited.Portrait is null,
                "manual edit clears unfinished enrichment/portrait and advances revision");
            Check(!await store.CompleteAsync(job, null, "late") && !await store.CanDispatchAsync(job), "text edit fences late enrichment");
            Check((await store.ReadAsync("customer"))?.Label == "Display-only label" && edited.UserInput.Name == "Robin",
                "manual admin edits preserve display labels separately from AI identity");
            Expect("character_revision_conflict", () => store.EditAdminCharacter(initial.ProfileId, new(1, initial.UserInput)));
            await store.RequestEnrichmentAsync("customer");
            var enrichedJob = (await store.ClaimAsync())!;
            var enrichment = new CharacterEnrichment("Robin", "Fiction", "A fictional traveler.", [], [], [], false, [],
                "fixture", "admin-test", 1, enrichedJob.Revision, "synthetic");
            Check(await store.CompleteAsync(enrichedJob, enrichment, null), "fixture enrichment ready");
            // Requesting a portrait is an explicitly consented fixture setup, never an admin side effect.
            await store.SaveAsync("customer", new(edited.Revision, edited.UserInput, edited.ProfileId, OpenAiConsent: true, PortraitConsent: true));
            var portraitJob = store.ClaimPortrait()!;
            Check(portraitJob is not null, "fixture portrait lease");
            var normalized = Png();
            normalized = CharacterAdminEndpoints.NormalizePortrait(normalized, "image/png");
            using (var data = SKData.CreateCopy(normalized))
            using (var codec = SKCodec.Create(data))
            {
                Check(codec.EncodedFormat == SKEncodedImageFormat.Webp, "upload normalized to webp");
            }
            var replaced = store.ReplaceAdminPortrait(edited.ProfileId, edited.Revision, normalized);
            Check(replaced.Revision == 3 && replaced.Enrichment?.InputRevision == 3 && replaced.Portrait?.HasImage == true,
                "upload retains enrichment and increments profile revision");
            Check((await store.ReadAsync("customer"))?.Label == "Display-only label"
                && !System.Text.Json.JsonSerializer.Serialize(WelcomeWriterInput.Build(portraitJob!)).Contains("Display-only label"),
                "portrait replacement preserves the label without adding it to welcome authoring input");
            Check(!store.CompletePortrait(portraitJob!, normalized, null), "late portrait worker cannot overwrite upload");
            var secondEdit = store.EditAdminCharacter(replaced.ProfileId, new(replaced.Revision, new("New name", "Fiction")));
            Check(secondEdit.Enrichment is null && secondEdit.Portrait?.HasImage == true
                && store.ReadAdminPortrait(secondEdit.ProfileId, secondEdit.Revision)!.SequenceEqual(normalized)
                && store.ReadAdminPortrait(secondEdit.ProfileId, replaced.Revision) is null, "text edit rebinds finished portrait");
            Expect("invalid_character_portrait", () => CharacterAdminEndpoints.NormalizePortrait([1, 2, 3], "image/png"));
            Expect("invalid_character_portrait", () => CharacterAdminEndpoints.NormalizePortrait(Png(), "image/jpeg"));
            var oversized = new DefaultHttpContext();
            oversized.Request.ContentType = "image/png";
            oversized.Request.Body = new MemoryStream(new byte[CharacterAdminEndpoints.MaximumUploadBytes + 1]);
            try { await CharacterAdminEndpoints.ReadPortraitUploadAsync(oversized.Request, default); throw new Exception("unbounded upload accepted"); }
            catch (CharacterException exception) when (exception.Code == "character_portrait_too_large") { }
            await store.FenceAccountDeletionAsync("customer");
            Check(store.ReadAdminCharacters().Items.Length == 0 && store.ReadAdminCharacter(secondEdit.ProfileId) is null
                && store.ReadAdminPortrait(secondEdit.ProfileId, secondEdit.Revision) is null, "account deletion hides admin data and image");
            Expect("character_not_found", () => store.ReplaceAdminPortrait(secondEdit.ProfileId, secondEdit.Revision, normalized));
            for (var index = 0; index < 57; index++)
            {
                await store.SaveAsync($"page-{index}", new(0,
                    new(new string('ž', 100), new string('č', 200), Description: new string('ř', 2000)), OpenAiConsent: true));
            }
            var firstPage = store.ReadAdminCharacters();
            var secondPage = store.ReadAdminCharacters(firstPage.NextCursor);
            Check(firstPage.Items.Length == 50 && firstPage.NextCursor is not null
                && secondPage.Items.Length == 7 && secondPage.NextCursor is null, "admin list has bounded pages");
            Check(firstPage.Items.Concat(secondPage.Items).Select(item => item.ProfileId).Distinct().Count() == 57,
                "keyset pagination omits no existing profile and repeats none");
            var pageBytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(firstPage,
                new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
            Check(pageBytes.Length < 256 * 1024 && !System.Text.Encoding.UTF8.GetString(pageBytes).Contains("description"),
                "compact Unicode list stays below native response limit without full descriptions");
            Expect("invalid_character_cursor", () => store.ReadAdminCharacters("../invalid"));
            await AccountIdentityTests.RunCharacterAdministrationHttpAsync();
            Console.WriteLine("Character administration revision, upload, stale worker, deletion and HTTP authorization tests passed.");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) { Directory.Delete(root, true); }
        }
    }

    internal static byte[] Png(bool noisy = false)
    {
        using var bitmap = new SKBitmap(256, 256);
        bitmap.Erase(SKColors.Red);
        if (noisy)
        {
            var random = new Random(37);
            for (var y = 0; y < bitmap.Height; y++)
            {
                for (var x = 0; x < bitmap.Width; x++)
                {
                    bitmap.SetPixel(x, y, new SKColor((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256)));
                }
            }
        }
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        return encoded.ToArray();
    }

    private static void Expect(string code, Action action)
    {
        try { action(); }
        catch (CharacterException exception) when (exception.Code == code) { return; }
        throw new InvalidOperationException("Expected " + code);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) { throw new InvalidOperationException("Character administration: " + message); }
    }
}

internal static partial class AccountIdentityTests
{
    internal static async Task RunCharacterAdministrationHttpAsync()
    {
        await using var fixture = await Fixture.StartAsync(customerFeatures: true);
        using var owner = fixture.Client();
        using var anonymous = fixture.Client();
        using var key = new VirtualPasskey();
        Check((await anonymous.GetAsync("/app/v1/admin/characters")).StatusCode == HttpStatusCode.Unauthorized, "anonymous character admin denied");
        await EnrollAsync(owner, key, Fixture.Bootstrap, "Owner");
        var session = await CsrfAsync(owner);
        var ownerId = session.GetProperty("account").GetProperty("id").GetString()!;
        var store = fixture.App.Services.GetRequiredService<CharacterProfileStore>();
        var profile = await store.SaveAsync(ownerId, new(0, new("Raven", "Fiction"), OpenAiConsent: true));
        Check((await owner.GetAsync("/app/v1/admin/characters")).IsSuccessStatusCode, "owner list allowed");
        owner.DefaultRequestHeaders.Remove("X-DV-Confirm");
        var request = new AdminCharacterWriteRequest(profile.Revision, new("Robin", "Fiction"));
        Check((await owner.PutAsJsonAsync("/app/v1/admin/characters/" + profile.ProfileId, request)).StatusCode == HttpStatusCode.Conflict,
            "manual edit confirmation required");
        owner.DefaultRequestHeaders.Add("X-DV-Confirm", "yes");
        var saved = await owner.PutAsJsonAsync("/app/v1/admin/characters/" + profile.ProfileId, request);
        Check(saved.IsSuccessStatusCode, "owner manual edit succeeds");
        var updated = await JsonAsync(saved);
        var rawImage = CharacterAdministrationTests.Png(noisy: true);
        Check(rawImage.Length > 65536, "HTTP image fixture exceeds account JSON limit");
        using var upload = new ByteArrayContent(rawImage);
        upload.Headers.ContentType = new("image/png");
        var uploadResponse = await owner.PutAsync($"/app/v1/admin/characters/{profile.ProfileId}/portrait?expectedRevision={updated.GetProperty("revision").GetInt64()}", upload);
        Check(uploadResponse.IsSuccessStatusCode, "raw portrait over 64KiB accepted with CSRF and recent proof");
        var uploaded = await JsonAsync(uploadResponse);
        var imageResponse = await owner.GetAsync($"/app/v1/admin/characters/{profile.ProfileId}/portrait?revision={uploaded.GetProperty("revision").GetInt64()}");
        Check(imageResponse.IsSuccessStatusCode && imageResponse.Content.Headers.ContentType?.MediaType == "image/webp"
            && imageResponse.Headers.CacheControl?.NoStore == true, "private portrait retrieval is normalized and no-store");
        using var noCsrf = fixture.Client();
        await SignInAsync(noCsrf, key);
        noCsrf.DefaultRequestHeaders.Remove("X-DV-CSRF");
        var deniedCsrf = await noCsrf.PutAsJsonAsync("/app/v1/admin/characters/" + profile.ProfileId, request);
        Check(deniedCsrf.StatusCode == HttpStatusCode.BadRequest, "admin write requires global CSRF proof");
        var invitation = await JsonAsync(await owner.PostAsJsonAsync("/app/v1/admin/invitations", new { role = "Operator" }));
        using var member = fixture.Client();
        using var memberKey = new VirtualPasskey();
        await EnrollAsync(member, memberKey, invitation.GetProperty("token").GetString()!, "Operator");
        await CsrfAsync(member);
        Check((await member.GetAsync("/app/v1/admin/characters")).StatusCode == HttpStatusCode.Forbidden
            && (await member.PutAsJsonAsync("/app/v1/admin/characters/" + profile.ProfileId, request)).StatusCode == HttpStatusCode.Forbidden,
            "authenticated nonadmin cannot read or edit characters");
        using (var scope = fixture.App.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AccountDatabase>();
            await db.Sessions.Where(item => item.UserId == ownerId).ExecuteUpdateAsync(update => update.SetProperty(item => item.AuthenticatedAt, 1));
        }
        var stale = await owner.PutAsJsonAsync("/app/v1/admin/characters/" + profile.ProfileId, request);
        Check(stale.StatusCode == HttpStatusCode.Forbidden && (await stale.Content.ReadAsStringAsync()).Contains("recent_passkey_required"),
            "manual edit recent passkey required");
    }
}
