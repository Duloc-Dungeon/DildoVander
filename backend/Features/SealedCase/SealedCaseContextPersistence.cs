using System.Text.Json;

namespace Kufr.Backend.Features.SealedCase;

// Created only by server case/roster authority. This is not a client contract.
// Grant provisioning/revocation belongs to the roster integration, not a second
// competing membership table in the projection layer.
internal sealed record SealedContextAuthority(string CaseId, string GroupId, SealedContextMember[] Members,
    bool FinaleReleased = false, string[]? ReleasedEventRefs = null);

internal sealed partial class SealedCaseStore
{
    internal SealedStoredRoster ReadRoster(string caseId, string groupId)
    {
        using var c = Open(); using var tx = c.BeginTransaction(deferred: true);
        _ = ReadCaseRevision(c, tx, caseId, groupId);
        using var cmd = c.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "SELECT server_seed,roster_json,frozen_at FROM sealed_case_rosters WHERE case_id=$id";
        cmd.Parameters.AddWithValue("$id", caseId);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) throw new SealedCasePersistenceException("case_roster_not_frozen", 409);
        var roster = new SealedStoredRoster(caseId, reader.GetString(0), JsonSerializer.Deserialize<SealedStoredRosterMember[]>(reader.GetString(1), Json)!, reader.GetInt64(2));
        reader.Close(); tx.Commit(); return roster;
    }

    internal SealedContextAuthority ReadRosterAuthority(string caseId, string groupId)
    {
        var roster = ReadRoster(caseId, groupId);
        return new(caseId, groupId, roster.Members.Select(m => new SealedContextMember(m.AccountId, m.PlayerId,
            [SealedContextRole.Player, SealedContextRole.PrivateNarrator, SealedContextRole.ChoiceGenerator])).ToArray());
    }

    // All source data comes from one read transaction over B1's canonical store.
    // No raw checkpoint, seed, outcome, revision or error reason leaves this API.
    // Production routes remain fail-closed until C1 supplies durable authority and
    // observation/presentation writers supply their authorized context records.
    internal SealedContextFrame ReadContext(string caseId, string groupId, string authenticatedAccountId,
        SealedContextRole role, SealedContextAuthority authority, Func<IDisposable>? authorizeRead = null)
    {
        var member = SealedContextService.DemandAccess(caseId, groupId, authenticatedAccountId, role, authority);
        using var c = Open(); using var tx = c.BeginTransaction(deferred: true);
        using var admission = authorizeRead?.Invoke();
        var revision = ReadCaseRevision(c, tx, caseId, groupId);
        SealedStoredWorld world;
        try { world = ReadWorld(c, tx, caseId, revision); }
        catch (SealedCasePersistenceException e) when (e.Code == "world_not_initialized")
        { throw new SealedCasePersistenceException("case_not_found", 404); }
        if (member.PlayerId is { } playerId && !world.Checkpoint.Players.Any(x => x.Id == playerId))
            throw new SealedCasePersistenceException("case_not_found", 404);
        var worldLog = new SealedWorldLog();
        var orders = new List<SealedCovertOrder>();
        using var cmd = c.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "SELECT orders_json,events_json FROM sealed_case_resolutions WHERE case_id=$id ORDER BY chapter";
        cmd.Parameters.AddWithValue("$id", caseId);
        using (var r = cmd.ExecuteReader())
        {
            while (r.Read())
            {
                var actions = JsonSerializer.Deserialize<SealedAction[]>(r.GetString(0), Json)!;
                var resolution = JsonSerializer.Deserialize<SealedWorldResolution>(r.GetString(1), Json)!;
                foreach (var e in resolution.Events)
                {
                    // These are references into the single canonical event log,
                    // not independently authored copies of world facts.
                    worldLog.Append(new(e.GetProperty("id").GetString()!, e.GetProperty("chapter").GetInt32(),
                        e.GetProperty("actor").GetString() ?? "", e.GetProperty("target").GetString(),
                        e.GetProperty("intent").GetString()!, e.GetProperty("result").GetString()!, new Dictionary<string,string>(), null));
                }
                foreach (var a in actions)
                {
                    var refs = resolution.Events.Where(e => e.GetProperty("actor").GetString() == a.ActorId)
                        .Select(e => e.GetProperty("id").GetString()!).ToArray();
                    // The command is the player's own input, not an outcome or an
                    // eligibility oracle. Preserve condition/fallback/revision too.
                    // The committed resolution acknowledges processing of this
                    // own command, independent of event refs/outcome/provenance.
                    // It does not grant an observed result or causal association.
                    orders.Add(new(a.Id, a.ActorId, JsonSerializer.Serialize(a, Json), a.TargetId, true, refs)
                    { ExecutionAcknowledged = true });
                }
            }
        }
        var known = world.Checkpoint.Players.ToDictionary(x => x.Id, x => x.KnownPlayers, StringComparer.Ordinal);
        var service = new SealedContextService(caseId, groupId, worldLog, [], [], orders, authority.Members,
            known, authority.FinaleReleased, authority.ReleasedEventRefs);
        var frame = service.Project(caseId, groupId, authenticatedAccountId, role);
        tx.Commit(); return frame;
    }
}
