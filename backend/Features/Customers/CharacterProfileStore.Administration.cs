using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Kufr.Backend.Features.Customers;

internal sealed record AdminCharacterPortrait(string Status, bool HasImage, string? ErrorCode, long UpdatedAt);
internal sealed record AdminCharacter(string ProfileId, long Revision, CharacterUserInput UserInput,
    string Status, CharacterEnrichment? Enrichment, string? ErrorCode, long UpdatedAt, AdminCharacterPortrait? Portrait);
internal sealed record AdminCharacterWriteRequest(long ExpectedRevision, CharacterUserInput UserInput);
internal sealed record AdminCharacterIdentity(string Name, string World);
internal sealed record AdminCharacterSummary(string ProfileId, long Revision, AdminCharacterIdentity UserInput,
    string Status, long UpdatedAt);
internal sealed record AdminCharacterPage(AdminCharacterSummary[] Items, string? NextCursor);

internal sealed partial class CharacterProfileStore
{
    private const string AdminVisible = """
        NOT EXISTS(SELECT 1 FROM character_account_fences f WHERE f.account_id=p.account_id)
        AND NOT EXISTS(SELECT 1 FROM character_deletions d WHERE d.account_id=p.account_id
            AND (d.profile_id='*' OR d.profile_id=p.profile_id))
        """;

    internal AdminCharacterPage ReadAdminCharacters(string? after = null)
    {
        if (after is not null && (after.Length != 32 || after.Any(character => !char.IsAsciiHexDigitLower(character))))
        {
            throw new CharacterException("invalid_character_cursor");
        }
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT p.profile_id,p.revision,p.input_json,p.status,p.updated_at
            FROM character_profiles p WHERE {AdminVisible} AND ($after IS NULL OR p.profile_id>$after)
            ORDER BY p.profile_id LIMIT 51;
            """;
        command.Parameters.AddWithValue("$after", (object?)after ?? DBNull.Value);
        using var reader = command.ExecuteReader();
        var items = new List<AdminCharacterSummary>();
        while (reader.Read())
        {
            var input = JsonSerializer.Deserialize<CharacterUserInput>(reader.GetString(2), Json)!;
            items.Add(new(reader.GetString(0), reader.GetInt64(1), new(input.Name, input.World),
                reader.GetString(3), reader.GetInt64(4)));
        }
        var hasMore = items.Count > 50;
        if (hasMore) { items.RemoveAt(50); }
        return new(items.ToArray(), hasMore ? items[^1].ProfileId : null);
    }

    internal AdminCharacter? ReadAdminCharacter(string profileId)
    {
        using var connection = Open();
        return ReadAdminCharacters(connection, null, profileId).SingleOrDefault();
    }

    private static AdminCharacter[] ReadAdminCharacters(SqliteConnection connection, SqliteTransaction? transaction, string? profileId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            SELECT p.profile_id,p.revision,p.input_json,p.status,p.enrichment_json,p.error_code,p.updated_at,
                a.status,a.image IS NOT NULL,a.error_code,a.updated_at
            FROM character_profiles p LEFT JOIN character_portraits a
                ON a.account_id=p.account_id AND a.profile_id=p.profile_id AND a.revision=p.revision
            WHERE {AdminVisible} AND ($profile IS NULL OR p.profile_id=$profile)
            ORDER BY p.updated_at DESC,p.profile_id;
            """;
        command.Parameters.AddWithValue("$profile", (object?)profileId ?? DBNull.Value);
        using var reader = command.ExecuteReader();
        var result = new List<AdminCharacter>();
        while (reader.Read())
        {
            result.Add(new(reader.GetString(0), reader.GetInt64(1),
                JsonSerializer.Deserialize<CharacterUserInput>(reader.GetString(2), Json)!, reader.GetString(3),
                reader.IsDBNull(4) ? null : JsonSerializer.Deserialize<CharacterEnrichment>(reader.GetString(4), Json),
                reader.IsDBNull(5) ? null : reader.GetString(5), reader.GetInt64(6),
                reader.IsDBNull(7) ? null : new(reader.GetString(7), reader.GetBoolean(8),
                    reader.IsDBNull(9) ? null : reader.GetString(9), reader.GetInt64(10))));
        }
        return result.ToArray();
    }

    internal byte[]? ReadAdminPortrait(string profileId, long revision)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT a.image FROM character_profiles p JOIN character_portraits a
                ON a.account_id=p.account_id AND a.profile_id=p.profile_id AND a.revision=p.revision
            WHERE p.profile_id=$profile AND p.revision=$revision AND {AdminVisible};
            """;
        command.Parameters.AddWithValue("$profile", profileId);
        command.Parameters.AddWithValue("$revision", revision);
        return command.ExecuteScalar() is byte[] image ? image : null;
    }

    internal AdminCharacter EditAdminCharacter(string profileId, AdminCharacterWriteRequest request)
    {
        Validate(request.UserInput);
        return MutateAdminCharacter(profileId, request.ExpectedRevision, request.UserInput, null);
    }

    internal AdminCharacter ReplaceAdminPortrait(string profileId, long expectedRevision, byte[] image)
    {
        if (image.Length is 0 or > CharacterAdminEndpoints.MaximumUploadBytes)
        {
            throw new CharacterException("invalid_character_portrait");
        }
        return MutateAdminCharacter(profileId, expectedRevision, null, image);
    }

    private AdminCharacter MutateAdminCharacter(string profileId, long expectedRevision, CharacterUserInput? input, byte[]? image)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        var current = ReadAdminCharacters(connection, transaction, profileId).SingleOrDefault()
            ?? throw new CharacterException("character_not_found", 404);
        if (current.Revision != expectedRevision)
        {
            throw new CharacterException("character_revision_conflict", 409);
        }
        var inputChanged = input is not null && input != current.UserInput;
        var revision = checked(current.Revision + 1);
        // Revision and lease invalidation share one write transaction with the replacement.
        // In-flight providers can finish, but cannot commit their obsolete responses.
        var enrichment = inputChanged ? null : current.Enrichment is { } saved ? saved with { InputRevision = revision } : null;
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE character_profiles SET revision=$revision,input_json=$input,status=$status,
                enrichment_json=$enrichment,error_code=NULL,updated_at=$now WHERE profile_id=$profile;
            DELETE FROM character_jobs WHERE profile_id=$profile;
            """;
        command.Parameters.AddWithValue("$profile", profileId);
        command.Parameters.AddWithValue("$revision", revision);
        command.Parameters.AddWithValue("$input", JsonSerializer.Serialize(input ?? current.UserInput, Json));
        command.Parameters.AddWithValue("$status", enrichment is null ? "saved" : current.Status);
        command.Parameters.AddWithValue("$enrichment", enrichment is null ? DBNull.Value : JsonSerializer.Serialize(enrichment, Json));
        command.Parameters.AddWithValue("$now", Now());
        command.ExecuteNonQuery();
        if (image is null)
        {
            command.CommandText = """
                DELETE FROM character_portraits WHERE profile_id=$profile AND (status!='ready' OR image IS NULL);
                UPDATE character_portraits SET revision=$revision,loading_message=NULL,final_message=NULL,
                    welcome_json=NULL,lease_token=NULL,lease_until=0,updated_at=$now WHERE profile_id=$profile;
                """;
        }
        else
        {
            command.CommandText = """
                INSERT INTO character_portraits(account_id,profile_id,revision,status,image,updated_at)
                SELECT account_id,profile_id,$revision,'ready',$image,$now FROM character_profiles WHERE profile_id=$profile
                ON CONFLICT(account_id) DO UPDATE SET revision=$revision,status='ready',image=$image,
                    loading_message=NULL,final_message=NULL,welcome_json=NULL,source_json=NULL,error_code=NULL,
                    attempt=0,lease_token=NULL,lease_until=0,updated_at=$now;
                """;
            command.Parameters.AddWithValue("$image", image);
        }
        command.ExecuteNonQuery();
        var updated = ReadAdminCharacters(connection, transaction, profileId).Single();
        transaction.Commit();
        return updated;
    }
}
