using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kufr.Backend.Features.Access;
using Kufr.Backend.Features.Customers;
using Kufr.Backend.Features.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Kufr.Backend.Features.Game;

internal sealed record GameMilestone(string Id, string Title, string Text, string Action, string Destination);
internal sealed record GameRanking(string Id, string DisplayName, int Xp, int Wins, int Rank);
internal sealed record GameProgressView(long Revision, int TacticalMatches, string KeeperState,
    IReadOnlyList<GameMilestone> Pending, IReadOnlyList<string> Titles,
    IReadOnlyList<GameRanking> CombatRanking, IReadOnlyList<GameRanking> YoloRanking,
    IReadOnlyList<GameMilestone> Messages);
internal sealed record GameProgressCommand(string OperationId, long ExpectedRevision, string EventId);

internal sealed partial class GameStore
{
    private static void EnsureProgressTables(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS game_progress_seen(
                group_id TEXT NOT NULL,user_id TEXT NOT NULL,event_id TEXT NOT NULL,
                PRIMARY KEY(group_id,user_id,event_id));
            CREATE TABLE IF NOT EXISTS game_progress_operations(
                operation_id TEXT PRIMARY KEY,user_id TEXT NOT NULL,fingerprint TEXT NOT NULL);
            """;
        command.ExecuteNonQuery();
    }
    internal GameProgressView ReadProgress(string groupId, string userId, IReadOnlyDictionary<string, string> peers)
    {
        using var connection = Open(); EnsureProgressTables(connection);
        var stats = Player(connection, userId, groupId: groupId);
        var completed = ProgressMatches(connection, groupId, userId);
        var tactical = completed.Count(state => state.Mode == "tactical");
        var seen = ProgressSeen(connection, null, groupId, userId);
        var boss = ReadBoss(groupId);
        var milestones = AvailableMilestones(stats.Xp, stats.Matches, tactical, peers.Count, boss);
        var titles = new List<string>();
        if (completed.Any(state => state.Mode == "yolo")) { titles.Add("Oběť odborníka"); }
        if (completed.Any(state => state.Rounds.Any(round => round.Winner == (state.ChallengerId == userId ? "challenger" : "opponent")
            && (state.ChallengerId == userId ? round.OpponentBid : round.ChallengerBid) > 0)))
        { titles.Add("Čtenář myšlenek"); }
        if (completed.GroupBy(state => state.ChallengerId == userId ? state.OpponentId : state.ChallengerId).Any(group => group.Count() >= 2))
        { titles.Add("Soupeř s pamětí"); }
        var combat = new List<GameRanking>();
        var yolo = new List<GameRanking>();
        foreach (var peer in peers)
        {
            var player = Player(connection, peer.Key, groupId: groupId);
            combat.Add(new(peer.Key, peer.Value, player.Xp, 0, 0));
            var wins = ProgressMatches(connection, groupId, peer.Key).Count(state => state.Mode == "yolo"
                && state.Result == (state.ChallengerId == peer.Key ? "challenger" : "opponent"));
            yolo.Add(new(peer.Key, peer.Value, 0, wins, 0));
        }
        var rankedCombat = Rank(combat, row => row.Xp);
        var rankedYolo = Rank(yolo, row => row.Wins);
        return new(ProgressRevision(stats.Xp, stats.Matches, seen.Count), tactical,
            boss.Revealed ? boss.Rescued ? "rescued" : "captured" : "available",
            milestones.Where(milestone => !seen.Contains(milestone.Id)).ToArray(), titles,
            rankedCombat, rankedYolo, milestones);
    }

    private static GameRanking[] Rank(IEnumerable<GameRanking> rows, Func<GameRanking, int> score)
    {
        var ordered = rows.OrderByDescending(score).ThenBy(row => row.Id, StringComparer.Ordinal).ToArray();
        var ranked = new GameRanking[ordered.Length];
        var previousScore = int.MinValue;
        var currentRank = 0;
        for (var index = 0; index < ordered.Length; index++)
        {
            var value = score(ordered[index]);
            if (index == 0 || value != previousScore) { currentRank = index + 1; }
            ranked[index] = ordered[index] with { Rank = currentRank };
            previousScore = value;
        }
        return ranked;
    }

    internal void AcknowledgeProgress(string groupId, string userId, GameProgressCommand request, int eligiblePlayers)
    {
        if (!Guid.TryParse(request.OperationId, out _)) { throw new GameFault(400, "game_operation_invalid"); }
        lock (_writeGate)
        {
            using var connection = Open(); EnsureProgressTables(connection); EnsureBossTables(connection);
            using var transaction = connection.BeginTransaction(deferred: false);
            EnsureGameAccount(connection, transaction, userId);
            var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { groupId, userId, request }, Json))));
            using var replay = connection.CreateCommand(); replay.Transaction = transaction;
            replay.CommandText = "SELECT user_id,fingerprint FROM game_progress_operations WHERE operation_id=$id";
            replay.Parameters.AddWithValue("$id", request.OperationId);
            using (var reader = replay.ExecuteReader())
            {
                if (reader.Read())
                {
                    if (reader.GetString(0) != userId || reader.GetString(1) != fingerprint) { throw new GameFault(409, "game_operation_conflict"); }
                    return;
                }
            }
            var stats = Player(connection, userId, transaction, groupId);
            var seen = ProgressSeen(connection, transaction, groupId, userId);
            if (request.ExpectedRevision != ProgressRevision(stats.Xp, stats.Matches, seen.Count)) { throw new GameFault(409, "game_revision_conflict"); }
            using var matches = connection.CreateCommand(); matches.Transaction = transaction;
            matches.CommandText = "SELECT COUNT(*) FROM game_matches WHERE group_id=$group AND (challenger_id=$user OR opponent_id=$user) AND json_extract(state_json,'$.status')='complete' AND json_extract(state_json,'$.mode')='tactical'";
            matches.Parameters.AddWithValue("$group", groupId); matches.Parameters.AddWithValue("$user", userId);
            var tactical = Convert.ToInt32(matches.ExecuteScalar());
            var boss = ReadBossProfile(connection, transaction, groupId);
            if (!AvailableMilestones(stats.Xp, stats.Matches, tactical, eligiblePlayers, boss).Any(milestone => milestone.Id == request.EventId))
            { throw new GameFault(409, "game_milestone_unavailable"); }
            using var record = connection.CreateCommand(); record.Transaction = transaction;
            record.CommandText = """
                INSERT INTO game_progress_seen(group_id,user_id,event_id) VALUES($group,$user,$event) ON CONFLICT DO NOTHING;
                INSERT INTO game_progress_operations(operation_id,user_id,fingerprint) VALUES($op,$user,$hash);
                """;
            record.Parameters.AddWithValue("$group", groupId); record.Parameters.AddWithValue("$user", userId);
            record.Parameters.AddWithValue("$event", request.EventId); record.Parameters.AddWithValue("$op", request.OperationId);
            record.Parameters.AddWithValue("$hash", fingerprint); record.ExecuteNonQuery(); transaction.Commit();
        }
    }
    private static long ProgressRevision(int xp, int matches, int seen) => (long)xp * 1_000_000 + (long)matches * 100 + seen;
    private static HashSet<string> ProgressSeen(SqliteConnection connection, SqliteTransaction? transaction, string groupId, string userId)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT event_id FROM game_progress_seen WHERE group_id=$group AND user_id=$user";
        command.Parameters.AddWithValue("$group", groupId); command.Parameters.AddWithValue("$user", userId);
        using var reader = command.ExecuteReader(); var seen = new HashSet<string>(StringComparer.Ordinal);
        while (reader.Read()) { seen.Add(reader.GetString(0)); }
        return seen;
    }
    private static List<State> ProgressMatches(SqliteConnection connection, string groupId, string userId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT state_json FROM game_matches WHERE group_id=$group AND (challenger_id=$user OR opponent_id=$user) AND json_extract(state_json,'$.status')='complete'";
        command.Parameters.AddWithValue("$group", groupId); command.Parameters.AddWithValue("$user", userId);
        using var reader = command.ExecuteReader(); var results = new List<State>();
        while (reader.Read()) { results.Add(JsonSerializer.Deserialize<State>(reader.GetString(0), Json)!); }
        return results;
    }
    private static List<GameMilestone> AvailableMilestones(int xp, int matches, int tactical, int players, GameBossView boss)
    {
        var result = new List<GameMilestone>();
        // A simultaneous upgrade takes priority; the UI presents at most one message per visit.
        if (xp >= 30) { result.Add(new("upgrade", "Tohle dřevo už ti nestačí.", "Ollivander pro tebe připravil hůlku II. Teď plánuješ zásobu kouzel. Hůlka I ti zůstává pro rychlé odvety.", "Navštívit dílnu", "/play/keeper")); }
        if (matches >= 2) { result.Add(new("sparks", "Malá jiskra. Velká drzost.", "Od dalšího společně potvrzeného duelu můžete přisadit dvě jiskry. Když prohraješ výměnu, jejich body získá soupeř.", "Vyzkoušet jiskry", "/play/challenge")); }
        if (tactical >= 2) { result.Add(new("loadouts", "Najdi svůj rukopis.", "Rovnováha, Nátlak, Úskok nebo Opevnění. Šest použití, čtyři výměny. Vlastní sestavu hrajete po společném souhlasu.", "Zkusit sestavu", "/play/challenge")); }
        if (tactical >= 3 && players >= 4) { result.Add(new("teams", "Čtyři hůlky. Jeden plán.", "Najdi spojence. Ve druhém bloku vystřídáte soupeře a můžeš předat jednu jiskru spoluhráči.", "Najít spojence", "/play/teams")); }
        if (matches >= 3) { result.Add(new("yolo", "Pravidla? Ta se poddají.", "Bohumil Křivopísk, rozhodčí bez licence, tě zve do YOLO lóže. AI vymyslí příběh i vítěze. Hraje se bez bojových XP.", "Do YOLO lóže", "/play/challenge?mode=yolo")); }
        if (tactical >= 1) { result.Add(new("clue-one", "Podezřelá reklama", "Nová hůlka prý vyhrává sama. Pod drobným písmem stojí: při reklamaci předložte poraženého soupeře.", "Pokračovat", "/play")); }
        if (tactical >= 3) { result.Add(new("clue-three", "Svědectví poraženého mága", "Prý jde o barona. Má nesnesitelně dobrou hůlku a ještě nesnesitelnější záruční podmínky.", "Pokračovat", "/play")); }
        if (tactical >= 5) { result.Add(new("clue-five", "Ollivander si něčeho všiml.", "Jakmile začne vyhrožovat Úderem, častěji ho opravdu použije. Štít si šetři na chvíli, kdy bude sázka vyšší.", "Pokračovat", "/play")); }
        if (boss.Rescued) { result.Add(new("rescue", "Opona se znovu zvedá.", "Ollivander je volný. Baron má stále záruční list, ale už žádné publikum. Tvoje hůlka i všechny XP zůstávají.", "Zpátky do dílny", "/play/keeper")); }
        return result;
    }
    internal static void DeleteProgressAccount(SqliteConnection connection, SqliteTransaction transaction, string userId)
    {
        using var exists = connection.CreateCommand(); exists.Transaction = transaction;
        exists.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE name='game_progress_seen'";
        if (Convert.ToInt32(exists.ExecuteScalar()) == 0) { return; }
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "DELETE FROM game_progress_seen WHERE user_id=$user; DELETE FROM game_progress_operations WHERE user_id=$user";
        command.Parameters.AddWithValue("$user", userId); command.ExecuteNonQuery();
    }
}

internal static class GameProgressEndpoints
{
    internal static void MapMobileGameProgress(this WebApplication app)
    {
        if (!app.Services.GetRequiredService<AccountConfiguration>().Enabled) { return; }
        var routes = app.MapGroup("/app/v1/game/progress").RequireAuthorization().RequireRateLimiting(GameRateLimit.Policy);
        routes.AddEndpointFilter(async (invocation, next) =>
        {
            try
            {
                invocation.HttpContext.Items["progress-group"] = await GameBossEndpoints.ParticipantGroupAsync(invocation.HttpContext);
                invocation.HttpContext.Response.Headers.CacheControl = "no-store";
                return await next(invocation);
            }
            catch (GameFault fault) { return ApiProblem.Create(invocation.HttpContext, fault.Status, fault.Code); }
        });
        routes.MapGet("", async (HttpContext context, AccountDatabase db, GameStore store,
            CharacterProfileStore profiles) =>
        {
            var groupId = (string)context.Items["progress-group"]!;
            var peers = await ProgressPeersAsync(db, groupId, context.RequestAborted);
            var progress = store.ReadProgress(groupId,
                context.User.FindFirstValue(ClaimTypes.NameIdentifier)!, peers);
            return Results.Ok(await GameDisplayNames.DecorateAsync(progress, profiles, context.RequestAborted));
        });
        routes.MapPost("/ack", async (GameProgressCommand request, HttpContext context, AccountDatabase db, GameStore store) =>
        {
            var groupId = (string)context.Items["progress-group"]!;
            var peers = await ProgressPeersAsync(db, groupId, context.RequestAborted);
            store.AcknowledgeProgress(groupId, context.User.FindFirstValue(ClaimTypes.NameIdentifier)!, request, peers.Count);
            return Results.Ok(new { acknowledged = true });
        });
    }
    private static async Task<Dictionary<string, string>> ProgressPeersAsync(AccountDatabase db, string groupId, CancellationToken token) =>
        await (from user in db.Users.AsNoTracking()
            join member in db.UserRoles on user.Id equals member.UserId
            join role in db.Roles on member.RoleId equals role.Id
            where user.LaunchGroupId == groupId && user.LaunchGroupRevokedAt == null && !user.Disabled
                && role.Name == AccountRoles.Customer && !db.DeletionRequests.Any(deletion => deletion.UserId == user.Id)
            select new { user.Id, user.DisplayName }).ToDictionaryAsync(user => user.Id, user => user.DisplayName, token);
}
