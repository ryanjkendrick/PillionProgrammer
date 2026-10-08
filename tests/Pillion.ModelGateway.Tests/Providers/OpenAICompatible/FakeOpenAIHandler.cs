using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace Pillion.ModelGateway.Tests.Providers.OpenAICompatible;

/// <summary>One request the fake received.</summary>
/// <param name="Method">The HTTP method.</param>
/// <param name="Path">The absolute path, without the leading slash (for example <c>v1/models</c>).</param>
/// <param name="Authorization">The Authorization header, or <see langword="null"/>.</param>
/// <param name="Body">The JSON body, if any.</param>
/// <param name="Host">The host the request was addressed to.</param>
internal sealed record ReceivedRequest(string Method, string Path, string? Authorization, JsonObject? Body, string Host);

/// <summary>An <see cref="HttpMessageHandler"/> that answers OpenAI-compatible API calls from fixtures.</summary>
internal sealed class FakeOpenAIHandler : HttpMessageHandler
{
    private readonly ConcurrentDictionary<string, Func<ReceivedRequest, HttpResponseMessage>> _routes = new(StringComparer.Ordinal);

    public ConcurrentQueue<ReceivedRequest> Requests { get; } = new();

    /// <summary>Gets or sets a value indicating whether every request fails as if nothing listened on the port.</summary>
    public bool RefuseConnections { get; set; }

    public static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "OpenAICompatible", name));

    /// <summary>Creates a handler that serves the recorded model list, a streamed chat and a streamed completion.</summary>
    public static FakeOpenAIHandler WithDefaults()
    {
        var handler = new FakeOpenAIHandler();
        handler.Text("GET v1/models", Fixture("models.json"), "application/json");
        handler.Text("POST v1/chat/completions", Fixture("chat-stream.sse"), "text/event-stream");
        handler.Text("POST v1/completions", Fixture("completions-stream.sse"), "text/event-stream");
        return handler;
    }

    public static HttpResponseMessage Respond(HttpStatusCode status, string content, string mediaType = "application/json") =>
        new(status) { Content = new StringContent(content, Encoding.UTF8, mediaType) };

    public void Text(string route, string content, string mediaType) => _routes[route] = _ => Respond(HttpStatusCode.OK, content, mediaType);

    public void Error(string route, HttpStatusCode status, string content, string mediaType = "application/json") =>
        _routes[route] = _ => Respond(status, content, mediaType);

    public void On(string route, Func<ReceivedRequest, HttpResponseMessage> respond) => _routes[route] = respond;

    public IEnumerable<ReceivedRequest> RequestsTo(string path) => Requests.Where(r => r.Path == path);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (RefuseConnections)
        {
            throw new HttpRequestException(
                "No connection could be made because the target machine actively refused it.",
                new SocketException((int)SocketError.ConnectionRefused));
        }

        JsonObject? body = null;
        if (request.Content is not null)
        {
            body = JsonNode.Parse(await request.Content.ReadAsStringAsync(cancellationToken))!.AsObject();
        }

        var path = request.RequestUri!.AbsolutePath.TrimStart('/');
        var received = new ReceivedRequest(request.Method.Method, path, request.Headers.Authorization?.ToString(), body, request.RequestUri.Host);
        Requests.Enqueue(received);
        return _routes.TryGetValue($"{request.Method} {path}", out var respond)
            ? respond(received)
            : Respond(HttpStatusCode.NotFound, "404 page not found", "text/plain");
    }
}

/// <summary>A response body that delivers some bytes and then waits until the reader cancels, like a stream that stalls mid-answer.</summary>
internal sealed class StallingContent : HttpContent
{
    private readonly byte[] _prefix;

    public StallingContent(string prefix, string mediaType)
    {
        _prefix = Encoding.UTF8.GetBytes(prefix);
        Headers.ContentType = new MediaTypeHeaderValue(mediaType);
    }

    protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new StallingStream(_prefix));

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => throw new NotSupportedException();

    protected override bool TryComputeLength(out long length)
    {
        length = -1;
        return false;
    }

    private sealed class StallingStream : Stream
    {
        private readonly byte[] _prefix;
        private int _position;

        public StallingStream(byte[] prefix) => _prefix = prefix;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_position < _prefix.Length)
            {
                var count = Math.Min(buffer.Length, _prefix.Length - _position);
                _prefix.AsMemory(_position, count).CopyTo(buffer);
                _position += count;
                return count;
            }

            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            return 0;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
