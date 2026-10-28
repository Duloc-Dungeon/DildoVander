using Kufr.Backend.Features.CustomerData;
using Kufr.Backend.Features.Identity;
using Microsoft.Data.Sqlite;

internal static partial class CustomerAdmissionTests
{
    private static async Task TransientSqliteReservationsAsync(Fixture fixture)
    {
        var owner = await fixture.AddAsync(AccountRoles.Customer);
        using (var blocker = new SqliteConnection(fixture.ConnectionString))
        {
            blocker.Open();
            using var held = blocker.BeginTransaction(deferred: false);
            using var started = new ManualResetEventSlim();
            var acquisition = Task.Run(() =>
            {
                started.Set();
                return fixture.Authority.AcquireCookie(fixture.Context(owner));
            });
            Check(started.Wait(TimeSpan.FromSeconds(2)), "contended admission starts");
            await Task.Delay(1400);
            held.Rollback();
            using var recovered = await acquisition.WaitAsync(TimeSpan.FromSeconds(3));
            Check(recovered.Proof.AccountId == owner.Account,
                "admission recovers when a transient writer outlasts the first SQLite wait");
        }

        // A deletion committed while admission waits must win over the caller's
        // old authenticated principal. Retrying never reuses an earlier proof.
        var deleting = await fixture.AddAsync(AccountRoles.Customer);
        using (var blocker = new SqliteConnection(fixture.ConnectionString))
        {
            blocker.Open();
            using var held = blocker.BeginTransaction(deferred: false);
            using var delete = blocker.CreateCommand();
            delete.Transaction = held;
            delete.CommandText = "INSERT INTO DeletionRequests(UserId,ReceiptId,RequestedAt,Attempts) VALUES($id,'retry-deletion',1,0)";
            delete.Parameters.AddWithValue("$id", deleting.Account);
            delete.ExecuteNonQuery();
            using var started = new ManualResetEventSlim();
            var acquisition = Task.Run(() =>
            {
                started.Set();
                try { using var guard = fixture.Authority.AcquireCookie(fixture.Context(deleting)); return (Exception?)null; }
                catch (Exception error) { return error; }
            });
            Check(started.Wait(TimeSpan.FromSeconds(2)), "deletion-race admission starts");
            await Task.Delay(1400);
            held.Commit();
            var error = await acquisition.WaitAsync(TimeSpan.FromSeconds(3));
            Check(error is CustomerDataAccessException { Status: 401 },
                "admission observes deletion committed before a successful retry");
        }

        using (var blocker = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = fixture.Profiles.CustomerDataDatabasePath }.ToString()))
        {
            blocker.Open();
            using var held = blocker.BeginTransaction(deferred: false);
            using var cancellation = new CancellationTokenSource();
            using var started = new ManualResetEventSlim();
            var acquisition = Task.Run(() =>
            {
                started.Set();
                try { using var guard = fixture.Authority.AcquireCookie(fixture.Context(owner), token: cancellation.Token); return (Exception?)null; }
                catch (Exception error) { return error; }
            });
            Check(started.Wait(TimeSpan.FromSeconds(2)), "cancelable contended admission starts");
            cancellation.CancelAfter(100);
            var error = await acquisition.WaitAsync(TimeSpan.FromSeconds(3));
            Check(error is OperationCanceledException, "cancellation prevents another SQLite reservation attempt");
            // The failed character reservation must release the identity writer.
            await fixture.ExecuteAsync("UPDATE Sessions SET LastSeenAt=LastSeenAt+1 WHERE UserId=$id", owner.Account);
        }

        var deadline = new CustomerDataGuardDeadline(CancellationToken.None);
        using (var account = CustomerDataSqliteGuard.Acquire(fixture.ConnectionString, deadline))
        {
            await Task.Delay(5100);
            var characters = new SqliteConnectionStringBuilder
                { DataSource = fixture.Profiles.CustomerDataDatabasePath }.ToString();
            Expect(503, () => CustomerDataSqliteGuard.Acquire(characters, deadline).Dispose());
        }
        Console.WriteLine("PASS transient SQLite admission recovery, concurrent deletion, cancellation/unwind and shared deadline");
    }
}
