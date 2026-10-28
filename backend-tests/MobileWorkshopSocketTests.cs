using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Kufr.Backend.Features.GptLive;
using Kufr.Backend.Features.Wands;

internal static class MobileWorkshopSocketTests
{
    internal static async Task RunAsync()
    {
        await ReassemblesFragmentedControlMessageAsync();
        await ReassemblesFragmentedAudioMessageAsync();
        await RejectsOversizedMessageAsync();
        await RejectsMixedFragmentTypesAsync();
        await HonorsCancellationAsync();
        VerifiesAppendOnlyConversationEvidence();
        VerifiesProviderFailuresStaySanitized();
        Console.WriteLine("Mobile workshop socket checks passed.");
    }

    private static async Task ReassemblesFragmentedControlMessageAsync()
    {
        const string json = "{\"type\":\"cancel\"}";
        using var socket = new FragmentedSocket(
            new(WebSocketMessageType.Text, Encoding.UTF8.GetBytes("{\"type\":"), false),
            new(WebSocketMessageType.Text, Encoding.UTF8.GetBytes("\"cancel\"}"), true));

        var message = await MobileWorkshopLiveRuntime.ReceiveClientMessageAsync(socket, CancellationToken.None);
        Check(message is { Type: WebSocketMessageType.Text } && Encoding.UTF8.GetString(message.Value.Payload) == json,
            "fragmented control frames are reassembled before JSON parsing");
    }

    private static async Task ReassemblesFragmentedAudioMessageAsync()
    {
        using var socket = new FragmentedSocket(
            new(WebSocketMessageType.Binary, [1, 2], false),
            new(WebSocketMessageType.Binary, [3, 4], true));

        var message = await MobileWorkshopLiveRuntime.ReceiveClientMessageAsync(socket, CancellationToken.None);
        Check(message is { Type: WebSocketMessageType.Binary } && message.Value.Payload.SequenceEqual(new byte[] { 1, 2, 3, 4 }),
            "fragmented PCM frames are reassembled without byte loss");
    }

    private static async Task RejectsOversizedMessageAsync()
    {
        using var socket = new FragmentedSocket(
            new(WebSocketMessageType.Text, new byte[16 * 1024], false),
            new(WebSocketMessageType.Text, [1], true));

        await ThrowsInvalidDataAsync(socket, "oversized control messages fail closed");
    }

    private static async Task RejectsMixedFragmentTypesAsync()
    {
        using var socket = new FragmentedSocket(
            new(WebSocketMessageType.Text, [1], false),
            new(WebSocketMessageType.Binary, [2], true));

        await ThrowsInvalidDataAsync(socket, "message fragment types cannot change mid-message");
    }

    private static async Task HonorsCancellationAsync()
    {
        using var socket = new FragmentedSocket();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        try
        {
            await MobileWorkshopLiveRuntime.ReceiveClientMessageAsync(socket, cancellation.Token);
            throw new InvalidOperationException("Canceled receive unexpectedly succeeded.");
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("PASS receive cancellation releases pending socket read");
        }
    }

    private static void VerifiesProviderFailuresStaySanitized()
    {
        using var json = JsonDocument.Parse("{\"type\":\"error\",\"error\":{\"code\":\"rate_limit_exceeded\",\"message\":\"private provider detail\"}}");
        var failure = LiveProviderFailure.Read(json.RootElement);
        Check(LiveFailureDiagnostics.Code(failure) == "provider_capacity"
            && !failure.Message.Contains("private provider detail", StringComparison.Ordinal),
            "provider failures retain safe categories without provider response text");

        var malformed = LiveFailureDiagnostics.Code(new InvalidDataException("private transcript detail"));
        Check(malformed == "live_data_invalid" && !malformed.Contains("private transcript detail", StringComparison.Ordinal),
            "transport diagnostics never expose the failed message body");
    }

    private static void VerifiesAppendOnlyConversationEvidence()
    {
        var first = new MobileWandWorkshopMessage("first", 1, "user", "Most je přerušený.", "voice", 1);
        var later = new MobileWandWorkshopMessage("later", 1, "user", "Půjdu přes zahradu.", "voice", 2);
        var correction = first with { Revision = 2, Text = "Most je opravený." };
        Check(MobileWorkshopLiveRuntime.UserEvidenceIsUnchangedPrefix([first], [first, later]),
            "queued Live work accepts later turns without treating them as a correction");
        Check(!MobileWorkshopLiveRuntime.UserEvidenceIsUnchangedPrefix([first], [correction, later]),
            "queued Live work rejects a corrected earlier answer");
    }

    private static async Task ThrowsInvalidDataAsync(FragmentedSocket socket, string assertion)
    {
        try
        {
            await MobileWorkshopLiveRuntime.ReceiveClientMessageAsync(socket, CancellationToken.None);
            throw new InvalidOperationException($"FAIL {assertion}");
        }
        catch (InvalidDataException)
        {
            Console.WriteLine($"PASS {assertion}");
        }
    }

    private static void Check(bool condition, string assertion)
    {
        if (!condition) throw new InvalidOperationException($"FAIL {assertion}");
        Console.WriteLine($"PASS {assertion}");
    }

    private sealed record Frame(WebSocketMessageType Type, byte[] Bytes, bool Complete);

    private sealed class FragmentedSocket(params Frame[] frames) : WebSocket
    {
        private int offset;
        private int index;
        private readonly TaskCompletionSource closed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => WebSocketState.Open;
        public override string? SubProtocol => null;

        public override void Abort() => closed.TrySetResult();
        public override void Dispose() => closed.TrySetResult();
        public override Task CloseAsync(WebSocketCloseStatus status, string? description, CancellationToken token) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus status, string? description, CancellationToken token) => Task.CompletedTask;
        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType type, bool endOfMessage, CancellationToken token) => Task.CompletedTask;

        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken token)
        {
            if (index >= frames.Length)
            {
                await closed.Task.WaitAsync(token);
                throw new WebSocketException("The test socket was closed.");
            }

            var frame = frames[index];
            var count = Math.Min(buffer.Count, frame.Bytes.Length - offset);
            frame.Bytes.AsSpan(offset, count).CopyTo(buffer.AsSpan());
            offset += count;
            var frameComplete = offset == frame.Bytes.Length;
            if (frameComplete)
            {
                index++;
                offset = 0;
            }

            return new(count, frame.Type, frame.Complete && frameComplete);
        }
    }
}
