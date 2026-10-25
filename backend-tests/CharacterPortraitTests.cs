using Kufr.Backend.Features.Customers;
using Microsoft.Data.Sqlite;
using System.Text.Json;

internal static class CharacterPortraitTests
{
    internal static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "dv-portrait-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            await ConsentAndDurabilityAsync(Path.Combine(root, "durable"));
            await ProfileIdentityFenceAsync(Path.Combine(root, "identity"));
            await RevisionAndDeletionAsync(Path.Combine(root, "fences"));
            await InterruptedAndRetriesAsync(Path.Combine(root, "retry"));
            await ConcurrentCreationAsync(Path.Combine(root, "concurrency"));
            Check(PortraitContentPolicy.ValidMessage("Hvězdy si pro vás právě zkoušejí slavnostní klobouky."), "Czech Unicode message accepted");
            Check(PortraitContentPolicy.ValidLoadingMessage("Raven ví, že kouzla se raději objeví včas. Olivander má kocovinu."),
                "character-specific suspense with the host hangover remains valid loading copy");
            foreach (var reveal in new[] { "Raven awaits a portrait.", "Teď vzniká malba.", "Brzy uvidíš obraz.",
                         "Tvé avatarové dobrodružství začíná.", "AI přináší překvapení.", "Probíhá výzkum.",
                         "Každý krok má svůj řád.", "Poznáváme postavu.", "Mícháme barvy.", "Malujeme tvůj portrét." })
            {
                Check(!PortraitContentPolicy.ValidLoadingMessage(reveal), "pre-reveal loading copy hides artifacts and process details");
            }
            Check(PortraitContentPolicy.ValidMessage("Raven, tvůj portrét právě dorazil."),
                "reveal copy may mention the completed portrait");
            Check(PortraitContentPolicy.ValidLoadingMessage("Stephen zaslechl za zavřenými dveřmi své jméno."),
                "a fictional character name is not mistaken for a progress step");
            Check(PortraitContentPolicy.ValidMessage("Wanda dorazila pozdě i do vlastního proroctví."), "fictional Wanda is not mistaken for a wand reveal");
            Check(!PortraitContentPolicy.ValidMessage("Your wand is ready")
                && !PortraitContentPolicy.ValidMessage("Two wands are ready"), "wand reveal remains blocked at whole-word boundaries");
            foreach (var secret in new[] { "Your wand is ready", "Kufr už čeká.", "Use <script>hello</script>", "Admin conversation", "Hello\nworld" })
            {
                Check(!PortraitContentPolicy.ValidMessage(secret), "unsafe generated message rejected");
            }
            Check(!PortraitContentPolicy.ValidMessage(new string('x', 251)), "oversized message rejected");
            var finalFallback = PortraitContentPolicy.FinalMessageFallback(new("Raven", "wizarding world"));
            Check(finalFallback.Length <= 250 && finalFallback.Contains("Raven", StringComparison.Ordinal)
                && finalFallback.Contains("wizarding world", StringComparison.Ordinal)
                && finalFallback.Contains("kocovin", StringComparison.OrdinalIgnoreCase),
                "bounded Czech fallback includes character, fictional world and prelaunch Olivander hangover joke");
            var enrichment = new CharacterEnrichment("Raven", "wizarding world",
                "Raven is a fictional wizard who speaks with birds. Source: https://example.invalid/private. Account detail.",
                [], [], ["private ambiguity"], false, [new("Private source", "https://example.invalid/private")],
                "fixture", "portrait-test", 1, 1, "web_sources");
            var characterContext = JsonSerializer.Serialize(PortraitContentPolicy.CharacterData(new("Raven", "wizarding world"), enrichment));
            Check(characterContext.Contains("Raven is a fictional wizard who speaks with birds", StringComparison.Ordinal)
                && !characterContext.Contains("example.invalid", StringComparison.Ordinal)
                && !characterContext.Contains("private ambiguity", StringComparison.OrdinalIgnoreCase),
                "portrait context passes one safe fictional summary trait without source URLs or ambiguity details");
            Check(PortraitContentPolicy.FinalMessagePrompt.Contains("kocoviny", StringComparison.OrdinalIgnoreCase)
                && PortraitContentPolicy.SafetyInstructions.Contains("one harmless public fictional trait", StringComparison.Ordinal),
                "portrait prompt allows one harmless public trait while preserving source and privacy restrictions");
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
        Check(store.ReadPortrait("local", local.ProfileId, local.Revision) is null, "local-only character never requests portrait");
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
        Check(store.ReadPortrait("portrait", profile.ProfileId, profile.Revision)?.Status == "queued", "countdown can show immediately before enrichment");
        Check(store.ClaimPortrait() is null, "portrait waits for enrichment");
        var job = (await store.ClaimAsync())!;
        await store.CompleteAsync(job, Result(job), null);
        var restarted = new CharacterProfileStore(root);
        var portraitJob = restarted.ClaimPortrait()!;
        Check(portraitJob.AccountId == "portrait" && portraitJob.Enrichment.Summary == "A fictional traveler.", "durable enriched context reaches image job");
        Check(store.ClaimPortrait() is null, "parallel worker cannot claim same live lease");
        Check(store.SetPortraitMessage(portraitJob, "Hvězdy se právě domlouvají."), "loading message publishes before portrait");
        Check(store.CompletePortrait(portraitJob, [1, 2, 3], null, "Postava jménem Raven má velké plány; drobné povinnosti si zjevně nechává na překvapení."), "current portrait publishes");
        Check(store.ReadPortraitImage("portrait", profile.ProfileId, profile.Revision)!.SequenceEqual(new byte[] { 1, 2, 3 }), "private image survives restart");
        Check(store.ReadPortraitImage("local", profile.ProfileId, profile.Revision) is null, "image isolation by authenticated owner");
        Check(store.ReadPortrait("portrait", profile.ProfileId, profile.Revision)?.FinalMessage is not null, "final message survives restart with image");
        Check(!store.CompletePortrait(portraitJob, [4], null), "duplicate completion cannot replace artwork");
        await store.SaveAsync("portrait", new(profile.Revision, input, profile.ProfileId, OpenAiConsent: true, PortraitConsent: true));
        Check(store.ReadPortrait("portrait", profile.ProfileId, profile.Revision)?.Status == "ready" && store.ClaimPortrait() is null, "idempotent retry never regenerates completed image");
        await store.SaveAsync("enrichment-only", new(noPortrait.Revision, input, noPortrait.ProfileId, OpenAiConsent: true, PortraitConsent: true));
        var oldCharacterPortrait = store.ClaimPortrait()!;
        Check(oldCharacterPortrait.AccountId == "enrichment-only", "existing ready character can explicitly consent to portrait");
        await store.SaveAsync("enrichment-only", new(noPortrait.Revision, input, noPortrait.ProfileId));
        Check(!store.CanDispatchPortrait(oldCharacterPortrait)
            && store.ReadPortrait("enrichment-only", noPortrait.ProfileId, noPortrait.Revision) is null,
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
        Check(store.ReadPortrait("alice", edited.ProfileId, edited.Revision) is null && store.ReadPortraitImage("alice", edited.ProfileId, edited.Revision) is null,
            "profile deletion atomically removes private portrait");
        Check(!new CharacterProfileStore(root).RetryPortrait("alice"), "restart cannot retry deleted asset");

        await store.SaveAsync("bob", new(0, new("B", "Fiction"), OpenAiConsent: true, PortraitConsent: true));
        enrichmentJob = (await store.ClaimAsync())!;
        await store.CompleteAsync(enrichmentJob, Result(enrichmentJob), null);
        current = store.ClaimPortrait()!;
        await store.FenceAccountDeletionAsync("bob");
        Check(!store.CanDispatchPortrait(current) && !store.CompletePortrait(current, [1], null), "account deletion prevents resurrection");
    }

    private static async Task ProfileIdentityFenceAsync(string root)
    {
        var store = new CharacterProfileStore(root);
        var first = await store.SaveAsync("alice", new(0, new("First", "Fiction"), OpenAiConsent: true, PortraitConsent: true));
        var enrichment = (await store.ClaimAsync())!;
        await store.CompleteAsync(enrichment, Result(enrichment), null);
        var portrait = store.ClaimPortrait()!;
        Check(store.CompletePortrait(portrait, [9, 8, 7], null, "První vzkaz."), "first profile artwork completes");
        var second = await store.SaveAsync("alice", new(first.Revision, new("Second", "Fiction"), first.ProfileId,
            NewCharacter: true, OpenAiConsent: true, PortraitConsent: true));
        Check(second.ProfileId != first.ProfileId && second.Revision == first.Revision,
            "replacement character can reuse numeric revision with a new identity");
        Check(store.ReadPortraitImage("alice", first.ProfileId, first.Revision) is null
            && store.ReadPortrait("alice", second.ProfileId, second.Revision)?.Status == "queued",
            "profile ID prevents old artwork from matching replacement at same revision");
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
        var profile = await store.ReadAsync("alice", CancellationToken.None);
        Check(store.ReadPortrait("alice", profile!.ProfileId, profile.Revision) is { Status: "failed", ErrorCode: "portrait_interrupted" }, "interrupted job is recoverable and visible");
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
