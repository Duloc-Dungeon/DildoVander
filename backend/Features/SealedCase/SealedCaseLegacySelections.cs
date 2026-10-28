using System.Text.Json;

namespace Kufr.Backend.Features.SealedCase;

internal sealed partial class SealedCaseStore
{
    // Add explicit invalidations to legacy, unfrozen lobbies. Never capture a new
    // current revision on upgrade or rewrite an already frozen selection/history.
    private void InvalidateLegacySelections()
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        using var query = connection.CreateCommand();
        query.Transaction = transaction;
        query.CommandText = """
            SELECT event.case_id,event.actor_id FROM sealed_case_events event
            JOIN sealed_cases ca ON ca.id=event.case_id
            WHERE event.kind='lobby' AND ca.status IN ('setup','lobby')
                AND NOT EXISTS(SELECT 1 FROM sealed_case_rosters r WHERE r.case_id=event.case_id)
                AND event.revision=(SELECT MAX(latest.revision) FROM sealed_case_events latest
                    WHERE latest.case_id=event.case_id AND latest.actor_id=event.actor_id AND latest.kind='lobby')
                AND json_extract(event.payload_json,'$.ready')=1
                AND json_extract(event.payload_json,'$.consent')=1
                AND json_extract(event.payload_json,'$.snapshotRef') IS NULL
            ORDER BY event.case_id,event.actor_id
            """;
        var invalid = new List<(string CaseId, string AccountId)>();
        using (var reader = query.ExecuteReader())
        {
            while (reader.Read()) { invalid.Add((reader.GetString(0), reader.GetString(1))); }
        }
        foreach (var (caseId, accountId) in invalid)
        {
            using var append = connection.CreateCommand();
            append.Transaction = transaction;
            append.CommandText = """
                INSERT INTO sealed_case_events(event_id,case_id,actor_id,idempotency_key,expected_revision,
                    revision,kind,payload_json,created_at)
                SELECT $id,id,$account,$id,revision,revision+1,'lobby',$payload,created_at FROM sealed_cases WHERE id=$case;
                UPDATE sealed_cases SET revision=revision+1 WHERE id=$case;
                """;
            append.Parameters.AddWithValue("$id", "legacy_reselect_" + Guid.NewGuid().ToString("N"));
            append.Parameters.AddWithValue("$account", accountId);
            append.Parameters.AddWithValue("$case", caseId);
            append.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(new
            {
                playerId = accountId, ready = false, consent = false, requiresReselection = true
            }, Json));
            append.ExecuteNonQuery();
        }
        transaction.Commit();
    }
}
