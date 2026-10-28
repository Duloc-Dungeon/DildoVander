using System.Text.Json;
using Kufr.Backend.Features.Identity;
using Kufr.Backend.Features.CustomerData;

namespace Kufr.Backend.Features.SealedCase;

internal sealed record SealedAiMove(SealedPlanConfirmation? Plan, SealedBallot? Ballot, string? Message = null);
internal sealed record SealedAiLease(bool Fresh, SealedAiMove? Move);

internal sealed partial class SealedCaseStore
{
    internal SealedAiCase[] RunningAiCases()
    {
        using var connection = Open(); using var query = connection.CreateCommand();
        query.CommandText = """
            SELECT ca.id,ca.group_id,json_extract(ca.setup_json,'$.createdBy'),bot.player_id
            FROM sealed_cases ca JOIN sealed_case_ai_players bot ON bot.case_id=ca.id
            WHERE ca.status='running' AND bot.finished=0 ORDER BY ca.id,bot.player_id
            """;
        using var rows = query.ExecuteReader(); var cases = new List<(string Id, string Group, string Owner, string Player)>();
        while (rows.Read()) { cases.Add((rows.GetString(0), rows.GetString(1), rows.GetString(2), rows.GetString(3))); }
        return cases.GroupBy(c => c.Id, StringComparer.Ordinal)
            .Select(g => new SealedAiCase(g.Key, g.First().Group, g.First().Owner, g.Select(c => c.Player).ToArray())).ToArray();
    }

    internal SealedAiLease? LeaseAiTurn(SealedAiCase game, string bot, string turn, Func<CustomerAccountAdmission> authorize)
    {
        using var connection = Open(); using var transaction = connection.BeginTransaction(deferred: false);
        using var admission = AcquireCaseAccess(connection, transaction, game.Id,
            () => DemandAiOrPlayerAdmission(connection, transaction, game.Id, game.Group, bot, () => authorize(), true), bot);
        var revision = ReadCaseRevision(connection, transaction, game.Id, game.Group);
        var timing = ReadTiming(connection, transaction, game.Id, revision, clock.GetUtcNow().ToUnixTimeSeconds());
        if (timing.PausedAt is not null || timing.Phase is not ("planning" or "finale")) { return null; }
        var currentTurn = timing.Phase == "planning" ? "plan-" + timing.Chapter
            : ReadFinale(connection, transaction, game.Id, RosterMembers(connection, transaction, game.Id), bot, timing).Phase;
        if (turn != currentTurn || currentTurn is not ("ballot" or "runoff") && timing.Phase != "planning") { return null; }
        using var query = connection.CreateCommand(); query.Transaction = transaction;
        query.CommandText = "SELECT started_at,completed,decision_json FROM sealed_case_ai_turns WHERE case_id=$case AND player_id=$bot AND turn_key=$turn";
        query.Parameters.AddWithValue("$case", game.Id); query.Parameters.AddWithValue("$bot", bot); query.Parameters.AddWithValue("$turn", turn);
        using (var prior = query.ExecuteReader())
        {
            if (prior.Read())
            {
                if (prior.GetInt32(1) == 2) { return null; }
                var decision = prior.IsDBNull(2) ? null : JsonSerializer.Deserialize<SealedAiMove>(prior.GetString(2), Json);
                if (decision is null && prior.GetInt64(0) > clock.GetUtcNow().ToUnixTimeSeconds() - 120) { return null; }
                prior.Close(); transaction.Commit(); return new(false, decision);
            }
        }
        // Persist dispatch before any provider request. Restart never retries a paid call.
        query.CommandText = "INSERT INTO sealed_case_ai_turns(case_id,player_id,turn_key,started_at,completed) VALUES($case,$bot,$turn,$now,0)";
        query.Parameters.AddWithValue("$now", clock.GetUtcNow().ToUnixTimeSeconds()); query.ExecuteNonQuery();
        transaction.Commit(); return new(true, null);
    }

    internal void FinishAiPlayer(SealedAiCase game, string bot, Func<CustomerAccountAdmission> authorize)
    {
        using var connection = Open(); using var transaction = connection.BeginTransaction(deferred: false);
        using var admission = AcquireCaseAccess(connection, transaction, game.Id,
            () => DemandAiOrPlayerAdmission(connection, transaction, game.Id, game.Group, bot, () => authorize(), true), bot);
        _ = ReadCaseRevision(connection, transaction, game.Id, game.Group);
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "UPDATE sealed_case_ai_players SET finished=1 WHERE case_id=$case AND player_id=$bot";
        command.Parameters.AddWithValue("$case", game.Id); command.Parameters.AddWithValue("$bot", bot); command.ExecuteNonQuery();
        transaction.Commit();
    }

    internal SealedAiMove? SaveAiDecision(SealedAiCase game, string bot, string turn, SealedAiMove? move, bool complete,
        Func<CustomerAccountAdmission> authorize)
    {
        using var connection = Open(); using var transaction = connection.BeginTransaction(deferred: false);
        using var admission = AcquireCaseAccess(connection, transaction, game.Id,
            () => DemandAiOrPlayerAdmission(connection, transaction, game.Id, game.Group, bot, () => authorize(), true), bot);
        _ = ReadCaseRevision(connection, transaction, game.Id, game.Group);
        using var query = connection.CreateCommand(); query.Transaction = transaction;
        query.CommandText = """
            UPDATE sealed_case_ai_turns SET completed=$state,decision_json=COALESCE(decision_json,$decision)
            WHERE case_id=$case AND player_id=$bot AND turn_key=$turn AND completed!=2
            """;
        query.Parameters.AddWithValue("$case", game.Id); query.Parameters.AddWithValue("$bot", bot); query.Parameters.AddWithValue("$turn", turn);
        query.Parameters.AddWithValue("$state", complete ? 2 : 1); query.Parameters.AddWithValue("$decision", move is null ? DBNull.Value : JsonSerializer.Serialize(move, Json));
        query.ExecuteNonQuery();
        query.CommandText = "SELECT decision_json FROM sealed_case_ai_turns WHERE case_id=$case AND player_id=$bot AND turn_key=$turn";
        var canonical = query.ExecuteScalar() as string;
        transaction.Commit(); return canonical is null ? null : JsonSerializer.Deserialize<SealedAiMove>(canonical, Json);
    }
}

internal sealed class SealedCaseAiRunner(SealedCaseStore store, CustomerAdmissionAuthority authority,
    ISealedCaseAiPlayers provider, ILogger<SealedCaseAiRunner> log)
{
    private CustomerAccountAdmission Authorize(SealedAiCase game)
    {
        var held = authority.AcquireAccount(game.Owner);
        if (held.Proof.AccountId == game.Owner && held.Proof.LaunchGroupId == game.Group
            && held.Proof.LaunchGroupRevokedAt is null && held.Proof.Roles.Contains(AccountRoles.Owner, StringComparer.Ordinal)) { return held; }
        held.Dispose(); throw new SealedCasePersistenceException("sealed_case_owner_required", 403);
    }

    internal async Task RunOnceAsync(CancellationToken token)
    {
        if (!provider.Available) { return; }
        // One case at a time and two bots per case bound paid concurrency and load.
        foreach (var game in store.RunningAiCases())
        {
            token.ThrowIfCancellationRequested();
            try
            {
                store.AdvanceAiCase(game.Id, game.Group, () => Authorize(game));
                await Parallel.ForEachAsync(game.Players, new ParallelOptions { MaxDegreeOfParallelism = 2, CancellationToken = token },
                    async (bot, cancellation) => await PlayAsync(game, bot, cancellation));
            }
            catch (Exception error) when (error is SealedCasePersistenceException or CustomerDataAccessException)
            { log.LogDebug("AI case unavailable: {Code}", error is SealedCasePersistenceException failure ? failure.Code : "account_unavailable"); }
        }
    }

    private async Task PlayAsync(SealedAiCase game, string bot, CancellationToken token)
    {
        var view = store.ReadAiPlay(game.Id, game.Group, bot, () => Authorize(game));
        if (view.Phase == "finished")
        {
            store.FinishAiPlayer(game, bot, () => Authorize(game)); return;
        }
        var turn = view.Phase == "planning" ? "plan-" + view.Chapter
            : view.Finale?.Phase is "ballot" or "runoff" ? view.Finale.Phase : null;
        if (turn is null) { return; }
        var lease = store.LeaseAiTurn(game, bot, turn, () => Authorize(game));
        if (lease is null) { return; }
        var move = lease.Move;
        if (move is null)
        {
            move = lease.Fresh ? await provider.ChooseAsync(view, token) : SealedCaseAiPlayers.Fallback(view);
            move = store.SaveAiDecision(game, bot, turn, move, false, () => Authorize(game));
        }
        if (move is null) { return; }
        try
        {
            if (move.Plan is { } plan)
            {
                var operation = "bot-" + bot + "-" + turn;
                for (var attempt = 0; attempt < 8; attempt++)
                {
                    var expected = store.OperationRevision(game.Id, bot, operation) ?? store.Read(game.Id).Revision;
                    try { store.ConfirmPlan(game.Id, game.Group, bot, operation, expected, plan, () => Authorize(game)); break; }
                    catch (SealedCasePersistenceException error) when (error.Code == "revision_conflict" && attempt < 7) { }
                }
            }
            else if (move.Ballot is { } ballot)
            { store.Vote(game.Id, game.Group, bot, "bot-" + bot + "-" + turn, ballot, () => Authorize(game)); }
            if (!string.IsNullOrWhiteSpace(move.Message))
            { store.PostStatement(game.Id, game.Group, bot, "bot-" + bot + "-" + turn + "-message", move.Message, [], true, () => Authorize(game)); }
            store.SaveAiDecision(game, bot, turn, null, true, () => Authorize(game));
        }
        catch (SealedCasePersistenceException error) when (error.Code is "chapter_locked" or "wrong_chapter" or "sealed_case_ballot_closed")
        { /* The stored proposal can resume after a pause; it never moves to another chapter. */ }
    }
}

internal sealed class SealedCaseAiWorker(SealedCaseAiRunner runner, ILogger<SealedCaseAiWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try { await runner.RunOnceAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception error) { log.LogError("AI players could not advance: {Type}", error.GetType().Name); }
        }
    }
}
