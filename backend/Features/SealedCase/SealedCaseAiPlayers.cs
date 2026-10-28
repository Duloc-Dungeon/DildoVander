using System.Security.Cryptography;
using System.Text.Json;
using Kufr.Backend.Features.Identity;
using Microsoft.Data.Sqlite;

namespace Kufr.Backend.Features.SealedCase;

internal sealed record SealedAddAiPlayersRequest(string OperationId, long ExpectedRevision, int Count);
internal sealed record SealedAiCase(string Id, string Group, string Owner, string[] Players);

internal sealed partial class SealedCaseStore
{
    private static void InitializeAiPlayersSchema(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS sealed_case_ai_players(
                case_id TEXT NOT NULL REFERENCES sealed_cases(id), player_id TEXT NOT NULL,
                display_name TEXT NOT NULL, finished INTEGER NOT NULL DEFAULT 0, PRIMARY KEY(case_id,player_id));
            CREATE TABLE IF NOT EXISTS sealed_case_ai_turns(
                case_id TEXT NOT NULL REFERENCES sealed_cases(id), player_id TEXT NOT NULL,
                turn_key TEXT NOT NULL, started_at INTEGER NOT NULL, completed INTEGER NOT NULL DEFAULT 0, decision_json TEXT NULL,
                PRIMARY KEY(case_id,player_id,turn_key));
            """;
        command.ExecuteNonQuery();
    }

    private static Dictionary<string, string> AiPlayers(SqliteConnection connection, SqliteTransaction transaction, string caseId)
    {
        using var query = connection.CreateCommand(); query.Transaction = transaction;
        query.CommandText = "SELECT player_id,display_name FROM sealed_case_ai_players WHERE case_id=$case ORDER BY player_id";
        query.Parameters.AddWithValue("$case", caseId); using var reader = query.ExecuteReader();
        var players = new Dictionary<string, string>(StringComparer.Ordinal);
        while (reader.Read()) { players.Add(reader.GetString(0), reader.GetString(1)); }
        return players;
    }

    // Only the background runner can supply an account admission for a bot.
    // A human cookie never grants a caller another player's projection.
    private static IDisposable DemandAiOrPlayerAdmission(SqliteConnection connection, SqliteTransaction transaction,
        string caseId, string group, string actor, Func<IDisposable> authorize, bool ai, bool allowOwner = false)
    {
        var admission = authorize();
        try
        {
            if (!ai && admission is CustomerAdmissionGuard human)
            {
                if (human.Proof.AccountId == actor && human.Proof.LaunchGroupId == group
                    && human.Proof.LaunchGroupRevokedAt is null && (human.Proof.Roles.Contains(AccountRoles.Customer, StringComparer.Ordinal) || allowOwner && human.Proof.Roles.Contains(AccountRoles.Owner, StringComparer.Ordinal)))
                { return admission; }
            }
            if (ai && admission is CustomerAccountAdmission owner)
            {
                DemandAiOwner(connection, transaction, caseId, group, owner.Proof);
                if (AiPlayers(connection, transaction, caseId).ContainsKey(actor)) { return admission; }
            }
            throw new SealedCasePersistenceException("case_not_found", 404);
        }
        catch { admission.Dispose(); throw; }
    }

    private static void DemandAiOwner(SqliteConnection connection, SqliteTransaction transaction,
        string caseId, string group, CustomerAdmissionProof proof)
    {
        using var query = connection.CreateCommand(); query.Transaction = transaction;
        query.CommandText = "SELECT json_extract(setup_json,'$.createdBy') FROM sealed_cases WHERE id=$case AND group_id=$group AND status!='privacy_terminated'";
        query.Parameters.AddWithValue("$case", caseId); query.Parameters.AddWithValue("$group", group);
        if (proof.AccountId != (string?)query.ExecuteScalar() || proof.LaunchGroupId != group
            || proof.LaunchGroupRevokedAt is not null || !proof.Roles.Contains(AccountRoles.Owner, StringComparer.Ordinal))
        { throw new SealedCasePersistenceException("sealed_case_owner_required", 403); }
    }

    internal SealedCaseActionReceipt AddAiPlayers(string caseId, string group, string actor, SealedAddAiPlayersRequest request,
        Func<CustomerAdmissionGuard> authorize, Func<IReadOnlySet<string>> eligibleMembers, bool providerAvailable = true)
    {
        if (string.IsNullOrWhiteSpace(request.OperationId) || request.OperationId.Length is < 8 or > 128)
        { throw new SealedCasePersistenceException("invalid_operation_id", 400); }
        if (request.Count is < 1 or > 30)
        { throw new SealedCasePersistenceException("sealed_case_invalid_ai_count", 400); }
        using var connection = Open(); using var transaction = connection.BeginTransaction(deferred: false);
        CustomerAdmissionGuard? owner = null;
        using var admission = AcquireCaseAccess(connection, transaction, caseId, () => owner = DemandPlayerAdmission(authorize, actor, group, allowOwner: true), actor);
        DemandAiOwner(connection, transaction, caseId, group, owner!.Proof);
        var revision = ReadCaseRevision(connection, transaction, caseId, group);
        var fingerprint = SealedScenario.Digest("ai-add", "request", JsonSerializer.Serialize(request, Json));
        using var query = connection.CreateCommand(); query.Transaction = transaction;
        query.CommandText = "SELECT event_id,revision,kind,client_fingerprint FROM sealed_case_events WHERE case_id=$case AND actor_id=$actor AND idempotency_key=$key";
        query.Parameters.AddWithValue("$case", caseId); query.Parameters.AddWithValue("$actor", actor); query.Parameters.AddWithValue("$key", request.OperationId);
        using (var previous = query.ExecuteReader())
        {
            if (previous.Read())
            {
                if (previous.GetString(2) != "ai-players-added" || previous.IsDBNull(3) || previous.GetString(3) != fingerprint)
                { throw new SealedCasePersistenceException("idempotency_key_reused", 409); }
                var replay = new SealedCaseActionReceipt(previous.GetString(0), previous.GetInt64(1), true, "lobby");
                previous.Close(); transaction.Commit(); return replay;
            }
        }
        if (!providerAvailable) { throw new SealedCasePersistenceException("sealed_case_ai_unavailable", 503); }
        if (request.ExpectedRevision != revision) { throw new SealedCasePersistenceException("revision_conflict", 409); }
        query.CommandText = "SELECT status,json_extract(setup_json,'$.expectedPlayers') FROM sealed_cases WHERE id=$case";
        int capacity; string status;
        using (var row = query.ExecuteReader()) { row.Read(); status = row.GetString(0); capacity = row.GetInt32(1); }
        if (status is not ("setup" or "lobby") || RosterMembers(connection, transaction, caseId).Length != 0)
        { throw new SealedCasePersistenceException("sealed_case_lobby_locked", 409); }
        var eligible = eligibleMembers(); var bots = AiPlayers(connection, transaction, caseId);
        var ready = LobbyEntries(connection, transaction, caseId).Values.Count(p => p.Ready && p.Consent && p.SnapshotRef is not null
            && (eligible.Contains(p.PlayerId) || bots.ContainsKey(p.PlayerId)));
        if (ready + request.Count > capacity) { throw new SealedCasePersistenceException("sealed_case_lobby_full", 409); }
        var added = new List<string>();
        for (var index = 0; index < request.Count; index++)
        {
            var id = "ai_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant(); added.Add(id);
            using var add = connection.CreateCommand(); add.Transaction = transaction;
            add.CommandText = "INSERT INTO sealed_case_ai_players(case_id,player_id,display_name) VALUES($case,$player,$name)";
            add.Parameters.AddWithValue("$case", caseId); add.Parameters.AddWithValue("$player", id);
            add.Parameters.AddWithValue("$name", "Svědek " + (bots.Count + index + 1)); add.ExecuteNonQuery();
        }
        var eventId = "sca_" + Guid.NewGuid().ToString("N");
        query.CommandText = """
            UPDATE sealed_cases SET revision=$next,status='lobby' WHERE id=$case;
            INSERT INTO sealed_case_events(event_id,case_id,actor_id,idempotency_key,expected_revision,revision,kind,payload_json,created_at,client_fingerprint)
                VALUES($event,$case,$actor,$key,$old,$next,'ai-players-added',$payload,$now,$fingerprint);
            """;
        query.Parameters.AddWithValue("$event", eventId); query.Parameters.AddWithValue("$old", revision); query.Parameters.AddWithValue("$next", revision + 1);
        query.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(new { aiPlayers = added }, Json));
        query.Parameters.AddWithValue("$now", clock.GetUtcNow().ToUnixTimeSeconds()); query.Parameters.AddWithValue("$fingerprint", fingerprint); query.ExecuteNonQuery();
        FreezeRosterIfFull(connection, transaction, caseId, capacity, clock.GetUtcNow().ToUnixTimeSeconds(), eligible);
        transaction.Commit(); return new(eventId, revision + 1, false, "lobby");
    }
}
