using Kufr.Backend.Features.CustomerData;
using Kufr.Backend.Features.Customers;
using Kufr.Backend.Features.Identity;
using Microsoft.Data.Sqlite;

namespace Kufr.Backend.Features.SealedCase;

internal sealed record SealedCaseSubject(string AccountId, string? ProfileId);

// Every case operation reserves case -> identity -> characters. Reuse admission's
// reservations rather than opening a second writer against the same database.
internal sealed class SealedCasePrivacyAuthority(string accountConnectionString, CharacterProfileStore profiles)
{
    internal IDisposable Acquire(SealedCaseSubject[] subjects, Func<IDisposable>? authorize)
    {
        IDisposable? admission = null;
        CustomerDataSqliteGuard? ownedAccount = null;
        CustomerDataSqliteGuard? ownedCharacter = null;
        try
        {
            admission = authorize?.Invoke();
            var deadline = new CustomerDataGuardDeadline(CancellationToken.None);
            var account = admission switch
            {
                CustomerAdmissionGuard held => held.AccountReservation,
                CustomerAccountAdmission held => held.AccountReservation,
                _ => ownedAccount = CustomerDataSqliteGuard.Acquire(accountConnectionString, deadline)
            };
            var character = admission is CustomerAdmissionGuard capture
                ? capture.CharacterReservation : ownedCharacter = profiles.OpenCustomerDataGuard(deadline);
            foreach (var subject in subjects)
            {
                using var identity = account.Command("SELECT 1 FROM DeletionRequests WHERE UserId=$account");
                identity.Parameters.AddWithValue("$account", subject.AccountId);
                using var deletion = character.Command("""
                    SELECT 1 FROM character_account_fences WHERE account_id=$account
                    UNION ALL SELECT 1 FROM character_deletions WHERE account_id=$account
                        AND (completed=0 OR profile_id='*' OR profile_id=$profile) LIMIT 1
                    """);
                deletion.Parameters.AddWithValue("$account", subject.AccountId);
                deletion.Parameters.AddWithValue("$profile", (object?)subject.ProfileId ?? DBNull.Value);
                if (identity.ExecuteScalar() is not null || deletion.ExecuteScalar() is not null)
                {
                    throw new SealedCasePersistenceException("case_not_found", 404);
                }
            }
            return new Reservation(admission, ownedAccount, ownedCharacter);
        }
        catch
        {
            ownedCharacter?.Dispose();
            ownedAccount?.Dispose();
            admission?.Dispose();
            throw;
        }
    }

    private sealed class Reservation(IDisposable? admission, IDisposable? account, IDisposable? character) : IDisposable
    {
        public void Dispose()
        {
            try { character?.Dispose(); }
            finally
            {
                try { account?.Dispose(); }
                finally { admission?.Dispose(); }
            }
        }
    }
}

internal sealed partial class SealedCaseStore
{
    private IDisposable? AcquireCaseAccess(SqliteConnection connection, SqliteTransaction transaction,
        string caseId, Func<IDisposable>? authorize = null, string? incomingAccountId = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT json_extract(setup_json,'$.createdBy'),NULL FROM sealed_cases WHERE id=$case
            UNION SELECT actor_id,NULL FROM sealed_case_events WHERE case_id=$case
            UNION SELECT subject_account,profile_id FROM sealed_case_private_snapshots WHERE case_id=$case
            UNION SELECT json_extract(member.value,'$.accountId'),json_extract(member.value,'$.profileId')
                FROM sealed_case_rosters roster,json_each(roster.roster_json) member WHERE roster.case_id=$case
            """;
        command.Parameters.AddWithValue("$case", caseId);
        var subjects = new List<SealedCaseSubject>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                if (!reader.IsDBNull(0))
                {
                    subjects.Add(new(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1)));
                }
            }
        }
        if (incomingAccountId is not null) { subjects.Add(new(incomingAccountId, null)); }
        return privacy is null ? authorize?.Invoke() : privacy.Acquire(subjects.ToArray(), authorize);
    }
}
