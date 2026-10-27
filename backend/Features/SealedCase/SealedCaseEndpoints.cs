using System.Security.Claims;
using System.Text.Json;
using Kufr.Backend.Features.Access;
using Kufr.Backend.Features.CustomerData;
using Kufr.Backend.Features.Identity;
using Kufr.Backend.Features.Launch;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;

namespace Kufr.Backend.Features.SealedCase;

internal sealed record SealedCaseCreateRequest(string? OperationId, int ExpectedPlayers, string? Variant = "first");
internal sealed record SealedCaseLobbyRequest(string? OperationId, long ExpectedRevision, bool Ready, bool Consent);
internal sealed record SealedCaseStartRequest(string? OperationId, long ExpectedRevision);
internal sealed record SealedCaseLobbyEntry(string PlayerId, bool Ready, bool Consent);
internal sealed record SealedCaseLobbyView(string Id, string PlayerId, string Status, long Revision,
    string Variant, int ExpectedPlayers, int ReadyPlayers, bool OwnReady, bool CanManage,
    bool CanStart, string StartBlocker, long ServerNow, int Chapters, int PlanningSeconds, int QuietSeconds);

// This HTTP slice exposes a real durable, session-bound lobby, NOT a simulated game.
// Until a complete scenario authority exists, start/actions deliberately fail closed.
internal static class SealedCaseEndpoints
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const string CaseItem = "dv-sealed-case";
    private const string GroupItem = "dv-sealed-group";
    private const string MembershipItem = "dv-sealed-membership-revision";

    internal static IServiceCollection AddSealedCase(this IServiceCollection services, IConfiguration configuration)
    {
        if (AccountConfiguration.Read(configuration).Enabled && configuration.GetValue<bool>("SealedCase:Enabled"))
        {
            services.AddSealedCaseRateLimits();
            services.AddSingleton(sp => new SealedCaseStore(Path.Combine(sp.GetRequiredService<AccountConfiguration>().DataRoot, "sealed-case.sqlite")));
        }
        return services;
    }

    internal static void MapSealedCase(this WebApplication app)
    {
        if (!app.Services.GetRequiredService<AccountConfiguration>().Enabled || !app.Configuration.GetValue<bool>("SealedCase:Enabled")) return;
        var routes = app.MapGroup("/app/v1/sealed-cases").RequireAuthorization().RequireRateLimiting(SealedCaseRateLimit.Policy);
        routes.AddEndpointFilter(async (invocation, next) =>
        {
            var h = invocation.HttpContext;
            h.Response.Headers.CacheControl = "no-store";
            var userId = h.User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (userId is null || !h.User.HasClaim(c => c.Type == AccountIdentity.SessionClaim)) return ApiProblem.Create(h, 401, "account_required");
            var db = h.RequestServices.GetRequiredService<AccountDatabase>();
            var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == userId && !u.Disabled, h.RequestAborted);
            if (user?.LaunchGroupId is null || user.LaunchGroupRevokedAt is not null
                || await db.DeletionRequests.AnyAsync(d => d.UserId == userId, h.RequestAborted)
                || !await (from m in db.UserRoles join r in db.Roles on m.RoleId equals r.Id where m.UserId == userId && r.Name == AccountRoles.Customer select m.UserId).AnyAsync(h.RequestAborted))
                return ApiProblem.Create(h, 403, "sealed_case_group_required");
            var launch = h.RequestServices.GetRequiredService<LaunchPolicyStore>().ReadGroup(user.LaunchGroupId);
            if (launch?.OpenedAt is null || !(await h.RequestServices.GetRequiredService<LaunchAccessService>().EvaluateAsync(h.User)).Allowed)
                return ApiProblem.Create(h, 403, "sealed_case_launch_unavailable");
            h.Items[GroupItem] = user.LaunchGroupId;
            h.Items[MembershipItem] = user.LaunchGroupAccessRevision;
            try
            {
                if (h.Request.RouteValues["id"]?.ToString() is { } id)
                {
                    var record = h.RequestServices.GetRequiredService<SealedCaseStore>().Read(id);
                    if (record.GroupId != user.LaunchGroupId) return ApiProblem.Create(h, 404, "case_not_found");
                    h.Items[CaseItem] = record;
                }
                return await next(invocation);
            }
            catch (SealedCasePersistenceException e) { return ApiProblem.Create(h, e.Status, e.Code); }
            catch (CustomerDataAccessException e) { return ApiProblem.Create(h, e.Status, e.Code); }
        });
        routes.MapPost("", async (SealedCaseCreateRequest request, HttpContext h, SealedCaseStore store, AccountDatabase db, TimeProvider clock) =>
        {
            ValidateOperation(request.OperationId);
            var userId = h.User.FindFirstValue(ClaimTypes.NameIdentifier)!;
            var created = store.Create((string)h.Items[GroupItem]!, userId, clock.GetUtcNow().ToUnixTimeSeconds(), request.ExpectedPlayers, request.Variant ?? "first", request.OperationId, () => AuthorizeWrite(h, db, clock));
            return Results.Created($"/app/v1/sealed-cases/{created.Id}", await ViewAsync(store.Read(created.Id), userId, db, clock, h.RequestAborted));
        });
        routes.MapGet("/{id}", async (HttpContext h, AccountDatabase db, TimeProvider clock) =>
            Results.Ok(await ViewAsync((SealedCaseRecord)h.Items[CaseItem]!, h.User.FindFirstValue(ClaimTypes.NameIdentifier)!, db, clock, h.RequestAborted)));
        routes.MapPost("/{id}/lobby", (SealedCaseLobbyRequest request, HttpContext h, SealedCaseStore store, AccountDatabase db, TimeProvider clock) =>
        {
            ValidateOperation(request.OperationId);
            if (request.Ready && !request.Consent) throw new SealedCasePersistenceException("sealed_case_consent_required", 400);
            var record = (SealedCaseRecord)h.Items[CaseItem]!;
            var userId = h.User.FindFirstValue(ClaimTypes.NameIdentifier)!;
            var receipt = store.Append(record.Id, record.GroupId, userId, "lobby", request.OperationId!, request.ExpectedRevision,
                clock.GetUtcNow().ToUnixTimeSeconds(), new SealedCaseLobbyEntry(userId, request.Ready, request.Consent), () => AuthorizeWrite(h, db, clock));
            return Results.Ok(receipt);
        });
        routes.MapPost("/{id}/start", async (SealedCaseStartRequest request, HttpContext h, AccountDatabase db, TimeProvider clock) =>
        {
            ValidateOperation(request.OperationId);
            var record = (SealedCaseRecord)h.Items[CaseItem]!;
            var view = await ViewAsync(record, h.User.FindFirstValue(ClaimTypes.NameIdentifier)!, db, clock, h.RequestAborted);
            if (!view.CanManage) return ApiProblem.Create(h, 403, "sealed_case_host_required");
            if (request.ExpectedRevision != record.Revision) return ApiProblem.Create(h, 409, "revision_conflict");
            if (view.ReadyPlayers != view.ExpectedPlayers) return ApiProblem.Create(h, 409, "sealed_case_lobby_not_ready");
            return ApiProblem.Create(h, 422, "sealed_case_scenario_unavailable");
        });
        routes.MapPost("/{id}/actions", (HttpContext h) => ApiProblem.Create(h, 422, "sealed_case_scenario_unavailable"));
    }

    private static void ValidateOperation(string? operationId)
    {
        if (string.IsNullOrWhiteSpace(operationId) || operationId.Length is < 8 or > 128) throw new SealedCasePersistenceException("invalid_operation_id", 400);
    }

    private static IDisposable AuthorizeWrite(HttpContext h, AccountDatabase db, TimeProvider clock)
    {
        var accountId = h.User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var sessionId = h.User.FindFirstValue(AccountIdentity.SessionClaim);
        var stampType = h.RequestServices.GetRequiredService<UserManager<AccountUser>>().Options.ClaimsIdentity.SecurityStampClaimType;
        var stamp = h.User.FindFirstValue(stampType);
        if (string.IsNullOrEmpty(sessionId) || string.IsNullOrEmpty(stamp)) throw new CustomerDataAccessException(401, "account_access_unavailable");
        var admission = CustomerAccountAdmission.Acquire(db.Database.GetConnectionString()!, clock, accountId, sessionId, stamp, h.RequestAborted);
        try
        {
            var proof = admission.Proof;
            if (proof.LaunchGroupId != (string)h.Items[GroupItem]! || proof.LaunchGroupRevokedAt is not null
                || proof.LaunchGroupAccessRevision != (long)h.Items[MembershipItem]!
                || !proof.Roles.Contains(AccountRoles.Customer, StringComparer.Ordinal))
                throw new CustomerDataAccessException(403, "sealed_case_group_required");
            return admission;
        }
        catch { admission.Dispose(); throw; }
    }

    private static async Task<SealedCaseLobbyView> ViewAsync(SealedCaseRecord record, string userId, AccountDatabase db, TimeProvider clock, CancellationToken cancellationToken)
    {
        using var setup = JsonDocument.Parse(record.SetupJson);
        var variant = setup.RootElement.GetProperty("variant").GetString()!;
        var expected = setup.RootElement.GetProperty("expectedPlayers").GetInt32();
        var schedule = SealedCaseRules.Schedule(variant, expected);
        var eligible = (await (from u in db.Users.AsNoTracking() join m in db.UserRoles on u.Id equals m.UserId join r in db.Roles on m.RoleId equals r.Id
            where u.LaunchGroupId == record.GroupId && u.LaunchGroupRevokedAt == null && !u.Disabled && r.Name == AccountRoles.Customer
                && !db.DeletionRequests.Any(d => d.UserId == u.Id) select u.Id).Distinct().ToListAsync(cancellationToken)).ToHashSet(StringComparer.Ordinal);
        var latest = new Dictionary<string, SealedCaseLobbyEntry>(StringComparer.Ordinal);
        foreach (var payload in record.Events)
        {
            using var doc = JsonDocument.Parse(payload);
            if (!doc.RootElement.TryGetProperty("playerId", out _) || !doc.RootElement.TryGetProperty("ready", out _) || !doc.RootElement.TryGetProperty("consent", out _)) continue;
            var entry = JsonSerializer.Deserialize<SealedCaseLobbyEntry>(payload, Json)!;
            if (eligible.Contains(entry.PlayerId)) latest[entry.PlayerId] = entry;
        }
        return new(record.Id, userId, record.Status, record.Revision, variant, expected,
            latest.Values.Count(x => x.Ready && x.Consent), latest.TryGetValue(userId, out var own) && own.Ready && own.Consent,
            setup.RootElement.GetProperty("createdBy").GetString() == userId, false, "sealed_case_scenario_unavailable",
            clock.GetUtcNow().ToUnixTimeSeconds(), schedule.Chapters, schedule.PlanningSeconds, schedule.QuietSeconds);
    }
}
