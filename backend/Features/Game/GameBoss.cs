using System.Security.Claims;
using Kufr.Backend.Features.Wands;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kufr.Backend.Features.Access;
using Kufr.Backend.Features.Identity;
using Kufr.Backend.Features.Launch;
using Kufr.Backend.Features.Customers;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Kufr.Backend.Features.Game;

internal sealed record GameBossView(bool Revealed, bool Rescued, int ReferenceXp, long Revision, string? ActiveAttemptId = null);
internal sealed record GameBossCommand(string OperationId, long ExpectedRevision,
    string? Choice = null, int SparkBid = 0, int? ExpectedReferenceXp = null);
internal sealed record GameBossPreview(GameBossView Profile, int? ProposedReferenceXp, int ActivePlayers, int? MinXp, int? MaxXp);
internal sealed record GameBossRound(int Index, string PlayerChoice, string OpponentChoice,
    string Winner, int PlayerSparkBid, int OpponentSparkBid, decimal PlayerDamage, decimal OpponentDamage);
internal sealed record GameBossAttemptView(string Id, string Mode, string Status, long Revision,
    GameOpponentView Opponent, IReadOnlyList<GameBossRound> Rounds, int CompletedRounds, int TotalRounds,
    int Block, int PlayerScore, int OpponentScore, int PlayerSparks, int OpponentSparks,
    IReadOnlyDictionary<string, int> PlayerInventory, IReadOnlyDictionary<string, int> OpponentInventory,
    bool OwnChoiceLocked, bool OpponentChoiceLocked, bool SparksEnabled, string? Result, int XpAwarded,
    decimal PlayerEnergy, decimal OpponentEnergy, decimal Strength, int Phase, string Hint,
    bool BossSpark, string SeedCommitment, string? SeedReveal, bool Exhibition);

internal sealed partial class GameStore
{
    private static void EnsureBossTables(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS game_boss_groups(
                group_id TEXT PRIMARY KEY, reference_xp INTEGER NOT NULL, rescued INTEGER NOT NULL,
                revision INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS game_boss_attempts(
                id TEXT PRIMARY KEY, group_id TEXT NOT NULL, user_id TEXT NOT NULL, state_json TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_boss_owner ON game_boss_attempts(group_id,user_id);
            CREATE TABLE IF NOT EXISTS game_boss_operations(
                operation_id TEXT PRIMARY KEY, actor TEXT NOT NULL, fingerprint TEXT NOT NULL, target TEXT NOT NULL);
            """;
        command.ExecuteNonQuery();
    }

    internal GameBossView ReadBoss(string groupId, string? userId = null)
    {
        using var connection = Open();
        EnsureBossTables(connection);
        var profile = ReadBossProfile(connection, null, groupId);
        if (userId is null) { return profile; }
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,state_json FROM game_boss_attempts WHERE group_id=$group AND user_id=$user";
        command.Parameters.AddWithValue("$group", groupId); command.Parameters.AddWithValue("$user", userId);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (JsonSerializer.Deserialize<BossAttempt>(reader.GetString(1), Json)!.Status == "active")
            { return profile with { ActiveAttemptId = reader.GetString(0) }; }
        }
        return profile;
    }

    private static GameBossView ReadBossProfile(SqliteConnection connection, SqliteTransaction? transaction, string groupId)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT reference_xp,rescued,revision FROM game_boss_groups WHERE group_id=$group";
        command.Parameters.AddWithValue("$group", groupId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? new(true, reader.GetInt32(1) != 0, reader.GetInt32(0), reader.GetInt64(2)) : new(false, false, 0, 0);
    }

    internal GameBossView RevealBoss(string groupId, string actor, IReadOnlySet<string> eligible,
        GameBossCommand request)
    {
        lock (_writeGate)
        {
            using var connection = Open(); EnsureBossTables(connection);
            using var transaction = connection.BeginTransaction(deferred: false);
            EnsureGameAccount(connection, transaction, actor);
            var fingerprint = BossFingerprint(actor, "reveal", groupId, request);
            if (FindBossOperation(connection, transaction, request.OperationId, actor, fingerprint) is not null)
            { return ReadBossProfile(connection, transaction, groupId); }
            var profile = ReadBossProfile(connection, transaction, groupId);
            if (profile.Revealed || request.ExpectedRevision != 0) { throw new GameFault(409, "boss_already_revealed"); }
            using var query = connection.CreateCommand(); query.Transaction = transaction;
            query.CommandText = "SELECT user_id,xp FROM game_players WHERE group_id=$group AND matches>0 AND xp>0";
            query.Parameters.AddWithValue("$group", groupId);
            var strengths = new List<int>();
            using (var reader = query.ExecuteReader())
            {
                while (reader.Read()) { if (eligible.Contains(reader.GetString(0))) { strengths.Add(reader.GetInt32(1)); } }
            }
            if (strengths.Count == 0) { throw new GameFault(409, "boss_needs_active_players"); }
            strengths.Sort();
            var median = strengths.Count % 2 == 1 ? strengths[strengths.Count / 2]
                : (int)(((long)strengths[strengths.Count / 2 - 1] + strengths[strengths.Count / 2]) / 2);
            if (request.ExpectedReferenceXp is not null && request.ExpectedReferenceXp != median)
            { throw new GameFault(409, "boss_reference_changed"); }
            using var insert = connection.CreateCommand(); insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO game_boss_groups(group_id,reference_xp,rescued,revision) VALUES($group,$xp,0,1)";
            insert.Parameters.AddWithValue("$group", groupId); insert.Parameters.AddWithValue("$xp", median);
            insert.ExecuteNonQuery();
            EnqueueBossArrivalChronicle(connection, transaction, groupId, eligible, median);
            RecordBossOperation(connection, transaction, request.OperationId, actor, fingerprint, groupId);
            transaction.Commit();
            return new(true, false, median, 1);
        }
    }

    internal GameBossPreview PreviewBoss(string groupId, IReadOnlySet<string> eligible)
    {
        using var connection = Open(); EnsureBossTables(connection);
        var profile = ReadBossProfile(connection, null, groupId);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT user_id,xp FROM game_players WHERE group_id=$group AND matches>0 AND xp>0";
        command.Parameters.AddWithValue("$group", groupId);
        var values = new List<int>();
        using (var reader = command.ExecuteReader())
        { while (reader.Read()) { if (eligible.Contains(reader.GetString(0))) { values.Add(reader.GetInt32(1)); } } }
        values.Sort();
        int? median = values.Count == 0 ? null : values.Count % 2 == 1 ? values[values.Count / 2]
            : (int)(((long)values[values.Count / 2 - 1] + values[values.Count / 2]) / 2);
        return new(profile, median, values.Count, values.Count == 0 ? null : values[0], values.Count == 0 ? null : values[^1]);
    }

    internal GameBossAttemptView StartBoss(string groupId, string userId, GameBossCommand request,
        GameYoloProfile? frozenProfile = null)
    {
        lock (_writeGate)
        {
            using var connection = Open(); EnsureBossTables(connection);
            using var transaction = connection.BeginTransaction(deferred: false);
            EnsureGameAccount(connection, transaction, userId);
            var fingerprint = BossFingerprint(userId, "start", groupId, request);
            if (FindBossOperation(connection, transaction, request.OperationId, userId, fingerprint) is { } replay)
            { return BossProjection(ReadBossAttempt(connection, transaction, groupId, userId, replay)); }
            var profile = ReadBossProfile(connection, transaction, groupId);
            if (!profile.Revealed || profile.Revision != request.ExpectedRevision) { throw new GameFault(409, "boss_profile_changed"); }
            using var active = connection.CreateCommand(); active.Transaction = transaction;
            active.CommandText = "SELECT state_json FROM game_boss_attempts WHERE group_id=$group AND user_id=$user";
            active.Parameters.AddWithValue("$group", groupId); active.Parameters.AddWithValue("$user", userId);
            using (var reader = active.ExecuteReader())
            {
                while (reader.Read())
                {
                    var prior = JsonSerializer.Deserialize<BossAttempt>(reader.GetString(0), Json)!;
                    if (prior.Status == "active") { throw new GameFault(409, "boss_attempt_active"); }
                }
            }
            var stats = Player(connection, userId, transaction, groupId);
            var attempt = new BossAttempt
            {
                Id = Guid.NewGuid().ToString("N"), GroupId = groupId, UserId = userId,
                Strength = BossStrength(stats.Xp, profile.ReferenceXp),
                Exhibition = stats.Xp < 30,
                Seed = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant(),
                ChronicleProfile = frozenProfile,
            };
            PrepareBossChoice(attempt);
            SaveBossAttempt(connection, transaction, attempt, insert: true);
            RecordBossOperation(connection, transaction, request.OperationId, userId, fingerprint, attempt.Id);
            transaction.Commit();
            return BossProjection(attempt);
        }
    }

    internal GameBossAttemptView ReadBossAttempt(string groupId, string userId, string id)
    {
        using var connection = Open(); EnsureBossTables(connection);
        return BossProjection(ReadBossAttempt(connection, null, groupId, userId, id));
    }

    internal GameBossAttemptView ChooseBoss(string groupId, string userId, string id, GameBossCommand request,
        IReadOnlySet<string>? eligibleMembers = null)
    {
        lock (_writeGate)
        {
            using var connection = Open(); EnsureBossTables(connection);
            using var transaction = connection.BeginTransaction(deferred: false);
            EnsureGameAccount(connection, transaction, userId);
            var attempt = ReadBossAttempt(connection, transaction, groupId, userId, id);
            var fingerprint = BossFingerprint(userId, "choose", id, request);
            if (FindBossOperation(connection, transaction, request.OperationId, userId, fingerprint) is not null)
            { return BossProjection(attempt); }
            if (attempt.Status != "active" || attempt.Revision != request.ExpectedRevision)
            { throw new GameFault(409, "boss_revision_conflict"); }
            if (request.Choice is not ("strike" or "trick" or "shield") || request.SparkBid is < 0 or > 1)
            { throw new GameFault(400, "boss_choice_invalid"); }
            if (request.SparkBid > (attempt.Exhibition ? 0 : attempt.PlayerSparks)
                || attempt.PlayerInventory[request.Choice] == 0)
            { throw new GameFault(409, "boss_inventory_exhausted"); }
            // The boss's choice was persisted before this request, independently of the player's card.
            var winner = request.Choice == attempt.BossChoice ? "draw"
                : Beats(request.Choice, attempt.BossChoice) ? "player" : "opponent";
            var value = 1 + request.SparkBid + attempt.BossBid;
            var playerDamage = winner == "player" ? value * attempt.Strength : 0;
            var bossDamage = winner == "opponent" ? value : 0;
            attempt.BossEnergy = Math.Max(0, attempt.BossEnergy - playerDamage);
            attempt.PlayerEnergy = Math.Max(0, attempt.PlayerEnergy - bossDamage);
            attempt.PlayerSparks -= request.SparkBid; attempt.BossSparks -= attempt.BossBid;
            attempt.PlayerInventory[request.Choice]--;
            attempt.BossInventory[attempt.BossChoice]--;
            attempt.Rounds.Add(new(attempt.Rounds.Count + 1, request.Choice, attempt.BossChoice, winner,
                request.SparkBid, attempt.BossBid, playerDamage, bossDamage));
            attempt.Revision++;
            if (attempt.BossEnergy == 0 || attempt.PlayerEnergy == 0 || attempt.Rounds.Count == 12)
            {
                attempt.Status = "complete"; attempt.Result = attempt.BossEnergy == 0 ? "win" : "loss";
                attempt.CompletedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                if (attempt.Result == "win")
                {
                    using var rescue = connection.CreateCommand(); rescue.Transaction = transaction;
                    rescue.CommandText = "UPDATE game_boss_groups SET rescued=1,revision=revision+1 WHERE group_id=$group AND rescued=0";
                    rescue.Parameters.AddWithValue("$group", groupId);
                    if (rescue.ExecuteNonQuery() == 1)
                    {
                        EnqueueBossRescueChronicle(connection, transaction, attempt,
                            eligibleMembers ?? new HashSet<string>([userId], StringComparer.Ordinal));
                    }
                }
            }
            else
            {
                if (attempt.Rounds.Count % 4 == 0) { attempt.PlayerInventory = BossStock(); attempt.BossInventory = BossStock(); }
                PrepareBossChoice(attempt);
            }
            SaveBossAttempt(connection, transaction, attempt, insert: false);
            if (attempt.Status == "complete") { EnqueuePersonalBossChronicle(connection, transaction, attempt); }
            RecordBossOperation(connection, transaction, request.OperationId, userId, fingerprint, id);
            transaction.Commit();
            return BossProjection(attempt);
        }
    }

    internal GameBossAttemptView CancelBoss(string groupId, string userId, string id, GameBossCommand request)
    {
        lock (_writeGate)
        {
            using var connection = Open(); EnsureBossTables(connection);
            using var transaction = connection.BeginTransaction(deferred: false);
            EnsureGameAccount(connection, transaction, userId);
            var attempt = ReadBossAttempt(connection, transaction, groupId, userId, id);
            var fingerprint = BossFingerprint(userId, "cancel", id, request);
            if (FindBossOperation(connection, transaction, request.OperationId, userId, fingerprint) is not null)
            { return BossProjection(attempt); }
            if (attempt.Status != "active" || attempt.Revision != request.ExpectedRevision)
            { throw new GameFault(409, "boss_revision_conflict"); }
            attempt.Status = "cancelled"; attempt.Revision++;
            SaveBossAttempt(connection, transaction, attempt, insert: false);
            RecordBossOperation(connection, transaction, request.OperationId, userId, fingerprint, id);
            transaction.Commit();
            return BossProjection(attempt);
        }
    }

    internal static void DeleteBossAccount(SqliteConnection connection, SqliteTransaction transaction, string userId)
    {
        using var exists = connection.CreateCommand(); exists.Transaction = transaction;
        exists.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='game_boss_attempts'";
        if (Convert.ToInt32(exists.ExecuteScalar()) == 0) { return; }
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "DELETE FROM game_boss_attempts WHERE user_id=$user; DELETE FROM game_boss_operations WHERE actor=$user;";
        command.Parameters.AddWithValue("$user", userId); command.ExecuteNonQuery();
    }

    private static bool Beats(string left, string right) => (left, right) is
        ("strike", "trick") or ("trick", "shield") or ("shield", "strike");
    private static decimal BossStrength(int xp, int referenceXp)
    {
        // Everyone can win at the same baseline. XP continuously improves damage, with
        // diminishing returns below 3x so outliers cannot skip the fight in one hit.
        var earnedXp = Math.Max(0m, xp);
        return 1m + 2m * earnedXp / (earnedXp + Math.Max(1m, referenceXp));
    }
    private static Dictionary<string, int> BossStock() => new() { ["strike"] = 2, ["trick"] = 2, ["shield"] = 2 };
    private static void PrepareBossChoice(BossAttempt attempt)
    {
        var index = attempt.Rounds.Count;
        var legal = attempt.BossInventory.Where(entry => entry.Value > 0).Select(entry => entry.Key).ToArray();
        var preferred = index >= 8 ? new[] { "strike", "trick", "shield", "strike" }[(index - 8) % 4]
            : index >= 4 && attempt.Rounds.Count > 0 ? attempt.Rounds[^1].PlayerChoice switch
            { "strike" => "shield", "trick" => "strike", _ => "trick" } : null;
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{attempt.Seed}:{index}"));
        var roll = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes) / ((double)uint.MaxValue + 1);
        if (preferred is not null && legal.Contains(preferred) && legal.Length > 1)
        {
            var others = legal.Where(choice => choice != preferred).ToArray();
            attempt.BossChoice = roll < 2d / 3 ? preferred : others[Math.Min(others.Length - 1, (int)((roll - 2d / 3) * 3 * others.Length))];
            attempt.Hint = $"Baron upřednostňuje {BossActionName(preferred)} (2/3). Zbývající legální kouzla mohou překvapit.";
        }
        else
        {
            attempt.BossChoice = legal[Math.Min(legal.Length - 1, (int)(roll * legal.Length))];
            attempt.Hint = legal.Length == 1 ? $"Baronovi zbývá jediný typ kouzla: {BossActionName(legal[0])}."
                : preferred is null ? "Předvádění: Baron volí rovnoměrně z dostupných kouzel."
                : "Preferované kouzlo je vyčerpané. Baron volí rovnoměrně z dostupných kouzel.";
        }
        attempt.BossBid = index is 3 or 8 ? 1 : 0;
    }
    private static string BossActionName(string action) => action switch { "strike" => "Úder", "trick" => "Lest", _ => "Štít" };
    private static GameBossAttemptView BossProjection(BossAttempt attempt) => new(
        attempt.Id, "boss", attempt.Status, attempt.Revision, new("boss", "Baron Šarlatán", 0, 0),
        attempt.Rounds, attempt.Rounds.Count, 12, Math.Min(3, attempt.Rounds.Count / 4 + 1),
        attempt.Rounds.Count(round => round.Winner == "player"), attempt.Rounds.Count(round => round.Winner == "opponent"),
        attempt.Exhibition ? 0 : attempt.PlayerSparks, attempt.BossSparks,
        new Dictionary<string, int>(attempt.PlayerInventory), new Dictionary<string, int>(attempt.BossInventory),
        false, true, !attempt.Exhibition, attempt.Result, 0, attempt.PlayerEnergy, attempt.BossEnergy,
        attempt.Strength, Math.Min(3, attempt.Rounds.Count / 4 + 1), attempt.Hint, attempt.BossBid == 1,
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(attempt.Seed))).ToLowerInvariant(),
        attempt.Status == "complete" ? attempt.Seed : null, attempt.Exhibition);

    private static BossAttempt ReadBossAttempt(SqliteConnection connection, SqliteTransaction? transaction,
        string groupId, string userId, string id)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT state_json FROM game_boss_attempts WHERE id=$id AND group_id=$group AND user_id=$user";
        command.Parameters.AddWithValue("$id", id); command.Parameters.AddWithValue("$group", groupId); command.Parameters.AddWithValue("$user", userId);
        var json = command.ExecuteScalar() as string ?? throw new GameFault(404, "boss_attempt_unavailable");
        return JsonSerializer.Deserialize<BossAttempt>(json, Json)!;
    }
    private static void SaveBossAttempt(SqliteConnection connection, SqliteTransaction transaction, BossAttempt attempt, bool insert)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = insert
            ? "INSERT INTO game_boss_attempts(id,group_id,user_id,state_json) VALUES($id,$group,$user,$state)"
            : "UPDATE game_boss_attempts SET state_json=$state WHERE id=$id AND group_id=$group AND user_id=$user";
        command.Parameters.AddWithValue("$id", attempt.Id); command.Parameters.AddWithValue("$group", attempt.GroupId);
        command.Parameters.AddWithValue("$user", attempt.UserId); command.Parameters.AddWithValue("$state", JsonSerializer.Serialize(attempt, Json));
        command.ExecuteNonQuery();
    }
    private static string BossFingerprint(string actor, string action, string target, GameBossCommand request)
    {
        if (!Guid.TryParse(request.OperationId, out _)) { throw new GameFault(400, "game_operation_invalid"); }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { actor, action, target, request }, Json))));
    }
    private static string? FindBossOperation(SqliteConnection connection, SqliteTransaction transaction,
        string operation, string actor, string fingerprint)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT actor,fingerprint,target FROM game_boss_operations WHERE operation_id=$op";
        command.Parameters.AddWithValue("$op", operation);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) { return null; }
        if (reader.GetString(0) != actor || reader.GetString(1) != fingerprint) { throw new GameFault(409, "game_operation_conflict"); }
        return reader.GetString(2);
    }
    private static void RecordBossOperation(SqliteConnection connection, SqliteTransaction transaction,
        string operation, string actor, string fingerprint, string target)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "INSERT INTO game_boss_operations(operation_id,actor,fingerprint,target) VALUES($op,$actor,$hash,$target)";
        command.Parameters.AddWithValue("$op", operation); command.Parameters.AddWithValue("$actor", actor);
        command.Parameters.AddWithValue("$hash", fingerprint); command.Parameters.AddWithValue("$target", target); command.ExecuteNonQuery();
    }
    private sealed class BossAttempt
    {
        public string Id { get; set; } = "";
        public string GroupId { get; set; } = "";
        public string UserId { get; set; } = "";
        public string Seed { get; set; } = "";
        public string Status { get; set; } = "active";
        public long Revision { get; set; } = 1;
        public long? CompletedAt { get; set; }
        public decimal PlayerEnergy { get; set; } = 6;
        public decimal BossEnergy { get; set; } = 5;
        public decimal Strength { get; set; }
        public bool Exhibition { get; set; }
        public GameYoloProfile? ChronicleProfile { get; set; }
        public int PlayerSparks { get; set; } = 2;
        public int BossSparks { get; set; } = 2;
        public string BossChoice { get; set; } = "strike";
        public int BossBid { get; set; }
        public string Hint { get; set; } = "";
        public string? Result { get; set; }
        public Dictionary<string, int> PlayerInventory { get; set; } = BossStock();
        public Dictionary<string, int> BossInventory { get; set; } = BossStock();
        public List<GameBossRound> Rounds { get; set; } = [];
    }
}

internal static class GameBossEndpoints
{
    internal static void MapMobileGameBoss(this WebApplication app)
    {
        if (!app.Services.GetRequiredService<AccountConfiguration>().Enabled) { return; }
        var group = app.MapGroup("/app/v1/game/boss").RequireAuthorization().RequireRateLimiting(GameRateLimit.Policy);
        group.AddEndpointFilter<GameChangedEndpointFilter>();
        group.AddEndpointFilter(async (invocation, next) =>
        {
            var context = invocation.HttpContext;
            try
            {
                var groupId = await ParticipantGroupAsync(context);
                context.Items["boss-group"] = groupId;
                context.Response.Headers.CacheControl = "no-store";
                return await next(invocation);
            }
            catch (GameFault fault) { return ApiProblem.Create(context, fault.Status, fault.Code); }
        });
        group.MapGet("", (HttpContext context, GameStore store) => Results.Ok(store.ReadBoss((string)context.Items["boss-group"]!, User(context))));
        group.MapPost("/attempts", async (GameBossCommand request, HttpContext context, GameStore store,
            AccountDatabase db, CharacterProfileStore profiles, WandGenerationStore wands) =>
        {
            var userId = User(context);
            var frozenProfile = await GameYoloWorker.ReadPublicProfile(userId, userId, profiles, wands, db, context.RequestAborted);
            return Results.Ok(store.StartBoss((string)context.Items["boss-group"]!, userId, request, frozenProfile));
        });
        group.MapGet("/attempts/{id}", (string id, HttpContext context, GameStore store) =>
            Results.Ok(store.ReadBossAttempt((string)context.Items["boss-group"]!, User(context), id)));
        group.MapPost("/attempts/{id}/choose", async (string id, GameBossCommand request, HttpContext context,
            GameStore store, AccountDatabase db) =>
        {
            var groupId = (string)context.Items["boss-group"]!;
            var eligible = await EligibleBossPlayersAsync(db, groupId, context.RequestAborted);
            return Results.Ok(store.ChooseBoss(groupId, User(context), id, request, eligible));
        });
        group.MapPost("/attempts/{id}/cancel", (string id, GameBossCommand request, HttpContext context, GameStore store) =>
            Results.Ok(store.CancelBoss((string)context.Items["boss-group"]!, User(context), id, request)));

        app.MapPost("/app/v1/admin/game/groups/{id}/boss", RevealAsync).RequireAuthorization().RequireRateLimiting("account");
        app.MapGet("/app/v1/admin/game/groups/{id}/boss", async (string id, HttpContext context, AccountDatabase db, GameStore store) =>
        {
            if (!context.User.HasClaim(claim => claim.Type == AccountIdentity.SessionClaim)
                || !context.User.IsInRole(AccountRoles.Owner) && !context.User.IsInRole(AccountRoles.Administrator))
            { return ApiProblem.Create(context, 403, "administrator_required"); }
            var eligible = await EligibleBossPlayersAsync(db, id, context.RequestAborted);
            context.Response.Headers.CacheControl = "no-store";
            return Results.Ok(store.PreviewBoss(id, eligible));
        }).RequireAuthorization().RequireRateLimiting("account");
    }
    private static string User(HttpContext context) => context.User.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? throw new GameFault(401, "account_required");
    internal static async Task<string> ParticipantGroupAsync(HttpContext context, bool requireWand = true, bool allowOwnerPreparation = false)
    {
        var id = User(context);
        if (!context.User.HasClaim(claim => claim.Type == AccountIdentity.SessionClaim)) { throw new GameFault(401, "account_required"); }
        var db = context.RequestServices.GetRequiredService<AccountDatabase>();
        await OwnerPlayerGroups.EnsureSelectionAsync(context, db, context.RequestServices.GetRequiredService<LaunchPolicyStore>());
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(user => user.Id == id && !user.Disabled
            && user.LaunchGroupRevokedAt == null && !db.DeletionRequests.Any(deletion => deletion.UserId == id), context.RequestAborted);
        if (user?.LaunchGroupId is null || !AccountRoles.IsPlayer(context.User)) { throw new GameFault(403, "game_group_required"); }
        var launchGroup = context.RequestServices.GetRequiredService<LaunchPolicyStore>().ReadGroup(user.LaunchGroupId);
        if (launchGroup is null || launchGroup.OpenedAt is null
            && !(allowOwnerPreparation && context.User.IsInRole(AccountRoles.Owner)))
        { throw new GameFault(403, "game_group_inactive"); }
        var access = await context.RequestServices.GetRequiredService<LaunchAccessService>().EvaluateAsync(context.User);
        if (!access.Allowed) { throw new GameFault(403, "game_group_inactive"); }
        // Boss attempts are the explicitly bounded exhibition escape route for
        // guests who have not yet crafted a wand (or are captive and cannot use
        // the workshop). StartBoss still requires a revealed group boss and
        // ChooseBoss is scoped to that participant's existing attempt.
        var path = context.Request.Path.Value ?? "";
        var bossAttemptAction = context.Request.Method == "POST"
            && (path == "/app/v1/game/boss/attempts"
                || System.Text.RegularExpressions.Regex.IsMatch(path,
                    @"^/app/v1/game/boss/attempts/[0-9a-fA-F]{32}/(?:choose|cancel)$",
                    System.Text.RegularExpressions.RegexOptions.CultureInvariant));
        if (requireWand && context.Request.Method != "GET" && !bossAttemptAction
            && !path.EndsWith("/cancel", StringComparison.Ordinal)
            && !path.EndsWith("/decline", StringComparison.Ordinal))
            await MobileWandSessions.RequireForGame(context, id);
        return user.LaunchGroupId;
    }
    private static async Task<IResult> RevealAsync(string id, GameBossCommand request, HttpContext context,
        AccountDatabase db, LaunchPolicyStore launch, GameStore store)
    {
        var actor = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (actor is null || !context.User.HasClaim(claim => claim.Type == AccountIdentity.SessionClaim)
            || !context.User.IsInRole(AccountRoles.Owner) && !context.User.IsInRole(AccountRoles.Administrator))
        { return ApiProblem.Create(context, 403, "administrator_required"); }
        var recent = await AccountAuthorization.RequireRecentAsync(context);
        if (recent is not null) { return recent; }
        if (launch.ReadGroup(id)?.OpenedAt is null) { return ApiProblem.Create(context, 409, "game_group_inactive"); }
        var eligible = await EligibleBossPlayersAsync(db, id, context.RequestAborted);
        try { return Results.Ok(store.RevealBoss(id, actor, eligible, request)); }
        catch (GameFault fault) { return ApiProblem.Create(context, fault.Status, fault.Code); }
    }
    private static async Task<HashSet<string>> EligibleBossPlayersAsync(AccountDatabase db, string id, CancellationToken token) =>
        await GameParticipants.Members(db, id).Select(user => user.Id).ToHashSetAsync(token);
}
