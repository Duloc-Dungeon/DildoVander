using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Kufr.Backend.Features.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

internal static partial class AccountIdentityTests
{
    public static async Task RunAccountRateLimitsAsync()
    {
        await AccountReadBudgetsAsync();
        await AccountSessionSurvivesPollingAsync();
        Console.WriteLine("PASS account read/socket limits, isolated session checks and command budgets");
    }

    private static async Task AccountReadBudgetsAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.Configuration.Sources.Clear();
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ApplicationIdentity:Enabled"] = "false",
            ["ApplicationIdentity:RequestsPerMinute"] = "10",
        });
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        builder.Services.AddAccountIdentity(builder.Configuration);
        await using var app = builder.Build();
        app.UseRateLimiter();
        app.UseAccountIdentity();
        app.MapAccountEndpoints();
        app.MapMethods("/test/read", ["GET", "HEAD"], () => Results.NoContent()).RequireRateLimiting("account");
        app.MapPost("/test/command", () => Results.NoContent()).RequireRateLimiting("account");
        app.MapPost("/test/socket-ticket", () => Results.NoContent()).RequireRateLimiting("account")
            .WithMetadata(new AccountIdentity.SocketTicketRateLimitMetadata());
        app.MapGet("/test/poll", () => Results.NoContent()).RequireRateLimiting(AccountIdentity.PollingRateLimitPolicy);
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };

        for (var command = 0; command < 10; command++)
        {
            using var response = await client.PostAsync("/test/command", null);
            Check(response.StatusCode == HttpStatusCode.NoContent, "commands retain their configured allowance");
        }
        using (var command = await client.PostAsync("/test/command", null))
        {
            Check(command.StatusCode == HttpStatusCode.TooManyRequests, "commands retain their lower configured limit");
        }
        using (var disguised = SocketUpgradeRequest("/test/command"))
        {
            disguised.Method = HttpMethod.Post;
            using var response = await client.SendAsync(disguised);
            Check(response.StatusCode == HttpStatusCode.TooManyRequests,
                "adding an Upgrade header cannot move an ordinary command into the socket allowance");
        }
        using (var head = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, "/test/read")))
        {
            Check(head.StatusCode == HttpStatusCode.NoContent, "HEAD uses the ordinary read allowance");
        }
        for (var read = 1; read < 4000; read++)
        {
            using var response = await client.GetAsync("/test/read");
            Check(response.StatusCode == HttpStatusCode.NoContent, "default read allowance supports 4000 reads per minute");
        }
        using (var limited = await client.GetAsync("/test/read"))
        {
            Check(limited.StatusCode == HttpStatusCode.TooManyRequests && limited.Headers.RetryAfter is not null,
                "ordinary reads remain bounded and advertise retry timing");
            var problem = await limited.Content.ReadFromJsonAsync<JsonElement>();
            Check(problem.GetProperty("code").GetString() == "rate_limited", "read rejection preserves the problem contract");
        }
        foreach (var path in new[] { "/app/v1/session", "/app/v1/session/", "/APP/V1/SESSION" })
        {
            using var session = await client.GetAsync(path);
            Check(session.StatusCode == HttpStatusCode.OK, "session checks retain their allowance after ordinary read exhaustion");
        }
        for (var poll = 0; poll < 41; poll++)
        {
            using var response = await client.GetAsync("/test/poll");
            Check(response.StatusCode == HttpStatusCode.NoContent, "dedicated polling does not consume ordinary read or command allowances");
        }
        for (var upgrade = 0; upgrade < 4000; upgrade++)
        {
            using var request = SocketUpgradeRequest("/test/read");
            using var response = await client.SendAsync(request);
            Check(response.StatusCode == HttpStatusCode.NoContent,
                "4000 socket admissions remain available after ordinary read and command exhaustion");
        }
        using (var upgrade = SocketUpgradeRequest("/test/read"))
        using (var response = await client.SendAsync(upgrade))
        {
            Check(response.StatusCode == HttpStatusCode.TooManyRequests, "socket admission retains its own finite allowance");
        }
        for (var ticket = 0; ticket < 41; ticket++)
        {
            using var response = await client.PostAsync("/test/socket-ticket", null);
            Check(response.StatusCode == HttpStatusCode.NoContent,
                "one-use socket ticket renewal has the increased allowance independent of commands and handshakes");
        }
        for (var upgrade = 0; upgrade < 41; upgrade++)
        {
            using var request = SocketUpgradeRequest("/test/poll");
            using var response = await client.SendAsync(request);
            Check(response.StatusCode == HttpStatusCode.NoContent, "dedicated polling socket allowance is also increased");
        }
        using var finalSession = await client.GetAsync("/app/v1/session");
        Check(finalSession.StatusCode == HttpStatusCode.OK, "command exhaustion does not block session verification");
    }

    private static HttpRequestMessage SocketUpgradeRequest(string path)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("Connection", "Upgrade");
        request.Headers.Add("Upgrade", "websocket");
        return request;
    }

    private static async Task AccountSessionSurvivesPollingAsync()
    {
        await using var fixture = await Fixture.StartAsync(configureConfiguration: configuration =>
        {
            configuration["ApplicationIdentity:ReadRequestsPerMinute"] = "10";
        });
        using var owner = fixture.Client();
        using var key = new VirtualPasskey();
        await EnrollAsync(owner, key, Fixture.Bootstrap, "Owner");
        var session = await CsrfAsync(owner);
        var accountId = session.GetProperty("account").GetProperty("id").GetString();
        for (var read = 0; read < 10; read++)
        {
            using var response = await owner.GetAsync("/app/v1/account");
            Check(response.StatusCode == HttpStatusCode.OK, "authenticated reads use the configured read allowance");
        }
        using (var limited = await owner.GetAsync("/app/v1/account"))
        {
            Check(limited.StatusCode == HttpStatusCode.TooManyRequests, "configured read limit is enforced for authenticated traffic");
        }
        using var verified = await owner.GetAsync("/app/v1/session");
        Check(verified.StatusCode == HttpStatusCode.OK, "authenticated session verification survives read throttling");
        var current = await verified.Content.ReadFromJsonAsync<JsonElement>();
        Check(current.GetProperty("account").GetProperty("id").GetString() == accountId,
            "throttling does not revoke the established account session");
    }
}
