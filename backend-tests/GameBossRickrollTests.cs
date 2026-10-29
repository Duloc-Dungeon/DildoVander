using Kufr.Backend.Features.Game;
using Microsoft.Data.Sqlite;

internal static class GameBossRickrollTests
{
    internal static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "boss-rickroll-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new GameStore(root);
            _ = store.ReadBoss("g");
            using (var connection = new SqliteConnection($"Data Source={Path.Combine(root, "mobile-game.sqlite")}"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "INSERT INTO game_boss_groups(group_id,reference_xp,rescued,revision) VALUES('g',40,0,1),('other',40,0,1)";
                command.ExecuteNonQuery();
            }
            Check(!store.ReadBoss("g", "first").RickrollEnabled && store.ReadBoss("g", "first").RickrollRevision == 0,
                "new group setting defaults disabled at revision zero");
            Check(!store.ClaimBossRickroll("g", "first", true).Play, "unrevealed boss cannot claim");

            var enabled = store.SetBossRickroll("g", "admin", new("toggle-operation-1", 0, true));
            Check(enabled.RickrollEnabled && enabled.RickrollRevision == 1, "toggle saves separate revision and returns the profile");
            Check(store.SetBossRickroll("g", "admin", new("toggle-operation-1", 0, true)) == enabled,
                "identical operation retry is idempotent");
            var conflict = false;
            try { store.SetBossRickroll("g", "admin", new("toggle-operation-1", 0, false)); }
            catch (GameFault fault) { conflict = fault.Status == 409 && fault.Code == "operation_payload_conflict"; }
            Check(conflict, "operation id cannot be reused with a different payload");
            var stale = false;
            try { store.SetBossRickroll("g", "admin", new("toggle-operation-2", 0, false)); }
            catch (GameFault fault) { stale = fault.Status == 409; }
            Check(stale && store.ReadBoss("g").RickrollEnabled, "stale independent revision does not change setting");

            var disabled = store.ClaimBossRickroll("g", "no-media", false);
            Check(!disabled.Play, "unavailable media does not consume a claim");
            Check(store.ClaimBossRickroll("g", "no-media", true).Play, "media availability can later satisfy an unconsumed claim");
            Check(store.SetBossRickroll("g", "admin", new("toggle-operation-3", 1, false)).RickrollRevision == 2,
                "turning off increments only the Rickroll revision");
            Check(!store.ClaimBossRickroll("g", "disabled", true).Play, "disabled setting does not claim");
            store.SetBossRickroll("g", "admin", new("toggle-operation-4", 2, true));

            var attempts = await Task.WhenAll(Enumerable.Range(0, 12)
                .Select(_ => Task.Run(() => store.ClaimBossRickroll("g", "first", true).Play)));
            Check(attempts.Count(play => play) == 1, "concurrent duplicate claims produce one play");
            Check(store.ReadBoss("g", "first").RickrollSeen, "claim is reflected on player's boss profile");
            store.SetBossRickroll("other", "admin", new("other-operation-1", 0, true));
            Check(!store.ClaimBossRickroll("other", "first", true).Play, "claim is once per account across groups");
            Check(store.ClaimBossRickroll("other", "second", true).Play, "a distinct player can claim independently");

            store.DeleteAccount("first");
            var erasedDenied = false;
            try { store.ClaimBossRickroll("other", "first", true); }
            catch (GameFault fault) { erasedDenied = fault.Status == 403; }
            Check(erasedDenied, "account tombstone prevents recreating claim");
            using (var connection = new SqliteConnection($"Data Source={Path.Combine(root, "mobile-game.sqlite")}"))
            {
                connection.Open();
                using var command = connection.CreateCommand(); command.CommandText = "SELECT COUNT(*) FROM game_boss_rickroll_claims WHERE user_id='first'";
                Check(Convert.ToInt32(command.ExecuteScalar()) == 0, "account deletion removes the global claim record");
            }
            Console.WriteLine("Boss Rickroll: defaults, separate revision, retry conflict, atomic global claim, unavailable media and erasure passed.");
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, recursive: true); }
        await MobileGameTests.RunBossRickrollHttpAsync();
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) { throw new Exception($"Boss Rickroll assertion failed: {message}"); }
    }
}
