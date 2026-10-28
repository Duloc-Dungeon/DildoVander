using System.Text;
using Kufr.Backend.Features.Customers;
using Kufr.Backend.Features.Show;
using Kufr.Backend.Features.VoiceTurns;
using Kufr.Backend.Features.Wands;
using Kufr.Backend.Features.Keepsakes;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

// Explicit, bounded diagnostic only. Normal tests never call this method.
internal static class MobileWorkshopLiveTests
{
    internal static async Task VerifyArtworkAsync(string sourceDatabase, string imageModel)
    {
        const string expectedWand = "0196ee0d47164407b457b3906a482d49";
        var output = Path.GetFullPath(Path.Combine("ai-eval", "results", "magicky-kufr-live-artwork"));
        Directory.CreateDirectory(output);
        var database = Path.Combine(output, "workshop.sqlite");
        // Recover the actual fictional walkthrough's selected result. Copying
        // keeps its source database read-only and lets retries inspect this job.
        if (!File.Exists(database)) File.Copy(sourceDatabase, database);
        var configuration = new ConfigurationBuilder().AddEnvironmentVariables().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Backend:DatabasePath"] = database,
            ["Keepsake:DataRoot"] = Path.Combine(output, "assets"),
            ["Keepsake:DataProtectionRoot"] = Path.Combine(output, "keys"),
            ["Keepsake:Enabled"] = "true", ["Keepsake:GenerationEnabled"] = "true",
            ["Keepsake:ProviderMode"] = "openai", ["Keepsake:ImageModel"] = imageModel,
            ["Keepsake:Origin"] = "https://synthetic.test"
        }).Build();
        if (!MobileWandWorkshopWorker.RealArtworkConfigured(configuration)) throw new InvalidOperationException("Real image configuration required.");
        using var lookup = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Mode = SqliteOpenMode.ReadOnly }.ToString());
        lookup.Open();
        using var command = lookup.CreateCommand();
        command.CommandText = "SELECT account_id,operation_id FROM wand_generation_sessions WHERE json_extract(selected_json,'$.wandId')=$id";
        command.Parameters.AddWithValue("$id", expectedWand);
        using var row = command.ExecuteReader();
        if (!row.Read()) throw new InvalidOperationException("The original live walkthrough's selected wand was not found.");
        var owner = row.GetString(0); var operation = row.GetString(1); row.Close();
        var wands = new WandGenerationStore(configuration, TimeProvider.System);
        var selected = wands.Read(owner, operation)?.SelectedWand ?? throw new InvalidOperationException("Owner-scoped result missing.");
        var keepsakes = new KeepsakeStore(configuration, TimeProvider.System);
        var issued = keepsakes.Issue(MobileWandWorkshopWorker.ArtworkSnapshot(selected), new(owner, "isolated-live-image-owner"));
        var actor = KeepsakeActor.ForAccount(owner, "isolated-live-image-owner", () => true);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        try
        {
            var artwork = keepsakes.FindOwnedWandArtwork(owner, selected.WandId);
            if (artwork?.Status != "ready")
            {
                var lease = keepsakes.LeaseNext() ?? throw new InvalidOperationException("No new image dispatch is safely available.");
                if (lease.Kind != "wand_artwork") throw new InvalidOperationException("Only one image job may be dispatched.");
                var request = keepsakes.Dispatch(lease) ?? throw new InvalidOperationException("Image dispatch unavailable.");
                if (request.Snapshot.GeneratedWand?.WandId != expectedWand) throw new InvalidOperationException("Image identity mismatch.");
                using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
                // Story model is unused by this image-only production branch.
                using var provider = new OpenAiKeepsakeProvider(http, configuration["OPENAI_API_KEY"]!, "", imageModel);
                Console.WriteLine("Dispatching one canonical image for the original saved fictional WandId.");
                var generated = await provider.GenerateAsync(request, timeout.Token);
                if (generated.Synthetic || !keepsakes.Complete(lease, generated)) throw new InvalidOperationException("Canonical publication failed.");
                artwork = keepsakes.FindOwnedWandArtwork(owner, expectedWand);
            }
            if (artwork?.AssetId is not { } assetId || artwork.Status != "ready") throw new InvalidOperationException("Saved canonical asset missing.");
            var asset = keepsakes.ReadAsset(issued.Id, actor, assetId);
            await File.WriteAllBytesAsync(Path.Combine(output, "wand.png"), asset.Bytes, timeout.Token);
            wands.CompleteWorkshop(owner, operation, issued.Id);
            if (wands.Read(owner, operation)?.SelectedWand?.WandId != expectedWand) throw new InvalidOperationException("Saved wand changed.");
            var deniedForeign = false;
            try { keepsakes.ReadAsset(issued.Id, KeepsakeActor.ForAccount("foreign-live-fixture", "foreign", () => true), assetId); }
            catch (KeepsakeException) { deniedForeign = true; }
            if (!deniedForeign) throw new InvalidOperationException("Foreign artwork access was not rejected.");
            await File.WriteAllTextAsync(Path.Combine(output, "receipt.json"), System.Text.Json.JsonSerializer.Serialize(new
            {
                wandId = expectedWand, locator = issued.Id, assetId, artwork.Status, artwork.Synthetic,
                bytes = asset.Bytes.Length, imageModel, foreignAccessDenied = deniedForeign,
                wand = selected.ToPublic(), manufactureCalls = 0, questionAuthoringCalls = 0
            }), timeout.Token);
            Console.WriteLine($"Canonical image saved and owner-authorized: wand={expectedWand}; asset={assetId}; bytes={asset.Bytes.Length}; foreign access rejected.");
            Console.WriteLine($"Artwork evidence: {output}");
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            var status = error is System.ClientModel.ClientResultException providerError ? providerError.Status : 0;
            Console.Error.WriteLine($"Artwork verification stopped ({error.GetType().Name}, status={status}); no automatic retry.");
            Environment.ExitCode = 1;
        }
    }

    // These are the three persisted approved turns from the single fictional
    // walkthrough. Replaying them verifies speech without another manufacture.
    internal static async Task VerifySpokenTurnsAsync()
    {
        var configuration = new ConfigurationBuilder().AddEnvironmentVariables().Build();
        if (string.IsNullOrWhiteSpace(configuration["OPENAI_API_KEY"]))
        {
            Console.WriteLine("Voice verification skipped: no configured API key.");
            return;
        }
        var approved = new[]
        {
            "Když se ti opravovaný most začne zvedat dřív, než máš hotovo, co uděláš jako první?",
            "Když modrá nit u kola praskne a most se dál zvedá, jakou pomoc čekáš od hůlky?",
            "Most má držet, ne dělat dojem. Jaký drobný zlozvyk bys snesla u hůlky, na kterou je při opravách mostů spoleh?"
        };
        var output = Path.GetFullPath(Path.Combine("ai-eval", "results", "magicky-kufr-voice-verification"));
        Directory.CreateDirectory(output);
        var receipts = new List<object>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        using var speech = new ShowSpeechProvider(configuration, new LiveHostLifetime(), NullLogger<ShowCueSpeechClient>.Instance);
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(25) };
        var transcription = new OpenAiTranscriptionClient(http, configuration["OPENAI_API_KEY"]!);
        try
        {
            for (var index = 0; index < approved.Length; index++)
            {
                var wav = await speech.SpeakAsync(approved[index], Kufr.Backend.Features.Speech.SpeechOptions.Default, timeout.Token);
                await File.WriteAllBytesAsync(Path.Combine(output, $"question-{index + 1}.wav"), wav, timeout.Token);
                var heard = await transcription.TranscribeAsync(wav, "cs-CZ", timeout.Token);
                var expected = NormalizeSpeech(approved[index]);
                var actual = NormalizeSpeech(heard);
                var similarity = SpeechSimilarity(expected, actual);
                var passed = similarity >= .90 && heard.Count(character => character == '?') <= 1 && actual.Length > 0;
                receipts.Add(new { number = index + 1, approved = approved[index], heard, similarity, passed, wavBytes = wav.Length });
                await File.WriteAllTextAsync(Path.Combine(output, "receipt.json"), System.Text.Json.JsonSerializer.Serialize(receipts), timeout.Token);
                Console.WriteLine($"Speech {index + 1}/3: similarity={similarity:F3}; passed={passed}; heard={heard}");
                if (!passed) throw new InvalidOperationException("Spoken turn differs from approved text.");
            }
            Console.WriteLine("Three approved turns verified through production TTS and transcription; no question authoring, wand generation or image generation repeated.");
            Console.WriteLine($"Voice evidence: {output}");
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            Console.Error.WriteLine($"Voice verification stopped ({error.GetType().Name}); no automatic retry.");
            Environment.ExitCode = 1;
        }
    }

    private static string NormalizeSpeech(string value) => new(value.Normalize(NormalizationForm.FormD)
        .Where(char.IsLetterOrDigit).Where(character => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(character)
            != System.Globalization.UnicodeCategory.NonSpacingMark).Select(char.ToLowerInvariant).ToArray());

    private static double SpeechSimilarity(string left, string right)
    {
        if (left.Length == 0 || right.Length == 0) return 0;
        var previous = Enumerable.Range(0, right.Length + 1).ToArray();
        for (var row = 1; row <= left.Length; row++)
        {
            var current = new int[right.Length + 1]; current[0] = row;
            for (var column = 1; column <= right.Length; column++)
            {
                current[column] = Math.Min(Math.Min(current[column - 1] + 1, previous[column] + 1),
                    previous[column - 1] + (left[row - 1] == right[column - 1] ? 0 : 1));
            }
            previous = current;
        }
        return 1 - (double)previous[right.Length] / Math.Max(left.Length, right.Length);
    }

    internal static async Task RunAsync()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENAI_API_KEY")))
        {
            Console.WriteLine("Live workshop skipped before provider calls: OPENAI_API_KEY is not configured.");
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), "dv-mobile-workshop-live-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var configuration = new ConfigurationBuilder().AddEnvironmentVariables().AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Backend:DatabasePath"] = Path.Combine(root, "workshop.sqlite"),
                    ["Keepsake:DataRoot"] = Path.Combine(root, "keepsakes"),
                    ["Keepsake:DataProtectionRoot"] = Path.Combine(root, "keys")
                }).Build();
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(8));
            var account = "live-fictional-" + Guid.NewGuid().ToString("N");
            var operation = Guid.NewGuid().ToString("N");
            var profile = new CharacterConversationSnapshot("fictional-clockwork-borough", 1,
                new("Tereza Klinková", "Clockwork Borough", "Fictional setting", "An entirely invented locksmith who always carries a blue thread.", "en"),
                new("Tereza Klinková", "Clockwork Borough", "Fictional setting", [
                    new("She repairs clockwork bridges and keeps a blue thread tied around her left glove.", []),
                    new("She tends to improvise when a careful plan breaks.", []),
                    new("She trusts practical tools more than ceremonial titles.", [])
                ], ["How does she react to a failed plan?"], [], false, [], "synthetic", "live-fixture", 0, 1, "synthetic"), 1);

            var store = new WandGenerationStore(configuration, TimeProvider.System);
            store.BeginMobile(account, operation, profile, 1);
            store.BeginWorkshop(account, operation, profile, 1, "fictional-live-group", 1);
            using var questions = new OpenAiMobileWandQuestionProvider(configuration);
            using var speech = new ShowSpeechProvider(configuration, new LiveHostLifetime(), NullLogger<Kufr.Backend.Features.Show.ShowCueSpeechClient>.Instance);
            using var transcribeHttp = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(25) };
            var transcription = new OpenAiTranscriptionClient(transcribeHttp, Environment.GetEnvironmentVariable("OPENAI_API_KEY")!);
            var answers = new[]
            {
                "Nejdřív bych uvázala modrou nit ke kole a zkusila najít, co se zaseklo.",
                "Hůlka by měla opravit nejbližší bezpečný most, abych mohla pokračovat pěšky.",
                "Ať občas při kouzlení tiše cvakne, jako by si sama kontrolovala čas."
            };
            var spokenQuestions = new List<string>(3);
            for (var index = 0; index < 3; index++)
            {
                timeout.Token.ThrowIfCancellationRequested();
                var job = store.ClaimWorkshopQuestion() ?? throw new InvalidOperationException("Question step was not claimable.");
                var draft = await questions.AuthorAsync(job, timeout.Token);
                store.CompleteWorkshopQuestion(job, draft.Question, draft.BasedOnFactIds, false);
                var state = store.ReadWorkshopState(account, operation);
                var question = state.Question ?? throw new InvalidOperationException("Approved question was not exposed.");
                if (question.Number != index + 1 || question.Total != 3 || question.UsedFallback)
                    throw new InvalidOperationException("Question approval state failed.");
                var wav = await speech.SpeakAsync(question.Question, Kufr.Backend.Features.Speech.SpeechOptions.Default, timeout.Token);
                var heard = await transcription.TranscribeAsync(wav, "cs-CZ", timeout.Token);
                if (string.IsNullOrWhiteSpace(heard)) throw new InvalidOperationException("Speech transcription was empty.");
                spokenQuestions.Add(question.Question);
                Console.WriteLine($"Question {question.Number}/3 approved and spoken; transcript nonempty ({Encoding.UTF8.GetByteCount(heard)} UTF-8 bytes).");
                store.CloseWorkshopQuestion(account, operation, question.QuestionId, question.QuestionRevision, answers[index], "text");
            }

            var steps = store.ReadWorkshopState(account, operation).Steps;
            if (steps.Length != 3 || steps.Any(step => step.Answer is null) || steps.Select(step => step.QuestionId).Distinct().Count() != 3)
                throw new InvalidOperationException("Three persisted question/answer steps were not present.");
            store.Begin(account, operation, profile, 1, openAiConsent: true, outputLanguage: "cs");
            using var generationProvider = new OpenAiWandGenerationProvider(configuration, store);
            var generation = await new WandGenerationService(store, generationProvider).GenerateAsync(account, operation,
                _ => Task.FromResult(true), timeout.Token, evidence: steps.Select(MobileWandWorkshopWorker.StepEvidence).ToArray());
            if (generation.Status != "ready" || generation.Candidates.Length != 1)
                throw new InvalidOperationException("Single generated wand was not ready.");
            var selected = store.Select(account, operation, profile.ProfileId, profile.Revision, 1, 1);
            Console.WriteLine($"One wand saved: {selected.WandId}; name={selected.Name}; material={selected.Material}; affinity={selected.Affinity}.");
            Console.WriteLine($"Three spoken questions: {string.Join(" | ", spokenQuestions)}");
            Console.WriteLine("Live result: fictional profile and typed answers; production question, wand, speech and transcription providers. This bounded text/voice harness does not dispatch canonical artwork; full image acceptance needs a configured real image provider and an owner-authorized workshop session.");
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // Never print provider bodies, exception messages, headers, or credentials.
            Console.Error.WriteLine($"Live workshop stopped safely ({error.GetType().Name}); no automatic retry was attempted.");
            Environment.ExitCode = 1;
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private sealed class LiveHostLifetime : IHostApplicationLifetime
    {
        private readonly CancellationTokenSource stopping = new();
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => stopping.Token;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() => stopping.Cancel();
    }
}
