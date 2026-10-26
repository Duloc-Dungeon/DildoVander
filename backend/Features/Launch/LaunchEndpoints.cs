using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Kufr.Backend.Features.Access;
using Kufr.Backend.Features.Diagnostics;
using Kufr.Backend.Features.DigitalTwin;
using Kufr.Backend.Features.Identity;

namespace Kufr.Backend.Features.Launch;

internal static class LaunchEndpoints
{
    internal static IServiceCollection AddLaunchManagement(this IServiceCollection services, IConfiguration configuration)
    {
        var accounts = AccountConfiguration.Read(configuration);
        if (!accounts.Enabled) { return services; }
        services.AddSingleton(new LaunchPolicyStore(accounts.DataRoot));
        services.AddSingleton<LaunchAccessService>();
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
        admin.MapGet("/groups", (LaunchPolicyStore store) => Results.Ok(new { groups = store.ReadGroups() }));
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
        path.Equals("/app/v1/session", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/app/v1/passkeys", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/app/v1/enrollment", StringComparison.OrdinalIgnoreCase)
        || path.Equals("/app/v1/account", StringComparison.OrdinalIgnoreCase)
        || path.Equals("/app/v1/account/deletion", StringComparison.OrdinalIgnoreCase)
        || path.Equals("/app/v1/account/data-export", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/app/v1/account/data-export/", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/app/v1/customer/data/exports", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/app/v1/launch", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/app/v1/prelaunch", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/app/v1/admin/launch", StringComparison.OrdinalIgnoreCase);

    private static bool IsAuthenticationSurface(PathString path) =>
        path.Equals("/app/v1/session", StringComparison.OrdinalIgnoreCase)
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
