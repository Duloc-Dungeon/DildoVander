using System.Security.Claims;
using Kufr.Backend.Features.Access;
using Kufr.Backend.Features.Chronicle;
using Kufr.Backend.Features.Customers;
using Kufr.Backend.Features.Game;
using Kufr.Backend.Features.Identity;
using Kufr.Backend.Features.Keepsakes;
using Kufr.Backend.Features.Wands;
using Microsoft.EntityFrameworkCore;

namespace Kufr.Backend.Features.Launch;

internal sealed partial class LaunchPolicyStore
{
    internal long? ReadChronicleClosure(string groupId)
    {
        using var connection = Open();
        using var query = connection.CreateCommand();
        query.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name='launch_chronicle_closures'";
        if (query.ExecuteScalar() is null) { return null; }
        query.CommandText = "SELECT closed_at FROM launch_chronicle_closures WHERE group_id=$group";
        query.Parameters.AddWithValue("$group", groupId);
        return query.ExecuteScalar() is long time ? time : null;
    }

    internal long FreezeEveningChronicle(string groupId, long expectedRevision, string actorId, long occurredAt,
        IReadOnlyList<ChronicleOutboxEnvelope> snapshots)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        using var schema = connection.CreateCommand();
        schema.Transaction = transaction;
        schema.CommandText = """
            CREATE TABLE IF NOT EXISTS launch_chronicle_closures(
                group_id TEXT PRIMARY KEY,closed_at INTEGER NOT NULL,actor_id TEXT NOT NULL);
            """;
        schema.ExecuteNonQuery();
        var group = ReadGroup(connection, transaction, groupId) ?? throw new LaunchConfigurationException("group_not_found", 404);
        using var prior = connection.CreateCommand();
        prior.Transaction = transaction;
        prior.CommandText = "SELECT closed_at FROM launch_chronicle_closures WHERE group_id=$group";
        prior.Parameters.AddWithValue("$group", groupId);
        if (prior.ExecuteScalar() is long existing) { return existing; }
        if (group.Revision != expectedRevision) { throw new LaunchConfigurationException("launch_revision_conflict", 409); }
        if (group.OpenedAt is null) { throw new LaunchConfigurationException("group_not_open", 409); }
        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = "INSERT INTO launch_chronicle_closures(group_id,closed_at,actor_id) VALUES($group,$time,$actor)";
        insert.Parameters.AddWithValue("$group", groupId);
        insert.Parameters.AddWithValue("$time", occurredAt);
        insert.Parameters.AddWithValue("$actor", actorId);
        insert.ExecuteNonQuery();
        foreach (var snapshot in snapshots) { ChronicleSourceOutbox.Append(connection, transaction, snapshot); }
        transaction.Commit();
        return occurredAt;
    }

    internal int DrainEveningChronicle(ChronicleStore destination)
    {
        using var connection = Open();
        return ChronicleSourceOutbox.Drain(connection, destination);
    }

    internal void EraseChronicleAccount(string accountId)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        ChronicleSourceOutbox.EraseAccount(connection, transaction, accountId);
        transaction.Commit();
    }

    internal void EraseChronicleProfile(string accountId, string profileId)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        ChronicleSourceOutbox.EraseProfile(connection, transaction, accountId, profileId);
        transaction.Commit();
    }
}

internal static class LaunchChronicleEndpoints
{
    internal static void MapLaunchChronicle(this WebApplication app)
    {
        if (!app.Services.GetRequiredService<AccountConfiguration>().Enabled) { return; }
        app.MapPost("/app/v1/admin/launch/groups/{id}/chronicle-close", async (string id, LaunchGroupRevision request,
            HttpContext http, LaunchPolicyStore launch, AccountDatabase accounts, ChronicleStore chronicle,
            CharacterProfileStore characters, GameStore games) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            if (!AccountRoles.IsAdmin(http.User) || !http.User.HasClaim(claim => claim.Type == AccountIdentity.SessionClaim))
            { return ApiProblem.Create(http, 403, "account_admin_required"); }
            var actor = http.User.FindFirstValue(ClaimTypes.NameIdentifier)!;
            if (!await accounts.Users.AnyAsync(user => user.Id == actor && !user.Disabled, http.RequestAborted)
                || await accounts.DeletionRequests.AnyAsync(deletion => deletion.UserId == actor, http.RequestAborted))
            { return ApiProblem.Create(http, 403, "account_admin_required"); }
            var group = launch.ReadGroup(id);
            if (group is null) { return ApiProblem.Create(http, 404, "group_not_found"); }
            // Deliver already committed sources before selecting the montage.
            games.DrainChronicleOutbox(chronicle);
            http.RequestServices.GetService<WandGenerationStore>()?.DrainWandChronicle(chronicle);
            var members = await GameParticipants.Members(accounts, id).Select(user => new { user.Id, user.DisplayName })
                .ToArrayAsync(http.RequestAborted);
            var now = http.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow().ToUnixTimeSeconds();
            var snapshots = new List<ChronicleOutboxEnvelope>();
            foreach (var member in members)
            {
                var events = chronicle.ReadRecordedEvents(id, member.Id).Where(item => item.OccurredAt <= now && item.SourceKind != "evening").ToArray();
                var moments = SelectMoments(events);
                var name = (await characters.ReadAsync(member.Id, http.RequestAborted))?.UserInput.Name ?? member.DisplayName;
                var self = new ChronicleParticipant(member.Id, name, "owner", name);
                var participants = moments.SelectMany(item => item.Participants).Append(self).DistinctBy(person => person.AccountId).ToArray();
                var snapshot = new ChronicleEventInput("evening", id + "_" + member.Id, id, now, "Závěr večera · " + group.Name,
                    "Osobní kapitola z odhalených a uložených událostí tohoto večera.", participants,
                    Story: moments.Select(item => $"{item.Title}: {item.Summary}").ToArray(),
                    ShareText: "Vzpomínky na večer " + group.Name, IllustrationRequested: true, Visibility: "public");
                snapshots.Add(new(snapshot, [member.Id], new Dictionary<string, IReadOnlyList<ChronicleAward>>()));
            }
            try
            {
                var closedAt = launch.FreezeEveningChronicle(id, request.ExpectedRevision, actor, now, snapshots);
                launch.DrainEveningChronicle(chronicle);
                return Results.Ok(new { closedAt });
            }
            catch (LaunchConfigurationException error) { return ApiProblem.Create(http, error.Status, error.Code); }
        }).RequireAuthorization().RequireRateLimiting("account");
    }

    private static ChronicleEventInput[] SelectMoments(ChronicleEventInput[] events)
    {
        var selected = new List<ChronicleEventInput>();
        foreach (var kind in new[] { "wand", "yolo", "duel", "team", "boss", "case" })
        {
            var candidate = events.Where(item => item.SourceKind == kind).OrderBy(item => item.OccurredAt).FirstOrDefault();
            if (candidate is not null) { selected.Add(candidate); }
        }
        return selected.OrderBy(item => item.OccurredAt).ToArray();
    }
}
