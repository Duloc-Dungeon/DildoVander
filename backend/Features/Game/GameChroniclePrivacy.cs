using System.Text.Json;
using Kufr.Backend.Features.Chronicle;
using Microsoft.Data.Sqlite;

namespace Kufr.Backend.Features.Game;

internal sealed partial class GameStore
{
    internal void DeleteChronicleProfile(string accountId, string profileId)
    {
        lock (_writeGate)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction(deferred: false);
            var outboxIds = new List<string>();
            using (var query = connection.CreateCommand())
            {
                query.Transaction = transaction;
                query.CommandText = "SELECT event_id,event_json FROM game_chronicle_outbox";
                using var reader = query.ExecuteReader();
                while (reader.Read())
                {
                    var envelope = JsonSerializer.Deserialize<ChronicleOutboxEnvelope>(reader.GetString(1), Json)!;
                    if (envelope.Event.Participants.Any(person => person.AccountId == accountId
                        && (ChronicleStore.ProfileId(person) is null || ChronicleStore.ProfileId(person) == profileId)))
                    { outboxIds.Add(reader.GetString(0)); }
                }
            }
            foreach (var id in outboxIds) { EraseChronicleRow(connection, transaction, "game_chronicle_outbox", "event_id", id); }
            EraseProfileMatches(connection, transaction, "game_matches", accountId, profileId, state =>
            {
                var match = JsonSerializer.Deserialize<State>(state, Json)!;
                var own = accountId == match.ChallengerId ? match.ChallengerPublicProfile : accountId == match.OpponentId ? match.OpponentPublicProfile : null;
                return (match.ChallengerId == accountId || match.OpponentId == accountId) && (own?.ProfileId is null || own.ProfileId == profileId);
            });
            EraseProfileMatches(connection, transaction, "game_team_matches", accountId, profileId, state =>
            {
                var match = JsonSerializer.Deserialize<TeamState>(state, Json)!;
                match.FrozenProfiles.TryGetValue(accountId, out var own);
                return match.A.Concat(match.B).Contains(accountId, StringComparer.Ordinal) && (own?.ProfileId is null || own.ProfileId == profileId);
            });
            EraseProfileMatches(connection, transaction, "game_boss_attempts", accountId, profileId, state =>
            {
                var match = JsonSerializer.Deserialize<BossAttempt>(state, Json)!;
                return match.UserId == accountId && (match.ChronicleProfile?.ProfileId is null || match.ChronicleProfile.ProfileId == profileId);
            });
            transaction.Commit();
        }
    }

    private static void EraseProfileMatches(SqliteConnection connection, SqliteTransaction transaction,
        string table, string accountId, string profileId, Func<string, bool> affected)
    {
        using var query = connection.CreateCommand();
        query.Transaction = transaction;
        query.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name=$table";
        query.Parameters.AddWithValue("$table", table);
        if (query.ExecuteScalar() is null) { return; }
        query.CommandText = $"SELECT id,state_json FROM {table}";
        query.Parameters.Clear();
        var ids = new List<string>();
        using (var reader = query.ExecuteReader())
        {
            while (reader.Read()) { if (affected(reader.GetString(1))) { ids.Add(reader.GetString(0)); } }
        }
        foreach (var id in ids) { EraseChronicleRow(connection, transaction, table, "id", id); }
    }

    private static void EraseChronicleRow(SqliteConnection connection, SqliteTransaction transaction, string table, string key, string id)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        // Table/key names are exclusively internal constants above.
        command.CommandText = $"DELETE FROM {table} WHERE {key}=$id";
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }
}
