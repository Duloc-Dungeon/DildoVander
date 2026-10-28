using System.Security.Claims;
using Kufr.Backend.Features.Wands;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kufr.Backend.Features.Identity;
using Kufr.Backend.Features.Launch;
using Kufr.Backend.Features.Access;
using Kufr.Backend.Features.Customers;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Kufr.Backend.Features.Game;

internal enum GameChoice { Strike, Trick, Shield }
internal sealed record GameCommand(string OperationId, long ExpectedRevision, string? Choice = null,
    int SparkBid = 0, bool? TasksEnabled = null, bool? AdultConsent = null, bool? FlirtConsent = null,
    string? TaskCategory = null, string? TaskAction = null, string? Loadout = null, bool? UseLoadouts = null);
internal sealed record GameChallenge(string OpponentId, string Mode, string OperationId,
    bool TasksEnabled = false, bool AdultConsent = false, bool FlirtConsent = false, bool UseLoadouts = false);
internal sealed record GameRoundView(int Index, string? PlayerChoice, string? OpponentChoice, string? Winner,
    int PlayerSparkBid, int OpponentSparkBid, int PlayerPoints, int OpponentPoints, string? Reason);
internal sealed record GamePlayerView(string Id, string DisplayName, int Xp, int MatchesPlayed,
    bool TacticalUnlocked, bool YoloUnlocked, bool HasWand = false, int WandTier = 0);
internal sealed record GameOpponentView(string Id, string DisplayName, int Xp, int MatchesPlayed);
internal sealed record GameLobbyOpponentView(string Id, string DisplayName, int Xp, int MatchesPlayed,
    bool HasWand, int WandTier, bool TacticalUnlocked);
internal sealed record GamePartyTask(string Title, string Text, string Category, string Status, int ExchangeCount);
internal sealed record GameYoloBeat(string Text, string[] DetailRefs);
internal sealed record GameYoloView(string Status, IReadOnlyList<GameYoloBeat> Beats, string? Verdict,
    string? WinnerId, string? ErrorCode);
internal sealed record GameMatchView(string Id, string Mode, string Status, long Revision,
    bool CanAccept, bool CanCancel,
    GameOpponentView Opponent, IReadOnlyList<GameRoundView> Rounds, int CompletedRounds,
    int PlayerScore, int OpponentScore, bool SparksEnabled, int PlayerSparks, int OpponentSparks, int Block,
    IReadOnlyDictionary<string, int> PlayerInventory, IReadOnlyDictionary<string, int> OpponentInventory,
    bool OwnChoiceLocked, bool OpponentChoiceLocked, int TotalRounds, string? Loadout,
    bool TasksEnabled, bool AdultConsent, bool FlirtConsent, GamePartyTask? PartyTask, long CreatedAt, long? CompletedAt,
    string? Result, int XpAwarded, GameYoloView? Yolo = null, string RulesVersion = "mobile-game-v0.3",
    bool LoadoutsEnabled = false, bool NeedsLoadout = false, bool OwnLoadoutLocked = false,
    bool OpponentLoadoutLocked = false, bool LoadoutsAvailable = false);
internal sealed record GameLobby(GamePlayerView Player, IReadOnlyList<GameLobbyOpponentView> Opponents,
    IReadOnlyList<GameMatchView> Incoming, IReadOnlyList<GameMatchView> Active,
    IReadOnlyList<GameMatchView> History, GameBossView? Boss = null);

internal sealed class GameFault(int status, string code) : Exception(code)
{
    internal int Status { get; } = status;
    internal string Code { get; } = code;
}

// Game state and XP live in one durable SQLite authority. Every mutation is
// revision checked and serialized by SQLite's write transaction.
internal sealed partial class GameStore
{
    private readonly string _connectionString;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly object _writeGate = new();

    internal GameStore(string dataRoot)
    {
        Directory.CreateDirectory(dataRoot);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(dataRoot, "mobile-game.sqlite"), ForeignKeys = true }.ToString();
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS game_players(group_id TEXT NOT NULL,user_id TEXT NOT NULL,xp INTEGER NOT NULL DEFAULT 0,matches INTEGER NOT NULL DEFAULT 0,PRIMARY KEY(group_id,user_id));
            CREATE TABLE IF NOT EXISTS game_matches(id TEXT PRIMARY KEY, group_id TEXT NOT NULL, challenger_id TEXT NOT NULL,
                opponent_id TEXT NOT NULL, state_json TEXT NOT NULL, created_at INTEGER NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_game_matches_group_created ON game_matches(group_id,created_at);
            CREATE TABLE IF NOT EXISTS game_operations(operation_id TEXT PRIMARY KEY,match_id TEXT NOT NULL,payload_hash TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS game_deleted_accounts(user_id TEXT PRIMARY KEY,deleted_at INTEGER NOT NULL);
            """;
        command.ExecuteNonQuery();
    }

    internal GameLobby Lobby(string groupId, string userId, string displayName, IReadOnlyList<GameLobbyOpponentView> opponents)
    {
        using var connection = Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT id,group_id,challenger_id,opponent_id,state_json FROM game_matches WHERE group_id=$g AND (challenger_id=$u OR opponent_id=$u) ORDER BY created_at DESC LIMIT 100";
        cmd.Parameters.AddWithValue("$g", groupId);
        cmd.Parameters.AddWithValue("$u", userId);
        var matches = new List<(string Id, string Group, string Challenger, string Opponent, State State)>();
        using (var reader = cmd.ExecuteReader()) while (reader.Read())
            matches.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), JsonSerializer.Deserialize<State>(reader.GetString(4), Json)!));
        var profile = Player(connection, userId, null, groupId);
        var incoming = new List<GameMatchView>(); var active = new List<GameMatchView>(); var history = new List<GameMatchView>();
        foreach (var m in matches)
        {
            var view = View(connection, m.Id, m.Group, m.Challenger, m.Opponent, m.State, userId);
            if (m.State.Status == "incoming" && m.Opponent == userId) incoming.Add(view);
            else if (m.State.Status is "active" or "generating") active.Add(view);
            else history.Add(view);
        }
        return new(new(userId, displayName, profile.Xp, profile.Matches, profile.Xp >= 30, profile.Matches >= 3), opponents, incoming, active, history, ReadBoss(groupId, userId));
    }

    internal GameOpponentView Stats(string groupId, string userId, string displayName)
    {
        using var connection = Open();
        var profile = Player(connection, userId, null, groupId);
        return new(userId, displayName, profile.Xp, profile.Matches);
    }

    internal string[] ActiveOpponentIds(string groupId, string userId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT CASE WHEN challenger_id=$user THEN opponent_id ELSE challenger_id END FROM game_matches WHERE group_id=$group AND (challenger_id=$user OR opponent_id=$user) AND json_extract(state_json,'$.status') IN ('incoming','active','generating')";
        command.Parameters.AddWithValue("$group", groupId);
        command.Parameters.AddWithValue("$user", userId);
        using var reader = command.ExecuteReader();
        var peers = new List<string>();
        while (reader.Read()) peers.Add(reader.GetString(0));
        return peers.ToArray();
    }

    internal void RetireIneligibleMatches(string groupId, IReadOnlySet<string> eligibleAccountIds, long now)
    {
        lock (_writeGate)
        {
            using var connection = Open(); using var transaction = connection.BeginTransaction(deferred: false);
            using var select = connection.CreateCommand(); select.Transaction = transaction;
            select.CommandText = "SELECT id,challenger_id,opponent_id,state_json FROM game_matches WHERE group_id=$g AND json_extract(state_json,'$.status') IN ('incoming','active','generating')";
            select.Parameters.AddWithValue("$g", groupId);
            var rows = new List<(string Id, State State)>();
            using (var reader = select.ExecuteReader()) while (reader.Read())
            {
                if (!eligibleAccountIds.Contains(reader.GetString(1)) || !eligibleAccountIds.Contains(reader.GetString(2)))
                {
                    rows.Add((reader.GetString(0), JsonSerializer.Deserialize<State>(reader.GetString(3), Json)!));
                }
            }
            foreach (var (id, state) in rows)
            {
                state.Status = "abandoned"; state.Revision++; state.CompletedAt = now;
                using var update = connection.CreateCommand(); update.Transaction = transaction;
                update.CommandText = "UPDATE game_matches SET state_json=$state WHERE id=$id";
                update.Parameters.AddWithValue("$state", JsonSerializer.Serialize(state, Json)); update.Parameters.AddWithValue("$id", id); update.ExecuteNonQuery();
            }
            transaction.Commit();
        }
    }

    internal void DeleteAccount(string accountId)
    {
        lock (_writeGate)
        {
            using var connection = Open(); using var transaction = connection.BeginTransaction(deferred: false);
            using (var fence = connection.CreateCommand())
            {
                fence.Transaction = transaction; fence.CommandText = "INSERT INTO game_deleted_accounts(user_id,deleted_at) VALUES($u,$now) ON CONFLICT(user_id) DO NOTHING";
                fence.Parameters.AddWithValue("$u", accountId); fence.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds()); fence.ExecuteNonQuery();
            }
            using (var operations = connection.CreateCommand())
            {
                operations.Transaction = transaction;
                operations.CommandText = "DELETE FROM game_operations WHERE match_id IN (SELECT id FROM game_matches WHERE challenger_id=$u OR opponent_id=$u)";
                operations.Parameters.AddWithValue("$u", accountId); operations.ExecuteNonQuery();
            }
            using (var matches = connection.CreateCommand())
            {
                matches.Transaction = transaction; matches.CommandText = "DELETE FROM game_matches WHERE challenger_id=$u OR opponent_id=$u";
                matches.Parameters.AddWithValue("$u", accountId); matches.ExecuteNonQuery();
            }
            using (var profile = connection.CreateCommand())
            {
                profile.Transaction = transaction; profile.CommandText = "DELETE FROM game_players WHERE user_id=$u";
                profile.Parameters.AddWithValue("$u", accountId); profile.ExecuteNonQuery();
            }
            DeleteProgressAccount(connection, transaction, accountId);
            DeleteBossAccount(connection, transaction, accountId);
            DeleteTeamAccount(connection, transaction, accountId);
            transaction.Commit();
        }
    }

    internal static void EnsureGameAccount(SqliteConnection connection, SqliteTransaction transaction, string accountId)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT 1 FROM game_deleted_accounts WHERE user_id=$u"; command.Parameters.AddWithValue("$u", accountId);
        if (command.ExecuteScalar() is not null) throw new GameFault(403, "game_account_erased");
    }

    internal GameMatchView Challenge(string groupId, string userId, string challengerName, string opponentId, string opponentName, GameChallenge request, long now,
        bool adultContentEnabled = true, bool flirtEnabled = true)
    {
        if (request.Mode is not ("quick" or "tactical" or "yolo")) throw new GameFault(400, "game_mode_invalid");
        if (request.OperationId.Length is < 8 or > 80) throw new GameFault(400, "operation_id_invalid");
        lock (_writeGate)
        {
            using var connection = Open(); using var tx = connection.BeginTransaction(deferred: false);
            var payloadHash = PayloadHash(userId, "challenge", request);
            if (FindOperation(connection, tx, request.OperationId) is { } prior)
            {
                if (prior.PayloadHash != payloadHash) throw new GameFault(409, "operation_payload_conflict");
                var existing = ReadMatch(connection, tx, prior.MatchId);
                if (existing is not null && existing.Value.Group == groupId && (existing.Value.Challenger == userId || existing.Value.Opponent == userId))
                    return View(connection, prior.MatchId, groupId, existing.Value.Challenger, existing.Value.Opponent, existing.Value.State, userId, tx);
                throw new GameFault(409, "operation_already_used");
            }
            var player = Player(connection, userId, tx, groupId);
            var opponent = Player(connection, opponentId, tx, groupId);
            EnsureGameAccount(connection, tx, userId);
            EnsureGameAccount(connection, tx, opponentId);
            if (request.Mode == "yolo" && (player.Matches < 3 || opponent.Matches < 3)) throw new GameFault(403, "yolo_locked");
            if (request.Mode == "tactical" && (player.Xp < 30 || opponent.Xp < 30))
                throw new GameFault(403, "tactical_locked");
            if (userId == opponentId) throw new GameFault(400, "opponent_invalid");
            if (ActiveCount(connection, tx, groupId, userId) >= 1 || ActiveCount(connection, tx, groupId, opponentId) >= 1)
                throw new GameFault(409, "match_already_active");
            var state = new State
            {
                RulesVersion = "mobile-game-v0.3",
                Mode = request.Mode,
                Status = "incoming",
                Revision = 1,
                CreatedAt = now,
                ChallengerTasks = true,
                ChallengerAdultConsent = adultContentEnabled,
                ChallengerFlirtConsent = flirtEnabled,
                TasksEnabled = true,
                AdultConsent = adultContentEnabled,
                FlirtConsent = flirtEnabled,
                TotalRounds = request.Mode == "quick" ? 7 : request.Mode == "tactical" ? 8 : 0,
                YoloStatus = request.Mode == "yolo" ? "awaiting_acceptance" : null,
                GroupId = groupId,
                SparksEnabled = request.Mode == "tactical" || player.Matches >= 2 && opponent.Matches >= 2,
                LoadoutsUnlocked = request.Mode == "tactical" && TacticalMatches(connection, tx, groupId, userId) >= 2 && TacticalMatches(connection, tx, groupId, opponentId) >= 2,
                ChallengerWantsLoadouts = request.UseLoadouts,
                ChallengerId = userId,
                OpponentId = opponentId,
                ChallengerName = challengerName,
                OpponentName = opponentName,
                OperationId = request.OperationId
            };
            var id = Guid.NewGuid().ToString("N");
            using var cmd = connection.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = "INSERT INTO game_matches(id,group_id,challenger_id,opponent_id,state_json,created_at) VALUES($id,$g,$c,$o,$s,$n)";
            cmd.Parameters.AddWithValue("$id", id); cmd.Parameters.AddWithValue("$g", groupId); cmd.Parameters.AddWithValue("$c", userId); cmd.Parameters.AddWithValue("$o", opponentId); cmd.Parameters.AddWithValue("$s", JsonSerializer.Serialize(state, Json)); cmd.Parameters.AddWithValue("$n", now); cmd.ExecuteNonQuery();
            RecordOperation(connection, tx, request.OperationId, id, payloadHash); tx.Commit(); tx.Dispose();
            return View(connection, id, groupId, userId, opponentId, state, userId);
        }
    }

    internal GameMatchView Mutate(string id, string groupId, string userId, string action, GameCommand request, long now,
        bool adultContentEnabled = true, bool flirtEnabled = true)
    {
        if (request.OperationId.Length is < 8 or > 80) throw new GameFault(400, "operation_id_invalid");
        lock (_writeGate)
        {
            using var connection = Open(); using var tx = connection.BeginTransaction(deferred: false);
            var row = ReadMatch(connection, tx, id);
            if (row is null || row.Value.Group != groupId || row.Value.Challenger != userId && row.Value.Opponent != userId) throw new GameFault(404, "game_match_unavailable");
            EnsureGameAccount(connection, tx, row.Value.Challenger);
            EnsureGameAccount(connection, tx, row.Value.Opponent);
            var state = row.Value.State;
            var payloadHash = PayloadHash(userId, action, request);
            if (FindOperation(connection, tx, request.OperationId) is { } prior)
            {
                if (prior.MatchId != id) throw new GameFault(409, "operation_already_used");
                if (prior.PayloadHash != payloadHash) throw new GameFault(409, "operation_payload_conflict");
                return View(connection, id, groupId, row.Value.Challenger, row.Value.Opponent, state, userId, tx);
            }
            if (state.Revision != request.ExpectedRevision) throw new GameFault(409, "game_revision_conflict");
            switch (action)
            {
                case "accept":
                    if (userId != state.OpponentId || state.Status != "incoming") throw new GameFault(409, "game_action_unavailable");
                    if (ActiveCount(connection, tx, groupId, userId, id) >= 1) throw new GameFault(409, "match_already_active");
                    state.OpponentTasks = true;
                    state.OpponentAdultConsent = adultContentEnabled;
                    state.OpponentFlirtConsent = flirtEnabled;
                    state.TasksEnabled = true;
                    state.AdultConsent = adultContentEnabled;
                    state.FlirtConsent = flirtEnabled;
                    state.LoadoutsEnabled = state.Mode == "tactical" && state.LoadoutsUnlocked && state.ChallengerWantsLoadouts && (request.UseLoadouts ?? false);
                    state.NeedsLoadout = state.LoadoutsEnabled;
                    if (state.Mode == "yolo")
                    {
                        state.Status = "generating";
                        state.YoloStatus = "generating";
                        state.YoloGenerationRevision = state.Revision + 1;
                    }
                    else { state.Status = "active"; }
                    break;
                case "decline": if (userId != state.OpponentId || state.Status != "incoming") throw new GameFault(409, "game_action_unavailable"); state.Status = "declined"; state.CompletedAt = now; break;
                case "cancel":
                    if (state.Mode == "yolo" && state.Status is "generating" or "failed")
                    {
                        state.Status = "cancelled"; state.YoloStatus = "cancelled"; state.YoloErrorCode = null; state.CompletedAt = now;
                    }
                    else if (state.Status == "active")
                    {
                        state.Status = "cancelled"; state.CompletedAt = now;
                    }
                    else
                    {
                        if (userId != state.ChallengerId || state.Status != "incoming") throw new GameFault(409, "game_action_unavailable");
                        state.Status = "cancelled"; state.CompletedAt = now;
                    }
                    break;
                case "choose": Choose(state, userId, request, now); break;
                case "loadout": SelectLoadout(state, userId, request); break;
                case "task":
                    if (!ApplyTask(state, userId, request))
                    {
                        RecordOperation(connection, tx, request.OperationId, id, payloadHash);
                        tx.Commit();
                        return View(connection, id, groupId, row.Value.Challenger, row.Value.Opponent, state, userId);
                    }
                    break;
                case "retry":
                    if (state.Mode != "yolo" || state.YoloStatus != "failed") throw new GameFault(409, "game_retry_unavailable");
                    if (ActiveCount(connection, tx, groupId, state.ChallengerId, id) >= 1
                        || ActiveCount(connection, tx, groupId, state.OpponentId, id) >= 1)
                        throw new GameFault(409, "match_already_active");
                    state.Status = "generating"; state.YoloStatus = "generating"; state.YoloErrorCode = null;
                    state.YoloGenerationRevision = state.Revision + 1;
                    break;
                default: throw new GameFault(404, "game_action_unavailable");
            }
            state.Revision++;
            if (state.Status == "complete") AwardXp(connection, tx, state);
            using var update = connection.CreateCommand(); update.Transaction = tx; update.CommandText = "UPDATE game_matches SET state_json=$s WHERE id=$id"; update.Parameters.AddWithValue("$s", JsonSerializer.Serialize(state, Json)); update.Parameters.AddWithValue("$id", id); update.ExecuteNonQuery();
            RecordOperation(connection, tx, request.OperationId, id, payloadHash);
            tx.Commit(); tx.Dispose();
            return View(connection, id, groupId, state.ChallengerId, state.OpponentId, state, userId);
        }
    }

    internal GameMatchView Read(string id, string groupId, string userId)
    {
        using var connection = Open(); var row = ReadMatch(connection, null, id);
        if (row is null || row.Value.Group != groupId || row.Value.Challenger != userId && row.Value.Opponent != userId) throw new GameFault(404, "game_match_unavailable");
        return View(connection, id, row.Value.Group, row.Value.Challenger, row.Value.Opponent, row.Value.State, userId);
    }

    internal (string GroupId, string ChallengerId, string OpponentId)? Participants(string id)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT group_id,challenger_id,opponent_id FROM game_matches WHERE id=$id";
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        return reader.Read() ? (reader.GetString(0), reader.GetString(1), reader.GetString(2)) : null;
    }

    private static void Choose(State s, string user, GameCommand r, long now)
    {
        if (s.Status != "active" || s.CompletedRounds >= s.TotalRounds || s.NeedsLoadout) throw new GameFault(409, "game_action_unavailable");
        var choice = (r.Choice ?? "").Trim().ToLowerInvariant() switch
        {
            "strike" => GameChoice.Strike,
            "trick" => GameChoice.Trick,
            "shield" => GameChoice.Shield,
            _ => throw new GameFault(400, "game_choice_invalid")
        };
        if (r.SparkBid < 0 || r.SparkBid > 1) throw new GameFault(400, "spark_bid_invalid");
        if (!s.SparksEnabled && r.SparkBid != 0) throw new GameFault(409, "sparks_locked");
        var round = s.Rounds.ElementAtOrDefault(s.CompletedRounds) ?? new Round();
        var chosen = choice.ToString().ToLowerInvariant();
        if (s.Mode == "tactical")
        {
            var inventory = user == s.ChallengerId ? s.ChallengerInventory : s.OpponentInventory;
            if (inventory[chosen] <= 0) throw new GameFault(409, "inventory_exhausted");
        }
        if (user == s.ChallengerId) { if (round.ChallengerChoice is not null) throw new GameFault(409, "choice_locked"); if (s.ChallengerSparks + round.ChallengerBid + r.SparkBid > 2) throw new GameFault(409, "sparks_exhausted"); round.ChallengerChoice = chosen; round.ChallengerBid = r.SparkBid; }
        else { if (round.OpponentChoice is not null) throw new GameFault(409, "choice_locked"); if (s.OpponentSparks + round.OpponentBid + r.SparkBid > 2) throw new GameFault(409, "sparks_exhausted"); round.OpponentChoice = chosen; round.OpponentBid = r.SparkBid; }
        if (!s.Rounds.Contains(round)) s.Rounds.Add(round);
        if (round.ChallengerChoice is not null && round.OpponentChoice is not null)
        {
            if (s.Mode == "tactical")
            {
                s.ChallengerInventory[round.ChallengerChoice]--;
                s.OpponentInventory[round.OpponentChoice]--;
            }
            s.ChallengerSparks += round.ChallengerBid; s.OpponentSparks += round.OpponentBid;
            round.Winner = Winner(round.ChallengerChoice, round.OpponentChoice);
            var awardedPoints = 1 + round.ChallengerBid + round.OpponentBid;
            if (round.Winner == "challenger") s.ChallengerScore += awardedPoints;
            else if (round.Winner == "opponent") s.OpponentScore += awardedPoints;
            s.CompletedRounds++;
            if (s.Mode == "tactical" && s.CompletedRounds % 4 == 0 && s.CompletedRounds < s.TotalRounds)
            {
                if (s.LoadoutsEnabled) { s.NeedsLoadout = true; s.ChallengerNextLoadout = null; s.OpponentNextLoadout = null; }
                else { s.ChallengerInventory = NewInventory(); s.OpponentInventory = NewInventory(); }
            }
            var challengerLeads = s.ChallengerScore > s.OpponentScore;
            var lead = Math.Abs(s.ChallengerScore - s.OpponentScore);
            var remainingRounds = s.TotalRounds - s.CompletedRounds;
            var maxCatchUp = remainingRounds;
            if (s.SparksEnabled)
            {
                maxCatchUp += Math.Min(remainingRounds, 2 - s.ChallengerSparks)
                    + Math.Min(remainingRounds, 2 - s.OpponentSparks);
            }
            if (s.CompletedRounds == s.TotalRounds || s.Mode == "quick" && lead > maxCatchUp)
            {
                s.Status = "complete"; s.CompletedAt = now;
                s.Result = s.ChallengerScore == s.OpponentScore ? "draw" : challengerLeads ? "challenger" : "opponent";
                AssignLoserTask(s);
            }
        }
    }

    private static void SelectLoadout(State state, string userId, GameCommand request)
    {
        if (state.Status != "active" || state.Mode != "tactical" || !state.LoadoutsEnabled || !state.NeedsLoadout)
            throw new GameFault(409, "game_loadout_unavailable");
        var name = (request.Loadout ?? "").Trim().ToLowerInvariant();
        if (!LoadoutStock.ContainsKey(name)) throw new GameFault(400, "game_loadout_invalid");
        if (userId == state.ChallengerId) { if (state.ChallengerNextLoadout is not null) throw new GameFault(409, "loadout_locked"); state.ChallengerNextLoadout = name; }
        else { if (state.OpponentNextLoadout is not null) throw new GameFault(409, "loadout_locked"); state.OpponentNextLoadout = name; }
        if (state.ChallengerNextLoadout is null || state.OpponentNextLoadout is null) return;
        state.ChallengerInventory = new(LoadoutStock[state.ChallengerNextLoadout]);
        state.OpponentInventory = new(LoadoutStock[state.OpponentNextLoadout]);
        state.ChallengerActiveLoadout = state.ChallengerNextLoadout;
        state.OpponentActiveLoadout = state.OpponentNextLoadout;
        state.NeedsLoadout = false;
    }

    private static string? Winner(string a, string b)
    {
        if (a == b) return null;
        return (a, b) switch { ("strike", "trick") or ("trick", "shield") or ("shield", "strike") => "challenger", ("trick", "strike") or ("shield", "trick") or ("strike", "shield") => "opponent", _ => throw new GameFault(400, "game_choice_invalid") };
    }

    private static bool ApplyTask(State state, string userId, GameCommand request)
    {
        switch (request.TaskAction)
        {
            case "skip":
            case "done":
                if (state.Status != "complete" || state.PartyTask is null
                    || state.Result is null or "draw") throw new GameFault(409, "game_task_unavailable");
                var loserId = state.Result == "challenger" ? state.OpponentId : state.ChallengerId;
                if (userId != loserId) throw new GameFault(403, "game_task_loser_only");
                if (state.PartyTask.Status != "pending")
                {
                    var requestedStatus = request.TaskAction == "done" ? "done" : "skipped";
                    if (state.PartyTask.Status == requestedStatus) return false;
                    throw new GameFault(409, "game_task_already_resolved");
                }
                if (request.TaskAction == "skip")
                {
                    state.PartyTask = state.PartyTask with { Status = "skipped" };
                    return true;
                }
                if (request.TaskAction == "done")
                {
                    state.TaskExchangeCount++;
                    state.PartyTask = state.PartyTask with { Status = "done", ExchangeCount = state.TaskExchangeCount };
                    return true;
                }
                throw new GameFault(400, "game_task_action_invalid");
            default: throw new GameFault(400, "game_task_action_invalid");
        }
    }

    private static void AssignLoserTask(State state)
    {
        if (state.Result is null or "draw") return;
        state.PartyTask = GameTaskCatalog.NextEligible(state.AdultConsent, state.FlirtConsent, state.OperationId);
    }

    private static void AwardXp(SqliteConnection c, SqliteTransaction tx, State s)
    {
        if (s.XpAwarded || s.Mode is not ("quick" or "tactical")) return;
        var won = s.Result == "challenger"; var lost = s.Result == "opponent"; var draw = s.Result == "draw";
        var quick = s.Mode == "quick";
        if (quick)
        {
            AddXp(c, tx, s.GroupId, s.ChallengerId, draw ? 6 : won ? 10 : 6);
            AddXp(c, tx, s.GroupId, s.OpponentId, draw ? 6 : lost ? 10 : 6);
        }
        else
        {
            AddXp(c, tx, s.GroupId, s.ChallengerId, won ? 20 : 12);
            AddXp(c, tx, s.GroupId, s.OpponentId, lost ? 20 : 12);
        }
        s.XpAwarded = true;
    }
    private static void AddXp(SqliteConnection c, SqliteTransaction tx, string groupId, string id, int amount) { using var cmd = c.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = "INSERT INTO game_players(group_id,user_id,xp,matches) VALUES($g,$u,$x,1) ON CONFLICT(group_id,user_id) DO UPDATE SET xp=xp+$x,matches=matches+1"; cmd.Parameters.AddWithValue("$g", groupId); cmd.Parameters.AddWithValue("$u", id); cmd.Parameters.AddWithValue("$x", amount); cmd.ExecuteNonQuery(); }
    private static (int Xp, int Matches) Player(SqliteConnection c, string id, SqliteTransaction? tx = null, string groupId = "") { using var cmd = c.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = "SELECT xp,matches FROM game_players WHERE group_id=$g AND user_id=$u"; cmd.Parameters.AddWithValue("$g", groupId); cmd.Parameters.AddWithValue("$u", id); using var r = cmd.ExecuteReader(); return r.Read() ? (r.GetInt32(0), r.GetInt32(1)) : (0, 0); }
    private static int ActiveCount(SqliteConnection c, SqliteTransaction tx, string groupId, string userId, string? exceptMatchId = null) { using var cmd = c.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = "SELECT count(*) FROM game_matches WHERE group_id=$g AND (challenger_id=$u OR opponent_id=$u) AND json_extract(state_json,'$.status') IN ('incoming','active','generating') AND ($except IS NULL OR id!=$except)"; cmd.Parameters.AddWithValue("$g", groupId); cmd.Parameters.AddWithValue("$u", userId); cmd.Parameters.AddWithValue("$except", (object?)exceptMatchId ?? DBNull.Value); return Convert.ToInt32(cmd.ExecuteScalar()); }
    private static int TacticalMatches(SqliteConnection c, SqliteTransaction tx, string groupId, string userId) { using var cmd = c.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = "SELECT count(*) FROM game_matches WHERE group_id=$g AND (challenger_id=$u OR opponent_id=$u) AND json_extract(state_json,'$.mode')='tactical' AND json_extract(state_json,'$.status')='complete'"; cmd.Parameters.AddWithValue("$g", groupId); cmd.Parameters.AddWithValue("$u", userId); return Convert.ToInt32(cmd.ExecuteScalar()); }
    private static (string MatchId, string PayloadHash)? FindOperation(SqliteConnection c, SqliteTransaction tx, string id) { using var cmd = c.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = "SELECT match_id,payload_hash FROM game_operations WHERE operation_id=$id"; cmd.Parameters.AddWithValue("$id", id); using var r = cmd.ExecuteReader(); return r.Read() ? (r.GetString(0), r.GetString(1)) : null; }
    private static void RecordOperation(SqliteConnection c, SqliteTransaction tx, string op, string id, string payloadHash) { using var cmd = c.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = "INSERT INTO game_operations(operation_id,match_id,payload_hash) VALUES($o,$m,$p)"; cmd.Parameters.AddWithValue("$o", op); cmd.Parameters.AddWithValue("$m", id); cmd.Parameters.AddWithValue("$p", payloadHash); cmd.ExecuteNonQuery(); }
    private static string PayloadHash(string actor, string action, object request) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(actor + "\n" + action + "\n" + JsonSerializer.Serialize(request, Json))));
    private static (string Group, string Challenger, string Opponent, State State)? ReadMatch(SqliteConnection c, SqliteTransaction? tx, string id) { using var cmd = c.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = "SELECT group_id,challenger_id,opponent_id,state_json FROM game_matches WHERE id=$id"; cmd.Parameters.AddWithValue("$id", id); using var r = cmd.ExecuteReader(); if (!r.Read()) return null; var state = JsonSerializer.Deserialize<State>(r.GetString(3), Json)!; if (state.RulesVersion != "mobile-game-v0.3") throw new GameFault(409, "game_rules_version_unsupported"); return (r.GetString(0), r.GetString(1), r.GetString(2), state); }
    private static GameMatchView View(SqliteConnection c, string id, string group, string challenger, string opponent, State s, string viewer, SqliteTransaction? transaction = null)
    {
        var ownIsChallenger = viewer == challenger;
        var oppId = ownIsChallenger ? opponent : challenger;
        var oppName = ownIsChallenger ? s.OpponentName : s.ChallengerName;
        var rounds = s.Rounds.Take(s.CompletedRounds).Select((r, i) => RoundView(i, r, ownIsChallenger, s.CompletedRounds)).ToArray();
        var currentRound = s.Rounds.ElementAtOrDefault(s.CompletedRounds);
        var ownChoiceLocked = currentRound is not null && (ownIsChallenger ? currentRound.ChallengerChoice : currentRound.OpponentChoice) is not null;
        var opponentChoiceLocked = currentRound is not null && (ownIsChallenger ? currentRound.OpponentChoice : currentRound.ChallengerChoice) is not null;
        var outcome = s.Result is null ? null : s.Result == "draw" ? "draw" : (s.Result == (ownIsChallenger ? "challenger" : "opponent") ? "win" : "loss");
        var xp = s.XpAwarded && s.Mode != "yolo" ? (s.Mode == "quick" ? (outcome == "win" ? 10 : 6) : (outcome == "win" ? 20 : 12)) : 0;
        var opponentStats = Player(c, oppId, transaction, group);
        var canCancel = s.Status == "incoming" && ownIsChallenger
            || s.Status == "active"
            || s.Mode == "yolo" && s.Status is "generating" or "failed";
        return new(id, s.Mode, s.Status, s.Revision, s.Status == "incoming" && !ownIsChallenger, canCancel, new(oppId, oppName, opponentStats.Xp, opponentStats.Matches), rounds, s.CompletedRounds,
            ownIsChallenger ? s.ChallengerScore : s.OpponentScore, ownIsChallenger ? s.OpponentScore : s.ChallengerScore,
            s.SparksEnabled,
            s.SparksEnabled ? Math.Max(0, 2 - (ownIsChallenger ? s.ChallengerSparks : s.OpponentSparks)) : 0,
            s.SparksEnabled ? Math.Max(0, 2 - (ownIsChallenger ? s.OpponentSparks : s.ChallengerSparks)) : 0,
            Math.Min(2, s.CompletedRounds / 4 + 1), new Dictionary<string, int>(ownIsChallenger ? s.ChallengerInventory : s.OpponentInventory), new Dictionary<string, int>(ownIsChallenger ? s.OpponentInventory : s.ChallengerInventory), ownChoiceLocked, opponentChoiceLocked, s.TotalRounds,
            s.NeedsLoadout
                ? (ownIsChallenger ? s.ChallengerNextLoadout : s.OpponentNextLoadout)
                : (ownIsChallenger ? s.ChallengerActiveLoadout : s.OpponentActiveLoadout),
            s.TasksEnabled, s.AdultConsent, s.FlirtConsent, outcome == "loss" ? s.PartyTask : null, s.CreatedAt, s.CompletedAt, outcome, xp,
            s.Mode == "yolo" ? new GameYoloView(s.YoloStatus ?? "awaiting_acceptance", s.YoloBeats, s.YoloVerdict, s.YoloWinnerId, s.YoloErrorCode) : null,
            s.RulesVersion, s.LoadoutsEnabled, s.NeedsLoadout,
            (ownIsChallenger ? s.ChallengerNextLoadout : s.OpponentNextLoadout) is not null,
            (ownIsChallenger ? s.OpponentNextLoadout : s.ChallengerNextLoadout) is not null, s.LoadoutsUnlocked);
    }

    private static GameRoundView RoundView(int index, Round round, bool ownIsChallenger, int completedRounds)
    {
        var revealed = index < completedRounds;
        var ownWinner = round.Winner == (ownIsChallenger ? "challenger" : "opponent");
        var draw = round.Winner is null && revealed;
        var winner = !revealed ? null : draw ? "draw" : ownWinner ? "player" : "opponent";
        var ownBid = ownIsChallenger ? round.ChallengerBid : round.OpponentBid;
        var otherBid = ownIsChallenger ? round.OpponentBid : round.ChallengerBid;
        var points = revealed && !draw ? 1 + ownBid + otherBid : 0;
        return new(index + 1, revealed ? (ownIsChallenger ? round.ChallengerChoice : round.OpponentChoice) : null,
            revealed ? (ownIsChallenger ? round.OpponentChoice : round.ChallengerChoice) : null, winner,
            revealed ? ownBid : 0, revealed ? otherBid : 0, winner == "player" ? points : 0, winner == "opponent" ? points : 0,
            !revealed ? null : draw ? "tie" : "cyclic");
    }
    private SqliteConnection Open() { var c = new SqliteConnection(_connectionString); c.Open(); return c; }
    private static Dictionary<string, int> NewInventory() => new() { { "strike", 2 }, { "trick", 2 }, { "shield", 2 } };
    private static readonly Dictionary<string, Dictionary<string, int>> LoadoutStock = new(StringComparer.Ordinal)
    {
        ["balanced"] = new() { { "strike", 2 }, { "trick", 2 }, { "shield", 2 } },
        ["pressure"] = new() { { "strike", 3 }, { "trick", 2 }, { "shield", 1 } },
        ["trickery"] = new() { { "strike", 1 }, { "trick", 3 }, { "shield", 2 } },
        ["fortress"] = new() { { "strike", 2 }, { "trick", 1 }, { "shield", 3 } }
    };
    private sealed class State { public string RulesVersion { get; set; } = "mobile-game-v0.3"; public bool LoadoutsUnlocked { get; set; } public bool ChallengerWantsLoadouts { get; set; } public bool LoadoutsEnabled { get; set; } public bool NeedsLoadout { get; set; } public string? ChallengerNextLoadout { get; set; } public string? OpponentNextLoadout { get; set; } public string? ChallengerActiveLoadout { get; set; } public string? OpponentActiveLoadout { get; set; } public string GroupId { get; set; } = ""; public string Mode { get; set; } = "quick"; public string Status { get; set; } = "incoming"; public long Revision { get; set; } public long CreatedAt { get; set; } public long? CompletedAt { get; set; } public int TotalRounds { get; set; } public int CompletedRounds { get; set; } public int ChallengerScore { get; set; } public int OpponentScore { get; set; } public bool SparksEnabled { get; set; } public int ChallengerSparks { get; set; } public int OpponentSparks { get; set; } public Dictionary<string, int> ChallengerInventory { get; set; } = NewInventory(); public Dictionary<string, int> OpponentInventory { get; set; } = NewInventory(); public string ChallengerId { get; set; } = ""; public string OpponentId { get; set; } = ""; public string ChallengerName { get; set; } = "Player"; public string OpponentName { get; set; } = "Player"; public string OperationId { get; set; } = ""; public bool ChallengerTasks { get; set; } public bool OpponentTasks { get; set; } public bool ChallengerAdultConsent { get; set; } public bool OpponentAdultConsent { get; set; } public bool ChallengerFlirtConsent { get; set; } public bool OpponentFlirtConsent { get; set; } public bool TasksEnabled { get; set; } public bool AdultConsent { get; set; } public bool FlirtConsent { get; set; } public GamePartyTask? PartyTask { get; set; } public int TaskSwapCount { get; set; } public List<Round> Rounds { get; set; } = []; public string? Result { get; set; } public bool XpAwarded { get; set; } public int TaskExchangeCount { get; set; } public string? YoloStatus { get; set; } public string? YoloErrorCode { get; set; } public long YoloGenerationRevision { get; set; } public string? YoloWinnerId { get; set; } public string? YoloVerdict { get; set; } public List<GameYoloBeat> YoloBeats { get; set; } = []; public GameYoloProfile? ChallengerPublicProfile { get; set; } public GameYoloProfile? OpponentPublicProfile { get; set; } }
    private sealed class Round { public string? ChallengerChoice { get; set; } public string? OpponentChoice { get; set; } public int ChallengerBid { get; set; } public int OpponentBid { get; set; } public string? Winner { get; set; } }
}

internal static class GameEndpoints
{
    internal static IServiceCollection AddMobileGame(this IServiceCollection services, IConfiguration configuration)
    {
        if (AccountConfiguration.Read(configuration).Enabled)
        {
            services.AddGameRateLimits();
            services.AddSingleton(sp => new GameStore(sp.GetRequiredService<AccountConfiguration>().DataRoot));
            services.AddSingleton<IGameYoloProvider, OpenAiGameYoloProvider>();
            services.AddSingleton<GameYoloWorker>();
            services.AddHostedService(sp => sp.GetRequiredService<GameYoloWorker>());
        }
        return services;
    }
    internal static void MapMobileGame(this WebApplication app)
    {
        if (!app.Services.GetRequiredService<AccountConfiguration>().Enabled) return;
        var group = app.MapGroup("/app/v1/game").RequireAuthorization().RequireRateLimiting(GameRateLimit.Policy);
        group.AddEndpointFilter(async (invocation, next) =>
        {
            var h = invocation.HttpContext; var id = h.User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (id is null || !h.User.HasClaim(c => c.Type == AccountIdentity.SessionClaim)) return ApiProblem.Create(h, 401, "account_required");
            h.Response.Headers.CacheControl = "no-store";
            var db = h.RequestServices.GetRequiredService<AccountDatabase>();
            var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == id && !u.Disabled, h.RequestAborted);
            if (user?.LaunchGroupId is null || user.LaunchGroupRevokedAt is not null || await db.DeletionRequests.AnyAsync(x => x.UserId == id, h.RequestAborted)) return ApiProblem.Create(h, 403, "game_group_required");
            if (!await (from m in db.UserRoles join r in db.Roles on m.RoleId equals r.Id where m.UserId == id && r.Name == AccountRoles.Customer select m.UserId).AnyAsync(h.RequestAborted)) return ApiProblem.Create(h, 403, "game_group_required");
            var launch = h.RequestServices.GetRequiredService<LaunchPolicyStore>().ReadGroup(user.LaunchGroupId);
            if (launch?.OpenedAt is null) return ApiProblem.Create(h, 403, "game_group_inactive");
            var access = await h.RequestServices.GetRequiredService<LaunchAccessService>().EvaluateAsync(h.User);
            if (!access.Allowed) return ApiProblem.Create(h, 403, "game_launch_unavailable");
            h.Items["dv-game-group"] = user.LaunchGroupId;
            if (h.Request.RouteValues["id"]?.ToString() is { } matchId)
            {
                var match = h.RequestServices.GetRequiredService<GameStore>().Participants(matchId);
                if (match is null || match.Value.GroupId != user.LaunchGroupId
                    || id != match.Value.ChallengerId && id != match.Value.OpponentId)
                    return ApiProblem.Create(h, 404, "game_match_unavailable");
                var participants = new[] { match.Value.ChallengerId, match.Value.OpponentId };
                var eligible = await (from participant in db.Users.AsNoTracking()
                                      join membership in db.UserRoles on participant.Id equals membership.UserId
                                      join role in db.Roles on membership.RoleId equals role.Id
                                      where participants.Contains(participant.Id) && participant.LaunchGroupId == user.LaunchGroupId
                                          && participant.LaunchGroupRevokedAt == null && !participant.Disabled
                                          && role.Name == AccountRoles.Customer
                                          && !db.DeletionRequests.Any(deletion => deletion.UserId == participant.Id)
                                      select participant.Id).Distinct().CountAsync(h.RequestAborted);
                if (eligible != 2) return ApiProblem.Create(h, 404, "game_match_unavailable");
            }
            try
            {
                if (h.Request.Method != "GET" && !h.Request.Path.Value!.EndsWith("/cancel", StringComparison.Ordinal)
                    && !h.Request.Path.Value.EndsWith("/decline", StringComparison.Ordinal) && !h.Request.Path.Value.EndsWith("/task", StringComparison.Ordinal))
                    await MobileWandSessions.RequireForGame(h, id);
                return await next(invocation);
            } catch (GameFault e) { return ApiProblem.Create(h, e.Status, e.Code); }
        });
        group.MapGet("", async (HttpContext h, AccountDatabase db, GameStore store,
            CharacterProfileStore profiles) =>
        {
            var id = h.User.FindFirstValue(ClaimTypes.NameIdentifier)!; var groupId = (string)h.Items["dv-game-group"]!;
            var activePeerQuery = from u in db.Users.AsNoTracking()
                                  join m in db.UserRoles on u.Id equals m.UserId
                                  join r in db.Roles on m.RoleId equals r.Id
                                  where u.LaunchGroupId == groupId && u.LaunchGroupRevokedAt == null && !u.Disabled && r.Name == AccountRoles.Customer && u.Id != id
                                      && !db.DeletionRequests.Any(deletion => deletion.UserId == u.Id)
                                  select u;
            var eligibleIds = (await activePeerQuery.Select(peer => peer.Id).Distinct().ToListAsync(h.RequestAborted)).Append(id).ToHashSet(StringComparer.Ordinal);
            var activeOpponentIds = store.ActiveOpponentIds(groupId, id);
            var peers = await activePeerQuery.Select(peer => new { peer.Id, peer.DisplayName }).Distinct()
                .OrderByDescending(peer => activeOpponentIds.Contains(peer.Id)).ThenBy(peer => peer.DisplayName)
                .Take(100).ToListAsync(h.RequestAborted);
            var name = await db.Users.AsNoTracking().Where(u => u.Id == id).Select(u => u.DisplayName).SingleAsync(h.RequestAborted);
            var opponents = new List<GameLobbyOpponentView>();
            foreach (var peer in peers)
            {
                var peerTier = await MobileWandSessions.Tier(h.RequestServices, peer.Id, h.RequestAborted);
                if (peerTier > 0)
                {
                    var stats = store.Stats(groupId, peer.Id, peer.DisplayName);
                    opponents.Add(new(stats.Id, stats.DisplayName, stats.Xp, stats.MatchesPlayed,
                        HasWand: true, WandTier: peerTier,
                        TacticalUnlocked: stats.Xp >= 30 && peerTier >= 2));
                }
            }
            store.RetireIneligibleMatches(groupId, eligibleIds, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            var lobby = store.Lobby(groupId, id, name, opponents);
            var tier = await MobileWandSessions.Tier(h.RequestServices, id, h.RequestAborted);
            var safeLobby = lobby with
            {
                Player = lobby.Player with { HasWand = tier > 0, WandTier = tier, TacticalUnlocked = lobby.Player.TacticalUnlocked && tier >= 2 },
                Incoming = lobby.Incoming.Where(match => eligibleIds.Contains(match.Opponent.Id)).ToArray(),
                Active = lobby.Active.Where(match => eligibleIds.Contains(match.Opponent.Id)).ToArray(),
                History = lobby.History.Where(match => eligibleIds.Contains(match.Opponent.Id)).ToArray()
            };
            h.Response.Headers.CacheControl = "no-store";
            return Results.Ok(await GameDisplayNames.DecorateAsync(safeLobby, profiles, h.RequestAborted));
        });
        group.MapPost("/matches", async (GameChallenge r, HttpContext h, AccountDatabase db, GameStore store,
            LaunchPolicyStore launch, CharacterProfileStore profiles) =>
        {
            var id = h.User.FindFirstValue(ClaimTypes.NameIdentifier)!; var groupId = (string)h.Items["dv-game-group"]!;
            await MobileWandSessions.RequireForGame(h, id, r.Mode == "tactical");
            var opponent = await (from u in db.Users join m in db.UserRoles on u.Id equals m.UserId join role in db.Roles on m.RoleId equals role.Id where u.Id == r.OpponentId && u.LaunchGroupId == groupId && u.LaunchGroupRevokedAt == null && !u.Disabled && role.Name == AccountRoles.Customer && !db.DeletionRequests.Any(deletion => deletion.UserId == u.Id) select new { u.Id, u.DisplayName }).SingleOrDefaultAsync(h.RequestAborted);
            if (opponent is null) return ApiProblem.Create(h, 404, "game_opponent_unavailable");
            await MobileWandSessions.RequireForGame(h, opponent.Id, r.Mode == "tactical");
            var challenger = await db.Users.AsNoTracking().Where(u => u.Id == id).Select(u => u.DisplayName).SingleAsync(h.RequestAborted);
            var groupSettings = launch.ReadGroup(groupId)!;
            var result = store.Challenge(groupId, id, challenger, opponent.Id, opponent.DisplayName, r,
                DateTimeOffset.UtcNow.ToUnixTimeSeconds(), groupSettings.AdultContentEnabled, groupSettings.FlirtEnabled);
            var decorated = await GameDisplayNames.DecorateAsync(result, profiles, h.RequestAborted);
            return Results.Created($"/app/v1/game/matches/{result.Id}", decorated);
        });
        group.MapGet("/matches/{id}", async (string id, HttpContext h, GameStore store,
            CharacterProfileStore profiles) =>
        {
            var match = store.Read(id, (string)h.Items["dv-game-group"]!,
                h.User.FindFirstValue(ClaimTypes.NameIdentifier)!);
            return Results.Ok(await GameDisplayNames.DecorateAsync(match, profiles, h.RequestAborted));
        });
        foreach (var action in new[] { "accept", "decline", "cancel", "choose", "loadout", "task", "retry" })
            group.MapPost($"/matches/{{id}}/{action}", async (string id, GameCommand request, HttpContext h,
                GameStore store, GameYoloWorker yolo, LaunchPolicyStore launch, CharacterProfileStore profiles) =>
            {
                var groupId = (string)h.Items["dv-game-group"]!;
                var userId = h.User.FindFirstValue(ClaimTypes.NameIdentifier)!;
                var snapshot = store.Read(id, groupId, userId);
                if (action is not ("decline" or "cancel" or "task"))
                {
                    await MobileWandSessions.RequireForGame(h, userId, snapshot.Mode == "tactical");
                    await MobileWandSessions.RequireForGame(h, snapshot.Opponent.Id, snapshot.Mode == "tactical");
                }
                var settings = launch.ReadGroup(groupId)!;
                var result = store.Mutate(id, groupId, userId, action,
                    request, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), settings.AdultContentEnabled, settings.FlirtEnabled);
                if (result.Yolo?.Status == "generating") yolo.Enqueue(id, result.Revision);
                return Results.Ok(await GameDisplayNames.DecorateAsync(result, profiles, h.RequestAborted));
            });
    }
}
