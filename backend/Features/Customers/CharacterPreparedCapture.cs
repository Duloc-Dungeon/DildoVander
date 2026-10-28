using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Kufr.Backend.Features.Customers;

internal sealed partial class CharacterProfileStore
{
    // A single SQLite read transaction binds preparation status, revision and
    // enrichment to the exact character snapshot used by the workshop.
    internal Task<CharacterConversationSnapshot?> CapturePreparedAsync(string accountId,
        string profileId, long revision, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: true);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT profile_id,revision,input_json,enrichment_json
            FROM character_profiles
            WHERE account_id=$account AND profile_id=$profile AND revision=$revision AND status='ready'
            """;
        command.Parameters.AddWithValue("$account", accountId);
        command.Parameters.AddWithValue("$profile", profileId);
        command.Parameters.AddWithValue("$revision", revision);
        using var reader = command.ExecuteReader();
        if (!reader.Read() || reader.IsDBNull(3)) return Task.FromResult<CharacterConversationSnapshot?>(null);
        var id = reader.GetString(0);
        var currentRevision = reader.GetInt64(1);
        var input = JsonSerializer.Deserialize<CharacterUserInput>(reader.GetString(2), Json);
        var enrichment = JsonSerializer.Deserialize<CharacterEnrichment>(reader.GetString(3), Json);
        reader.Close();
        if (input is null || enrichment is null || enrichment.NeedsClarification
            || enrichment.InputRevision != currentRevision) return Task.FromResult<CharacterConversationSnapshot?>(null);
        var snapshot = new CharacterConversationSnapshot(id, currentRevision, input, enrichment, Now());
        transaction.Commit();
        return Task.FromResult<CharacterConversationSnapshot?>(snapshot);
    }
}
