using Kufr.Backend.Features.Customers;
using Kufr.Backend.Features.Game;

namespace Kufr.Backend.Features.Wands;

internal static class MobileWandSessions
{
    internal static GeneratedWandSnapshot? Current(WandGenerationStore store, string owner, string profileId, long revision) =>
        store.LatestMobileSelected(owner, profileId, revision) ?? store.LatestBonded(owner, profileId, revision);

    internal static async Task<int> Tier(IServiceProvider services, string owner, CancellationToken token)
    {
        var profile = await services.GetRequiredService<CharacterProfileStore>().ReadAsync(owner, token);
        if (profile is null) return 0;
        var store = services.GetRequiredService<WandGenerationStore>();
        return Current(store, owner, profile.ProfileId, profile.Revision) is null ? 0
            : Math.Max(1, store.MobileTier(owner, profile.ProfileId, profile.Revision));
    }

    internal static async Task RequireForGame(HttpContext context, string owner, bool tactical = false)
    {
        var tier = await Tier(context.RequestServices, owner, context.RequestAborted);
        if (tier == 0) throw new GameFault(409, "game_wand_required");
        if (tactical && tier < 2) throw new GameFault(409, "game_wand_upgrade_required");
    }
}

internal sealed partial class WandGenerationStore
{
    private static void EnsureMobile(Microsoft.Data.Sqlite.SqliteConnection connection) => Execute(connection, null, """
        CREATE TABLE IF NOT EXISTS mobile_wand_crafts(
            account_id TEXT NOT NULL,operation_id TEXT NOT NULL,profile_id TEXT NOT NULL,
            profile_revision INTEGER NOT NULL,tier INTEGER NOT NULL,PRIMARY KEY(account_id,operation_id));
        CREATE TABLE IF NOT EXISTS mobile_wand_voice_leases(account_id TEXT PRIMARY KEY,lease TEXT NOT NULL,expires_at INTEGER NOT NULL);
        CREATE UNIQUE INDEX IF NOT EXISTS ix_mobile_wand_tier_revision ON mobile_wand_crafts(account_id,profile_id,profile_revision,tier);
        """);

    internal string? MobileOperation(string owner, string profileId, long revision, int tier)
    {
        using var connection = Open(); EnsureMobile(connection);
        using var command = Command(connection, null, "SELECT operation_id FROM mobile_wand_crafts WHERE account_id=$a AND profile_id=$p AND profile_revision=$r AND tier=$t",
            ("$a", owner), ("$p", profileId), ("$r", revision), ("$t", tier));
        return command.ExecuteScalar() as string;
    }

    internal int MobileOperationTier(string owner, string operation)
    {
        using var connection = Open(); EnsureMobile(connection);
        using var command = Command(connection, null, "SELECT tier FROM mobile_wand_crafts WHERE account_id=$a AND operation_id=$o", ("$a", owner), ("$o", operation));
        return command.ExecuteScalar() is { } tier ? Convert.ToInt32(tier) : 0;
    }

    internal string? ClaimMobileVoice(string owner)
    {
        using var connection = Open(); EnsureMobile(connection);
        var lease = Guid.NewGuid().ToString("N");
        using var command = Command(connection, null, """
            INSERT INTO mobile_wand_voice_leases(account_id,lease,expires_at) VALUES($a,$l,$until)
            ON CONFLICT(account_id) DO UPDATE SET lease=$l,expires_at=$until
            WHERE mobile_wand_voice_leases.expires_at<=$now
            """, ("$a", owner), ("$l", lease), ("$until", Now + 210), ("$now", Now));
        return command.ExecuteNonQuery() == 1 ? lease : null;
    }

    internal void ReleaseMobileVoice(string owner, string lease)
    {
        using var connection = Open(); EnsureMobile(connection);
        Execute(connection, null, "DELETE FROM mobile_wand_voice_leases WHERE account_id=$a AND lease=$l", ("$a", owner), ("$l", lease));
    }

    internal void BeginMobile(string owner, string operationId, CharacterConversationSnapshot profile, int tier)
    {
        using var connection = Open(); EnsureMobile(connection);
        using var transaction = connection.BeginTransaction(deferred: false);
        using (var imported = Command(connection, transaction, """
            SELECT 1 FROM wand_generation_sessions WHERE account_id=$a AND operation_id=$o
            AND NOT EXISTS(SELECT 1 FROM mobile_wand_crafts WHERE account_id=$a AND operation_id=$o)
            """, ("$a", owner), ("$o", operationId)))
            if (imported.ExecuteScalar() is not null) throw new WandGenerationException("wand_operation_conflict");
        using var previous = Command(connection, transaction,
            "SELECT profile_id,profile_revision,tier FROM mobile_wand_crafts WHERE account_id=$a AND operation_id=$o",
            ("$a", owner), ("$o", operationId));
        using (var reader = previous.ExecuteReader())
            if (reader.Read() && (reader.GetString(0) != profile.ProfileId || reader.GetInt64(1) != profile.Revision || reader.GetInt32(2) != tier))
                throw new WandGenerationException("wand_operation_conflict");
        try { Execute(connection, transaction, """
            INSERT INTO mobile_wand_crafts(account_id,operation_id,profile_id,profile_revision,tier)
            VALUES($a,$o,$p,$r,$t) ON CONFLICT(account_id,operation_id) DO NOTHING
            """, ("$a", owner), ("$o", operationId), ("$p", profile.ProfileId), ("$r", profile.Revision), ("$t", tier)); }
        catch (Microsoft.Data.Sqlite.SqliteException e) when (e.SqliteErrorCode == 19) { throw new WandGenerationException("wand_operation_conflict"); }
        transaction.Commit();
    }

    internal GeneratedWandSnapshot? LatestMobileSelected(string owner, string profileId, long revision)
    {
        using var connection = Open(); EnsureMobile(connection);
        using var command = Command(connection, null, """
            SELECT s.selected_json FROM wand_generation_sessions s JOIN mobile_wand_crafts m
            ON m.account_id=s.account_id AND m.operation_id=s.operation_id
            WHERE s.account_id=$a AND s.profile_id=$p AND s.profile_revision<=$r AND s.state='selected'
            AND NOT EXISTS(SELECT 1 FROM wand_generation_revocations v WHERE v.account_id=s.account_id AND v.profile_id IN ('*',s.profile_id))
            ORDER BY m.tier DESC,s.rowid DESC LIMIT 1
            """, ("$a", owner), ("$p", profileId), ("$r", revision));
        return command.ExecuteScalar() is string json ? Deserialize<GeneratedWandSnapshot>(json) : null;
    }

    internal int MobileTier(string owner, string profileId, long revision)
    {
        using var connection = Open(); EnsureMobile(connection);
        using var command = Command(connection, null, """
            SELECT COALESCE(MAX(m.tier),0) FROM mobile_wand_crafts m JOIN wand_generation_sessions s
            ON s.account_id=m.account_id AND s.operation_id=m.operation_id
            WHERE m.account_id=$a AND m.profile_id=$p AND m.profile_revision<=$r AND s.state='selected'
            AND NOT EXISTS(SELECT 1 FROM wand_generation_revocations v WHERE v.account_id=s.account_id AND v.profile_id IN ('*',s.profile_id))
            """, ("$a", owner), ("$p", profileId), ("$r", revision));
        return Convert.ToInt32(command.ExecuteScalar());
    }
}
