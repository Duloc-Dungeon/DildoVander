using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Kufr.Backend.Features.Chronicle;

/// <summary>Owns durable, account-scoped event projections and illustration work.</summary>
internal sealed partial class ChronicleStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly string _connectionString;
    private readonly object _gate = new();
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(10);
    private const int MaxAttempts = 5;
    private const int MaxPendingIllustrations = 500;

    internal ChronicleStore(string dataRoot)
    {
        Directory.CreateDirectory(dataRoot);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(dataRoot, "chronicle.sqlite"),
            DefaultTimeout = 5,
            ForeignKeys = true,
            Pooling = true
        }.ToString();
        using var connection = Open();
        Execute(connection, null, """
            CREATE TABLE IF NOT EXISTS chronicle_events(
                id TEXT PRIMARY KEY,source_kind TEXT NOT NULL,source_id TEXT NOT NULL,
                group_id TEXT NOT NULL,occurred_at INTEGER NOT NULL,event_json TEXT NOT NULL,
                event_hash TEXT NOT NULL,illustration_state TEXT NOT NULL DEFAULT 'pending',
                image BLOB,media_type TEXT,image_synthetic INTEGER NOT NULL DEFAULT 0,revision INTEGER NOT NULL DEFAULT 1,
                UNIQUE(source_kind,source_id));
            CREATE TABLE IF NOT EXISTS chronicle_owners(
                event_id TEXT NOT NULL,owner_id TEXT NOT NULL,revealed_at INTEGER,
                PRIMARY KEY(event_id,owner_id),FOREIGN KEY(event_id) REFERENCES chronicle_events(id) ON DELETE CASCADE);
            CREATE TABLE IF NOT EXISTS chronicle_awards(
                event_id TEXT NOT NULL,owner_id TEXT NOT NULL,award_id TEXT NOT NULL,title TEXT NOT NULL,
                scope TEXT NOT NULL,evidence TEXT NOT NULL,acquired_at INTEGER NOT NULL,
                PRIMARY KEY(event_id,owner_id,award_id),UNIQUE(owner_id,award_id,scope),
                FOREIGN KEY(event_id,owner_id) REFERENCES chronicle_owners(event_id,owner_id) ON DELETE CASCADE);
            CREATE TABLE IF NOT EXISTS chronicle_jobs(
                id TEXT PRIMARY KEY,event_id TEXT NOT NULL UNIQUE,state TEXT NOT NULL,
                attempts INTEGER NOT NULL DEFAULT 0,fence INTEGER NOT NULL DEFAULT 0,
                lease_until INTEGER NOT NULL DEFAULT 0,available_at INTEGER NOT NULL DEFAULT 0,
                error_code TEXT,FOREIGN KEY(event_id) REFERENCES chronicle_events(id) ON DELETE CASCADE);
            CREATE TABLE IF NOT EXISTS chronicle_deleted_accounts(owner_id TEXT PRIMARY KEY,deleted_at INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS chronicle_deleted_profiles(owner_id TEXT NOT NULL,profile_id TEXT NOT NULL,deleted_at INTEGER NOT NULL,
                PRIMARY KEY(owner_id,profile_id));
            CREATE INDEX IF NOT EXISTS chronicle_owner_order ON chronicle_owners(owner_id,revealed_at);
            CREATE INDEX IF NOT EXISTS chronicle_job_queue ON chronicle_jobs(state,available_at,lease_until);
            DELETE FROM chronicle_awards WHERE NOT EXISTS(SELECT 1 FROM chronicle_owners o
                WHERE o.event_id=chronicle_awards.event_id AND o.owner_id=chronicle_awards.owner_id);
            DELETE FROM chronicle_jobs WHERE NOT EXISTS(SELECT 1 FROM chronicle_events e WHERE e.id=chronicle_jobs.event_id);
            DELETE FROM chronicle_owners WHERE NOT EXISTS(SELECT 1 FROM chronicle_events e WHERE e.id=chronicle_owners.event_id);
            """);
    }

    internal string? Ingest(ChronicleOutboxEnvelope envelope)
    {
        Validate(envelope);
        var input = envelope.Event with
        {
            VisualMotifs = envelope.Event.VisualMotifs ?? envelope.AwardsByOwner.Values.SelectMany(awards => awards)
                .Select(award => award.Title + ": " + award.Evidence).Distinct(StringComparer.Ordinal).Take(32).ToArray()
        };
        var id = StableId(input.SourceKind, input.SourceId);
        var body = JsonSerializer.Serialize(input, Json);
        if (Encoding.UTF8.GetByteCount(body) > 12 * 1024 * 1024)
        {
            throw new InvalidDataException("Chronicle event exceeds its storage limit.");
        }
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body)));
        lock (_gate)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction(deferred: false);
            if (HasDeletedProfile(connection, transaction, input))
            {
                transaction.Commit();
                return null;
            }
            foreach (var participant in input.Participants.Select(value => value.AccountId).Distinct(StringComparer.Ordinal))
            {
                if (ScalarString(connection, transaction, "SELECT owner_id FROM chronicle_deleted_accounts WHERE owner_id=$owner",
                    ("$owner", participant)) is not null)
                {
                    transaction.Commit();
                    return null;
                }
            }
            var initialIllustrationState = !input.IllustrationRequested ? "not_requested"
                : HasQueueCapacity(connection, transaction) ? "queued" : "deferred";
            using (var insert = Command(connection, transaction, """
                INSERT INTO chronicle_events(id,source_kind,source_id,group_id,occurred_at,event_json,event_hash,illustration_state)
                VALUES($id,$kind,$source,$group,$at,$json,$hash,$state) ON CONFLICT(source_kind,source_id) DO NOTHING
                """, ("$id", id), ("$kind", input.SourceKind), ("$source", input.SourceId),
                ("$group", input.GroupId), ("$at", input.OccurredAt), ("$json", body), ("$hash", hash),
                ("$state", initialIllustrationState)))
            {
                insert.ExecuteNonQuery();
            }
            using (var verify = Command(connection, transaction,
                "SELECT id,event_hash FROM chronicle_events WHERE source_kind=$kind AND source_id=$source",
                ("$kind", input.SourceKind), ("$source", input.SourceId)))
            using (var reader = verify.ExecuteReader())
            {
                if (!reader.Read() || reader.GetString(1) != hash)
                {
                    throw new InvalidDataException("Chronicle source was replayed with different event facts.");
                }
                id = reader.GetString(0);
            }
            foreach (var owner in envelope.OwnerIds.Distinct(StringComparer.Ordinal))
            {
                Execute(connection, transaction,
                    "INSERT INTO chronicle_owners(event_id,owner_id) VALUES($event,$owner) ON CONFLICT DO NOTHING",
                    ("$event", id), ("$owner", owner));
                if (!envelope.AwardsByOwner.TryGetValue(owner, out var awards)) { continue; }
                foreach (var award in awards)
                {
                    Execute(connection, transaction, """
                        INSERT INTO chronicle_awards(event_id,owner_id,award_id,title,scope,evidence,acquired_at)
                        VALUES($event,$owner,$id,$title,$scope,$evidence,$at)
                        ON CONFLICT(owner_id,award_id,scope) DO UPDATE SET
                            event_id=excluded.event_id,title=excluded.title,evidence=excluded.evidence,acquired_at=excluded.acquired_at
                        WHERE excluded.acquired_at<chronicle_awards.acquired_at
                        """, ("$event", id), ("$owner", owner), ("$id", award.Id),
                        ("$title", award.Title), ("$scope", award.Scope), ("$evidence", award.Evidence), ("$at", input.OccurredAt));
                }
            }
            if (input.IllustrationRequested)
            {
                var jobId = StableId("illustration", input.SourceKind + ":" + input.SourceId);
                Execute(connection, transaction, """
                    INSERT INTO chronicle_jobs(id,event_id,state,available_at)
                    VALUES($id,$event,$state,$now) ON CONFLICT(event_id) DO NOTHING
                    """, ("$id", jobId), ("$event", id), ("$state", initialIllustrationState), ("$now", NowSeconds));
            }
            transaction.Commit();
        }
        return id;
    }

    internal ChroniclePage List(string ownerId, string? kind = null, string? participant = null,
        string? badge = null, string? before = null, int limit = 30)
    {
        limit = Math.Clamp(limit, 1, 50);
        var cursor = DecodeCursor(before);
        using var connection = Open();
        var ids = new List<(string Id, long At)>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT e.id,e.occurred_at FROM chronicle_events e
                JOIN chronicle_owners o ON o.event_id=e.id
                WHERE o.owner_id=$owner
                    AND ($kind IS NULL OR e.source_kind=$kind)
                    AND ($before IS NULL OR e.occurred_at<$before OR (e.occurred_at=$before AND e.id<$beforeId))
                    AND ($badge IS NULL OR (o.revealed_at IS NOT NULL AND EXISTS(SELECT 1 FROM chronicle_awards a WHERE a.event_id=e.id AND a.owner_id=$owner AND a.award_id=$badge)))
                    AND ($participant IS NULL OR (o.revealed_at IS NOT NULL AND EXISTS(SELECT 1 FROM json_each(e.event_json,'$.participants') p WHERE json_extract(p.value,'$.accountId')=$participant)))
                ORDER BY e.occurred_at DESC,e.id DESC LIMIT $limit
                """;
            command.Parameters.AddWithValue("$owner", ownerId);
            command.Parameters.AddWithValue("$kind", (object?)kind ?? DBNull.Value);
            command.Parameters.AddWithValue("$participant", (object?)participant ?? DBNull.Value);
            command.Parameters.AddWithValue("$badge", (object?)badge ?? DBNull.Value);
            command.Parameters.AddWithValue("$before", (object?)cursor?.At ?? DBNull.Value);
            command.Parameters.AddWithValue("$beforeId", (object?)cursor?.Id ?? DBNull.Value);
            command.Parameters.AddWithValue("$limit", limit + 1);
            using var reader = command.ExecuteReader();
            while (reader.Read()) { ids.Add((reader.GetString(0), reader.GetInt64(1))); }
        }
        var hasMore = ids.Count > limit;
        if (hasMore) { ids.RemoveAt(ids.Count - 1); }
        var items = ids.Select(row => ReadView(connection, ownerId, row.Id, detail: false)!).ToArray();
        var awards = ReadBadges(ownerId);
        return new(items, hasMore && ids.Count > 0 ? EncodeCursor(ids[^1].At, ids[^1].Id) : null, awards);
    }

    internal ChronicleEventView? Read(string ownerId, string id)
    {
        using var connection = Open();
        return ReadView(connection, ownerId, id, detail: true);
    }

    internal ChronicleEventView? FindSource(string ownerId, string kind, string sourceId)
    {
        using var connection = Open();
        var id = ScalarString(connection, null, """
            SELECT e.id FROM chronicle_events e JOIN chronicle_owners o ON o.event_id=e.id
            WHERE o.owner_id=$owner AND e.source_kind=$kind AND e.source_id=$source
            """, ("$owner", ownerId), ("$kind", kind), ("$source", sourceId));
        return id is null ? null : ReadView(connection, ownerId, id, detail: true);
    }

    internal bool Reveal(string ownerId, string id)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE chronicle_owners SET revealed_at=COALESCE(revealed_at,$now) WHERE event_id=$id AND owner_id=$owner";
        command.Parameters.AddWithValue("$now", NowSeconds);
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$owner", ownerId);
        return command.ExecuteNonQuery() == 1;
    }

    internal bool RequestIllustration(string ownerId, string id)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        string? eventJson = null;
        string? currentState = null;
        using (var query = Command(connection, transaction, """
            SELECT e.event_json,e.illustration_state FROM chronicle_events e JOIN chronicle_owners o ON o.event_id=e.id
            WHERE e.id=$id AND o.owner_id=$owner AND o.revealed_at IS NOT NULL
            """, ("$id", id), ("$owner", ownerId)))
        using (var reader = query.ExecuteReader())
        {
            if (reader.Read()) { eventJson = reader.GetString(0); currentState = reader.GetString(1); }
        }
        if (eventJson is null) { return false; }
        if (currentState is "ready" or "queued" or "generating") { transaction.Commit(); return true; }
        var nextState = HasQueueCapacity(connection, transaction) ? "queued" : "deferred";
        Execute(connection, transaction, """
            INSERT INTO chronicle_jobs(id,event_id,state,available_at)
            VALUES($job,$event,$state,$now) ON CONFLICT(event_id) DO NOTHING
            """, ("$job", StableId("illustration", id)), ("$event", id), ("$state", nextState), ("$now", NowSeconds));
        Execute(connection, transaction,
            "UPDATE chronicle_events SET illustration_state=$state,revision=revision+1 WHERE id=$id AND illustration_state IN ('not_requested','failed','awaiting_reconciliation','deferred')",
            ("$id", id), ("$state", nextState));
        Execute(connection, transaction, """
            UPDATE chronicle_jobs SET state=$state,attempts=0,fence=fence+1,available_at=$now,lease_until=0,error_code=NULL
            WHERE event_id=$id AND state IN ('awaiting_reconciliation','failed','deferred')
            """, ("$state", nextState), ("$now", NowSeconds), ("$id", id));
        transaction.Commit();
        return true;
    }

    internal ChronicleImage? ReadImage(string ownerId, string id)
    {
        using var connection = Open();
        using var command = Command(connection, null, """
            SELECT e.media_type,e.image FROM chronicle_events e JOIN chronicle_owners o ON o.event_id=e.id
            WHERE e.id=$id AND o.owner_id=$owner AND o.revealed_at IS NOT NULL AND e.image IS NOT NULL
            """, ("$id", id), ("$owner", ownerId));
        using var reader = command.ExecuteReader();
        return reader.Read() ? new(reader.GetString(0), (byte[])reader[1]) : null;
    }

    internal ChronicleShareProjection? ReadShareProjection(string ownerId, string id)
    {
        var view = Read(ownerId, id);
        if (view is null || !view.Revealed) { return null; }
        if (view.SourceKind is "case" or "conversation")
        {
            var visibility = ReadVisibility(ownerId, id);
            if (visibility != "public") { return null; }
        }
        return new(view.Title, view.Participants, view.Result, view.ShareText, ReadImage(ownerId, id));
    }

    internal ChronicleImage? ReadShareImage(string ownerId, string id) => ReadShareProjection(ownerId, id)?.Image;

    internal ChronicleImage? ReadAwardImage(string ownerId, string id, string awardId)
    {
        using var connection = Open();
        using var command = Command(connection, null, """
            SELECT e.media_type,e.image FROM chronicle_events e
            JOIN chronicle_owners o ON o.event_id=e.id
            JOIN chronicle_awards a ON a.event_id=e.id AND a.owner_id=o.owner_id
            WHERE e.id=$id AND o.owner_id=$owner AND o.revealed_at IS NOT NULL
                AND a.award_id=$award AND e.image IS NOT NULL
            """, ("$id", id), ("$owner", ownerId), ("$award", awardId));
        using var reader = command.ExecuteReader();
        return reader.Read() ? new(reader.GetString(0), (byte[])reader[1]) : null;
    }

    internal IReadOnlyList<ChronicleAward> ReadBadges(string ownerId)
    {
        using var connection = Open();
        using var command = Command(connection, null, """
            SELECT DISTINCT a.award_id,a.title,a.scope,a.evidence,a.event_id,a.acquired_at FROM chronicle_awards a
            JOIN chronicle_owners o ON o.event_id=a.event_id AND o.owner_id=a.owner_id
            JOIN chronicle_events e ON e.id=a.event_id
            WHERE a.owner_id=$owner AND o.revealed_at IS NOT NULL ORDER BY a.acquired_at,a.title
            """, ("$owner", ownerId));
        using var reader = command.ExecuteReader();
        var rows = new List<ChronicleAward>();
        while (reader.Read()) { rows.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetInt64(5))); }
        return rows;
    }

    internal IReadOnlyList<ChronicleEventInput> ReadRecordedEvents(string groupId, string ownerId,
        bool revealedOnly = true)
    {
        using var connection = Open();
        using var command = Command(connection, null, """
            SELECT e.event_json FROM chronicle_events e JOIN chronicle_owners o ON o.event_id=e.id
            WHERE e.group_id=$group AND o.owner_id=$owner AND ($revealed=0 OR o.revealed_at IS NOT NULL)
                AND e.source_kind IN ('yolo','duel','team','boss','case','wand','evening','conversation')
            ORDER BY e.occurred_at,e.id
            """, ("$group", groupId), ("$owner", ownerId), ("$revealed", revealedOnly ? 1 : 0));
        using var reader = command.ExecuteReader();
        var events = new List<ChronicleEventInput>();
        while (reader.Read())
        {
            if (JsonSerializer.Deserialize<ChronicleEventInput>(reader.GetString(0), Json) is { } item)
            {
                events.Add(item);
            }
        }
        return events;
    }

    internal ChronicleJobLease? LeaseNext()
    {
        lock (_gate)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction(deferred: false);
            var expired = new List<string>();
            using (var expiredQuery = Command(connection, transaction,
                "SELECT event_id FROM chronicle_jobs WHERE state='dispatched' AND lease_until<=$now",
                ("$now", NowSeconds)))
            using (var expiredReader = expiredQuery.ExecuteReader())
            {
                while (expiredReader.Read()) { expired.Add(expiredReader.GetString(0)); }
            }
            Execute(connection, transaction, "UPDATE chronicle_jobs SET state='awaiting_reconciliation' WHERE state='dispatched' AND lease_until<=$now",
                ("$now", NowSeconds));
            foreach (var expiredEventId in expired)
            {
                Execute(connection, transaction, "UPDATE chronicle_events SET illustration_state='awaiting_reconciliation',revision=revision+1 WHERE id=$id",
                    ("$id", expiredEventId));
            }
            PromoteDeferredIllustrations(connection, transaction);
            using var select = Command(connection, transaction, """
                SELECT j.id,j.event_id,j.attempts,j.fence,e.event_json FROM chronicle_jobs j
                JOIN chronicle_events e ON e.id=j.event_id
                WHERE j.attempts<$max AND ((j.state='queued' AND j.available_at<=$now) OR (j.state='leased' AND j.lease_until<=$now))
                ORDER BY j.available_at,j.id LIMIT 1
                """, ("$max", MaxAttempts), ("$now", NowSeconds));
            using var reader = select.ExecuteReader();
            if (!reader.Read()) { return null; }
            var id = reader.GetString(0);
            var eventId = reader.GetString(1);
            var attempts = reader.GetInt32(2) + 1;
            var fence = reader.GetInt32(3) + 1;
            var input = JsonSerializer.Deserialize<ChronicleEventInput>(reader.GetString(4), Json)!;
            reader.Close();
            Execute(connection, transaction, "UPDATE chronicle_jobs SET state='leased',attempts=$attempts,fence=$fence,lease_until=$until WHERE id=$id",
                ("$attempts", attempts), ("$fence", fence), ("$until", NowSeconds + (long)Lease.TotalSeconds), ("$id", id));
            Execute(connection, transaction, "UPDATE chronicle_events SET illustration_state='generating',revision=revision+1 WHERE id=$id", ("$id", eventId));
            transaction.Commit();
            return new(id, input, fence, attempts);
        }
    }

    internal bool MarkDispatched(ChronicleJobLease lease)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        var updated = Execute(connection, transaction, """
            UPDATE chronicle_jobs SET state='dispatched',lease_until=$until
            WHERE id=$id AND fence=$fence AND state='leased'
            """, ("$until", NowSeconds + (long)Lease.TotalSeconds), ("$id", lease.Id), ("$fence", lease.Fence));
        transaction.Commit();
        return updated == 1;
    }

    internal bool Complete(ChronicleJobLease lease, ChronicleIllustration image)
    {
        if (image.Bytes.Length is < 1 or > 8_000_000 || image.MediaType is not ("image/png" or "image/jpeg" or "image/webp"))
        {
            throw new InvalidDataException("Chronicle illustration media is unsupported.");
        }
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        var updated = Execute(connection, transaction, """
            UPDATE chronicle_events SET image=$image,media_type=$type,image_synthetic=$synthetic,illustration_state='ready',revision=revision+1
            WHERE id=(SELECT event_id FROM chronicle_jobs WHERE id=$job AND fence=$fence AND state='dispatched')
            """, ("$image", image.Bytes), ("$type", image.MediaType), ("$synthetic", image.Synthetic ? 1 : 0), ("$job", lease.Id), ("$fence", lease.Fence));
        if (updated == 1)
        {
            Execute(connection, transaction, "UPDATE chronicle_jobs SET state='complete',lease_until=0,error_code=NULL WHERE id=$id AND fence=$fence",
                ("$id", lease.Id), ("$fence", lease.Fence));
        }
        transaction.Commit();
        return updated == 1;
    }

    internal void Fail(ChronicleJobLease lease, string errorCode, bool dispatchUncertain = false)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        var terminal = lease.Attempts >= MaxAttempts;
        var retrySeconds = Math.Min(900, 5 * (1 << Math.Min(lease.Attempts, 7)));
        Execute(connection, transaction, """
            UPDATE chronicle_jobs SET state=$state,available_at=$retry,lease_until=0,error_code=$error
            WHERE id=$id AND fence=$fence AND state IN ('leased','dispatched')
            """, ("$state", dispatchUncertain ? "awaiting_reconciliation" : terminal ? "failed" : "queued"),
            ("$retry", terminal || dispatchUncertain ? 0 : NowSeconds + retrySeconds),
            ("$error", SafeError(errorCode)), ("$id", lease.Id), ("$fence", lease.Fence));
        Execute(connection, transaction, """
            UPDATE chronicle_events SET illustration_state=$state,revision=revision+1
            WHERE id=(SELECT event_id FROM chronicle_jobs WHERE id=$id AND fence=$fence)
            """, ("$state", dispatchUncertain ? "awaiting_reconciliation" : terminal ? "failed" : "queued"),
            ("$id", lease.Id), ("$fence", lease.Fence));
        transaction.Commit();
    }

    internal void DeleteAccount(string ownerId)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        Execute(connection, transaction, "INSERT INTO chronicle_deleted_accounts(owner_id,deleted_at) VALUES($owner,$now) ON CONFLICT DO NOTHING",
            ("$owner", ownerId), ("$now", NowSeconds));
        Execute(connection, transaction, """
            DELETE FROM chronicle_events WHERE id IN (
                SELECT e.id FROM chronicle_events e,json_each(e.event_json,'$.participants') p
                WHERE json_extract(p.value,'$.accountId')=$owner)
            """, ("$owner", ownerId));
        Execute(connection, transaction, "DELETE FROM chronicle_owners WHERE owner_id=$owner", ("$owner", ownerId));
        Execute(connection, transaction, "DELETE FROM chronicle_events WHERE NOT EXISTS(SELECT 1 FROM chronicle_owners o WHERE o.event_id=chronicle_events.id)");
        transaction.Commit();
    }

    internal string ExportAccount(string ownerId, IReadOnlyCollection<string>? admittedIds = null)
    {
        using var connection = Open();
        using var command = Command(connection, null, """
            SELECT e.id FROM chronicle_events e JOIN chronicle_owners o ON o.event_id=e.id
            WHERE o.owner_id=$owner AND o.revealed_at IS NOT NULL ORDER BY e.occurred_at,e.id
            """, ("$owner", ownerId));
        using var reader = command.ExecuteReader();
        var ids = new List<string>();
        while (reader.Read()) { ids.Add(reader.GetString(0)); }
        reader.Close();
        var events = ids.Where(id => admittedIds is null || admittedIds.Contains(id))
            .Select(id => ReadView(connection, ownerId, id, detail: true))
            .Where(item => item is not null).ToArray();
        return JsonSerializer.Serialize(events, Json);
    }

    private ChronicleEventView? ReadView(SqliteConnection connection, string ownerId, string id, bool detail)
    {
        using var command = Command(connection, null, """
            SELECT e.event_json,e.illustration_state,e.revision,o.revealed_at,e.image_synthetic
            FROM chronicle_events e JOIN chronicle_owners o ON o.event_id=e.id
            WHERE e.id=$id AND o.owner_id=$owner
            """, ("$id", id), ("$owner", ownerId));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) { return null; }
        var input = JsonSerializer.Deserialize<ChronicleEventInput>(reader.GetString(0), Json)!;
        var state = reader.GetString(1);
        var revision = reader.GetInt32(2);
        var revealed = !reader.IsDBNull(3);
        var synthetic = reader.GetInt32(4) != 0;
        reader.Close();
        using var awardsCommand = Command(connection, null, """
            SELECT award_id,title,scope,evidence,event_id,acquired_at FROM chronicle_awards
            WHERE event_id=$event AND owner_id=$owner ORDER BY title
            """, ("$event", id), ("$owner", ownerId));
        using var awardsReader = awardsCommand.ExecuteReader();
        var awards = new List<ChronicleAward>();
        while (awardsReader.Read()) { awards.Add(new(awardsReader.GetString(0), awardsReader.GetString(1), awardsReader.GetString(2), awardsReader.GetString(3), awardsReader.GetString(4), awardsReader.GetInt64(5))); }
        if (!revealed)
        {
            return new(id, input.SourceKind, input.SourceId, input.GroupId, input.OccurredAt,
                HiddenTitle(input.SourceKind), "", [], null, null, [], [], [],
                state, false, null, revision, null, synthetic, false);
        }
        var participants = input.Participants.Select(p => new ChronicleParticipantView(p.AccountId,
            p.DisplayName, p.Role, p.CharacterName, p.WandName, p.VisualReferenceVersion)).ToArray();
        return new(id, input.SourceKind, input.SourceId, input.GroupId, input.OccurredAt,
            input.Title, input.Summary, participants, input.Result, input.WinnerId,
            detail ? input.Story ?? [] : [], detail ? input.Decisions ?? [] : [],
            awards.Select(a => a with { EventId = id, AcquiredAt = input.OccurredAt }).ToArray(),
            state, true, input.ShareText, revision, detail ? input.DecisiveChoice : null, synthetic,
            input.SourceKind is not ("case" or "conversation") || input.Visibility == "public");
    }

    private static string HiddenTitle(string sourceKind) => sourceKind switch
    {
        "yolo" => "Památka ze zápasu",
        "duel" => "Památka ze souboje",
        "team" => "Památka z týmového zápasu",
        "boss" => "Památka z výzvy",
        "case" => "Památka z případu",
        "wand" => "Památka z dílny",
        "evening" => "Památka z večera",
        "conversation" => "Památka z rozhovoru",
        _ => "Nová památka"
    };

    private string? ReadVisibility(string ownerId, string id)
    {
        using var connection = Open();
        return ScalarString(connection, null, """
            SELECT e.event_json FROM chronicle_events e JOIN chronicle_owners o ON o.event_id=e.id
            WHERE e.id=$id AND o.owner_id=$owner AND o.revealed_at IS NOT NULL
            """, ("$id", id), ("$owner", ownerId)) is { } json
            ? JsonDocument.Parse(json).RootElement.TryGetProperty("visibility", out var visibility) ? visibility.GetString() : null
            : null;
    }

    private static void Validate(ChronicleOutboxEnvelope envelope)
    {
        var input = envelope.Event;
        if (input.SourceKind is not ("yolo" or "duel" or "team" or "boss" or "case" or "wand" or "evening" or "conversation")
            || string.IsNullOrWhiteSpace(input.SourceId) || input.SourceId.Length > 100
            || string.IsNullOrWhiteSpace(input.GroupId) || input.GroupId.Length > 100
            || string.IsNullOrWhiteSpace(input.Title) || input.Title.Length > 160
            || string.IsNullOrWhiteSpace(input.Summary) || input.Summary.Length > 1000
            || input.Participants.Count is < 1 or > 32 || envelope.OwnerIds.Count is < 1 or > 32
            || input.Participants.Any(p => string.IsNullOrWhiteSpace(p.AccountId) || p.AccountId.Length > 100
                || string.IsNullOrWhiteSpace(p.DisplayName) || p.DisplayName.Length > 120
                || p.VisualSnapshotJson?.Length > 1_000_000)
            || input.Story?.Count > 256 || input.Story?.Any(text => text.Length > 4000) == true
            || input.Decisions?.Count > 256 || input.Decisions?.Any(decision => decision.Choice.Length > 160
                || decision.Consequence.Length > 4000 || decision.Evidence?.Length > 500) == true
            || input.ShareText?.Length > 500 || input.DecisiveChoice?.Length > 300)
        {
            throw new InvalidDataException("Chronicle event is invalid.");
        }
        if (envelope.OwnerIds.Any(id => !input.Participants.Any(p => p.AccountId == id)))
        {
            throw new InvalidDataException("Chronicle owner is not a participant.");
        }
    }

    private static bool HasQueueCapacity(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = Command(connection, transaction,
            "SELECT COUNT(*) FROM chronicle_jobs WHERE state IN ('queued','leased','dispatched','awaiting_reconciliation')");
        return Convert.ToInt32(command.ExecuteScalar()) < MaxPendingIllustrations;
    }

    private static void PromoteDeferredIllustrations(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var count = Command(connection, transaction,
            "SELECT COUNT(*) FROM chronicle_jobs WHERE state IN ('queued','leased','dispatched','awaiting_reconciliation')");
        var available = MaxPendingIllustrations - Convert.ToInt32(count.ExecuteScalar());
        if (available <= 0) { return; }
        var rows = new List<(string JobId, string EventId)>();
        using (var select = Command(connection, transaction,
            "SELECT id,event_id FROM chronicle_jobs WHERE state='deferred' ORDER BY available_at,id LIMIT $limit",
            ("$limit", available)))
        using (var reader = select.ExecuteReader())
        {
            while (reader.Read()) { rows.Add((reader.GetString(0), reader.GetString(1))); }
        }
        foreach (var row in rows)
        {
            Execute(connection, transaction, "UPDATE chronicle_jobs SET state='queued',available_at=$now WHERE id=$id AND state='deferred'",
                ("$now", NowSeconds), ("$id", row.JobId));
            Execute(connection, transaction, "UPDATE chronicle_events SET illustration_state='queued',revision=revision+1 WHERE id=$id AND illustration_state='deferred'",
                ("$id", row.EventId));
        }
    }

    private static (long At, string Id)? DecodeCursor(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor)) { return null; }
        try
        {
            var base64 = cursor.Replace('-', '+').Replace('_', '/');
            base64 = base64.PadRight(base64.Length + ((4 - base64.Length % 4) % 4), '=');
            var parts = Encoding.UTF8.GetString(Convert.FromBase64String(base64)).Split('|', 2);
            if (parts.Length == 2 && long.TryParse(parts[0], out var at) && parts[1].Length <= 80) { return (at, parts[1]); }
        }
        catch (FormatException) { }
        throw new ArgumentException("chronicle_cursor_invalid");
    }

    private static string EncodeCursor(long at, string id) => Convert.ToBase64String(Encoding.UTF8.GetBytes($"{at}|{id}"))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static string StableId(string kind, string source)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(kind + ":" + source));
        return new Guid(hash.AsSpan(0, 16)).ToString("D");
    }
    private static string SafeError(string code) => string.IsNullOrWhiteSpace(code) ? "illustration_failed" : code[..Math.Min(code.Length, 80)];
    private static long NowSeconds => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    private SqliteConnection Open() { var connection = new SqliteConnection(_connectionString); connection.Open(); return connection; }
    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? transaction, string sql, params (string Name, object Value)[] values)
    {
        var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql;
        foreach (var (name, value) in values) { command.Parameters.AddWithValue(name, value); }
        return command;
    }
    private static int Execute(SqliteConnection connection, SqliteTransaction? transaction, string sql, params (string Name, object Value)[] values)
    {
        using var command = Command(connection, transaction, sql, values); return command.ExecuteNonQuery();
    }
    private static string? ScalarString(SqliteConnection connection, SqliteTransaction? transaction, string sql, params (string Name, object Value)[] values)
    {
        using var command = Command(connection, transaction, sql, values); return command.ExecuteScalar() as string;
    }
}
