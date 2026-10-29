using System.Text.Json;
using Kufr.Backend.Features.SealedCase;
using Microsoft.Data.Sqlite;

namespace Kufr.Backend.Features.Chronicle;

internal sealed partial class ChronicleStore
{
    internal void DeleteProfile(string ownerId, string profileId)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        Execute(connection, transaction, """
            INSERT INTO chronicle_deleted_profiles(owner_id,profile_id,deleted_at) VALUES($owner,$profile,$now)
            ON CONFLICT(owner_id,profile_id) DO NOTHING
            """, ("$owner", ownerId), ("$profile", profileId), ("$now", NowSeconds));
        var affected = new List<string>();
        using (var query = Command(connection, transaction, """
            SELECT e.id,e.event_json FROM chronicle_events e WHERE EXISTS(
                SELECT 1 FROM json_each(e.event_json,'$.participants') participant
                WHERE json_extract(participant.value,'$.accountId')=$owner)
            """, ("$owner", ownerId)))
        using (var reader = query.ExecuteReader())
        {
            while (reader.Read())
            {
                var snapshot = JsonSerializer.Deserialize<ChronicleEventInput>(reader.GetString(1), Json)!;
                if (HasDeletedProfile(connection, transaction, snapshot)) { affected.Add(reader.GetString(0)); }
            }
        }
        foreach (var id in affected)
        {
            Execute(connection, transaction, "DELETE FROM chronicle_events WHERE id=$id", ("$id", id));
        }
        transaction.Commit();
    }

    private static bool HasDeletedProfile(SqliteConnection connection, SqliteTransaction transaction, ChronicleEventInput snapshot)
    {
        foreach (var person in snapshot.Participants)
        {
            using var query = Command(connection, transaction,
                "SELECT profile_id,deleted_at FROM chronicle_deleted_profiles WHERE owner_id=$owner", ("$owner", person.AccountId));
            using var reader = query.ExecuteReader();
            while (reader.Read())
            {
                var profile = ProfileId(person);
                if (profile == reader.GetString(0) || profile is null && snapshot.OccurredAt <= reader.GetInt64(1)) { return true; }
            }
        }
        return false;
    }

    internal static string? ProfileId(ChronicleParticipant person)
    {
        const string marker = "character:";
        var start = person.VisualReferenceVersion.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0) { return null; }
        start += marker.Length;
        var end = person.VisualReferenceVersion.IndexOf(':', start);
        return end > start ? person.VisualReferenceVersion[start..end] : null;
    }

    // The caller validates every subject under identity/character reservations
    // immediately before serializing a response or admitting a provider dispatch.
    internal SealedCaseSubject[]? SecuritySubjects(string ownerId, IReadOnlyList<string> eventIds)
    {
        using var connection = Open();
        var subjects = new List<SealedCaseSubject> { new(ownerId, null) };
        foreach (var id in eventIds.Distinct(StringComparer.Ordinal))
        {
            var body = ScalarString(connection, null, """
                SELECT e.event_json FROM chronicle_events e JOIN chronicle_owners o ON o.event_id=e.id
                WHERE e.id=$id AND o.owner_id=$owner
                """, ("$id", id), ("$owner", ownerId));
            if (body is null) { return null; }
            var snapshot = JsonSerializer.Deserialize<ChronicleEventInput>(body, Json)!;
            subjects.AddRange(snapshot.Participants.Where(person => !person.AccountId.StartsWith("baron-", StringComparison.Ordinal))
                .Select(person => new SealedCaseSubject(person.AccountId, ProfileId(person))));
        }
        return subjects.Distinct().ToArray();
    }

    internal string[] OwnedEventIds(string ownerId)
    {
        using var connection = Open();
        using var query = Command(connection, null, "SELECT event_id FROM chronicle_owners WHERE owner_id=$owner", ("$owner", ownerId));
        using var reader = query.ExecuteReader();
        var ids = new List<string>();
        while (reader.Read()) { ids.Add(reader.GetString(0)); }
        return ids.ToArray();
    }
}
