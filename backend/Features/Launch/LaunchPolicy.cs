using System.Security.Claims;
using Kufr.Backend.Features.Customers;
using Kufr.Backend.Features.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Kufr.Backend.Features.Launch;

internal sealed record LaunchConfiguration(long Revision, string TimeZone, bool PersonalizedLoadingEnabled,
    string PersonalizedLoadingPrompt)
{
    internal const string DefaultPersonalizedLoadingPrompt =
        "Napiš jednu krátkou českou větu jako napínavou předzvěst pro právě uloženou fikční postavu. Využij její jméno a nejvýše jeden neškodný veřejný fikční rys z bezpečného shrnutí, aniž prozradíš, co se chystá. Pointu založ na konkrétním rozporu, antiklimaxu nebo českém rčení; kocovina není povinný motiv.";

    internal static LaunchConfiguration Default => new(0, "Europe/Prague", true, DefaultPersonalizedLoadingPrompt);
}

internal sealed record LaunchConfigurationUpdate(long ExpectedRevision, string TimeZone,
    bool PersonalizedLoadingEnabled, string PersonalizedLoadingPrompt);
internal sealed record EffectiveLaunchAccess(bool Allowed, string Audience, long? LaunchAt, long ServerNow,
    bool PersonalizedLoadingEnabled);
internal sealed record LaunchAuditEntry(string ActorId, long ChangedAt, long Revision, string ChangeSummary);
internal sealed record LaunchGroup(string Id, string Name, long? LaunchAt, long Revision, long? OpenedAt,
    bool AdultContentEnabled = true, bool FlirtEnabled = true, long? ChronicleClosedAt = null);
internal sealed record LaunchGroupCreate(string Name, bool? AdultContentEnabled = null, bool? FlirtEnabled = null);
internal sealed record LaunchGroupUpdate(long ExpectedRevision, string Name,
    bool? AdultContentEnabled = null, bool? FlirtEnabled = null);
internal sealed record LaunchGroupRevision(long ExpectedRevision);
internal sealed record BetaAdmissionConfiguration(long Revision, string? GroupId, bool RecruitmentOpen,
    string? FixedUniverse, long? GroupRevision = null, long? GroupLaunchAt = null);
internal sealed record BetaAdmissionUpdate(long ExpectedRevision, string? GroupId, bool RecruitmentOpen,
    string? FixedUniverse, long? ExpectedGroupRevision, long? LaunchAt);
internal sealed record BetaAdmissionGroupRequest(long ExpectedRevision, string? GroupId = null);
internal sealed record BetaMember(string UserId, string DisplayName, long JoinedAt, bool AccessActive,
    long AccessRevision);
internal sealed record BetaMemberAccessUpdate(long ExpectedRevision, bool Active);

internal sealed class LaunchConfigurationException(string code, int status) : Exception(code)
{
    internal string Code { get; } = code;
    internal int Status { get; } = status;
}

internal sealed partial class LaunchPolicyStore
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
            """;
        command.ExecuteNonQuery();
        EnsureGroupOpeningColumn(connection);
        EnsureGroupContentColumns(connection);
        using var setup = connection.CreateCommand();
        setup.CommandText = """
            CREATE TABLE IF NOT EXISTS beta_admission (
                id INTEGER PRIMARY KEY CHECK(id=1), revision INTEGER NOT NULL,
                group_id TEXT NULL, recruitment_open INTEGER NOT NULL DEFAULT 0,
                fixed_universe TEXT NULL);
            INSERT OR IGNORE INTO beta_admission(id,revision,group_id,recruitment_open,fixed_universe)
                VALUES(1,1,NULL,0,NULL);
            INSERT OR IGNORE INTO launch_configuration VALUES
                (1,0,0,NULL,0,0,NULL,0,'Europe/Prague','','',1,$prompt);
            """;
        // Required historical columns remain storage-only compatibility fields.
        setup.Parameters.AddWithValue("$prompt", LaunchConfiguration.DefaultPersonalizedLoadingPrompt);
        setup.ExecuteNonQuery();
    }

    private static void EnsureGroupOpeningColumn(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(launch_groups)";
        using var reader = command.ExecuteReader();
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read()) { columns.Add(reader.GetString(1)); }
        reader.Close();
        // Old timer columns remain untouched in existing databases. Only the absolute
        // opening time and opened marker are read by the current policy.
        if (!columns.Contains("opened_at"))
        {
            command.CommandText = "ALTER TABLE launch_groups ADD COLUMN opened_at INTEGER NULL";
            command.ExecuteNonQuery();
        }
    }

    private static void EnsureGroupContentColumns(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(launch_groups)";
        using var reader = command.ExecuteReader();
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read()) { columns.Add(reader.GetString(1)); }
        reader.Close();
        using var migration = connection.CreateCommand();
        if (!columns.Contains("adult_content_enabled"))
        { migration.CommandText += "ALTER TABLE launch_groups ADD COLUMN adult_content_enabled INTEGER NOT NULL DEFAULT 1;"; }
        if (!columns.Contains("flirt_enabled"))
        { migration.CommandText += "ALTER TABLE launch_groups ADD COLUMN flirt_enabled INTEGER NOT NULL DEFAULT 1;"; }
        if (!string.IsNullOrEmpty(migration.CommandText)) { migration.ExecuteNonQuery(); }
    }

    internal LaunchConfiguration Read()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT revision,time_zone,personalized_loading_enabled,personalized_loading_prompt FROM launch_configuration WHERE id=1";
        using var reader = command.ExecuteReader();
        if (!reader.Read()) { throw new InvalidOperationException("Launch configuration is unavailable."); }
        return new(reader.GetInt64(0), reader.GetString(1), reader.GetBoolean(2), reader.GetString(3));
    }

    internal LaunchGroup[] ReadGroups()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,name,launch_at,revision,opened_at,adult_content_enabled,flirt_enabled FROM launch_groups ORDER BY name COLLATE NOCASE,id";
        using var reader = command.ExecuteReader();
        var groups = new List<LaunchGroup>();
        while (reader.Read()) { groups.Add(ReadGroup(reader)); }
        return groups.ToArray();
    }

    internal BetaAdmissionConfiguration ReadBetaAdmission()
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        return ReadBetaAdmission(connection, transaction);
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
            command.CommandText = "SELECT a.revision,a.group_id,a.recruitment_open,a.fixed_universe,g.revision,g.launch_at FROM beta_admission a LEFT JOIN launch_groups g ON g.id=a.group_id WHERE a.id=1";
            using var reader = command.ExecuteReader();
            if (!reader.Read() || reader.GetInt64(0) != revision || reader.IsDBNull(1)
                || reader.GetString(1) != groupId || !reader.GetBoolean(2))
            {
                transaction.Dispose();
                connection.Dispose();
                return null;
            }
            var admission = new BetaAdmissionConfiguration(reader.GetInt64(0), reader.GetString(1),
                reader.GetBoolean(2), reader.IsDBNull(3) ? null : reader.GetString(3),
            NullableInt64(reader, 4), NullableInt64(reader, 5));
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

    internal WaitingRoomMoveReservation? ReserveWaitingRoomMove(string? sourceGroupId,
        string targetGroupId)
    {
        var connection = Open();
        SqliteTransaction? transaction = null;
        try
        {
            transaction = connection.BeginTransaction();
            if (!LockGroup(connection, transaction, targetGroupId))
            {
                transaction.Dispose();
                connection.Dispose();
                return null;
            }
            if (sourceGroupId is not null) { _ = LockGroup(connection, transaction, sourceGroupId); }
            var source = sourceGroupId is null ? null : ReadGroup(connection, transaction, sourceGroupId);
            return new WaitingRoomMoveReservation(connection, transaction, source);
        }
        catch
        {
            transaction?.Dispose();
            connection.Dispose();
            throw;
        }
    }

    private static bool LockGroup(SqliteConnection connection, SqliteTransaction transaction, string id)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE launch_groups SET updated_at=updated_at WHERE id=$id";
        command.Parameters.AddWithValue("$id", id);
        return command.ExecuteNonQuery() == 1;
    }

    internal BetaAdmissionConfiguration UpdateBetaAdmission(string actorId, BetaAdmissionUpdate update, long now)
    {
        if (update.ExpectedRevision < 1 || update.FixedUniverse is { Length: > 100 }
            || update.FixedUniverse?.Any(char.IsControl) == true
            || update.FixedUniverse is { } universe && string.IsNullOrWhiteSpace(universe)
            || update.LaunchAt is < 0 or > 253402300799999
            || update.GroupId is null && (update.RecruitmentOpen || update.LaunchAt is not null || update.ExpectedGroupRevision is not null))
        { throw new LaunchConfigurationException("invalid_beta_admission", 400); }
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        var admission = ReadBetaAdmission(connection, transaction);
        if (admission.Revision != update.ExpectedRevision)
        { throw new LaunchConfigurationException("beta_admission_revision_conflict", 409); }
        if (update.GroupId is { } groupId)
        {
            var group = ReadGroup(connection, transaction, groupId)
                ?? throw new LaunchConfigurationException("launch_group_unavailable", 404);
            if (group.Revision != update.ExpectedGroupRevision)
            { throw new LaunchConfigurationException("launch_group_revision_conflict", 409); }
            if (update.RecruitmentOpen && update.LaunchAt is null && group.OpenedAt is null)
            { throw new LaunchConfigurationException("invalid_beta_admission", 400); }
            // A term that has elapsed is already open even before its marker is persisted.
            var alreadyOpen = group.OpenedAt is not null || group.LaunchAt is { } due && due <= now;
            if (alreadyOpen && group.LaunchAt != update.LaunchAt)
            { throw new LaunchConfigurationException("launch_group_already_open", 409); }
            using var schedule = connection.CreateCommand();
            schedule.Transaction = transaction;
            schedule.CommandText = """
                UPDATE launch_groups SET launch_at=$launch,opened_at=$opened,
                    revision=revision+1,updated_at=$now WHERE id=$group AND revision=$expected;
                """;
            AddNullable(schedule, "$launch", update.LaunchAt);
            AddNullable(schedule, "$opened", group.OpenedAt ?? (update.LaunchAt is { } term && term <= now ? now : null));
            schedule.Parameters.AddWithValue("$group", groupId);
            schedule.Parameters.AddWithValue("$expected", update.ExpectedGroupRevision!);
            schedule.Parameters.AddWithValue("$now", now);
            if (schedule.ExecuteNonQuery() != 1)
            { throw new LaunchConfigurationException("launch_group_revision_conflict", 409); }
        }
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE beta_admission SET revision=revision+1,group_id=$group,recruitment_open=$open,fixed_universe=$universe WHERE id=1 AND revision=$expected";
        command.Parameters.AddWithValue("$group", update.GroupId is null ? DBNull.Value : update.GroupId);
        command.Parameters.AddWithValue("$open", update.RecruitmentOpen);
        command.Parameters.AddWithValue("$universe", update.FixedUniverse is null ? DBNull.Value : update.FixedUniverse.Trim());
        command.Parameters.AddWithValue("$expected", update.ExpectedRevision);
        if (command.ExecuteNonQuery() != 1)
        { throw new LaunchConfigurationException("beta_admission_revision_conflict", 409); }
        InsertGroupAudit(connection, transaction, actorId, now, update.ExpectedRevision + 1, "beta_admission_updated");
        var result = ReadBetaAdmission(connection, transaction);
        transaction.Commit();
        return result;
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
            var existing = ReadGroupByName(connection, transaction, "Noví hosté");
            if (existing is null)
            {
                group = new LaunchGroup(Guid.NewGuid().ToString("N"), "Noví hosté", null, 1, null);
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
        group = ReadGroup(connection, transaction, group.Id)!;
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
        command.CommandText = "SELECT a.revision,a.group_id,a.recruitment_open,a.fixed_universe,g.revision,g.launch_at FROM beta_admission a LEFT JOIN launch_groups g ON g.id=a.group_id WHERE a.id=1";
        using var reader = command.ExecuteReader();
        if (!reader.Read()) { throw new InvalidOperationException("Beta admission configuration is unavailable."); }
        return new(reader.GetInt64(0), reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.GetBoolean(2), reader.IsDBNull(3) ? null : reader.GetString(3),
            NullableInt64(reader, 4), NullableInt64(reader, 5));
    }

    private static LaunchGroup? ReadGroup(SqliteConnection connection, SqliteTransaction transaction, string id)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT id,name,launch_at,revision,opened_at,adult_content_enabled,flirt_enabled FROM launch_groups WHERE id=$id";
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadGroup(reader) : null;
    }

    private static LaunchGroup? ReadGroupByName(SqliteConnection connection, SqliteTransaction transaction, string name)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT id,name,launch_at,revision,opened_at,adult_content_enabled,flirt_enabled FROM launch_groups WHERE name=$name COLLATE NOCASE";
        command.Parameters.AddWithValue("$name", name);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadGroup(reader) : null;
    }

    internal LaunchGroup? ReadGroup(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 64) { return null; }
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,name,launch_at,revision,opened_at,adult_content_enabled,flirt_enabled FROM launch_groups WHERE id=$id";
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadGroup(reader) : null;
    }

    internal LaunchGroup CreateGroup(string actorId, LaunchGroupCreate request, long now)
    {
        ValidateGroup(request.Name);
        var group = new LaunchGroup(Guid.NewGuid().ToString("N"), request.Name.Trim(), null, 1, null, request.AdultContentEnabled ?? true, request.FlirtEnabled ?? true);
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO launch_groups(id,name,launch_at,revision,updated_at,adult_content_enabled,flirt_enabled) VALUES($id,$name,NULL,1,$now,$adult,$flirt)";
        command.Parameters.AddWithValue("$id", group.Id);
        command.Parameters.AddWithValue("$name", group.Name);
        command.Parameters.AddWithValue("$now", now);
        command.Parameters.AddWithValue("$adult", group.AdultContentEnabled);
        command.Parameters.AddWithValue("$flirt", group.FlirtEnabled);
        try { command.ExecuteNonQuery(); }
        catch (SqliteException error) when (error.SqliteErrorCode == 19)
        { throw new LaunchConfigurationException("launch_group_name_conflict", 409); }
        InsertGroupAudit(connection, transaction, actorId, now, 1, "launch_group_created");
        group = ReadGroup(connection, transaction, group.Id)!;
        transaction.Commit();
        return group;
    }

    internal LaunchGroup UpdateGroup(string actorId, string id, LaunchGroupUpdate request, long now)
    {
        ValidateGroup(request.Name);
        if (request.ExpectedRevision < 1) { throw new LaunchConfigurationException("invalid_launch_group", 400); }
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        var current = ReadGroup(connection, transaction, id)
            ?? throw new LaunchConfigurationException("launch_group_unavailable", 404);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        var adultContentEnabled = request.AdultContentEnabled ?? current.AdultContentEnabled;
        var flirtEnabled = request.FlirtEnabled ?? current.FlirtEnabled;
        command.CommandText = "UPDATE launch_groups SET name=$name,adult_content_enabled=$adult,flirt_enabled=$flirt,revision=revision+1,updated_at=$now WHERE id=$id AND revision=$expected";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$name", request.Name.Trim());
        command.Parameters.AddWithValue("$now", now);
        command.Parameters.AddWithValue("$expected", request.ExpectedRevision);
        command.Parameters.AddWithValue("$adult", adultContentEnabled);
        command.Parameters.AddWithValue("$flirt", flirtEnabled);
        try
        {
            if (command.ExecuteNonQuery() != 1)
            { throw new LaunchConfigurationException("launch_group_revision_conflict", 409); }
        }
        catch (SqliteException error) when (error.SqliteErrorCode == 19)
        { throw new LaunchConfigurationException("launch_group_name_conflict", 409); }
        InsertGroupAudit(connection, transaction, actorId, now, request.ExpectedRevision + 1, "launch_group_updated");
        var updated = ReadGroup(connection, transaction, id)!;
        transaction.Commit();
        return updated;
    }

    internal LaunchGroup? OpenDueGroup(string id, long now)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE launch_groups SET opened_at=$now,revision=revision+1,updated_at=$now
            WHERE id=$id AND opened_at IS NULL AND launch_at IS NOT NULL AND launch_at<=$now;
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$now", now);
        command.ExecuteNonQuery();
        return ReadGroup(id);
    }

    private static LaunchGroup ReadGroup(SqliteDataReader reader) => new(reader.GetString(0), reader.GetString(1),
        NullableInt64(reader, 2), reader.GetInt64(3), NullableInt64(reader, 4), reader.GetBoolean(5), reader.GetBoolean(6));

    private static void ValidateGroup(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 80 || name.Any(char.IsControl))
        { throw new LaunchConfigurationException("invalid_launch_group", 400); }
    }

    internal void AuditBetaMemberMoved(string actorId, string accountId, string? sourceGroupId,
        string? targetGroupId, long revision, long now)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        InsertGroupAudit(connection, transaction, actorId, now, revision,
            $"waiting_room_member_moved:{accountId}:{sourceGroupId ?? "unassigned"}:{targetGroupId ?? "unassigned"}");
        transaction.Commit();
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
            UPDATE launch_configuration SET revision=revision+1,time_zone=$tz,
                personalized_loading_enabled=$personalized,personalized_loading_prompt=$prompt
                WHERE id=1 AND revision=$expected;
            """;
        command.Parameters.AddWithValue("$tz", update.TimeZone.Trim());
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
        if (old.TimeZone != update.TimeZone) { yield return "time_zone"; }
        if (old.PersonalizedLoadingEnabled != update.PersonalizedLoadingEnabled || old.PersonalizedLoadingPrompt != update.PersonalizedLoadingPrompt) { yield return "personalization"; }
    }

    private static LaunchConfiguration ReadInside(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT revision,time_zone,personalized_loading_enabled,personalized_loading_prompt FROM launch_configuration WHERE id=1";
        using var reader = command.ExecuteReader();
        reader.Read();
        return new(reader.GetInt64(0), reader.GetString(1), reader.GetBoolean(2), reader.GetString(3));
    }

    private static void Validate(LaunchConfigurationUpdate update)
    {
        if (update.ExpectedRevision < 0 || !ValidTimeZone(update.TimeZone)
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

internal sealed class WaitingRoomMoveReservation(SqliteConnection connection, SqliteTransaction transaction,
    LaunchGroup? source) : IDisposable
{
    internal bool SourceStarted(long now) => source?.OpenedAt is not null
        || source?.LaunchAt is not null && source.LaunchAt <= now;

    public void Dispose()
    {
        transaction.Dispose();
        connection.Dispose();
    }
}

internal sealed class LaunchAccessService(LaunchPolicyStore store, TimeProvider timeProvider,
    IServiceScopeFactory? scopeFactory = null)
{
    private sealed record AccountLaunchSnapshot(string? GroupId, long? RevokedAt, long Revision);

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
        var initial = await ReadAccountLaunchSnapshotAsync(db, userId);
        var result = initial?.RevokedAt is not null
            ? DenyForChangedMembership(principal, initial)
            : initial?.GroupId is { } groupId
                ? EvaluateGroupAware(principal, groupId, store.ReadGroup(groupId))
                : Evaluate(principal);
        var current = await ReadAccountLaunchSnapshotAsync(db, userId);
        if (!SameAccountLaunchSnapshot(initial, current))
        { return DenyForChangedMembership(principal, current); }
        return result;
    }

    private static async Task<AccountLaunchSnapshot?> ReadAccountLaunchSnapshotAsync(AccountDatabase db,
        string userId)
    {
        var account = await db.Users.AsNoTracking().Where(user => user.Id == userId)
            .Select(user => new { user.LaunchGroupId, user.LaunchGroupRevokedAt, user.LaunchGroupAccessRevision })
            .SingleOrDefaultAsync();
        return account is null ? null
            : new(account.LaunchGroupId, account.LaunchGroupRevokedAt, account.LaunchGroupAccessRevision);
    }

    private static bool SameAccountLaunchSnapshot(AccountLaunchSnapshot? first,
        AccountLaunchSnapshot? second)
        => first?.GroupId == second?.GroupId && first?.RevokedAt == second?.RevokedAt
            && first?.Revision == second?.Revision;

    private EffectiveLaunchAccess DenyForChangedMembership(ClaimsPrincipal principal,
        AccountLaunchSnapshot? current)
    {
        var baseline = EvaluateGroupAware(principal, null, null);
        var audience = current?.RevokedAt is not null ? "revoked"
            : principal.IsInRole(AccountRoles.Beta) ? "beta" : "production";
        return baseline with { Allowed = false, Audience = audience, LaunchAt = null };
    }

    private EffectiveLaunchAccess EvaluateGroupAware(ClaimsPrincipal principal, string? groupId, LaunchGroup? group)
    {
        var now = timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var config = store.Read();
        if (AccountRoles.IsStaff(principal) || principal.IsInRole(AccountRoles.Tester))
        {
            return new(true, "privileged", null, now, config.PersonalizedLoadingEnabled);
        }
        var current = groupId is not null && group?.Id == groupId ? store.OpenDueGroup(groupId, now) : null;
        return new(current?.OpenedAt is not null, principal.IsInRole(AccountRoles.Beta) ? "beta" : "production",
            current?.LaunchAt, now, config.PersonalizedLoadingEnabled);
    }
}
