using Kufr.Backend.Features.Customers;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using System.Net;
using System.Text;
using System.Text.Json;

internal static class CharacterPortraitTests
{
    internal static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "dv-portrait-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            await ExperienceWireContractAsync();
            WelcomeContract();
            await ConsentAndDurabilityAsync(Path.Combine(root, "durable"));
            await ProfileIdentityFenceAsync(Path.Combine(root, "identity"));
            await RevisionAndDeletionAsync(Path.Combine(root, "fences"));
            await InterruptedAndRetriesAsync(Path.Combine(root, "retry"));
            await IndependentWelcomeAsync(Path.Combine(root, "welcome"));
            await MissingPortraitAsync(Path.Combine(root, "missing"));
            await FailedResearchAsync(Path.Combine(root, "research"));
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
            Check(finalFallback == "Tvá pověst dorazila před tebou. Už jsme se jí omluvili.",
                "text failure uses the stable, non-identifying Czech fallback");
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

    private static async Task ExperienceWireContractAsync()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["OPENAI_API_KEY"] = "offline-injected-transport-only" }).Build();
        var handler = new PortraitExperienceHandler();
        using var provider = new OpenAiCharacterPortraitProvider(configuration, new HttpClient(handler));
        var input = new CharacterUserInput("Raven", "Original fiction");
        var job = new CharacterPortraitJob("offline", "profile", 1, "lease", 1, input,
            Result(new("wire", "offline", "profile", 1, input, null, "lease", 1)));
        var result = await provider.GenerateExperienceAsync(job,
            "Keep the surprise secret.", true, CancellationToken.None);
        Check(handler.Calls == 1 && result.LoadingMessage == PortraitExperienceHandler.Loading
            && result.Welcome?.Joke == PortraitExperienceHandler.Joke
            && result.FinalMessage == PortraitExperienceHandler.Joke,
            "one Responses call parses structured welcome and retains the legacy finalMessage projection");
    }

    private static void WelcomeContract()
    {
        var cases = new[]
        {
            ("Hermiona Grangerová", "Harry Potter", "contrary_will", "Učebnice máš nastudované. Teď už jen čekáš, až ti někdo zadá život za domácí úkol.", "Bystrá nebelvírská čarodějka z mudlovské rodiny, která pomáhá přátelům znalostmi i odvahou.", "Některé věci tu mají vlastní vůli. Povinnou četbu nemají."),
            ("Darth Vader", "Star Wars", "rivalry", "Tolik temné moci. A dovolenou ti stejně musí podepsat císař.", "Anakin Skywalker, někdejší rytíř Jedi, který přešel na temnou stranu a sloužil císaři.", "Možná tě čeká nesouhlas, který se nevyřídí rozkazem."),
            ("Mrakoplaš", "Zeměplocha", "independent_choice", "Tvůj talent pro útěk je obdivuhodný. Zvlášť když tě dobrodružství pokaždé najde dřív.", "Mimořádně neschopný mág známý rychlým útěkem, který se opakovaně ocitá na nebezpečných výpravách.", "Něco si tě možná vybere samo. Útěk tentokrát nebude v nabídce.")
        };
        foreach (var item in cases)
        {
            var job = WelcomeJob(item.Item1, item.Item2, "fixture-profile", 7);
            var welcome = new CharacterWelcome(item.Item4, item.Item5, item.Item6,
                WelcomeWriterInput.Facts(job.Input, job.Enrichment, job.ProfileId, job.Revision).Select(fact => fact.Id).ToArray(), item.Item3);
            Check(CharacterWelcomePolicy.Valid(welcome, job), item.Item1 + " editorial welcome satisfies the strict contract");
            Check(!CharacterWelcomePolicy.Valid(welcome with { Joke = "Opona má trému. Slyšela, koho má představit." }, job),
                "welcome cannot repeat the waiting joke already displayed in this journey");
            Check(welcome.Joke.Length <= 120 && welcome.Identity.Length <= 120 && welcome.Teaser.Length <= 100
                && welcome.Joke.Length + welcome.Identity.Length + welcome.Teaser.Length + 2 <= 250,
                item.Item1 + " example stays within individual and combined limits");
            var invalidTeaserId = item.Item3 == "rivalry" ? "contrary_will" : "rivalry";
            Check(!CharacterWelcomePolicy.Valid(welcome with { TeaserId = invalidTeaserId }, job),
                item.Item1 + " cannot change the application-approved teaser category");
        }

        var other = WelcomeJob("Sam Elánius", "Zeměplocha", "other-profile", 1);
        var selectedTheme = WelcomeWriterInput.AllowedTeaserId(other.Input, other.Enrichment);
        Check(new[] { "contrary_will", "rivalry", "independent_choice" }.Contains(selectedTheme),
            "non-fixture character receives exactly one permitted stable theme");
        Check(selectedTheme == WelcomeWriterInput.AllowedTeaserId(other.Input, other.Enrichment),
            "non-fixture theme selection is stable");

        var original = WelcomeJob("Hermiona Grangerová", "Harry Potter", "profile-a", 3);
        var alias = original with { Input = new CharacterUserInput("Hermiona", "HP") };
        Check(WelcomeWriterInput.IdentityReliable(alias.Input, alias.Enrichment),
            "source-backed canonical recognition can differ from the unchanged user name and world alias");
        Check(alias.Input.Name == "Hermiona", "recognition never rewrites the user name");
        var clarification = original.Enrichment with { NeedsClarification = true };
        Check(!WelcomeWriterInput.IdentityReliable(original.Input, clarification),
            "unresolved clarification never becomes reliable identity");
        var noEvidence = original.Enrichment with { Facts = [], Sources = [] };
        Check(!WelcomeWriterInput.IdentityReliable(original.Input, noEvidence),
            "model interpretation without evidence never becomes reliable identity");
        var generic = original with { Enrichment = noEvidence };
        Check(CharacterWelcomePolicy.Valid(new("Tvá pověst čeká u dveří. Doufá, že si jí nevšimneš.", "",
            "Některé věci tu mají vlastní vůli.", [], WelcomeWriterInput.AllowedTeaserId(generic.Input, noEvidence)), generic),
            "a generic approved teaser requires no invented factual evidence");
        var reordered = original with { Enrichment = original.Enrichment with { Facts = original.Enrichment.Facts.Reverse().ToArray() } };
        var originalIds = WelcomeWriterInput.Facts(original.Input, original.Enrichment, original.ProfileId, original.Revision)
            .ToDictionary(fact => fact.Text, fact => fact.Id, StringComparer.Ordinal);
        var reorderedIds = WelcomeWriterInput.Facts(reordered.Input, reordered.Enrichment, reordered.ProfileId, reordered.Revision)
            .ToDictionary(fact => fact.Text, fact => fact.Id, StringComparer.Ordinal);
        Check(originalIds.Count == reorderedIds.Count && originalIds.All(pair => reorderedIds[pair.Key] == pair.Value),
            "fact IDs are stable when research result ordering changes");
        Check(originalIds.Values.All(id => !WelcomeWriterInput.Facts(original.Input, original.Enrichment, original.ProfileId, 4)
                .Any(fact => fact.Id == id))
            && originalIds.Values.All(id => !WelcomeWriterInput.Facts(original.Input, original.Enrichment, "profile-b", 3)
                .Any(fact => fact.Id == id)),
            "fact IDs bind to profile and revision");

        var untrusted = new CharacterWelcome("Darth Vader se připravuje na souboj.", "", "", [], null);
        Check(!CharacterWelcomePolicy.Valid(untrusted, WelcomeJob("Darth Vader", "Star Wars", "p", 1)),
            "welcome text cannot expose a prohibited topic");
        Check(CharacterWelcomePolicy.Fallback.Identity.Length == 0 && CharacterWelcomePolicy.Fallback.Teaser.Length == 0,
            "stable fallback omits identity and teaser when evidence or text generation is unavailable");
    }

    private static CharacterPortraitJob WelcomeJob(string name, string world, string profileId, long revision)
    {
        var input = new CharacterUserInput(name, world);
        var factTexts = name switch
        {
            "Hermiona Grangerová" => new[]
            {
                "Je čarodějka z mudlovské rodiny, dcera dvou zubařů, a patří do Nebelvíru.",
                "Je známá svou inteligencí a pilností; stihla přečíst učebnice ještě před nástupem do Bradavic a dosahovala výborných výsledků.",
                "Je blízkou přítelkyní Harryho a Rona a její odvaha, věrnost i schopnost řešit problémy jsou pro jejich společné úsilí důležité."
            },
            "Darth Vader" => new[]
            {
                "Darth Vader je Anakin Skywalker, někdejší rytíř Jedi a hrdina Klonových válek, který byl zlákán temnou stranou Síly.",
                "Sloužil císaři Darthu Sidiousovi a prosazoval vůli Impéria, mimo jiné pronásledováním přeživších Jediů a bojem proti povstalcům."
            },
            "Mrakoplaš" => new[]
            {
                "Oficiální průvodce postavami Terryho Pratchetta ho charakterizuje jako mimořádně neschopného mága, jehož nejvýraznější schopností je rychlý útěk; zároveň ho povolávají na nebezpečné výpravy, když je třeba mág.",
                "V Barvě kouzel se živí také využíváním mimořádného nadání pro cizí jazyky a vyhýbání se práci je popsáno jako jedno z jeho životních pravidel."
            },
            _ => Array.Empty<string>()
        };
        var urls = factTexts.Select((_, index) => $"https://example.test/{index}").ToArray();
        var facts = factTexts.Select((text, index) => new CharacterFact(text, [urls[index]])).ToArray();
        var sources = urls.Select((url, index) => new CharacterSource("Evidence " + index, url)).ToArray();
        var enrichment = new CharacterEnrichment(name, world, "Doložená fikční postava.", facts, [], [], false,
            sources, "fixture", "welcome-test", 1, revision, "web_sources");
        return new("account", profileId, revision, "lease", 1, input, enrichment);
    }

    private sealed class PortraitExperienceHandler : HttpMessageHandler
    {
        internal const string Loading = "Raven zaslechl za zavřenými dveřmi své jméno. Olivander si vyžádal vývar.";
        internal const string Joke = "Tvá pověst dorazila před tebou. Už jsme se jí omluvili.";
        internal int Calls { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++;
            Check(request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath == "/v1/responses",
                "experience uses one Responses request through the injected offline transport");
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            var payload = body.RootElement;
            Check(payload.GetProperty("store").GetBoolean() == false
                && payload.GetProperty("text").GetProperty("format").GetProperty("type").GetString() == "json_object"
                && payload.GetProperty("input").GetString()!.Contains("json", StringComparison.OrdinalIgnoreCase),
                "JSON-mode instruction must occur inside input, not only the separate instructions field");
            var text = JsonSerializer.Serialize(new { loadingMessage = Loading, joke = Joke, identity = "", teaser = "", usedFactIds = Array.Empty<string>(), teaserId = (string?)null });
            var response = JsonSerializer.Serialize(new
            {
                status = "completed",
                output = new[] { new { type = "message", content = new[] { new { type = "output_text", text } } } }
            });
            return new(HttpStatusCode.OK) { Content = new StringContent(response, Encoding.UTF8, "application/json") };
        }
    }

    private static async Task ConsentAndDurabilityAsync(string root)
    {
        var store = new CharacterProfileStore(root);
        var input = new CharacterUserInput("Raven", "Original fiction");
        try
        {
            await store.SaveAsync("local", new(0, input));
            throw new InvalidOperationException("Missing AI consent was accepted.");
        }
        catch (CharacterException error) when (error.Code == "openai_consent_required") { }
        Check(await store.ReadAsync("local") is null && store.ClaimPortrait() is null,
            "missing consent creates neither a character nor a portrait");
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
        store.SuspendAccountWork("enrichment-only");
        Check(!store.CanDispatchPortrait(oldCharacterPortrait)
            && store.ReadPortrait("enrichment-only", noPortrait.ProfileId, noPortrait.Revision)?.Status == "failed",
            "membership revocation cancels in-flight portrait and preserves completed character data");
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
            OpenAiConsent: true, PortraitConsent: true));
        Check(second.ProfileId == first.ProfileId && second.Revision == first.Revision + 1,
            "administrator edits keep one character identity with a new revision");
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
        Check(!store.RetryPortrait("alice", profile.ProfileId, profile.Revision + 1),
            "stale retry cannot queue a newer portrait after a concurrent edit");
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

    private static async Task IndependentWelcomeAsync(string root)
    {
        var store = new CharacterProfileStore(root);
        var profile = await store.SaveAsync("alice", new(0, new("Raven", "Fiction"), OpenAiConsent: true, PortraitConsent: true));
        var enrichment = (await store.ClaimAsync())!;
        await store.CompleteAsync(enrichment, Result(enrichment), null);
        var portrait = store.ClaimPortrait()!;
        var welcome = new CharacterWelcome("Tvá pověst čeká u dveří. Doufá, že si jí nevšimneš.", "",
            "Některé věci tu mají vlastní vůli.", [], WelcomeWriterInput.AllowedTeaserId(portrait.Input, portrait.Enrichment));
        var final = CharacterWelcomePolicy.LegacyText(welcome);
        Check(store.SetPortraitExperience(portrait, final, welcome), "successful welcome persists before the image request");
        Check(store.ReadPortrait("alice", profile.ProfileId, profile.Revision) is { Status: "running", Welcome: not null },
            "welcome is readable while image generation runs");
        Check(store.CompletePortrait(portrait, null, "openai_http_503"), "image failure completes independently");
        var failed = new CharacterProfileStore(root).ReadPortrait("alice", profile.ProfileId, profile.Revision)!;
        Check(failed is { Status: "failed" } && failed.Welcome?.Joke == welcome.Joke && failed.FinalMessage == final,
            "image failure and restart preserve the generated welcome");
        Check(store.RetryPortrait("alice"), "failed image can be retried explicitly");
        var retry = store.ClaimPortrait()!;
        Check(store.ReadPortrait("alice", profile.ProfileId, profile.Revision)?.Welcome?.Joke == welcome.Joke,
            "image retry reuses the same successful joke");
        await store.DeleteAsync("alice", profile.Revision, profile.ProfileId);
        Check(!store.SetPortraitExperience(retry, final, welcome), "deleted character cannot regain its welcome from a late response");
    }

    private static async Task MissingPortraitAsync(string root)
    {
        var store = new CharacterProfileStore(root);
        var profile = await store.SaveAsync("alice", new(0, new("Raven", "Fiction"), OpenAiConsent: true));
        var enrichment = (await store.ClaimAsync())!;
        await store.CompleteAsync(enrichment, Result(enrichment), null);
        Check(store.ReadPortrait("alice", profile.ProfileId, profile.Revision) is null, "legacy character has no portrait request");
        Check(!store.RequestPortrait("other-account", profile.ProfileId, profile.Revision)
            && !store.RequestPortrait("alice", profile.ProfileId, profile.Revision + 1),
            "portrait request is fenced by owner and current revision");
        Check(store.RequestPortrait("alice", profile.ProfileId, profile.Revision)
            && !store.RequestPortrait("alice", profile.ProfileId, profile.Revision),
            "missing portrait is queued idempotently without rewriting the character");
        var job = store.ClaimPortrait()!;
        Check(job.ProfileId == profile.ProfileId && job.Revision == profile.Revision,
            "queued portrait retains the saved character identity");
        await store.FenceAccountDeletionAsync("alice");
        Check(!store.RequestPortrait("alice", profile.ProfileId, profile.Revision), "deletion fence denies new portrait requests");
    }

    private static async Task FailedResearchAsync(string root)
    {
        var store = new CharacterProfileStore(root);
        var profile = await store.SaveAsync("alice", new(0, new("Raven", "Fiction"), OpenAiConsent: true, PortraitConsent: true));
        var enrichment = (await store.ClaimAsync())!;
        await store.CompleteAsync(enrichment, null, "character_provider_invalid_response");
        Check(store.ReadPortrait("alice", profile.ProfileId, profile.Revision)?.Status == "queued",
            "research failure does not misreport a queued image as failed");
        var portrait = store.ClaimPortrait();
        Check(portrait is not null && portrait.Input.Name == profile.UserInput.Name
            && portrait.Enrichment is { NeedsClarification: true, Summary.Length: 0, Facts.Length: 0 },
            "image can use the exact saved input without invented research facts");
        Check(!WelcomeWriterInput.IdentityReliable(portrait!.Input, portrait.Enrichment),
            "failed research permits no verified identity in the generated welcome");
        Check(store.CompletePortrait(portrait, [1, 2, 3], null), "image result remains publishable after research failure");
        Check((await store.ReadAsync("alice"))?.Status == "failed"
            && store.ReadPortrait("alice", profile.ProfileId, profile.Revision)?.Status == "ready",
            "research and portrait retain their independent truthful statuses");
    }

    private static CharacterEnrichment Result(CharacterJob job) => new(job.UserInput.Name, job.UserInput.World,
        "A fictional traveler.", [], [], [], false, [], "fake", "portrait-tests", DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        job.Revision, "synthetic");

    private static void Check(bool condition, string message)
    {
        if (!condition) { throw new InvalidOperationException("Portrait test failed: " + message); }
    }
}
