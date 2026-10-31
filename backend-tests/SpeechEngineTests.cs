using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Kufr.Backend.Features.Customers;
using Kufr.Backend.Features.Identity;
using Kufr.Backend.Features.PersonaLab;
using Kufr.Backend.Features.Voices;
using Kufr.Backend.Features.Wands;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

internal static class SpeechEngineTests
{
    private const string ApiKey = "synthetic-offline-speech-key";
    private const long Now = 1_700_000_000;

    internal static async Task RunAsync()
    {
        JwtConformance();
        await RecognitionAsync();
        await ActivityAndReplyFencesAsync();
        await CompletionReplyDoesNotDuplicateAsync();
        await RuntimeQueuedCommitFenceAsync();
        await ConcreteRuntimeOpeningAndVerdictAsync();
        await RejectedMessagesAsync();
        await PreparationAsync();
        Console.WriteLine("Speech Engine JWT, upstream history/turn fencing, final chunks, private provisioning and cleanup passed (offline).");
    }

    private static void JwtConformance()
    {
        // Official SDK resource.py: raw SHA-256 digest bytes, not hex or the API key itself.
        var valid = Jwt(Claims());
        Check(SpeechEngineProtocol.Verify(valid, ApiKey, Now), "official SDK HMAC derivation accepts a valid token");
        Check(SpeechEngineProtocol.Verify("  bEaReR " + valid + "  ", ApiKey + "_residency_eu", Now),
            "Bearer and residency suffix use the documented normalized signing key");
        Check(SpeechEngineProtocol.Verify("Bearer   " + valid, ApiKey, Now), "Bearer prefix whitespace follows SDK normalization");
        Check(!SpeechEngineProtocol.Verify(valid, ApiKey + "wrong", Now), "another API key cannot authenticate upstream");
        Check(!SpeechEngineProtocol.Verify(Jwt(Claims(), hashKey: false), ApiKey, Now), "raw API-key HMAC is rejected");
        Check(!SpeechEngineProtocol.Verify(Jwt(Claims(), hexKey: true), ApiKey, Now), "hex-encoded digest HMAC is rejected");
        var expiryBoundary = Claims(); expiryBoundary["iat"] = Now - 3600; expiryBoundary["exp"] = Now - 60;
        Check(SpeechEngineProtocol.Verify(Jwt(expiryBoundary), ApiKey, Now), "documented 60-second expiry leeway is retained");
        expiryBoundary["exp"] = Now - 61;
        Check(!SpeechEngineProtocol.Verify(Jwt(expiryBoundary), ApiKey, Now), "expiry fails independently of the issued-at ordering gate");
        foreach (var (claim, value) in new (string, object?)[] {
            ("iss", "https://untrusted.invalid"), ("sub", "another-subject"),
            ("exp", Now - 61), ("iat", Now + 61), ("iat", Now + 601),
            ("nbf", Now + 61), ("exp", "tomorrow"), ("iat", null), ("nbf", "later") })
        {
            var claims = Claims(); claims[claim] = value;
            Check(!SpeechEngineProtocol.Verify(Jwt(claims), ApiKey, Now), "invalid " + claim + " fails closed");
        }
        foreach (var missing in new[] { "iss", "sub", "exp", "iat" })
        {
            var claims = Claims(); claims.Remove(missing);
            Check(!SpeechEngineProtocol.Verify(Jwt(claims), ApiKey, Now), "missing " + missing + " fails closed");
        }
        Check(!SpeechEngineProtocol.Verify(Jwt(Claims(), new { alg = "none" }), ApiKey, Now), "unsigned algorithm is rejected");
        Check(!SpeechEngineProtocol.Verify(Jwt(Claims(), new { alg = "HS512" }), ApiKey, Now), "algorithm substitution is rejected");
        Check(!SpeechEngineProtocol.Verify(Jwt(Claims(), new { alg = "HS256", crit = new[] { "extension" } }), ApiKey, Now),
            "unrecognized critical JWT extensions are rejected");
        foreach (var malformed in new string?[] { null, "", ".", "a.b.c", valid + ".extra", new('a', 8193) })
        { Check(!SpeechEngineProtocol.Verify(malformed, ApiKey, Now), "malformed or unbounded JWT fails closed"); }
        Check(!SpeechEngineProtocol.Verify(valid, null, Now), "missing provider credentials never disable verification");
        Check(SpeechEngineProtocol.EventId(null) == 0 && SpeechEngineProtocol.EventId("12") == 12, "opening and committed event IDs parse");
        foreach (var invalid in new[] { "-1", "1.0", " 1", "9223372036854775808" })
        { Throws<InvalidDataException>(() => SpeechEngineProtocol.EventId(invalid), "invalid response event IDs fail closed"); }
    }

    private static Dictionary<string, object?> Claims() => new()
    {
        ["iss"] = "https://api.elevenlabs.io/convai/speech-engine", ["sub"] = "convai_speech_engine_upstream",
        ["iat"] = Now - 10, ["exp"] = Now + 600, ["nbf"] = Now - 10
    };

    private static string Jwt(Dictionary<string, object?> claims, object? header = null, bool hashKey = true, bool hexKey = false)
    {
        var body = WebEncoders.Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(header ?? new { alg = "HS256", typ = "JWT" }))
            + "." + WebEncoders.Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(claims));
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(ApiKey));
        var key = hexKey ? Encoding.ASCII.GetBytes(Convert.ToHexStringLower(digest)) : hashKey ? digest : Encoding.UTF8.GetBytes(ApiKey);
        return body + "." + WebEncoders.Base64UrlEncode(HMACSHA256.HashData(key, Encoding.ASCII.GetBytes(body)));
    }

    private static async Task RecognitionAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var socket = new EngineSocket();
        using var client = new EngineSocket();
        await using var recognition = new SpeechEngineRecognition(socket, deadline.Token);
        socket.Message(new { type = "init", conversation_id = "offline-conversation" });
        await recognition.Ready.Task.WaitAsync(deadline.Token);
        socket.Message(new { type = "ping" });
        await UntilAsync(() => socket.Sent.Any(json => Type(json) == "pong"), deadline.Token);
        socket.Message(Turn(1, "Current guest words."));
        Check(await recognition.ReceiveAsync(deadline.Token) is { Committed: true, EventId: "1", Text: "Current guest words." },
            "only newest user text from provider history enters the recognition stream");
        var provider = new SpeechEngineSpeechProvider(recognition, new LabSocketSender(client));
        var oldReply = provider.ForReply("1");
        await oldReply.SpeakAsync("First clause.", (_, _) => throw new Exception("WebRTC must own audio"), deadline.Token);
        socket.Message(Turn(2, "Corrected guest words."));
        Check(await recognition.ReceiveAsync(deadline.Token) is { EventId: "2", Text: "Corrected guest words." }, "newer turn replaces inference authority");
        var sentBeforeStale = socket.Sent.Count;
        await oldReply.SpeakAsync("Stale clause.", (_, _) => Task.CompletedTask, deadline.Token);
        await oldReply.CompleteReplyAsync(deadline.Token);
        Check(socket.Sent.Count == sentBeforeStale, "frozen old reply cannot be relabeled or finalized with a newer ID");
        socket.Message(Turn(2, "Duplicate must not enter."));
        socket.Message(Turn(1, "Older must not enter."));
        socket.Message(Turn(3, "Third accepted turn."));
        Check(await recognition.ReceiveAsync(deadline.Token) is { EventId: "3", Text: "Third accepted turn." }, "duplicate and stale history cannot append evidence");
        var reply = provider.ForReply("3");
        await reply.SpeakAsync("One sentence.", (_, _) => Task.CompletedTask, deadline.Token);
        await reply.SpeakAsync(" Another sentence.", (_, _) => Task.CompletedTask, deadline.Token);
        await reply.CompleteReplyAsync(deadline.Token);
        var chunks = socket.Sent.Select(json => JsonSerializer.Deserialize<JsonElement>(json))
            .Where(value => value.GetProperty("type").GetString() == "agent_response" && value.GetProperty("event_id").GetInt64() == 3).ToArray();
        Check(chunks.Length == 3 && chunks[0].GetProperty("content").GetString() == "One sentence."
            && !chunks[0].GetProperty("is_final").GetBoolean()
            && chunks[1].GetProperty("content").GetString() == " Another sentence."
            && chunks[2].GetProperty("content").GetString() == "" && chunks[2].GetProperty("is_final").GetBoolean(),
            "response chunks retain exact text and terminate with a separate final marker");
        var finalizedCount = socket.Sent.Count;
        await reply.CompleteReplyAsync(deadline.Token);
        await recognition.SendAsync(3, "Late same-turn content must not reopen speech.", false, deadline.Token);
        await reply.SpeakAsync("Another late clause.", (_, _) => Task.CompletedTask, deadline.Token);
        Check(socket.Sent.Count == finalizedCount, "duplicate finalization and later same-event content cannot reopen a finalized engine reply");
        var notifications = client.Sent.Select(json => JsonSerializer.Deserialize<JsonElement>(json))
            .Where(value => value.GetProperty("type").GetString() == "speech_engine_speaking").ToArray();
        Check(notifications.Length == 2 && notifications[0].GetProperty("sequence").GetInt64() > 0
            && notifications[1].GetProperty("sequence").GetInt64() > notifications[0].GetProperty("sequence").GetInt64(),
            "playback notifications carry positive monotonic IDs across replies");
        recognition.Activity(4); recognition.Activity(4);
        Check(await recognition.ReceiveAsync(deadline.Token) is { Committed: false, SpeechStarted: true, EventId: "4" },
            "versioned activity interrupts once without becoming user evidence");
        socket.Message(Turn(4, "After activity."));
        Check(await recognition.ReceiveAsync(deadline.Token) is { EventId: "4" }, "activity does not replace the next committed transcript");
        socket.Message(new
        {
            type = "user_transcript", event_id = 5,
            user_transcript = new[] { new { role = "user", content = new string('x', 20_000) },
                new { role = "user", content = "Latest committed guest words." },
                new { role = "agent", content = "Provider assistant history must not become evidence." } }
        });
        Check(await recognition.ReceiveAsync(deadline.Token) is { EventId: "5", Text: "Latest committed guest words." },
            "official trailing-agent history over 16 KiB selects only the newest user within the 64 KiB provider bound");
        await ThrowsAsync<InvalidDataException>(() => recognition.SendAudioAsync([1, 2], deadline.Token), "PCM cannot bypass the WebRTC transport");
    }

    private static async Task ActivityAndReplyFencesAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var socket = new EngineSocket();
        await using var recognition = new SpeechEngineRecognition(socket, deadline.Token);
        socket.Message(new { type = "init", conversation_id = "activity-fence-fixture" });
        await recognition.Ready.Task.WaitAsync(deadline.Token);
        socket.Message(Turn(4, "Fourth committed turn."));
        Check(await recognition.ReceiveAsync(deadline.Token) is { EventId: "4" }, "initial committed turn enters the consumer");
        recognition.Activity(4); recognition.Activity(3);
        recognition.Activity(6); recognition.Activity(6); recognition.Activity(5);
        Check(await recognition.ReceiveAsync(deadline.Token) is { SpeechStarted: true, EventId: "6" },
            "late committed activity is ignored and only the highest new activity is queued");
        socket.Message(Turn(5, "Fifth commit trails newer speech."));
        Check(await recognition.ReceiveAsync(deadline.Token) is { Committed: true, EventId: "5", MoreSpeechPending: true }
            && recognition.HasPendingAfter("5") && !recognition.IsCurrent(5),
            "future activity six remains pending when older commit five arrives");
        var sent = socket.Sent.Count;
        await recognition.SendAsync(5, "Must not speak while six is pending.", false, deadline.Token);
        Check(socket.Sent.Count == sent, "newer activity prevents stale synthesis before its transcript commits");
        socket.Message(Turn(6, "Sixth committed turn."));
        Check(await recognition.ReceiveAsync(deadline.Token) is { EventId: "6", MoreSpeechPending: false },
            "matching commit resolves its own pending activity");
        recognition.Activity(6); recognition.Activity(5); recognition.Activity(long.MaxValue);
        var previousPongs = socket.Sent.Count(json => Type(json) == "pong");
        socket.Message(Turn(7, "Seventh turn stays queued."));
        socket.Message(new { type = "ping" });
        await UntilAsync(() => socket.Sent.Count(json => Type(json) == "pong") > previousPongs, deadline.Token);
        var sideEffects = 0;
        using (var stale = recognition.EnterReply(6)) { if (stale is not null) { sideEffects++; } }
        Check(sideEffects == 0, "newer queued commit invalidates an old reply before the runtime dequeues it");
        Check(await recognition.ReceiveAsync(deadline.Token) is { EventId: "7", MoreSpeechPending: false },
            "late activity creates no spurious turn and cannot poison newer committed input");
        var current = recognition.EnterReply(7);
        Check(current is not null, "current reply can acquire a durable side-effect lease");
        sideEffects++;
        current!.Dispose(); current.Dispose();
        using (var reacquired = recognition.EnterReply(7))
        {
            Check(sideEffects == 1 && reacquired is not null, "reply lease disposal is idempotent and releases the gate exactly once");
        }
        recognition.CompleteConversation();
        recognition.Activity(8);
        socket.Message(Turn(8, "Late committed words after saved wand."));
        await Task.Delay(50, deadline.Token);
        Check(recognition.LatestEventId == 8 && !recognition.HasPendingAfter("8"),
            "completion ignores late transcript evidence while retaining the provider's latest turn ownership");
        var verdict = new SpeechEngineSpeechProvider(recognition, new LabSocketSender(socket)).ForCompletion(8);
        await verdict.SpeakAsync("Saved wand verdict.", (_, _) => Task.CompletedTask, deadline.Token);
        socket.Message(Turn(9, "Transcript after final phrase began."));
        await UntilAsync(() => recognition.LatestEventId == 9, deadline.Token);
        await verdict.CompleteReplyAsync(deadline.Token);
        var retry = new SpeechEngineSpeechProvider(recognition, new LabSocketSender(socket)).ForCompletion(9);
        await retry.SpeakAsync("Saved wand verdict.", (_, _) => Task.CompletedTask, deadline.Token);
        await retry.CompleteReplyAsync(deadline.Token);
        Check(socket.Sent.Any(json =>
        {
            using var message = JsonDocument.Parse(json);
            return message.RootElement.TryGetProperty("is_final", out var final) && final.GetBoolean()
                && message.RootElement.GetProperty("event_id").GetInt64() == 9;
        }) && socket.Sent.Any(json =>
        {
            using var message = JsonDocument.Parse(json);
            return message.RootElement.TryGetProperty("content", out var content) && content.GetString() == "Saved wand verdict."
                && message.RootElement.GetProperty("event_id").GetInt64() == 9;
        }) && !socket.Sent.Any(json =>
        {
            using var message = JsonDocument.Parse(json);
            return message.RootElement.TryGetProperty("is_final", out var final) && final.GetBoolean()
                && message.RootElement.GetProperty("event_id").GetInt64() == 8;
        }), "saved verdict retries its complete text on a newer provider turn and never finalizes a stale turn");
    }

    private static async Task CompletionReplyDoesNotDuplicateAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var upstream = new EngineSocket();
        using var client = new EngineSocket();
        await using var recognition = new SpeechEngineRecognition(upstream, deadline.Token);
        upstream.Message(new { type = "init", conversation_id = "synthetic-conversation" });
        await recognition.Ready.Task.WaitAsync(deadline.Token);
        upstream.Message(Turn(1, "Accepted before completion."));
        Check(await recognition.ReceiveAsync(deadline.Token) is { EventId: "1" }, "accepted event available before completion");
        recognition.CompleteConversation();
        var verdict = new SpeechEngineSpeechProvider(recognition, new LabSocketSender(client)).ForCompletion(1);
        await verdict.SpeakAsync("Saved wand verdict.", (_, _) => Task.CompletedTask, deadline.Token);
        await verdict.CompleteReplyAsync(deadline.Token);
        var contentCount = upstream.Sent.Select(json => JsonDocument.Parse(json)).Count(document =>
            document.RootElement.GetProperty("type").GetString() == "agent_response"
            && document.RootElement.GetProperty("content").GetString() == "Saved wand verdict.");
        Check(contentCount == 1, "completion finalization does not replay verdict text when provider ownership stayed on the same turn");
    }

    private static async Task RuntimeQueuedCommitFenceAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "dv-engine-runtime-fence-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            using var upstream = new EngineSocket();
            using var client = new EngineSocket();
            await using var recognition = new SpeechEngineRecognition(upstream, lifetime.Token);
            var held = new HeldRecognition(recognition);
            var leaseCheck = new ReplyLeaseCheck();
            var speech = new HeldSpeechProvider(new SpeechEngineSpeechProvider(recognition, new LabSocketSender(client)), held, leaseCheck);
            var dialogue = new QueuedReadyDialogue();
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["Backend:DatabasePath"] = Path.Combine(root, "wands.sqlite") }).Build();
            var wands = new WandGenerationStore(configuration, TimeProvider.System);
            var profile = new CharacterConversationSnapshot("profile", 1, new("Raven", "Synthetic"),
                new("Raven", "Synthetic", "A prepared fixture", [new("Finds maps", ["https://example.test"])],
                    [], [], false, [], "synthetic", "test", 1, 1, "synthetic"), 1);
            wands.BeginMobile("owner", "operation", profile, 0);
            wands.BeginWorkshop("owner", "operation", profile, 0, "group", 1);
            var admission = new MobileWandAdmission("owner", "operation", "session", "stamp", "https://localhost",
                DateTimeOffset.UtcNow.AddMinutes(1), profile) { ElevenLabsConsent = true };
            var runtime = new ElevenLabsWorkshopRuntime(wands, speech, dialogue, NullLogger<ElevenLabsWorkshopRuntime>.Instance);
            var run = runtime.RunAsync(admission, client, _ => Task.CompletedTask, lifetime.Token);
            try
            {
                upstream.Message(new { type = "init", conversation_id = "durable-runtime-fence" });
                await UntilAsync(() => wands.ReadWorkshopMessages("owner", "operation").Any(message => message.Role == "assistant"), lifetime.Token);
                upstream.Message(Turn(1, "I protect my friends."));
                await dialogue.FirstDeciding.Task.WaitAsync(lifetime.Token);
                upstream.Message(Turn(2, "Wait, I also need to protect the bridge."));
                await held.NewerCommitHeld.Task.WaitAsync(lifetime.Token);
                dialogue.ReleaseFirst.TrySetResult();
                await leaseCheck.OldReplyChecked.Task.WaitAsync(lifetime.Token);
                Check(!leaseCheck.OldReplyAllowed, "runtime obtains no durable lease for a ready draft superseded before dequeue");
                held.ReleaseConsumer.TrySetResult();
                await dialogue.SecondDeciding.Task.WaitAsync(lifetime.Token);
                var messages = wands.ReadWorkshopMessages("owner", "operation");
                Check(wands.ReadWorkshopState("owner", "operation").Status == "asking"
                    && messages.All(message => message.Role != "assistant" || message.Text != QueuedReadyDialogue.SupersededReply)
                    && messages.Count(message => message.Role == "user") == 2,
                    "queued correction blocks stale assistant append and manufacture while preserving both real user commits");
            }
            finally
            {
                held.ReleaseConsumer.TrySetResult(); lifetime.Cancel();
                await run.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class HeldRecognition(IWorkshopRecognitionConnection inner) : IWorkshopRecognitionConnection
    {
        internal TaskCompletionSource NewerCommitHeld { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseConsumer { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task SendAudioAsync(byte[] pcm, CancellationToken token) => inner.SendAudioAsync(pcm, token);
        public async Task<WorkshopRecognition?> ReceiveAsync(CancellationToken token)
        {
            var item = await inner.ReceiveAsync(token);
            if (item is { Committed: true, EventId: "2" })
            { NewerCommitHeld.TrySetResult(); await ReleaseConsumer.Task.WaitAsync(token); }
            return item;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ReplyLeaseCheck
    {
        internal TaskCompletionSource OldReplyChecked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool OldReplyAllowed;
    }

    private sealed class HeldSpeechProvider(IWorkshopSpeechProvider inner, HeldRecognition recognition, ReplyLeaseCheck check,
        string? eventId = null) : IWorkshopSpeechProvider
    {
        public bool IsCurrentReply => inner.IsCurrentReply;
        public WorkshopReplyLease? EnterReply()
        {
            var lease = inner.EnterReply();
            if (eventId == "1") { check.OldReplyAllowed = lease is not null; check.OldReplyChecked.TrySetResult(); }
            return lease;
        }
        public IWorkshopSpeechProvider ForReply(string? id) => new HeldSpeechProvider(inner.ForReply(id), recognition, check, id);
        public async Task<IWorkshopRecognitionConnection> ConnectAsync(CancellationToken token)
        { await inner.ConnectAsync(token); return recognition; }
        public Task SpeakAsync(string text, Func<byte[], CancellationToken, Task> audio, CancellationToken token) => inner.SpeakAsync(text, audio, token);
        public Task CompleteReplyAsync(CancellationToken token) => inner.CompleteReplyAsync(token);
    }

    private sealed class QueuedReadyDialogue : IMobileWandQuestionProvider
    {
        internal const string SupersededReply = "I have enough to choose your wand.";
        internal TaskCompletionSource FirstDeciding { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseFirst { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource SecondDeciding { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<MobileWandDialogueDraft> ReplyAsync(CharacterConversationSnapshot profile,
            MobileWandWorkshopMessage[] messages, CancellationToken token)
        {
            var count = messages.Count(message => message.Role == "user");
            if (count == 0) { return new("What protects your bridge?", false); }
            if (count == 1)
            {
                FirstDeciding.TrySetResult(); await ReleaseFirst.Task.WaitAsync(token);
                return new(SupersededReply, true);
            }
            SecondDeciding.TrySetResult();
            return new("What else should I know about the bridge?", false);
        }
    }

    private static async Task ConcreteRuntimeOpeningAndVerdictAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "dv-engine-verdict-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            using var upstream = new EngineSocket();
            using var client = new EngineSocket();
            // RunAsync owns disposal of the concrete connected recognition stream.
            var recognition = new SpeechEngineRecognition(upstream, lifetime.Token);
            var speech = new SpeechEngineSpeechProvider(recognition, new LabSocketSender(client));
            var dialogue = new CommittedOnlyDialogue();
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["Backend:DatabasePath"] = Path.Combine(root, "wands.sqlite") }).Build();
            var wands = new WandGenerationStore(configuration, TimeProvider.System);
            var profile = new CharacterConversationSnapshot("profile", 1, new("Raven", "Synthetic"),
                new("Raven", "Synthetic", "A prepared fixture", [new("Finds maps", ["https://example.test"])],
                    [], [], false, [], "synthetic", "test", 1, 1, "synthetic"), 1);
            wands.BeginMobile("owner", "operation", profile, 0);
            wands.BeginWorkshop("owner", "operation", profile, 0, "group", 1);
            wands.AppendWorkshopMessage("owner", "operation", Guid.NewGuid().ToString("N"),
                "assistant", "Saved canonical opening.", "voice");
            var admission = new MobileWandAdmission("owner", "operation", "session", "stamp", "https://localhost",
                DateTimeOffset.UtcNow.AddMinutes(1), profile) { ElevenLabsConsent = true };
            var runtime = new ElevenLabsWorkshopRuntime(wands, speech, dialogue, NullLogger<ElevenLabsWorkshopRuntime>.Instance);
            var run = runtime.RunAsync(admission, client, _ => Task.CompletedTask, lifetime.Token);
            try
            {
                upstream.Message(new { type = "init", conversation_id = "concrete-opening-verdict" });
                await UntilAsync(() => client.Sent.Any(json => Type(json) == "ready"), lifetime.Token);
                upstream.Message(new { type = "ping" });
                await UntilAsync(() => upstream.Sent.Any(json => Type(json) == "pong"), lifetime.Token);
                Check(dialogue.Calls == 0 && !upstream.Sent.Any(json => Type(json) == "agent_response"),
                    "concrete engine waits for a real user and sends no unsupported event-zero greeting");
                upstream.Message(Turn(1, "Protect the bridge."));
                await UntilAsync(() => wands.ReadWorkshopState("owner", "operation").Status == "generating", lifetime.Token);
                Check(dialogue.Calls == 1 && wands.ReadWorkshopMessages("owner", "operation")
                    .Count(message => message.Text == "Saved canonical opening.") == 1,
                    "first committed user triggers one dialogue and the already saved opening is reused");
                await UntilAsync(() => upstream.Sent.Any(json => Type(json) == "agent_response"), lifetime.Token);
                wands.Begin("owner", "operation", profile, 1, true);
                var job = wands.Claim("owner", "operation", false) ?? throw new InvalidOperationException("Missing synthetic generation job");
                wands.Complete(job, MobileWandTests.Candidates().Take(1).ToArray());
                var wand = wands.Select("owner", "operation", profile.ProfileId, profile.Revision, 1);
                await UntilAsync(() => upstream.Sent.Select(json => JsonSerializer.Deserialize<JsonElement>(json))
                    .Any(value => value.GetProperty("type").GetString() == "agent_response" && value.GetProperty("is_final").GetBoolean()), lifetime.Token);
                var responses = upstream.Sent.Select(json => JsonSerializer.Deserialize<JsonElement>(json))
                    .Where(value => value.GetProperty("type").GetString() == "agent_response").ToArray();
                Check(responses.Length >= 2 && responses.All(value => value.GetProperty("event_id").GetInt64() == 1)
                    && responses.Count(value => value.GetProperty("is_final").GetBoolean()) == 1
                    && responses[^1].GetProperty("is_final").GetBoolean()
                    && string.Concat(responses.Select(value => value.GetProperty("content").GetString())).Contains(wand.Name, StringComparison.Ordinal),
                    "manufacture keeps the accepted turn open until the real saved-wand verdict, then emits one final marker");
                client.Message(new { type = "speech_engine_activity", eventId = 2 });
                upstream.Message(Turn(2, "Late speech after completion."));
                await UntilAsync(() => upstream.Sent.Select(json => JsonSerializer.Deserialize<JsonElement>(json))
                    .Any(value => value.GetProperty("type").GetString() == "agent_response"
                        && value.GetProperty("event_id").GetInt64() == 2 && value.GetProperty("is_final").GetBoolean()), lifetime.Token);
                var responsesAfterLateSpeech = upstream.Sent.Select(json => JsonSerializer.Deserialize<JsonElement>(json))
                    .Where(value => value.GetProperty("type").GetString() == "agent_response").ToArray();
                var replay = responsesAfterLateSpeech.Where(value => value.GetProperty("event_id").GetInt64() == 2).ToArray();
                Check(replay.Length >= 2 && replay[^1].GetProperty("is_final").GetBoolean()
                    && string.Concat(replay.Select(value => value.GetProperty("content").GetString()))
                        .Contains(wand.Name, StringComparison.Ordinal)
                    && wands.ReadWorkshopMessages("owner", "operation").Count(message => message.Role == "user") == 1,
                    "the real completion Guard replays the whole saved verdict on a later provider-owned turn without persisting late speech");
            }
            finally { lifetime.Cancel(); await run.WaitAsync(TimeSpan.FromSeconds(5)); }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class CommittedOnlyDialogue : IMobileWandQuestionProvider
    {
        internal int Calls;
        public Task<MobileWandDialogueDraft> ReplyAsync(CharacterConversationSnapshot profile,
            MobileWandWorkshopMessage[] messages, CancellationToken token)
        {
            Check(messages.Any(message => message.Role == "user"), "engine must not request a second opening dialogue");
            Interlocked.Increment(ref Calls);
            return Task.FromResult(new MobileWandDialogueDraft("I have enough to choose your wand.", true));
        }
    }

    private static object Turn(long id, string text) => new
    {
        type = "user_transcript", event_id = id,
        user_transcript = new[] { new { role = "user", content = "Hidden old history" },
            new { role = "assistant", content = "Untrusted assistant manufacture claim" }, new { role = "user", content = text } }
    };

    private static async Task RejectedMessagesAsync()
    {
        var invalidMessages = new[] {
            JsonSerializer.Serialize(new { type = "init", conversation_id = "duplicate-init" }),
            JsonSerializer.Serialize(new { type = "user_transcript", event_id = 0, user_transcript = new[] { new { role = "user", content = "zero" } } }),
            JsonSerializer.Serialize(new { type = "user_transcript", event_id = 1, user_transcript = new[] { new { role = "assistant", content = "not a guest" } } }),
            JsonSerializer.Serialize(Turn(1, new string('x', 1801))),
            JsonSerializer.Serialize(new { type = "user_transcript", event_id = 1, user_transcript = Array.Empty<object>() }),
            JsonSerializer.Serialize(new { type = "user_transcript", event_id = 1, user_transcript = Enumerable.Repeat(new { role = "user", content = "x" }, 129).ToArray() }),
            "{", JsonSerializer.Serialize(new { type = "unknown" }), new string('x', 300_000)
        };
        foreach (var message in invalidMessages)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var socket = new EngineSocket();
            await using var recognition = new SpeechEngineRecognition(socket, deadline.Token);
            socket.Message(new { type = "init", conversation_id = "bounded-fixture" });
            await recognition.Ready.Task.WaitAsync(deadline.Token);
            socket.Raw(message);
            try { await recognition.ReceiveAsync(deadline.Token); throw new Exception("Malformed engine input was accepted"); }
            catch (Exception error) when (error is InvalidDataException or JsonException or InvalidOperationException or KeyNotFoundException or WandGenerationException) { }
        }
        using var beforeInit = new EngineSocket();
        await using var uninitialized = new SpeechEngineRecognition(beforeInit, CancellationToken.None);
        beforeInit.Message(Turn(1, "Before init"));
        await ThrowsAsync<InvalidDataException>(() => uninitialized.Ready.Task.WaitAsync(TimeSpan.FromSeconds(5)), "transcript before provider init fails closed");
    }

    private static async Task PreparationAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "dv-speech-engine-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ApplicationIdentity:DataRoot"] = root, ["ELEVENLABS_API_KEY"] = ApiKey,
                ["Wands:Voice:SpeechEnginePublicOrigin"] = "https://engine.example.test"
            }).Build();
            var voices = new ActorVoiceSettings(configuration, new(true, root, Path.Combine(root, "keys"), "localhost", ["https://localhost"]));
            voices.Save(new(0, "SelectedKufrVoice", "IndependentBaron"));
            var handler = new EngineHandler();
            using var sessions = new SpeechEngineSessions(configuration, ElevenLabsWorkshopOptions.Read(configuration), voices,
                NullLogger<SpeechEngineSessions>.Instance, new HttpClient(handler));
            await sessions.StartAsync(CancellationToken.None);
            var profile = new CharacterConversationSnapshot("profile", 1, new("Private guest", "Synthetic profile"), null, 1);
            var admission = new MobileWandAdmission("owner", "operation", "session", "stamp", "https://localhost",
                DateTimeOffset.UtcNow.AddMinutes(1), profile) { ElevenLabsConsent = true };
            const string opening = "Canonical opening already saved.";
            var prepared = await sessions.PrepareAsync(admission, CancellationToken.None, opening);
            Check(prepared.Key.Length == 64 && prepared.Key.All(char.IsAsciiHexDigit)
                && prepared.ConversationToken == EngineHandler.Token && prepared.FirstMessage == opening,
                "server returns a bounded random capability, opaque browser token and exact saved opening");
            using var created = JsonDocument.Parse(handler.Created.Single());
            var body = created.RootElement;
            Check(body.GetProperty("conversation").GetProperty("max_duration_seconds").GetInt32() == WorkshopVoiceLimits.ProviderSeconds
                && WorkshopVoiceLimits.ProviderSeconds >= WorkshopVoiceLimits.ConversationSeconds + 120
                && WorkshopVoiceLimits.LeaseSeconds > WorkshopVoiceLimits.ProviderSeconds,
                "provider playback tail remains inside the exclusive owner lease");
            Check(body.GetProperty("overrides").GetProperty("first_message").GetBoolean(),
                "resource explicitly permits the browser's first-message override");
            Check(body.GetProperty("language").GetString() == "cs"
                && body.GetProperty("tts").GetProperty("model_id").GetString() == "eleven_v4"
                && body.GetProperty("tts").GetProperty("voice_id").GetString() == "SelectedKufrVoice"
                && body.GetProperty("tts").GetProperty("speed").GetDouble() == 1.0,
                "actual create request uses selected Kufr voice with normal Czech v4 settings");
            Check(body.GetProperty("speech_engine").GetProperty("ws_url").GetString()
                == "wss://engine.example.test/app/v1/customer/wand/speech-engine/" + prepared.Key,
                "callback binds an unguessable server capability without guest identifiers");
            Check(body.GetProperty("vad").GetProperty("background_voice_detection").GetBoolean()
                && body.GetProperty("turn").GetProperty("turn_eagerness").GetString() == "patient",
                "workshop filters background speakers and waits through conversational pauses");
            Check(!body.GetProperty("turn").GetProperty("speculative_turn").GetBoolean()
                && !body.GetProperty("privacy").GetProperty("record_voice").GetBoolean()
                && body.GetProperty("privacy").GetProperty("retention_days").GetInt32() == 0
                && body.GetProperty("privacy").GetProperty("delete_audio").GetBoolean()
                && body.GetProperty("privacy").GetProperty("delete_transcript_and_pii").GetBoolean(),
                "provisioning disables speculative turns and retained audio/transcript data");
            Check(!handler.Created.Single().Contains("Private guest") && handler.AuthenticatedRequests == 2,
                "only server API authentication is sent and profile history is excluded from provisioning");
            await ThrowsCodeAsync(() => sessions.PrepareAsync(admission, CancellationToken.None), "wand_session_busy");
            var bound = admission with { SpeechEngineKey = prepared.Key };
            foreach (var tampered in new[] { bound with { Owner = "foreign" }, bound with { Operation = "other-operation" }, bound with { AccountSession = "other-session" } })
            { ThrowsCode(() => sessions.Require(tampered), "speech_engine_session_expired"); }
            Check(sessions.Require(bound).Admission == admission, "tampering does not consume the owner's one-time control admission");
            ThrowsCode(() => sessions.Require(bound), "speech_engine_session_expired");
            await sessions.StopAsync(CancellationToken.None);
            Check(handler.Deletes == 1 && new SpeechEngineCleanupStore(configuration).Read().Length == 0,
                "normal shutdown deletes the remote resource and clears durable cleanup state");
            ThrowsCode(() => sessions.Require(bound), "speech_engine_session_expired");

            var failureHandler = new EngineHandler { RejectToken = true };
            using var failed = new SpeechEngineSessions(configuration, ElevenLabsWorkshopOptions.Read(configuration), voices,
                NullLogger<SpeechEngineSessions>.Instance, new HttpClient(failureHandler));
            await ThrowsCodeAsync(() => failed.PrepareAsync(admission, CancellationToken.None), "speech_engine_preparation_failed");
            Check(failureHandler.Deletes == 1 && new SpeechEngineCleanupStore(configuration).Read().Length == 0,
                "failed token issuance cleans up its already-created resource");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static string? Type(string json) => JsonSerializer.Deserialize<JsonElement>(json).GetProperty("type").GetString();
    private static async Task UntilAsync(Func<bool> condition, CancellationToken token)
    { while (!condition()) { await Task.Delay(10, token); } }
    private static void Check(bool condition, string message)
    { if (!condition) { throw new InvalidOperationException("Speech Engine: " + message); } }
    private static void Throws<T>(Action action, string message) where T : Exception
    { try { action(); } catch (T) { return; } throw new InvalidOperationException("Speech Engine: " + message); }
    private static async Task ThrowsAsync<T>(Func<Task> action, string message) where T : Exception
    { try { await action(); } catch (T) { return; } throw new InvalidOperationException("Speech Engine: " + message); }
    private static void ThrowsCode(Action action, string code)
    { try { action(); } catch (WandGenerationException error) { Check(error.Code == code, "expected " + code); return; } throw new Exception("Expected " + code); }
    private static async Task ThrowsCodeAsync(Func<Task> action, string code)
    { try { await action(); } catch (WandGenerationException error) { Check(error.Code == code, "expected " + code); return; } throw new Exception("Expected " + code); }

    private sealed class EngineHandler : HttpMessageHandler
    {
        internal const string Token = "synthetic-offline-browser-conversation-token";
        internal ConcurrentQueue<string> Created { get; } = new();
        internal bool RejectToken;
        internal int AuthenticatedRequests, Deletes;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Check(request.RequestUri?.Host == "api.elevenlabs.io" && request.Headers.GetValues("xi-api-key").Single() == ApiKey,
                "provider requests use server-owned API credentials");
            AuthenticatedRequests++;
            if (request.Method == HttpMethod.Delete) { Deletes++; return new(HttpStatusCode.NoContent); }
            if (request.Method == HttpMethod.Post)
            {
                Check(request.RequestUri!.AbsolutePath == "/v1/speech-engine", "create uses official engine collection");
                Created.Enqueue(await request.Content!.ReadAsStringAsync(token));
                return new(HttpStatusCode.OK) { Content = new StringContent("{\"speech_engine_id\":\"seng_offlinefixture\"}", Encoding.UTF8, "application/json") };
            }
            Check(request.Method == HttpMethod.Get && request.RequestUri!.PathAndQuery == "/v1/convai/conversation/token?agent_id=seng_offlinefixture",
                "conversation token is scoped to this created engine");
            return new(RejectToken ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK)
            { Content = new StringContent(JsonSerializer.Serialize(new { token = Token }), Encoding.UTF8, "application/json") };
        }
    }

    private sealed class EngineSocket : WebSocket
    {
        private readonly Channel<byte[]> messages = Channel.CreateUnbounded<byte[]>();
        private byte[]? current;
        private int offset;
        internal ConcurrentQueue<string> Sent { get; } = new();
        internal void Message(object message) => Raw(JsonSerializer.Serialize(message));
        internal void Raw(string message) => messages.Writer.TryWrite(Encoding.UTF8.GetBytes(message));
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => WebSocketState.Open;
        public override string? SubProtocol => null;
        public override void Abort() { }
        public override void Dispose() { }
        public override Task CloseAsync(WebSocketCloseStatus status, string? description, CancellationToken token) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus status, string? description, CancellationToken token) => Task.CompletedTask;
        public override Task SendAsync(ArraySegment<byte> bytes, WebSocketMessageType type, bool endOfMessage, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Check(type == WebSocketMessageType.Text, "upstream sends text, never PCM"); Sent.Enqueue(Encoding.UTF8.GetString(bytes)); return Task.CompletedTask; }
        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> bytes, CancellationToken token)
        {
            current ??= await messages.Reader.ReadAsync(token);
            var count = Math.Min(bytes.Count, current.Length - offset);
            current.AsSpan(offset, count).CopyTo(bytes.AsSpan()); offset += count;
            var final = offset == current.Length;
            if (final) { current = null; offset = 0; }
            return new(count, WebSocketMessageType.Text, final);
        }
    }
}
