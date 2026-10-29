using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Kufr.Backend.Features.Customers;
using Kufr.Backend.Features.GptLive;
using Kufr.Backend.Features.Identity;
using Kufr.Backend.Features.Launch;
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
        var longTranscript = string.Concat(Enumerable.Repeat("Dlouhá odpověď s opravou a modrou nití. ", 160)) + "🧙";
        var chunks = MobileWorkshopLiveRuntime.TranscriptChunks(longTranscript).ToArray();
        Check(chunks.Length > 2 && chunks.All(chunk => chunk.Length <= 1800)
            && string.Concat(chunks) == longTranscript,
            "continuous rich Live speech is persisted in bounded lossless surrogate-safe chunks");
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
        builder.Services.AddLaunchManagement(builder.Configuration);
        builder.Services.AddSingleton<IMobileWandQuestionProvider, OpenAiMobileWandQuestionProvider>();
        var profiles = new CharacterProfileStore(Path.Combine(root, "profiles"));
        builder.Services.AddSingleton(profiles);
        await using var app = builder.Build();
        try
        {
            await AccountProvisioning.MigrateAsync(app.Services);
            await app.InitializeAccountsAsync();
            _ = app.Services.GetRequiredService<IMobileWandQuestionProvider>();
            await using var scope = app.Services.CreateAsyncScope();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<AccountUser>>();
            var launch = app.Services.GetRequiredService<LaunchPolicyStore>();
            var group = launch.CreateGroup("test", new("Closed workshop night"),
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            var user = new AccountUser { Id = "alice", UserName = "alice", DisplayName = "Alice", OpenAiConsentAt = 1,
                LaunchGroupId = group.Id, LaunchGroupAccessRevision = 1,
                OpenAiConsentVersion = AccountAiConsent.CurrentVersion };
            Check((await users.CreateAsync(user)).Succeeded, "create synthetic owner");
            Check((await users.AddToRoleAsync(user, AccountRoles.Customer)).Succeeded, "create customer workshop owner");
            var db = scope.ServiceProvider.GetRequiredService<AccountDatabase>();
            db.Sessions.Add(new AccountSession { Id = "cookie", UserId = user.Id, ExpiresAt = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds(), AuthenticatedAt = 1 });
            await db.SaveChangesAsync();
            var (profile, captured) = await PrepareProfileAsync(profiles, user.Id, new("Raven", "Original"));
            var wands = new WandGenerationStore(builder.Configuration, TimeProvider.System);
            var secondStore = new WandGenerationStore(builder.Configuration, TimeProvider.System);
            await WorkshopStateMachineAsync(profiles, wands, builder.Configuration);
            await WorkshopMembershipRecoveryAsync(Path.Combine(root, "membership-recovery"));
            await LegacyOperationRetirementAsync(profiles, wands);
            await InFlightManufactureCaptureAsync(profiles, Path.Combine(root, "inflight-capture"));
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
            wands.BeginWorkshop("alice", operation, captured, 1, group.Id, user.LaunchGroupAccessRevision);
            try { wands.BeginMobile("alice", Guid.NewGuid().ToString("N"), captured, 1); throw new Exception("duplicate pending craft"); }
            catch (WandGenerationException e) when (e.Code == "wand_operation_conflict") { }
            var runtime = new MobileWandRuntime(app.Services.GetRequiredService<IServiceScopeFactory>(), wands);
            var admission = new MobileWandAdmission("alice", operation, "cookie", user.SecurityStamp!, "https://localhost", DateTimeOffset.UtcNow.AddSeconds(30), captured);
            var ticket = runtime.Ticket(admission);
            Check(runtime.Take(ticket, operation, "https://localhost") == admission with
                { LaunchSnapshot = (group.Id, user.LaunchGroupAccessRevision) },
                "ticket binds account operation, origin and immutable membership snapshot");
            try { runtime.Take(ticket, operation, "https://localhost"); throw new Exception("replayed ticket"); } catch (WandGenerationException) { }
            Check(await runtime.Authorized(admission, default), "live account admission");
            var frozenAdmission = admission with { LaunchSnapshot = (group.Id, user.LaunchGroupAccessRevision) };
            user.LaunchGroupAccessRevision++;
            await db.SaveChangesAsync();
            wands.RecoverWorkshopMembership("alice", operation, frozenAdmission.LaunchSnapshot!.Value,
                group.Id, user.LaunchGroupAccessRevision);
            var frozenTicket = runtime.Ticket(frozenAdmission);
            var recoveredAdmission = runtime.Take(frozenTicket, operation, "https://localhost");
            Check(recoveredAdmission.LaunchSnapshot == frozenAdmission.LaunchSnapshot
                && !await runtime.Authorized(recoveredAdmission, default),
                "ticket preserves the captured membership revision after recovery instead of silently granting the new binding");
            db.Sessions.Remove(await db.Sessions.SingleAsync(s => s.Id == "cookie")); await db.SaveChangesAsync();
            Check(!await runtime.Authorized(admission, default), "revoked cookie immediately loses voice authority");
            wands.DeleteProfile("alice", profile.ProfileId);
            Check(MobileWandSessions.Current(wands, "alice", profile.ProfileId, profile.Revision) is null, "deleted profile cannot retain a playable wand");
            Console.WriteLine("Mobile workshop admission ticket replay, session revocation and account erasure passed.");
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    private static async Task WorkshopMembershipRecoveryAsync(string root)
    {
        // Recovery leaves intentionally pending jobs; keep them out of the bounded shared queue fixtures.
        Directory.CreateDirectory(root);
        var profiles = new CharacterProfileStore(Path.Combine(root, "profiles"));
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Backend:DatabasePath"] = Path.Combine(root, "wands.sqlite")
        }).Build();
        var wands = new WandGenerationStore(configuration, TimeProvider.System);
        const string owner = "membership-recovery";
        var (profile, snapshot) = await PrepareProfileAsync(profiles, owner, new("Raven", "Original fiction"));
        var operation = Guid.NewGuid().ToString("N");
        wands.BeginMobile(owner, operation, snapshot, 1);
        wands.BeginWorkshop(owner, operation, snapshot, 1, "old-group", 1);
        wands.AppendWorkshopMessage(owner, operation, Guid.NewGuid().ToString("N"), "user", "Follow the map.", "text");
        wands.FinishWorkshopConversation(owner, operation, profile.ProfileId, profile.Revision);
        wands.Begin(owner, operation, snapshot, 1, true);
        var oldJob = wands.Claim(owner, operation, false, ("old-group", 1))!;
        wands.RecoverWorkshopMembership(owner, operation, ("old-group", 1), "new-group", 2);
        Check(!wands.CanDispatch(oldJob), "membership recovery invalidates the in-flight provider lease");
        var staleCompletionRejected = false;
        try { wands.Complete(oldJob, Candidates().Take(1).ToArray()); }
        catch (WandGenerationException error) when (error.Code == "wand_generation_stale") { staleCompletionRejected = true; }
        Check(staleCompletionRejected, "a provider response from the old membership cannot publish candidates");
        Check(wands.Read(owner, operation)?.Status == "unavailable"
            && wands.ReadWorkshopState(owner, operation).Status == "failed"
            && wands.ReadWorkshopState(owner, operation).CanRetry,
            "uncertain running manufacture is not silently repeated and requires explicit retry");
        wands.ResumeWorkshop(owner, operation);
        wands.FailWorkshop(owner, operation, "late_revocation", ("old-group", 1));
        // Public status still projects the interrupted generation as failed until its retry is claimed.
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = configuration["Backend:DatabasePath"]! }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM mobile_wand_workshops WHERE account_id=$owner AND operation_id=$operation AND status='generating' AND retry_requested=1";
            command.Parameters.AddWithValue("$owner", owner);
            command.Parameters.AddWithValue("$operation", operation);
            Check(Convert.ToInt32(command.ExecuteScalar()) == 1, "an old worker cannot fail a recovered workshop");
        }
        var staleClaimRejected = false;
        try { wands.Claim(owner, operation, true, ("old-group", 1)); }
        catch (WandGenerationException error) when (error.Code == "wand_session_unavailable") { staleClaimRejected = true; }
        Check(staleClaimRejected, "old membership cannot claim a recovered generation lease");
        var currentJob = wands.Claim(owner, operation, true, ("new-group", 2))!;
        wands.Complete(currentJob, Candidates().Take(1).ToArray());
        var staleSelectionRejected = false;
        try { wands.Select(owner, operation, profile.ProfileId, profile.Revision, 1, 1, ("old-group", 1)); }
        catch (WandGenerationException error) when (error.Code == "wand_session_unavailable") { staleSelectionRejected = true; }
        Check(staleSelectionRejected, "old membership cannot select a recovered provider result");
        var selected = wands.Select(owner, operation, profile.ProfileId, profile.Revision, 1, 1, ("new-group", 2));
        wands.CompleteWorkshopWithoutArtwork(owner, operation, "fixture", ("new-group", 2));
        wands.RecoverWorkshopMembership(owner, operation, ("new-group", 2), "new-group", 4);
        Check(wands.ReadWorkshopState(owner, operation).Status == "complete"
            && wands.ReadWorkshopState(owner, operation).Wand?.WandId == selected.WandId
            && wands.ReadWorkshopMessages(owner, operation).Single().Text == "Follow the map.",
            "re-enabled membership preserves immutable selection and conversation");

        foreach (var generationStatus in new[] { "absent", "queued", "ready" })
        {
            foreach (var interruptedStatus in new[] { "failed", "paused" })
            {
                var recoveryOwner = "recovery-" + generationStatus + "-" + interruptedStatus;
                var (recoveryProfile, recoverySnapshot) = await PrepareProfileAsync(profiles,
                    recoveryOwner, new("Raven", "Original fiction"));
                var recoveryOperation = Guid.NewGuid().ToString("N");
                wands.BeginMobile(recoveryOwner, recoveryOperation, recoverySnapshot, 1);
                wands.BeginWorkshop(recoveryOwner, recoveryOperation, recoverySnapshot, 1, "old-group", 1);
                wands.AppendWorkshopMessage(recoveryOwner, recoveryOperation, Guid.NewGuid().ToString("N"),
                    "user", "Keep this accepted answer.", "text");
                wands.FinishWorkshopConversation(recoveryOwner, recoveryOperation,
                    recoveryProfile.ProfileId, recoveryProfile.Revision);
                if (generationStatus != "absent")
                {
                    wands.Begin(recoveryOwner, recoveryOperation, recoverySnapshot, 1, true);
                }
                if (generationStatus == "ready")
                {
                    var readyJob = wands.Claim(recoveryOwner, recoveryOperation, false, ("old-group", 1))!;
                    wands.Complete(readyJob, Candidates().Take(1).ToArray());
                }
                if (interruptedStatus == "paused")
                {
                    wands.PauseWorkshopForBoss(recoveryOwner, recoveryOperation);
                }
                else
                {
                    wands.FailWorkshop(recoveryOwner, recoveryOperation, "workshop_authority_revoked");
                }
                var candidate = wands.Read(recoveryOwner, recoveryOperation)?.Candidates.SingleOrDefault();
                wands.RecoverWorkshopMembership(recoveryOwner, recoveryOperation, ("old-group", 1), "new-group", 2);
                Check(wands.ReadWorkshopState(recoveryOwner, recoveryOperation).Status == "generating",
                    $"{generationStatus}/{interruptedStatus} recovery preserves accepted finish intent");
                var editingRejected = false;
                try
                {
                    wands.AppendWorkshopMessage(recoveryOwner, recoveryOperation, Guid.NewGuid().ToString("N"),
                        "user", "Change evidence after manufacture.", "text");
                }
                catch (WandGenerationException error) when (error.Code == "wand_message_locked") { editingRejected = true; }
                Check(editingRejected, $"{generationStatus}/{interruptedStatus} recovery never reopens frozen evidence");
                if (candidate is not null)
                {
                    Check(wands.Claim(recoveryOwner, recoveryOperation, false, ("new-group", 2)) is null,
                        "ready recovery cannot dispatch another paid generation");
                    var recoveredSelection = wands.Select(recoveryOwner, recoveryOperation, recoveryProfile.ProfileId,
                        recoveryProfile.Revision, 1, 1, ("new-group", 2));
                    Check(recoveredSelection.WandId == candidate.WandId,
                        "ready recovery selects the existing candidate from the accepted answer");
                }
            }
        }
    }

    private static async Task WorkshopStateMachineAsync(CharacterProfileStore profiles, WandGenerationStore wands,
        IConfiguration configuration)
    {
        const string owner = "workshop-owner";
        var (profile, captured) = await PrepareProfileAsync(profiles, owner,
            new("Havran", "Fikční svět", "verze A", "Loví ztracené mapy"));
        var promptMessage = new MobileWandWorkshopMessage("humor-user", 1, "user", "Vyrob mi hůlku.", "text", 1);
        using (var opening = JsonDocument.Parse(OpenAiMobileWandQuestionProvider.BuildReplyRequest(captured, [], "gpt-6.1-terra")))
        {
            using var openingInput = JsonDocument.Parse(opening.RootElement.GetProperty("input").GetString()!);
            Check(openingInput.RootElement.GetProperty("conversation").GetArrayLength() == 0,
                "a prepared profile can author an opening without invented guest evidence");
        }
        using (var draft = JsonDocument.Parse(OpenAiMobileWandQuestionProvider.BuildReplyRequest(captured,
            [promptMessage], "gpt-6.1-terra")))
        using (var live = JsonDocument.Parse(JsonSerializer.Serialize(MobileWorkshopLiveRuntime.Start(
            new(owner, "humor-operation", "session", "stamp", "https://localhost", DateTimeOffset.UtcNow.AddMinutes(1), captured),
            [promptMessage], new { id = "voice_workshop_fixture" }, "humor-start", "An expressive original Czech baritone."))))
        {
            var instructions = draft.RootElement.GetProperty("instructions").GetString()!;
            var liveInstructions = live.RootElement.GetProperty("session").GetProperty("instructions").GetString()!;
            Check(instructions == WorkshopPrompts.Dialogue
                && liveInstructions.Contains(WorkshopHumorInstructions.Text),
                "dialogue uses the approved canonical Czech prompt while legacy Live retains its character-based policy");
            Check(liveInstructions.Contains("Emoce vyjadřuj intonací, důrazem a střídáním tempa")
                && liveInstructions.Contains("při otázce") && liveInstructions.Contains("při opravě", StringComparison.OrdinalIgnoreCase)
                && liveInstructions.Contains("An expressive original Czech baritone.")
                && !liveInstructions.Contains("talentovaný a klidný řemeslník"),
                "actual workshop startup requests expressive situation-based delivery and the saved custom voice description");
            Check(live.RootElement.GetProperty("session").GetProperty("audio").GetProperty("output")
                .GetProperty("voice").GetProperty("id").GetString() == "voice_workshop_fixture",
                "expressive workshop startup keeps the custom voice ID object intact");
            Check(draft.RootElement.GetProperty("reasoning").GetProperty("effort").GetString() == "none",
                "the short authored reply does not consume its small output budget on hidden reasoning");
            using var input = JsonDocument.Parse(draft.RootElement.GetProperty("input").GetString()!);
            var fact = input.RootElement.GetProperty("enrichment").GetProperty("facts")[0];
            Check(fact.GetProperty("text").GetString() == captured.Enrichment!.Facts[0].Text
                && fact.GetProperty("sourceUrls")[0].GetString() == "https://example.test/map",
                "the roast prompt receives actual researched facts and evidence rather than an invented character summary");
        }
        Check(await profiles.CapturePreparedAsync(owner, profile.ProfileId, profile.Revision + 1) is null,
            "a completion for an older profile revision cannot capture the current character");
        var waitingProfile = await profiles.SaveAsync("workshop-profile-preparing",
            new(0, new("Raven", "Fikční svět"), OpenAiConsent: true));
        Check(await profiles.CapturePreparedAsync("workshop-profile-preparing",
            waitingProfile.ProfileId, waitingProfile.Revision) is null,
            "an unfinished preparation cannot open a workshop profile snapshot");
        var operation = Guid.NewGuid().ToString("N");
        wands.BeginMobile(owner, operation, captured, 1);
        wands.BeginWorkshop(owner, operation, captured, 1, "test-group", 1);

        var state = wands.ReadWorkshopState(owner, operation);
        Check(state.Status == "asking" && state.Question is null && state.Steps.Length == 0
            && state.Messages is { Length: 0 }, "prepared workshop opens an empty natural conversation without authoring a question");
        var noEvidenceRejected = false;
        try { wands.FinishWorkshopConversation(owner, operation, profile.ProfileId, profile.Revision); }
        catch (WandGenerationException error) when (error.Code == "wand_conversation_evidence_required") { noEvidenceRejected = true; }
        Check(noEvidenceRejected, "an empty conversation cannot start wand manufacture");
        try
        {
            wands.BeginWorkshop(owner, Guid.NewGuid().ToString("N"), captured with { Enrichment = null }, 1, "test-group", 1);
            throw new InvalidOperationException("Unprepared profile entered workshop.");
        }
        catch (WandGenerationException error) when (error.Code == "character_not_ready") { }

        var first = wands.AppendWorkshopMessage(owner, operation, Guid.NewGuid().ToString("N"),
            "user", "Nejdřív ověřím, která mapa chybí.", "text");
        var userMessage = first[0];
        first = wands.UpdateWorkshopMessage(owner, operation, userMessage.MessageId,
            userMessage.Revision, "Nejdřív ověřím, která mapa se skutečně ztratila.", "text");
        Check(first[0].Revision == 2 && first[0].Text.Contains("skutečně", StringComparison.Ordinal),
            "message corrections advance revision and replace older interpretation");
        var duplicate = wands.AppendWorkshopMessage(owner, operation, userMessage.MessageId,
            "user", "Nejdřív ověřím, která mapa se skutečně ztratila.", "text");
        Check(duplicate.Length == 1, "retried message IDs are accepted exactly once after correction");
        var conflict = false;
        try { wands.AppendWorkshopMessage(owner, operation, userMessage.MessageId, "user", "jiný text", "text"); }
        catch (WandGenerationException error) when (error.Code == "wand_message_id_conflict") { conflict = true; }
        Check(conflict, "a reused stable message ID cannot mutate a different message");
        wands.AppendWorkshopMessage(owner, operation, Guid.NewGuid().ToString("N"),
            "assistant", "Ta mapa se ztratila poblíž tvého oblíbeného bludiště. Dobře, aspoň je konkrétní.", "voice");
        wands.AppendWorkshopMessage(owner, operation, Guid.NewGuid().ToString("N"),
            "user", "To stačí, vyrob ji.", "text");
        wands.AppendWorkshopMessage(owner, operation, Guid.NewGuid().ToString("N"),
            "assistant", "Výtečně. Mapa se konečně našla; její sebevědomí bude složitější oprava.", "voice");
        Check(wands.FinishWorkshopConversation(owner, operation, profile.ProfileId, profile.Revision),
            "a short two-user-turn transcript can finish explicitly without a fixed exchange count");
        Check(!wands.FinishWorkshopConversation(owner, operation, profile.ProfileId, profile.Revision),
            "repeated finish dispatches only one generation operation");
        Check(wands.ReadWorkshopState(owner, operation).Status == "generating"
            && wands.Read(owner, operation) is null, "finishing is a separate durable transition before model dispatch");
        var pending = wands.PendingWorkshops().Single(job => job.OperationId == operation);
        Check(pending.Messages.Length == 4 && pending.Steps.Length == 0
            && MobileWandWorkshopWorker.GenerationEvidence(pending).Length == 4,
            "generation receives every accepted corrected user and assistant turn from the frozen profile");
        Check(wands.PauseWorkshopForBoss(owner, operation)
            && wands.ReadWorkshopState(owner, operation).Status == "paused",
            "temporary boss captivity pauses an unfinished generation without failing the operation");
        Check(wands.ReadWorkshopMessages(owner, operation).Length == 4
            && wands.ResumeWorkshopAfterBoss(owner, operation)
            && wands.ReadWorkshopState(owner, operation).Status == "generating",
            "rescue resumes the same persisted conversation and generation operation");
        var locked = false;
        try { wands.AppendWorkshopMessage(owner, operation, Guid.NewGuid().ToString("N"), "user", "late", "text"); }
        catch (WandGenerationException error) when (error.Code == "wand_message_locked") { locked = true; }
        Check(locked, "late speech after finish cannot mutate the generation snapshot");

        var (evidenceProfile, evidenceSnapshot) = await PrepareProfileAsync(profiles,
            "workshop-evidence-race", new("Mapař", "Fikční svět"));
        var evidenceOperation = Guid.NewGuid().ToString("N");
        wands.BeginMobile("workshop-evidence-race", evidenceOperation, evidenceSnapshot, 1);
        wands.BeginWorkshop("workshop-evidence-race", evidenceOperation, evidenceSnapshot, 1, "test-group", 1);
        var evidenceUser = wands.AppendWorkshopMessage("workshop-evidence-race", evidenceOperation,
            Guid.NewGuid().ToString("N"), "user", "Prověřím starou mapu.", "text").Single();
        var initialEvidenceDigest = WandGenerationStore.UserEvidenceDigest(
            wands.ReadWorkshopMessages("workshop-evidence-race", evidenceOperation));
        wands.AppendWorkshopMessage("workshop-evidence-race", evidenceOperation,
            Guid.NewGuid().ToString("N"), "assistant", "Rozumím.", "voice");
        Check(initialEvidenceDigest == WandGenerationStore.UserEvidenceDigest(
                wands.ReadWorkshopMessages("workshop-evidence-race", evidenceOperation)),
            "assistant acknowledgements do not change the user evidence digest");
        var editedEvidence = wands.UpdateWorkshopMessage("workshop-evidence-race", evidenceOperation,
            evidenceUser.MessageId, evidenceUser.Revision, "Oprava: prověřím severní mapu.", "text");
        var staleDigestRejected = false;
        try
        {
            wands.FinishWorkshopConversation("workshop-evidence-race", evidenceOperation,
                evidenceProfile.ProfileId, evidenceProfile.Revision, initialEvidenceDigest);
        }
        catch (WandGenerationException error) when (error.Code == "wand_conversation_changed")
        { staleDigestRejected = true; }
        Check(staleDigestRejected && editedEvidence[0].Revision == evidenceUser.Revision + 1
            && wands.ReadWorkshopState("workshop-evidence-race", evidenceOperation).Status == "asking",
            "an atomic finish rejects changed user evidence and leaves the dialogue open");
        var latestEvidenceDigest = WandGenerationStore.UserEvidenceDigest(editedEvidence);
        Check(wands.FinishWorkshopConversation("workshop-evidence-race", evidenceOperation,
            evidenceProfile.ProfileId, evidenceProfile.Revision, latestEvidenceDigest),
            "finish accepts the current corrected user evidence digest");

        var (fourExchangeProfile, fourExchangeSnapshot) = await PrepareProfileAsync(profiles,
            "workshop-four-exchanges", new("Mapař", "Fikční svět"));
        var fourExchangeOperation = Guid.NewGuid().ToString("N");
        wands.BeginMobile("workshop-four-exchanges", fourExchangeOperation, fourExchangeSnapshot, 1);
        wands.BeginWorkshop("workshop-four-exchanges", fourExchangeOperation, fourExchangeSnapshot,
            1, "test-group", 1);
        for (var exchange = 0; exchange < 4; exchange++)
        {
            wands.AppendWorkshopMessage("workshop-four-exchanges", fourExchangeOperation,
                Guid.NewGuid().ToString("N"), "user", $"Zpráva {exchange + 1}.", "text");
            wands.AppendWorkshopMessage("workshop-four-exchanges", fourExchangeOperation,
                Guid.NewGuid().ToString("N"), "assistant", $"Odpověď {exchange + 1}.", "voice");
        }
        Check(wands.FinishWorkshopConversation("workshop-four-exchanges", fourExchangeOperation,
            fourExchangeProfile.ProfileId, fourExchangeProfile.Revision)
            && wands.PendingWorkshops().Single(job => job.OperationId == fourExchangeOperation).Messages.Length == 8,
            "a longer natural conversation retains all exchanges and completes without a three-turn condition");

        var (legacyProfile, legacySnapshot) = await PrepareProfileAsync(profiles,
            "workshop-legacy-recovery", new("Raven", "Fikční svět"));
        var legacyOperation = Guid.NewGuid().ToString("N");
        wands.BeginMobile("workshop-legacy-recovery", legacyOperation, legacySnapshot, 1);
        wands.BeginWorkshop("workshop-legacy-recovery", legacyOperation, legacySnapshot, 1, "test-group", 1);
        var legacySteps = new[]
        {
            new MobileWandWorkshopStep("legacy-q1", 1, 1, "První otázka?", [], "legacy-a1", 2, "Odpověď opravená.", SourceMode: "text"),
            new MobileWandWorkshopStep("legacy-q2", 1, 2, "Další otázka?", [])
        };
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = configuration["Backend:DatabasePath"] ?? throw new InvalidOperationException("Backend database path missing.") }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE mobile_wand_workshops SET status='authoring',steps_json=$steps WHERE account_id=$owner AND operation_id=$operation";
            command.Parameters.AddWithValue("$steps", JsonSerializer.Serialize(legacySteps,
                new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            command.Parameters.AddWithValue("$owner", "workshop-legacy-recovery");
            command.Parameters.AddWithValue("$operation", legacyOperation);
            command.ExecuteNonQuery();
        }
        var resumedLegacy = wands.ReadWorkshopState("workshop-legacy-recovery", legacyOperation);
        Check(resumedLegacy.Status == "asking" && resumedLegacy.Steps.Length == 0
            && resumedLegacy.Messages?.Select(message => (message.Role, message.Text)).SequenceEqual([
                ("assistant", "První otázka?"), ("user", "Odpověď opravená."), ("assistant", "Další otázka?")]) == true
            && resumedLegacy.Messages?.Single(message => message.Role == "user").Revision == 2,
            "legacy issued questions and corrected answer revision migrate to the same resumable free-form dialogue");

        var (failedQuestionProfile, failedQuestionSnapshot) = await PrepareProfileAsync(profiles,
            "workshop-legacy-question-failure", new("Raven", "Fikční svět"));
        var failedQuestionOperation = Guid.NewGuid().ToString("N");
        wands.BeginMobile("workshop-legacy-question-failure", failedQuestionOperation, failedQuestionSnapshot, 1);
        wands.BeginWorkshop("workshop-legacy-question-failure", failedQuestionOperation, failedQuestionSnapshot,
            1, "test-group", 1);
        var failedGenerationOperation = Guid.NewGuid().ToString("N");
        wands.BeginMobile("workshop-legacy-generation-failure", failedGenerationOperation, failedQuestionSnapshot, 1);
        wands.BeginWorkshop("workshop-legacy-generation-failure", failedGenerationOperation, failedQuestionSnapshot,
            1, "test-group", 1);
        var oldGeneration = wands.Begin("workshop-legacy-generation-failure", failedGenerationOperation,
            failedQuestionSnapshot, 1, openAiConsent: true);
        var oldGenerationJob = wands.Claim("workshop-legacy-generation-failure", failedGenerationOperation, false)!;
        wands.Complete(oldGenerationJob, Candidates().Take(1).ToArray());
        Check(wands.Read("workshop-legacy-generation-failure", failedGenerationOperation)?.Status == "ready",
            "legacy generation fixture has a durable paid result before the transient failure");
        var permanentFailureOperation = Guid.NewGuid().ToString("N");
        wands.BeginMobile("workshop-legacy-permanent-failure", permanentFailureOperation, failedQuestionSnapshot, 1);
        wands.BeginWorkshop("workshop-legacy-permanent-failure", permanentFailureOperation, failedQuestionSnapshot,
            1, "test-group", 1);
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = configuration["Backend:DatabasePath"] ?? throw new InvalidOperationException("Backend database path missing.") }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE mobile_wand_workshops SET status='failed',error_code=CASE operation_id WHEN $question THEN 'question_authoring_authority_unavailable' WHEN $generation THEN 'wand_workshop_boss_captured' ELSE 'wand_generation_provider_rejected' END,steps_json=CASE WHEN operation_id=$question THEN $steps ELSE '[]' END WHERE operation_id IN ($question,$generation,$permanent)";
            command.Parameters.AddWithValue("$question", failedQuestionOperation);
            command.Parameters.AddWithValue("$generation", failedGenerationOperation);
            command.Parameters.AddWithValue("$permanent", permanentFailureOperation);
            command.Parameters.AddWithValue("$steps", JsonSerializer.Serialize(legacySteps,
                new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            command.ExecuteNonQuery();
        }
        var restoredQuestion = wands.ReadWorkshopState("workshop-legacy-question-failure", failedQuestionOperation);
        Check(restoredQuestion.Status == "paused" && restoredQuestion.ErrorCode == "wand_workshop_boss_captured"
            && restoredQuestion.Messages?.Single(message => message.Role == "user").Revision == 2,
            "known temporary question authority failure migrates to paused and preserves the corrected answer");
        var restoredGeneration = wands.ReadWorkshopState("workshop-legacy-generation-failure", failedGenerationOperation);
        Check(restoredGeneration.Status == "paused" && restoredGeneration.ErrorCode == "wand_workshop_boss_captured"
            && wands.ResumeWorkshopAfterBoss("workshop-legacy-generation-failure", failedGenerationOperation)
            && wands.Read("workshop-legacy-generation-failure", failedGenerationOperation)?.SessionId == oldGeneration.SessionId,
            "known boss-captured generation failure resumes the same paid result instead of redispatching it");
        Check(wands.ReadWorkshopState("workshop-legacy-permanent-failure", permanentFailureOperation).Status == "failed",
            "unrecognized permanent generation failures remain failed rather than being rescued by migration");

        var priorEvidence = new[]
        {
            new MobileWandWorkshopMessage("stable-user", 1, "user", "Ověřím mapu.", "text", 1),
            new MobileWandWorkshopMessage("stable-assistant", 1, "assistant", "Dobře.", "voice", 2)
        };
        var assistantAcknowledged = priorEvidence.Append(
            new MobileWandWorkshopMessage("ack", 1, "assistant", "Rozumím.", "voice", 3));
        var correctedEvidence = new[]
        {
            priorEvidence[0] with { Revision = 2, Text = "Změna: ověřím severní mapu." }, priorEvidence[1]
        };
        var appendedUserEvidence = priorEvidence.Append(
            new MobileWandWorkshopMessage("new-user", 1, "user", "To stačí.", "text", 3));
        Check(MobileWorkshopLiveRuntime.SameUserEvidence(priorEvidence, assistantAcknowledged)
            && !MobileWorkshopLiveRuntime.SameUserEvidence(priorEvidence, correctedEvidence)
            && !MobileWorkshopLiveRuntime.SameUserEvidence(priorEvidence, appendedUserEvidence),
            "assistant acknowledgements do not invalidate generation evidence, while user corrections or new input do");

        var generation = wands.Begin(owner, operation, captured, 1, openAiConsent: true);
        var duplicateGeneration = wands.Begin(owner, operation, captured, 1, openAiConsent: true);
        Check(generation.SessionId == duplicateGeneration.SessionId && generation.ReadyMask == 1,
            "mobile generation uses one idempotent candidate while physical six-slot validation remains separate");
        wands.DeleteProfile(owner, profile.ProfileId);
        Check(wands.FindWorkshop(owner, profile.ProfileId, profile.Revision, 1) is null,
            "profile revocation erases the workshop snapshot, issued questions and answers");

        const string erasedOwner = "workshop-account-erasure";
        var (erasedProfile, erasedCapture) = await PrepareProfileAsync(profiles, erasedOwner,
            new("Raven", "Fictional world"));
        var erasedOperation = Guid.NewGuid().ToString("N");
        wands.BeginMobile(erasedOwner, erasedOperation, erasedCapture, 1);
        wands.BeginWorkshop(erasedOwner, erasedOperation, erasedCapture, 1, "test-group", 1);
        wands.DeleteAccount(erasedOwner);
        Check(wands.FindWorkshop(erasedOwner, erasedProfile.ProfileId, erasedProfile.Revision, 1) is null,
            "account erasure deletes all mobile workshop snapshots");

        const string interruptedOwner = "workshop-start-crash";
        var (interruptedProfile, interruptedCapture) = await PrepareProfileAsync(profiles, interruptedOwner,
            new("Raven", "Fictional world"));
        var interruptedOperation = Guid.NewGuid().ToString("N");
        wands.BeginMobile(interruptedOwner, interruptedOperation, interruptedCapture, 1);
        Check(wands.MobileOperation(interruptedOwner, interruptedProfile.ProfileId,
            interruptedProfile.Revision, 1) == interruptedOperation && wands.Read(interruptedOwner, interruptedOperation) is null,
            "mobile reservation can survive a crash before any generation session exists");
        wands.BeginWorkshop(interruptedOwner, interruptedOperation, interruptedCapture, 1, "test-group", 1);
        Check(wands.ReadWorkshopState(interruptedOwner, interruptedOperation) is { Status: "asking", Steps.Length: 0 },
            "explicit restart repairs the reservation crash window as an empty resumable conversation");
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

    private static async Task InFlightManufactureCaptureAsync(CharacterProfileStore profiles, string root)
    {
        // Queue fairness coverage leaves more pending jobs than one worker batch can return.
        Directory.CreateDirectory(root);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["Backend:DatabasePath"] = Path.Combine(root, "wands.sqlite") }).Build();
        var wands = new WandGenerationStore(configuration, TimeProvider.System);
        const string owner = "inflight-captured-owner";
        var (profile, captured) = await PrepareProfileAsync(profiles, owner, new("Míra", "Fiktivní most"));
        var operation = Guid.NewGuid().ToString("N");
        wands.BeginMobile(owner, operation, captured, 1);
        wands.BeginWorkshop(owner, operation, captured, 1, "test-group", 1);
        wands.AppendWorkshopMessage(owner, operation, Guid.NewGuid().ToString("N"), "user", "Zastav most a vyrob hůlku.", "voice");
        wands.FinishWorkshopConversation(owner, operation, profile.ProfileId, profile.Revision);
        var job = wands.PendingWorkshops().Single(item => item.OperationId == operation);
        wands.Begin(owner, operation, captured, 1, true, outputLanguage: "cs");
        var provider = new DelayedWorkshopProvider();
        var service = new WandGenerationService(wands, provider);
        var running = service.GenerateAsync(owner, operation, _ => Task.FromResult(true), evidence: MobileWandWorkshopWorker.GenerationEvidence(job));
        await provider.Started.Task;
        Check(wands.PauseWorkshopForBoss(owner, operation), "capture pauses a dispatch already in flight");
        provider.Release.TrySetResult();
        Check((await running).Status == "ready" && wands.ReadWorkshopState(owner, operation).Status == "paused",
            "an accepted paid manufacture is retained during capture");
        Check(wands.ResumeWorkshopAfterBoss(owner, operation), "rescue resumes the original manufacturing operation");
        var reused = await service.GenerateAsync(owner, operation, _ => Task.FromResult(true), evidence: MobileWandWorkshopWorker.GenerationEvidence(job));
        Check(reused.Status == "ready" && provider.Calls == 1, "rescue reuses the result without a second provider call");
    }

    private sealed class DelayedWorkshopProvider : IWandGenerationProvider
    {
        internal readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Calls;
        public async Task<WandCandidateContent[]> GenerateAsync(WandGenerationJob job, CancellationToken token)
        { Calls++; Started.TrySetResult(); await Release.Task.WaitAsync(token); return Candidates().Take(1).ToArray(); }
    }

    private static async Task SeedGeneratingWorkshopAsync(CharacterProfileStore profiles,
        WandGenerationStore wands, string owner)
    {
        var (profile, captured) = await PrepareProfileAsync(profiles, owner, new(owner, "Fictional world"));
        var operation = Guid.NewGuid().ToString("N");
        wands.BeginMobile(owner, operation, captured, 1);
        wands.BeginWorkshop(owner, operation, captured, 1, "test-group", 1);
        wands.AppendWorkshopMessage(owner, operation, Guid.NewGuid().ToString("N"),
            "user", "Připraveno k výrobě.", "text");
        wands.FinishWorkshopConversation(owner, operation, profile.ProfileId, profile.Revision);
    }

    private static async Task<(CharacterProfile Profile, CharacterConversationSnapshot Snapshot)> PrepareProfileAsync(
        CharacterProfileStore profiles, string owner, CharacterUserInput input)
    {
        var profile = await profiles.SaveAsync(owner, new(0, input, OpenAiConsent: true));
        var enrichment = new CharacterEnrichment(input.Name, input.World, input.Description ?? "Synthetic prepared character.",
            [new("Keeps finding lost maps while insisting the route was obvious.", ["https://example.test/map"] )],
            ["A new interruption while following a map."], [], false,
            [new("Synthetic source", "https://example.test/map")], "synthetic", "test-v1",
            DateTimeOffset.UtcNow.ToUnixTimeSeconds(), profile.Revision, "synthetic");
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = profiles.CustomerDataDatabasePath }.ToString());
        connection.Open();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE character_profiles SET status='ready',enrichment_json=$enrichment WHERE account_id=$owner AND profile_id=$profile AND revision=$revision";
            command.Parameters.AddWithValue("$enrichment", JsonSerializer.Serialize(enrichment,
                new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            command.Parameters.AddWithValue("$owner", owner);
            command.Parameters.AddWithValue("$profile", profile.ProfileId);
            command.Parameters.AddWithValue("$revision", profile.Revision);
            if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("Could not seed prepared character context.");
        }
        var snapshot = await profiles.CapturePreparedAsync(owner, profile.ProfileId, profile.Revision)
            ?? throw new InvalidOperationException("Prepared character snapshot unavailable.");
        return (profile with { Status = "ready", Enrichment = enrichment }, snapshot);
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
            "legacy unselected six-candidate operation is retired before a natural-dialogue mobile craft");
    }

    internal sealed class Provider : IWandGenerationProvider
    {
        public Task<WandCandidateContent[]> GenerateAsync(WandGenerationJob job, CancellationToken cancellationToken)
            => Task.FromResult(Candidates());
    }

    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
