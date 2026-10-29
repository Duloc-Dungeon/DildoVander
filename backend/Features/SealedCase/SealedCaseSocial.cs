using System.Text.Json;
using Kufr.Backend.Features.Identity;
using Microsoft.Data.Sqlite;

namespace Kufr.Backend.Features.SealedCase;

internal sealed record SealedBallot(int Round, string[] Nominees);
internal sealed partial class SealedCaseStore
{
    private static SealedStatement[] Statements(SqliteConnection connection, SqliteTransaction transaction, string caseId, string? viewer = null)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT payload_json FROM (SELECT revision,payload_json FROM sealed_case_events WHERE case_id=$case AND kind='statement' AND ($viewer IS NULL OR actor_id=$viewer OR json_extract(payload_json,'$.public')=1 OR EXISTS (SELECT 1 FROM json_each(payload_json,'$.recipients') WHERE value=$viewer)) ORDER BY revision DESC LIMIT 100) ORDER BY revision";
        command.Parameters.AddWithValue("$case", caseId); command.Parameters.AddWithValue("$viewer", (object?)viewer ?? DBNull.Value);
        using var reader = command.ExecuteReader(); var messages = new List<SealedStatement>();
        while (reader.Read()) { messages.Add(JsonSerializer.Deserialize<SealedStatement>(reader.GetString(0), Json)!); } return messages.ToArray();
    }
    private static SealedRecap[] Recap(SqliteConnection connection, SqliteTransaction transaction, string caseId)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT events_json FROM sealed_case_resolutions WHERE case_id=$case ORDER BY chapter";
        command.Parameters.AddWithValue("$case", caseId); using var reader = command.ExecuteReader(); var recap = new List<SealedRecap>();
        while (reader.Read())
        {
            var resolution = JsonSerializer.Deserialize<SealedWorldResolution>(reader.GetString(0), Json)!;
            recap.AddRange(resolution.Results.Select(r => new SealedRecap(r.Chapter, r.ActorId, r.TargetId, r.Intent.ToString(), r.Result.ToString(), caseId + ":" + r.Id, r.Reason, r.Modifiers, r.Rolls, r.TraceCreated)));
        }
        return recap.ToArray();
    }

    internal SealedCaseActionReceipt PostStatement(string caseId, string group, string actor, string operationId,
        string body, string[] recipients, bool isPublic, Func<IDisposable> authorize)
    {
        body = body.Trim();
        if (body.Length is < 1 or > 1200 || body.Any(c => char.IsControl(c) && c is not '\n' and not '\r' and not '\t') || recipients.Length > 30
            || recipients.Distinct(StringComparer.Ordinal).Count() != recipients.Length || isPublic && recipients.Length != 0)
        { throw new SealedCasePersistenceException("sealed_case_invalid_statement", 400); }
        var now = clock.GetUtcNow().ToUnixTimeSeconds();
        return AppendSocial(caseId, group, actor, operationId, "statement", new { body, recipients, isPublic }, authorize,
            (connection, transaction, revision) =>
            {
                var members = RosterMembers(connection, transaction, caseId);
                if (recipients.Any(id => members.All(m => m.PlayerId != id)) || !isPublic && recipients.Length == 0)
                { throw new SealedCasePersistenceException("sealed_case_invalid_audience", 400); }
                return new SealedStatement("msg_" + Guid.NewGuid().ToString("N"), actor, body, recipients, isPublic, now);
            });
    }

    internal SealedCaseActionReceipt Vote(string caseId, string group, string actor, string operationId,
        SealedBallot ballot, Func<IDisposable> authorize)
        => AppendSocial(caseId, group, actor, operationId, "ballot", ballot, authorize, (connection, transaction, revision) =>
        {
            if (Disconnected(connection, transaction, caseId).Contains(actor)) { throw new SealedCasePersistenceException("sealed_case_disconnected", 409); }
            var timing = ReadTiming(connection, transaction, caseId, revision, clock.GetUtcNow().ToUnixTimeSeconds());
            var members = RosterMembers(connection, transaction, caseId);
            var finale = ReadFinale(connection, transaction, caseId, members, actor, timing);
            if (ballot.Round == 1 && finale.Phase != "ballot" || ballot.Round == 2 && finale.Phase != "runoff" || ballot.Round is not (1 or 2))
            { throw new SealedCasePersistenceException("sealed_case_ballot_closed", 409); }
            try
            {
                if (ballot.Round == 1) { SealedCaseRules.ValidateVote(members.Select(m => m.PlayerId).ToArray(), actor, ballot.Nominees); }
                else if (ballot.Nominees.Length != 0 && (ballot.Nominees.Length != finale.RunoffPlaces
                    || ballot.Nominees.Distinct(StringComparer.Ordinal).Count() != ballot.Nominees.Length
                    || ballot.Nominees.Any(id => id == actor || !finale.RunoffCandidates.Contains(id))))
                { throw new SealedCaseRuleException("Invalid runoff ballot."); }
            }
            catch (SealedCaseRuleException) { throw new SealedCasePersistenceException("sealed_case_invalid_ballot", 400); }
            return ballot;
        });

    private SealedCaseActionReceipt AppendSocial(string caseId, string group, string actor, string operationId, string kind,
        object request, Func<IDisposable> authorize, Func<SqliteConnection, SqliteTransaction, long, object> validate, bool allowOperator = false)
    {
        if (string.IsNullOrWhiteSpace(operationId) || operationId.Length is < 8 or > 128) { throw new SealedCasePersistenceException("invalid_operation_id", 400); }
        var fingerprint = SealedScenario.Digest("request", kind, JsonSerializer.Serialize(request, Json));
        using var connection = Open(); using var transaction = connection.BeginTransaction(deferred: false);
        using var admission = AcquireCaseAccess(connection, transaction, caseId, () =>
        {
            var held = authorize();
            return DemandAiOrPlayerAdmission(connection, transaction, caseId, group, actor, () => held, held is CustomerAccountAdmission, allowOwner: allowOperator);
        }, actor);
        var revision = ReadCaseRevision(connection, transaction, caseId, group);
        if (!allowOperator) { DemandRosterMember(connection, transaction, caseId, actor); }
        using var previous = connection.CreateCommand(); previous.Transaction = transaction;
        previous.CommandText = "SELECT event_id,revision,kind,client_fingerprint FROM sealed_case_events WHERE case_id=$case AND actor_id=$actor AND idempotency_key=$key";
        previous.Parameters.AddWithValue("$case", caseId); previous.Parameters.AddWithValue("$actor", actor); previous.Parameters.AddWithValue("$key", operationId);
        using (var reader = previous.ExecuteReader())
        {
            if (reader.Read())
            {
                if (reader.GetString(2) != kind || reader.IsDBNull(3) || reader.GetString(3) != fingerprint)
                { throw new SealedCasePersistenceException("idempotency_key_reused", 409); }
                var result = new SealedCaseActionReceipt(reader.GetString(0), reader.GetInt64(1), true, "running"); reader.Close(); transaction.Commit(); return result;
            }
        }
        var payload = validate(connection, transaction, revision);
        var id = "scv_" + Guid.NewGuid().ToString("N");
        using var append = connection.CreateCommand(); append.Transaction = transaction;
        append.CommandText = """
            INSERT INTO sealed_case_events(event_id,case_id,actor_id,idempotency_key,expected_revision,revision,kind,payload_json,created_at,client_fingerprint)
                VALUES($event,$case,$actor,$key,$old,$next,$kind,$payload,$now,$fingerprint);
            UPDATE sealed_cases SET revision=$next WHERE id=$case;
            """;
        append.Parameters.AddWithValue("$event", id); append.Parameters.AddWithValue("$case", caseId); append.Parameters.AddWithValue("$actor", actor);
        append.Parameters.AddWithValue("$key", operationId); append.Parameters.AddWithValue("$old", revision); append.Parameters.AddWithValue("$next", revision + 1);
        append.Parameters.AddWithValue("$kind", kind); append.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(payload, Json));
        append.Parameters.AddWithValue("$now", clock.GetUtcNow().ToUnixTimeSeconds()); append.Parameters.AddWithValue("$fingerprint", fingerprint);
        append.ExecuteNonQuery(); transaction.Commit(); return new(id, revision + 1, false, "running");
    }

    private static Dictionary<string, string[]> Ballots(SqliteConnection connection, SqliteTransaction transaction, string caseId, int round)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT actor_id,payload_json FROM sealed_case_events WHERE case_id=$case AND kind='ballot' ORDER BY revision";
        command.Parameters.AddWithValue("$case", caseId); using var reader = command.ExecuteReader(); var ballots = new Dictionary<string, string[]>(StringComparer.Ordinal);
        while (reader.Read())
        {
            var ballot = JsonSerializer.Deserialize<SealedBallot>(reader.GetString(1), Json)!;
            if (ballot.Round == round) { ballots[reader.GetString(0)] = ballot.Nominees; }
        }
        foreach (var disconnected in Disconnected(connection, transaction, caseId)) { ballots.Remove(disconnected); }
        return ballots;
    }

    private static SealedFinaleView ReadFinale(SqliteConnection connection, SqliteTransaction transaction, string caseId,
        SealedStoredRosterMember[] members, string actor, SealedTimingSnapshot timing)
    {
        var schedule = SealedCaseRules.Schedule(timing.Variant, timing.OriginalPlayers);
        var finaleStarts = timing.FinaleEndsAt - schedule.FinaleSeconds;
        var firstDeadline = finaleStarts + schedule.FinaleSeconds * 2 / 3;
        var time = timing.PausedAt ?? timing.ServerNow;
        var first = Ballots(connection, transaction, caseId, 1); var second = Ballots(connection, transaction, caseId, 2);
        var ids = members.Select(m => m.PlayerId).ToArray(); var ballotSize = SealedCaseRules.Conspirators(ids.Length);
        using var seedQuery = connection.CreateCommand(); seedQuery.Transaction = transaction;
        seedQuery.CommandText = "SELECT server_seed FROM sealed_case_rosters WHERE case_id=$case"; seedQuery.Parameters.AddWithValue("$case", caseId);
        var seed = (string)seedQuery.ExecuteScalar()!;
        var tally = SealedCaseRules.Tally(ids, first, seed);
        var active = time < firstDeadline || !tally.NeedsRunoff ? first : second;
        var phase = time < finaleStarts ? "waiting" : time < firstDeadline ? "ballot"
            : time < timing.FinaleEndsAt && tally.NeedsRunoff ? "runoff" : time < timing.FinaleEndsAt ? "waiting" : "released";
        if (timing.PausedAt is not null) { phase = "paused"; }
        using var resolved = connection.CreateCommand(); resolved.Transaction = transaction;
        resolved.CommandText = "SELECT COALESCE(MAX(chapter),0) FROM sealed_case_resolutions WHERE case_id=$case"; resolved.Parameters.AddWithValue("$case", caseId);
        if (phase == "released" && (long)resolved.ExecuteScalar()! != timing.Chapters) { phase = "resolving"; }
        string[]? selected = null; string? winner = null; string[]? conspirators = null; SealedRecap[]? recap = null; string? message = null;
        if (phase == "released")
        {
            if (!tally.NeedsRunoff) { selected = tally.Selected.ToArray(); }
            else
            {
                var counts = tally.TiedCandidates.ToDictionary(id => id, _ => 0, StringComparer.Ordinal);
                foreach (var target in second.Values.SelectMany(x => x)) { if (counts.ContainsKey(target)) { counts[target]++; } }
                var chosen = tally.TiedCandidates.OrderByDescending(id => counts[id])
                    .ThenBy(id => SealedScenario.Digest(seed, "runoff-after-close", id), StringComparer.Ordinal).Take(tally.OpenPlaces);
                selected = tally.Selected.Concat(chosen).ToArray();
            }
            conspirators = members.Where(m => m.Team == SealedSecretTeam.Conspirator).Select(m => m.PlayerId).ToArray();
            using var setupQuery = connection.CreateCommand(); setupQuery.Transaction = transaction;
            setupQuery.CommandText = "SELECT setup_json FROM sealed_cases WHERE id=$case";
            setupQuery.Parameters.AddWithValue("$case", caseId);
            using var setup = JsonDocument.Parse((string)setupQuery.ExecuteScalar()!);
            var presentation = SealedScenarioCatalog.FromSetup(setup.RootElement);
            var guardiansWin = SealedCaseRules.GuardiansWin(selected, conspirators, conspirators.Length);
            winner = guardiansWin ? presentation.GuardianLabel : presentation.ConspiratorLabel;
            message = guardiansWin ? presentation.FinaleGuardian : presentation.FinaleConspirator;
            recap = Recap(connection, transaction, caseId);
        }
        return new(phase, ballotSize, active.ContainsKey(actor), active.GetValueOrDefault(actor, []),
            time >= firstDeadline && tally.NeedsRunoff ? tally.TiedCandidates.ToArray() : [], time >= firstDeadline && tally.NeedsRunoff ? tally.OpenPlaces : 0, selected, winner, conspirators, recap, message);
    }
}
