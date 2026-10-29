using Microsoft.Data.Sqlite;

namespace Kufr.Backend.Features.Game;

internal sealed record GameBossNarrationCache(string? Text, byte[]? Audio, string? AudioKey);

internal sealed partial class GameStore
{
    private static void EnsureBossNarrationAudioSchema(SqliteConnection connection)
    {
        bool HasAudioKey(SqliteTransaction? transaction = null)
        {
            using var query = connection.CreateCommand();
            query.Transaction = transaction;
            query.CommandText = "PRAGMA table_info(game_boss_narration)";
            using var reader = query.ExecuteReader();
            while (reader.Read())
            {
                if (reader.GetString(1) == "audio_key") { return true; }
            }
            return false;
        }
        if (HasAudioKey()) { return; }
        // Upgrade an existing local cache without losing its immutable words.
        // Recheck under the write transaction if another process upgrades first.
        using var migration = connection.BeginTransaction(deferred: false);
        if (!HasAudioKey(migration))
        {
            using var alter = connection.CreateCommand();
            alter.Transaction = migration;
            alter.CommandText = "ALTER TABLE game_boss_narration ADD COLUMN audio_key TEXT";
            alter.ExecuteNonQuery();
        }
        migration.Commit();
    }

    internal GameBossNarrationCache? ReadBossNarration(string group, string user, string id,
        long revision, string key)
    {
        lock (_writeGate)
        {
            using var connection = Open();
            EnsureBossTables(connection);
            var attempt = ReadBossAttempt(connection, null, group, user, id);
            RequireBossNarrationRevision(attempt, revision);
            using var query = connection.CreateCommand();
            query.CommandText = "SELECT text,audio,audio_key FROM game_boss_narration WHERE attempt_id=$id AND revision=$revision AND context_key=$key";
            query.Parameters.AddWithValue("$id", id);
            query.Parameters.AddWithValue("$revision", revision);
            query.Parameters.AddWithValue("$key", key);
            using var reader = query.ExecuteReader();
            return reader.Read() ? new(reader.IsDBNull(0) ? null : reader.GetString(0),
                reader.IsDBNull(1) ? null : (byte[])reader.GetValue(1),
                reader.IsDBNull(2) ? null : reader.GetString(2)) : null;
        }
    }

    internal string? ClaimBossNarration(string group, string user, string id, long revision, string key)
    {
        lock (_writeGate)
        {
            using var connection = Open();
            EnsureBossTables(connection);
            using var transaction = connection.BeginTransaction(deferred: false);
            EnsureGameAccount(connection, transaction, user);
            RequireBossNarrationRevision(ReadBossAttempt(connection, transaction, group, user, id), revision);
            using (var upsert = connection.CreateCommand())
            {
                upsert.Transaction = transaction;
                upsert.CommandText = """
                    INSERT INTO game_boss_narration(attempt_id,revision,context_key) VALUES($id,$revision,$key)
                    ON CONFLICT(attempt_id) DO UPDATE SET revision=$revision,context_key=$key,
                        text=NULL,audio=NULL,audio_key=NULL,claim=NULL,claim_until=0
                    WHERE revision<>$revision OR context_key<>$key
                    """;
                upsert.Parameters.AddWithValue("$id", id);
                upsert.Parameters.AddWithValue("$revision", revision);
                upsert.Parameters.AddWithValue("$key", key);
                upsert.ExecuteNonQuery();
            }
            var claim = Guid.NewGuid().ToString("N");
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE game_boss_narration SET claim=$claim,claim_until=$until WHERE attempt_id=$id AND claim_until<=$now";
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$claim", claim);
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            command.Parameters.AddWithValue("$now", now);
            command.Parameters.AddWithValue("$until", now + 90);
            var acquired = command.ExecuteNonQuery() == 1;
            transaction.Commit();
            return acquired ? claim : null;
        }
    }

    internal void PublishBossNarration(string group, string user, string id, long revision,
        string key, string claim, string? text, byte[]? audio, string? audioKey = null)
    {
        if ((audio is null) != (audioKey is null)) { throw new InvalidDataException("Audio requires its rendering identity."); }
        lock (_writeGate)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction(deferred: false);
            EnsureGameAccount(connection, transaction, user);
            RequireBossNarrationRevision(ReadBossAttempt(connection, transaction, group, user, id), revision);
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE game_boss_narration SET text=COALESCE($text,text),audio=COALESCE($audio,audio),
                    audio_key=CASE WHEN $audio IS NOT NULL THEN $audioKey ELSE audio_key END,claim=NULL,claim_until=0
                WHERE attempt_id=$id AND revision=$revision AND context_key=$key AND claim=$claim AND claim_until>$now
                """;
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$revision", revision);
            command.Parameters.AddWithValue("$key", key);
            command.Parameters.AddWithValue("$claim", claim);
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            command.Parameters.AddWithValue("$text", (object?)text ?? DBNull.Value);
            command.Parameters.AddWithValue("$audio", (object?)audio ?? DBNull.Value);
            command.Parameters.AddWithValue("$audioKey", (object?)audioKey ?? DBNull.Value);
            if (command.ExecuteNonQuery() != 1) { throw new GameFault(409, "boss_narration_revision_conflict"); }
            transaction.Commit();
        }
    }

    internal void ReleaseBossNarration(string id, string claim)
    {
        lock (_writeGate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE game_boss_narration SET claim=NULL,claim_until=0 WHERE attempt_id=$id AND claim=$claim";
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$claim", claim);
            command.ExecuteNonQuery();
        }
    }

    private static void RequireBossNarrationRevision(BossAttempt attempt, long revision)
    {
        if (revision < 1) { throw new GameFault(400, "boss_narration_revision_invalid"); }
        if (attempt.Revision != revision || attempt.Status is not ("active" or "complete"))
        { throw new GameFault(409, "boss_narration_revision_conflict"); }
    }
}
