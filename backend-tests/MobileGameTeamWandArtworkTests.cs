using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Kufr.Backend.Features.Game;
using Kufr.Backend.Features.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

internal static partial class MobileGameTests
{
    internal static async Task RunTeamWandArtworkHttpAsync()
    {
        await using var fixture = await HttpFixture.StartAsync(withKeepsakes: true);
        var (partnerId, outsiderId) = await fixture.SeedTeamArtworkPlayersAsync();
        using var alice = fixture.Client();
        using var bob = fixture.Client();
        using var partner = fixture.Client();
        using var secondOpponent = fixture.Client();
        using var foreign = fixture.Client();
        using var outsider = fixture.Client();
        using var anonymous = fixture.Client();
        await fixture.LoginAsync(alice, fixture.AliceId);
        await fixture.LoginAsync(bob, fixture.BobId);
        await fixture.LoginAsync(partner, partnerId);
        await fixture.LoginAsync(secondOpponent, fixture.PausedId);
        await fixture.LoginAsync(foreign, fixture.ForeignId);
        await fixture.LoginAsync(outsider, outsiderId);
        alice.DefaultRequestHeaders.Add("X-DV-CSRF", await fixture.CsrfAsync(alice));
        var bobArtwork = await fixture.PublishCurrentWandArtworkAsync(fixture.BobId);
        var otherArtwork = await fixture.PublishCurrentWandArtworkAsync(fixture.PausedId);
        await fixture.PublishCurrentWandArtworkAsync(partnerId);
        using var created = await alice.PostAsJsonAsync("/app/v1/game/teams",
            new GameTeamCreate(partnerId, [fixture.BobId, fixture.PausedId], "team-artwork-create"));
        Check(created.StatusCode == HttpStatusCode.OK, "team artwork fixture creates a tactical match through the API");
        var match = await created.Content.ReadFromJsonAsync<JsonElement>();
        var matchId = match.GetProperty("id").GetString()!;
        var matchPath = $"/app/v1/game/teams/{matchId}";
        var metadata = match.GetProperty("opponentWands");
        var sharedUrl = metadata.GetProperty(fixture.BobId).GetProperty("wandArtworkUrl").GetString()!;
        var otherUrl = metadata.GetProperty(fixture.PausedId).GetProperty("wandArtworkUrl").GetString()!;
        Check(metadata.EnumerateObject().Count() == 2
            && metadata.GetProperty(fixture.BobId).GetProperty("wandName").GetString() is { Length: > 0 }
            && metadata.GetProperty(fixture.BobId).GetProperty("wandSynthetic").GetBoolean()
            && sharedUrl == $"{matchPath}/opponents/{fixture.BobId}/wand-artwork?revision={bobArtwork.Sha256}",
            "team creation returns only the two opposing canonical user IDs and revision-bound artwork metadata");
        var read = await alice.GetFromJsonAsync<JsonElement>(matchPath);
        Check(read.GetProperty("opponentWands").GetProperty(fixture.BobId).GetProperty("wandArtworkUrl").GetString() == sharedUrl,
            "specific team reads hydrate current opponent artwork");
        using var media = await alice.GetAsync(sharedUrl);
        Check(media.StatusCode == HttpStatusCode.OK
            && media.Content.Headers.ContentType?.MediaType == "image/png"
            && media.Headers.CacheControl?.NoStore == true
            && media.Headers.TryGetValues("X-Content-Type-Options", out var contentOptions)
            && contentOptions.Contains("nosniff")
            && (await media.Content.ReadAsByteArrayAsync()).SequenceEqual(bobArtwork.Bytes),
            "authorized team participant receives only the approved canonical PNG with private response headers");
        Check((await partner.GetAsync(sharedUrl)).StatusCode == HttpStatusCode.OK
            && (await alice.GetAsync(otherUrl)).StatusCode == HttpStatusCode.OK,
            "both teammates can access artwork for either opponent");
        Check((await anonymous.GetAsync(sharedUrl)).StatusCode == HttpStatusCode.Unauthorized
            && (await foreign.GetAsync(sharedUrl)).StatusCode == HttpStatusCode.NotFound
            && (await outsider.GetAsync(sharedUrl)).StatusCode == HttpStatusCode.NotFound,
            "team media requires authentication and the exact same-group match participant");
        var mediaPath = $"{matchPath}/opponents/{fixture.BobId}/wand-artwork";
        Check((await bob.GetAsync(sharedUrl)).StatusCode == HttpStatusCode.NotFound
            && (await alice.GetAsync($"{matchPath}/opponents/{partnerId}/wand-artwork?revision={bobArtwork.Sha256}")).StatusCode == HttpStatusCode.NotFound
            && (await alice.GetAsync($"{matchPath}/opponents/{outsiderId}/wand-artwork?revision={bobArtwork.Sha256}")).StatusCode == HttpStatusCode.NotFound,
            "an existing publication never grants access to oneself, a teammate or an arbitrary account");
        Check((await alice.GetAsync(mediaPath)).StatusCode == HttpStatusCode.NotFound
            && (await alice.GetAsync(mediaPath + "?revision=stale")).StatusCode == HttpStatusCode.NotFound
            && (await alice.GetAsync(mediaPath + "?revision=" + new string('0', 64))).StatusCode == HttpStatusCode.NotFound,
            "team media rejects missing, malformed and obsolete revisions");
        using var cancelled = await alice.PostAsJsonAsync(matchPath + "/cancel",
            new GameTeamCommand("team-artwork-cancel", match.GetProperty("revision").GetInt64()));
        var cancelledMatch = await cancelled.Content.ReadFromJsonAsync<JsonElement>();
        Check(cancelled.StatusCode == HttpStatusCode.OK
            && cancelledMatch.GetProperty("opponentWands").GetProperty(fixture.BobId).GetProperty("wandArtworkUrl").GetString() == sharedUrl,
            "team mutation responses retain current opponent artwork metadata");
        fixture.Keepsakes.SetWandPublication(bobArtwork.KeepsakeId,
            await fixture.KeepsakeOwnerActorAsync(fixture.BobId), false, bobArtwork.EditionId, bobArtwork.Revision);
        var withdrawn = await alice.GetFromJsonAsync<JsonElement>(matchPath);
        Check(!withdrawn.GetProperty("opponentWands").TryGetProperty(fixture.BobId, out _)
            && (await alice.GetAsync(sharedUrl)).StatusCode == HttpStatusCode.NotFound,
            "withdrawn publication removes team metadata and immediately revokes the prior URL");
        var oldProfile = (await fixture.Profiles.ReadAsync(fixture.PausedId))!;
        await fixture.Profiles.DeleteAsync(fixture.PausedId, oldProfile.Revision, oldProfile.ProfileId);
        var stale = await alice.GetFromJsonAsync<JsonElement>(matchPath);
        Check(!stale.GetProperty("opponentWands").TryGetProperty(fixture.PausedId, out _)
            && (await alice.GetAsync(otherUrl)).StatusCode == HttpStatusCode.NotFound,
            "an artwork URL cannot outlive its current character and generated wand binding");
        await fixture.RevokeAsync(fixture.BobId);
        using var ineligible = await alice.GetAsync(otherUrl);
        Check(ineligible.StatusCode == HttpStatusCode.Forbidden
            && ineligible.Content.Headers.ContentType?.MediaType != "image/png",
            "loss of a team participant's eligibility prevents every opponent media read");
        Console.WriteLine("Team opponent artwork HTTP privacy and revision checks passed.");
    }

    private sealed partial class HttpFixture
    {
        internal async Task<(string PartnerId, string OutsiderId)> SeedTeamArtworkPlayersAsync()
        {
            await using var scope = app.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AccountDatabase>();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<AccountUser>>();
            var moved = await db.Users.SingleAsync(user => user.Id == PausedId);
            moved.LaunchGroupId = ActiveGroupId;
            moved.LaunchGroupAccessRevision++;
            var partnerId = Guid.NewGuid().ToString("N");
            var outsiderId = Guid.NewGuid().ToString("N");
            foreach (var id in new[] { partnerId, outsiderId })
            {
                var user = new AccountUser
                {
                    Id = id, UserName = id + "@example.invalid", Email = id + "@example.invalid",
                    EmailConfirmed = true, DisplayName = "Team fixture", OpenAiConsentAt = 1,
                    OpenAiConsentVersion = AccountAiConsent.CurrentVersion, LaunchGroupId = ActiveGroupId,
                    LaunchGroupJoinedAt = 1, LaunchGroupAccessRevision = 1, CreatedAt = 1
                };
                var created = await users.CreateAsync(user);
                if (!created.Succeeded)
                {
                    throw new InvalidOperationException("Cannot seed team artwork account.");
                }
                await users.AddToRoleAsync(user, AccountRoles.Customer);
            }
            await db.SaveChangesAsync();
            var participants = new[] { AliceId, partnerId, BobId, PausedId };
            foreach (var id in participants)
            {
                await MobileWandTests.Seed(Profiles, Wands, id, tier: 2);
            }
            // Initialize the real game schema, then unlock the tactical team fixture.
            app.Services.GetRequiredService<GameStore>().ReadProgress(ActiveGroupId, AliceId, new Dictionary<string, string>());
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = Path.Combine(app.Services.GetRequiredService<AccountConfiguration>().DataRoot, "mobile-game.sqlite") }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO game_players(group_id,user_id,xp,matches) VALUES($group,$user,50,3) ON CONFLICT(group_id,user_id) DO UPDATE SET xp=50,matches=3";
            command.Parameters.AddWithValue("$group", ActiveGroupId);
            var userParameter = command.Parameters.Add("$user", SqliteType.Text);
            foreach (var id in participants)
            {
                userParameter.Value = id;
                command.ExecuteNonQuery();
            }
            return (partnerId, outsiderId);
        }
    }
}
