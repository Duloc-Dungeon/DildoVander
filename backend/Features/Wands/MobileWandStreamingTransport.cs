using System.Text;

namespace Kufr.Backend.Features.Wands;

// The ordinary SDK transport buffers JSON. Streaming keeps the installed SDK's
// event parser while bounding bytes and each SSE line before it can allocate it.
internal sealed class MobileWandStreamingTransport(HttpClient upstream) : HttpMessageHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        if (request.RequestUri is not { Scheme: "https", Host: "api.openai.com", AbsolutePath: "/v1/responses" })
        {
            throw new InvalidDataException("Invalid dialogue endpoint.");
        }
        using var forwarded = new HttpRequestMessage(request.Method, request.RequestUri) { Content = request.Content };
        foreach (var header in request.Headers) { forwarded.Headers.TryAddWithoutValidation(header.Key, header.Value); }
        HttpResponseMessage response;
        try { response = await upstream.SendAsync(forwarded, HttpCompletionOption.ResponseHeadersRead, token); }
        finally { forwarded.Content = null; }
        try
        {
            if (!response.IsSuccessStatusCode)
            {
                if ((int)response.StatusCode < 400) { throw new InvalidDataException("Dialogue redirect rejected."); }
                response.Content.Dispose();
                response.Content = new StringContent("{\"error\":{\"message\":\"Dialogue request rejected.\",\"type\":\"provider_http_error\"}}",
                    Encoding.UTF8, "application/json");
                return response;
            }
            if (response.Content.Headers.ContentType?.MediaType != "text/event-stream"
                || response.Content.Headers.ContentLength > 131072)
            {
                throw new InvalidDataException("Invalid dialogue event stream.");
            }
            var original = response.Content;
            var stream = await original.ReadAsStreamAsync(token);
            var content = new StreamContent(new BoundedEvents(stream, original));
            foreach (var header in original.Headers) { content.Headers.TryAddWithoutValidation(header.Key, header.Value); }
            response.Content = content;
            return response;
        }
        catch { response.Dispose(); throw; }
    }

    private sealed class BoundedEvents(Stream inner, HttpContent owner) : Stream
    {
        private int total;
        private int line;
        private void Count(ReadOnlySpan<byte> bytes)
        {
            total += bytes.Length;
            if (total > 131072) { throw new InvalidDataException("Dialogue stream exceeded its bound."); }
            foreach (var value in bytes)
            {
                line = value == '\n' ? 0 : line + 1;
                if (line > 32768) { throw new InvalidDataException("Dialogue event line exceeded its bound."); }
            }
        }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = inner.Read(buffer, offset, Math.Min(count, 1024));
            Count(buffer.AsSpan(offset, read));
            return read;
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            var read = await inner.ReadAsync(buffer[..Math.Min(buffer.Length, 1024)], token);
            Count(buffer.Span[..read]);
            return read;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token) =>
            ReadAsync(buffer.AsMemory(offset, count), token).AsTask();
        protected override void Dispose(bool disposing) { if (disposing) { owner.Dispose(); } base.Dispose(disposing); }
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
