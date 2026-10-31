using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.RateLimiting;
using Kufr.Backend.Features.Access;
using Kufr.Backend.Features.Customers;
using Kufr.Backend.Features.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Kufr.Backend.Features.Game;

internal sealed record ArcadeRunTicket(string RunId, uint Seed, string RulesVersion, long ExpiresAt);
internal sealed record ArcadeFinishCommand(int EndTick, IReadOnlyList<int>? Taps);
internal sealed record ArcadeFinishReceipt(int Score, int Best, bool NewBest);
internal sealed record ArcadeRankingRow(string Id, string DisplayName, int Score, int Rank);
internal sealed record ArcadeView(string RulesVersion, string PlayerId, string GroupId, int Best,
    IReadOnlyList<ArcadeRankingRow> Ranking);

internal static class GameArcadeRules
{
    internal const string Version = "spark-v1";
    internal const string HarderVersion = "spark-v2";
    internal const string NightInventoryVersion = "spark-v3";
    internal const int MaximumTick = 108000;
    internal const int MaximumInputs = 24000;
    internal const int MaximumGates = 1321;
    internal const int MaximumOutstandingRuns = 64;

    internal static string ValidateVersion(string? version)
    {
        if (version is null or Version)
        {
            return Version;
        }
        if (version == HarderVersion)
        {
            return HarderVersion;
        }
        if (version == NightInventoryVersion)
        {
            return NightInventoryVersion;
        }
        throw new GameFault(409, "arcade_rules_unavailable");
    }

    internal static int Verify(uint seed, int endTick, IReadOnlyList<int> taps, string version = Version)
    {
        version = ValidateVersion(version);
        if (version == NightInventoryVersion)
        {
            return GameArcadeV3.Verify(seed, endTick, taps);
        }
        if (endTick is < 1 or > MaximumTick || taps.Count > MaximumInputs)
            throw new GameFault(400, "arcade_replay_invalid");
        var previous = -1;
        foreach (var tap in taps)
        {
            if (tap < 0 || tap >= endTick || tap < previous)
                throw new GameFault(400, "arcade_replay_invalid");
            previous = tap;
        }

        var gates = BuildGates(seed, version);
        var tapIndex = 0;
        var direction = 1;
        var x = 180;
        var cleared = 0;
        for (var tick = 0; tick < endTick; tick++)
        {
            while (tapIndex < taps.Count && taps[tapIndex] == tick)
            {
                direction = -direction;
                tapIndex++;
            }
            var completedTicks = tick + 1;
            var movement = version == HarderVersion && completedTicks % 5 == 0 ? 3 : 2;
            x += direction * movement;
            var scroll = completedTicks * 2 + (version == HarderVersion ? completedTicks / 5 : 0);
            if (x - 7 <= 16 || x + 7 >= 344)
            {
                if (tick + 1 != endTick || tapIndex != taps.Count)
                    throw new GameFault(400, "arcade_replay_invalid");
                return cleared;
            }

            var gate = gates[cleared];
            var centerY = 480 - gate.Distance + scroll;
            if (Collides(x, 480, gate.CenterX, gate.Width, gate.Height, centerY))
            {
                if (tick + 1 != endTick || tapIndex != taps.Count)
                    throw new GameFault(400, "arcade_replay_invalid");
                return cleared;
            }
            if (scroll > gate.Distance + gate.Height / 2 + 7)
                cleared++;
        }

        if (endTick != MaximumTick || tapIndex != taps.Count)
            throw new GameFault(400, "arcade_replay_invalid");
        return cleared;
    }

    private sealed record Gate(int Distance, int CenterX, int Width, int Height);

    private static Gate[] BuildGates(uint seed, string version)
    {
        var gates = new Gate[MaximumGates];
        var center = 244;
        var state = seed == 0 ? 1u : seed;
        for (var i = 0; i < gates.Length; i++)
        {
            var width = i == 0 ? 168 : Math.Max(88, 152 - i / 5 * 4);
            var height = i % 7 == 6 ? 72 : 22;
            if (version == HarderVersion && i > 0 && height == 22)
            {
                width -= 4;
            }
            if (i > 0)
            {
                state ^= state << 13;
                state ^= state >> 17;
                state ^= state << 5;
                center = Math.Clamp(center + ((int)(state % 7) - 3) * 32, 92, 268);
                if (height == 72)
                {
                    width = Math.Max(104, width);
                }
            }
            gates[i] = new(120 + i * 180, center, width, height);
        }
        return gates;
    }

    private static bool Collides(int x, int y, int centerX, int width, int height, int centerY)
    {
        var half = width / 2.0;
        return HitsRectangle(x, y, 16, centerX - half, centerY - height / 2.0, centerY + height / 2.0)
            || HitsRectangle(x, y, centerX + half, 344, centerY - height / 2.0, centerY + height / 2.0);
    }

    private static bool HitsRectangle(int x, int y, double left, double right, double top, double bottom)
    {
        var nearestX = Math.Clamp(x, left, right);
        var nearestY = Math.Clamp(y, top, bottom);
        var dx = x - nearestX;
        var dy = y - nearestY;
        return dx * dx + dy * dy <= 49;
    }
}

internal sealed partial class GameStore
{
    private static void EnsureArcadeTables(SqliteConnection connection, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS game_arcade_courses(group_id TEXT NOT NULL,rules_version TEXT NOT NULL,seed INTEGER NOT NULL,PRIMARY KEY(group_id,rules_version));
            CREATE TABLE IF NOT EXISTS game_arcade_runs(run_id TEXT PRIMARY KEY,operation_id TEXT NOT NULL,user_id TEXT NOT NULL,group_id TEXT NOT NULL,rules_version TEXT NOT NULL,seed INTEGER NOT NULL,expires_at INTEGER NOT NULL,taps_json TEXT,receipt_json TEXT,end_tick INTEGER,UNIQUE(user_id,group_id,operation_id));
            CREATE INDEX IF NOT EXISTS ix_game_arcade_runs_user_group ON game_arcade_runs(user_id,group_id);
            CREATE TABLE IF NOT EXISTS game_arcade_bests(group_id TEXT NOT NULL,user_id TEXT NOT NULL,score INTEGER NOT NULL,PRIMARY KEY(group_id,user_id));
            CREATE TABLE IF NOT EXISTS game_arcade_bests_v2(group_id TEXT NOT NULL,user_id TEXT NOT NULL,score INTEGER NOT NULL,PRIMARY KEY(group_id,user_id));
            CREATE TABLE IF NOT EXISTS game_arcade_bests_v3(group_id TEXT NOT NULL,user_id TEXT NOT NULL,score INTEGER NOT NULL,PRIMARY KEY(group_id,user_id));
            """;
        command.ExecuteNonQuery();
    }

    internal ArcadeRunTicket IssueArcadeRun(string groupId, string userId, string operationId, long now,
        string? rulesVersion = null)
    {
        var version = GameArcadeRules.ValidateVersion(rulesVersion);
        ValidateArcadeOperation(operationId);
        using var connection = Open();
        EnsureArcadeTables(connection);
        using var transaction = connection.BeginTransaction(deferred: false);
        EnsureGameAccount(connection, transaction, userId);
        using (var prune = connection.CreateCommand())
        {
            prune.Transaction = transaction;
            prune.CommandText = "DELETE FROM game_arcade_runs WHERE expires_at<=$now";
            prune.Parameters.AddWithValue("$now", now);
            prune.ExecuteNonQuery();
        }
        var seed = ReadCourseSeed(connection, transaction, groupId, version);
        using (var existing = connection.CreateCommand())
        {
            existing.Transaction = transaction;
            existing.CommandText = "SELECT run_id,seed,rules_version,expires_at FROM game_arcade_runs WHERE user_id=$u AND group_id=$g AND operation_id=$o";
            existing.Parameters.AddWithValue("$u", userId); existing.Parameters.AddWithValue("$g", groupId); existing.Parameters.AddWithValue("$o", operationId);
            using var reader = existing.ExecuteReader();
            if (reader.Read())
            {
                if (reader.GetString(2) != version)
                {
                    throw new GameFault(409, "arcade_rules_conflict");
                }
                return new(reader.GetString(0), (uint)reader.GetInt64(1), reader.GetString(2), reader.GetInt64(3));
            }
        }
        using (var active = connection.CreateCommand())
        {
            active.Transaction = transaction;
            active.CommandText = "SELECT COUNT(*) FROM game_arcade_runs WHERE user_id=$u AND receipt_json IS NULL AND expires_at>$now";
            active.Parameters.AddWithValue("$u", userId); active.Parameters.AddWithValue("$now", now);
            if (Convert.ToInt32(active.ExecuteScalar()) >= GameArcadeRules.MaximumOutstandingRuns)
                throw new GameFault(429, "arcade_run_limit");
        }
        var runId = Guid.NewGuid().ToString("N");
        var expiresAt = checked(now + 7_200_000);
        using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO game_arcade_runs(run_id,operation_id,user_id,group_id,rules_version,seed,expires_at) VALUES($r,$o,$u,$g,$v,$s,$e)";
            insert.Parameters.AddWithValue("$r", runId); insert.Parameters.AddWithValue("$o", operationId); insert.Parameters.AddWithValue("$u", userId);
            insert.Parameters.AddWithValue("$g", groupId); insert.Parameters.AddWithValue("$v", version); insert.Parameters.AddWithValue("$s", (long)seed); insert.Parameters.AddWithValue("$e", expiresAt);
            insert.ExecuteNonQuery();
        }
        transaction.Commit();
        return new(runId, seed, version, expiresAt);
    }

    internal ArcadeFinishReceipt FinishArcadeRun(string groupId, string userId, string runId, int endTick, IReadOnlyList<int>? taps, long now)
    {
        if (taps is null || taps.Count > GameArcadeRules.MaximumInputs)
        {
            throw new GameFault(400, "arcade_replay_invalid");
        }
        using var connection = Open(); EnsureArcadeTables(connection);
        using var transaction = connection.BeginTransaction(deferred: false);
        EnsureGameAccount(connection, transaction, userId);
        string rules; uint seed; long expires; string? priorTaps; string? priorReceipt;
        using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = "SELECT rules_version,seed,expires_at,taps_json,receipt_json FROM game_arcade_runs WHERE run_id=$r AND user_id=$u AND group_id=$g";
            read.Parameters.AddWithValue("$r", runId); read.Parameters.AddWithValue("$u", userId); read.Parameters.AddWithValue("$g", groupId);
            using var reader = read.ExecuteReader();
            if (!reader.Read())
            {
                throw new GameFault(404, "arcade_run_unavailable");
            }
            rules = reader.GetString(0); seed = (uint)reader.GetInt64(1); expires = reader.GetInt64(2);
            priorTaps = reader.IsDBNull(3) ? null : reader.GetString(3); priorReceipt = reader.IsDBNull(4) ? null : reader.GetString(4);
        }
        var bestsTable = ArcadeBestsTable(rules);
        var tapsJson = JsonSerializer.Serialize(taps);
        if (priorReceipt is not null)
        {
            if (priorTaps != tapsJson || !ReadStoredEndTick(connection, transaction, runId, endTick))
            {
                throw new GameFault(409, "arcade_replay_conflict");
            }
            transaction.Commit();
            return JsonSerializer.Deserialize<ArcadeFinishReceipt>(priorReceipt, Json)!;
        }
        if (expires <= now)
        {
            throw new GameFault(410, "arcade_run_expired");
        }
        var score = GameArcadeRules.Verify(seed, endTick, taps, rules);
        int oldBest;
        using (var best = connection.CreateCommand())
        {
            best.Transaction = transaction;
            best.CommandText = $"SELECT score FROM {bestsTable} WHERE group_id=$g AND user_id=$u";
            best.Parameters.AddWithValue("$g", groupId); best.Parameters.AddWithValue("$u", userId);
            oldBest = Convert.ToInt32(best.ExecuteScalar() ?? 0);
        }
        var newBest = score > oldBest;
        var receipt = new ArcadeFinishReceipt(score, Math.Max(score, oldBest), newBest);
        using (var updateBest = connection.CreateCommand())
        {
            updateBest.Transaction = transaction;
            updateBest.CommandText = $"INSERT INTO {bestsTable}(group_id,user_id,score) VALUES($g,$u,$s) ON CONFLICT(group_id,user_id) DO UPDATE SET score=MAX(score,excluded.score)";
            updateBest.Parameters.AddWithValue("$g", groupId); updateBest.Parameters.AddWithValue("$u", userId); updateBest.Parameters.AddWithValue("$s", score);
            updateBest.ExecuteNonQuery();
        }
        using (var updateRun = connection.CreateCommand())
        {
            updateRun.Transaction = transaction;
            updateRun.CommandText = "UPDATE game_arcade_runs SET taps_json=$t,receipt_json=$r,end_tick=$e WHERE run_id=$id AND receipt_json IS NULL";
            updateRun.Parameters.AddWithValue("$t", tapsJson); updateRun.Parameters.AddWithValue("$r", JsonSerializer.Serialize(receipt)); updateRun.Parameters.AddWithValue("$e", endTick); updateRun.Parameters.AddWithValue("$id", runId);
            updateRun.ExecuteNonQuery();
        }
        transaction.Commit();
        return receipt;
    }

    private static uint ReadCourseSeed(SqliteConnection connection, SqliteTransaction transaction, string groupId, string version)
    {
        using var select = connection.CreateCommand(); select.Transaction = transaction;
        select.CommandText = "SELECT seed FROM game_arcade_courses WHERE group_id=$g AND rules_version=$v";
        select.Parameters.AddWithValue("$g", groupId); select.Parameters.AddWithValue("$v", version);
        var value = select.ExecuteScalar();
        if (value is not null)
        {
            return (uint)Convert.ToInt64(value);
        }
        Span<byte> seedBytes = stackalloc byte[sizeof(uint)];
        RandomNumberGenerator.Fill(seedBytes);
        var seed = BitConverter.ToUInt32(seedBytes);
        if (seed == 0)
        {
            seed = 1;
        }
        using var insert = connection.CreateCommand(); insert.Transaction = transaction;
        insert.CommandText = "INSERT INTO game_arcade_courses(group_id,rules_version,seed) VALUES($g,$v,$s)";
        insert.Parameters.AddWithValue("$g", groupId); insert.Parameters.AddWithValue("$v", version); insert.Parameters.AddWithValue("$s", (long)seed); insert.ExecuteNonQuery();
        return seed;
    }

    private static bool ReadStoredEndTick(SqliteConnection connection, SqliteTransaction transaction, string runId, int endTick)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT end_tick FROM game_arcade_runs WHERE run_id=$r"; command.Parameters.AddWithValue("$r", runId);
        return Convert.ToInt32(command.ExecuteScalar()) == endTick;
    }

    internal (int Best, IReadOnlyList<ArcadeRankingRow> Ranking) ReadArcadeRanking(string groupId, string playerId,
        IReadOnlyList<(string Id, string Name)> members, string? rulesVersion = null)
    {
        var bestsTable = ArcadeBestsTable(rulesVersion);
        using var connection = Open(); EnsureArcadeTables(connection);
        var ids = members.Select(member => member.Id).ToHashSet(StringComparer.Ordinal);
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT user_id,score FROM {bestsTable} WHERE group_id=$g"; command.Parameters.AddWithValue("$g", groupId);
        var scores = new Dictionary<string, int>(StringComparer.Ordinal);
        using (var reader = command.ExecuteReader()) while (reader.Read()) scores[reader.GetString(0)] = reader.GetInt32(1);
        var ordered = members.Select(member => (member.Id, member.Name, Score: scores.GetValueOrDefault(member.Id)))
            .OrderByDescending(row => row.Score).ThenBy(row => row.Id, StringComparer.Ordinal).ToArray();
        var ranked = new List<ArcadeRankingRow>();
        var rank = 0;
        for (var index = 0; index < ordered.Length; index++)
        {
            var row = ordered[index];
            if (index == 0 || row.Score != ordered[index - 1].Score)
            {
                rank = index + 1;
            }
            ranked.Add(new(row.Id, row.Name, row.Score, rank));
        }
        var ranking = ranked.ToArray();
        return (ids.Contains(playerId) ? scores.GetValueOrDefault(playerId) : 0, ranking);
    }

    private static void ValidateArcadeOperation(string operationId)
    {
        if (string.IsNullOrWhiteSpace(operationId) || operationId.Length is < 8 or > 80)
        {
            throw new GameFault(400, "operation_id_invalid");
        }
    }

    internal static void DeleteArcadeAccount(SqliteConnection connection, SqliteTransaction transaction, string accountId)
    {
        EnsureArcadeTables(connection, transaction);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM game_arcade_runs WHERE user_id=$u; DELETE FROM game_arcade_bests WHERE user_id=$u; DELETE FROM game_arcade_bests_v2 WHERE user_id=$u; DELETE FROM game_arcade_bests_v3 WHERE user_id=$u";
        command.Parameters.AddWithValue("$u", accountId);
        command.ExecuteNonQuery();
    }

    // The original board stays intact for released clients and pending v1 runs.
    private static string ArcadeBestsTable(string? version) => GameArcadeRules.ValidateVersion(version) switch
    {
        GameArcadeRules.Version => "game_arcade_bests",
        GameArcadeRules.HarderVersion => "game_arcade_bests_v2",
        GameArcadeRules.NightInventoryVersion => "game_arcade_bests_v3",
        _ => throw new GameFault(409, "arcade_rules_unavailable")
    };
}

internal static class GameArcadeEndpoints
{
    internal static void MapMobileGameArcade(this WebApplication app)
    {
        if (!app.Services.GetRequiredService<AccountConfiguration>().Enabled)
        {
            return;
        }
        var group = app.MapGroup("/app/v1/game/arcade").RequireAuthorization().RequireRateLimiting(GameRateLimit.ArcadePolicy);
        group.AddEndpointFilter<GameChangedEndpointFilter>();
        group.AddEndpointFilter(async (invocation, next) =>
        {
            try
            {
                var context = invocation.HttpContext;
                context.Items["arcade-group"] = await GameBossEndpoints.ParticipantGroupAsync(context, requireWand: false);
                context.Response.Headers.CacheControl = "no-store";
                return await next(invocation);
            }
            catch (GameFault fault) { return ApiProblem.Create(invocation.HttpContext, fault.Status, fault.Code); }
        });
        group.MapGet("", async (HttpContext context, AccountDatabase db, GameStore store, CharacterProfileStore profiles) =>
        {
            var requestedVersions = context.Request.Query["rulesVersion"];
            if (requestedVersions.Count > 1)
            {
                throw new GameFault(400, "arcade_rules_invalid");
            }
            var version = GameArcadeRules.ValidateVersion(requestedVersions.FirstOrDefault());
            var groupId = (string)context.Items["arcade-group"]!;
            var members = await GameParticipants.Members(db, groupId).Select(user => new { user.Id, user.DisplayName }).ToListAsync(context.RequestAborted);
            var names = await ResolveNamesAsync(members.Select(row => (row.Id, row.DisplayName)), profiles, db, context.RequestAborted);
            var view = store.ReadArcadeRanking(groupId, ArcadeUser(context), members.Select(row =>
                (row.Id, names.GetValueOrDefault(row.Id, row.DisplayName))).ToArray(), version);
            return Results.Ok(new ArcadeView(version, ArcadeUser(context), groupId, view.Best, view.Ranking));
        });
        group.MapPost("/runs", (ArcadeIssueCommand request, HttpContext context, GameStore store) =>
            Results.Ok(store.IssueArcadeRun((string)context.Items["arcade-group"]!, ArcadeUser(context), request.OperationId,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), request.RulesVersion)));
        group.MapPost("/runs/{runId}/finish", (string runId, ArcadeFinishCommand request, HttpContext context, GameStore store) =>
            Results.Ok(store.FinishArcadeRun((string)context.Items["arcade-group"]!, ArcadeUser(context), runId, request.EndTick,
                request.Taps, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())))
            .WithMetadata(new RequestSizeLimitAttribute(512 * 1024));
    }

    private sealed record ArcadeIssueCommand(string OperationId, string? RulesVersion = null);

    private static async Task<IReadOnlyDictionary<string, string>> ResolveNamesAsync(
        IEnumerable<(string Id, string Name)> players, CharacterProfileStore profiles, AccountDatabase accounts,
        CancellationToken token)
    {
        var uniquePlayers = players.DistinctBy(player => player.Id).ToArray();
        var ids = uniquePlayers.Select(player => player.Id).ToArray();
        var owners = await (from membership in accounts.UserRoles
            join role in accounts.Roles on membership.RoleId equals role.Id
            where ids.Contains(membership.UserId) && role.Name == AccountRoles.Owner
            select membership.UserId).ToHashSetAsync(token);
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var player in uniquePlayers)
        {
            if (owners.Contains(player.Id))
            {
                names[player.Id] = AccountRoles.OwnerDisplayName;
                continue;
            }
            var profile = await profiles.ReadAsync(player.Id, token);
            if (profile is { Label: { Length: > 0 } })
            {
                names[player.Id] = GameDisplayNames.DisplayName(player.Name, profile);
            }
        }
        return names;
    }

    private static string ArcadeUser(HttpContext context) => context.User.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? throw new GameFault(401, "account_required");
}
