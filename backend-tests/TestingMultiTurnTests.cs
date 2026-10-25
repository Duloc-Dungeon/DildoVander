using System.Diagnostics;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using Kufr.Backend.Features.Conversations;
using Kufr.Backend.Features.GptLive;
using Kufr.Backend.Features.PersonaLab;
using Kufr.Backend.Features.Realtime;
using Kufr.Backend.Features.TestingSessions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

// Real runtime, SDK protocol parsing, SQLite, testing bridge, memory and native
// virtual hardware. Only the external provider socket is controlled/offline.
internal static class TestingMultiTurnTests
{
    internal static async Task RunAsync()
    {
        foreach (var engine in new[] { GptLiveProtocol.Model })
        {
            await RunEngineAsync(engine);
        }
    }

    private static async Task RunEngineAsync(string engine)
    {
        var root = Path.Combine(Path.GetTempPath(), "tester-multiturn-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            // No configured provider credentials: accidental secondary provider
            // work fails locally instead of making a paid call.
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["Backend:DatabasePath"] = Path.Combine(root, "isolated.sqlite") }).Build();
            var baseline = LivePromptSnapshot.Create(PersonalityCatalog.OllivanderId, new GptLiveOptions());
            var database = Path.Combine(root, "lab.sqlite");
            var store = new PersonaLabStore(database, baseline);
            using var resources = new TestingResources(configuration, TimeProvider.System);
            using var hardware = new TestingHardwareAdapter(configuration);
            var sockets = new List<Socket>();
            var fault = "none";
            TaskCompletionSource<Socket>? awaitedConnection = null;
            Task<WebSocket> Connect(CancellationToken token)
            {
                var socket = new Socket(engine, "controlled-reply-" + sockets.Count, fault);
                sockets.Add(socket);
                awaitedConnection?.TrySetResult(socket);
                return Task.FromResult<WebSocket>(socket);
            }
            // Live's injected transport still requires an availability setting;
            // this fixture string never reaches a network transport.
            var liveConfiguration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["OPENAI_API_KEY"] = "offline-injected-socket-only" }).Build();
            using var live = new GptLiveClient(liveConfiguration, Connect);
            using var realtime = new OpenAiRealtimeClient(Connect);
            TestingSessionService service = null!;
            using var services = new ServiceCollection().AddSingleton(_ => service).BuildServiceProvider();
            using var runtime = new PersonaLabRuntime(store, live, realtime, testing: new TestingConversationBridge(services));
            service = new(resources, store, runtime, hardware, TimeProvider.System);
            var session = service.Create("tester", new(engine, OpenAiConsent: true, RecordContent: true,
                OperationId: Guid.NewGuid().ToString()));
            var character = await service.SaveCharacterAsync("tester", null, new(new("Mira", "Fictional clock library")), default);
            await service.SelectCharacterAsync("tester", session.Id, new(character.Id, session.Revision), default);
            if(engine==RealtimeProtocol.Model)
            {
                var context=PersonaLabRuntime.Context(store.Session("tester",session.LabSessionId),"Fictional input",out _);
                Check(context.DeferWandSelection,"Tester without prepared candidates explicitly defers selection");
            }
            var timings = new List<long>();
            var phaseTimings = new List<LabEvent>();
            for (var index = 0; index < 10; index++)
            {
                var timer = Stopwatch.StartNew();
                var result = await runtime.TurnAsync("tester", session.LabSessionId, "Fictional message " + index, default);
                timings.Add(timer.ElapsedMilliseconds);
                Check(result.Turns.Length == (index + 1) * 2, "each turn persists exactly one user and assistant");
                Check(!runtime.IsActive(session.LabSessionId) && runtime.ActiveCount == 0 && runtime.Operation(session.LabSessionId) is null,
                    "each operation releases both the guard and observable progress before the next admission");
                var detail = await service.DetailAsync("tester", session.Id, default);
                Check(!detail.ConversationActive && detail.Session.Id == session.Id, "authoritative detail is ready in the original session");
                Check(detail.Conversation.Events.Last().Type == "operation_released", "release has terminal diagnostic evidence");
                Check(detail.Conversation.Events.Last().OperationId is not null, "release retains the correlated operation ID");
                phaseTimings.AddRange(detail.Conversation.Events.Where(entry=>entry.OperationId==detail.Conversation.Events.Last().OperationId));
                Check(detail.CharacterSnapshot?.Context.Contains("Fictional clock library") == true, "frozen character context survives every turn");
                Check(resources.Wands.Read("tester", session.Id) is null, "ordinary text does not generate unrelated wand candidates");
                var reopened = new PersonaLabStore(database, baseline).Session("tester", session.LabSessionId);
                for (var turn = 0; turn <= index; turn++)
                {
                    Check(reopened.Turns[turn * 2].Text == "Fictional message " + turn
                        && reopened.Turns[turn * 2 + 1].Text == "controlled-reply-" + turn, "durable turns retain exact order and attribution");
                }
                Check(sockets[index].Commands.Contains("Fictional message " + index), "current input reaches the configured protocol");
                if(engine==RealtimeProtocol.Model)
                {
                    Check(!sockets[index].Commands.Contains("\"name\":\"propose_wand\""),"unprepared candidates never expose a blind selection tool");
                }
                if (index > 0) { Check(sockets[index].Commands.Contains("controlled-reply-" + (index - 1)), "next connection receives retained history"); }
            }

            // A provider rejection/disconnect never leaves a busy registry. A
            // canceled admitted handshake rejects overlap until cleanup finishes.
            foreach (var failure in new[] { "reject", "disconnect", "hold" })
            {
                fault = failure;
                var before = store.Session("tester", session.LabSessionId).Turns.Length;
                var connectionCreated = new TaskCompletionSource<Socket>(TaskCreationOptions.RunContinuationsAsynchronously);
                awaitedConnection = connectionCreated;
                using var cancellation = new CancellationTokenSource();
                var pending = runtime.TurnAsync("tester", session.LabSessionId, "Failure fixture", cancellation.Token);
                if (failure == "hold")
                {
                    // Admission awaits hardware before creating this connection.
                    // A previous socket's handshake cannot authorize cancellation.
                    var heldSocket = await connectionCreated.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    await heldSocket.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    Check(runtime.Operation(session.LabSessionId)?.Phase == "connecting_provider"
                        || runtime.IsActive(session.LabSessionId), "in-flight provider operation remains observable");
                    try { await runtime.TurnAsync("tester", session.LabSessionId, "Overlap", default); throw new Exception("Overlap accepted"); }
                    catch (LabException error) when (error.Code == "lab_session_busy") { }
                    cancellation.Cancel();
                }
                try { await pending; throw new Exception("Provider fault accepted"); }
                catch (LabException) { }
                awaitedConnection = null;
                Check(runtime.ActiveCount == 0 && runtime.Operation(session.LabSessionId) is null, "failure/cancellation releases operation");
                var failed = store.Session("tester", session.LabSessionId);
                Check(failed.Turns.Skip(before).All(turn => turn.Role != "assistant"), "provider failure fabricates no reply");
                Check(failed.Turns.Count(turn => turn.Text == "Overlap") == 0, "rejected overlap creates no duplicate user turn");
            }
            fault = "none";
            await runtime.TurnAsync("tester", session.LabSessionId, "Recovered original session", default);
            Check(runtime.ActiveCount == 0 && store.Session("tester", session.LabSessionId).Turns.Last().Role == "assistant",
                "recovery accepts another turn without recreating the conversation");
            fault = "hold";
            using (var timedRuntime = new PersonaLabRuntime(store, live, realtime,
                options: new PersonaLabOptions { MaxSessionSeconds = 1 }, testing: new TestingConversationBridge(services)))
            {
                try { await timedRuntime.TurnAsync("tester", session.LabSessionId, "Timeout fixture", default); throw new Exception("Timeout ignored"); }
                catch (LabException error) when (error.Code == "lab_provider_timeout") { }
                Check(timedRuntime.ActiveCount == 0 && timedRuntime.Operation(session.LabSessionId) is null, "server deadline releases active state");
            }
            fault = "none";
            var reopenedStore = new PersonaLabStore(database, baseline);
            using (var resumedRuntime = new PersonaLabRuntime(reopenedStore, live, realtime, testing: new TestingConversationBridge(services)))
            {
                service = new(resources, reopenedStore, resumedRuntime, hardware, TimeProvider.System);
                var count = reopenedStore.Session("tester", session.LabSessionId).Turns.Length;
                var result = await resumedRuntime.TurnAsync("tester", session.LabSessionId, "After application restart", default);
                Check(result.Id == session.LabSessionId && result.Turns.Length == count + 2 && resumedRuntime.ActiveCount == 0,
                    "fresh runtime resumes the original persisted conversation after timeout");
            }
            Console.WriteLine($"Tester ten-turn lifecycle {engine}: PASS; controlled socket wall ms min={timings.Min()}, max={timings.Max()}, average={timings.Average():F0}; no live provider calls.");
            Console.WriteLine("Controlled phase mean ms: "+string.Join(", ",phaseTimings.Where(entry=>entry.LatencyMs.HasValue)
                .GroupBy(entry=>entry.Type).Select(group=>$"{group.Key}={group.Average(entry=>entry.LatencyMs!.Value):F0}")));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(root, true);
        }
    }

    private static void Check(bool condition, string message)
    { if (!condition) { throw new InvalidOperationException(message); } }

    private sealed class Socket(string engine, string reply, string fault) : WebSocket
    {
        private readonly Channel<byte[]> incoming = Channel.CreateUnbounded<byte[]>();
        private byte[]? fragment;
        private int offset;
        private bool created;
        private bool disposed;
        internal string Commands { get; private set; } = "";
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => disposed ? WebSocketState.Closed : WebSocketState.Open;
        public override string? SubProtocol => null;
        public override void Abort() { disposed = true; }
        public override void Dispose() { disposed = true; }
        public override Task CloseAsync(WebSocketCloseStatus status, string? description, CancellationToken token) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus status, string? description, CancellationToken token) => Task.CompletedTask;
        private void Push(object value) => incoming.Writer.TryWrite(JsonSerializer.SerializeToUtf8Bytes(value));
        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType type, bool complete, CancellationToken token)
        {
            using var document = JsonDocument.Parse(buffer);
            var value = document.RootElement;
            Commands += value.GetRawText();
            var command = value.GetProperty("type").GetString();
            if (command is "session.start" or "session.update")
            {
                Started.TrySetResult();
                if (fault == "disconnect") { throw new WebSocketException("controlled disconnect"); }
                if (fault == "reject") { Push(new { type = "error", error = new { type = "rate_limit_error", code = "rate_limit_exceeded", message = "private provider detail" } }); }
                else if (fault != "hold" && engine == GptLiveProtocol.Model)
                {
                    Push(new { type = "session.started", client_event_id = value.GetProperty("event_id").GetString(), session = new { model = engine, id = "controlled-live" } });
                    Push(new { type = "session.output_transcript.delta", delta = reply });
                }
                else if (fault != "hold") { Push(new { type = "session.updated", session = new { type = "realtime", model = engine } }); }
            }
            if (command == "response.create")
            {
                Push(new { type = "response.created", response = new { id = "controlled-response" } });
                Push(new { type = "response.output_audio.delta", response_id = "controlled-response", item_id = "controlled-assistant",
                    content_index = 0, delta = Convert.ToBase64String(new byte[4800]) });
                Push(new { type = "response.done", response = new { id = "controlled-response", status = "completed",
                    output = new[] { new { type = "message", role = "assistant", status = "completed", content = new[] { new { type = "output_audio", transcript = reply } } } },
                    usage = new { input_tokens = 2, output_tokens = 2, input_token_details = new { text_tokens = 2, audio_tokens = 0 } } } });
            }
            if (command == "session.close")
            { Push(new { type = "session.closed", client_event_id = value.GetProperty("event_id").GetString(), reason = "close_requested", usage = new { seconds = 1.2 } }); }
            return Task.CompletedTask;
        }
        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken token)
        {
            if (!created && engine == RealtimeProtocol.Model)
            { created = true; Push(new { type = "session.created", session = new { type = "realtime", model = engine } }); }
            fragment ??= await incoming.Reader.ReadAsync(token);
            var count = Math.Min(buffer.Count, fragment.Length - offset);
            fragment.AsSpan(offset, count).CopyTo(buffer.AsSpan());
            offset += count;
            var complete = offset == fragment.Length;
            if (complete) { fragment = null; offset = 0; }
            return new(count, WebSocketMessageType.Text, complete);
        }
    }
}
