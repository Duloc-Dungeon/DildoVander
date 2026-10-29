using System.Text.Json;
using System.Text.Json.Nodes;
using Kufr.Backend.Features.Game;
using Kufr.Backend.Features.Keepsakes;
using Kufr.Backend.Features.Wands;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

internal static class GameWandArtworkTests
{
    internal static async Task RunAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "game-wand-artwork-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await VerifyPublishedProjectionAsync(directory);
            VerifyMatchParticipantFence(directory);
            Console.WriteLine("Game wand artwork projection and match participant checks passed.");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task VerifyPublishedProjectionAsync(string directory)
    {
        var database = Path.Combine(directory, "keepsakes.db");
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Backend:DatabasePath"] = database,
            ["Keepsake:DataRoot"] = Path.Combine(directory, "content"),
            ["Keepsake:Enabled"] = "true",
            ["Keepsake:GenerationEnabled"] = "true"
        }).Build();
        var store = new KeepsakeStore(config, TimeProvider.System);
        const string accountId = "opponent-account";
        const string wandId = "current-opponent-wand";
        var issued = store.Issue(new(Guid.NewGuid().ToString("D"), "test-device", 1, "fixture-wand",
            "Fixture wand", "Generated fixture", "Offline test fixture."),
            new KeepsakeCustomerOwner(accountId, "account-session"));
        var owner = store.Bootstrap();
        store.Claim(store.IssueClaim(issued.Id, "test-device").Token, owner.Token, "claim-owner");
        var job = store.LeaseNext() ?? throw new InvalidOperationException("Expected a fixture generation job.");
        var request = store.Dispatch(job) ?? throw new InvalidOperationException("Expected a fixture generation request.");
        store.Complete(job, await new FakeKeepsakeProvider().GenerateAsync(request, default));
        var edition = store.Read(issued.Id, owner.Token).Editions.Single();
        var assetId = edition.ArtworkAssetId;

        Check(store.ReadGameSharedWandArtwork(accountId, wandId) is null,
            "generated wand art is hidden until publication approval is explicitly bound");
        SetGeneratedWandMetadata(database, issued.Id, wandId, edition.Id, assetId, approved: true);

        var published = store.ReadGameSharedWandArtwork(accountId, wandId);
        var media = store.ReadGameSharedWandArtwork(accountId, wandId, published?.Sha256, includeBytes: true);
        Check(published is { Name: "Published fixture wand", Synthetic: true, Bytes: null }
            && media?.Bytes is { } bytes
            && bytes.SequenceEqual(store.ReadAsset(issued.Id, owner.Token, assetId).Bytes),
            "match metadata avoids loading media and the authorized media read returns only the approved canonical PNG");
        Check(store.ReadGameSharedWandArtwork("other-account", wandId) is null
            && store.ReadGameSharedWandArtwork(accountId, "another-wand") is null,
            "account and wand mismatches cannot resolve the owner's artwork");
        Check(store.ReadGameSharedWandArtwork(accountId, wandId, "stale-revision") is null
            && store.ReadGameSharedWandArtwork(accountId, wandId, published!.Sha256, includeBytes: true) is not null,
            "media reads require the current canonical SHA revision");

        SetGeneratedWandMetadata(database, issued.Id, wandId, edition.Id, assetId, approved: false);
        Check(store.ReadGameSharedWandArtwork(accountId, wandId) is null,
            "withdrawing the publication binding immediately hides the artwork");
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database }.ToString()))
        {
            connection.Open();
            using var delete = connection.CreateCommand();
            delete.CommandText = "UPDATE dv10_keepsakes SET deleted=1 WHERE id=$id";
            delete.Parameters.AddWithValue("$id", issued.Id);
            delete.ExecuteNonQuery();
        }
        Check(store.ReadGameSharedWandArtwork(accountId, wandId) is null,
            "deleted keepsakes no longer yield match artwork");
    }

    private static void SetGeneratedWandMetadata(string database, string keepsakeId, string wandId,
        string editionId, string assetId, bool approved)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database }.ToString());
        connection.Open();
        using var read = connection.CreateCommand();
        read.CommandText = "SELECT document FROM dv10_keepsakes WHERE id=$id";
        read.Parameters.AddWithValue("$id", keepsakeId);
        var document = JsonNode.Parse((string)read.ExecuteScalar()!)!.AsObject();
        document["snapshot"]!["generatedWand"] = JsonSerializer.SerializeToNode(new GeneratedWandPublicSnapshot(
            wandId, 1, "Published fixture wand", "oak", "phoenix feather", "steady", "Test lore",
            "A test wand", "fire", "water", []), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        document["artwork"] = JsonSerializer.SerializeToNode(new WandArtworkSnapshot(
            wandId, assetId, 1, true, "fixture", "fixture-model", "fixture-prompt-v1", DateTimeOffset.UtcNow),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        document["wandPublicationEditionId"] = approved ? editionId : null;
        using var update = connection.CreateCommand();
        update.CommandText = "UPDATE dv10_keepsakes SET document=$document WHERE id=$id";
        update.Parameters.AddWithValue("$document", document.ToJsonString(new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        update.Parameters.AddWithValue("$id", keepsakeId);
        update.ExecuteNonQuery();
    }

    private static void VerifyMatchParticipantFence(string directory)
    {
        var store = new GameStore(Path.Combine(directory, "game"));
        var challenge = store.Challenge("group-a", "alice", "Alice", "bob", "Bob",
            new("bob", "quick", "wand-artwork-match-01"), 1);
        Check(store.Read(challenge.Id, "group-a", "alice").Opponent.Id == "bob"
            && store.Read(challenge.Id, "group-a", "bob").Opponent.Id == "alice",
            "both participants receive only the other player's match projection");
        ExpectUnavailable(() => store.Read(challenge.Id, "group-b", "alice"));
        ExpectUnavailable(() => store.Read(challenge.Id, "group-a", "mallory"));
    }

    private static void ExpectUnavailable(Action action)
    {
        try { action(); }
        catch (GameFault fault) when (fault.Status == 404 && fault.Code == "game_match_unavailable") { return; }
        throw new InvalidOperationException("Expected match artwork access to require the exact group participant.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Game wand artwork test failed: " + message);
    }
}
