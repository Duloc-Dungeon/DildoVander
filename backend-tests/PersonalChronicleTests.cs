using Kufr.Backend.Features.Chronicle;

internal static class PersonalChronicleTests
{
    internal static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "chronicle-store-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            PersistenceAndReveal(root);
            DeletionFence(root);
            ProfileErasure(root);
            DispatchReconciliation(root);
            Console.WriteLine("Chronicle persistence tests passed (local synthetic fixtures; no provider calls).");
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    private static void PersistenceAndReveal(string root)
    {
        var store = new ChronicleStore(Path.Combine(root, "reveal"));
        var envelope = Envelope("match-1", illustration: true);
        var id = store.Ingest(envelope) ?? throw new InvalidOperationException("Expected ingestion.");
        Check(id.Length == 36, "chronicle entry ids are canonical UUIDs");
        Check(store.Ingest(envelope) == id, "duplicate source events preserve one idempotent entry");
        var altered = envelope with { Event = envelope.Event with { Summary = "Changed facts" } };
        Expect<InvalidDataException>(() => store.Ingest(altered), "same source cannot be replayed with altered facts");

        var locked = store.FindSource("alice", "yolo", "match-1")
            ?? throw new InvalidOperationException("Source owner could not find their locked event.");
        Check(!locked.Revealed && locked.Result is null && locked.Participants.Count == 0
            && locked.Awards.Count == 0 && locked.ShareText is null,
            "locked post-game lookup does not reveal names, result, awards or story");
        Check(store.FindSource("mallory", "yolo", "match-1") is null,
            "a nonparticipant cannot resolve another account's event");
        var page = store.List("alice");
        Check(page.Items.Count == 1 && !page.Items[0].Revealed && page.Badges.Count == 0,
            "timeline contains only a generic placeholder and hides unrevealed badges");
        Check(store.Reveal("alice", id), "owner can reveal their own event");
        var revealed = store.Read("alice", id)!;
        Check(revealed.Revealed && revealed.Result == "alice" && revealed.Participants.Count == 2
            && revealed.Participants.All(participant => participant.AccountId.Length > 0)
            && revealed.Decisions.Count == 1 && revealed.Awards.Count == 1,
            "revealed detail includes saved facts and the event-linked award");
        Check(revealed.Awards[0].EventId == id && revealed.Awards[0].AcquiredAt == envelope.Event.OccurredAt,
            "award provenance retains its source event and original timestamp");
        Check(store.Read("bob", id) is { Revealed: false }, "each participant reveals their own copy independently");
        Check(store.ReadImage("alice", id) is null, "image is not readable before generation completes");
    }

    private static void DeletionFence(string root)
    {
        var store = new ChronicleStore(Path.Combine(root, "deletion"));
        var envelope = Envelope("shared-match", illustration: true);
        var id = store.Ingest(envelope)!;
        store.Reveal("bob", id);
        store.DeleteAccount("alice");
        Check(store.Read("bob", id) is null, "deleting a participant erases the shared event for every owner");
        Check(store.Ingest(envelope) is null, "a stale game outbox cannot recreate an erased participant's scene");
        Check(DependentRows(Path.Combine(root, "deletion"), id) == 0, "foreign-key cascades physically erase all owners, award evidence and jobs");
    }

    private static void ProfileErasure(string root)
    {
        var directory = Path.Combine(root, "profiles");
        var store = new ChronicleStore(directory);
        var envelope = Envelope("old-character", illustration: true);
        envelope = envelope with { Event = envelope.Event with
        {
            Participants = envelope.Event.Participants.Select(person => person.AccountId == "alice"
                ? person with { VisualReferenceVersion = "character:old-profile:1" } : person).ToArray()
        } };
        var oldId = store.Ingest(envelope)!;
        var lease = store.LeaseNext()!;
        store.MarkDispatched(lease);
        var next = envelope with { Event = envelope.Event with { SourceId = "new-character", Participants = envelope.Event.Participants
            .Select(person => person.AccountId == "alice" ? person with { VisualReferenceVersion = "character:new-profile:1" } : person).ToArray() } };
        var newId = store.Ingest(next)!;
        store.DeleteProfile("alice", "old-profile");
        Check(store.Read("bob", oldId) is null && store.Read("alice", newId) is not null,
            "profile erasure removes the old shared scene without fencing a different character on the account");
        Check(store.Ingest(envelope) is null && store.Ingest(next) == newId, "late profile outbox delivery respects the exact profile fence");
        Check(DependentRows(directory, oldId) == 0, "profile erasure also physically removes in-flight work and evidence");
        Check(!store.Complete(lease, new([137, 80, 78, 71], "image/png")), "a late provider result cannot restore an erased profile");
    }

    private static long DependentRows(string root, string eventId)
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
        { DataSource = Path.Combine(root, "chronicle.sqlite") }.ToString());
        connection.Open();
        using var query = connection.CreateCommand();
        query.CommandText = """
            SELECT (SELECT COUNT(*) FROM chronicle_awards WHERE event_id=$id)
                +(SELECT COUNT(*) FROM chronicle_jobs WHERE event_id=$id)
                +(SELECT COUNT(*) FROM chronicle_owners WHERE event_id=$id)
            """;
        query.Parameters.AddWithValue("$id", eventId);
        return (long)query.ExecuteScalar()!;
    }

    private static void DispatchReconciliation(string root)
    {
        var store = new ChronicleStore(Path.Combine(root, "dispatch"));
        var id = store.Ingest(Envelope("unknown-result", illustration: true))!;
        store.Reveal("alice", id);
        var lease = store.LeaseNext() ?? throw new InvalidOperationException("Queued illustration was not leased.");
        Check(store.MarkDispatched(lease), "worker persists provider dispatch before sending a request");
        store.Fail(lease, "provider_result_unknown", dispatchUncertain: true);
        Check(store.LeaseNext() is null, "uncertain provider result is never automatically retried");
        Check(store.Read("alice", id)?.IllustrationState == "awaiting_reconciliation",
            "uncertain image result keeps the event and reports reconciliation state");
        Check(store.RequestIllustration("alice", id), "an owner can explicitly retry their revealed illustration");
        var retry = store.LeaseNext() ?? throw new InvalidOperationException("Explicit illustration retry was not queued.");
        Check(retry.Fence > lease.Fence, "explicit retry fences off a late provider response");
        Check(store.MarkDispatched(retry), "retry dispatch is fenced by its current lease");
        Check(store.Complete(retry, new([1, 2, 3], "image/png", Synthetic: true)),
            "current dispatched job can store a generated illustration");
        Check(store.Read("alice", id) is { IllustrationState: "ready", IllustrationSynthetic: true }
            && store.ReadImage("alice", id) is { MediaType: "image/png", Bytes.Length: 3 },
            "image bytes and synthetic marker persist only after successful completion");
    }

    private static ChronicleOutboxEnvelope Envelope(string sourceId, bool illustration)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var input = new ChronicleEventInput("yolo", sourceId, "group-1", now, "YOLO zápas", "Výsledek", [
            new ChronicleParticipant("alice", "Alice", "challenger", "Čarodějka", "Ořech"),
            new ChronicleParticipant("bob", "Bob", "opponent", "Kouzelník", "Jasan")
        ], "alice", "alice", ["První dějství"],
            [new ChronicleDecision("alice", "Ochranit", "Plán uspěl")], "Plán byl dokončen.", illustration);
        var awards = new Dictionary<string, IReadOnlyList<ChronicleAward>>
        {
            ["alice"] = [new("first-yolo", "Oběť odborníka", "group:group-1", "První YOLO.")]
        };
        return new(input, ["alice", "bob"], awards);
    }

    private static void Check(bool condition, string description)
    {
        if (!condition) { throw new InvalidOperationException(description); }
    }

    private static void Expect<T>(Action action, string description) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException(description);
    }
}
