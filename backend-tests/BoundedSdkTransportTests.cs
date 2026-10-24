using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using Kufr.Backend.Features.Customers;
using Kufr.Backend.Features.OpenAi;
using OpenAI.Responses;

#pragma warning disable OPENAI001

internal static class BoundedSdkTransportTests
{
    internal static async Task RunAsync()
    {
        foreach (var status in new[] { 400, 401, 403, 408, 429, 500, 503 })
        {
            await FailureAsync(status);
        }
        Console.WriteLine("Bounded SDK transport preserves HTTP failure status, privacy and single dispatch (offline).");
    }

    internal static async Task<ClientResultException> FailureAsync(int status)
    {
        ClientResultException? failure = null;
        using var body = new UnreadableContent();
        var handler = new ErrorHandler((HttpStatusCode)status, body);
        using var upstream = new HttpClient(handler);
        using var bounded = BoundedSdkTransport.Create(upstream, 1024, ["application/json"]);
        var client = new ResponsesClient(new ApiKeyCredential("offline-fixture"), new ResponsesClientOptions
        {
            Transport = BoundedSdkTransport.Pipeline(bounded), RetryPolicy = new ClientRetryPolicy(0)
        });
        using var request = BinaryContent.Create(BinaryData.FromString("{\"model\":\"fixture\",\"input\":\"fixture\"}"));
        try
        {
            await client.CreateResponseAsync(request, new RequestOptions());
            throw new InvalidOperationException("Provider HTTP failure was accepted.");
        }
        catch (ClientResultException error)
        {
            failure = error;
            Require(error.Status == status && CharacterProviderFailure.Status(error) == status,
                "SDK and worker diagnostics retain the upstream HTTP status");
            Require(CharacterProviderFailure.Code(error) == "openai_http_" + status,
                "HTTP failures are distinct from invalid provider output");
            Require(CharacterProviderFailure.IsTransient(error) == (status is 408 or 429 || status >= 500),
                "only transient HTTP failures use the existing durable retry policy");
            Require(!error.Message.Contains("private", StringComparison.Ordinal),
                "SDK errors cannot expose the original response body");
        }
        Require(handler.Calls == 1, "the SDK performs exactly one dispatch");
        Require(body.Disposed && !body.Read, "the upstream error body is disposed without reading or buffering it");
        return failure!;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) { throw new InvalidOperationException(message); }
    }

    private sealed class ErrorHandler(HttpStatusCode status, HttpContent body) : HttpMessageHandler
    {
        internal int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(status) { Content = body });
        }
    }

    private sealed class UnreadableContent : HttpContent
    {
        internal bool Read { get; private set; }
        internal bool Disposed { get; private set; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            Read = true;
            throw new InvalidOperationException("private upstream content must not be read");
        }
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
}
