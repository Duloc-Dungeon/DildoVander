using Microsoft.Data.Sqlite;

namespace Kufr.Backend.Features.Customers;

internal static class CharacterLabel
{
    internal static string? Normalize(string? label)
    {
        if (label is { Length: > 80 } || label?.Any(char.IsControl) == true)
        {
            throw new CharacterException("invalid_character_label");
        }
        return string.IsNullOrWhiteSpace(label) ? null : label.Trim();
    }

    internal static string DisplayName(string name, string? label) =>
        string.IsNullOrEmpty(label) ? name : $"{name} · {label}";
}

internal sealed partial class CharacterProfileStore
{
    internal string? ReadDisplayLabel(string accountId, string profileId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT label FROM character_labels WHERE account_id=$account AND profile_id=$profile";
        command.Parameters.AddWithValue("$account", accountId);
        command.Parameters.AddWithValue("$profile", profileId);
        return command.ExecuteScalar() as string;
    }

    private static void InitializeLabels(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS character_labels (
                account_id TEXT PRIMARY KEY,
                profile_id TEXT NOT NULL REFERENCES character_profiles(profile_id) ON DELETE CASCADE,
                label TEXT NOT NULL);
            """;
        command.ExecuteNonQuery();
    }

    internal Task<CharacterProfile> WriteLabelAsync(string accountId, CharacterLabelWriteRequest request,
        CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var label = CharacterLabel.Normalize(request.Label);
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        var current = Read(connection, transaction, accountId)
            ?? throw new CharacterException("character_not_found", 404);
        if (current.ProfileId != request.ProfileId || current.Label != request.ExpectedLabel)
        {
            throw new CharacterException("character_label_conflict", 409);
        }
        if (Scalar(connection, transaction,
            "SELECT COUNT(*) FROM character_account_fences WHERE account_id=$account", accountId) != 0)
        {
            throw new CharacterException("account_data_deleted", 409);
        }
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = label is null
            ? "DELETE FROM character_labels WHERE account_id=$account"
            : """
                INSERT INTO character_labels(account_id,profile_id,label) VALUES($account,$profile,$label)
                ON CONFLICT(account_id) DO UPDATE SET profile_id=$profile,label=$label
                """;
        command.Parameters.AddWithValue("$account", accountId);
        command.Parameters.AddWithValue("$profile", current.ProfileId);
        command.Parameters.AddWithValue("$label", (object?)label ?? DBNull.Value);
        command.ExecuteNonQuery();
        transaction.Commit();
        // Display metadata does not revise the AI profile or enqueue enrichment.
        return Task.FromResult(current with { Label = label });
    }
}
