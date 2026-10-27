using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Kufr.Backend.Features.Customers;

internal sealed record CharacterPortrait(string Status, string? LoadingMessage, string? FinalMessage,
    string? ErrorCode, int Attempt, long UpdatedAt, CharacterWelcome? Welcome = null);
internal sealed record CharacterPortraitJob(string AccountId, string ProfileId, long Revision, string LeaseToken,
    int Attempt, CharacterUserInput Input, CharacterEnrichment Enrichment);
internal sealed record PortraitJobSummary(string ProfileId, long Revision, string Status, int Attempt, string? ErrorCode, long UpdatedAt);

internal sealed partial class CharacterProfileStore
{
    private static void InitializePortraits(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        // Same database and transaction as profile deletion: no orphan assets or late resurrection.
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS character_portraits (
                account_id TEXT PRIMARY KEY REFERENCES character_profiles(account_id) ON DELETE CASCADE,
                profile_id TEXT NOT NULL, revision INTEGER NOT NULL, status TEXT NOT NULL,
                loading_message TEXT NULL, final_message TEXT NULL, welcome_json TEXT NULL, image BLOB NULL, error_code TEXT NULL,
                attempt INTEGER NOT NULL DEFAULT 0, lease_token TEXT NULL,
                lease_until INTEGER NOT NULL DEFAULT 0, updated_at INTEGER NOT NULL);
            CREATE INDEX IF NOT EXISTS character_portrait_queue ON character_portraits(status,updated_at);
            """;
        command.ExecuteNonQuery();
        using var column = connection.CreateCommand();
        column.CommandText = "SELECT COUNT(*) FROM pragma_table_info('character_portraits') WHERE name='final_message'";
        if ((long)column.ExecuteScalar()! == 0)
        {
            column.CommandText = "ALTER TABLE character_portraits ADD COLUMN final_message TEXT NULL";
            column.ExecuteNonQuery();
        }
        column.CommandText = "SELECT COUNT(*) FROM pragma_table_info('character_portraits') WHERE name='welcome_json'";
        if ((long)column.ExecuteScalar()! == 0)
        {
            column.CommandText = "ALTER TABLE character_portraits ADD COLUMN welcome_json TEXT NULL";
            column.ExecuteNonQuery();
        }
    }

    private static void SavePortraitRequest(SqliteConnection connection, SqliteTransaction transaction,
        string accountId, CharacterProfile profile, CharacterWriteRequest request)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.Parameters.AddWithValue("$account", accountId);
        command.CommandText = "SELECT COUNT(*) FROM character_portraits WHERE account_id=$account";
        var previouslyConsented = (long)command.ExecuteScalar()! != 0;
        if (!request.PortraitConsent && !previouslyConsented) { return; }
        if (!request.OpenAiConsent)
        {
            command.CommandText = "DELETE FROM character_portraits WHERE account_id=$account";
            command.ExecuteNonQuery();
            return;
        }
        command.CommandText = """
            INSERT INTO character_portraits(account_id,profile_id,revision,status,updated_at)
            VALUES($account,$profile,$revision,'queued',$now)
            ON CONFLICT(account_id) DO UPDATE SET profile_id=$profile,revision=$revision,status='queued',
                loading_message=NULL,final_message=NULL,welcome_json=NULL,image=NULL,error_code=NULL,attempt=0,lease_token=NULL,lease_until=0,updated_at=$now
            WHERE character_portraits.profile_id!=$profile OR character_portraits.revision!=$revision;
            """;
        command.Parameters.AddWithValue("$profile", profile.ProfileId);
        command.Parameters.AddWithValue("$revision", profile.Revision);
        command.Parameters.AddWithValue("$now", Now());
        command.ExecuteNonQuery();
    }

    internal CharacterPortrait? ReadPortrait(string accountId, string profileId, long revision)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT a.status,a.loading_message,a.error_code,a.attempt,a.updated_at,p.status,a.final_message,a.welcome_json
            FROM character_portraits a JOIN character_profiles p USING(account_id)
            WHERE a.account_id=$account AND a.profile_id=$profile AND a.revision=$revision
                AND a.profile_id=p.profile_id AND a.revision=p.revision;
            """;
        command.Parameters.AddWithValue("$account", accountId);
        command.Parameters.AddWithValue("$profile", profileId);
        command.Parameters.AddWithValue("$revision", revision);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) { return null; }
        return new(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.GetInt32(3), reader.GetInt64(4), reader.IsDBNull(7) ? null : JsonSerializer.Deserialize<CharacterWelcome>(reader.GetString(7)));
    }

    internal byte[]? ReadPortraitImage(string accountId, string profileId, long revision)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT a.image FROM character_portraits a JOIN character_profiles p USING(account_id)
            WHERE a.account_id=$account AND a.profile_id=$profile AND a.revision=$revision AND a.profile_id=p.profile_id
                AND a.revision=p.revision AND a.status='ready'
                AND NOT EXISTS(SELECT 1 FROM character_account_fences WHERE account_id=$account);
            """;
        command.Parameters.AddWithValue("$account", accountId);
        command.Parameters.AddWithValue("$profile", profileId);
        command.Parameters.AddWithValue("$revision", revision);
        return command.ExecuteScalar() as byte[];
    }

    internal CharacterPortraitJob? ClaimPortrait(long? currentTime = null)
    {
        var now = currentTime ?? Now();
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        // Image requests may have been billed before a disconnect. Never automatically repeat an uncertain request.
        command.CommandText = """
            UPDATE character_portraits SET status='failed',error_code='portrait_interrupted',lease_token=NULL,updated_at=$now
                WHERE status='running' AND lease_until<$now;
            SELECT a.account_id,a.profile_id,a.revision,a.attempt,p.input_json,p.enrichment_json
            FROM character_portraits a JOIN character_profiles p USING(account_id)
            WHERE a.status='queued' AND a.attempt<3 AND a.profile_id=p.profile_id AND a.revision=p.revision
                AND (p.enrichment_json IS NOT NULL OR p.status='failed')
                AND NOT EXISTS(SELECT 1 FROM character_account_fences f WHERE f.account_id=a.account_id)
            ORDER BY a.updated_at LIMIT 1;
            """;
        command.Parameters.AddWithValue("$now", now);
        CharacterPortraitJob? job;
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read()) { transaction.Commit(); return null; }
            var input = JsonSerializer.Deserialize<CharacterUserInput>(reader.GetString(4), Json)!;
            var revision = reader.GetInt64(2);
            // Research is optional. Failed enrichment must not strand a consented
            // image request; the original input remains explicitly unverified.
            var enrichment = reader.IsDBNull(5)
                ? new CharacterEnrichment(input.Name, input.World, "", [], [], [], true, [],
                    "none", "portrait-input-only", now, revision, "user_input")
                : JsonSerializer.Deserialize<CharacterEnrichment>(reader.GetString(5), Json)!;
            job = new(reader.GetString(0), reader.GetString(1), revision, Guid.NewGuid().ToString("N"),
                reader.GetInt32(3) + 1, input, enrichment);
        }
        command.CommandText = """
            UPDATE character_portraits SET status='running',attempt=$attempt,lease_token=$lease,lease_until=$until,
                updated_at=$now,error_code=NULL WHERE account_id=$account AND status='queued';
            """;
        BindPortrait(command, job);
        command.Parameters.AddWithValue("$attempt", job.Attempt);
        command.Parameters.AddWithValue("$until", now + 240);
        command.ExecuteNonQuery();
        transaction.Commit();
        return job;
    }

    internal bool CanDispatchPortrait(CharacterPortraitJob job)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*) FROM character_portraits a JOIN character_profiles p USING(account_id)
            WHERE a.account_id=$account AND a.profile_id=$profile AND a.revision=$revision AND a.lease_token=$lease
                AND a.status='running' AND a.lease_until>$now AND p.profile_id=a.profile_id AND p.revision=a.revision
                AND NOT EXISTS(SELECT 1 FROM character_account_fences WHERE account_id=$account);
            """;
        BindPortrait(command, job);
        command.Parameters.AddWithValue("$now", Now());
        return (long)command.ExecuteScalar()! == 1;
    }

    internal bool SetPortraitMessage(CharacterPortraitJob job, string message) => UpdatePortrait(job,
        "loading_message=$message", command => command.Parameters.AddWithValue("$message", message));

    internal bool SetPortraitExperience(CharacterPortraitJob job, string finalMessage, CharacterWelcome welcome)
    {
        if (!PortraitContentPolicy.ValidMessage(finalMessage) || !CharacterWelcomePolicy.Valid(welcome, job))
        {
            return false;
        }
        return UpdatePortrait(job, "final_message=$final,welcome_json=$welcome", command =>
        {
            command.Parameters.AddWithValue("$final", finalMessage);
            command.Parameters.AddWithValue("$welcome", JsonSerializer.Serialize(welcome));
        });
    }

    internal bool CompletePortrait(CharacterPortraitJob job, byte[]? image, string? errorCode,
        string? finalMessage = null, CharacterWelcome? welcome = null)
    {
        if (image is { Length: > 8 * 1024 * 1024 or 0 }) { throw new InvalidDataException("Portrait size invalid."); }
        finalMessage = image is null ? null
            : PortraitContentPolicy.ValidMessage(finalMessage) ? finalMessage
            : PortraitContentPolicy.FinalMessageFallback(job.Input);
        if (image is null) { welcome = null; }
        else if (!CharacterWelcomePolicy.Valid(welcome, job)) { welcome = CharacterWelcomePolicy.Fallback; }
        // Text has its own successful result. An image failure must not erase it.
        return UpdatePortrait(job, "status=$status,image=$image,final_message=COALESCE($final,final_message),welcome_json=COALESCE($welcome,welcome_json),error_code=$error,lease_until=0", command =>
        {
            command.Parameters.AddWithValue("$status", image is null ? "failed" : "ready");
            command.Parameters.AddWithValue("$image", (object?)image ?? DBNull.Value);
            command.Parameters.AddWithValue("$final", (object?)finalMessage ?? DBNull.Value);
            command.Parameters.AddWithValue("$welcome", welcome is null ? DBNull.Value : JsonSerializer.Serialize(welcome));
            command.Parameters.AddWithValue("$error", (object?)errorCode ?? DBNull.Value);
        });
    }

    private bool UpdatePortrait(CharacterPortraitJob job, string assignments, Action<SqliteCommand> bind)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            UPDATE character_portraits SET {assignments},updated_at=$now
            WHERE account_id=$account AND profile_id=$profile AND revision=$revision AND lease_token=$lease AND status='running'
                AND lease_until>$now AND NOT EXISTS(SELECT 1 FROM character_account_fences WHERE account_id=$account);
            """;
        BindPortrait(command, job);
        command.Parameters.AddWithValue("$now", Now());
        bind(command);
        return command.ExecuteNonQuery() == 1;
    }

    internal bool RetryPortrait(string accountId, string? profileId = null, long? revision = null)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE character_portraits SET status='queued',error_code=NULL,lease_token=NULL,lease_until=0,updated_at=$now
            WHERE account_id=$account AND status='failed' AND attempt<3
                AND ($profile IS NULL OR profile_id=$profile) AND ($revision IS NULL OR revision=$revision)
                AND NOT EXISTS(SELECT 1 FROM character_account_fences WHERE account_id=$account);
            """;
        command.Parameters.AddWithValue("$account", accountId);
        command.Parameters.AddWithValue("$profile", (object?)profileId ?? DBNull.Value);
        command.Parameters.AddWithValue("$revision", (object?)revision ?? DBNull.Value);
        command.Parameters.AddWithValue("$now", Now());
        return command.ExecuteNonQuery() == 1;
    }

    // The authenticated endpoint establishes account consent before creating a
    // portrait for an older character that only requested text preparation.
    internal bool RequestPortrait(string accountId, string profileId, long revision)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO character_portraits(account_id,profile_id,revision,status,updated_at)
            SELECT p.account_id,p.profile_id,p.revision,'queued',$now FROM character_profiles p
            WHERE p.account_id=$account AND p.profile_id=$profile AND p.revision=$revision
                AND (p.enrichment_json IS NOT NULL OR p.status IN ('queued','running','failed'))
                AND NOT EXISTS(SELECT 1 FROM character_account_fences WHERE account_id=$account)
            ON CONFLICT(account_id) DO NOTHING;
            """;
        command.Parameters.AddWithValue("$account", accountId);
        command.Parameters.AddWithValue("$profile", profileId);
        command.Parameters.AddWithValue("$revision", revision);
        command.Parameters.AddWithValue("$now", Now());
        return command.ExecuteNonQuery() == 1;
    }

    internal PortraitJobSummary[] PortraitJobSummaries()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT profile_id,revision,status,attempt,error_code,updated_at FROM character_portraits ORDER BY updated_at DESC LIMIT 100";
        using var reader = command.ExecuteReader();
        var result = new List<PortraitJobSummary>();
        while (reader.Read())
        {
            result.Add(new(reader.GetString(0), reader.GetInt64(1), reader.GetString(2), reader.GetInt32(3),
                reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetInt64(5) * 1000));
        }
        return result.ToArray();
    }

    internal string? PortraitOwner(string profileId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT account_id FROM character_portraits WHERE profile_id=$profile";
        command.Parameters.AddWithValue("$profile", profileId);
        return command.ExecuteScalar() as string;
    }

    private static void BindPortrait(SqliteCommand command, CharacterPortraitJob job)
    {
        command.Parameters.AddWithValue("$account", job.AccountId);
        command.Parameters.AddWithValue("$profile", job.ProfileId);
        command.Parameters.AddWithValue("$revision", job.Revision);
        command.Parameters.AddWithValue("$lease", job.LeaseToken);
    }
}
