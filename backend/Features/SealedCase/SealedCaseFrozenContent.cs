using System.Text.Json;
using Kufr.Backend.Features.Customers;
using Kufr.Backend.Features.Identity;

namespace Kufr.Backend.Features.SealedCase;

// Server-only material for the authenticated member's frozen selection. Never
// serialize this record through the lobby, context or public roster contracts.
internal sealed record SealedFrozenCharacter(SealedStoredRosterMember Member, CharacterProfile Profile,
    CharacterPortrait? Portrait, byte[]? Image);

internal sealed partial class SealedCaseStore
{
    internal SealedFrozenCharacter ResolveFrozenCharacter(string caseId, string groupId, string accountId,
        Func<CustomerAdmissionGuard> authorize)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: true);
        CustomerAdmissionGuard? memberAdmission = null;
        using var admission = AcquireCaseAccess(connection, transaction, caseId,
            () => memberAdmission = authorize());
        var proof = memberAdmission!.Proof;
        if (proof.AccountId != accountId || proof.LaunchGroupId != groupId || proof.LaunchGroupRevokedAt is not null
            || !proof.Roles.Contains(AccountRoles.Customer, StringComparer.Ordinal))
        {
            throw new SealedCasePersistenceException("case_not_found", 404);
        }
        _ = ReadCaseRevision(connection, transaction, caseId, groupId);
        using var rosterQuery = connection.CreateCommand();
        rosterQuery.Transaction = transaction;
        rosterQuery.CommandText = "SELECT roster_json FROM sealed_case_rosters WHERE case_id=$case";
        rosterQuery.Parameters.AddWithValue("$case", caseId);
        if (rosterQuery.ExecuteScalar() is not string rosterJson)
        {
            throw new SealedCasePersistenceException("case_roster_not_frozen", 409);
        }
        var member = JsonSerializer.Deserialize<SealedStoredRosterMember[]>(rosterJson, Json)!
            .SingleOrDefault(candidate => candidate.AccountId == accountId);
        if (member?.SnapshotRef is null)
        {
            throw new SealedCasePersistenceException("case_not_found", 404);
        }
        using var snapshot = connection.CreateCommand();
        snapshot.Transaction = transaction;
        snapshot.CommandText = """
            SELECT profile_json,portrait_json,image FROM sealed_case_private_snapshots
            WHERE case_id=$case AND snapshot_ref=$reference AND subject_account=$account
                AND profile_id=$profile AND profile_revision=$revision
            """;
        snapshot.Parameters.AddWithValue("$case", caseId);
        snapshot.Parameters.AddWithValue("$reference", member.SnapshotRef);
        snapshot.Parameters.AddWithValue("$account", accountId);
        snapshot.Parameters.AddWithValue("$profile", member.ProfileId);
        snapshot.Parameters.AddWithValue("$revision", member.ProfileRevision);
        using var reader = snapshot.ExecuteReader();
        if (!reader.Read())
        {
            throw new SealedCasePersistenceException("case_not_found", 404);
        }
        var result = new SealedFrozenCharacter(member,
            JsonSerializer.Deserialize<CharacterProfile>(reader.GetString(0), Json)!,
            reader.IsDBNull(1) ? null : JsonSerializer.Deserialize<CharacterPortrait>(reader.GetString(1), Json),
            reader.IsDBNull(2) ? null : (byte[])reader[2]);
        reader.Close();
        transaction.Commit();
        return result;
    }
}
