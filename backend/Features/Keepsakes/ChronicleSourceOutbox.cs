using System.Text.Json;
using Kufr.Backend.Features.Chronicle;
using Microsoft.Data.Sqlite;

namespace Kufr.Backend.Features.Keepsakes;

// Each source writes this journal in its own result transaction. Delivery may
// repeat after a crash; ChronicleStore owns deduplication and deletion fences.
internal static class ChronicleSourceOutbox
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal static void Ensure(SqliteConnection connection, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS chronicle_source_outbox(
                source_key TEXT PRIMARY KEY, envelope TEXT NOT NULL, delivered INTEGER NOT NULL DEFAULT 0);
            """;
        command.ExecuteNonQuery();
    }

    internal static void Append(SqliteConnection connection, SqliteTransaction transaction,
        ChronicleOutboxEnvelope envelope)
    {
        Ensure(connection, transaction);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO chronicle_source_outbox(source_key,envelope)
            VALUES($key,$envelope) ON CONFLICT(source_key) DO NOTHING;
            """;
        command.Parameters.AddWithValue("$key", envelope.Event.SourceKind + ":" + envelope.Event.SourceId);
        command.Parameters.AddWithValue("$envelope", JsonSerializer.Serialize(envelope, Json));
        command.ExecuteNonQuery();
    }

    internal static int Drain(SqliteConnection connection, ChronicleStore destination)
    {
        Ensure(connection);
        var pending = new List<(string Key, ChronicleOutboxEnvelope Envelope)>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT source_key,envelope FROM chronicle_source_outbox WHERE delivered=0 ORDER BY rowid LIMIT 32";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                pending.Add((reader.GetString(0), JsonSerializer.Deserialize<ChronicleOutboxEnvelope>(reader.GetString(1), Json)!));
            }
        }
        foreach (var item in pending)
        {
            destination.Ingest(item.Envelope);
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE chronicle_source_outbox SET delivered=1 WHERE source_key=$key";
            command.Parameters.AddWithValue("$key", item.Key);
            command.ExecuteNonQuery();
        }
        return pending.Count;
    }

    internal static void EraseAccount(SqliteConnection connection, SqliteTransaction transaction, string accountId)
    {
        Ensure(connection, transaction);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        // A shared scene cannot retain a deleted participant in another owner's
        // pending copy. The destination applies the same whole-scene policy.
        command.CommandText = """
            DELETE FROM chronicle_source_outbox WHERE EXISTS(
                SELECT 1 FROM json_each(envelope,'$.event.participants')
                WHERE json_extract(value,'$.accountId')=$owner)
                OR EXISTS(SELECT 1 FROM json_each(envelope,'$.ownerIds') WHERE value=$owner);
            """;
        command.Parameters.AddWithValue("$owner", accountId);
        command.ExecuteNonQuery();
    }

    internal static void EraseProfile(SqliteConnection connection, SqliteTransaction transaction, string accountId, string profileId)
    {
        Ensure(connection, transaction);
        var keys = new List<string>();
        using (var query = connection.CreateCommand())
        {
            query.Transaction = transaction;
            query.CommandText = "SELECT source_key,envelope FROM chronicle_source_outbox";
            using var reader = query.ExecuteReader();
            while (reader.Read())
            {
                var envelope = JsonSerializer.Deserialize<ChronicleOutboxEnvelope>(reader.GetString(1), Json)!;
                if (envelope.Event.Participants.Any(person => person.AccountId == accountId
                    && (ChronicleStore.ProfileId(person) is null || ChronicleStore.ProfileId(person) == profileId)))
                { keys.Add(reader.GetString(0)); }
            }
        }
        foreach (var key in keys)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM chronicle_source_outbox WHERE source_key=$key";
            command.Parameters.AddWithValue("$key", key);
            command.ExecuteNonQuery();
        }
    }
}
