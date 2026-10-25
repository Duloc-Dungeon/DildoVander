using System.Security.Claims;
using Kufr.Backend.Features.Customers;
using Kufr.Backend.Features.Identity;
using Microsoft.Data.Sqlite;

namespace Kufr.Backend.Features.Launch;

internal sealed record LaunchConfiguration(long Revision, bool BetaEnabled, long? BetaLaunchAt,
    bool BetaUnlocked, bool ProductionEnabled, long? ProductionLaunchAt, bool ProductionUnlocked,
    string TimeZone, string Teaser, string InitialLoadingMessage, bool PersonalizedLoadingEnabled,
    string PersonalizedLoadingPrompt)
{
    internal static LaunchConfiguration Default => new(0, false, null, false, false, null, false,
        "Europe/Prague", "A little something is getting ready for you.",
        "A peculiar little surprise is taking shape…", false,
        "Write one brief, playful, mysterious loading message inspired by the supplied fictional character context. Do not reveal product features or repeat research facts.");
}

internal sealed record LaunchConfigurationUpdate(long ExpectedRevision, bool BetaEnabled, long? BetaLaunchAt,
    bool BetaUnlocked, bool ProductionEnabled, long? ProductionLaunchAt, bool ProductionUnlocked,
    string TimeZone, string Teaser, string InitialLoadingMessage, bool PersonalizedLoadingEnabled,
    string PersonalizedLoadingPrompt);

internal sealed record EffectiveLaunchAccess(bool Allowed, string Audience, long? LaunchAt, long ServerNow,
    string Teaser, string InitialLoadingMessage, bool PersonalizedLoadingEnabled);
internal sealed record LaunchAuditEntry(string ActorId, long ChangedAt, long Revision, string ChangeSummary);

internal sealed class LaunchConfigurationException(string code, int status) : Exception(code)
{
    internal string Code { get; } = code;
    internal int Status { get; } = status;
}

internal sealed class LaunchPolicyStore
{
    private readonly string _connectionString;

    internal LaunchPolicyStore(string dataRoot)
    {
        Directory.CreateDirectory(dataRoot);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(dataRoot, "launch.sqlite"),
            ForeignKeys = true
        }.ToString();
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS launch_configuration (
                id INTEGER PRIMARY KEY CHECK(id=1), revision INTEGER NOT NULL,
                beta_enabled INTEGER NOT NULL, beta_launch_at INTEGER NULL, beta_unlocked INTEGER NOT NULL,
                production_enabled INTEGER NOT NULL, production_launch_at INTEGER NULL, production_unlocked INTEGER NOT NULL,
                time_zone TEXT NOT NULL, teaser TEXT NOT NULL, initial_loading_message TEXT NOT NULL,
                personalized_loading_enabled INTEGER NOT NULL, personalized_loading_prompt TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS launch_audit (
                id INTEGER PRIMARY KEY AUTOINCREMENT, actor_id TEXT NOT NULL, changed_at INTEGER NOT NULL,
                revision INTEGER NOT NULL, change_summary TEXT NOT NULL);
            INSERT OR IGNORE INTO launch_configuration VALUES
                (1,0,0,NULL,0,0,NULL,0,'Europe/Prague',
                 'A little something is getting ready for you.',
                 'A peculiar little surprise is taking shape…',0,
                 'Write one brief, playful, mysterious loading message inspired by the supplied fictional character context. Do not reveal product features or repeat research facts.');
            """;
        command.ExecuteNonQuery();
    }

    internal LaunchConfiguration Read()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT revision,beta_enabled,beta_launch_at,beta_unlocked,production_enabled,production_launch_at,production_unlocked,time_zone,teaser,initial_loading_message,personalized_loading_enabled,personalized_loading_prompt FROM launch_configuration WHERE id=1";
        using var reader = command.ExecuteReader();
        if (!reader.Read()) { throw new InvalidOperationException("Launch configuration is unavailable."); }
        return new(reader.GetInt64(0), reader.GetBoolean(1), NullableInt64(reader, 2), reader.GetBoolean(3),
            reader.GetBoolean(4), NullableInt64(reader, 5), reader.GetBoolean(6), reader.GetString(7),
            reader.GetString(8), reader.GetString(9), reader.GetBoolean(10), reader.GetString(11));
    }

    internal IReadOnlyList<LaunchAuditEntry> ReadAudit(int count = 50)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT actor_id,changed_at,revision,change_summary FROM launch_audit ORDER BY id DESC LIMIT $count";
        command.Parameters.AddWithValue("$count", Math.Clamp(count, 1, 100));
        using var reader = command.ExecuteReader();
        var entries = new List<LaunchAuditEntry>();
        while (reader.Read())
        {
            entries.Add(new(reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetString(3)));
        }
        return entries;
    }

    internal LaunchConfiguration Update(string actorId, LaunchConfigurationUpdate update, long now)
    {
        Validate(update);
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using (var reservation = connection.CreateCommand())
        {
            reservation.Transaction = transaction;
            reservation.CommandText = "UPDATE launch_configuration SET revision=revision WHERE id=1 AND revision=$expected";
            reservation.Parameters.AddWithValue("$expected", update.ExpectedRevision);
            if (reservation.ExecuteNonQuery() != 1)
            {
                throw new LaunchConfigurationException("launch_revision_conflict", 409);
            }
        }
        var previous = ReadInside(connection, transaction);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE launch_configuration SET revision=revision+1,beta_enabled=$be,beta_launch_at=$bt,beta_unlocked=$bu,
                production_enabled=$pe,production_launch_at=$pt,production_unlocked=$pu,time_zone=$tz,teaser=$teaser,
                initial_loading_message=$message,personalized_loading_enabled=$personalized,
                personalized_loading_prompt=$prompt WHERE id=1 AND revision=$expected;
            """;
        command.Parameters.AddWithValue("$be", update.BetaEnabled);
        AddNullable(command, "$bt", update.BetaLaunchAt);
        command.Parameters.AddWithValue("$bu", update.BetaUnlocked);
        command.Parameters.AddWithValue("$pe", update.ProductionEnabled);
        AddNullable(command, "$pt", update.ProductionLaunchAt);
        command.Parameters.AddWithValue("$pu", update.ProductionUnlocked);
        command.Parameters.AddWithValue("$tz", update.TimeZone.Trim());
        command.Parameters.AddWithValue("$teaser", update.Teaser.Trim());
        command.Parameters.AddWithValue("$message", update.InitialLoadingMessage.Trim());
        command.Parameters.AddWithValue("$personalized", update.PersonalizedLoadingEnabled);
        command.Parameters.AddWithValue("$prompt", update.PersonalizedLoadingPrompt.Trim());
        command.Parameters.AddWithValue("$expected", update.ExpectedRevision);
        if (command.ExecuteNonQuery() != 1)
        {
            throw new LaunchConfigurationException("launch_revision_conflict", 409);
        }

        using var audit = connection.CreateCommand();
        audit.Transaction = transaction;
        audit.CommandText = "INSERT INTO launch_audit(actor_id,changed_at,revision,change_summary) VALUES($actor,$now,$revision,$summary)";
        audit.Parameters.AddWithValue("$actor", actorId);
        audit.Parameters.AddWithValue("$now", now);
        audit.Parameters.AddWithValue("$revision", update.ExpectedRevision + 1);
        var summary = string.Join(",", ChangedFields(previous, update));
        audit.Parameters.AddWithValue("$summary", summary.Length == 0 ? "no_effective_change" : summary);
        audit.ExecuteNonQuery();
        transaction.Commit();
        return Read();
    }

    private static IEnumerable<string> ChangedFields(LaunchConfiguration old, LaunchConfigurationUpdate update)
    {
        if (old.BetaEnabled != update.BetaEnabled || old.BetaLaunchAt != update.BetaLaunchAt || old.BetaUnlocked != update.BetaUnlocked) { yield return "beta_schedule"; }
        if (old.ProductionEnabled != update.ProductionEnabled || old.ProductionLaunchAt != update.ProductionLaunchAt || old.ProductionUnlocked != update.ProductionUnlocked) { yield return "production_schedule"; }
        if (old.TimeZone != update.TimeZone || old.Teaser != update.Teaser || old.InitialLoadingMessage != update.InitialLoadingMessage) { yield return "guest_experience"; }
        if (old.PersonalizedLoadingEnabled != update.PersonalizedLoadingEnabled || old.PersonalizedLoadingPrompt != update.PersonalizedLoadingPrompt) { yield return "personalization"; }
    }

    private static LaunchConfiguration ReadInside(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT revision,beta_enabled,beta_launch_at,beta_unlocked,production_enabled,production_launch_at,production_unlocked,time_zone,teaser,initial_loading_message,personalized_loading_enabled,personalized_loading_prompt FROM launch_configuration WHERE id=1";
        using var reader = command.ExecuteReader();
        reader.Read();
        return new(reader.GetInt64(0), reader.GetBoolean(1), NullableInt64(reader, 2), reader.GetBoolean(3),
            reader.GetBoolean(4), NullableInt64(reader, 5), reader.GetBoolean(6), reader.GetString(7),
            reader.GetString(8), reader.GetString(9), reader.GetBoolean(10), reader.GetString(11));
    }

    private static void Validate(LaunchConfigurationUpdate update)
    {
        if (update.ExpectedRevision < 0 || !ValidTimeZone(update.TimeZone)
            || update.BetaLaunchAt is < 0 or > 253402300799999
            || update.ProductionLaunchAt is < 0 or > 253402300799999
            || update.Teaser is null || update.Teaser.Trim().Length > 250
            || !string.IsNullOrWhiteSpace(update.Teaser) && !PortraitContentPolicy.ValidMessage(update.Teaser)
            || update.InitialLoadingMessage is null || update.InitialLoadingMessage.Trim().Length is 0 or > 160
            || !PortraitContentPolicy.ValidMessage(update.InitialLoadingMessage)
            || update.PersonalizedLoadingPrompt is null || update.PersonalizedLoadingPrompt.Trim().Length is 0 or > 4000)
        {
            throw new LaunchConfigurationException("invalid_launch_configuration", 400);
        }
    }

    private static bool ValidTimeZone(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 100) { return false; }
        try { _ = TimeZoneInfo.FindSystemTimeZoneById(value); return true; }
        catch (TimeZoneNotFoundException) { return false; }
        catch (InvalidTimeZoneException) { return false; }
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    private static long? NullableInt64(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);
    private static void AddNullable(SqliteCommand command, string name, long? value) => command.Parameters.AddWithValue(name, (object?)value ?? DBNull.Value);
}

internal sealed class LaunchAccessService(LaunchPolicyStore store, TimeProvider timeProvider)
{
    internal LaunchConfiguration Configuration => store.Read();

    internal EffectiveLaunchAccess Evaluate(ClaimsPrincipal principal)
    {
        var now = timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var config = store.Read();
        if (AccountRoles.IsStaff(principal) || principal.IsInRole(AccountRoles.Tester))
        {
            return new(true, "privileged", null, now, config.Teaser, config.InitialLoadingMessage, config.PersonalizedLoadingEnabled);
        }

        var beta = principal.IsInRole(AccountRoles.Beta);
        var enabled = beta ? config.BetaEnabled : config.ProductionEnabled;
        var launchAt = beta ? config.BetaLaunchAt : config.ProductionLaunchAt;
        var manuallyUnlocked = beta ? config.BetaUnlocked : config.ProductionUnlocked;
        var allowed = !enabled || manuallyUnlocked || launchAt is not null && now >= launchAt;
        return new(allowed, beta ? "beta" : "production", enabled ? launchAt : null, now,
            config.Teaser, config.InitialLoadingMessage, config.PersonalizedLoadingEnabled);
    }
}
