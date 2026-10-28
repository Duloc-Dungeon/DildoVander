using System.Net;
using System.Security.Claims;
using System.Text.Json;
using Kufr.Backend.Features.ApplicationUpdates;
using Kufr.Backend.Features.Identity;
using Kufr.Backend.Features.Launch;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.EntityFrameworkCore;

internal static class ApplicationUpdateTests
{
    public static async Task RunAsync()
    {
        await EndpointReturnsConfiguredPlatformsWithoutAuthenticationAsync();
        await EndpointUsesNonForcingDefaultsAsync();
        await PaddedBuildComponentsCompareNumericallyAsync();
        await EndpointPassesProductionLaunchGateAsync();
        await InvalidConfigurationFailsAtStartupAsync();
        Console.WriteLine("Application update checks passed.");
    }

    private static async Task EndpointUsesNonForcingDefaultsAsync()
    {
        await using var app = await StartAppAsync(new Dictionary<string, string?>());
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        using var response = await client.GetAsync("/app/v1/application-version");
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Check(response.StatusCode == HttpStatusCode.OK, "default version endpoint is available");
        CheckPlatform(document.RootElement, "ios", "0", "0", "https://dildovander.de/ios/");
        CheckPlatform(document.RootElement, "android", "0", "0", string.Empty);
    }

    private static async Task EndpointReturnsConfiguredPlatformsWithoutAuthenticationAsync()
    {
        var configuration = new Dictionary<string, string?>
        {
            ["ApplicationUpdates:Ios:MinimumBuild"] = "10.9.1",
            ["ApplicationUpdates:Ios:LatestBuild"] = "10.10.1",
            ["ApplicationUpdates:Ios:UpdateUrl"] = "https://dildovander.de/ios/",
            ["ApplicationUpdates:Android:MinimumBuild"] = "1000000",
            ["ApplicationUpdates:Android:LatestBuild"] = "1000005",
            ["ApplicationUpdates:Android:UpdateUrl"] = "https://example.com/android",
        };

        await using var app = await StartAppAsync(configuration);
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        using var response = await client.GetAsync("/app/v1/application-version");
        Check(response.StatusCode == HttpStatusCode.OK, "version endpoint is available without authentication");
        Check(response.Headers.CacheControl?.NoStore == true, "version response disables caching");

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        CheckPlatform(root, "ios", "10.9.1", "10.10.1", "https://dildovander.de/ios/");
        CheckPlatform(root, "android", "1000000", "1000005", "https://example.com/android");
    }

    private static async Task PaddedBuildComponentsCompareNumericallyAsync()
    {
        await using var app = await StartAppAsync(new Dictionary<string, string?>
        {
            ["ApplicationUpdates:Ios:MinimumBuild"] = "10.9",
            ["ApplicationUpdates:Ios:LatestBuild"] = "10.9.0",
            ["ApplicationUpdates:Android:MinimumBuild"] = "3",
            ["ApplicationUpdates:Android:LatestBuild"] = "003",
            ["ApplicationUpdates:Android:UpdateUrl"] = "https://example.com/android",
        });
    }

    private static async Task EndpointPassesProductionLaunchGateAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "dildovander-app-update-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var launchStore = new LaunchPolicyStore(Path.Combine(root, "launch"));
            launchStore.Update("admin", new(0, false, null, false, true, 9_000_000_000_000, false,
                "Europe/Prague", "", "waiting", false, "prompt"), 1_000);

            var accountConnectionString = new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(root, "accounts.sqlite"),
                Pooling = false,
            }.ToString();
            using (var connection = new SqliteConnection(accountConnectionString))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE AspNetUsers (
                        Id TEXT NOT NULL PRIMARY KEY, LaunchGroupId TEXT NULL,
                        LaunchGroupAccessRevision INTEGER NOT NULL, LaunchGroupRevokedAt INTEGER NULL);
                    INSERT INTO AspNetUsers(Id,LaunchGroupAccessRevision)
                        VALUES('locked-user',0);
                    INSERT INTO AspNetUsers(Id,LaunchGroupAccessRevision,LaunchGroupRevokedAt)
                        VALUES('revoked-user',1,1000);
                    """;
                command.ExecuteNonQuery();
            }

            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ApplicationIdentity:Enabled"] = "true",
            });
            builder.Logging.ClearProviders();
            builder.Services.Configure<IdentityOptions>(options =>
                options.Stores.SchemaVersion = IdentitySchemaVersions.Version3);
            builder.Services.AddDbContext<AccountDatabase>(options => options.UseSqlite(accountConnectionString));
            builder.Services.AddSingleton(new AccountConfiguration(true, root, root, "localhost", ["https://localhost"]));
            builder.Services.AddSingleton(launchStore);
            builder.Services.AddSingleton(TimeProvider.System);
            builder.Services.AddSingleton<LaunchAccessService>(services => new LaunchAccessService(
                launchStore,
                TimeProvider.System,
                services.GetRequiredService<IServiceScopeFactory>()));
            builder.Services.AddApplicationUpdateSettings(builder.Configuration);
            builder.WebHost.UseUrls("http://127.0.0.1:0");

            await using var app = builder.Build();
            app.Use(async (context, next) =>
            {
                var claims = new List<Claim>();
                var userId = context.Request.Headers["X-Test-User"].ToString();
                if (userId.Length > 0)
                {
                    claims.Add(new(ClaimTypes.NameIdentifier, userId));
                    claims.Add(new(ClaimTypes.Role, AccountRoles.Customer));
                    claims.Add(new(AccountIdentity.SessionClaim, "test-session"));
                }

                context.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
                await next(context);
            });
            app.UseLaunchAccessPolicy();
            app.MapApplicationUpdateEndpoints();
            app.MapGet("/app/v1/customer/character", () => Results.Ok());
            await app.StartAsync();

            try
            {
                var address = app.Services.GetRequiredService<IServer>()
                    .Features.Get<IServerAddressesFeature>()!.Addresses.Single();
                using var client = new HttpClient { BaseAddress = new Uri(address) };

                using var anonymous = await client.GetAsync("/app/v1/application-version");
                Check(anonymous.StatusCode == HttpStatusCode.OK,
                    "anonymous app version request bypasses a locked production launch gate");

                using var lockedRequest = new HttpRequestMessage(HttpMethod.Get, "/app/v1/application-version");
                lockedRequest.Headers.Add("X-Test-User", "locked-user");
                using var locked = await client.SendAsync(lockedRequest);
                Check(locked.StatusCode == HttpStatusCode.OK,
                    "locked signed-in account can check its application version");

                using var revokedRequest = new HttpRequestMessage(HttpMethod.Get, "/app/v1/application-version");
                revokedRequest.Headers.Add("X-Test-User", "revoked-user");
                using var revoked = await client.SendAsync(revokedRequest);
                Check(revoked.StatusCode == HttpStatusCode.OK,
                    "revoked account can check its application version");

                using var ordinaryRequest = new HttpRequestMessage(HttpMethod.Get, "/app/v1/customer/character");
                ordinaryRequest.Headers.Add("X-Test-User", "locked-user");
                using var ordinary = await client.SendAsync(ordinaryRequest);
                Check((int)ordinary.StatusCode == 423,
                    "application version exception does not open other locked application APIs");

                using var wrongMethod = new HttpRequestMessage(HttpMethod.Post, "/app/v1/application-version");
                using var deniedMethod = await client.SendAsync(wrongMethod);
                Check((int)deniedMethod.StatusCode == 423,
                    "launch gate exception is limited to the GET version endpoint");
            }
            finally
            {
                await app.StopAsync();
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task InvalidConfigurationFailsAtStartupAsync()
    {
        await ExpectStartupValidationFailureAsync(new Dictionary<string, string?>
        {
            ["ApplicationUpdates:Ios:MinimumBuild"] = "10.10.1",
            ["ApplicationUpdates:Ios:LatestBuild"] = "10.9.1",
        });
        await ExpectStartupValidationFailureAsync(new Dictionary<string, string?>
        {
            ["ApplicationUpdates:Ios:MinimumBuild"] = "1.0.0.0",
        });
        await ExpectStartupValidationFailureAsync(new Dictionary<string, string?>
        {
            ["ApplicationUpdates:Ios:MinimumBuild"] = "+1",
        });
        await ExpectStartupValidationFailureAsync(new Dictionary<string, string?>
        {
            ["ApplicationUpdates:Ios:MinimumBuild"] = " 1",
        });
        await ExpectStartupValidationFailureAsync(new Dictionary<string, string?>
        {
            ["ApplicationUpdates:Ios:MinimumBuild"] = "00000000001",
        });
        await ExpectStartupValidationFailureAsync(new Dictionary<string, string?>
        {
            ["ApplicationUpdates:Ios:MinimumBuild"] = "2147483648",
        });
        await ExpectStartupValidationFailureAsync(new Dictionary<string, string?>
        {
            ["ApplicationUpdates:Android:LatestBuild"] = "2100000001",
        });
        await ExpectStartupValidationFailureAsync(new Dictionary<string, string?>
        {
            ["ApplicationUpdates:Ios:UpdateUrl"] = "http://dildovander.de/ios/",
        });
        await ExpectStartupValidationFailureAsync(new Dictionary<string, string?>
        {
            ["ApplicationUpdates:Ios:UpdateUrl"] = "https://user:password@dildovander.de/ios/",
        });
        await ExpectStartupValidationFailureAsync(new Dictionary<string, string?>
        {
            ["ApplicationUpdates:Android:LatestBuild"] = "1.0",
        });
    }

    private static async Task ExpectStartupValidationFailureAsync(Dictionary<string, string?> values)
    {
        await using var app = BuildApp(values);
        try
        {
            await app.StartAsync();
        }
        catch (OptionsValidationException)
        {
            return;
        }

        throw new InvalidOperationException("Invalid application update configuration was accepted.");
    }

    private static async Task<WebApplication> StartAppAsync(Dictionary<string, string?> values)
    {
        var app = BuildApp(values);
        try
        {
            await app.StartAsync();
            return app;
        }
        catch
        {
            await app.DisposeAsync();
            throw;
        }
    }

    private static WebApplication BuildApp(Dictionary<string, string?> values)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.Configuration.AddInMemoryCollection(values);
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddApplicationUpdateSettings(builder.Configuration);
        var app = builder.Build();
        app.MapApplicationUpdateEndpoints();
        return app;
    }

    private static void CheckPlatform(JsonElement root, string name, string minimum, string latest, string updateUrl)
    {
        var platform = root.GetProperty(name);
        Check(platform.GetProperty("minimumBuild").GetString() == minimum, $"{name} minimum build is configured");
        Check(platform.GetProperty("latestBuild").GetString() == latest, $"{name} latest build is configured");
        Check(platform.GetProperty("updateUrl").GetString() == updateUrl, $"{name} update URL is configured");
    }

    private static void Check(bool condition, string description)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"Application update check failed: {description}.");
        }
    }
}
