using System.Net;
using System.Security.Cryptography;
using Kufr.Backend.Features.Access;
using Kufr.Backend.Features.Launch;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;

namespace Kufr.Backend.Features.Identity;

internal sealed class LocalTryHubConfiguration
{
    private readonly byte[] secret;
    internal string Origin { get; }

    private LocalTryHubConfiguration(string origin, byte[] secret)
    {
        Origin = origin;
        this.secret = secret;
    }

    internal static LocalTryHubConfiguration Read(IConfiguration configuration, AccountConfiguration identity)
    {
        var origin = configuration["LocalTryHub:Origin"];
        var configuredSecret = configuration["LocalTryHub:SessionSecret"];
        var root = configuration["LocalTryHub:DataRoot"];
        if (!identity.Enabled || !Uri.TryCreate(origin, UriKind.Absolute, out var uri)
            || uri.Scheme != "https" || uri.Host != "localhost" || !string.IsNullOrEmpty(uri.UserInfo)
            || uri.GetLeftPart(UriPartial.Authority) != origin
            || !identity.AllowedOrigins.Contains(origin, StringComparer.Ordinal)
            || configuredSecret?.Length != 64 || !configuredSecret.All(Uri.IsHexDigit)
            || string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root)
            || Path.GetPathRoot(Path.GetFullPath(root)) == Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)))
        {
            throw new InvalidOperationException("LocalTryHub requires an exact localhost HTTPS origin, a random 64-hex session secret and isolated absolute data paths.");
        }
        RequireContained(root, identity.DataRoot);
        RequireContained(root, identity.KeyRoot);
        RequireContained(root, configuration["Backend:DatabasePath"]);
        foreach (var key in new[] { "Keepsake:DataRoot", "Keepsake:DataProtectionRoot", "ModelReleases:Root" })
        {
            if (configuration[key] is { Length: > 0 } path) { RequireContained(root, path); }
        }
        return new(origin, Convert.FromHexString(configuredSecret));
    }

    private static void RequireContained(string root, string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            throw new InvalidOperationException("LocalTryHub data paths must remain inside its isolated root.");
        }
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var fullPath = Path.GetFullPath(path);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, comparison))
        {
            throw new InvalidOperationException("LocalTryHub data paths must remain inside its isolated root.");
        }
        // Lexical containment must not let a junction/symlink route the isolated
        // account or its keys back to another installation's durable files.
        for (var ancestor = fullPath; ancestor is not null; ancestor = Path.GetDirectoryName(ancestor))
        {
            if ((Directory.Exists(ancestor) || File.Exists(ancestor))
                && (File.GetAttributes(ancestor) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException("LocalTryHub data paths cannot traverse links or junctions.");
            }
        }
    }

    internal bool Allows(HttpContext context, IPAddress? transportPeer, IPAddress? transportLocal)
    {
        var supplied = context.Request.Headers["X-DV-Local-Try"].ToString();
        return context.Request.IsHttps && transportPeer is not null && IPAddress.IsLoopback(transportPeer)
            && transportLocal is not null && IPAddress.IsLoopback(transportLocal)
            && context.Request.Headers.Origin.ToString() == Origin
            && supplied.Length == 64 && supplied.All(Uri.IsHexDigit)
            && CryptographicOperations.FixedTimeEquals(secret, Convert.FromHexString(supplied));
    }
}

internal static partial class AccountEndpoints
{
    private const string LocalTryHubUserId = "dv-local-try-hub";
    private const string LocalTryHubUserName = "local-try-hub@localhost.invalid";
    private static readonly object LocalTryHubTransportKey = new();

    // Register before forwarded-header middleware so IP authorization uses the
    // socket peer rather than a client-controlled forwarded address.
    internal static void MapLocalTryHubSession(this WebApplication app)
    {
        if (!app.Environment.IsDevelopment() || !app.Configuration.GetValue<bool>("LocalTryHub:Enabled")) { return; }
        var configuration = LocalTryHubConfiguration.Read(app.Configuration, app.Services.GetRequiredService<AccountConfiguration>());
        var experiments = app.Services.GetService<LocalTryHubExperimentStore>()
            ?? new LocalTryHubExperimentStore(app.Configuration, app.Environment);
        var accountGate = new SemaphoreSlim(1, 1);
        app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments("/local-try-api"))
            {
                context.Items[LocalTryHubTransportKey] = (context.Connection.RemoteIpAddress, context.Connection.LocalIpAddress);
                // Two 12k prompts can expand to six JSON bytes per character.
                if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } bodySize)
                { bodySize.MaxRequestBodySize = 256 * 1024; }
            }
            await next(context);
        });
        bool Authorized(HttpContext context)
        {
            context.Response.Headers.CacheControl = "no-store";
            return context.Items[LocalTryHubTransportKey] is ValueTuple<IPAddress?, IPAddress?> transport
                && configuration.Allows(context, transport.Item1, transport.Item2);
        }
        app.MapPost("/local-try-api/experiments/read", (HttpContext context) => Authorized(context)
            ? Results.Ok(experiments.View()) : ApiProblem.Create(context, 403, "local_try_hub_denied"));
        app.MapPost("/local-try-api/experiments", (HttpContext context, LocalTryHubExperimentUpdate change) => Authorized(context)
            ? experiments.Save(context, change) : ApiProblem.Create(context, 403, "local_try_hub_denied"));
        app.MapPost("/local-try-api/session", async (HttpContext context, UserManager<AccountUser> users,
            SignInManager<AccountUser> signIn, AccountDatabase db, LaunchPolicyStore launch) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!Authorized(context))
            {
                return ApiProblem.Create(context, 403, "local_try_hub_denied");
            }
            // A valid account cookie retains its account, roles and processing
            // choices. This shortcut never rebinds an already signed-in browser.
            if (context.User.Identity?.IsAuthenticated == true)
            {
                return Results.Ok(new { ready = true });
            }
            await accountGate.WaitAsync(context.RequestAborted);
            try
            {
                var user = await users.FindByIdAsync(LocalTryHubUserId);
                if (user is null)
                {
                    user = new AccountUser
                    {
                        Id = LocalTryHubUserId, UserName = LocalTryHubUserName,
                        DisplayName = "Local try guest", CreatedAt = Now()
                    };
                    if (!(await users.CreateAsync(user)).Succeeded)
                    {
                        return ApiProblem.Create(context, 409, "local_try_hub_account_unavailable");
                    }
                }
                var roles = await users.GetRolesAsync(user);
                if (user.Disabled || user.UserName != LocalTryHubUserName || user.LaunchGroupRevokedAt is not null
                    || roles.Any(role => role is not (AccountRoles.Customer or AccountRoles.Tester))
                    || await db.DeletionRequests.AnyAsync(request => request.UserId == user.Id, context.RequestAborted))
                {
                    return ApiProblem.Create(context, 403, "local_try_hub_account_unavailable");
                }
                var missingRoles = new[] { AccountRoles.Customer, AccountRoles.Tester }.Except(roles).ToArray();
                if (missingRoles.Length > 0 && !(await users.AddToRolesAsync(user, missingRoles)).Succeeded)
                {
                    return ApiProblem.Create(context, 409, "local_try_hub_account_unavailable");
                }
                if (user.LaunchGroupId is null)
                {
                    var group = launch.ReadGroups().SingleOrDefault(group => group.Name == "Local try hub")
                        ?? launch.CreateGroup(LocalTryHubUserId, new("Local try hub", null), Now() * 1000);
                    user.LaunchGroupId = group.Id;
                    user.LaunchGroupJoinedAt = Now();
                    user.LaunchGroupAccessRevision++;
                    if (!(await users.UpdateAsync(user)).Succeeded)
                    {
                        return ApiProblem.Create(context, 409, "local_try_hub_account_unavailable");
                    }
                }
                // The normal session cookie is reused, but this shortcut does not
                // claim that a passkey was recently presented.
                await CreateSessionAsync(context, user, signIn, db, authenticatedAt: 0);
                return Results.Ok(new { ready = true });
            }
            finally { accountGate.Release(); }
        });
    }
}
