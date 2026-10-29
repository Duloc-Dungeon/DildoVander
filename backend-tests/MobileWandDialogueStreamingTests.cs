using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Kufr.Backend.Features.Customers;
using Kufr.Backend.Features.Wands;
using Microsoft.Extensions.Configuration;

internal static class MobileWandDialogueStreamingTests
{
    internal static async Task RunAsync()
    {
        ParsesOnlySafeCompleteClauses();
        await SdkStreamAsync(ready: false);
        await SdkStreamAsync(ready: true);
        await RejectsIncompleteStreamAsync();
    }

    private static void ParsesOnlySafeCompleteClauses()
    {
        var decoder = new MobileWandReplyStream();
        Check(decoder.Append("""{"readyToFinish":false,"reply":"Zdravím tě. Dál \uD83D""")
            .SequenceEqual(new[] { "Zdravím tě." }), "complete clauses precede an incomplete Unicode escape safely");
        Check(decoder.Text == "Zdravím tě. Dál ", "visible snapshots exclude unfinished Unicode escapes");
        Check(decoder.Append("""\uDE00 pokračuje?"}""").Single() == " Dál 😀 pokračuje?",
            "split surrogate pairs are decoded before entering speech");
        var replyFirst = new MobileWandReplyStream();
        Check(replyFirst.Append("""{"reply":"Zdravím tě.",""").Length == 0,
            "reply-first output waits for the readiness discriminator");
        Check(replyFirst.Append("\"readyToFinish\":false}").Single() == "Zdravím tě.",
            "a completed false discriminator releases buffered ordinary speech");
        Check(new MobileWandReplyStream().Append("""{"readyToFinish":true,"reply":"Vyrobím ti hůlku."}""").Length == 0,
            "terminal speech never escapes before the completed decision is accepted");
        Check(new MobileWandReplyStream().Append("""{"readyToFinish":false,"reply":"Hůlku jsem uložil."}""").Length == 0,
            "ambiguous result claims are held even behind a false discriminator");
        Check(new MobileWandReplyStream().Append("""{"readyToFinish":false,"reply":"Nedokončená věta""").Length == 0,
            "a dangling clause is not spoken provisionally");
        var question = new MobileWandReplyStream();
        question.Append("""{"readyToFinish":false,"reply":"Jakou hůlku""");
        Check(question.Text == "Jakou hůlku", "ordinary wand questions stream text before sentence punctuation");
        Check(question.Append(""" si přeješ?"}""").Single() == "Jakou hůlku si přeješ?",
            "a wand noun does not suppress an ordinary question behind ready=false");
    }

    private static async Task SdkStreamAsync(bool ready)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var handler = new SseHandler();
        using var provider = Provider(handler);
        var firstClause = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstText = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var clauses = new List<string>();
        var snapshots = new List<string>();
        var json = ready
            ? "{\"readyToFinish\":true,\"reply\":\"I will manufacture it.\"}"
            : "{\"readyToFinish\":false,\"reply\":\"Tell me your story. What happened?\"}";
        var prefix = ready ? json : "{\"readyToFinish\":false,\"reply\":\"Tell me";
        handler.Delta(prefix, 0);
        var task = provider.ReplyStreamingAsync(Profile(), [], (text, _) =>
        {
            clauses.Add(text);
            firstClause.TrySetResult();
            return Task.CompletedTask;
        }, (text, _) =>
        {
            snapshots.Add(text);
            firstText.TrySetResult();
            return Task.CompletedTask;
        }, timeout.Token, "Synthetic developer prompt");
        await Task.WhenAny(handler.Requested.Task, task).WaitAsync(timeout.Token);
        if (task.IsCompleted) { await task; }
        await handler.Requested.Task.WaitAsync(timeout.Token);
        if (ready)
        {
            await Task.Delay(80, timeout.Token);
            Check(clauses.Count == 0 && snapshots.Count == 0 && !task.IsCompleted,
                "ready=true text and audio are withheld until a valid completed response");
        }
        else
        {
            await firstText.Task.WaitAsync(timeout.Token);
            Check(snapshots.Single() == "Tell me" && clauses.Count == 0 && !task.IsCompleted,
                "actual SDK text snapshots grow independently of sentence/audio callbacks");
            handler.Delta(" your story. ", 1);
            await firstClause.Task.WaitAsync(timeout.Token);
            Check(!task.IsCompleted && clauses[0] == "Tell me your story.", "the actual SDK yields decoded speech before final GPT completion");
            handler.Delta(json[(prefix.Length + " your story. ".Length)..], 2);
        }
        handler.Complete(json);
        var result = await task.WaitAsync(timeout.Token);
        Check(result.ReadyToFinish == ready && result.Reply == (ready ? "I will manufacture it." : "Tell me your story. What happened?"),
            "only the validated final response produces the readiness flag");
        Check(handler.CorrectRequest, "streaming keeps the schema, developer prompt, fast tier and readiness-first order");
    }

    private static async Task RejectsIncompleteStreamAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var handler = new SseHandler();
        using var provider = Provider(handler);
        handler.Delta("{\"readyToFinish\":true,\"reply\":\"A dangling decision", 0);
        handler.Stream.End();
        try
        {
            await provider.ReplyStreamingAsync(Profile(), [], (_, _) => throw new Exception("Terminal speech leaked"), timeout.Token);
            throw new Exception("Incomplete stream was accepted");
        }
        catch (MobileWandQuestionAuthoringException error)
        {
            Check(error.Code is "dialogue_response_incomplete" or "dialogue_provider_uncertain", "an incomplete stream cannot create a final decision");
        }
        var oversized = new SseHandler();
        using var oversizedProvider = Provider(oversized);
        oversized.Stream.Add("data: " + new string('x', 33000) + "\n\n");
        oversized.Stream.End();
        try
        {
            await oversizedProvider.ReplyStreamingAsync(Profile(), [], (_, _) => Task.CompletedTask, timeout.Token);
            throw new Exception("Unbounded SSE line accepted");
        }
        catch (MobileWandQuestionAuthoringException) { }
    }

    private static OpenAiMobileWandQuestionProvider Provider(SseHandler handler) => new(
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["OPENAI_API_KEY"] = "synthetic-offline-key", ["Wands:Model"] = "gpt-6.1-terra" }).Build(), new HttpClient(handler));

    private static CharacterConversationSnapshot Profile() => new("profile", 1, new("Raven", "Synthetic"),
        new("Raven", "Synthetic", "Prepared fixture", [new("Finds maps", ["https://example.test"])],
            [], [], false, [], "synthetic", "test", 1, 1, "synthetic"), 1);

    private static void Check(bool condition, string reason)
    {
        if (!condition) { throw new InvalidOperationException(reason); }
    }

    private sealed class SseHandler : HttpMessageHandler
    {
        internal EventStream Stream { get; } = new();
        internal TaskCompletionSource Requested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool CorrectRequest;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            var root = document.RootElement;
            CorrectRequest = root.GetProperty("stream").GetBoolean() && root.GetProperty("service_tier").GetString() == "fast"
                && root.GetProperty("model").GetString() == "gpt-6.1-terra"
                && root.GetProperty("instructions").GetString() == "Synthetic developer prompt"
                && root.GetProperty("text").GetProperty("format").GetProperty("schema").GetProperty("properties")
                    .EnumerateObject().First().Name == "readyToFinish";
            Requested.TrySetResult();
            var content = new StreamContent(Stream);
            content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
            return new(HttpStatusCode.OK) { Content = content };
        }
        internal void Delta(string delta, int sequence) => Event("response.output_text.delta", new
        {
            type = "response.output_text.delta", delta, sequence_number = sequence,
            item_id = "msg_fixture", output_index = 0, content_index = 0
        });
        internal void Complete(string text)
        {
            Event("response.completed", new
            {
                type = "response.completed", sequence_number = 3,
                response = new
                {
                    id = "resp_fixture", @object = "response", created_at = 1, status = "completed",
                    model = "gpt-6.1-terra", service_tier = "fast",
                    output = new[] { new { type = "message", id = "msg_fixture", role = "assistant", status = "completed",
                        content = new[] { new { type = "output_text", text, annotations = Array.Empty<object>() } } } },
                    usage = new { input_tokens = 10, output_tokens = 10, total_tokens = 20 }
                }
            });
            Stream.End();
        }
        private void Event(string type, object value) => Stream.Add("event: " + type + "\ndata: " + JsonSerializer.Serialize(value) + "\n\n");
    }

    private sealed class EventStream : Stream
    {
        private readonly Channel<byte[]> chunks = Channel.CreateUnbounded<byte[]>();
        private byte[]? current;
        private int offset;
        internal void Add(string text) => chunks.Writer.TryWrite(Encoding.UTF8.GetBytes(text));
        internal void End() => chunks.Writer.TryComplete();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            if (current is null || offset == current.Length)
            {
                if (!await chunks.Reader.WaitToReadAsync(token)) { return 0; }
                current = await chunks.Reader.ReadAsync(token);
                offset = 0;
            }
            var count = Math.Min(buffer.Length, current.Length - offset);
            current.AsMemory(offset, count).CopyTo(buffer);
            offset += count;
            return count;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token) => ReadAsync(buffer.AsMemory(offset, count), token).AsTask();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
