using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Kufr.Backend.Features.Keepsakes;

internal sealed partial class KeepsakeStore
{
    internal const string WandArtworkPromptVersion = "personal-wand-art-v5-transparent-cutout";

    // One-time deterministic adoption during startup, never during GET. Preserve
    // the currently published illustration; retain contradictory older artwork
    // privately and invalidate its old share grants rather than substituting it.
    private void AdoptLegacyWandArtwork(SqliteConnection connection)
    {
        using var transaction = connection.BeginTransaction();
        using var lookup = Command(connection, transaction,
            "SELECT id,document,published_edition_id FROM dv10_keepsakes WHERE deleted=0 "
            + "AND json_extract(document,'$.snapshot.generatedWand') IS NOT NULL "
            + "AND json_extract(document,'$.artwork') IS NULL");
        var rows = new List<(string Id, Document Document, string? Published)>();
        using (var reader = lookup.ExecuteReader())
            while (reader.Read()) rows.Add((reader.GetString(0), System.Text.Json.JsonSerializer.Deserialize<Document>(reader.GetString(1), Json)!, reader.IsDBNull(2) ? null : reader.GetString(2)));
        foreach (var (id, document, published) in rows)
        {
            var valid = document.Editions.Where(edition => edition.PublicationState != "private_history"
                && document.Assets.Any(asset => asset.Id == edition.ArtworkAssetId && asset.Kind == "base_image" && asset.UploadId is null)).ToArray();
            var chosen = valid.FirstOrDefault(edition => edition.Id == published) ?? valid.OrderBy(edition => edition.CreatedAt).FirstOrDefault();
            if (chosen is not null)
            {
                document.Artwork = new(document.Snapshot.GeneratedWand!.WandId, chosen.ArtworkAssetId, 1,
                    chosen.Synthetic, chosen.Provider, chosen.Model, "legacy-adopted-v1", chosen.CreatedAt);
                for (var index = 0; index < document.Editions.Count; index++)
                {
                    var old = document.Editions[index];
                    if (old.ArtworkAssetId.Length == 0 || old.ArtworkAssetId == chosen.ArtworkAssetId) continue;
                    document.Editions[index] = old with { PublicationState = "private_history" };
                    Execute(connection, transaction, "DELETE FROM dv10_grants WHERE link_hash IN (SELECT secret_hash FROM dv10_links WHERE keepsake_id=$id AND edition_id=$edition AND kind='share')", ("$id", id), ("$edition", old.Id));
                    Execute(connection, transaction, "DELETE FROM dv10_links WHERE keepsake_id=$id AND edition_id=$edition AND kind='share'", ("$id", id), ("$edition", old.Id));
                    RecordRevocation(connection, transaction, id, null, "legacy_artwork_reconciled");
                }
                document.Revision++;
                Save(connection, transaction, id, document);
            }
            else
            {
                // A queued old story must not occupy the only queue slot while
                // waiting for its newly independent artwork dependency.
                Execute(connection, transaction, "UPDATE dv10_jobs SET state='awaiting_artwork' WHERE keepsake_id=$id AND kind='base' AND state IN ('queued','leased')", ("$id", id));
            }
        }
        transaction.Commit();
    }

    private bool TryAdmitWandArtwork(SqliteConnection connection, SqliteTransaction transaction, string id, Document document)
    {
        if (document.Snapshot.GeneratedWand is null || CanonicalGeneratedArtworkId(document) is not null
            || document.CurrentArtworkJob is not null || !MayDispatch("wand_artwork")
            || !HasQueueCapacity(connection, transaction) || !CanReserveGenerationStorage()) return false;
        var job = EnqueueInternal(connection, transaction, id, "wand_artwork", new(new()));
        document.CurrentArtworkJob = job.Id;
        Save(connection, transaction, id, document);
        RecordCompletionUpdates(connection, transaction, id);
        return true;
    }

    private OwnedWandArtworkReference? ArtworkReference(SqliteConnection connection, SqliteTransaction? transaction,
        string id, Document document)
    {
        if (document.Snapshot.GeneratedWand is null) return null;
        if (CanonicalGeneratedArtworkId(document) is { } assetId)
        {
            var asset = document.Assets.SingleOrDefault(item => item.Id == assetId);
            var artwork = document.Artwork;
            var repairJob = document.CurrentArtworkJob is { } currentJob ? ReadJob(connection, transaction, currentJob) : null;
            var repairing = artwork is not null && repairJob is { State: "queued" or "leased" or "dispatched" or "awaiting_reconciliation" }
                && IsRepairJob(connection, transaction, repairJob.Id);
            var repairFailed = artwork is not null && repairJob is { State: "failed" }
                && IsRepairJob(connection, transaction, repairJob.Id);
            return new(id, assetId, Status: repairing ? "repairing" : repairFailed ? "repair_failed" : "ready", Synthetic: artwork?.Synthetic
                    ?? document.Editions.FirstOrDefault(edition => edition.ArtworkAssetId == assetId)?.Synthetic ?? false,
                Version: artwork?.Version ?? 1, Sha256: asset?.Sha256,
                CanRepair: asset is not null && artwork is not null && !repairing && IsRealWandArtworkConfigured(),
                SourceAssetId: artwork?.SourceAssetId, SourceSha256: artwork?.SourceSha256);
        }
        if (document.CurrentArtworkJob is not { } jobId) return new(id, null, "missing", MayDispatch("wand_artwork"));
        var job = ReadJob(connection, transaction, jobId);
        var status = job.State switch { "queued" => "queued", "leased" or "dispatched" => "running", _ => "failed" };
        return new(id, null, status, status == "failed" && MayDispatch("wand_artwork"), job.ErrorCode);
    }

    private static bool IsRepairJob(SqliteConnection connection, SqliteTransaction? transaction, string jobId)
    {
        using var command = Command(connection, transaction,
            "SELECT json_extract(request_json,'$.artworkSourceVersion') FROM dv10_jobs WHERE id=$id", ("$id", jobId));
        return command.ExecuteScalar() is not null and not DBNull;
    }

    internal KeepsakeJob RetryWandArtwork(string id, KeepsakeActor actor, string key)
    {
        RequireFeature("GenerationEnabled");
        lock (gate)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            RequireAccess(connection, transaction, id, actor, KeepsakeScope.Write);
            var document = Load(connection, transaction, id);
            if (document.Snapshot.GeneratedWand is null) throw new KeepsakeException("personal_wand_unavailable", 409);
            var scope = "retry-wand-artwork:" + id;
            if (Replay(connection, transaction, scope, key, "v1") is { } replay) return ReadJob(connection, transaction, replay);
            if (document.CurrentArtworkJob is { } current)
            {
                var old = ReadJob(connection, transaction, current);
                if (CanonicalGeneratedArtworkId(document) is not null || old.State is "queued" or "leased" or "dispatched")
                {
                    Remember(connection, transaction, scope, key, "v1", current);
                    transaction.Commit();
                    return old;
                }
                // Explicit recovery abandons an uncertain attempt; incrementing its fence
                // rejects late results. Restart alone never repeats a paid dispatch.
                Execute(connection, transaction, "UPDATE dv10_jobs SET state='cancelled',fence=fence+1 WHERE id=$id", ("$id", current));
            }
            else if (CanonicalGeneratedArtworkId(document) is not null)
                throw new KeepsakeException("wand_artwork_already_published", 409);
            CheckAdmissionCapacity();
            if (!HasQueueCapacity(connection, transaction)) throw new KeepsakeException("generation_backpressure", 429);
            var job = EnqueueInternal(connection, transaction, id, "wand_artwork", new(new()));
            document.CurrentArtworkJob = job.Id;
            document.Revision++;
            Save(connection, transaction, id, document);
            Remember(connection, transaction, scope, key, "v1", job.Id);
            RecordCompletionUpdates(connection, transaction, id);
            transaction.Commit();
            return job;
        }
    }

    internal KeepsakeJob RepairWandArtwork(string id, KeepsakeActor actor, string wandId,
        int expectedVersion, string sourceAssetId, string sourceSha256, string key)
    {
        if (!Guid.TryParseExact(wandId, "N", out _) || !Guid.TryParseExact(sourceAssetId, "D", out _)
            || expectedVersion < 1 || sourceSha256.Length != 64
            || sourceSha256.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new KeepsakeException("invalid_wand_artwork_repair", 400);
        }
        lock (gate)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            RequireAccess(connection, transaction, id, actor, KeepsakeScope.Write);
            var document = Load(connection, transaction, id);
            sourceSha256 = sourceSha256.ToUpperInvariant();
            var body = JsonSerializer.Serialize(new
            {
                wandId,
                expectedVersion,
                sourceAssetId,
                sourceSha256 = sourceSha256.ToUpperInvariant()
            }, Json);
            var scope = "repair-wand-artwork:" + id;
            if (Replay(connection, transaction, scope, key, body) is { } replay)
            {
                return ReadJob(connection, transaction, replay);
            }

            RequireFeature("GenerationEnabled");
            if (!IsRealWandArtworkConfigured())
            {
                throw new KeepsakeException("wand_artwork_provider_unavailable", 503);
            }

            var wand = document.Snapshot.GeneratedWand;
            var artwork = document.Artwork;
            var source = document.Assets.SingleOrDefault(asset => asset.Id == sourceAssetId);
            if (wand?.WandId != wandId || artwork is null || artwork.Version != expectedVersion
                || artwork.AssetId != sourceAssetId || source is null || source.Sha256 != sourceSha256
                || source.Kind != "base_image" || source.MediaType != "image/png")
            {
                throw new KeepsakeException("wand_artwork_source_changed", 409);
            }

            if (document.CurrentArtworkJob is { } pendingId)
            {
                var pending = ReadJob(connection, transaction, pendingId);
                if (pending.State is "queued" or "leased" or "dispatched" or "awaiting_reconciliation")
                {
                    throw new KeepsakeException("wand_artwork_repair_in_progress", 409);
                }
            }

            CheckAdmissionCapacity();
            if (!HasQueueCapacity(connection, transaction))
            {
                throw new KeepsakeException("generation_backpressure", 429);
            }

            var request = new JobRequest(new(), ArtworkWandId: wandId,
                ArtworkSourceVersion: expectedVersion, ArtworkSourceAssetId: sourceAssetId,
                ArtworkSourceSha256: sourceSha256.ToUpperInvariant());
            var job = EnqueueInternal(connection, transaction, id, "wand_artwork", request);
            document.CurrentArtworkJob = job.Id;
            document.Revision++;
            Save(connection, transaction, id, document);
            Remember(connection, transaction, scope, key, body, job.Id);
            RecordCompletionUpdates(connection, transaction, id);
            transaction.Commit();
            return job;
        }
    }

    private bool IsRealWandArtworkConfigured() => MayDispatch("wand_artwork")
        && configuration.GetValue<string>("Keepsake:ProviderMode") == "openai"
        && !string.IsNullOrWhiteSpace(configuration["OPENAI_API_KEY"])
        && OpenAiKeepsakeProvider.SupportsTransparentWandArtwork(configuration["Keepsake:ImageModel"]
            ?? Kufr.Backend.Features.Images.ImageGenerationDefaults.Model);

    private bool CompleteWandArtwork(SqliteConnection connection, SqliteTransaction transaction, LeasedJob job,
        Document document, JobRequest request, KeepsakeProviderOutput output, bool publish)
    {
        if (!publish) return false;
        var wand = document.Snapshot.GeneratedWand ?? throw new KeepsakeException("personal_wand_unavailable", 409);
        var isRepair = request.ArtworkSourceVersion is not null;
        if (isRepair)
        {
            var current = document.Artwork;
            var source = document.Assets.SingleOrDefault(asset => asset.Id == request.ArtworkSourceAssetId);
            if (request.ArtworkWandId != wand.WandId || current is null
                || current.Version != request.ArtworkSourceVersion || current.AssetId != request.ArtworkSourceAssetId
                || source?.Sha256 != request.ArtworkSourceSha256)
            {
                Execute(connection, transaction, "UPDATE dv10_jobs SET state='archived_private' WHERE id=$id AND fence=$f",
                    ("$id", job.Id), ("$f", job.Fence));
                RecordCompletionUpdates(connection, transaction, job.KeepsakeId);
                transaction.Commit();
                return true;
            }
        }
        else if (CanonicalGeneratedArtworkId(document) is not null || document.Artwork is not null)
        {
            return false;
        }

        var asset = StoreAsset(job.KeepsakeId, document, "base_image", output.MediaType, output.ImageBytes);
        StoreAsset(job.KeepsakeId, document, "provider_response", "application/json",
            System.Text.Encoding.UTF8.GetBytes(output.ContentResponse));
        StoreAsset(job.KeepsakeId, document, "prompt", "text/plain; charset=utf-8", System.Text.Encoding.UTF8.GetBytes(output.Prompt));
        document.Artwork = new(wand.WandId, asset.Id, isRepair ? document.Artwork!.Version + 1 : 1,
            output.Synthetic, output.Provider, output.Model, WandArtworkPromptVersion, Now,
            request.ArtworkSourceAssetId, request.ArtworkSourceSha256, output.GenerationParameters);
        document.Revision++;
        Save(connection, transaction, job.KeepsakeId, document);
        Execute(connection, transaction, "UPDATE dv10_jobs SET state='completed' WHERE id=$id AND fence=$f", ("$id", job.Id), ("$f", job.Fence));
        Execute(connection, transaction, "UPDATE dv10_jobs SET state='queued' WHERE keepsake_id=$id AND kind='base' AND state='awaiting_artwork'", ("$id", job.KeepsakeId));
        if (!isRepair) TryAdmitAutomaticBase(connection, transaction, job.KeepsakeId, document);
        RecordCompletionUpdates(connection, transaction, job.KeepsakeId);
        transaction.Commit();
        return true;
    }

    internal Kufr.Backend.Features.Show.DuelView ProjectBattleArtwork(Kufr.Backend.Features.Show.DuelView duel)
    {
        // Preserve identity/event snapshots in storage, but do not leave withdrawn
        // opponent artwork in an open account-history presentation after polling.
        return duel with
        {
            Left = duel.Left with { ArtworkUrl = Visible(duel.Left) },
            Right = duel.Right with { ArtworkUrl = Visible(duel.Right) }
        };
        string? Visible(Kufr.Backend.Features.Show.DuelIdentitySnapshot side) =>
            side.ArtworkUrl is { } original && PublicWandArtworkUrl(side.IdentityId, duel.EveningId, duel.DeviceId) == original ? original : null;
    }

    // Only approved public editions expose art to battle spectators. The URL is
    // an identifier, not a capability; every byte request repeats publication checks.
    internal string? PublicWandArtworkUrl(string identityId, string eveningId, string deviceId)
        => PublicWandArtworkUrl(identityId, eveningId, deviceId, out _);

    internal string? PublicWandArtworkUrl(string identityId, string eveningId, string deviceId, out bool synthetic)
    {
        synthetic = false;
        lock (gate)
        {
            using var connection = Open();
            using var command = Command(connection, null, """
                SELECT k.id,k.document,k.published_edition_id FROM dv10_keepsakes k,
                    json_each(k.document,'$.wizardIdentities') i
                WHERE k.device_id=$device AND k.evening_id=$evening AND k.deleted=0
                    AND json_extract(i.value,'$.identityId')=$identity
                """, ("$device", deviceId), ("$evening", eveningId), ("$identity", identityId));
            using var reader = command.ExecuteReader();
            if (!reader.Read() || reader.IsDBNull(2)) return null;
            var id = reader.GetString(0);
            var document = System.Text.Json.JsonSerializer.Deserialize<Document>(reader.GetString(1), Json)!;
            var editionId = reader.GetString(2);
            if (!WandPublicationApproved(document, editionId)) return null;
            var edition = document.Editions.SingleOrDefault(item => item.Id == editionId && item.PublicationState == "ready");
            if (edition is null || !document.Assets.Any(asset => asset.Id == edition.ArtworkAssetId && IsPublicBaseAsset(asset, edition))) return null;
            synthetic = document.Artwork?.Synthetic ?? edition.Synthetic;
            return $"/guest/v1/evenings/{eveningId}/keepsakes/{id}/editions/{editionId}/assets/{edition.ArtworkAssetId}";
        }
    }
}
