using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kufr.Backend.Features.Access;
using Kufr.Backend.Features.Identity;
using Kufr.Backend.Features.Launch;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Kufr.Backend.Features.Game;

internal sealed record GameTeamCreate(string PartnerId, IReadOnlyList<string> OpponentIds,
    string OperationId, long ExpectedRevision = 0);
internal sealed record GameTeamCommand(string OperationId, long ExpectedRevision, string? Choice = null,
    int SparkBid = 0, bool GiveSpark = false);
internal sealed record GameTeamRound(int Index, int FirstDuelA, int FirstDuelB,
    string? ChoiceA0, string? ChoiceB0, string? ChoiceA1, string? ChoiceB1,
    int BidA0, int BidB0, int BidA1, int BidB1, string? Winner, int TeamAPoints, int TeamBPoints);
internal sealed record GameTeamView(string Id, string Status, long Revision,
    IReadOnlyList<string> TeamMembers, IReadOnlyList<string> Opponents, IReadOnlyList<string> AcceptedBy,
    IReadOnlyList<GameTeamRound> Rounds, int TeamScore, int OpponentScore, int CompletedRounds,
    int OwnSparks, IReadOnlyDictionary<string,int> OwnInventory, bool OwnChoiceLocked, IReadOnlyList<bool> AllFourLocked,
    long? HalftimeEndsAt, string? Result, int XpAwarded,
    IReadOnlyDictionary<string,int> SparkBalances, bool OwnTransferLocked, bool OnTeamA, string RulesVersion = "mobile-game-v0.3");
internal sealed record GameTeamsLobby(IReadOnlyList<GameTeamView> Teams);

internal sealed partial class GameStore
{
    private static readonly string[] TeamCards = ["strike", "trick", "shield"];
    private const int TeamMatchRounds = 8;

    private sealed class TeamState
    {
        public string GroupId { get; set; } = "";
        public string[] A { get; set; } = [];
        public string[] B { get; set; } = [];
        public List<string> Accepted { get; set; } = [];
        public long Revision { get; set; } = 1;
        public string Status { get; set; } = "incoming";
        public long CreatedAt { get; set; }
        public long? HalftimeEndsAt { get; set; }
        public Dictionary<string,int> HalftimeBaseline { get; set; } = new();
        public Dictionary<string,int> Sparks { get; set; } = new();
        public Dictionary<string,Dictionary<string,int>> Inventory { get; set; } = new();
        public Dictionary<int,Dictionary<string,TeamLockedChoice>> Locked { get; set; } = new();
        public Dictionary<string,int> Transfers { get; set; } = new();
        public List<GameTeamRound> Rounds { get; set; } = [];
        public string? Result { get; set; }
        public bool XpAwarded { get; set; }
    }
    private sealed record TeamLockedChoice(string Choice, int Bid);
    private sealed record TeamOp(string Hash, string TeamId);

    private static void EnsureTeamTables(SqliteConnection c)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "CREATE TABLE IF NOT EXISTS game_team_matches(id TEXT PRIMARY KEY,group_id TEXT NOT NULL,state_json TEXT NOT NULL,created_at INTEGER NOT NULL); CREATE INDEX IF NOT EXISTS ix_game_team_group ON game_team_matches(group_id,created_at); CREATE TABLE IF NOT EXISTS game_team_operations(operation_id TEXT PRIMARY KEY,payload_hash TEXT NOT NULL,team_id TEXT NOT NULL);";
        cmd.ExecuteNonQuery();
    }

    internal GameTeamView CreateTeamMatch(string groupId, string actor, GameTeamCreate request, long now)
    {
        ValidateTeamOperation(request.OperationId);
        if (request.ExpectedRevision != 0) { throw new GameFault(409, "team_revision_conflict"); }
        if (string.IsNullOrWhiteSpace(request.PartnerId) || request.OpponentIds is null || request.OpponentIds.Count != 2)
        { throw new GameFault(400, "team_participants_invalid"); }
        var participants = new[] { actor, request.PartnerId, request.OpponentIds[0], request.OpponentIds[1] };
        if (participants.Distinct(StringComparer.Ordinal).Count() != 4) { throw new GameFault(400, "team_participants_invalid"); }
        lock (_writeGate)
        {
            using var c = Open(); EnsureTeamTables(c); using var tx = c.BeginTransaction(deferred: false);
            foreach (var user in participants) { EnsureGameAccount(c, tx, user); }
            var hash = TeamHash(actor, "create", request);
            if (FindTeamOperation(c, tx, request.OperationId, hash) is { } replay) { var prior = ReadTeamState(c, tx, replay.TeamId, groupId); tx.Commit(); return TeamView(prior, actor, replay.TeamId); }
            if (ActiveTeamParticipation(c, tx, groupId, participants)) { throw new GameFault(409, "team_match_already_active"); }
            foreach (var user in participants)
            {
                if (Player(c, user, tx, groupId).Xp < 30) { throw new GameFault(403, "team_match_locked"); }
            }
            var state = new TeamState { GroupId = groupId, A = participants[..2], B = participants[2..], CreatedAt = checked((int)now), Accepted = [actor],
                Sparks = participants.ToDictionary(x => x, _ => 2), Inventory = participants.ToDictionary(x => x, _ => TeamCards.ToDictionary(card => card, _ => 2)) };
            var id = Guid.NewGuid().ToString("N"); SaveTeam(c, tx, id, state, true); RecordTeamOperation(c, tx, request.OperationId, hash, id);
            tx.Commit(); return TeamView(state, actor, id);
        }
    }

    internal GameTeamsLobby ListTeamMatches(string groupId, string actor)
    {
        using var c = Open(); EnsureTeamTables(c); using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT id,state_json FROM game_team_matches WHERE group_id=$g
                AND (EXISTS(SELECT 1 FROM json_each(state_json,'$.a') WHERE value=$u)
                    OR EXISTS(SELECT 1 FROM json_each(state_json,'$.b') WHERE value=$u))
            ORDER BY CASE WHEN json_extract(state_json,'$.status') IN ('incoming','active','halftime') THEN 0 ELSE 1 END,created_at DESC LIMIT 100
            """;
        cmd.Parameters.AddWithValue("$g", groupId); cmd.Parameters.AddWithValue("$u", actor);
        var result = new List<GameTeamView>(); using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var state = JsonSerializer.Deserialize<TeamState>(reader.GetString(1), Json)!;
            if (state.A.Contains(actor, StringComparer.Ordinal) || state.B.Contains(actor, StringComparer.Ordinal)) { result.Add(TeamView(state, actor, reader.GetString(0))); }
        }
        return new(result);
    }

    internal GameTeamView ReadTeamMatch(string id, string groupId, string actor, long now)
        => MutateTeam(id, groupId, actor, "read", new("read-op-" + Guid.NewGuid().ToString("N"), 0), now);

    internal void RetireIneligibleTeams(string groupId, IReadOnlySet<string> eligible)
    {
        lock (_writeGate)
        {
            using var connection = Open(); EnsureTeamTables(connection);
            using var transaction = connection.BeginTransaction(deferred: false);
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "SELECT id,state_json FROM game_team_matches WHERE group_id=$group AND json_extract(state_json,'$.status') IN ('incoming','active','halftime')";
            command.Parameters.AddWithValue("$group", groupId);
            var retired = new List<(string Id, TeamState State)>();
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    var state = JsonSerializer.Deserialize<TeamState>(reader.GetString(1), Json)!;
                    if (state.A.Concat(state.B).Any(user => !eligible.Contains(user)))
                    { state.Status = "cancelled"; state.Revision++; retired.Add((reader.GetString(0), state)); }
                }
            }
            foreach (var match in retired) { SaveTeam(connection, transaction, match.Id, match.State, false); }
            transaction.Commit();
        }
    }

    internal GameTeamView MutateTeam(string id, string groupId, string actor, string action, GameTeamCommand request, long now)
    {
        ValidateTeamOperation(request.OperationId);
        lock (_writeGate)
        {
            using var c = Open(); EnsureTeamTables(c); using var tx = c.BeginTransaction(deferred: false);
            var state = ReadTeamState(c, tx, id, groupId);
            if (!state.A.Contains(actor, StringComparer.Ordinal) && !state.B.Contains(actor, StringComparer.Ordinal)) { throw new GameFault(404, "team_match_unavailable"); }
            foreach (var participant in state.A.Concat(state.B)) { EnsureGameAccount(c, tx, participant); }
            var halftimeDue = state.Status == "halftime" && state.HalftimeEndsAt is not null && now >= state.HalftimeEndsAt;
            var hash = TeamHash(actor, action, request);
            if (action != "read" && FindTeamOperation(c, tx, request.OperationId, hash) is { } replay)
            {
                if (replay.TeamId != id) { throw new GameFault(409, "operation_already_used"); }
                tx.Commit(); return TeamView(state, actor, id);
            }
            if (action != "read" && state.Revision != request.ExpectedRevision) { throw new GameFault(409, "team_revision_conflict"); }
            if (action == "read") { }
            else if (action == "accept")
            {
                if (state.Status != "incoming" || !state.A.Concat(state.B).Contains(actor, StringComparer.Ordinal)) { throw new GameFault(409, "team_action_unavailable"); }
                if (!state.Accepted.Contains(actor, StringComparer.Ordinal)) { state.Accepted.Add(actor); }
                if (state.Accepted.Count == 4) { state.Status = "active"; }
            }
            else if (action is "decline" or "cancel")
            {
                if ((action == "decline" && state.Status != "incoming")
                    || (action == "cancel" && state.Status is not ("incoming" or "active" or "halftime")))
                { throw new GameFault(409, "team_action_unavailable"); }
                state.Status = "cancelled";
            }
            else if (action == "choose") { LockTeamChoice(state, actor, request, now); }
            else if (action == "transfer") { RecordTransfer(state, actor, request, now); }
            else { throw new GameFault(400, "team_action_invalid"); }

            ResolveHalftime(state, now);
            if (action != "read") { state.Revision++; RecordTeamOperation(c, tx, request.OperationId, hash, id); }
            else if (halftimeDue) { state.Revision++; }
            if (state.Status == "complete" && !state.XpAwarded) { AwardTeamXp(c, tx, state); }
            SaveTeam(c, tx, id, state, false);
            tx.Commit(); return TeamView(state, actor, id);
        }
    }

    private static void LockTeamChoice(TeamState state, string actor, GameTeamCommand request, long now)
    {
        ResolveHalftime(state, now);
        if (state.Status != "active") { throw new GameFault(409, "team_action_unavailable"); }
        if (state.HalftimeEndsAt is not null && now < state.HalftimeEndsAt) { throw new GameFault(409, "team_halftime_active"); }
        var choice = request.Choice;
        if (choice is not ("strike" or "trick" or "shield") || request.SparkBid is < 0 or > 1) { throw new GameFault(400, "team_choice_invalid"); }
        var participants = state.A.Concat(state.B).ToArray();
        var roundIndex = state.Rounds.Count + 1;
        if (state.Inventory[actor][choice] <= 0 || request.SparkBid > state.Sparks[actor]) { throw new GameFault(409, "team_inventory_exhausted"); }
        if (state.Locked.TryGetValue(roundIndex, out var locked) && locked.ContainsKey(actor)) { throw new GameFault(409, "team_choice_locked"); }
        if (!state.Locked.ContainsKey(roundIndex)) { state.Locked[roundIndex] = new(); }
        state.Locked[roundIndex][actor] = new(choice, request.SparkBid);
        if (state.Locked[roundIndex].Count == 4)
        {
            foreach (var entry in state.Locked[roundIndex])
            {
                state.Inventory[entry.Key][entry.Value.Choice]--;
                state.Sparks[entry.Key] -= entry.Value.Bid;
            }
            ResolveTeamRound(state, roundIndex);
            if (roundIndex == 4)
            {
                state.Status = "halftime"; state.HalftimeEndsAt = now + 15;
                state.HalftimeBaseline = new(state.Sparks);
            }
            else if (roundIndex == TeamMatchRounds)
            {
                state.Status = "complete";
                var aScore = state.Rounds.Sum(round => round.TeamAPoints); var bScore = state.Rounds.Sum(round => round.TeamBPoints);
                state.Result = aScore == bScore ? "draw" : aScore > bScore ? "a" : "b";
            }
        }
    }

    private static void ResolveHalftime(TeamState state, long now)
    {
        if (state.Status != "halftime" || state.HalftimeEndsAt is null || now < state.HalftimeEndsAt) { return; }
        foreach (var user in state.A.Concat(state.B)) { state.Inventory[user] = TeamCards.ToDictionary(card => card, _ => 2); }
        foreach (var team in new[] { state.A, state.B })
        {
            var outgoing0 = state.Transfers.GetValueOrDefault(team[0]); var outgoing1 = state.Transfers.GetValueOrDefault(team[1]);
            if (outgoing0 != outgoing1)
            {
                var sender = outgoing0 > 0 ? team[0] : team[1]; var recipient = outgoing0 > 0 ? team[1] : team[0];
                state.Sparks[sender] = state.HalftimeBaseline.GetValueOrDefault(sender) - 1;
                state.Sparks[recipient] = state.HalftimeBaseline.GetValueOrDefault(recipient) + 1;
            }
            else { state.Sparks[team[0]] = state.HalftimeBaseline.GetValueOrDefault(team[0]); state.Sparks[team[1]] = state.HalftimeBaseline.GetValueOrDefault(team[1]); }
        }
        state.Transfers.Clear(); state.Status = "active"; state.HalftimeEndsAt = null;
    }

    private static void RecordTransfer(TeamState state, string actor, GameTeamCommand request, long now)
    {
        ResolveHalftime(state, now);
        if (state.Status != "halftime" || state.HalftimeEndsAt is null || now >= state.HalftimeEndsAt) { throw new GameFault(409, "team_transfer_unavailable"); }
        var team = state.A.Contains(actor, StringComparer.Ordinal) ? state.A : state.B;
        var receiver = team.Single(user => user != actor);
        if (state.Transfers.ContainsKey(actor)) { throw new GameFault(409, "team_transfer_already_sent"); }
        if (request.GiveSpark && state.HalftimeBaseline.GetValueOrDefault(actor) <= 0) { throw new GameFault(409, "team_sparks_exhausted"); }
        state.Transfers[actor] = request.GiveSpark ? 1 : 0;
    }

    private static void ResolveTeamRound(TeamState state, int round)
    {
        var choices = state.Locked[round]; var a0 = state.A[0]; var a1 = state.A[1]; var b0 = state.B[0]; var b1 = state.B[1];
        var leftB = round <= 4 ? b0 : b1; var rightB = round <= 4 ? b1 : b0;
        var left = TeamDuel(choices[a0], choices[leftB]); var right = TeamDuel(choices[a1], choices[rightB]);
        var aPoints = Math.Max(0, left) + Math.Max(0, right);
        var bPoints = Math.Max(0, -left) + Math.Max(0, -right);
        state.Rounds.Add(new(round, round <= 4 ? 0 : 1, round <= 4 ? 1 : 0,
            choices[a0].Choice, choices[leftB].Choice, choices[a1].Choice, choices[rightB].Choice,
            choices[a0].Bid, choices[leftB].Bid, choices[a1].Bid, choices[rightB].Bid,
            aPoints == bPoints ? "draw" : aPoints > bPoints ? "a" : "b", aPoints, bPoints));
    }

    private static int TeamDuel(TeamLockedChoice a, TeamLockedChoice b)
    {
        if (a.Choice == b.Choice) { return 0; }
        var aWins = a.Choice == "strike" && b.Choice == "trick" || a.Choice == "trick" && b.Choice == "shield" || a.Choice == "shield" && b.Choice == "strike";
        return (aWins ? 1 : -1) * (1 + a.Bid + b.Bid);
    }

    private static void AwardTeamXp(SqliteConnection c, SqliteTransaction tx, TeamState state)
    {
        var aScore = state.Rounds.Sum(round => round.TeamAPoints); var bScore = state.Rounds.Sum(round => round.TeamBPoints);
        foreach (var user in state.A) { AddXp(c, tx, state.GroupId, user, aScore == bScore ? 12 : aScore > bScore ? 20 : 12); }
        foreach (var user in state.B) { AddXp(c, tx, state.GroupId, user, aScore == bScore ? 12 : bScore > aScore ? 20 : 12); }
        state.XpAwarded = true;
    }

    private static GameTeamView TeamView(TeamState state, string actor, string id)
    {
        var ownLocked = state.Locked.TryGetValue(state.Rounds.Count + 1, out var locked) && locked.ContainsKey(actor);
        var onA = state.A.Contains(actor, StringComparer.Ordinal);
        var members = onA ? state.A : state.B; var opponents = onA ? state.B : state.A;
        var revealed = state.Rounds.ToArray();
        var aScore = revealed.Sum(round => round.TeamAPoints); var bScore = revealed.Sum(round => round.TeamBPoints);
        var teamResult = aScore == bScore ? "draw" : (onA ? aScore > bScore : bScore > aScore) ? "win" : "loss";
        var xpAwarded = state.XpAwarded ? teamResult == "draw" || teamResult == "loss" ? 12 : 20 : 0;
        return new(id, state.Status, state.Revision, members, opponents, state.Accepted, revealed,
            onA ? aScore : bScore, onA ? bScore : aScore, revealed.Length,
            state.Sparks.GetValueOrDefault(actor), new Dictionary<string,int>(state.Inventory.GetValueOrDefault(actor) ?? new()), ownLocked,
            state.A.Concat(state.B).Select(user => locked?.ContainsKey(user) == true).ToArray(),
            state.HalftimeEndsAt, state.XpAwarded ? teamResult : null, xpAwarded,
            new Dictionary<string,int>(state.Sparks), state.Transfers.ContainsKey(actor), onA);
    }

    private static void SaveTeam(SqliteConnection c, SqliteTransaction tx, string id, TeamState state, bool insert)
    {
        using var cmd = c.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = insert ? "INSERT INTO game_team_matches(id,group_id,state_json,created_at) VALUES($id,$g,$s,$n)" : "UPDATE game_team_matches SET state_json=$s WHERE id=$id AND group_id=$g";
        cmd.Parameters.AddWithValue("$id", id); cmd.Parameters.AddWithValue("$g", state.GroupId); cmd.Parameters.AddWithValue("$s", JsonSerializer.Serialize(state, Json)); cmd.Parameters.AddWithValue("$n", state.CreatedAt); cmd.ExecuteNonQuery();
    }
    private static TeamState ReadTeamState(SqliteConnection c, SqliteTransaction tx, string id, string group)
    {
        using var cmd = c.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = "SELECT state_json FROM game_team_matches WHERE id=$id AND group_id=$g";
        cmd.Parameters.AddWithValue("$id", id); cmd.Parameters.AddWithValue("$g", group);
        var json = cmd.ExecuteScalar() as string ?? throw new GameFault(404, "team_match_unavailable");
        var state = JsonSerializer.Deserialize<TeamState>(json, Json)!;
        return state;
    }
    private static bool ActiveTeamParticipation(SqliteConnection c, SqliteTransaction tx, string group, string[] participants)
    {
        using var cmd = c.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = "SELECT state_json FROM game_team_matches WHERE group_id=$g"; cmd.Parameters.AddWithValue("$g", group);
        using var reader = cmd.ExecuteReader(); while (reader.Read()) { var s = JsonSerializer.Deserialize<TeamState>(reader.GetString(0), Json)!; if (s.Status is "incoming" or "active" or "halftime") if (s.A.Concat(s.B).Intersect(participants, StringComparer.Ordinal).Any()) return true; } return false;
    }
    private static TeamOp? FindTeamOperation(SqliteConnection c, SqliteTransaction tx, string id, string hash)
    {
        using var cmd = c.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = "SELECT payload_hash,team_id FROM game_team_operations WHERE operation_id=$id"; cmd.Parameters.AddWithValue("$id", id);
        using var reader = cmd.ExecuteReader(); if (!reader.Read()) return null; if (reader.GetString(0) != hash) { throw new GameFault(409, "operation_payload_conflict"); } return new(reader.GetString(0), reader.GetString(1));
    }
    private static void RecordTeamOperation(SqliteConnection c, SqliteTransaction tx, string id, string hash, string team)
    {
        using var cmd = c.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = "INSERT INTO game_team_operations(operation_id,payload_hash,team_id) VALUES($id,$h,$t)";
        cmd.Parameters.AddWithValue("$id", id); cmd.Parameters.AddWithValue("$h", hash); cmd.Parameters.AddWithValue("$t", team); cmd.ExecuteNonQuery();
    }
    private static void ValidateTeamOperation(string id) { if (string.IsNullOrWhiteSpace(id) || id.Length is < 8 or > 80) { throw new GameFault(400, "operation_id_invalid"); } }
    private static string TeamHash(string actor, string action, object request) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(actor + "|" + action + "|" + JsonSerializer.Serialize(request, Json))));

    internal static void DeleteTeamAccount(SqliteConnection c, SqliteTransaction tx, string user)
    {
        using (var exists = c.CreateCommand())
        {
            exists.Transaction = tx; exists.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name='game_team_matches'";
            if (exists.ExecuteScalar() is null) { return; }
        }
        using var cmd = c.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = "DELETE FROM game_team_matches WHERE json_extract(state_json,'$.a[0]')=$u OR json_extract(state_json,'$.a[1]')=$u OR json_extract(state_json,'$.b[0]')=$u OR json_extract(state_json,'$.b[1]')=$u";
        cmd.Parameters.AddWithValue("$u", user); cmd.ExecuteNonQuery();
        using var ops = c.CreateCommand(); ops.Transaction = tx; ops.CommandText = "DELETE FROM game_team_operations WHERE team_id NOT IN (SELECT id FROM game_team_matches)"; ops.ExecuteNonQuery();
    }
}

internal static class GameTeamEndpoints
{
    internal static void MapMobileGameTeams(this WebApplication app)
    {
        if (!app.Services.GetRequiredService<AccountConfiguration>().Enabled) { return; }
        var group = app.MapGroup("/app/v1/game/teams").RequireAuthorization().RequireRateLimiting(GameRateLimit.Policy);
        group.AddEndpointFilter(async (invocation, next) =>
        {
            var context = invocation.HttpContext;
            try
            {
                var groupId = await ParticipantGroupAsync(context);
                context.Items["team-group"] = groupId;
                var db = context.RequestServices.GetRequiredService<AccountDatabase>();
                var eligible = await db.Users.AsNoTracking().Where(user => user.LaunchGroupId == groupId
                    && user.LaunchGroupRevokedAt == null && !user.Disabled
                    && !db.DeletionRequests.Any(deletion => deletion.UserId == user.Id)
                    && db.UserRoles.Any(member => member.UserId == user.Id && db.Roles.Any(role => role.Id == member.RoleId && role.Name == AccountRoles.Customer)))
                    .Select(user => user.Id).ToHashSetAsync(context.RequestAborted);
                context.Items["team-eligible"] = eligible;
                context.RequestServices.GetRequiredService<GameStore>().RetireIneligibleTeams(groupId, eligible);
                context.Response.Headers.CacheControl = "no-store";
                return await next(invocation);
            }
            catch (GameFault fault) { return ApiProblem.Create(context, fault.Status, fault.Code); }
        });
        group.MapGet("", (HttpContext context, GameStore store) =>
        {
            var eligible = (HashSet<string>)context.Items["team-eligible"]!;
            var lobby = store.ListTeamMatches(Group(context), User(context));
            return Results.Ok(new GameTeamsLobby(lobby.Teams.Where(match => match.TeamMembers.Concat(match.Opponents).All(eligible.Contains)).ToArray()));
        });
        group.MapPost("", async (GameTeamCreate request, HttpContext context, GameStore store) =>
        {
            if (request.OpponentIds is null || request.OpponentIds.Count != 2) { throw new GameFault(400, "team_participants_invalid"); }
            await EnsureLiveParticipants(context, [User(context), request.PartnerId, .. request.OpponentIds]);
            return Results.Ok(store.CreateTeamMatch(Group(context), User(context), request, DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
        });
        group.MapGet("/{id}", async (string id, HttpContext context, GameStore store) =>
        {
            var result = store.ReadTeamMatch(id, Group(context), User(context), DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            await EnsureLiveParticipants(context, result.TeamMembers.Concat(result.Opponents)); return Results.Ok(result);
        });
        foreach (var action in new[] { "accept", "decline", "cancel", "choose", "transfer" })
        {
            group.MapPost($"/{{id}}/{action}", async (string id, GameTeamCommand request, HttpContext context, GameStore store) =>
            {
                var prior = store.ReadTeamMatch(id, Group(context), User(context), DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                await EnsureLiveParticipants(context, prior.TeamMembers.Concat(prior.Opponents));
                return Results.Ok(store.MutateTeam(id, Group(context), User(context), action, request, DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
            });
        }
    }
    private static string User(HttpContext context) => context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? throw new GameFault(401, "account_required");
    private static string Group(HttpContext context) => (string)context.Items["team-group"]!;
    private static async Task EnsureLiveParticipants(HttpContext context, IEnumerable<string> participants)
    {
        var ids = participants.Distinct(StringComparer.Ordinal).ToArray();
        if (ids.Length != 4) { throw new GameFault(404, "team_match_unavailable"); }
        var group = Group(context); var db = context.RequestServices.GetRequiredService<AccountDatabase>();
        var live = await db.Users.AsNoTracking().Where(u => ids.Contains(u.Id) && u.LaunchGroupId == group
            && !u.Disabled && u.LaunchGroupRevokedAt == null && !db.DeletionRequests.Any(d => d.UserId == u.Id)
            && db.UserRoles.Any(m => m.UserId == u.Id && db.Roles.Any(r => r.Id == m.RoleId && r.Name == AccountRoles.Customer)))
            .Select(u => u.Id).ToListAsync(context.RequestAborted);
        if (live.Count != 4) { throw new GameFault(403, "team_participant_unavailable"); }
    }
    private static async Task<string> ParticipantGroupAsync(HttpContext context)
    {
        var id = User(context); if (!context.User.HasClaim(c => c.Type == AccountIdentity.SessionClaim)) { throw new GameFault(401, "account_required"); }
        var db = context.RequestServices.GetRequiredService<AccountDatabase>();
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == id && !u.Disabled && u.LaunchGroupRevokedAt == null
            && !db.DeletionRequests.Any(d => d.UserId == id), context.RequestAborted);
        if (user?.LaunchGroupId is null || !context.User.IsInRole(AccountRoles.Customer)) { throw new GameFault(403, "game_group_required"); }
        if (context.RequestServices.GetRequiredService<LaunchPolicyStore>().ReadGroup(user.LaunchGroupId)?.OpenedAt is null)
        { throw new GameFault(403, "game_group_inactive"); }
        if (!(await context.RequestServices.GetRequiredService<LaunchAccessService>().EvaluateAsync(context.User)).Allowed) { throw new GameFault(403, "game_group_inactive"); }
        return user.LaunchGroupId;
    }
}
