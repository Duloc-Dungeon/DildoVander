namespace Kufr.Backend.Features.Customers;

internal sealed partial class CharacterProfileStore
{
    private readonly object workSync = new();
    private readonly Dictionary<string, HashSet<CancellationTokenSource>> activeWork = new();

    internal CharacterWorkLease BeginAccountWork(string accountId, CancellationToken token)
    {
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        lock (workSync)
        {
            if (!activeWork.TryGetValue(accountId, out var work))
            {
                activeWork[accountId] = work = [];
            }
            work.Add(cancellation);
        }
        return new CharacterWorkLease(cancellation, () =>
        {
            lock (workSync)
            {
                if (activeWork.TryGetValue(accountId, out var work))
                {
                    work.Remove(cancellation);
                    if (work.Count == 0) { activeWork.Remove(accountId); }
                }
            }
        });
    }

    // Called while the account authority is reserved. Preserve completed data, but
    // invalidate leases so a late provider response cannot resurrect revoked work.
    internal void SuspendAccountWork(string accountId)
    {
        lock (workSync)
        {
            if (activeWork.TryGetValue(accountId, out var work))
            {
                foreach (var cancellation in work) { cancellation.Cancel(); }
            }
        }
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE character_profiles SET status='saved',error_code='membership_access_revoked'
                WHERE account_id=$account AND status IN ('queued','running');
            DELETE FROM character_jobs WHERE account_id=$account;
            UPDATE character_portraits SET status='failed',error_code='membership_access_revoked',
                lease_token=NULL,lease_until=0
                WHERE account_id=$account AND status IN ('queued','running');
            """;
        command.Parameters.AddWithValue("$account", accountId);
        command.ExecuteNonQuery();
        transaction.Commit();
    }
}

internal sealed class CharacterWorkLease(CancellationTokenSource cancellation, Action release) : IDisposable
{
    internal CancellationToken Token => cancellation.Token;
    public void Dispose()
    {
        release();
        cancellation.Dispose();
    }
}
