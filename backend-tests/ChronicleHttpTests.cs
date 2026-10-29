using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Kufr.Backend.Features.Chronicle;
using Kufr.Backend.Features.Identity;
using Kufr.Backend.Features.Keepsakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

internal static partial class MobileGameTests
{
    internal static async Task RunChronicleHttpAsync()
    {
        await using var fixture = await HttpFixture.StartAsync();
        var owner = fixture.AliceId;
        var other = fixture.BobId;
        var input = new ChronicleEventInput("yolo", "original-match", "historical-group", 1234,
            "Uložený příběh", "Tajný výsledek: společné fiasko.",
            [new(owner, "Kazimír", "participant", VisualSnapshotJson: "{\"private\":\"frozen-portrait\"}"), new(other, "Berta", "participant")],
            Result: "fiasco", Story: ["Plán", "Komplikace", "Následek"], ShareText: "Obě postavy v sazích.", Visibility: "public");
        var id = fixture.Chronicle.Ingest(new(input, [owner, other], new Dictionary<string, IReadOnlyList<ChronicleAward>>
        { [owner] = [new("accomplices", "Spolupachatelé", "pair:historical", "Potvrzené společné fiasko.")] }))!;
        var path = "/app/v1/chronicle/" + id;
        using var alice = fixture.Client();
        Check((await alice.GetAsync(path)).StatusCode == HttpStatusCode.Unauthorized, "Chronicle cannot be read anonymously.");
        await fixture.LoginAsync(alice, owner);
        var preview = await alice.GetFromJsonAsync<JsonElement>(path);
        Check(!preview.GetProperty("revealed").GetBoolean() && preview.GetProperty("summary").GetString() == ""
            && preview.GetProperty("awards").GetArrayLength() == 0, "Server locks summaries and award evidence before reveal.");
        Check(!preview.GetRawText().Contains("frozen-portrait"), "Server never returns raw visual snapshots.");
        Check((await alice.PostAsJsonAsync(path + "/reveal", new { })).StatusCode == HttpStatusCode.BadRequest,
            "Reveal mutation requires normal account CSRF.");
        alice.DefaultRequestHeaders.Add("X-DV-CSRF", await fixture.CsrfAsync(alice));
        using var revealed = await alice.PostAsJsonAsync(path + "/reveal", new { });
        revealed.EnsureSuccessStatusCode();
        var result = await revealed.Content.ReadFromJsonAsync<JsonElement>();
        Check(result.GetProperty("item").GetProperty("result").GetString() == "fiasco", "Reveal preserves a fiasco without inventing a winner.");
        using var bob = fixture.Client();
        await fixture.LoginAsync(bob, other);
        Check(!(await bob.GetFromJsonAsync<JsonElement>(path)).GetProperty("revealed").GetBoolean(), "Each owner has a separate reveal state.");
        using var stranger = fixture.Client();
        await fixture.LoginAsync(stranger, fixture.ForeignId);
        Check((await stranger.GetAsync(path)).StatusCode == HttpStatusCode.NotFound, "Unrelated accounts cannot read an event.");
        Check((await alice.GetAsync(path + "/image")).StatusCode == HttpStatusCode.NotFound, "An unfinished image does not masquerade as ready.");
        Check((await alice.PostAsJsonAsync(path + "/illustration", new { })).StatusCode == HttpStatusCode.Accepted, "Revealed event can explicitly queue illustration.");
        var lease = fixture.Chronicle.LeaseNext()!;
        Check(fixture.Chronicle.MarkDispatched(lease), "Image dispatch is journaled before provider invocation.");
        fixture.Chronicle.Complete(lease, new(ChronicleIllustrator.RenderCard(input, null, true), "image/png", true));
        using var image = await alice.GetAsync(path + "/image");
        image.EnsureSuccessStatusCode();
        Check(image.Content.Headers.ContentType?.MediaType == "image/png" && image.Headers.CacheControl?.NoStore == true,
            "Authorized images are served without caching.");
        Check((await bob.GetAsync(path + "/image")).StatusCode == HttpStatusCode.NotFound, "Another owner cannot fetch the image before their reveal.");
        Check((await alice.GetAsync(path + "/badges/accomplices/image")).StatusCode == HttpStatusCode.OK, "An earned badge uses the event's illustration.");
        Check((await alice.GetAsync(path + "/badges/not-earned/image")).StatusCode == HttpStatusCode.NotFound, "A missing award cannot be exported.");
        fixture.Chronicle.Ingest(new(input with { SourceId = "queued-before-deletion", IllustrationRequested = true },
            [owner, other], new Dictionary<string, IReadOnlyList<ChronicleAward>>()));
        using (var scope = fixture.ChronicleServices.CreateScope())
        {
            var accounts = scope.ServiceProvider.GetRequiredService<AccountDatabase>();
            accounts.DeletionRequests.Add(new AccountDeletionRequest
            { UserId = other, ReceiptId = Guid.NewGuid().ToString("N"), RequestedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds() });
            await accounts.SaveChangesAsync();
        }
        Check(fixture.Chronicle.Read(owner, id) is not null, "Fixture retains the scene before background erasure.");
        foreach (var route in new[] { path, path + "/image", path + "/share", "/app/v1/chronicle/export" })
        {
            Check((await alice.GetAsync(route)).StatusCode == HttpStatusCode.NotFound,
                "Accepted participant deletion immediately fences every response before background cleanup: " + route);
        }
        var provider = new ChronicleCountingIllustrator();
        using (var worker = new ChronicleWorker(fixture.Chronicle, provider,
            fixture.ChronicleServices.GetRequiredService<IServiceScopeFactory>(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ChronicleWorker>.Instance))
        {
            Check(await worker.ProcessNextAsync() && provider.Calls == 0,
                "Accepted participant deletion blocks new provider dispatch before background cleanup.");
        }
        using (var scope = fixture.ChronicleServices.CreateScope())
        {
            var accounts = scope.ServiceProvider.GetRequiredService<AccountDatabase>();
            accounts.DeletionRequests.Remove(await accounts.DeletionRequests.SingleAsync(request => request.UserId == other));
            await accounts.SaveChangesAsync();
        }
        using (var scope = fixture.ChronicleServices.CreateScope())
        {
            var accounts = scope.ServiceProvider.GetRequiredService<AccountDatabase>();
            var user = await accounts.Users.SingleAsync(user => user.Id == owner);
            user.LaunchGroupRevokedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            await accounts.SaveChangesAsync();
        }
        Check((await alice.GetAsync(path)).StatusCode == HttpStatusCode.OK, "Group revocation does not remove permitted account history.");
        var otherProfile = await fixture.Profiles.ReadAsync(other);
        otherProfile ??= await fixture.Profiles.SaveAsync(other, new(0, new("Berta", "Testovací svět")));
        await fixture.Profiles.DeleteAsync(other, otherProfile.Revision, otherProfile.ProfileId);
        Check((await alice.GetAsync(path)).StatusCode == HttpStatusCode.NotFound,
            "Accepted character deletion fences other owners even before cross-store reconciliation.");
        fixture.Chronicle.DeleteAccount(owner);
        Check((await alice.GetAsync(path)).StatusCode == HttpStatusCode.NotFound
            && (await bob.GetAsync(path)).StatusCode == HttpStatusCode.NotFound, "Deleting a participant removes every shared scene copy.");
        Console.WriteLine("Chronicle HTTPS account/CSRF/owner/reveal/image/badge/erasure checks passed.");
    }

    private sealed class ChronicleCountingIllustrator : IChronicleIllustrator
    {
        internal int Calls { get; private set; }
        public Task<ChronicleIllustration> GenerateAsync(ChronicleEventInput snapshot, CancellationToken token)
        {
            Calls++;
            return Task.FromResult(new ChronicleIllustration(ChronicleIllustrator.RenderCard(snapshot, null, true), "image/png", true));
        }
    }
}
