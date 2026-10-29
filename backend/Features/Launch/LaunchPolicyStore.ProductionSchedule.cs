using Microsoft.Data.Sqlite;

namespace Kufr.Backend.Features.Launch;

internal sealed partial class LaunchPolicyStore
{
    internal void ReconcileProductionGroups(long now)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        SynchronizeProductionGroups(connection, transaction, ReadInside(connection, transaction), "system", now);
        transaction.Commit();
    }

    private static void SynchronizeProductionGroups(SqliteConnection connection, SqliteTransaction transaction,
        LaunchConfiguration configuration, string actorId, long now)
    {
        var ids = new List<string>();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT id FROM launch_groups";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                ids.Add(reader.GetString(0));
            }
        }
        foreach (var id in ids)
        {
            SynchronizeProductionGroup(connection, transaction, configuration,
                ReadGroup(connection, transaction, id)!, actorId, now);
        }
    }

    private static void SynchronizeProductionGroup(SqliteConnection connection, SqliteTransaction transaction,
        LaunchConfiguration configuration, LaunchGroup group, string actorId, long now)
    {
        // Membership and recruitment select the audience; production alone controls opening.
        var launchAt = configuration.ProductionEnabled ? configuration.ProductionLaunchAt : null;
        var open = !configuration.ProductionEnabled || configuration.ProductionUnlocked
            || launchAt is not null && launchAt <= now;
        long? openedAt = open ? group.OpenedAt ?? now : null;
        var forceStart = configuration.ProductionEnabled && !configuration.ProductionUnlocked && launchAt is not null;
        // A confirmation marker keeps this inherited absolute term out of the
        // legacy unconfirmed-countdown migration. Preserve it on an unchanged term.
        long? confirmedAt = launchAt is null ? null
            : group.LaunchAt == launchAt ? group.CountdownConfirmedAt ?? now : now;
        if (group.LaunchAt == launchAt && group.OpenedAt == openedAt && group.ForceStartAtEnd == forceStart
            && group.CountdownConfirmedAt == confirmedAt && group.CountdownDurationSeconds is null)
        {
            return;
        }

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE launch_groups SET launch_at=$launch,opened_at=$opened,force_start_at_end=$force,
                countdown_confirmed_at=$confirmed,countdown_duration_seconds=NULL,
                revision=revision+1,updated_at=$now WHERE id=$id;
            """;
        AddNullable(command, "$launch", launchAt);
        AddNullable(command, "$opened", openedAt);
        AddNullable(command, "$confirmed", confirmedAt);
        command.Parameters.AddWithValue("$force", forceStart);
        command.Parameters.AddWithValue("$now", now);
        command.Parameters.AddWithValue("$id", group.Id);
        command.ExecuteNonQuery();
        InsertGroupAudit(connection, transaction, actorId, now, group.Revision + 1,
            "launch_group_production_schedule_synchronized");
    }
}

// Runs once before serving requests; repairs every existing group without
// requiring an administrator to save the already configured production term.
internal sealed class LaunchScheduleStartup(LaunchPolicyStore store, TimeProvider clock) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        store.ReconcileProductionGroups(clock.GetUtcNow().ToUnixTimeMilliseconds());
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
