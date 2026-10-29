using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Kufr.Backend.Features.Access;
using Kufr.Backend.Features.Customers;
using Kufr.Backend.Features.Identity;
using Kufr.Backend.Features.Voices;
using Kufr.Backend.Features.Wands;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

internal static class WorkshopSettingsTests
{
    internal static async Task RunAsync()
    {
        await PersistenceAsync();
        await EffectiveRequestsAsync();
        await EngineDefaultsAsync();
        await AccountIdentityTests.RunWorkshopSettingsHttpAsync();
        Console.WriteLine("Workshop settings: durable CAS/corruption, frozen prompts/reasoning, actual SDK requests, engine defaults and HTTPS admin/large-body gates passed (offline).");
    }

    private static async Task PersistenceAsync()
    {
        using var fixture = new SettingsFixture();
        var store = fixture.Store();
        var initial = store.Snapshot();
        Check(initial.Revision == 0 && initial.DialoguePrompt == WorkshopPrompts.Dialogue
            && initial.LivePrompt == MobileWorkshopLiveRuntime.DefaultInstructions && initial.ReasoningEffort == "none",
            "fresh stores use the canonical prompts and explicit no-reasoning default");
        var admission = new MobileWandAdmission("owner", "operation", "session", "stamp", "https://localhost",
            DateTimeOffset.UtcNow.AddMinutes(1), Profile()) { WorkshopSettings = initial };
        var saved = store.Save(new(0, "New dialogue prompt.", "New live prompt.", "high"));
        Check(fixture.Store().Snapshot() == saved && admission.DialogueInstructions == initial.DialoguePrompt
            && admission.LiveInstructions == initial.LivePrompt && admission.ReasoningEffort == "none",
            "fresh instances see persisted settings while admitted connections retain their original immutable snapshot");
        using (var live = JsonDocument.Parse(JsonSerializer.Serialize(MobileWorkshopLiveRuntime.Start(
            admission with { WorkshopSettings = saved }, [], "fixture-voice", "fixture-start", "Trusted voice description"))))
        {
            Check(live.RootElement.GetProperty("session").GetProperty("instructions").GetString()!
                .StartsWith(saved.LivePrompt, StringComparison.Ordinal), "actual legacy session update consumes captured production live instructions");
        }
        var local = admission with { LocalExperiment = new(1, "speech-engine", "Local dialogue.", "Local live.") };
        Check(local.DialogueInstructions == "Local dialogue." && local.LiveInstructions == "Local live."
            && local.ReasoningEffort == initial.ReasoningEffort, "trusted local prompt overrides retain the independently captured reasoning choice");
        Throws(() => store.Save(new(0, "stale", "stale", "none")), "workshop_settings_conflict", 409);
        foreach (var invalid in new[] { "", "None", "xhigh", "off", "high " })
        { Throws(() => store.Save(new(saved.Revision, "dialogue", "live", invalid)), "workshop_settings_invalid", 400); }
        Throws(() => store.Save(new(saved.Revision, new string('x', 12001), "live", "none")), "workshop_settings_invalid", 400);
        Throws(() => store.Save(new(saved.Revision, "dialogue", " ", "none")), "workshop_settings_invalid", 400);
        var outcomes = await Task.WhenAll(Enumerable.Range(0, 2).Select(index => Task.Run(() =>
        {
            try { fixture.Store().Save(new(saved.Revision, "Concurrent " + index, "Live " + index, "low")); return "saved"; }
            catch (WorkshopSettingsException error) { return error.Code; }
        })));
        Check(outcomes.Count(value => value == "saved") == 1 && outcomes.Count(value => value == "workshop_settings_conflict") == 1,
            "separate store instances cannot lose a concurrent compare-and-swap update");
        var current = store.Snapshot();
        var large = store.Save(new(current.Revision, new string('ř', 12000), new string('ů', 12000), "medium"));
        Check(fixture.Store().Snapshot() == large && new FileInfo(fixture.File).Length > 65536
            && !Directory.EnumerateFiles(fixture.Root, "*.tmp").Any(), "full Czech prompts survive escaped JSON persistence with no abandoned temporary files");
        File.WriteAllText(fixture.File, "{broken settings");
        Throws(() => store.Snapshot(), "workshop_settings_corrupt", 503);
        Throws(() => store.Save(new(large.Revision, "valid", "valid", "none")), "workshop_settings_corrupt", 503);
        Check(File.ReadAllText(fixture.File) == "{broken settings", "corrupt settings fail closed without replacement");
        File.WriteAllText(fixture.File, new string('x', 256 * 1024 + 1));
        Throws(() => fixture.Store().Snapshot(), "workshop_settings_corrupt", 503);
        Check(new FileInfo(fixture.File).Length == 256 * 1024 + 1, "oversized durable settings remain preserved for repair");
    }

    private static async Task EffectiveRequestsAsync()
    {
        using var fixture = new SettingsFixture();
        var settings = fixture.Store();
        using var handler = new DialogueHandler();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["OPENAI_API_KEY"] = "offline-key", ["Wands:Model"] = "gpt-6.1-terra" }).Build();
        using var provider = new OpenAiMobileWandQuestionProvider(configuration, new HttpClient(handler));
        foreach (var effort in new[] { "none", "high" })
        {
            var saved = settings.Save(new(settings.Snapshot().Revision, new string('ř', 12000), new string('ů', 12000), effort));
            var captured = fixture.Store().Snapshot();
            Check(captured == saved, "full Czech prompt is loaded from durable settings before provider dispatch");
            foreach (var stream in new[] { false, true })
            {
                var payload = OpenAiMobileWandQuestionProvider.BuildReplyRequest(Profile(), [], "gpt-6.1-terra",
                    captured.DialoguePrompt, stream, captured.ReasoningEffort);
                Check(Encoding.UTF8.GetByteCount(payload) > 65536 && Encoding.UTF8.GetByteCount(payload) < 256 * 1024,
                    "accepted full Czech prompt exceeds the old provider limit but fits the bounded complete request");
            }
            _ = await provider.ReplyAsync(Profile(), [], default, captured.DialoguePrompt, captured.ReasoningEffort);
            _ = await provider.ReplyStreamingAsync(Profile(), [], (_, _) => Task.CompletedTask,
                (_, _) => Task.CompletedTask, default, captured.DialoguePrompt, captured.ReasoningEffort);
            foreach (var request in handler.Requests.TakeLast(2))
            {
                Check(request.GetProperty("reasoning").GetProperty("effort").GetString() == effort
                    && request.GetProperty("instructions").GetString() == captured.DialoguePrompt
                    && request.GetProperty("max_output_tokens").GetInt32() == (effort == "high" ? 3000 : 420)
                    && request.GetProperty("model").GetString() == "gpt-6.1-terra"
                    && request.GetProperty("service_tier").GetString() == "fast",
                    "actual nonstream and typed streaming SDK requests preserve all 12000 Czech characters, reasoning caps and Terra Fast policy");
            }
        }
        var oversizedHistory = Enumerable.Range(0, 64).Select(index => new MobileWandWorkshopMessage(
            "message-" + index, 1, index % 2 == 0 ? "user" : "assistant", new string('ř', 600), "voice", index)).ToArray();
        try
        {
            OpenAiMobileWandQuestionProvider.BuildReplyRequest(Profile(), oversizedHistory, "gpt-6.1-terra",
                new string('ř', 12000), stream: true, reasoningEffort: "high");
            throw new InvalidOperationException("Oversized complete provider context was accepted.");
        }
        catch (MobileWandQuestionAuthoringException error)
        { Check(error.Code == "dialogue_context_too_large", "complete payload remains bounded at 256 KiB before provider dispatch"); }
        Check(handler.Requests.Count == 4 && handler.Requests.Count(value => value.GetProperty("stream").GetBoolean()) == 2,
            "reasoning verification observes four real SDK requests without hidden retries");
    }

    private static async Task EngineDefaultsAsync()
    {
        using var fixture = new SettingsFixture();
        Check(ElevenLabsWorkshopOptions.Read(fixture.Configuration).UseSpeechEngine, "production voice defaults to Speech Engine");
        foreach (var explicitOrigin in new string?[] { null, "https://explicit-tunnel.example.test" })
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ApplicationIdentity:DataRoot"] = fixture.Root, ["ELEVENLABS_API_KEY"] = "offline-key",
                ["ApplicationIdentity:AllowedOrigins:0"] = "https://localhost:5173",
                ["ApplicationIdentity:AllowedOrigins:1"] = "http://public.example.test",
                ["ApplicationIdentity:AllowedOrigins:2"] = "https://first-public.example.test",
                ["ApplicationIdentity:AllowedOrigins:3"] = "https://second-public.example.test",
                ["Wands:Voice:SpeechEnginePublicOrigin"] = explicitOrigin
            }).Build();
            using var handler = new EngineHandler();
            using var engines = new SpeechEngineSessions(configuration, ElevenLabsWorkshopOptions.Read(configuration),
                new ActorVoiceSettings(configuration, fixture.Accounts), NullLogger<SpeechEngineSessions>.Instance, new HttpClient(handler));
            await engines.StartAsync(default);
            var admission = new MobileWandAdmission("owner", "operation", "session", "stamp", "https://localhost",
                DateTimeOffset.UtcNow.AddMinutes(1), Profile()) { ElevenLabsConsent = true };
            _ = await engines.PrepareAsync(admission, default);
            Check(handler.Callback!.StartsWith((explicitOrigin ?? "https://first-public.example.test").Replace("https://", "wss://", StringComparison.Ordinal)
                + "/app/v1/customer/wand/speech-engine/", StringComparison.Ordinal),
                "actual callback uses the first configured public HTTPS origin or explicit local tunnel override");
            await engines.StopAsync(default);
        }
    }

    private static CharacterConversationSnapshot Profile() => new("profile", 1, new("Raven", "Synthetic"),
        new("Raven", "Synthetic", "Prepared fixture", [new("Finds maps", ["https://example.test"])],
            [], [], false, [], "synthetic", "test", 1, 1, "synthetic"), 1);
    private static void Check(bool condition, string message)
    { if (!condition) { throw new InvalidOperationException("Workshop settings: " + message); } }
    private static void Throws(Action action, string code, int status)
    {
        try { action(); } catch (WorkshopSettingsException error) { Check(error.Code == code && error.Status == status, "expected " + code); return; }
        throw new InvalidOperationException("Expected " + code);
    }

    private sealed class SettingsFixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "dv-workshop-settings-" + Guid.NewGuid().ToString("N"));
        internal string File => Path.Combine(Root, "workshop-settings.json");
        internal IConfiguration Configuration { get; } = new ConfigurationBuilder().Build();
        internal AccountConfiguration Accounts => new(true, Root, Path.Combine(Root, "keys"), "localhost", ["https://localhost"]);
        internal WorkshopSettingsStore Store() => new(Configuration, Accounts);
        public void Dispose() { if (Directory.Exists(Root)) { Directory.Delete(Root, recursive: true); } }
    }

    private sealed class DialogueHandler : HttpMessageHandler
    {
        internal List<JsonElement> Requests { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Check(request.RequestUri?.AbsoluteUri == "https://api.openai.com/v1/responses", "dialogue uses the actual Responses SDK endpoint");
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            Requests.Add(document.RootElement.Clone());
            const string reply = "{\"readyToFinish\":false,\"reply\":\"Co chrání tvůj most?\"}";
            var response = new
            {
                id = "resp_settings", @object = "response", created_at = 1, status = "completed", model = "gpt-6.1-terra", service_tier = "fast",
                output = new[] { new { type = "message", id = "msg_settings", role = "assistant", status = "completed",
                    content = new[] { new { type = "output_text", text = reply, annotations = Array.Empty<object>() } } } }
            };
            if (!document.RootElement.GetProperty("stream").GetBoolean())
            { return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(response), Encoding.UTF8, "application/json") }; }
            var delta = new { type = "response.output_text.delta", delta = reply, sequence_number = 0, item_id = "msg_settings", output_index = 0, content_index = 0 };
            var completed = new { type = "response.completed", sequence_number = 1, response };
            var events = "event: response.output_text.delta\ndata: " + JsonSerializer.Serialize(delta)
                + "\n\nevent: response.completed\ndata: " + JsonSerializer.Serialize(completed) + "\n\n";
            return new(HttpStatusCode.OK) { Content = new StringContent(events, Encoding.UTF8, "text/event-stream") };
        }
    }

    private sealed class EngineHandler : HttpMessageHandler
    {
        internal string? Callback;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.Method == HttpMethod.Delete) { return new(HttpStatusCode.NoContent); }
            if (request.Method == HttpMethod.Post)
            {
                using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
                Callback = document.RootElement.GetProperty("speech_engine").GetProperty("ws_url").GetString();
                return new(HttpStatusCode.OK) { Content = new StringContent("{\"speech_engine_id\":\"seng_settingsfixture\"}", Encoding.UTF8, "application/json") };
            }
            return new(HttpStatusCode.OK) { Content = new StringContent("{\"token\":\"synthetic-settings-browser-token\"}", Encoding.UTF8, "application/json") };
        }
    }
}

internal static partial class AccountIdentityTests
{
    internal static async Task RunWorkshopSettingsHttpAsync()
    {
        await using var fixture = await Fixture.StartAsync(
            configureServices: services => services.AddSingleton<WorkshopSettingsStore>(),
            configureEndpoints: app => app.MapWorkshopSettings());
        using var owner = fixture.Client(); using var anonymous = fixture.Client(); using var ownerKey = new VirtualPasskey();
        const string path = "/app/v1/admin/workshop-settings";
        Check((await anonymous.GetAsync(path)).StatusCode == HttpStatusCode.Unauthorized, "anonymous workshop administration is denied");
        await EnrollAsync(owner, ownerKey, Fixture.Bootstrap, "Workshop owner");
        var session = await CsrfAsync(owner);
        var ownerId = session.GetProperty("account").GetProperty("id").GetString()!;
        using var initialResponse = await owner.GetAsync(path);
        Check(initialResponse.IsSuccessStatusCode && initialResponse.Headers.CacheControl?.NoStore == true, "admin settings are private and never cached");
        var initial = await JsonAsync(initialResponse);
        var revision = initial.GetProperty("revision").GetInt64();
        var change = new WorkshopSettingsChange(revision, new string('ř', 12000), new string('ů', 12000), "high");
        owner.DefaultRequestHeaders.Remove("X-DV-Confirm");
        Check((await owner.PutAsJsonAsync(path, change)).StatusCode == HttpStatusCode.Conflict, "workshop changes require explicit confirmation");
        owner.DefaultRequestHeaders.Add("X-DV-Confirm", "yes");
        owner.DefaultRequestHeaders.Remove("X-DV-CSRF");
        Check((await owner.PutAsJsonAsync(path, change)).StatusCode == HttpStatusCode.BadRequest, "workshop changes require CSRF proof");
        await CsrfAsync(owner);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(change, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Check(bytes.Length > 65536 && bytes.Length < 256 * 1024, "full escaped Czech prompts exceed the ordinary request limit");
        using var fullRequest = new HttpRequestMessage(HttpMethod.Put, path) { Content = new ByteArrayContent(bytes) };
        fullRequest.Content.Headers.ContentType = new("application/json");
        var saved = await JsonAsync(await owner.SendAsync(fullRequest));
        Check(saved.GetProperty("dialoguePrompt").GetString() == change.DialoguePrompt
            && saved.GetProperty("livePrompt").GetString() == change.LivePrompt
            && saved.GetProperty("reasoningEffort").GetString() == "high",
            "actual HTTPS request accepts both complete 12000-character Czech prompts and saved reasoning");
        Check((await owner.PutAsJsonAsync(path, change)).StatusCode == HttpStatusCode.Conflict, "stale settings cannot overwrite a newer prompt revision");
        var current = change with { ExpectedRevision = saved.GetProperty("revision").GetInt64() };
        Check((await owner.PutAsJsonAsync(path, current with { ReasoningEffort = "xhigh" })).StatusCode == HttpStatusCode.BadRequest,
            "unsupported reasoning choices never reach the provider");
        foreach (var role in new[] { AccountRoles.Tester, AccountRoles.Administrator })
        {
            var invitation = await JsonAsync(await owner.PostAsJsonAsync("/app/v1/admin/invitations", new { role }));
            using var member = fixture.Client(); using var memberKey = new VirtualPasskey();
            await EnrollAsync(member, memberKey, invitation.GetProperty("token").GetString()!, role);
            await CsrfAsync(member); member.DefaultRequestHeaders.Add("X-DV-Confirm", "yes");
            Check((await member.GetAsync(path)).StatusCode == (role == AccountRoles.Administrator ? HttpStatusCode.OK : HttpStatusCode.Forbidden),
                "only real administrative roles can read production workshop settings");
            if (role == AccountRoles.Tester)
            { Check((await member.PutAsJsonAsync(path, current)).StatusCode == HttpStatusCode.Forbidden, "Tester cannot write production prompt settings"); }
        }
        using (var scope = fixture.App.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AccountDatabase>();
            await db.Sessions.Where(item => item.UserId == ownerId).ExecuteUpdateAsync(update => update.SetProperty(item => item.AuthenticatedAt, 1));
        }
        var stale = await owner.PutAsJsonAsync(path, current);
        Check(stale.StatusCode == HttpStatusCode.Forbidden && (await stale.Content.ReadAsStringAsync()).Contains("recent_passkey_required"),
            "production prompt writes require a recently presented passkey");
        var file = Path.Combine(fixture.App.Services.GetRequiredService<AccountConfiguration>().DataRoot, "workshop-settings.json");
        File.WriteAllText(file, "{corrupt prompt settings");
        var corrupt = await owner.GetAsync(path);
        Check(corrupt.StatusCode == HttpStatusCode.ServiceUnavailable && (await corrupt.Content.ReadAsStringAsync()).Contains("workshop_settings_corrupt")
            && File.ReadAllText(file) == "{corrupt prompt settings", "corrupt production settings fail explicitly without replacement");
        using (var scope = fixture.App.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AccountDatabase>();
            await db.Sessions.Where(item => item.UserId == ownerId).ExecuteUpdateAsync(update => update.SetProperty(item => item.ExpiresAt, 1));
        }
        Check((await owner.GetAsync(path)).StatusCode == HttpStatusCode.Unauthorized, "expired account sessions cannot read workshop settings");
    }
}
