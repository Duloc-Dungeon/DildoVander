using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Kufr.Backend.Features.Access;
using Kufr.Backend.Features.Conversations;
using Kufr.Backend.Features.Customers;
using Kufr.Backend.Features.Diagnostics;
using Kufr.Backend.Features.DigitalTwin;
using Kufr.Backend.Features.Identity;
using Microsoft.EntityFrameworkCore;

namespace Kufr.Backend.Features.Launch;

internal static class LaunchEndpoints
{
    private const int WaitingRoomPageSize = 100;
    private sealed record WaitingRoomMember(string UserId, string DisplayName, long JoinedAt,
        string? GroupId, string? GroupName, string State, long AccessRevision, string? FixedUniverse);
    private sealed record WaitingRoomMove(long ExpectedMembershipRevision, string? ExpectedSourceGroupId,
        string? TargetGroupId);

    private static async Task<IResult> MoveWaitingRoomMemberAsync(string userId, WaitingRoomMove request,
        HttpContext context, AccountDatabase db, LaunchPolicyStore store, TimeProvider timeProvider)
    {
        var recent = await AccountAuthorization.RequireRecentAsync(context);
        if (recent is not null) { return recent; }
        if (context.Request.Headers["X-DV-Confirm"] != "yes")
        { return ApiProblem.Create(context, 409, "confirmation_required"); }
        if (request.ExpectedMembershipRevision < 0 || string.IsNullOrWhiteSpace(request.TargetGroupId)
            || request.TargetGroupId.Length > 64 || request.ExpectedSourceGroupId is { Length: > 64 })
        { return ApiProblem.Create(context, 400, "invalid_waiting_room_move"); }
        var actorId = context.User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var now = timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        long accessRevision;
        long? joinedAt;
        string? fixedUniverse;
        string? movedFrom = null;
        var moved = false;
        using (var reservation = store.ReserveWaitingRoomMove(request.ExpectedSourceGroupId,
            request.TargetGroupId))
        {
            if (reservation is null) { return ApiProblem.Create(context, 404, "launch_group_unavailable"); }
            await using (var transaction = await db.Database.BeginTransactionAsync())
            {
                await db.Database.ExecuteSqlRawAsync("UPDATE AspNetUsers SET Id=Id WHERE Id = {0}", userId);
                var user = await db.Users.SingleOrDefaultAsync(candidate => candidate.Id == userId);
                if (user is null || !await IsCustomerAsync(db, userId))
                { return ApiProblem.Create(context, 404, "waiting_room_member_unavailable"); }
                if (user.Disabled || await db.DeletionRequests.AnyAsync(candidate => candidate.UserId == userId))
                { return ApiProblem.Create(context, 409, "waiting_room_member_unavailable"); }
                if (await HasProtectedRoleAsync(db, userId))
                { return ApiProblem.Create(context, 403, "beta_member_access_protected"); }
                if (user.LaunchGroupAccessRevision != request.ExpectedMembershipRevision
                    || user.LaunchGroupId != request.ExpectedSourceGroupId)
                { return ApiProblem.Create(context, 409, "beta_member_revision_conflict"); }
                if (user.LaunchGroupRevokedAt is null
                    && reservation.SourceStarted(timeProvider.GetUtcNow().ToUnixTimeMilliseconds()))
                { return ApiProblem.Create(context, 409, "waiting_room_member_already_started"); }
                if (user.LaunchGroupId != request.TargetGroupId)
                {
                    user.LaunchGroupId = request.TargetGroupId;
                    user.LaunchGroupJoinedAt ??= timeProvider.GetUtcNow().ToUnixTimeSeconds();
                    user.LaunchGroupAccessRevision++;
                    await db.SaveChangesAsync();
                    await transaction.CommitAsync();
                    movedFrom = request.ExpectedSourceGroupId;
                    moved = true;
                }
                accessRevision = user.LaunchGroupAccessRevision;
                joinedAt = user.LaunchGroupJoinedAt;
                fixedUniverse = user.AdmissionUniverse;
            }
        }
        if (moved)
        { store.AuditBetaMemberMoved(actorId, userId, movedFrom, request.TargetGroupId, accessRevision, now); }
        return Results.Ok(new
        {
            userId,
            groupId = request.TargetGroupId,
            accessRevision,
            joinedAt,
            fixedUniverse
        });
    }

    private static Task<bool> IsCustomerAsync(AccountDatabase db, string userId)
        => (from membership in db.UserRoles
            join role in db.Roles on membership.RoleId equals role.Id
            where membership.UserId == userId && role.Name == AccountRoles.Customer
            select membership.UserId).AnyAsync();

    private static Task<bool> HasProtectedRoleAsync(AccountDatabase db, string userId)
        => (from membership in db.UserRoles
            join role in db.Roles on membership.RoleId equals role.Id
            where membership.UserId == userId
                && new[] { AccountRoles.Owner, AccountRoles.Administrator, AccountRoles.Operator,
                    AccountRoles.Viewer, AccountRoles.Tester }.Contains(role.Name!)
            select membership.UserId).AnyAsync();

    private static async Task<IResult> ReadWaitingRoomAsync(string? after, HttpContext context,
        AccountDatabase db, LaunchPolicyStore store, TimeProvider timeProvider)
    {
        if (after is { Length: > 450 } || after?.Any(char.IsControl) == true)
        { return ApiProblem.Create(context, 400, "invalid_waiting_room_cursor"); }
        var groups = store.ReadGroups().ToDictionary(group => group.Id, StringComparer.Ordinal);
        var now = timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        foreach (var group in groups.Values.ToArray())
        {
            if (group.ForceStartAtEnd && group.LaunchAt is not null && group.LaunchAt <= now)
            { groups[group.Id] = store.CompleteForcedCountdown(group.Id, now) ?? group; }
        }
        var openedGroupIds = groups.Values.Where(group => group.OpenedAt is not null)
            .Select(group => group.Id).ToArray();
        var query = from user in db.Users.AsNoTracking()
            join membership in db.UserRoles on user.Id equals membership.UserId
            join role in db.Roles on membership.RoleId equals role.Id
            where role.Name == AccountRoles.Customer && !user.Disabled
                && !db.DeletionRequests.Any(candidate => candidate.UserId == user.Id)
                && (user.LaunchGroupRevokedAt != null || user.LaunchGroupId == null
                    || !openedGroupIds.Contains(user.LaunchGroupId))
                && !(from protectedMembership in db.UserRoles
                    join protectedRole in db.Roles on protectedMembership.RoleId equals protectedRole.Id
                    where protectedMembership.UserId == user.Id
                        && new[] { AccountRoles.Owner, AccountRoles.Administrator, AccountRoles.Operator,
                            AccountRoles.Viewer, AccountRoles.Tester }.Contains(protectedRole.Name!)
                    select protectedMembership.UserId).Any()
                && (after == null || string.Compare(user.Id, after) > 0)
            orderby user.Id
            select new
            {
                user.Id,
                user.DisplayName,
                user.CreatedAt,
                user.LaunchGroupJoinedAt,
                user.LaunchGroupId,
                user.LaunchGroupRevokedAt,
                user.LaunchGroupAccessRevision,
                user.AdmissionUniverse
            };
        var page = await query.Take(WaitingRoomPageSize + 1).ToArrayAsync();
        var hasMore = page.Length > WaitingRoomPageSize;
        var members = page.Take(WaitingRoomPageSize).ToArray();
        var nextCursor = hasMore ? members[^1].Id : null;
        return Results.Ok(new
        {
            members = members.Select(user => new WaitingRoomMember(user.Id, user.DisplayName,
                user.LaunchGroupJoinedAt ?? user.CreatedAt, user.LaunchGroupId,
                user.LaunchGroupId is not null && groups.TryGetValue(user.LaunchGroupId, out var group) ? group.Name : null,
                user.LaunchGroupRevokedAt is null ? "waiting" : "revoked",
                user.LaunchGroupAccessRevision, user.AdmissionUniverse)).ToArray(),
            nextCursor
        });
    }

    private static async Task<IResult> SetMemberAccessAsync(string id, string userId,
        BetaMemberAccessUpdate request, HttpContext context, AccountDatabase db,
        LaunchPolicyStore store, TimeProvider timeProvider)
    {
        var recent = await AccountAuthorization.RequireRecentAsync(context);
        if (recent is not null) { return recent; }
        if (context.Request.Headers["X-DV-Confirm"] != "yes")
        { return ApiProblem.Create(context, 409, "confirmation_required"); }
        var actor = context.User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var now = timeProvider.GetUtcNow().ToUnixTimeSeconds();
        var group = store.ReadGroup(id);
        if (group is null) { return ApiProblem.Create(context, 404, "launch_group_unavailable"); }
        await using var transaction = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlRawAsync("UPDATE AspNetUsers SET Id=Id WHERE Id = {0}", userId);
        var user = await db.Users.SingleOrDefaultAsync(x => x.Id == userId);
        if (user is null || user.LaunchGroupId != id
            || !await (from membership in db.UserRoles
                join role in db.Roles on membership.RoleId equals role.Id
                where membership.UserId == userId && role.Name == AccountRoles.Customer
                select membership.UserId).AnyAsync())
        {
            return ApiProblem.Create(context, 404, "beta_member_unavailable");
        }
        if (await (from membership in db.UserRoles
                join role in db.Roles on membership.RoleId equals role.Id
                where membership.UserId == userId
                    && new[] { AccountRoles.Owner, AccountRoles.Administrator, AccountRoles.Operator,
                        AccountRoles.Viewer, AccountRoles.Tester }.Contains(role.Name!)
                select membership.UserId).AnyAsync())
        { return ApiProblem.Create(context, 403, "beta_member_access_protected"); }
        if (request.ExpectedRevision != user.LaunchGroupAccessRevision)
        { return ApiProblem.Create(context, 409, "beta_member_revision_conflict"); }
        var currentlyActive = user.LaunchGroupRevokedAt is null;
        if (currentlyActive != request.Active)
        {
            if (user.Disabled || await db.DeletionRequests.AnyAsync(x => x.UserId == userId))
            { return ApiProblem.Create(context, 409, "account_not_available_for_membership_change"); }
            user.LaunchGroupRevokedAt = request.Active ? null : now;
            user.LaunchGroupAccessRevision++;
            await db.SaveChangesAsync();
            if (!request.Active)
            {
                context.RequestServices.GetService<CharacterProfileStore>()?.SuspendAccountWork(userId);
                context.RequestServices.GetService<OperationJournal>()?.SuspendCustomerAccount(userId);
            }
            await transaction.CommitAsync();
            store.AuditBetaMemberAccess(actor, id, userId, request.Active,
                user.LaunchGroupAccessRevision, timeProvider.GetUtcNow().ToUnixTimeMilliseconds());
        }
        return Results.Ok(new BetaMember(user.Id, user.DisplayName, user.LaunchGroupJoinedAt ?? user.CreatedAt,
            user.LaunchGroupRevokedAt is null, user.LaunchGroupAccessRevision));
    }

    internal static IServiceCollection AddLaunchManagement(this IServiceCollection services, IConfiguration configuration)
    {
        var accounts = AccountConfiguration.Read(configuration);
        if (!accounts.Enabled) { return services; }
        services.AddSingleton(new LaunchPolicyStore(accounts.DataRoot));
        services.AddSingleton<LaunchAccessService>();
        services.AddHostedService<LaunchScheduleStartup>();
        return services;
    }

    internal static void MapLaunchEndpoints(this WebApplication app)
    {
        app.MapMethods("/app/v1/launch/resource-access", ["GET", "HEAD"], async (HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var policy = context.RequestServices.GetService<LaunchAccessService>();
            var allowed = !context.RequestServices.GetRequiredService<AccountConfiguration>().Enabled
                || policy is not null && (await policy.EvaluateAsync(context.User)).Allowed
                || context.RequestServices.GetService<ServerAdminAccess>()?.Authenticate(context) == true
                || HasResourceReadbackCredential(context);
            // nginx auth_request accepts 2xx/401/403; never return the countdown's 423 here.
            return Results.StatusCode(allowed ? 204 : 403);
        });
        if (!app.Services.GetRequiredService<AccountConfiguration>().Enabled) { return; }
        app.MapOwnerPlayerGroups();
        app.MapGet("/app/v1/enrollment/default", (HttpContext context, LaunchPolicyStore store) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var admission = store.ReadBetaAdmission();
            var group = admission.GroupId is null ? null : store.ReadGroup(admission.GroupId);
            var open = admission.RecruitmentOpen && group is not null;
            return Results.Ok(new
            {
                available = open,
                groupId = open ? group!.Id : null,
                groupName = open ? group!.Name : null,
                fixedUniverse = open ? admission.FixedUniverse : null,
                universe = open ? admission.FixedUniverse : null
            });
        }).RequireRateLimiting("account");
        app.MapPost("/app/v1/enrollment/default/admit", AdmitUnassignedCustomerAsync)
            .RequireAuthorization().RequireRateLimiting("account");
        app.MapGet("/app/v1/customer/access", async (HttpContext context, AccountDatabase db,
            LaunchPolicyStore store) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            await OwnerPlayerGroups.EnsureSelectionAsync(context, db, store);
            var id = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
            var user = id is null ? null : await db.Users.SingleOrDefaultAsync(x => x.Id == id);
            if (user is null) { return ApiProblem.Create(context, 401, "account_access_unavailable"); }
            var group = user.LaunchGroupId is null ? null : store.ReadGroup(user.LaunchGroupId);
            var active = user.LaunchGroupId is not null && user.LaunchGroupRevokedAt is null && !user.Disabled;
            var state = user.LaunchGroupRevokedAt is not null ? "revoked"
                : user.LaunchGroupId is null ? "unassigned" : active ? "active" : "revoked";
            return Results.Ok(new
            {
                state,
                groupId = user.LaunchGroupId,
                groupName = group?.Name,
                joinedAt = user.LaunchGroupJoinedAt,
                accessRevision = user.LaunchGroupAccessRevision,
                fixedUniverse = user.AdmissionUniverse,
                launchAt = group?.LaunchAt,
                recruitmentOpen = store.TryReadOpenBetaAdmission(out _, out _)
            });
        }).RequireAuthorization().RequireRateLimiting("account");
        app.MapGet("/app/v1/launch", async (HttpContext context, LaunchAccessService policy) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return Results.Ok(await policy.EvaluateAsync(context.User));
        }).RequireRateLimiting("account");

        var admin = app.MapGroup("/app/v1/admin/launch").RequireAuthorization().RequireRateLimiting("account");
        admin.AddEndpointFilter((invocation, next) => AccountRoles.IsAdmin(invocation.HttpContext.User)
            ? next(invocation)
            : ValueTask.FromResult<object?>(ApiProblem.Create(invocation.HttpContext, 403, "account_admin_required")));
        admin.MapGet("", (LaunchPolicyStore store) => Results.Ok(store.Read()));
        admin.MapGet("/audit", (LaunchPolicyStore store) => Results.Ok(new { entries = store.ReadAudit() }));
        admin.MapGet("/groups", (LaunchPolicyStore store, TimeProvider timeProvider) => Results.Ok(new
        {
            groups = store.ReadGroups().Select(group => group.LaunchAt is not null
                && group.ForceStartAtEnd && group.LaunchAt <= timeProvider.GetUtcNow().ToUnixTimeMilliseconds()
                    ? store.CompleteForcedCountdown(group.Id, timeProvider.GetUtcNow().ToUnixTimeMilliseconds()) ?? group
                    : group).ToArray()
        }));
        admin.MapGet("/waiting-room", ReadWaitingRoomAsync);
        admin.MapPut("/waiting-room/{userId}/group", MoveWaitingRoomMemberAsync);
        admin.MapGet("/beta-admission", (LaunchPolicyStore store) =>
        {
            var admission = store.ReadBetaAdmission();
            return Results.Ok(new
            {
                admission.Revision, admission.GroupId, admission.RecruitmentOpen, admission.FixedUniverse,
                groupName = admission.GroupId is null ? null : store.ReadGroup(admission.GroupId)?.Name
            });
        });
        admin.MapPost("/beta-admission/group", async (BetaAdmissionGroupRequest request, HttpContext context,
            LaunchPolicyStore store, TimeProvider timeProvider) =>
        {
            var recent = await AccountAuthorization.RequireRecentAsync(context);
            if (recent is not null) { return recent; }
            if (context.Request.Headers["X-DV-Confirm"] != "yes")
            { return ApiProblem.Create(context, 409, "confirmation_required"); }
            try
            {
                var actor = context.User.FindFirstValue(ClaimTypes.NameIdentifier)!;
                var result = store.CreateOrSelectBetaGroup(actor, request, timeProvider.GetUtcNow().ToUnixTimeMilliseconds());
                return Results.Ok(new
                {
                    result.Admission.Revision, result.Admission.GroupId, result.Admission.RecruitmentOpen,
                    result.Admission.FixedUniverse, groupName = result.Group.Name
                });
            }
            catch (LaunchConfigurationException error)
            { return ApiProblem.Create(context, error.Status, error.Code); }
        });
        admin.MapPut("/beta-admission", async (BetaAdmissionUpdate request, HttpContext context,
            LaunchPolicyStore store, TimeProvider timeProvider) =>
        {
            var recent = await AccountAuthorization.RequireRecentAsync(context);
            if (recent is not null) { return recent; }
            if (context.Request.Headers["X-DV-Confirm"] != "yes")
            { return ApiProblem.Create(context, 409, "confirmation_required"); }
            try
            {
                var actor = context.User.FindFirstValue(ClaimTypes.NameIdentifier)!;
                var admission = store.UpdateBetaAdmission(actor, request, timeProvider.GetUtcNow().ToUnixTimeMilliseconds());
                return Results.Ok(new
                {
                    admission.Revision, admission.GroupId, admission.RecruitmentOpen, admission.FixedUniverse,
                    groupName = admission.GroupId is null ? null : store.ReadGroup(admission.GroupId)?.Name
                });
            }
            catch (LaunchConfigurationException error)
            { return ApiProblem.Create(context, error.Status, error.Code); }
        });
        admin.MapGet("/groups/{id}/members", async (string id, HttpContext context, AccountDatabase db,
            LaunchPolicyStore store) =>
        {
            if (store.ReadGroup(id) is not { } group) { return ApiProblem.Create(context, 404, "launch_group_unavailable"); }
            var members = await db.Users.Where(user =>
                    (user.LaunchGroupId == id && db.UserRoles.Any(membership => membership.UserId == user.Id
                        && db.Roles.Any(role => role.Id == membership.RoleId && role.Name == AccountRoles.Customer))
                    || db.UserRoles.Any(membership => membership.UserId == user.Id
                        && db.Roles.Any(role => role.Id == membership.RoleId && role.Name == AccountRoles.Owner)))
                    && !db.DeletionRequests.Any(deletion => deletion.UserId == user.Id))
                .OrderBy(user => user.LaunchGroupJoinedAt).ThenBy(user => user.Id)
                .Select(user => new BetaMember(user.Id, user.DisplayName, user.LaunchGroupJoinedAt ?? user.CreatedAt,
                    !user.Disabled && (user.LaunchGroupRevokedAt == null || db.UserRoles.Any(membership => membership.UserId == user.Id
                        && db.Roles.Any(role => role.Id == membership.RoleId && role.Name == AccountRoles.Owner))),
                    user.LaunchGroupAccessRevision))
                .Take(500).ToArrayAsync();
            return Results.Ok(new { group, members });
        });
        admin.MapPut("/groups/{id}/members/{userId}", SetMemberAccessAsync);
        admin.MapPost("/groups", async (LaunchGroupCreate request, HttpContext context, LaunchPolicyStore store,
            TimeProvider timeProvider) =>
        {
            var recent = await AccountAuthorization.RequireRecentAsync(context);
            if (recent is not null) { return recent; }
            if (context.Request.Headers["X-DV-Confirm"] != "yes")
            { return ApiProblem.Create(context, 409, "confirmation_required"); }
            try
            {
                var actor = context.User.FindFirstValue(ClaimTypes.NameIdentifier)!;
                return Results.Ok(new { group = store.CreateGroup(actor, request, timeProvider.GetUtcNow().ToUnixTimeMilliseconds()) });
            }
            catch (LaunchConfigurationException error)
            { return ApiProblem.Create(context, error.Status, error.Code); }
        });
        admin.MapPut("/groups/{id}", async (string id, LaunchGroupUpdate request, HttpContext context,
            LaunchPolicyStore store, TimeProvider timeProvider) =>
        {
            var recent = await AccountAuthorization.RequireRecentAsync(context);
            if (recent is not null) { return recent; }
            if (context.Request.Headers["X-DV-Confirm"] != "yes")
            { return ApiProblem.Create(context, 409, "confirmation_required"); }
            try
            {
                var actor = context.User.FindFirstValue(ClaimTypes.NameIdentifier)!;
                return Results.Ok(new { group = store.UpdateGroup(actor, id, request, timeProvider.GetUtcNow().ToUnixTimeMilliseconds()) });
            }
            catch (LaunchConfigurationException error)
            { return ApiProblem.Create(context, error.Status, error.Code); }
        });
        admin.MapPut("/groups/{id}/countdown", async (string id, LaunchGroupCountdownUpdate request,
            HttpContext context, LaunchPolicyStore store, TimeProvider timeProvider) =>
        {
            var recent = await AccountAuthorization.RequireRecentAsync(context);
            if (recent is not null) { return recent; }
            if (context.Request.Headers["X-DV-Confirm"] != "yes")
            { return ApiProblem.Create(context, 409, "confirmation_required"); }
            try
            {
                return Results.Ok(new { group = store.SaveCountdown(context.User.FindFirstValue(ClaimTypes.NameIdentifier)!,
                    id, request, timeProvider.GetUtcNow().ToUnixTimeMilliseconds()) });
            }
            catch (LaunchConfigurationException error)
            { return ApiProblem.Create(context, error.Status, error.Code); }
        });
        admin.MapPost("/groups/{id}/countdown/confirm", async (string id, LaunchGroupRevision request,
            HttpContext context, LaunchPolicyStore store, TimeProvider timeProvider) =>
        {
            var recent = await AccountAuthorization.RequireRecentAsync(context);
            if (recent is not null) { return recent; }
            if (context.Request.Headers["X-DV-Confirm"] != "yes")
            { return ApiProblem.Create(context, 409, "confirmation_required"); }
            try
            {
                return Results.Ok(new { group = store.ConfirmCountdown(context.User.FindFirstValue(ClaimTypes.NameIdentifier)!,
                    id, request, timeProvider.GetUtcNow().ToUnixTimeMilliseconds()) });
            }
            catch (LaunchConfigurationException error)
            { return ApiProblem.Create(context, error.Status, error.Code); }
        });
        admin.MapPost("/groups/{id}/start", async (string id, LaunchGroupRevision request,
            HttpContext context, LaunchPolicyStore store, TimeProvider timeProvider) =>
        {
            var recent = await AccountAuthorization.RequireRecentAsync(context);
            if (recent is not null) { return recent; }
            if (context.Request.Headers["X-DV-Confirm"] != "yes")
            { return ApiProblem.Create(context, 409, "confirmation_required"); }
            try
            {
                return Results.Ok(new { group = store.StartGroup(context.User.FindFirstValue(ClaimTypes.NameIdentifier)!,
                    id, request, timeProvider.GetUtcNow().ToUnixTimeMilliseconds()) });
            }
            catch (LaunchConfigurationException error)
            { return ApiProblem.Create(context, error.Status, error.Code); }
        });
        admin.MapPut("", async (LaunchConfigurationUpdate update, HttpContext context, LaunchPolicyStore store,
            TimeProvider timeProvider) =>
        {
            var recent = await AccountAuthorization.RequireRecentAsync(context);
            if (recent is not null) { return recent; }
            if (context.Request.Headers["X-DV-Confirm"] != "yes")
            { return ApiProblem.Create(context, 409, "confirmation_required"); }
            try
            {
                var actor = context.User.FindFirstValue(ClaimTypes.NameIdentifier)!;
                return Results.Ok(store.Update(actor, update, timeProvider.GetUtcNow().ToUnixTimeMilliseconds()));
            }
            catch (LaunchConfigurationException error)
            {
                return ApiProblem.Create(context, error.Status, error.Code);
            }
        });
    }

    private static bool HasResourceReadbackCredential(HttpContext context)
    {
        // This optional capability authorizes only model/download reads, never operational admin APIs.
        var expected = context.RequestServices.GetRequiredService<IConfiguration>()["Launch:ResourceReadbackTokenSha256"];
        var header = context.Request.Headers.Authorization.ToString();
        if (expected is not { Length: 64 } || !expected.All(Uri.IsHexDigit)
            || !header.StartsWith("Bearer ", StringComparison.Ordinal)) { return false; }
        var credential = header[7..];
        return credential.Length is >= 32 and <= 256 && !credential.Any(char.IsWhiteSpace)
            && CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(credential)), Convert.FromHexString(expected));
    }

    internal static void UseLaunchAccessPolicy(this WebApplication app)
    {
        if (!app.Services.GetRequiredService<AccountConfiguration>().Enabled) { return; }
        app.Use(async (context, next) =>
        {
            var path = context.Request.Path;
            if (HttpMethods.IsGet(context.Request.Method)
                && path.Equals("/app/v1/application-version", StringComparison.OrdinalIgnoreCase))
            {
                await next(context);
                return;
            }

            if (!IsApplicationSurface(path))
            {
                await next(context);
                return;
            }

            var access = await context.RequestServices.GetRequiredService<LaunchAccessService>().EvaluateAsync(context.User);
            var hasAccountSession = context.User.HasClaim(claim => claim.Type == AccountIdentity.SessionClaim);
            var isPublicDeviceApi = !hasAccountSession && path.StartsWithSegments("/v1")
                && !path.StartsWithSegments("/v1/admin")
                && !path.StartsWithSegments("/v1/keepsakes")
                && IsValidDeviceRequest(context, path);
            var isOperatorApi = !hasAccountSession && IsValidOperatorRequest(context, path);
            if (hasAccountSession && access.Audience == "revoked" && !IsRevokedSafe(path))
            {
                await ApiProblem.Create(context, 403, "customer_access_revoked").ExecuteAsync(context);
                return;
            }
            if (access.Allowed || hasAccountSession && IsPrelaunchSafe(path)
                || !hasAccountSession && IsAuthenticationSurface(path)
                || isPublicDeviceApi || isOperatorApi || !hasAccountSession && IsTwinRequest(context, path))
            {
                await next(context);
                return;
            }

            await ApiProblem.Create(context, 423, "launch_pending").ExecuteAsync(context);
        });
    }

    private static bool IsApplicationSurface(PathString path) =>
        path.StartsWithSegments("/app/v1") || path.StartsWithSegments("/v1")
        || path.StartsWithSegments("/guest") || path.StartsWithSegments("/keepsake")
        || path.Equals("/twin", StringComparison.OrdinalIgnoreCase);

    private static bool IsPrelaunchSafe(PathString path) =>
        path.StartsWithSegments("/app/v1/push", StringComparison.OrdinalIgnoreCase) ||
        path.Equals("/app/v1/session", StringComparison.OrdinalIgnoreCase)
        || path.Equals("/app/v1/account/ai-consent", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/app/v1/customer/wand", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/app/v1/customer/enrollment", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/app/v1/passkeys", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/app/v1/enrollment", StringComparison.OrdinalIgnoreCase)
        || path.Equals("/app/v1/account", StringComparison.OrdinalIgnoreCase)
        || path.Equals("/app/v1/customer/access", StringComparison.OrdinalIgnoreCase)
        || path.Equals("/app/v1/account/deletion", StringComparison.OrdinalIgnoreCase)
        || path.Equals("/app/v1/account/data-export", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/app/v1/account/data-export/", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/app/v1/customer/data/exports", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/app/v1/launch", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/app/v1/prelaunch", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/app/v1/admin/launch", StringComparison.OrdinalIgnoreCase);

    private static bool IsRevokedSafe(PathString path) =>
        path.StartsWithSegments("/app/v1/push", StringComparison.OrdinalIgnoreCase) ||
        path.Equals("/app/v1/session", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/app/v1/passkeys", StringComparison.OrdinalIgnoreCase)
        || path.Equals("/app/v1/account", StringComparison.OrdinalIgnoreCase)
        || path.Equals("/app/v1/customer/access", StringComparison.OrdinalIgnoreCase)
        || path.Equals("/app/v1/account/deletion", StringComparison.OrdinalIgnoreCase)
        || path.Equals("/app/v1/account/data-export", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/app/v1/account/data-export/", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/app/v1/customer/data/exports", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/app/v1/launch", StringComparison.OrdinalIgnoreCase);

    private static IResult AdmitUnassignedCustomerAsync(HttpContext context, CustomerAdmissionAuthority authority,
        LaunchPolicyStore store, TimeProvider timeProvider)
    {
        var admission = store.ReadBetaAdmission();
        if (admission.GroupId is null || !admission.RecruitmentOpen)
        { return ApiProblem.Create(context, 423, "beta_enrollment_closed"); }
        var actor = context.User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        BetaMember member;
        using (var reservation = store.ReserveBetaEnrollment(admission.GroupId, admission.Revision))
        {
            if (reservation is null)
            { return ApiProblem.Create(context, 409, "beta_enrollment_closed_or_changed"); }
            using var account = authority.AcquireAccountCookie(context, context.RequestAborted);
            if (account.Proof.LaunchGroupId is not null)
            { return ApiProblem.Create(context, 409, "customer_access_already_assigned"); }
            if (!account.Proof.Roles.Contains(AccountRoles.Customer, StringComparer.Ordinal))
            { return ApiProblem.Create(context, 403, "customer_role_required"); }
            var now = timeProvider.GetUtcNow().ToUnixTimeSeconds();
            if (!account.AdmitToGroup(reservation.Group.Id, now, reservation.Admission.FixedUniverse))
            { return ApiProblem.Create(context, 409, "customer_access_admission_conflict"); }
            account.Commit();
            member = new(account.Proof.AccountId, account.Proof.DisplayName ?? "", now, true, 1);
        }
        store.AuditBetaMemberAdmitted(actor, admission.GroupId, member.AccessRevision,
            timeProvider.GetUtcNow().ToUnixTimeMilliseconds());
        return Results.Ok(new
        {
            state = "active", groupId = admission.GroupId, groupName = store.ReadGroup(admission.GroupId)?.Name,
            joinedAt = member.JoinedAt, accessRevision = member.AccessRevision,
            fixedUniverse = admission.FixedUniverse
        });
    }

    private static bool IsAuthenticationSurface(PathString path) =>
        path.Equals("/app/v1/session", StringComparison.OrdinalIgnoreCase)
        || System.Text.RegularExpressions.Regex.IsMatch(path.Value ?? "", "\\A/app/v1/customer/wand/sessions/[0-9a-f]{32}/live\\z")
        || path.StartsWithSegments("/app/v1/customer/enrollment", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/app/v1/passkeys", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/app/v1/enrollment", StringComparison.OrdinalIgnoreCase)
        || path.Equals("/app/v1/account/deletion", StringComparison.OrdinalIgnoreCase)
        || path.Equals("/app/v1/launch/resource-access", StringComparison.OrdinalIgnoreCase)
        || path.Equals("/app/v1/launch", StringComparison.OrdinalIgnoreCase);

    private static bool IsValidDeviceRequest(HttpContext context, PathString path)
    {
        var devices = context.RequestServices.GetService<DeviceAccessStore>();
        if (devices is null) { return false; }
        return path.Equals("/v1/device-access/token", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/v1/twin/device-token", StringComparison.OrdinalIgnoreCase)
            ? DeviceAccessEndpoints.AuthenticateCredential(context, devices) is not null
            : DeviceAccessEndpoints.Authenticate(context, devices) is not null;
    }

    private static bool IsTwinRequest(HttpContext context, PathString path)
    {
        if (!path.StartsWithSegments("/v1/twin") && !path.Equals("/twin", StringComparison.OrdinalIgnoreCase)) { return false; }
        // This exact authentication endpoint validates the submitted credential and same-origin request itself.
        if (path.Equals("/v1/twin/session", StringComparison.OrdinalIgnoreCase)
            && context.Request.Method is "POST" or "DELETE") { return true; }
        var access = context.RequestServices.GetService<TwinAccess>();
        if (access is null) { return false; }
        if (path.StartsWithSegments("/v1/twin/ingest", out var remainder))
        {
            var header = context.Request.Headers.Authorization.ToString();
            var source = header.StartsWith("Bearer ", StringComparison.Ordinal) ? access.VerifySource(header[7..]) : null;
            return source is { Role: "ingest" } && remainder.Value == "/" + source.InstanceId;
        }
        return access.Session(context.Request.Cookies["__Host-kufr-twin"]) is not null;
    }

    private static bool IsValidOperatorRequest(HttpContext context, PathString path)
    {
        if (path.StartsWithSegments("/v1/admin/logs", StringComparison.OrdinalIgnoreCase))
        {
            return context.RequestServices.GetService<LogReadAccess>()?.Authenticate(context) == true;
        }

        var authorizedPath = path.StartsWithSegments("/v1/admin")
            || path.StartsWithSegments("/v1/show")
            || path.StartsWithSegments("/v1/keepsakes")
                && path.Value?.EndsWith("/restore-owner", StringComparison.OrdinalIgnoreCase) == true;
        return authorizedPath
            && context.RequestServices.GetService<ServerAdminAccess>()?.Authenticate(context) == true;
    }
}
