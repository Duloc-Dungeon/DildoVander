using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Kufr.Backend.Features.Customers;
using Kufr.Backend.Features.GptLive;
using Kufr.Backend.Features.Identity;
using Kufr.Backend.Features.Wands;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

internal static class MobileWandTests
{
    internal static async Task Seed(CharacterProfileStore profiles, WandGenerationStore store, string owner, int tier = 1)
    {
        var profile = await profiles.ReadAsync(owner) ?? await profiles.SaveAsync(owner, new(0, new("Raven", "Original"), OpenAiConsent: true));
        var captured = await profiles.CaptureAsync(owner, profile.ProfileId, profile.Revision) ?? throw new InvalidOperationException();
        var operation = Guid.NewGuid().ToString("N");
        store.BeginMobile(owner, operation, captured, tier); store.Begin(owner, operation, captured, 63, true);
        var job = store.Claim(owner, operation, false)!;
        store.Complete(job, Candidates());
        store.Select(owner, operation, profile.ProfileId, profile.Revision, 1);
    }
    internal static WandCandidateContent[] Candidates() => Enumerable.Range(0, 6).Select(index =>
        new WandCandidateContent(new[] { "Jantarová ozvěna", "Stříbrná kometa", "Safírový šepot", "Měděný úsvit", "Dubový list", "Rubínový plamen" }[index],
            "Dub", "Fikční jiskra", "Zvídavá", "Fikční příběh hůlky.", "Digitální dubová hůlka", "Hvězdy", "Netrpělivost",
            new[] { "strike", "trick", "shield" }.Select(action => new WandSpell(action, $"{index} {action} ozvěna", "Herní účinek")).ToArray())).ToArray();

    internal static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "mobile-wands-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ApplicationIdentity:Enabled"] = "true", ["ApplicationIdentity:DataRoot"] = Path.Combine(root, "accounts"),
            ["ApplicationIdentity:DataProtectionRoot"] = Path.Combine(root, "keys"),
            ["ApplicationIdentity:RelyingPartyId"] = "localhost", ["ApplicationIdentity:AllowedOrigins:0"] = "https://localhost",
            ["Backend:DatabasePath"] = Path.Combine(root, "wands.sqlite"), ["OPENAI_API_KEY"] = "synthetic-not-a-provider-key"
        });
        builder.Services.AddAccountIdentity(builder.Configuration);
        builder.Services.AddSingleton<IMobileWandQuestionProvider, OpenAiMobileWandQuestionProvider>();
        var profiles = new CharacterProfileStore(Path.Combine(root, "profiles"));
        builder.Services.AddSingleton(profiles);
        await using var app = builder.Build();
        try
        {
            await AccountProvisioning.MigrateAsync(app.Services);
            _ = app.Services.GetRequiredService<IMobileWandQuestionProvider>();
            await using var scope = app.Services.CreateAsyncScope();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<AccountUser>>();
            var user = new AccountUser { Id = "alice", UserName = "alice", DisplayName = "Alice", OpenAiConsentAt = 1,
                OpenAiConsentVersion = AccountAiConsent.CurrentVersion };
            Check((await users.CreateAsync(user)).Succeeded, "create synthetic owner");
            var db = scope.ServiceProvider.GetRequiredService<AccountDatabase>();
            db.Sessions.Add(new AccountSession { Id = "cookie", UserId = user.Id, ExpiresAt = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds(), AuthenticatedAt = 1 });
            await db.SaveChangesAsync();
            var profile = await profiles.SaveAsync(user.Id, new(0, new("Raven", "Original"), OpenAiConsent: true));
            var captured = (await profiles.CaptureAsync(user.Id, profile.ProfileId, profile.Revision))!;
            var wands = new WandGenerationStore(builder.Configuration, TimeProvider.System);
            var secondStore = new WandGenerationStore(builder.Configuration, TimeProvider.System);
            await WorkshopStateMachineAsync(profiles, wands, builder.Configuration);
            await LegacyOperationRetirementAsync(profiles, wands);
            var lease = wands.ClaimMobileVoice("alice")!;
            Check(secondStore.ClaimMobileVoice("alice") is null, "voice ownership is exclusive across instances");
            secondStore.ReleaseMobileVoice("alice", "incorrect-lease");
            Check(wands.ClaimMobileVoice("alice") is null, "incorrect lease cannot release another conversation");
            wands.ReleaseMobileVoice("alice", lease);
            Check(secondStore.ClaimMobileVoice("bob") is { }, "different account remains independent");
            var unrelated = Guid.NewGuid().ToString("N");
            wands.Begin("alice", unrelated, captured, 63, true);
            try { wands.BeginMobile("alice", unrelated, captured, 2); throw new Exception("imported unrelated operation"); }
            catch (WandGenerationException e) when (e.Code == "wand_operation_conflict") { }
            var stale = Guid.NewGuid().ToString("N");
            wands.BeginMobile("revision-test", stale, captured, 1);
            var fresh = Guid.NewGuid().ToString("N");
            wands.BeginMobile("revision-test", fresh, captured with { Revision = 2 }, 1);
            Check(wands.MobileOperation("revision-test", captured.ProfileId, 2, 1) == fresh, "admin revision gets a fresh pending craft");
            var operation = Guid.NewGuid().ToString("N");
            wands.BeginMobile("alice", operation, captured, 1); wands.Begin("alice", operation, captured, 63, true);
            try { wands.BeginMobile("alice", Guid.NewGuid().ToString("N"), captured, 1); throw new Exception("duplicate pending craft"); }
            catch (WandGenerationException e) when (e.Code == "wand_operation_conflict") { }
            var generated = new WandGenerationService(wands, new Provider());
            using var upstream = new Socket(); using var phone = new Socket();
            using var live = new GptLiveClient(builder.Configuration, _ => Task.FromResult<WebSocket>(upstream));
            using var decisions = new WandDecisionService((input, _) =>
            {
                Check(input.GeneratedWands?.Count == 6 && input.Evidence.Count > 0, "decision uses generated candidates and actual input");
                var evidence = input.Evidence.Last();
                return Task.FromResult(new WandDecisionResult(WandDecisionStatus.ValidProposal, 2,
                    [new(evidence.Reference, evidence.Text, "Fikční zvídavost")], "", input.Revision, input.AvailabilityRevision));
            });
            var runtime = new MobileWandRuntime(app.Services.GetRequiredService<IServiceScopeFactory>(), wands, generated, live, decisions, new());
            var admission = new MobileWandAdmission("alice", operation, "cookie", user.SecurityStamp!, "https://localhost", DateTimeOffset.UtcNow.AddSeconds(30), captured);
            var ticket = runtime.Ticket(admission);
            Check(runtime.Take(ticket, operation, "https://localhost") == admission, "ticket binds account operation and origin");
            try { runtime.Take(ticket, operation, "https://localhost"); throw new Exception("replayed ticket"); } catch (WandGenerationException) { }
            Check(await runtime.Authorized(admission, default), "live account admission");
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var running = runtime.Run(admission, phone, deadline.Token);
            using var start = JsonDocument.Parse(await upstream.Command("session.start", deadline.Token));
            upstream.Emit(new { type = "session.started", client_event_id = start.RootElement.GetProperty("event_id").GetString(), session = new { id = "synthetic_live" } });
            await phone.Command("ready", deadline.Token);
            upstream.Emit(new { type = "session.output_transcript.delta", delta = "Co tě vede?", start_ms = 0, end_ms = 100, event_id = "q1" });
            upstream.Emit(new { type = "session.input_transcript.delta", delta = "Hledám hvězdy.", start_ms = 150, end_ms = 250, event_id = "a1" });
            upstream.Emit(new { type = "session.delegation.created", delegation = new { id = "choose", target = "client" } });
            using var selected = JsonDocument.Parse(await phone.Command("wand", deadline.Token));
            Check(selected.RootElement.GetProperty("wand").GetProperty("physicalSlot").GetInt32() == 2, "actual validated delegated choice reaches phone");
            var persisted = MobileWandSessions.Current(secondStore, "alice", profile.ProfileId, profile.Revision);
            Check(persisted?.PhysicalSlot == 2 && secondStore.MobileTier("alice", profile.ProfileId, profile.Revision) == 1, "mobile selection persists without physical outcome");
            Check(MobileWandSessions.Current(secondStore, "bob", profile.ProfileId, profile.Revision) is null, "another account never acquires this wand");
            phone.Emit(new { type = "cancel" }); await phone.Command("interrupted", deadline.Token);
            upstream.Emit(new {type="session.output_transcript.delta",delta="Late discarded speech",start_ms=300,end_ms=400,event_id="stale-output"});
            upstream.Emit(new {type="session.output_audio.delta",delta=Convert.ToBase64String(new byte[]{0,32,0,32})});
            upstream.Emit(new {type="session.input_transcript.delta",delta="Nová odpověď.",start_ms=700,end_ms=800,event_id="fresh-answer"});
            using var afterInterrupt = JsonDocument.Parse(await phone.Command("transcript", deadline.Token));
            Check(afterInterrupt.RootElement.GetProperty("role").GetString()=="user" && phone.BinaryWrites==0,
                "interruption cannot be cleared by late current-turn text or audio before fresh input");
            phone.Emit(new { type = "end" }); await running;
            Check(wands.ClaimMobileVoice("alice") is { }, "ending voice releases durable ownership");
            await StopDuringDecision(app.Services, builder.Configuration, profiles, wands, users, db);
            db.Sessions.Remove(await db.Sessions.SingleAsync(s => s.Id == "cookie")); await db.SaveChangesAsync();
            Check(!await runtime.Authorized(admission, default), "revoked cookie immediately loses voice authority");
            wands.DeleteProfile("alice", profile.ProfileId);
            Check(MobileWandSessions.Current(wands, "alice", profile.ProfileId, profile.Revision) is null, "deleted profile cannot retain a playable wand");
            Console.WriteLine("Mobile wand fake voice, delegated choice, persistence, ticket, revocation and concurrency passed.");
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    private static async Task WorkshopStateMachineAsync(CharacterProfileStore profiles, WandGenerationStore wands,
        IConfiguration configuration)
    {
        const string owner = "workshop-owner";
        var profile = await profiles.SaveAsync(owner, new(0, new("Havran", "Fikční svět", "verze A", "Loví ztracené mapy"), OpenAiConsent: true));
        var captured = await profiles.CaptureAsync(owner, profile.ProfileId, profile.Revision)
            ?? throw new InvalidOperationException("Workshop profile snapshot missing.");
        var operation = Guid.NewGuid().ToString("N");
        wands.BeginMobile(owner, operation, captured, 1);
        wands.BeginWorkshop(owner, operation, captured, 1, "test-group", 1);

        var state = wands.ReadWorkshopState(owner, operation);
        Check(state.Status == "authoring" && state.Question is null && state.Steps.Length == 3,
            "workshop starts with three stable steps and waits for an approved first question");
        var firstJob = wands.ClaimWorkshopQuestion()!;
        Check(firstJob.Step.Number == 1 && firstJob.Steps.Length == 3,
            "question authoring claims only the first persisted diagnostic step");
        using (var database = new SqliteConnection("Data Source=" + configuration["Backend:DatabasePath"]))
        {
            database.Open();
            using var expire = database.CreateCommand();
            expire.CommandText = "UPDATE mobile_wand_workshops SET question_lease_until=0 WHERE account_id=$a AND operation_id=$o";
            expire.Parameters.AddWithValue("$a", owner); expire.Parameters.AddWithValue("$o", operation); expire.ExecuteNonQuery();
        }
        Check(wands.ClaimWorkshopQuestion() is null, "expired uncertain authoring lease is never automatically redispatched");
        state = wands.ReadWorkshopState(owner, operation);
        Check(state.CanRetry && state.RecoveryKind == "question_fallback" && state.Steps.All(step => step.Answer is null),
            "expired question has a no-paid-call fallback recovery and keeps its step count");
        Check(wands.RecoverWorkshopQuestionFallback(owner, operation), "same durable question is recovered with disclosed fallback");
        var lateReplyRejected = false;
        try { wands.CompleteWorkshopQuestion(firstJob, "Pozdní odpověď?", [], false); }
        catch (WandGenerationException) { lateReplyRejected = true; }
        Check(lateReplyRejected, "late author response cannot replace recovered approved fallback");
        var uncertainOperation = Guid.NewGuid().ToString("N");
        wands.BeginMobile(owner, uncertainOperation, captured, 2);
        wands.BeginWorkshop(owner, uncertainOperation, captured, 2, "test-group", 1);
        var uncertainJob = wands.ClaimWorkshopQuestion()!;
        wands.FailWorkshopQuestion(uncertainJob, "question_provider_uncertain");
        var uncertainState = wands.ReadWorkshopState(owner, uncertainOperation);
        Check(uncertainState.CanRetry && uncertainState.RecoveryKind == "question_fallback",
            "actual provider transport uncertainty enables only same-step fallback recovery");
        Check(wands.RecoverWorkshopQuestionFallback(owner, uncertainOperation),
            "provider transport uncertainty recovers without redispatching paid authoring");
        var uncertainLateReplyRejected = false;
        try { wands.CompleteWorkshopQuestion(uncertainJob, "Přepsaná otázka?", [], false); }
        catch (WandGenerationException) { uncertainLateReplyRejected = true; }
        Check(uncertainLateReplyRejected, "late provider response after uncertain recovery is fenced");
        var first = wands.ReadWorkshopState(owner, operation).Question!;
        Check(first is { Number: 1, Total: 3, UsedFallback: true }, "recovered fallback is the only active first question");
        var steps = wands.CloseWorkshopQuestion(owner, operation, first.QuestionId, first.QuestionRevision,
            "Nejprve požádám o pomoc.", "text");
        Check(wands.ReadWorkshopState(owner, operation).Status == "authoring" && steps[1].Question.Length == 0,
            "accepted answer starts authoring of the next question without exposing unapproved text");
        var secondJob = wands.ClaimWorkshopQuestion()!;
        Check(secondJob.Step.Number == 2 && secondJob.Steps[0].Answer is not null,
            "second question author receives the first accepted answer");
        wands.CompleteWorkshopQuestion(secondJob, "Když zvolená pomoc selže, jakou jednu věc má hůlka udělat?", [], false);
        var firstAnswer = steps[0];
        wands.CorrectWorkshopAnswer(owner, operation, firstAnswer.AnswerId!, firstAnswer.AnswerRevision,
            "Nevím, požádám o pomoc.", "text");
        Check(wands.ReadWorkshopState(owner, operation).Steps[0].AnswerRevision == 2,
            "answer correction advances answer revision without creating a step");

        var second = wands.ReadWorkshopState(owner, operation).Question!;
        steps = wands.CloseWorkshopQuestion(owner, operation, second.QuestionId, second.QuestionRevision,
            "Ať mi pomůže nenápadně odejít.", "voice");
        Check(steps.Count(step => step.Answer is not null || step.Skipped) == 2
            && wands.Read(owner, operation) is null,
            "two closed questions do not create a generation session");

        var thirdJob = wands.ClaimWorkshopQuestion()!;
        Check(thirdJob.Step.Number == 3 && thirdJob.Steps.Count(step => step.Answer is not null) == 2,
            "third question author receives both accepted answers");
        wands.CompleteWorkshopQuestion(thirdJob, "Jaký drobný rozmar může mít hůlka, který ti občas zkomplikuje den?", [], false);
        var third = wands.ReadWorkshopState(owner, operation).Question!;
        Check(third.Number == 3 && third.Total == 3,
            "third approved question is the final diagnostic step");
        steps = wands.CloseWorkshopQuestion(owner, operation, third.QuestionId, third.QuestionRevision, null, null);
        state = wands.ReadWorkshopState(owner, operation);
        Check(steps.Count(step => step.Answer is not null || step.Skipped) == 3
            && state.Status == "generating" && state.Question is null && wands.Read(owner, operation) is null,
            "closing the third step queues workshop state without premature generation work");
        Check(steps.Select(step => step.QuestionId).Distinct(StringComparer.Ordinal).Count() == 3,
            "every question has a distinct persisted identifier");

        var generation = wands.Begin(owner, operation, captured, 1, openAiConsent: true);
        var duplicate = wands.Begin(owner, operation, captured, 1, openAiConsent: true);
        Check(generation.SessionId == duplicate.SessionId && generation.ReadyMask == 1,
            "mobile generation uses one idempotent candidate while physical six-slot validation remains separate");
        var accepted = false;
        try
        {
            wands.CloseWorkshopQuestion(owner, operation, Guid.NewGuid().ToString("N"), 1,
                "čtvrtá odpověď", "text");
            accepted = true;
        }
        catch (WandGenerationException) { }
        Check(!accepted, "a fourth diagnostic question cannot be closed after the third step");
        wands.DeleteProfile(owner, profile.ProfileId);
        Check(wands.FindWorkshop(owner, profile.ProfileId, profile.Revision, 1) is null,
            "profile revocation erases the workshop snapshot, issued questions and answers");

        const string erasedOwner = "workshop-account-erasure";
        var erasedProfile = await profiles.SaveAsync(erasedOwner,
            new(0, new("Raven", "Fictional world"), OpenAiConsent: true));
        var erasedCapture = await profiles.CaptureAsync(erasedOwner, erasedProfile.ProfileId, erasedProfile.Revision)!;
        var erasedOperation = Guid.NewGuid().ToString("N");
        wands.BeginMobile(erasedOwner, erasedOperation, erasedCapture!, 1);
        wands.BeginWorkshop(erasedOwner, erasedOperation, erasedCapture!, 1, "test-group", 1);
        wands.DeleteAccount(erasedOwner);
        Check(wands.FindWorkshop(erasedOwner, erasedProfile.ProfileId, erasedProfile.Revision, 1) is null,
            "account erasure deletes all mobile workshop snapshots");

        const string interruptedOwner = "workshop-start-crash";
        var interruptedProfile = await profiles.SaveAsync(interruptedOwner,
            new(0, new("Raven", "Fictional world"), OpenAiConsent: true));
        var interruptedCapture = await profiles.CaptureAsync(interruptedOwner, interruptedProfile.ProfileId,
            interruptedProfile.Revision) ?? throw new InvalidOperationException();
        var interruptedOperation = Guid.NewGuid().ToString("N");
        wands.BeginMobile(interruptedOwner, interruptedOperation, interruptedCapture, 1);
        Check(wands.MobileOperation(interruptedOwner, interruptedProfile.ProfileId,
            interruptedProfile.Revision, 1) == interruptedOperation && wands.Read(interruptedOwner, interruptedOperation) is null,
            "mobile reservation can survive a crash before any generation session exists");
        wands.BeginWorkshop(interruptedOwner, interruptedOperation, interruptedCapture, 1, "test-group", 1);
        Check(wands.ReadWorkshopState(interruptedOwner, interruptedOperation) is { Status: "authoring", Steps.Length: 3 },
            "explicit restart repairs the reservation crash window with three stable diagnostic steps");
        wands.DeleteProfile(interruptedOwner, interruptedProfile.ProfileId);

        for (var index = 0; index < 9; index++)
            await SeedGeneratingWorkshopAsync(profiles, wands, "queue-owner-" + index);
        var firstPage = wands.PendingWorkshops();
        Check(firstPage.Length == 8, "generation worker queue batch is bounded");
        foreach (var job in firstPage) wands.DeferWorkshop(job.AccountId, job.OperationId);
        var nextPage = wands.PendingWorkshops();
        Check(nextPage.Any(job => job.AccountId == "queue-owner-8"),
            "expired-session jobs rotate behind later eligible work instead of starving it (" +
            string.Join(',', nextPage.Select(job => job.AccountId)) + ")");
    }

    private static async Task SeedGeneratingWorkshopAsync(CharacterProfileStore profiles,
        WandGenerationStore wands, string owner)
    {
        var profile = await profiles.SaveAsync(owner, new(0, new(owner, "Fictional world"), OpenAiConsent: true));
        var captured = await profiles.CaptureAsync(owner, profile.ProfileId, profile.Revision)
            ?? throw new InvalidOperationException();
        var operation = Guid.NewGuid().ToString("N");
        wands.BeginMobile(owner, operation, captured, 1);
        wands.BeginWorkshop(owner, operation, captured, 1, "test-group", 1);
        for (var number = 1; number <= 3; number++)
        {
            var authoring = wands.ClaimWorkshopQuestion()!;
            wands.CompleteWorkshopQuestion(authoring,
                $"Když se změní okolnosti, co uděláš v kroku {number}?", [], false);
            var question = wands.ReadWorkshopState(owner, operation).Question!;
            wands.CloseWorkshopQuestion(owner, operation, question.QuestionId,
                question.QuestionRevision, null, null);
        }
    }

    private static async Task LegacyOperationRetirementAsync(CharacterProfileStore profiles,
        WandGenerationStore wands)
    {
        const string selectedOwner = "legacy-selected-owner";
        var selectedProfile = await profiles.SaveAsync(selectedOwner,
            new(0, new("Raven", "Fictional world"), OpenAiConsent: true));
        var selectedSnapshot = await profiles.CaptureAsync(selectedOwner, selectedProfile.ProfileId,
            selectedProfile.Revision) ?? throw new InvalidOperationException();
        var selectedOperation = Guid.NewGuid().ToString("N");
        wands.BeginMobile(selectedOwner, selectedOperation, selectedSnapshot, 1);
        wands.Begin(selectedOwner, selectedOperation, selectedSnapshot, 1, true);
        var selectedJob = wands.Claim(selectedOwner, selectedOperation, false)!;
        wands.Complete(selectedJob, Candidates().Take(1).ToArray());
        var savedWand = wands.Select(selectedOwner, selectedOperation, selectedProfile.ProfileId, selectedProfile.Revision, 1, 1);
        var verboseWand = savedWand with { DesignDescription = new string('ž', 700),
            Temperament = new string('č', 300), Core = new string('ř', 160), Material = new string('ů', 160) };
        var artworkSnapshot = MobileWandWorkshopWorker.ArtworkSnapshot(verboseWand);
        Check(artworkSnapshot.CatalogFacts.Length <= 4000
            && artworkSnapshot.GeneratedWand?.DesignDescription == verboseWand.DesignDescription
            && artworkSnapshot.GeneratedWand.Core == verboseWand.Core,
            "maximum Czech wand fiction reaches artwork intact without exceeding catalog metadata limits");
        var refused = false;
        try { wands.RetireMobileOperation(selectedOwner, selectedOperation); }
        catch (WandGenerationException error) when (error.Code == "wand_already_created") { refused = true; }
        Check(refused && MobileWandSessions.Current(wands, selectedOwner,
            selectedProfile.ProfileId, selectedProfile.Revision) is not null
            && wands.MobileOperation(selectedOwner, selectedProfile.ProfileId, selectedProfile.Revision, 1) == selectedOperation,
            "legacy selected first wand is never erased by rejected workshop start");

        const string draftOwner = "legacy-unselected-owner";
        var draftProfile = await profiles.SaveAsync(draftOwner,
            new(0, new("Raven", "Fictional world"), OpenAiConsent: true));
        var draftSnapshot = await profiles.CaptureAsync(draftOwner, draftProfile.ProfileId,
            draftProfile.Revision) ?? throw new InvalidOperationException();
        var draftOperation = Guid.NewGuid().ToString("N");
        wands.BeginMobile(draftOwner, draftOperation, draftSnapshot, 1);
        wands.Begin(draftOwner, draftOperation, draftSnapshot, 63, true);
        wands.RetireMobileOperation(draftOwner, draftOperation);
        Check(wands.MobileOperation(draftOwner, draftProfile.ProfileId, draftProfile.Revision, 1) is null,
            "legacy unselected six-candidate operation is retired before an exactly-three-answer craft");
    }
    private static async Task StopDuringDecision(IServiceProvider services, IConfiguration config, CharacterProfileStore profiles,
        WandGenerationStore wands, UserManager<AccountUser> users, AccountDatabase db)
    {
        var user = new AccountUser {Id="stop-owner",UserName="stop-owner",DisplayName="Stop",OpenAiConsentAt=1,OpenAiConsentVersion=AccountAiConsent.CurrentVersion};
        Check((await users.CreateAsync(user)).Succeeded, "create stop fixture owner");
        db.Sessions.Add(new AccountSession {Id="stop-cookie",UserId=user.Id,ExpiresAt=DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds(),AuthenticatedAt=1});
        await db.SaveChangesAsync();
        var profile = await profiles.SaveAsync(user.Id,new(0,new("Stop", "Original"),OpenAiConsent:true));
        var captured = (await profiles.CaptureAsync(user.Id,profile.ProfileId,profile.Revision))!;
        var operation=Guid.NewGuid().ToString("N");
        wands.BeginMobile(user.Id,operation,captured,1); wands.Begin(user.Id,operation,captured,63,true);
        using var upstream = new Socket(); using var phone = new Socket();
        using var live = new GptLiveClient(config,_=>Task.FromResult<WebSocket>(upstream));
        var started = new TaskCompletionSource<WandDecisionInput>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<WandDecisionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var decisions = new WandDecisionService((input,_)=> { started.TrySetResult(input); return release.Task; });
        var runtime = new MobileWandRuntime(services.GetRequiredService<IServiceScopeFactory>(),wands,new(wands,new Provider()),live,decisions,new());
        var admission = new MobileWandAdmission(user.Id,operation,"stop-cookie",user.SecurityStamp!,"https://localhost",DateTimeOffset.UtcNow.AddSeconds(30),captured);
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var run=runtime.Run(admission,phone,deadline.Token);
        using var start=JsonDocument.Parse(await upstream.Command("session.start",deadline.Token));
        upstream.Emit(new {type="session.started",client_event_id=start.RootElement.GetProperty("event_id").GetString()});
        await phone.Command("ready",deadline.Token);
        upstream.Emit(new {type="session.output_transcript.delta",delta="Co hledáš?",start_ms=0,end_ms=100,event_id="q"});
        upstream.Emit(new {type="session.input_transcript.delta",delta="Hvězdy.",start_ms=150,end_ms=250,event_id="a"});
        upstream.Emit(new {type="session.delegation.created",delegation=new {id="slow",target="client"}});
        var input=await started.Task.WaitAsync(deadline.Token);
        phone.Emit(new {type="end"}); await upstream.Command("session.close",deadline.Token);
        var fact=input.Evidence.Last();
        release.SetResult(new(WandDecisionStatus.ValidProposal,1,[new(fact.Reference,fact.Text,"Zvídavost")],"",input.Revision,input.AvailabilityRevision));
        await run;
        Check(MobileWandSessions.Current(wands,user.Id,profile.ProfileId,profile.Revision) is null,"late decision cannot create a wand after acknowledged end");
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    internal sealed class Provider : IWandGenerationProvider
    { public Task<WandCandidateContent[]> GenerateAsync(WandGenerationJob job, CancellationToken token) => Task.FromResult(Candidates()); }
    private sealed class Socket : WebSocket
    {
        internal int BinaryWrites;
        private readonly Channel<byte[]> input = Channel.CreateUnbounded<byte[]>();
        private readonly Channel<string> output = Channel.CreateUnbounded<string>();
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => WebSocketState.Open;
        public override string? SubProtocol => null;
        internal void Emit(object value) => input.Writer.TryWrite(JsonSerializer.SerializeToUtf8Bytes(value));
        internal async Task<string> Command(string type, CancellationToken token)
        { while (true) { var text = await output.Reader.ReadAsync(token); using var doc = JsonDocument.Parse(text); if (doc.RootElement.GetProperty("type").GetString() == type) return text; } }
        public override void Abort() { }
        public override void Dispose() { }
        public override Task CloseAsync(WebSocketCloseStatus status, string? description, CancellationToken token) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus status, string? description, CancellationToken token) => Task.CompletedTask;
        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType type, bool end, CancellationToken token)
        {
            if (type == WebSocketMessageType.Text)
            {
                var text = Encoding.UTF8.GetString(buffer); output.Writer.TryWrite(text);
                using var doc = JsonDocument.Parse(text);
                if (doc.RootElement.GetProperty("type").GetString() == "session.close") Emit(new { type = "session.closed", usage = new { seconds = 1 } });
            }
            else Interlocked.Increment(ref BinaryWrites);
            return Task.CompletedTask;
        }
        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken token)
        { var next = await input.Reader.ReadAsync(token); next.CopyTo(buffer.AsSpan()); return new(next.Length, WebSocketMessageType.Text, true); }
    }
}
