using System.Net;
using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using Kufr.Backend.Features.GptLive;
using Kufr.Backend.Features.Identity;
using Kufr.Backend.Features.PersonaLab;
using Kufr.Backend.Features.Realtime;
using Kufr.Backend.Features.TestingSessions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;

internal static partial class AccountIdentityTests
{
    internal static async Task RunTestingSessionHttpAsync()
    {
        var warnings = new IdentityWarningCapture();
        var providerFailure = await BoundedSdkTransportTests.FailureAsync(503);
        await using var fixture = await Fixture.StartAsync(configureEndpoints: app =>
        {
            app.MapPersonaLab(); app.MapTestingSessions();
        }, configureServices: services =>
        {
            // Offline wand candidates let the request reach the failing decision
            // double; no provider key or physical grant is used.
            services.AddSingleton(new GptLiveOptions());
            services.AddSingleton<GptLiveClient>(); services.AddSingleton<OpenAiRealtimeClient>();
            services.AddPersonaLab(); services.AddTestingSessions();
            services.AddSingleton<TestingResources>(provider => new TestingResources(
                provider.GetRequiredService<IConfiguration>(), provider.GetRequiredService<TimeProvider>(),
                new TestingSessionsTests.WandDouble(), null, null,
                new WandDecisionService((_, _) => Task.FromException<WandDecisionResult>(providerFailure))));
            services.AddSingleton<ILoggerProvider>(warnings);
        });
        _ = fixture.App.Services.GetRequiredService<TestingSessionService>();
        using var anonymous = fixture.Client();
        Check((await anonymous.GetAsync("/app/v1/testing-sessions")).StatusCode == HttpStatusCode.Unauthorized,
            "anonymous testing-session reads require normal authentication");
        using var owner = fixture.Client();
        using var ownerKey = new VirtualPasskey();
        await EnrollAsync(owner, ownerKey, Fixture.Bootstrap, "Owner"); await CsrfAsync(owner);
        using var tester = fixture.Client();
        using var testerKey = new VirtualPasskey();
        await InviteAndEnroll(tester, testerKey, AccountRoles.Tester);
        var request = new TestingSessionRequest(OperationId: Guid.NewGuid().ToString("D"), OpenAiConsent: true, RecordContent: true);
        var csrf = tester.DefaultRequestHeaders.GetValues("X-DV-CSRF").Single();
        tester.DefaultRequestHeaders.Remove("X-DV-CSRF");
        Check((await tester.PostAsJsonAsync("/app/v1/testing-sessions", request)).StatusCode == HttpStatusCode.BadRequest,
            "real antiforgery rejects test-session creation without CSRF");
        tester.DefaultRequestHeaders.Add("X-DV-CSRF", csrf);
        var created = await JsonAsync(await tester.PostAsJsonAsync("/app/v1/testing-sessions", request));
        var id = created.GetProperty("id").GetString()!;
        var labId = created.GetProperty("labSessionId").GetString()!;
        var virtualId = created.GetProperty("virtualDeviceId").GetString()!;
        Check(created.GetProperty("inputMode").GetString() == "text" && created.GetProperty("engine").GetString() == "gpt-live-1"
            && virtualId == "test-" + id, "one-click creation uses default real engine and explicit virtual identity");
        var duplicate = await JsonAsync(await tester.PostAsJsonAsync("/app/v1/testing-sessions", request));
        Check(duplicate.GetProperty("id").GetString() == id, "uncertain create retry is idempotent");
        var detail = await JsonAsync(await tester.GetAsync("/app/v1/testing-sessions/" + id));
        var hardware = detail.GetProperty("hardware");
        Check(hardware.GetProperty("connected").GetBoolean() && hardware.GetProperty("ready").GetBoolean()
            && hardware.GetProperty("compartments").GetArrayLength() == 6,
            "backend automatically prepares production native virtual hardware without BLE or manual setup");
        Check((await JsonAsync(await tester.GetAsync("/app/v1/testing-sessions"))).GetArrayLength() == 1,
            "the authenticated tester lists its durable test session");
        Check((await JsonAsync(await tester.GetAsync("/app/v1/persona-lab/sessions"))).GetArrayLength() == 0,
            "normal Lab list excludes testing-managed conversation lifecycle");
        Check((await tester.PostAsJsonAsync("/app/v1/persona-lab/sessions/" + labId + "/end", new {})).StatusCode == HttpStatusCode.Conflict,
            "raw Lab end cannot bypass test-session shutdown");
        Check((await tester.DeleteAsync("/app/v1/persona-lab/sessions/" + labId)).StatusCode == HttpStatusCode.Conflict,
            "raw Lab deletion cannot orphan test resources");
        var catalog = await JsonAsync(await tester.GetAsync("/app/v1/testing-sessions/catalog"));
        Check(!catalog.GetProperty("capabilities").GetProperty("providerConfigured").GetProperty("live").GetBoolean(),
            "backend truthfully reports absent provider configuration");
        Check((await tester.PostAsJsonAsync("/app/v1/persona-lab/sessions/" + labId + "/turns", new { text="No fake response" })).StatusCode == HttpStatusCode.ServiceUnavailable,
            "missing provider remains an actual API failure and cannot produce a fake successful reply");
        using var withoutConsent = await tester.PostAsJsonAsync("/app/v1/testing-sessions/characters", new
        {
            userInput = new { name="Clockmaker", world="Original fiction", description="Repairs clocks" },
            openAiConsent=false, longTermConsent=false, expectedRevision=0
        });
        Check(withoutConsent.StatusCode == HttpStatusCode.Conflict,
            "test character creation also requires explicit AI consent");
        var character = await JsonAsync(await tester.PostAsJsonAsync("/app/v1/testing-sessions/characters", new
        {
            userInput = new { name="Clockmaker", world="Original fiction", description="Repairs clocks" },
            openAiConsent=true, longTermConsent=false, expectedRevision=0
        }));
        var characterId=character.GetProperty("id").GetString()!;
        await JsonAsync(await tester.PostAsJsonAsync("/app/v1/testing-sessions/"+id+"/character",new { characterId,expectedRevision=created.GetProperty("revision").GetInt32() }));
        using (var rejectedDecision = await tester.PostAsJsonAsync("/app/v1/testing-sessions/" + id + "/select", new { }))
        {
            Check(rejectedDecision.StatusCode == HttpStatusCode.BadGateway,
                "an upstream SDK HTTP failure is a handled provider error, not an unhandled server failure");
            var problem = await rejectedDecision.Content.ReadFromJsonAsync<JsonElement>();
            Check(problem.GetProperty("code").GetString() == "openai_http_503"
                && !string.IsNullOrEmpty(problem.GetProperty("traceId").GetString()),
                "provider rejection keeps its safe code and request correlation at the testing HTTP boundary");
        }
        using var other = fixture.Client(); using var otherKey = new VirtualPasskey();
        await InviteAndEnroll(other,otherKey,AccountRoles.Tester);
        Check((await other.GetAsync("/app/v1/testing-sessions/"+id)).StatusCode==HttpStatusCode.NotFound,
            "another Tester cannot read the owned test session");
        Check((await other.PostAsJsonAsync("/app/v1/testing-sessions/"+id+"/end",new {})).StatusCode==HttpStatusCode.NotFound,
            "another Tester cannot terminate the owned test session");
        Check((await JsonAsync(await other.GetAsync("/app/v1/testing-sessions/characters"))).GetArrayLength()==0,
            "test characters remain isolated between accounts");
        using var customer=fixture.Client();using var customerKey=new VirtualPasskey();
        await InviteAndEnroll(customer,customerKey,AccountRoles.Customer);
        Check((await customer.GetAsync("/app/v1/testing-sessions")).StatusCode==HttpStatusCode.Forbidden,
            "Customer role does not acquire testing capabilities");
        Check((await owner.GetAsync("/app/v1/testing-sessions")).StatusCode==HttpStatusCode.Forbidden,
            "production Owner authority does not implicitly grant a testing capability");
        using var staff=fixture.Client();using var staffKey=new VirtualPasskey();
        await InviteAndEnroll(staff,staffKey,AccountRoles.Operator);
        Check((await staff.PostAsJsonAsync("/app/v1/testing-sessions",request)).StatusCode==HttpStatusCode.Forbidden,
            "production Operator role cannot create a test session without Tester capability");
        Check((await tester.GetAsync("/app/v1/admin/users")).StatusCode==HttpStatusCode.Forbidden,
            "Tester cannot inspect production account administration");
        Check((await tester.GetAsync("/v1/test-device")).StatusCode==HttpStatusCode.Unauthorized,
            "Tester account is not physical device authentication");
        using var virtualCredential=fixture.Client();
        virtualCredential.DefaultRequestHeaders.Authorization=new("Bearer",virtualId);
        Check((await virtualCredential.GetAsync("/v1/test-device")).StatusCode==HttpStatusCode.Unauthorized,
            "virtual suitcase identity is never a physical device credential");
        using var resumed=fixture.Client();await SignInAsync(resumed,testerKey);await CsrfAsync(resumed);
        var restored=await JsonAsync(await resumed.GetAsync("/app/v1/testing-sessions/"+id));
        Check(restored.GetProperty("session").GetProperty("id").GetString()==id
            && restored.GetProperty("character").GetProperty("id").GetString()==characterId,
            "normal passkey sign-in resumes stable session and character after client restart");
        var ended=await JsonAsync(await resumed.PostAsJsonAsync("/app/v1/testing-sessions/"+id+"/end",new {}));
        Check(ended.GetProperty("status").GetString()=="ended", "explicit end persists canonical lifecycle");
        Check((await resumed.DeleteAsync("/app/v1/testing-sessions/"+id)).StatusCode==HttpStatusCode.NoContent,
            "explicit authorized test deletion disposes test-owned resources");
        Check((await resumed.GetAsync("/app/v1/testing-sessions/"+id)).StatusCode==HttpStatusCode.NotFound,
            "deleted session cannot be resumed");
        Check((await JsonAsync(await owner.GetAsync("/app/v1/admin/users"))).GetProperty("users").GetArrayLength()==5,
            "test resource deletion preserves production account records");
        await VerifyPollingLimitsAsync(tester);
        Check(warnings.Events.IsEmpty, "session tokens and invitation listing do not emit cache-header or unordered-query warnings");
        Console.WriteLine("Testing-session HTTPS: real passkey invitations/authentication, CSRF, hardware-free start, isolated ownership, lifecycle, resume, deletion and production authorization passed (no provider calls).");

        async Task InviteAndEnroll(HttpClient client,VirtualPasskey key,string role)
        {
            await CsrfAsync(owner);
            var invitation=await JsonAsync(await owner.PostAsJsonAsync("/app/v1/admin/invitations",new {role,recoveryUserId=(string?)null}));
            await EnrollAsync(client,key,invitation.GetProperty("token").GetString()!,role);await CsrfAsync(client);
        }
    }

    private static async Task VerifyPollingLimitsAsync(HttpClient tester)
    {
        // This represents more than a minute of the original polling fan-out.
        // It must not spend the account allowance used by commands and sign-in.
        for (var index = 0; index < 125; index++)
        {
            using var response = await tester.GetAsync("/app/v1/testing-sessions/characters");
            Check(response.IsSuccessStatusCode, "routine polling has a separate bounded allowance");
        }
        using var saved = await tester.PostAsJsonAsync("/app/v1/testing-sessions/characters", new
        {
            userInput = new { name = "Clockmaker", world = "Original fiction" },
            openAiConsent = true, longTermConsent = false, expectedRevision = 0
        });
        Check(saved.IsSuccessStatusCode, "polling cannot reject an explicit character save");
        using var session = await tester.GetAsync("/app/v1/session");
        Check(session.IsSuccessStatusCode, "polling cannot exhaust the account session check");

        var limited = false;
        for (var index = 0; index < 240; index++)
        {
            using var response = await tester.GetAsync("/app/v1/testing-sessions/characters");
            if (response.StatusCode != HttpStatusCode.TooManyRequests)
            {
                Check(response.IsSuccessStatusCode, "polling remains healthy before its own limit");
                continue;
            }
            var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
            Check(problem.GetProperty("code").GetString() == "rate_limited"
                && !string.IsNullOrEmpty(problem.GetProperty("traceId").GetString()),
                "limiter rejection uses the diagnostic problem contract");
            Check(response.Headers.RetryAfter?.Delta is { TotalSeconds: >= 1 and <= 60 },
                "limiter rejection tells clients when reads may resume");
            limited = true;
            break;
        }
        Check(limited, "polling still has a finite request limit");
        using var command = await tester.PostAsJsonAsync("/app/v1/testing-sessions/missing/end", new { });
        Check(command.StatusCode == HttpStatusCode.NotFound, "even an exhausted polling bucket leaves commands available");
    }

    private sealed class IdentityWarningCapture : ILoggerProvider
    {
        internal ConcurrentQueue<int> Events { get; } = new();
        public ILogger CreateLogger(string categoryName) => new WarningLogger(categoryName, Events);
        public void Dispose() { }

        private sealed class WarningLogger(string category, ConcurrentQueue<int> events) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
                Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (IsEnabled(logLevel) && (category == "Microsoft.AspNetCore.Antiforgery.DefaultAntiforgery" && eventId.Id == 8
                    || category == "Microsoft.EntityFrameworkCore.Query" && eventId.Id == 10102))
                {
                    events.Enqueue(eventId.Id);
                }
            }
        }
    }
}
