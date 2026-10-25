using Kufr.Backend.Features.Customers;
using Microsoft.Data.Sqlite;

internal static class CharacterPortraitTests
{
    internal static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "dv-portrait-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            await ConsentAndDurabilityAsync(Path.Combine(root, "durable"));
            await RevisionAndDeletionAsync(Path.Combine(root, "fences"));
            await InterruptedAndRetriesAsync(Path.Combine(root, "retry"));
            await ConcurrentCreationAsync(Path.Combine(root, "concurrency"));
            Check(PortraitContentPolicy.ValidMessage("Hvězdy si pro vás právě zkoušejí slavnostní klobouky."), "Czech Unicode message accepted");
            foreach (var secret in new[] { "Your wand is ready", "Kufr už čeká.", "Use <script>hello</script>", "Admin conversation", "Hello\nworld" })
            {
                Check(!PortraitContentPolicy.ValidMessage(secret), "unsafe generated message rejected");
            }
            Check(!PortraitContentPolicy.ValidMessage(new string('x', 251)), "oversized message rejected");
            Console.WriteLine("Character portrait consent, durability, revision/deletion fences, concurrency and bounded retry tests passed.");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) { Directory.Delete(root, true); }
        }
    }

    private static async Task ConsentAndDurabilityAsync(string root)
    {
        var store = new CharacterProfileStore(root);
        var input = new CharacterUserInput("Raven", "Original fiction");
        var local = await store.SaveAsync("local", new(0, input));
        Check(store.ReadPortrait("local") is null, "local-only character never requests portrait");
        var noPortrait = await store.SaveAsync("enrichment-only", new(0, input, OpenAiConsent: true));
        var firstJob = (await store.ClaimAsync())!;
        await store.CompleteAsync(firstJob, Result(firstJob), null);
        using (var connection = new SqliteConnection("Data Source=" + Path.Combine(root, "customer-characters.sqlite")))
        {
            connection.Open();
            using var cleanup = connection.CreateCommand();
            cleanup.CommandText = "DELETE FROM character_jobs WHERE account_id='enrichment-only'";
            cleanup.ExecuteNonQuery();
        }
        Check(store.ClaimPortrait() is null, "existing enrichment consent alone never starts paid portrait");
        var profile = await store.SaveAsync("portrait", new(0, input, OpenAiConsent: true, PortraitConsent: true));
        Check(store.ReadPortrait("portrait")?.Status == "queued", "countdown can show immediately before enrichment");
        Check(store.ClaimPortrait() is null, "portrait waits for enrichment");
        var job = (await store.ClaimAsync())!;
        await store.CompleteAsync(job, Result(job), null);
        var restarted = new CharacterProfileStore(root);
        var portraitJob = restarted.ClaimPortrait()!;
        Check(portraitJob.AccountId == "portrait" && portraitJob.Enrichment.Summary == "A fictional traveler.", "durable enriched context reaches image job");
        Check(store.ClaimPortrait() is null, "parallel worker cannot claim same live lease");
        Check(store.SetPortraitMessage(portraitJob, "Hvězdy se právě domlouvají."), "loading message publishes before portrait");
        Check(store.CompletePortrait(portraitJob, [1, 2, 3], null), "current portrait publishes");
        Check(store.ReadPortraitImage("portrait", profile.Revision)!.SequenceEqual(new byte[] { 1, 2, 3 }), "private image survives restart");
        Check(store.ReadPortraitImage("local", profile.Revision) is null, "image isolation by authenticated owner");
        Check(!store.CompletePortrait(portraitJob, [4], null), "duplicate completion cannot replace artwork");
        await store.SaveAsync("portrait", new(profile.Revision, input, profile.ProfileId, OpenAiConsent: true, PortraitConsent: true));
        Check(store.ReadPortrait("portrait")?.Status == "ready" && store.ClaimPortrait() is null, "idempotent retry never regenerates completed image");
        await store.SaveAsync("enrichment-only", new(noPortrait.Revision, input, noPortrait.ProfileId, OpenAiConsent: true, PortraitConsent: true));
        var oldCharacterPortrait = store.ClaimPortrait()!;
        Check(oldCharacterPortrait.AccountId == "enrichment-only", "existing ready character can explicitly consent to portrait");
        await store.SaveAsync("enrichment-only", new(noPortrait.Revision, input, noPortrait.ProfileId));
        Check(!store.CanDispatchPortrait(oldCharacterPortrait) && store.ReadPortrait("enrichment-only") is null,
            "withdrawing consent cancels queued or in-flight portrait even when enrichment is already ready");
    }

    private static async Task RevisionAndDeletionAsync(string root)
    {
        var store = new CharacterProfileStore(root);
        var profile = await store.SaveAsync("alice", new(0, new("Raven", "Fiction"), OpenAiConsent: true, PortraitConsent: true));
        var enrichmentJob = (await store.ClaimAsync())!;
        await store.CompleteAsync(enrichmentJob, Result(enrichmentJob), null);
        var stale = store.ClaimPortrait()!;
        var edited = await store.SaveAsync("alice", new(profile.Revision, new("New Raven", "Fiction"), profile.ProfileId,
            OpenAiConsent: true, PortraitConsent: true));
        Check(!store.CanDispatchPortrait(stale) && !store.CompletePortrait(stale, [1], null), "revision fences stale image provider result");
        enrichmentJob = (await store.ClaimAsync())!;
        await store.CompleteAsync(enrichmentJob, Result(enrichmentJob), null);
        var current = store.ClaimPortrait()!;
        await store.DeleteAsync("alice", edited.Revision, edited.ProfileId);
        Check(!store.CanDispatchPortrait(current) && !store.CompletePortrait(current, [1], null), "deletion fences in-flight provider");
        Check(store.ReadPortrait("alice") is null && store.ReadPortraitImage("alice", edited.Revision) is null,
            "profile deletion atomically removes private portrait");
        Check(!new CharacterProfileStore(root).RetryPortrait("alice"), "restart cannot retry deleted asset");

        await store.SaveAsync("bob", new(0, new("B", "Fiction"), OpenAiConsent: true, PortraitConsent: true));
        enrichmentJob = (await store.ClaimAsync())!;
        await store.CompleteAsync(enrichmentJob, Result(enrichmentJob), null);
        current = store.ClaimPortrait()!;
        await store.FenceAccountDeletionAsync("bob");
        Check(!store.CanDispatchPortrait(current) && !store.CompletePortrait(current, [1], null), "account deletion prevents resurrection");
    }

    private static async Task InterruptedAndRetriesAsync(string root)
    {
        var store = new CharacterProfileStore(root);
        await store.SaveAsync("alice", new(0, new("R", "Fiction"), OpenAiConsent: true, PortraitConsent: true));
        var job = (await store.ClaimAsync())!;
        await store.CompleteAsync(job, Result(job), null);
        var portrait = store.ClaimPortrait()!;
        Check(store.ClaimPortrait(DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 241) is null,
            "expired lease becomes failed without automatic duplicate paid request");
        Check(store.ReadPortrait("alice") is { Status: "failed", ErrorCode: "portrait_interrupted" }, "interrupted job is recoverable and visible");
        Check(!store.CompletePortrait(portrait, [1], null), "expired lease result cannot publish");
        Check(store.RetryPortrait("alice"), "explicit retry recovers uncertain request");
        for (var attempt = 2; attempt <= 3; attempt++)
        {
            portrait = store.ClaimPortrait()!;
            Check(portrait.Attempt == attempt, "attempt counter persists across retries");
            store.CompletePortrait(portrait, null, "provider_unavailable");
            Check(store.RetryPortrait("alice") == (attempt < 3), "bounded retry avoids unlimited generation costs");
        }
        Check(store.ClaimPortrait() is null, "exhausted job never re-dispatches");
    }

    private static async Task ConcurrentCreationAsync(string root)
    {
        var first = new CharacterProfileStore(root);
        var second = new CharacterProfileStore(root);
        async Task<bool> CreateAsync(CharacterProfileStore store)
        {
            try
            {
                await Task.Run(() => store.SaveAsync("same-account", new(0, new("Raven", "Fiction"),
                    OpenAiConsent: true, PortraitConsent: true)));
                return true;
            }
            catch (CharacterException error) when (error.Code == "character_revision_conflict") { return false; }
        }
        var result = await Task.WhenAll(CreateAsync(first), CreateAsync(second));
        Check(result.Count(value => value) == 1 && first.PortraitJobSummaries().Length == 1,
            "concurrent clients create exactly one owned character and portrait request");
    }

    private static CharacterEnrichment Result(CharacterJob job) => new(job.UserInput.Name, job.UserInput.World,
        "A fictional traveler.", [], [], [], false, [], "fake", "portrait-tests", DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        job.Revision, "synthetic");

    private static void Check(bool condition, string message)
    {
        if (!condition) { throw new InvalidOperationException("Portrait test failed: " + message); }
    }
}
