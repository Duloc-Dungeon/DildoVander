using System.Security.Cryptography;
using System.Text.Json;
using Kufr.Backend.Features.Keepsakes;
using Kufr.Backend.Features.Wands;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using SkiaSharp;

// Opt-in only: a configured invocation may make one real image-generation call.
internal static class WandArtworkLiveTests
{
    private const string WandId = "0196ee0d47164407b457b3906a482d49";
    private const string ReferenceAttemptFileName = ".wand-reveal-reference-attempted";

    internal static async Task GenerateReferenceAsync(string outputDirectory, string imageModel)
    {
        var apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException("OPENAI_API_KEY is required for paid wand reference generation.");
        }
        if (!OpenAiKeepsakeProvider.SupportsTransparentWandArtwork(imageModel))
        {
            throw new InvalidOperationException("The configured image model is not documented for transparent output.");
        }

        var output = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(output) || File.Exists(output))
        {
            throw new InvalidOperationException("Reference output already exists; refusing to risk a second paid generation.");
        }

        Directory.CreateDirectory(output);
        var attemptedPath = Path.Combine(output, ReferenceAttemptFileName);
        await using (var attempt = new FileStream(attemptedPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            await JsonSerializer.SerializeAsync(attempt, new
            {
                attemptedAtUtc = DateTimeOffset.UtcNow,
                provider = "openai",
                model = imageModel,
                promptVersion = KeepsakeStore.WandArtworkPromptVersion
            }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            await attempt.FlushAsync();
        }
        if (Directory.EnumerateFileSystemEntries(output).Any(path => !string.Equals(
            Path.GetFullPath(path), Path.GetFullPath(attemptedPath), StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("Reference output changed during setup; refusing provider dispatch.");
        }

        var generatedWand = new GeneratedWandPublicSnapshot(
            WandId: "fictional-wand-reference",
            PhysicalSlot: 1,
            Name: "Fictional Reference Wand",
            Material: "Ash and dark wood",
            Core: "Pale crystalline core",
            Temperament: "Fictional visual reference only",
            Lore: "",
            DesignDescription: "A slender vertical wand with a silver carved grip and a pale crystalline core; ash and dark wood form the shaft.",
            Affinity: "",
            Weakness: "",
            Spells: []);
        var snapshot = new KeepsakeIssueSnapshot(
            OutcomeId: "fictional-reference-outcome",
            DeviceId: "fictional-reference-device",
            Slot: 1,
            CatalogId: "fictional-reference-catalog",
            OfficialName: "Fictional Reference",
            CatalogFacts: "",
            PhysicalEvidence: "",
            GeneratedWand: generatedWand);
        var request = new KeepsakeProviderRequest(
            KeepsakeId: "fictional-reference-keepsake",
            Snapshot: snapshot,
            Brief: new KeepsakeBrief(),
            JobId: "fictional-reference-job",
            EditionId: null,
            UploadId: null,
            Variant: null,
            NormalizedPhoto: null,
            ExistingStory: null)
        {
            GenerationKind = "wand_artwork"
        };

        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        using var provider = new OpenAiKeepsakeProvider(http, apiKey, "unused-story-model", imageModel);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        await File.WriteAllTextAsync(Path.Combine(output, "prompt.txt"),
            OpenAiKeepsakeProvider.BuildWandImagePrompt(generatedWand));
        var generated = await provider.GenerateAsync(request, timeout.Token);
        if (generated.Provider != "openai" || generated.Model != imageModel
            || generated.MediaType != "image/png" || generated.Synthetic)
        {
            throw new InvalidDataException("Provider returned unexpected image metadata.");
        }
        var imageBytes = generated.ImageBytes;
        var imagePath = Path.Combine(output, "wand-reference.png");
        // Keep a returned paid artifact even if the subsequent acceptance check fails.
        await File.WriteAllBytesAsync(imagePath, imageBytes);
        var imageInfo = ValidateReferencePng(imageBytes);
        await File.WriteAllTextAsync(Path.Combine(output, "prompt.txt"), generated.Prompt);

        using var response = JsonDocument.Parse(generated.ContentResponse);
        var usage = response.RootElement.TryGetProperty("usage", out var usageElement)
            ? usageElement.Clone()
            : (JsonElement?)null;
        var receipt = new
        {
            provider = generated.Provider,
            model = generated.Model,
            promptVersion = KeepsakeStore.WandArtworkPromptVersion,
            promptSha256 = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(generated.Prompt))).ToLowerInvariant(),
            input = new
            {
                visualOnly = new
                {
                    material = generatedWand.Material,
                    core = generatedWand.Core,
                    designDescription = generatedWand.DesignDescription
                }
            },
            generationParameters = generated.GenerationParameters is null
                ? (JsonElement?)null
                : JsonDocument.Parse(generated.GenerationParameters).RootElement.Clone(),
            image = new
            {
                file = Path.GetFileName(imagePath),
                mediaType = generated.MediaType,
                sha256 = Convert.ToHexString(SHA256.HashData(imageBytes)).ToLowerInvariant(),
                bytes = imageBytes.Length,
                width = imageInfo.Width,
                height = imageInfo.Height,
                alpha = new { present = true, fullyTransparentPixels = imageInfo.FullyTransparentPixels }
            },
            usage
        };
        await File.WriteAllTextAsync(Path.Combine(output, "receipt.json"),
            JsonSerializer.Serialize(receipt, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
        Console.WriteLine($"One fictional wand reference saved to {output}");
    }

    private static (int Width, int Height, long FullyTransparentPixels) ValidateReferencePng(byte[] bytes)
    {
        using var bitmap = SKBitmap.Decode(bytes)
            ?? throw new InvalidDataException("Provider image could not be decoded as PNG.");
        if (bitmap.Width != 1024 || bitmap.Height != 1536)
        {
            throw new InvalidDataException($"Expected a 1024x1536 2:3 image; received {bitmap.Width}x{bitmap.Height}.");
        }
        if (bitmap.AlphaType == SKAlphaType.Opaque)
        {
            throw new InvalidDataException("Generated PNG has no alpha channel.");
        }

        long fullyTransparentPixels = 0;
        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                if (bitmap.GetPixel(x, y).Alpha == 0)
                {
                    fullyTransparentPixels++;
                }
            }
        }
        if (fullyTransparentPixels == 0)
        {
            throw new InvalidDataException("Generated PNG contains no fully transparent pixels.");
        }
        return (bitmap.Width, bitmap.Height, fullyTransparentPixels);
    }

    internal static async Task VerifyAsync(string sourceDatabase, string imageModel)
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENAI_API_KEY")))
        {
            Console.WriteLine("Transparent artwork verification skipped: no configured API key.");
            return;
        }
        if (!OpenAiKeepsakeProvider.SupportsTransparentWandArtwork(imageModel))
        {
            throw new InvalidOperationException("The configured image model is not documented for transparent output.");
        }

        var sourcePath = Path.GetFullPath(sourceDatabase);
        if (!File.Exists(sourcePath)) throw new FileNotFoundException("The original fictional live artwork database was not found.", sourcePath);
        var output = Path.GetFullPath(Path.Combine("ai-eval", "results", "magicky-kufr-transparent-artwork"));
        Directory.CreateDirectory(output);
        var database = Path.Combine(output, "workshop.sqlite");
        var sourceRoot = Path.GetDirectoryName(sourcePath)!;
        if (!File.Exists(database))
        {
            File.Copy(sourcePath, database);
            CopyDirectory(Path.Combine(sourceRoot, "assets"), Path.Combine(output, "assets"));
            if (Directory.Exists(Path.Combine(sourceRoot, "keys")))
                CopyDirectory(Path.Combine(sourceRoot, "keys"), Path.Combine(output, "keys"));
        }
        if (!Directory.Exists(Path.Combine(output, "assets")))
            throw new InvalidOperationException("The isolated artwork clone has no copied source assets.");

        var configuration = new ConfigurationBuilder().AddEnvironmentVariables().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Backend:DatabasePath"] = database,
            ["Keepsake:DataRoot"] = Path.Combine(output, "assets"),
            ["Keepsake:Enabled"] = "true",
            ["Keepsake:GenerationEnabled"] = "true",
            ["Keepsake:ProviderMode"] = "openai",
            ["Keepsake:ImageModel"] = imageModel,
            ["Keepsake:Origin"] = "https://synthetic.test"
        }).Build();

        var ownerBinding = ReadOwnerBinding(database);
        var owner = ownerBinding.OwnerId;
        var actor = KeepsakeActor.ForAccount(owner, ownerBinding.SessionId, () => true);
        var store = new KeepsakeStore(configuration, TimeProvider.System,
            DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(output, "keys"))));
        var view = store.Read(ownerBinding.Locator, actor);
        var selected = view.Snapshot.GeneratedWand;
        if (selected?.WandId != WandId) throw new InvalidOperationException("The saved fictional wand identity changed.");
        var original = view.Artwork?.Version > 1
            ? view.Artwork.SourceAssetId ?? throw new InvalidOperationException("Repair source asset is missing.")
            : view.Artwork?.AssetId ?? throw new InvalidOperationException("The original artwork is missing.");
        var originalAsset = view.Assets.SingleOrDefault(asset => asset.Id == original)
            ?? throw new InvalidOperationException("The original source image asset is missing.");
        var sourceSha = originalAsset.Sha256;
        var sourceContent = store.ReadAsset(ownerBinding.Locator, actor, originalAsset.Id);
        RequireSourcePng(sourceContent.Bytes);

        var current = store.FindOwnedWandArtwork(owner, WandId)
            ?? throw new InvalidOperationException("Owner-scoped active artwork is missing.");
        if (current.Version == 1)
        {
            if (current.AssetId != originalAsset.Id || current.Sha256 != sourceSha)
                throw new InvalidOperationException("The selected source image is not the active artwork version.");
            await RepairOnceAsync(store, actor, ownerBinding, current, imageModel, output);
            current = store.FindOwnedWandArtwork(owner, WandId)
                ?? throw new InvalidOperationException("Repaired artwork is missing.");
        }
        else if (current.SourceAssetId != originalAsset.Id || current.SourceSha256 != sourceSha)
        {
            throw new InvalidOperationException("The active repair does not cite the original source asset and checksum.");
        }

        if (current.AssetId is not { } repairedAsset || current.Version < 2 || current.Status != "ready")
            throw new InvalidOperationException("A completed transparent repair is not available.");
        var repairedContent = store.ReadAsset(ownerBinding.Locator, actor, repairedAsset);
        KeepsakeStore.ValidateWandArtwork(new(null, repairedContent.Bytes, repairedContent.Asset.MediaType,
            "openai", imageModel, "", "", false));
        var after = store.Read(ownerBinding.Locator, actor);
        if (after.Snapshot.GeneratedWand?.WandId != WandId
            || after.Artwork is not { Version: >= 2 }
            || after.Artwork.SourceAssetId != originalAsset.Id
            || after.Artwork.SourceSha256 != sourceSha)
        {
            throw new InvalidOperationException("Repair changed the wand identity or lost source provenance.");
        }

        var foreignDenied = false;
        try { store.ReadAsset(ownerBinding.Locator, KeepsakeActor.ForAccount("foreign-fixture", "foreign", () => true), repairedAsset); }
        catch (KeepsakeException error) when (error.Status is 401 or 403 or 404 or 410) { foreignDenied = true; }
        if (!foreignDenied) throw new InvalidOperationException("Foreign account was allowed to read the repaired image.");

        await File.WriteAllBytesAsync(Path.Combine(output, "wand-source.png"), sourceContent.Bytes);
        await File.WriteAllBytesAsync(Path.Combine(output, "wand-transparent.png"), repairedContent.Bytes);
        await File.WriteAllBytesAsync(Path.Combine(output, "wand-on-dark.png"), Composite(repairedContent.Bytes, new(8, 9, 11)));
        await File.WriteAllBytesAsync(Path.Combine(output, "wand-on-light.png"), Composite(repairedContent.Bytes, new(248, 247, 243)));
        await File.WriteAllTextAsync(Path.Combine(output, "receipt.json"), JsonSerializer.Serialize(new
        {
            wandId = WandId,
            originalAssetId = originalAsset.Id,
            originalSha256 = sourceSha,
            originalVersion = current.Version - 1,
            repairedAssetId = repairedAsset,
            repairedSha256 = repairedContent.Asset.Sha256,
            repairedVersion = current.Version,
            current.Status,
            provider = "openai",
            model = imageModel,
            promptVersion = KeepsakeStore.WandArtworkPromptVersion,
            generationParameters = new { size = "1024x1536", output_format = "png", background = "transparent" },
            transparentPngBytes = repairedContent.Bytes.Length,
            foreignAccessDenied = foreignDenied,
            wandIdentityUnchanged = after.Snapshot.GeneratedWand.WandId == WandId,
            newWandManufactures = 0,
            baseJobsDispatched = 0,
            questionAuthoringCalls = 0
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Console.WriteLine($"Transparent artwork version saved for fictional WandId {WandId}; owner access verified, foreign access denied.");
        Console.WriteLine($"Visual evidence and receipt: {output}");
    }

    private static async Task RepairOnceAsync(KeepsakeStore store, KeepsakeActor actor,
        (string OwnerId, string SessionId, string Locator) binding, OwnedWandArtworkReference source,
        string imageModel, string output)
    {
        var before = store.Read(binding.Locator, actor);
        var activeJobs = before.Jobs.Where(job => job.State is "queued" or "leased" or "dispatched" or "awaiting_reconciliation").ToArray();
        if (activeJobs.Any(job => job.Kind != "base"))
            throw new InvalidOperationException("An unrelated paid image job is already active; refusing provider dispatch.");
        if (activeJobs.Any(job => job.State != "queued"))
            throw new InvalidOperationException("An ambiguous or leased provider job exists; refusing provider dispatch.");

        // The source fixture's old story queue is unrelated to this image-only
        // verification. Retire only never-dispatched base jobs in the isolated clone.
        CancelNeverDispatchedStoryJobs(Path.Combine(output, "workshop.sqlite"), binding.Locator);
        var key = Guid.NewGuid().ToString("D");
        var job = store.RepairWandArtwork(binding.Locator, actor, WandId, source.Version,
            source.AssetId!, source.Sha256!, key);
        var afterEnqueue = store.Read(binding.Locator, actor);
        var newJobs = afterEnqueue.Jobs.Where(item => !before.Jobs.Any(old => old.Id == item.Id)).ToArray();
        if (newJobs.Length != 1 || newJobs[0].Id != job.Id || newJobs[0].Kind != "wand_artwork")
            throw new InvalidOperationException("The repair did not enqueue exactly one image-only job.");

        var lease = store.LeaseNext() ?? throw new InvalidOperationException("The repair image job was not leaseable.");
        if (lease.Id != job.Id || lease.Kind != "wand_artwork")
            throw new InvalidOperationException("The provider queue selected a different job.");
        var request = store.Dispatch(lease) ?? throw new InvalidOperationException("The repair job could not be safely dispatched.");
        if (request.GenerationKind != "wand_artwork" || request.Snapshot.GeneratedWand?.WandId != WandId
            || request.CanonicalArtworkAssetId != source.AssetId)
        {
            throw new InvalidOperationException("The dispatch is not bound to the expected wand and source version.");
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        { Timeout = Timeout.InfiniteTimeSpan };
        using var provider = new OpenAiKeepsakeProvider(http,
            Environment.GetEnvironmentVariable("OPENAI_API_KEY")!, "", imageModel);
        try
        {
            Console.WriteLine("Dispatching one image-only repair for the existing fictional wand.");
            var generated = await provider.GenerateAsync(request, timeout.Token);
            if (generated.Synthetic || !store.Complete(lease, generated))
                throw new InvalidOperationException("The repaired image was not accepted by the canonical store.");
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // Preserve an uncertain provider call for reconciliation; this test
            // never automatically dispatches it a second time.
            store.MarkAmbiguous(lease, "transparent_artwork_attempt_unknown");
            var status = error is System.ClientModel.ClientResultException providerError ? providerError.Status : 0;
            throw new InvalidOperationException($"Image-only repair stopped ({error.GetType().Name}, status={status}); no retry dispatched.");
        }
    }

    private static (string OwnerId, string SessionId, string Locator) ReadOwnerBinding(string database)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = database, Mode = SqliteOpenMode.ReadOnly }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT o.owner_id,o.session_id,o.keepsake_id
            FROM dv10_customer_owners o JOIN dv10_keepsakes k ON k.id=o.keepsake_id
            WHERE k.deleted=0 AND json_extract(k.document,'$.snapshot.generatedWand.wandId')=$wand
            ORDER BY k.rowid DESC LIMIT 1
            """;
        command.Parameters.AddWithValue("$wand", WandId);
        using var row = command.ExecuteReader();
        if (!row.Read()) throw new InvalidOperationException("No owner-bound archive exists for the original fictional wand.");
        return (row.GetString(0), row.GetString(1), row.GetString(2));
    }

    private static void CancelNeverDispatchedStoryJobs(string database, string locator)
    {
        using var connection = new SqliteConnection("Data Source=" + database);
        connection.Open();
        using var transaction = connection.BeginTransaction();
        using (var ambiguous = connection.CreateCommand())
        {
            ambiguous.Transaction = transaction;
            ambiguous.CommandText = "SELECT count(*) FROM dv10_jobs WHERE keepsake_id=$id AND kind='base' AND state IN ('leased','dispatched','awaiting_reconciliation')";
            ambiguous.Parameters.AddWithValue("$id", locator);
            if ((long)ambiguous.ExecuteScalar()! != 0)
                throw new InvalidOperationException("A story provider call is already ambiguous in the source clone.");
        }
        using (var cancel = connection.CreateCommand())
        {
            cancel.Transaction = transaction;
            cancel.CommandText = "UPDATE dv10_jobs SET state='cancelled' WHERE keepsake_id=$id AND kind='base' AND state IN ('queued','awaiting_artwork')";
            cancel.Parameters.AddWithValue("$id", locator);
            cancel.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    private static void RequireSourcePng(byte[] bytes)
    {
        using var bitmap = SKBitmap.Decode(bytes) ?? throw new InvalidOperationException("The original source artwork is not a decodable PNG.");

        var hasVisible = false;
        for (var y = 0; y < bitmap.Height; y++)
        for (var x = 0; x < bitmap.Width; x++)
        {
            var alpha = bitmap.GetPixel(x, y).Alpha;

            hasVisible |= alpha > 0;
        }
        if (!hasVisible) throw new InvalidOperationException("The selected original source artwork is empty.");
    }

    private static byte[] Composite(byte[] sourceBytes, SKColor background)
    {
        using var source = SKBitmap.Decode(sourceBytes) ?? throw new InvalidOperationException("Repaired PNG could not be decoded.");
        using var bitmap = new SKBitmap(new SKImageInfo(source.Width, source.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(background);
        canvas.DrawBitmap(source, 0, 0, new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None));
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        return encoded.ToArray();
    }

    private static void CopyDirectory(string source, string destination)
    {
        if (!Directory.Exists(source)) throw new DirectoryNotFoundException("Expected source artwork directory is missing.");
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: false);
        foreach (var directory in Directory.EnumerateDirectories(source))
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
    }
}
