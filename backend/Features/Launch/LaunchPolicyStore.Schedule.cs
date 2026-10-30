namespace Kufr.Backend.Features.Launch;

internal sealed partial class LaunchPolicyStore
{
    internal void OpenDueGroups(long now)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE launch_groups SET opened_at=$now,revision=revision+1,updated_at=$now
            WHERE opened_at IS NULL AND launch_at IS NOT NULL AND launch_at<=$now;
            """;
        command.Parameters.AddWithValue("$now", now);
        command.ExecuteNonQuery();
    }
}

// Opening is permanent. A later recruitment term only belongs to its selected group.
internal sealed class LaunchScheduleStartup(LaunchPolicyStore store, TimeProvider clock) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        store.OpenDueGroups(clock.GetUtcNow().ToUnixTimeMilliseconds());
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
