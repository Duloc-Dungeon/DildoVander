using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kufr.Backend.Features.Access;
using Kufr.Backend.Features.Identity;
using Kufr.Backend.Features.Launch;
using Microsoft.Data.Sqlite;

namespace Kufr.Backend.Features.Game;

internal sealed record GameBossRickrollCommand(string OperationId, long ExpectedRevision, bool Enabled);
internal sealed record GameBossRickrollClaim(bool Play);

internal sealed partial class GameStore
{
    private static bool ReadBossRickrollEnabled(SqliteConnection connection, SqliteTransaction? transaction, string groupId)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT enabled FROM game_boss_rickroll_groups WHERE group_id=$group";
        command.Parameters.AddWithValue("$group", groupId);
        return Convert.ToInt32(command.ExecuteScalar() ?? 0) != 0;
    }

    private static long ReadBossRickrollRevision(SqliteConnection connection, SqliteTransaction? transaction, string groupId)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT revision FROM game_boss_rickroll_groups WHERE group_id=$group";
        command.Parameters.AddWithValue("$group", groupId);
        return Convert.ToInt64(command.ExecuteScalar() ?? 0);
    }

    private static bool HasBossRickrollClaim(SqliteConnection connection, SqliteTransaction? transaction, string userId)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT 1 FROM game_boss_rickroll_claims WHERE user_id=$user";
        command.Parameters.AddWithValue("$user", userId);
        return command.ExecuteScalar() is not null;
    }

    internal GameBossView SetBossRickroll(string groupId, string actor, GameBossRickrollCommand request)
    {
        if (string.IsNullOrWhiteSpace(request.OperationId) || request.OperationId.Length is < 8 or > 80)
        { throw new GameFault(400, "operation_id_invalid"); }
        lock (_writeGate)
        {
            using var connection = Open(); EnsureBossTables(connection);
            using var transaction = connection.BeginTransaction(deferred: false);
            EnsureGameAccount(connection, transaction, actor);
            var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                JsonSerializer.Serialize(new { actor, groupId, request.ExpectedRevision, request.Enabled })))).ToLowerInvariant();
            using (var replay = connection.CreateCommand())
            {
                replay.Transaction = transaction;
                replay.CommandText = "SELECT actor,fingerprint,group_id FROM game_boss_rickroll_operations WHERE operation_id=$op";
                replay.Parameters.AddWithValue("$op", request.OperationId);
                using var reader = replay.ExecuteReader();
                if (reader.Read())
                {
                    if (reader.GetString(0) != actor || reader.GetString(1) != fingerprint || reader.GetString(2) != groupId)
                    { throw new GameFault(409, "operation_payload_conflict"); }
                    reader.Close();
                    var replayView = ReadBossProfile(connection, transaction, groupId) with
                    { RickrollEnabled = ReadBossRickrollEnabled(connection, transaction, groupId), RickrollRevision = ReadBossRickrollRevision(connection, transaction, groupId) };
                    transaction.Commit();
                    return replayView;
                }
            }
            var revision = ReadBossRickrollRevision(connection, transaction, groupId);
            if (request.ExpectedRevision != revision) { throw new GameFault(409, "boss_rickroll_revision_changed"); }
            using (var update = connection.CreateCommand())
            {
                update.Transaction = transaction;
                update.CommandText = "INSERT INTO game_boss_rickroll_groups(group_id,enabled,revision) VALUES($group,$enabled,1) ON CONFLICT(group_id) DO UPDATE SET enabled=excluded.enabled,revision=game_boss_rickroll_groups.revision+1";
                update.Parameters.AddWithValue("$group", groupId);
                update.Parameters.AddWithValue("$enabled", request.Enabled ? 1 : 0);
                update.ExecuteNonQuery();
            }
            using (var record = connection.CreateCommand())
            {
                record.Transaction = transaction;
                record.CommandText = "INSERT INTO game_boss_rickroll_operations(operation_id,actor,fingerprint,group_id) VALUES($op,$actor,$fingerprint,$group)";
                record.Parameters.AddWithValue("$op", request.OperationId); record.Parameters.AddWithValue("$actor", actor);
                record.Parameters.AddWithValue("$fingerprint", fingerprint); record.Parameters.AddWithValue("$group", groupId);
                record.ExecuteNonQuery();
            }
            var profile = ReadBossProfile(connection, transaction, groupId) with
            { RickrollEnabled = request.Enabled, RickrollRevision = revision + 1 };
            transaction.Commit();
            return profile;
        }
    }

    internal GameBossRickrollClaim ClaimBossRickroll(string groupId, string userId, bool mediaAvailable)
    {
        lock (_writeGate)
        {
            using var connection = Open(); EnsureBossTables(connection);
            using var transaction = connection.BeginTransaction(deferred: false);
            EnsureGameAccount(connection, transaction, userId);
            var boss = ReadBossProfile(connection, transaction, groupId);
            if (!boss.Revealed || !ReadBossRickrollEnabled(connection, transaction, groupId) || !mediaAvailable)
            { transaction.Commit(); return new(false); }
            using var claim = connection.CreateCommand(); claim.Transaction = transaction;
            claim.CommandText = "INSERT INTO game_boss_rickroll_claims(user_id,group_id,claimed_at) VALUES($user,$group,$at) ON CONFLICT(user_id) DO NOTHING";
            claim.Parameters.AddWithValue("$user", userId); claim.Parameters.AddWithValue("$group", groupId);
            claim.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            var inserted = claim.ExecuteNonQuery() == 1;
            transaction.Commit();
            return new(inserted);
        }
    }
}

internal static class GameBossRickrollEndpoints
{
    internal static void MapBossRickroll(this WebApplication app)
    {
        app.MapPost("/app/v1/admin/game/groups/{id}/boss/rickroll", async (string id, GameBossRickrollCommand? request,
            HttpContext context, GameStore store, LaunchPolicyStore launch) =>
        {
            if (!context.User.HasClaim(claim => claim.Type == AccountIdentity.SessionClaim)
                || !context.User.IsInRole(AccountRoles.Owner) && !context.User.IsInRole(AccountRoles.Administrator))
            { return ApiProblem.Create(context, 403, "administrator_required"); }
            var recent = await AccountAuthorization.RequireRecentAsync(context);
            if (recent is not null) { return recent; }
            if (launch.ReadGroup(id)?.OpenedAt is null) { return ApiProblem.Create(context, 409, "game_group_inactive"); }
            var actor = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (actor is null) { return ApiProblem.Create(context, 401, "account_required"); }
            if (request is null) { return ApiProblem.Create(context, 400, "operation_id_invalid"); }
            try { return Results.Ok(store.SetBossRickroll(id, actor, request)); }
            catch (GameFault fault) { return ApiProblem.Create(context, fault.Status, fault.Code); }
        }).RequireAuthorization().RequireRateLimiting("account");

    }
}
