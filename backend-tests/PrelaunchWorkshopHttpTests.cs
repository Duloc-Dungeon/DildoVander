using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Kufr.Backend.Features.Customers;
using Kufr.Backend.Features.Game;
using Kufr.Backend.Features.GptLive;
using Kufr.Backend.Features.Identity;
using Kufr.Backend.Features.Keepsakes;
using Kufr.Backend.Features.Launch;
using Kufr.Backend.Features.Wands;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

internal static partial class AccountIdentityTests
{
    internal static async Task RunPrelaunchWorkshopHttpAsync()
    {
        var generation = new PrelaunchWandProvider();
        await using var fixture = await Fixture.StartAsync(customerFeatures: true, betaRecruitmentOpen: false,
            configureServices: services =>
            {
                services.AddSingleton(sp => new GameStore(sp.GetRequiredService<AccountConfiguration>().DataRoot));
                services.AddSingleton<WandGenerationStore>();
                services.AddSingleton(sp => new Kufr.Backend.Features.Chronicle.ChronicleStore(
                    sp.GetRequiredService<AccountConfiguration>().DataRoot));
                services.AddSingleton<IWandGenerationProvider>(generation);
                services.AddSingleton<IMobileWandQuestionProvider, PrelaunchWorkshopDialogue>();
                services.AddSingleton<WandGenerationService>();
                services.AddSingleton<MobileWandRuntime>();
                services.AddSingleton<MobileWorkshopLiveRuntime>();
                services.AddSingleton<GptLiveClient>();
                services.AddSingleton(sp => GptLiveOptions.Read(sp.GetRequiredService<IConfiguration>()));
            },
            configureConfiguration: config => ((IConfigurationBuilder)config).AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Keepsake:ProviderMode"] = "openai", ["Keepsake:ImageModel"] = "gpt-image-1.5",
                    ["OPENAI_API_KEY"] = "synthetic-offline-workshop-key"
                }),
            configureEndpoints: app => app.MapMobileWands());
        Check(!fixture.App.Services.GetServices<IHostedService>()
            .Any(service => service is KeepsakeWorker or MobileWandWorkshopWorker),
            "workshop scenario exclusively controls its manufacture and artwork queues");
        using var owner = fixture.Client();
        using var guest = fixture.Client();
        using var movedGuest = fixture.Client();
        using var ownerKey = new VirtualPasskey();
        using var guestKey = new VirtualPasskey();
        using var movedKey = new VirtualPasskey();
        await EnrollAsync(owner, ownerKey, Fixture.Bootstrap, "Workshop owner");
        await CsrfAsync(owner);
        var launch = fixture.App.Services.GetRequiredService<LaunchPolicyStore>();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var launchConfiguration = launch.Read();
        launch.Update("test-owner", new(launchConfiguration.Revision, false, null, false,
            true, now + 86_400_000, false, launchConfiguration.TimeZone, launchConfiguration.Teaser,
            launchConfiguration.InitialLoadingMessage, launchConfiguration.PersonalizedLoadingEnabled,
            launchConfiguration.PersonalizedLoadingPrompt), now);
        var group = launch.CreateGroup("test-owner", new("Closed wand night", null), now);
        var otherGroup = launch.CreateGroup("test-owner", new("Other closed night", null), now);
        var guestId = await EnrollWorkshopGuestAsync(guest, guestKey, "Early wand guest");
        var movedId = await EnrollWorkshopGuestAsync(movedGuest, movedKey, "Moving wand guest");
        var profiles = fixture.App.Services.GetRequiredService<CharacterProfileStore>();
        var wands = fixture.App.Services.GetRequiredService<WandGenerationStore>();
        var prepared = await PrepareWorkshopCharacterAsync(guestId, new("Raven", "Original fictional world"));
        await PrepareWorkshopCharacterAsync(movedId, new("Rowan", "Original fictional world"));
        Check(!(await JsonAsync(await guest.GetAsync("/app/v1/launch"))).GetProperty("allowed").GetBoolean(),
            "first workshop starts with a genuinely closed group");
        Check((await guest.GetAsync("/app/v1/prelaunch/character")).IsSuccessStatusCode,
            "prelaunch character reveal remains readable during workshop preparation");
        var eligibility = await JsonAsync(await guest.GetAsync("/app/v1/customer/wand/workshop"));
        Check(eligibility.GetProperty("eligible").GetBoolean(), "first wand is eligible before the night opens");
        Check(!(await JsonAsync(await guest.GetAsync("/app/v1/customer/wand/workshop?upgrade=1")))
            .GetProperty("eligible").GetBoolean(), "upgrades stay unavailable before the night opens");
        var operation = Guid.NewGuid().ToString("N");
        var sessionPath = "/app/v1/customer/wand/workshop/sessions/" + operation;
        var started = await JsonAsync(await guest.PostAsJsonAsync("/app/v1/customer/wand/workshop/sessions",
            new { operationId = operation }));
        Check(started.GetProperty("session").GetProperty("status").GetString() == "asking",
            "closed-night guest can create the first workshop");
        var resumed = await JsonAsync(await guest.GetAsync("/app/v1/customer/wand/workshop/sessions/current"));
        Check(resumed.GetProperty("session").GetProperty("id").GetString() == operation,
            "closed-night guest resumes the same workshop");
        var admission = await WorkshopAdmissionAsync(guestId, operation, prepared);
        await fixture.App.Services.GetRequiredService<MobileWorkshopLiveRuntime>().RequireAuthorityAsync(admission, default);
        Check((await guest.PostAsJsonAsync(sessionPath + "/ticket", new { socketOrigin = "https://localhost" })).IsSuccessStatusCode,
            "closed-night guest can obtain the same protected Live admission as text");
        var messageId = Guid.NewGuid().ToString("N");
        const string messageText = "I find a new route before asking for directions.";
        Check((await guest.PostAsJsonAsync(sessionPath + "/messages",
            new { messageId, text = messageText })).IsSuccessStatusCode,
            "closed-night text dialogue persists owner evidence");
        Check((await guest.PostAsJsonAsync(sessionPath + "/messages", new { messageId, text = messageText })).IsSuccessStatusCode,
            "an uncertain message retry returns the accepted turn without another provider call");
        Check((await guest.PostAsJsonAsync(sessionPath + "/messages", new { messageId, text = "A different answer." })).StatusCode == HttpStatusCode.Conflict,
            "an edited payload cannot silently reuse the accepted message ID");
        Check(wands.ReadWorkshopMessages(guestId, operation).Count(message => message.Role == "user") == 1,
            "mismatched duplicate message never appends or replaces user evidence");
        Check((await guest.PostAsJsonAsync(sessionPath + "/finish", new { })).IsSuccessStatusCode,
            "closed-night conversation can finish");
        await RunWorkshopQueueAsync(() =>
        {
            var state = wands.ReadWorkshopState(guestId, operation);
            // The public status projects selection as complete before artwork
            // issuance finishes. Keep the worker alive until its durable handoff.
            return state.Status == "complete"
                && (state.KeepsakeId is { Length: > 0 } || state.Artwork?.ErrorCode is { Length: > 0 });
        });
        var manufactured = wands.ReadWorkshopState(guestId, operation);
        Check(manufactured.KeepsakeId is { Length: > 0 },
            $"workshop durably issues its artwork archive before queue handoff (error={manufactured.Artwork?.ErrorCode ?? "none"})");
        var completed = (await JsonAsync(await guest.GetAsync(sessionPath))).GetProperty("session");
        var wandId = completed.GetProperty("wand").GetProperty("wandId").GetString();
        Check(generation.Calls == 1 && !string.IsNullOrWhiteSpace(wandId),
            "worker manufactures and saves the first wand while its group stays closed");
        var keepsakes = fixture.App.Services.GetRequiredService<KeepsakeStore>();
        var artworkJob = keepsakes.LeaseNext() ?? throw new InvalidOperationException("No first-wand artwork job.");
        _ = keepsakes.Dispatch(artworkJob)!;
        keepsakes.MarkAmbiguous(artworkJob, "generation_provider_unavailable");
        using (var retry = new HttpRequestMessage(HttpMethod.Post, sessionPath + "/artwork/retry") { Content = JsonContent.Create(new { }) })
        {
            retry.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("D"));
            Check((await guest.SendAsync(retry)).IsSuccessStatusCode,
                "closed-night owner can explicitly retry their failed illustration");
        }
        var retriedJob = keepsakes.LeaseNext() ?? throw new InvalidOperationException("No retried artwork job.");
        Check(keepsakes.Complete(retriedJob, await new FakeKeepsakeProvider().GenerateAsync(keepsakes.Dispatch(retriedJob)!, default)),
            "offline illustration publishes for the saved prelaunch wand");
        completed = (await JsonAsync(await guest.GetAsync(sessionPath))).GetProperty("session");
        var artwork = completed.GetProperty("artwork");
        var assetId = artwork.GetProperty("assetId").GetString();
        var artworkPath = sessionPath + "/artwork/assets/" + assetId;
        var image = await guest.GetAsync(artworkPath);
        Check(image.IsSuccessStatusCode && (await image.Content.ReadAsByteArrayAsync()).Length > 0,
            "closed-night owner reads canonical artwork through the workshop route");
        Check((await movedGuest.GetAsync(artworkPath)).StatusCode == HttpStatusCode.NotFound,
            "another closed-night guest cannot read the owner's workshop artwork");
        var arbitraryAssetRead = await guest.GetAsync(sessionPath + "/artwork/assets/" + Guid.NewGuid().ToString("D"));
        Check(arbitraryAssetRead.StatusCode == HttpStatusCode.NotFound,
            $"workshop asset route cannot read arbitrary owner archive assets (received {arbitraryAssetRead.StatusCode})");
        var archiveRead = await guest.GetAsync($"/app/v1/archive/owned/{artwork.GetProperty("locator").GetString()}/assets/{assetId}");
        Check(archiveRead.StatusCode == HttpStatusCode.Locked,
            $"general archives remain locked before the night opens (received {archiveRead.StatusCode})");
        Check((await guest.GetAsync("/app/v1/game/progress")).StatusCode == HttpStatusCode.Locked,
            "crafting before launch does not unlock gameplay");
        Check((await JsonAsync(await guest.GetAsync("/app/v1/customer/wand"))).GetProperty("wand")
            .GetProperty("wandId").GetString() == wandId, "saved first wand is readable while waiting");

        var movedOperation = Guid.NewGuid().ToString("N");
        var movedPath = "/app/v1/customer/wand/workshop/sessions/" + movedOperation;
        Check((await movedGuest.PostAsJsonAsync("/app/v1/customer/wand/workshop/sessions",
            new { operationId = movedOperation })).IsSuccessStatusCode, "second guest has a prelaunch workshop");
        var movedProfile = await profiles.ReadAsync(movedId) ?? throw new InvalidOperationException();
        var movedSnapshot = await profiles.CapturePreparedAsync(movedId, movedProfile.ProfileId, movedProfile.Revision)
            ?? throw new InvalidOperationException();
        var movedAdmission = await WorkshopAdmissionAsync(movedId, movedOperation, movedSnapshot);
        var mobileRuntime = fixture.App.Services.GetRequiredService<MobileWandRuntime>();
        var oldTicket = mobileRuntime.Ticket(movedAdmission);
        movedAdmission = mobileRuntime.Take(oldTicket, movedOperation, movedAdmission.Origin);
        wands.AppendWorkshopMessage(movedId, movedOperation, Guid.NewGuid().ToString("N"), "user", "Use the map.", "text");
        Check((await movedGuest.PostAsJsonAsync(movedPath + "/finish", new { })).IsSuccessStatusCode,
            "second workshop is queued before membership moves");
        await ChangeWorkshopMemberAsync(movedId, user => { user.LaunchGroupId = otherGroup.Id; user.LaunchGroupAccessRevision++; });
        Check((await movedGuest.GetAsync(movedPath)).StatusCode == HttpStatusCode.NotFound,
            "group move hides an old workshop even when both groups are closed");
        Check(!await fixture.App.Services.GetRequiredService<MobileWandRuntime>().Authorized(movedAdmission, default),
            "group move revokes an issued Live ticket before connection");
        await RunWorkshopQueueAsync(() => wands.ReadWorkshopState(movedId, movedOperation).Status == "failed");
        Check(generation.Calls == 1 && wands.ReadWorkshopState(movedId, movedOperation).Wand is null,
            "worker rejects a moved member without manufacturing their queued wand");
        var recoverable = await JsonAsync(await movedGuest.GetAsync("/app/v1/customer/wand/workshop"));
        Check(recoverable.GetProperty("eligible").GetBoolean() && recoverable.GetProperty("session").ValueKind == JsonValueKind.Null,
            "valid new membership offers explicit recovery without exposing the fenced conversation");
        var recovered = (await JsonAsync(await movedGuest.PostAsJsonAsync("/app/v1/customer/wand/workshop/sessions",
            new { operationId = Guid.NewGuid().ToString("N") }))).GetProperty("session");
        Check(recovered.GetProperty("id").GetString() == movedOperation
            && recovered.GetProperty("messages").GetArrayLength() == 1
            && recovered.GetProperty("status").GetString() == "generating",
            "explicit authorized recovery retains the operation and conversation after a group move");
        Check((await movedGuest.PostAsJsonAsync(movedPath + "/messages",
            new { messageId = Guid.NewGuid().ToString("N"), text = "Change frozen evidence." })).StatusCode == HttpStatusCode.Conflict,
            "recovered accepted manufacture cannot return to editable conversation");
        Check(!await mobileRuntime.Authorized(movedAdmission, default),
            "membership recovery never revives a ticket issued under the old launch snapshot");
        await ChangeWorkshopMemberAsync(movedId, user => { user.LaunchGroupRevokedAt = AccountEndpoints.Now(); user.LaunchGroupAccessRevision++; });
        Check(!(await movedGuest.PostAsJsonAsync("/app/v1/customer/wand/workshop/sessions",
            new { operationId = Guid.NewGuid().ToString("N") })).IsSuccessStatusCode,
            "revoked membership cannot rebind or create a workshop");
        await ChangeWorkshopMemberAsync(movedId, user => { user.LaunchGroupRevokedAt = null; user.LaunchGroupAccessRevision++; });
        Check((await movedGuest.PostAsJsonAsync("/app/v1/customer/wand/workshop/sessions",
            new { operationId = Guid.NewGuid().ToString("N") })).IsSuccessStatusCode,
            "re-enabled current membership can recover the same workshop again");
        await RunWorkshopQueueAsync(() => wands.ReadWorkshopState(movedId, movedOperation).Status == "complete");
        Check(generation.Calls == 2 && wands.ReadWorkshopState(movedId, movedOperation).Wand is not null,
            "recovered unstarted manufacture runs once under the current membership");

        var portraitReplacement = profiles.ReplaceAdminPortrait(prepared.ProfileId, prepared.Revision,
            CharacterAdminEndpoints.NormalizePortrait(CharacterAdministrationTests.Png(), "image/png"));
        var afterPortrait = await JsonAsync(await guest.GetAsync("/app/v1/customer/wand/workshop"));
        Check(portraitReplacement.Revision == prepared.Revision + 1
            && afterPortrait.GetProperty("session").GetProperty("id").GetString() == operation
            && afterPortrait.GetProperty("session").GetProperty("wand").GetProperty("wandId").GetString() == wandId,
            "portrait-only revision returns the existing completed wand rather than offering blocked recreation");
        Check((await guest.GetAsync(sessionPath)).IsSuccessStatusCode && (await guest.GetAsync(artworkPath)).IsSuccessStatusCode
            && (await guest.GetAsync("/app/v1/customer/wand/workshop/sessions/current")).IsSuccessStatusCode,
            "portrait-only revision preserves protected completed-session and artwork reads");
        Check((await guest.PostAsJsonAsync(sessionPath + "/messages", new { messageId = Guid.NewGuid().ToString("N"), text = "Edit old work." })).StatusCode == HttpStatusCode.Conflict,
            "portrait-only completed projection does not authorize editing the older workshop revision");
        var savedReply = wands.ReadWorkshopMessages(guestId, operation).First(message => message.Role == "assistant");
        Check((await guest.PostAsJsonAsync(sessionPath + "/moments",
            new { messageId = savedReply.MessageId, expectedRevision = savedReply.Revision })).IsSuccessStatusCode,
            "portrait-only revision permits saving an exact reply from the completed historical workshop");

        var openingTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        launchConfiguration = launch.Read();
        launch.Update("test-owner", new(launchConfiguration.Revision, false, null, false,
            true, openingTime, true, launchConfiguration.TimeZone, launchConfiguration.Teaser,
            launchConfiguration.InitialLoadingMessage, launchConfiguration.PersonalizedLoadingEnabled,
            launchConfiguration.PersonalizedLoadingPrompt), openingTime);
        var opened = (await JsonAsync(await guest.GetAsync(sessionPath))).GetProperty("session");
        Check(opened.GetProperty("wand").GetProperty("wandId").GetString() == wandId
            && (await guest.GetAsync(artworkPath)).IsSuccessStatusCode,
            "opening the same night preserves the saved first wand and artwork without recreation");
        await profiles.SaveAsync(guestId, new(portraitReplacement.Revision, new("Raven", "A revised fictional world"),
            prepared.ProfileId, OpenAiConsent: true), canEditExisting: true);
        Check((await guest.GetAsync(artworkPath)).StatusCode == HttpStatusCode.Conflict
            && (await guest.PostAsJsonAsync(sessionPath + "/artwork/retry", new { })).StatusCode == HttpStatusCode.Conflict,
            "a changed character revision cannot read or regenerate artwork for the old workshop");
        Check((await guest.PostAsJsonAsync(sessionPath + "/moments",
            new { messageId = savedReply.MessageId, expectedRevision = savedReply.Revision })).StatusCode == HttpStatusCode.Conflict,
            "changed character input cannot save a reply from the old completed workshop");
        await ChangeWorkshopMemberAsync(guestId, user => { user.LaunchGroupRevokedAt = AccountEndpoints.Now(); user.LaunchGroupAccessRevision++; });
        Check((await guest.GetAsync(sessionPath)).StatusCode == HttpStatusCode.Forbidden
            && (await guest.GetAsync(artworkPath)).StatusCode == HttpStatusCode.Forbidden,
            "membership revocation blocks saved workshop and artwork reads");
        Check(!await fixture.App.Services.GetRequiredService<MobileWandRuntime>().Authorized(admission, default),
            "membership revocation blocks the original Live admission");
        Console.WriteLine("Prelaunch workshop HTTPS: create/resume, text/Live admission, worker finish, canonical artwork/retry, opening transition and membership revocation passed (offline providers).");

        async Task<string> EnrollWorkshopGuestAsync(HttpClient client, VirtualPasskey key, string name)
        {
            var invitation = (await JsonAsync(await owner.PostAsJsonAsync("/app/v1/admin/invitations",
                new { role = "Customer", launchGroupId = group.Id }))).GetProperty("token").GetString()!;
            await EnrollAsync(client, key, invitation, name);
            return (await CsrfAsync(client)).GetProperty("account").GetProperty("id").GetString()!;
        }

        async Task<MobileWandAdmission> WorkshopAdmissionAsync(string accountId, string id, CharacterConversationSnapshot snapshot)
        {
            await using var scope = fixture.App.Services.CreateAsyncScope();
            var accounts = scope.ServiceProvider.GetRequiredService<AccountDatabase>();
            var user = await accounts.Users.AsNoTracking().SingleAsync(user => user.Id == accountId);
            var session = await accounts.Sessions.AsNoTracking().SingleAsync(session => session.UserId == accountId);
            return new(accountId, id, session.Id, user.SecurityStamp!, "https://localhost", DateTimeOffset.UtcNow.AddMinutes(1), snapshot);
        }

        async Task<CharacterConversationSnapshot> PrepareWorkshopCharacterAsync(string accountId, CharacterUserInput input)
        {
            var profile = await profiles.SaveAsync(accountId, new(0, input, OpenAiConsent: true));
            var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
            while (DateTimeOffset.UtcNow < deadline)
            {
                var snapshot = await profiles.CapturePreparedAsync(accountId, profile.ProfileId, profile.Revision);
                if (snapshot is not null) { return snapshot; }
                await Task.Delay(25);
            }
            throw new InvalidOperationException("Offline character worker did not prepare the workshop profile.");
        }

        async Task ChangeWorkshopMemberAsync(string accountId, Action<AccountUser> change)
        {
            await using var scope = fixture.App.Services.CreateAsyncScope();
            var accounts = scope.ServiceProvider.GetRequiredService<AccountDatabase>();
            var user = await accounts.Users.SingleAsync(user => user.Id == accountId);
            change(user);
            await accounts.SaveChangesAsync();
        }

        async Task RunWorkshopQueueAsync(Func<bool> finished)
        {
            using var worker = new MobileWandWorkshopWorker(fixture.App.Services.GetRequiredService<IServiceScopeFactory>(),
                wands, fixture.App.Services.GetRequiredService<WandGenerationService>(), NullLogger<MobileWandWorkshopWorker>.Instance);
            await worker.StartAsync(default);
            try
            {
                var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
                while (!finished() && DateTimeOffset.UtcNow < deadline) { await Task.Delay(25); }
                Check(finished(), "workshop queue reaches the expected terminal state");
            }
            finally { await worker.StopAsync(default); }
        }
    }

    private sealed class PrelaunchWorkshopDialogue : IMobileWandQuestionProvider
    {
        public Task<MobileWandDialogueDraft> ReplyAsync(CharacterConversationSnapshot profile,
            MobileWandWorkshopMessage[] messages, CancellationToken token) =>
            Task.FromResult(new MobileWandDialogueDraft("A map is an excellent place to start. What happens next?", false));
    }

    private sealed class PrelaunchWandProvider : IWandGenerationProvider
    {
        internal int Calls { get; private set; }
        public Task<WandCandidateContent[]> GenerateAsync(WandGenerationJob job, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(MobileWandTests.Candidates().Take(1).ToArray());
        }
    }
}
