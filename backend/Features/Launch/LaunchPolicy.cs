using System.Security.Claims;
using Kufr.Backend.Features.Customers;
using Kufr.Backend.Features.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Kufr.Backend.Features.Launch;

internal sealed record LaunchConfiguration(long Revision, bool BetaEnabled, long? BetaLaunchAt,
    bool BetaUnlocked, bool ProductionEnabled, long? ProductionLaunchAt, bool ProductionUnlocked,
    string TimeZone, string Teaser, string InitialLoadingMessage, bool PersonalizedLoadingEnabled,
    string PersonalizedLoadingPrompt)
{
    internal const string DefaultTeaser = "Noční stolek si něco šeptá. Olivander má kocovinu; osud dnes vyvolává čísla až po snídani.";
    internal const string DefaultInitialLoadingMessage = "Někdo už klepe na dveře osudu. Olivander má kocovinu, a tak se i proroctví postavilo do fronty.";
    internal const string DefaultPersonalizedLoadingPrompt =
        "Napiš jednu krátkou českou větu jako napínavou předzvěst pro právě uloženou fikční postavu. Využij její jméno a nejvýše jeden neškodný veřejný fikční rys z bezpečného shrnutí, aniž prozradíš, co se chystá. Přidej suchý britský humor a drobnou narážku na Olivanderovu kocovinu.";

    internal static LaunchConfiguration Default => new(0, false, null, false, false, null, false,
        "Europe/Prague", DefaultTeaser, DefaultInitialLoadingMessage, true, DefaultPersonalizedLoadingPrompt);
}

internal sealed record LaunchConfigurationUpdate(long ExpectedRevision, bool BetaEnabled, long? BetaLaunchAt,
    bool BetaUnlocked, bool ProductionEnabled, long? ProductionLaunchAt, bool ProductionUnlocked,
    string TimeZone, string Teaser, string InitialLoadingMessage, bool PersonalizedLoadingEnabled,
    string PersonalizedLoadingPrompt);

internal sealed record EffectiveLaunchAccess(bool Allowed, string Audience, long? LaunchAt, long ServerNow,
    string Teaser, string InitialLoadingMessage, bool PersonalizedLoadingEnabled);
internal sealed record LaunchAuditEntry(string ActorId, long ChangedAt, long Revision, string ChangeSummary);
internal sealed record LaunchGroup(string Id, string Name, long? LaunchAt, long Revision);
internal sealed record LaunchGroupCreate(string Name, long? LaunchAt);
internal sealed record LaunchGroupUpdate(long ExpectedRevision, string Name, long? LaunchAt);
internal sealed record BetaAdmissionConfiguration(long Revision, string? GroupId, bool RecruitmentOpen,
    string? FixedUniverse);
internal sealed record BetaAdmissionUpdate(long ExpectedRevision, string? GroupId, bool RecruitmentOpen,
    string? FixedUniverse);
internal sealed record BetaAdmissionGroupRequest(long ExpectedRevision, string? GroupId = null);
internal sealed record BetaMember(string UserId, string DisplayName, long JoinedAt, bool AccessActive,
    long AccessRevision);
internal sealed record BetaMemberAccessUpdate(long ExpectedRevision, bool Active);

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
            CREATE TABLE IF NOT EXISTS launch_groups (
                id TEXT PRIMARY KEY, name TEXT NOT NULL COLLATE NOCASE UNIQUE,
                launch_at INTEGER NULL, revision INTEGER NOT NULL, updated_at INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS beta_admission (
                id INTEGER PRIMARY KEY CHECK(id=1), revision INTEGER NOT NULL,
                group_id TEXT NULL, recruitment_open INTEGER NOT NULL DEFAULT 0,
                fixed_universe TEXT NULL);
            INSERT OR IGNORE INTO beta_admission(id,revision,group_id,recruitment_open,fixed_universe)
                VALUES(1,1,NULL,0,NULL);
            INSERT OR IGNORE INTO launch_configuration VALUES
                (1,0,0,NULL,0,0,NULL,0,'Europe/Prague',
                 'A little something is getting ready for you.',
                 'A peculiar little surprise is taking shape…',0,
                 'Write one brief, playful, mysterious loading message inspired by the supplied fictional character context. Do not reveal product features or repeat research facts.');
            """;
        command.ExecuteNonQuery();
        using var defaults = connection.CreateCommand();
        defaults.CommandText = """
            UPDATE launch_configuration SET teaser=$teaser,initial_loading_message=$message,
                personalized_loading_enabled=1,personalized_loading_prompt=$prompt
            WHERE id=1 AND revision=0
                AND teaser='A little something is getting ready for you.'
                AND initial_loading_message='A peculiar little surprise is taking shape…'
                AND personalized_loading_enabled=0
                AND personalized_loading_prompt='Write one brief, playful, mysterious loading message inspired by the supplied fictional character context. Do not reveal product features or repeat research facts.';
            """;
        defaults.Parameters.AddWithValue("$teaser", LaunchConfiguration.DefaultTeaser);
        defaults.Parameters.AddWithValue("$message", LaunchConfiguration.DefaultInitialLoadingMessage);
        defaults.Parameters.AddWithValue("$prompt", LaunchConfiguration.DefaultPersonalizedLoadingPrompt);
        defaults.ExecuteNonQuery();
        using var previousDefaults = connection.CreateCommand();
        previousDefaults.CommandText = """
            UPDATE launch_configuration SET teaser=$teaser,initial_loading_message=$message,
                personalized_loading_prompt=$prompt
            WHERE id=1 AND revision=0
                AND teaser='Noční stolek pro velká očekávání. Olivander má kocovinu; na noční zázrak si chvíli počkáš.'
                AND initial_loading_message='Olivander má kocovinu; dnes stíháme připravit jen noční stolek. Na velké zázraky si ještě chvíli počkej.'
                AND personalized_loading_enabled=1
                AND personalized_loading_prompt='Napiš jednu krátkou českou větu, která postavu pozná přes jediný neškodný veřejný fikční rys z bezpečného shrnutí. Přidej suchý britský humor, slovní hříčku o nočním stolku a připomeň, že Olivander má kocovinu, takže se čeká. Neuváděj zdroje, citace, rešerše ani soukromé údaje.';
            """;
        previousDefaults.Parameters.AddWithValue("$teaser", LaunchConfiguration.DefaultTeaser);
        previousDefaults.Parameters.AddWithValue("$message", LaunchConfiguration.DefaultInitialLoadingMessage);
        previousDefaults.Parameters.AddWithValue("$prompt", LaunchConfiguration.DefaultPersonalizedLoadingPrompt);
        previousDefaults.ExecuteNonQuery();
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

    internal LaunchGroup[] ReadGroups()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,name,launch_at,revision FROM launch_groups ORDER BY name COLLATE NOCASE,id";
        using var reader = command.ExecuteReader();
        var groups = new List<LaunchGroup>();
        while (reader.Read()) { groups.Add(ReadGroup(reader)); }
        return groups.ToArray();
    }

    internal BetaAdmissionConfiguration ReadBetaAdmission()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT revision,group_id,recruitment_open,fixed_universe FROM beta_admission WHERE id=1";
        using var reader = command.ExecuteReader();
        if (!reader.Read()) { throw new InvalidOperationException("Beta admission configuration is unavailable."); }
        return new(reader.GetInt64(0), reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.GetBoolean(2), reader.IsDBNull(3) ? null : reader.GetString(3));
    }

    internal bool TryReadOpenBetaAdmission(out BetaAdmissionConfiguration admission, out LaunchGroup group)
    {
        admission = ReadBetaAdmission();
        if (admission.RecruitmentOpen && admission.GroupId is not null
            && ReadGroup(admission.GroupId) is { } selected)
        {
            group = selected;
            return true;
        }
        group = null!;
        return false;
    }

    internal BetaEnrollmentReservation? ReserveBetaEnrollment(string groupId, long revision)
    {
        var connection = Open();
        SqliteTransaction? transaction = null;
        try
        {
            // Keep configuration stable through the account creation transaction.
            transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT revision,group_id,recruitment_open,fixed_universe FROM beta_admission WHERE id=1";
            using var reader = command.ExecuteReader();
            if (!reader.Read() || reader.GetInt64(0) != revision || reader.IsDBNull(1)
                || reader.GetString(1) != groupId || !reader.GetBoolean(2))
            {
                transaction.Dispose();
                connection.Dispose();
                return null;
            }
            var admission = new BetaAdmissionConfiguration(reader.GetInt64(0), reader.GetString(1),
                reader.GetBoolean(2), reader.IsDBNull(3) ? null : reader.GetString(3));
            reader.Close();
            var group = ReadGroup(groupId);
            if (group is null)
            {
                transaction.Dispose();
                connection.Dispose();
                return null;
            }
            return new BetaEnrollmentReservation(connection, transaction, admission, group);
        }
        catch
        {
            transaction?.Dispose();
            connection.Dispose();
            throw;
        }
    }

    internal BetaAdmissionConfiguration UpdateBetaAdmission(string actorId, BetaAdmissionUpdate update, long now)
    {
        if (update.ExpectedRevision < 1 || update.FixedUniverse is { Length: > 100 }
            || update.FixedUniverse?.Any(char.IsControl) == true || update.FixedUniverse is { } universe && string.IsNullOrWhiteSpace(universe))
        { throw new LaunchConfigurationException("invalid_beta_admission", 400); }
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        if (update.GroupId is not null && !GroupExists(connection, transaction, update.GroupId))
        { throw new LaunchConfigurationException("launch_group_unavailable", 404); }
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE beta_admission SET revision=revision+1,group_id=$group,recruitment_open=$open,fixed_universe=$universe WHERE id=1 AND revision=$expected";
        command.Parameters.AddWithValue("$group", update.GroupId is null ? DBNull.Value : update.GroupId);
        command.Parameters.AddWithValue("$open", update.RecruitmentOpen && update.GroupId is not null);
        command.Parameters.AddWithValue("$universe", update.FixedUniverse is null ? DBNull.Value : update.FixedUniverse.Trim());
        command.Parameters.AddWithValue("$expected", update.ExpectedRevision);
        if (command.ExecuteNonQuery() != 1)
        { throw new LaunchConfigurationException("beta_admission_revision_conflict", 409); }
        InsertGroupAudit(connection, transaction, actorId, now, update.ExpectedRevision + 1, "beta_admission_updated");
        transaction.Commit();
        return ReadBetaAdmission();
    }

    internal (BetaAdmissionConfiguration Admission, LaunchGroup Group) CreateOrSelectBetaGroup(
        string actorId, BetaAdmissionGroupRequest request, long now)
    {
        if (request.ExpectedRevision < 1)
        { throw new LaunchConfigurationException("invalid_beta_admission", 400); }
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        var current = ReadBetaAdmission(connection, transaction);
        if (current.Revision != request.ExpectedRevision)
        { throw new LaunchConfigurationException("beta_admission_revision_conflict", 409); }

        LaunchGroup group;
        if (request.GroupId is { Length: > 0 } groupId)
        {
            group = ReadGroup(connection, transaction, groupId)
                ?? throw new LaunchConfigurationException("launch_group_unavailable", 404);
        }
        else
        {
            var existing = ReadGroupByName(connection, transaction, "Interní beta");
            if (existing is null)
            {
                group = new LaunchGroup(Guid.NewGuid().ToString("N"), "Interní beta", null, 1);
                using var create = connection.CreateCommand();
                create.Transaction = transaction;
                create.CommandText = "INSERT INTO launch_groups(id,name,launch_at,revision,updated_at) VALUES($id,$name,NULL,1,$now)";
                create.Parameters.AddWithValue("$id", group.Id);
                create.Parameters.AddWithValue("$name", group.Name);
                create.Parameters.AddWithValue("$now", now);
                create.ExecuteNonQuery();
            }
            else
            {
                group = existing;
            }
        }

        using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = "UPDATE beta_admission SET revision=revision+1,group_id=$group,recruitment_open=0,fixed_universe=NULL WHERE id=1 AND revision=$expected";
        update.Parameters.AddWithValue("$group", group.Id);
        update.Parameters.AddWithValue("$expected", request.ExpectedRevision);
        if (update.ExecuteNonQuery() != 1)
        { throw new LaunchConfigurationException("beta_admission_revision_conflict", 409); }
        InsertGroupAudit(connection, transaction, actorId, now, request.ExpectedRevision + 1, "beta_admission_group_selected");
        transaction.Commit();
        return (ReadBetaAdmission(), group);
    }

    internal void AuditBetaMemberAccess(string actorId, string groupId, string accountId, bool active,
        long accessRevision, long now)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        InsertGroupAudit(connection, transaction, actorId, now, accessRevision,
            $"beta_member_access_{(active ? "restored" : "revoked")}:{groupId}:{accountId}");
        transaction.Commit();
    }

    internal void AuditBetaMemberAdmitted(string actorId, string groupId, long accessRevision, long now)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        InsertGroupAudit(connection, transaction, actorId, now, accessRevision,
            $"beta_member_admitted:{groupId}");
        transaction.Commit();
    }

    private static BetaAdmissionConfiguration ReadBetaAdmission(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT revision,group_id,recruitment_open,fixed_universe FROM beta_admission WHERE id=1";
        using var reader = command.ExecuteReader();
        if (!reader.Read()) { throw new InvalidOperationException("Beta admission configuration is unavailable."); }
        return new(reader.GetInt64(0), reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.GetBoolean(2), reader.IsDBNull(3) ? null : reader.GetString(3));
    }

    private static LaunchGroup? ReadGroup(SqliteConnection connection, SqliteTransaction transaction, string id)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT id,name,launch_at,revision FROM launch_groups WHERE id=$id";
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadGroup(reader) : null;
    }

    private static LaunchGroup? ReadGroupByName(SqliteConnection connection, SqliteTransaction transaction, string name)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT id,name,launch_at,revision FROM launch_groups WHERE name=$name COLLATE NOCASE";
        command.Parameters.AddWithValue("$name", name);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadGroup(reader) : null;
    }

    internal static bool GroupExists(SqliteConnection connection, SqliteTransaction transaction, string id)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT 1 FROM launch_groups WHERE id=$id";
        command.Parameters.AddWithValue("$id", id);
        return command.ExecuteScalar() is not null;
    }

    internal LaunchGroup? ReadGroup(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 64) { return null; }
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,name,launch_at,revision FROM launch_groups WHERE id=$id";
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadGroup(reader) : null;
    }

    internal LaunchGroup CreateGroup(string actorId, LaunchGroupCreate request, long now)
    {
        ValidateGroup(request.Name, request.LaunchAt);
        var group = new LaunchGroup(Guid.NewGuid().ToString("N"), request.Name.Trim(), request.LaunchAt, 1);
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO launch_groups(id,name,launch_at,revision,updated_at) VALUES($id,$name,$launch,1,$now)";
        command.Parameters.AddWithValue("$id", group.Id);
        command.Parameters.AddWithValue("$name", group.Name);
        AddNullable(command, "$launch", group.LaunchAt);
        command.Parameters.AddWithValue("$now", now);
        try { command.ExecuteNonQuery(); }
        catch (SqliteException error) when (error.SqliteErrorCode == 19)
        { throw new LaunchConfigurationException("launch_group_name_conflict", 409); }
        InsertGroupAudit(connection, transaction, actorId, now, 1, "launch_group_created");
        transaction.Commit();
        return group;
    }

    internal LaunchGroup UpdateGroup(string actorId, string id, LaunchGroupUpdate request, long now)
    {
        ValidateGroup(request.Name, request.LaunchAt);
        if (request.ExpectedRevision < 1) { throw new LaunchConfigurationException("invalid_launch_group", 400); }
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE launch_groups SET name=$name,launch_at=$launch,revision=revision+1,updated_at=$now WHERE id=$id AND revision=$expected";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$name", request.Name.Trim());
        AddNullable(command, "$launch", request.LaunchAt);
        command.Parameters.AddWithValue("$now", now);
        command.Parameters.AddWithValue("$expected", request.ExpectedRevision);
        try
        {
            if (command.ExecuteNonQuery() != 1)
            { throw new LaunchConfigurationException("launch_group_revision_conflict", 409); }
        }
        catch (SqliteException error) when (error.SqliteErrorCode == 19)
        { throw new LaunchConfigurationException("launch_group_name_conflict", 409); }
        InsertGroupAudit(connection, transaction, actorId, now, request.ExpectedRevision + 1, "launch_group_updated");
        transaction.Commit();
        return ReadGroup(id)!;
    }

    private static LaunchGroup ReadGroup(SqliteDataReader reader) => new(reader.GetString(0), reader.GetString(1),
        NullableInt64(reader, 2), reader.GetInt64(3));

    private static void ValidateGroup(string? name, long? launchAt)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 80 || name.Any(char.IsControl)
            || launchAt is < 0 or > 253402300799999)
        { throw new LaunchConfigurationException("invalid_launch_group", 400); }
    }

    private static void InsertGroupAudit(SqliteConnection connection, SqliteTransaction transaction,
        string actorId, long now, long revision, string summary)
    {
        using var audit = connection.CreateCommand();
        audit.Transaction = transaction;
        audit.CommandText = "INSERT INTO launch_audit(actor_id,changed_at,revision,change_summary) VALUES($actor,$now,$revision,$summary)";
        audit.Parameters.AddWithValue("$actor", actorId);
        audit.Parameters.AddWithValue("$now", now);
        audit.Parameters.AddWithValue("$revision", revision);
        audit.Parameters.AddWithValue("$summary", summary);
        audit.ExecuteNonQuery();
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
            || !string.IsNullOrWhiteSpace(update.Teaser) && !PortraitContentPolicy.ValidLoadingMessage(update.Teaser)
            || update.InitialLoadingMessage is null || update.InitialLoadingMessage.Trim().Length is 0 or > 160
            || !PortraitContentPolicy.ValidLoadingMessage(update.InitialLoadingMessage)
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

internal sealed class BetaEnrollmentReservation(SqliteConnection connection, SqliteTransaction transaction,
    BetaAdmissionConfiguration admission, LaunchGroup group) : IDisposable
{
    internal BetaAdmissionConfiguration Admission { get; } = admission;
    internal LaunchGroup Group { get; } = group;

    public void Dispose()
    {
        transaction.Dispose();
        connection.Dispose();
    }
}

internal sealed class LaunchAccessService(LaunchPolicyStore store, TimeProvider timeProvider,
    IServiceScopeFactory? scopeFactory = null)
{
    internal LaunchConfiguration Configuration => store.Read();

    internal EffectiveLaunchAccess Evaluate(ClaimsPrincipal principal)
        => Evaluate(principal, null, null);

    internal EffectiveLaunchAccess Evaluate(ClaimsPrincipal principal, string? groupId, LaunchGroup? group)
        => EvaluateGroupAware(principal, groupId, group);

    internal async Task<EffectiveLaunchAccess> EvaluateAsync(ClaimsPrincipal principal)
    {
        if (AccountRoles.IsStaff(principal) || principal.IsInRole(AccountRoles.Tester)) { return Evaluate(principal); }
        var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId) || scopeFactory is null) { return Evaluate(principal); }
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AccountDatabase>();
        var accountAccess = await db.Users.AsNoTracking().Where(user => user.Id == userId)
            .Select(user => new { user.LaunchGroupId, user.LaunchGroupRevokedAt }).SingleOrDefaultAsync();
        if (accountAccess?.LaunchGroupRevokedAt is not null)
        {
            var now = timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
            var config = store.Read();
            return new(false, "revoked", null, now, config.Teaser, config.InitialLoadingMessage,
                config.PersonalizedLoadingEnabled);
        }
        var groupId = accountAccess?.LaunchGroupId;
        if (groupId is null) { return Evaluate(principal); }
        return EvaluateGroupAware(principal, groupId, store.ReadGroup(groupId));
    }

    private EffectiveLaunchAccess EvaluateGroupAware(ClaimsPrincipal principal, string? groupId, LaunchGroup? group)
    {
        var now = timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var config = store.Read();
        var teaser = string.IsNullOrWhiteSpace(config.Teaser) || PortraitContentPolicy.ValidLoadingMessage(config.Teaser)
            ? config.Teaser : LaunchConfiguration.DefaultTeaser;
        var initialLoadingMessage = PortraitContentPolicy.ValidLoadingMessage(config.InitialLoadingMessage)
            ? config.InitialLoadingMessage : LaunchConfiguration.DefaultInitialLoadingMessage;
        if (AccountRoles.IsStaff(principal) || principal.IsInRole(AccountRoles.Tester))
        {
            return new(true, "privileged", null, now, teaser, initialLoadingMessage, config.PersonalizedLoadingEnabled);
        }

        var beta = principal.IsInRole(AccountRoles.Beta);
        if (groupId is not null)
        {
            var schedule = group is not null && group.Id == groupId ? group.LaunchAt : null;
            var groupAllowed = schedule is not null && now >= schedule.Value;
            return new(groupAllowed, beta ? "beta" : "production", schedule, now,
                teaser, initialLoadingMessage, config.PersonalizedLoadingEnabled);
        }
        var enabled = beta ? config.BetaEnabled : config.ProductionEnabled;
        var launchAt = beta ? config.BetaLaunchAt : config.ProductionLaunchAt;
        var manuallyUnlocked = beta ? config.BetaUnlocked : config.ProductionUnlocked;
        var allowed = !enabled || manuallyUnlocked || launchAt is not null && now >= launchAt;
        return new(allowed, beta ? "beta" : "production", enabled ? launchAt : null, now,
            teaser, initialLoadingMessage, config.PersonalizedLoadingEnabled);
    }
}
