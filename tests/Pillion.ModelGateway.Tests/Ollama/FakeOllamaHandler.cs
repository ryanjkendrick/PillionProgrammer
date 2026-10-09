using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace Pillion.ModelGateway.Tests.Ollama;

/// <summary>An <see cref="HttpMessageHandler"/> that answers Ollama API calls from recorded fixtures.</summary>
internal sealed class FakeOllamaHandler : HttpMessageHandler
{
    private readonly ConcurrentDictionary<string, Func<HttpRequestMessage, JsonObject?, HttpResponseMessage>> _routes = new(StringComparer.Ordinal);

    private readonly ConcurrentDictionary<string, Func<HttpRequestMessage, JsonObject?, CancellationToken, Task<HttpResponseMessage>>> _asyncRoutes = new(StringComparer.Ordinal);

    public ConcurrentQueue<(string Path, JsonObject? Body)> Requests { get; } = new();

    /// <summary>Gets or sets a value indicating whether every request fails as if nothing listened on the port.</summary>
    public bool RefuseConnections { get; set; }

    public static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Ollama", name));

    public static FakeOllamaHandler WithDefaults()
    {
        var handler = new FakeOllamaHandler();
        handler.Json("GET api/version", Fixture("version.json"));
        handler.Json("GET api/tags", Fixture("tags.json"));
        handler.On("POST api/show", (_, body) => body?["model"]?.GetValue<string>() switch
        {
            "gemma4:e2b" => Respond(HttpStatusCode.OK, Fixture("show-gemma4.json")),
            "qwen2.5-coder:1.5b" => Respond(HttpStatusCode.OK, Fixture("show-qwen-coder.json")),
            "qwen3-embedding:0.6b" => Respond(HttpStatusCode.OK, Fixture("show-qwen3-embedding.json")),
            _ => Respond(HttpStatusCode.NotFound, Fixture("error-model-not-found.json")),
        });
        handler.Json("POST api/chat", Fixture("chat-gemma4.ndjson"), "application/x-ndjson");
        handler.Json("POST api/generate", Fixture("generate-qwen-coder.ndjson"), "application/x-ndjson");
        return handler;
    }

    public static HttpResponseMessage Respond(HttpStatusCode status, string content, string mediaType = "application/json") =>
        new(status) { Content = new StringContent(content, Encoding.UTF8, mediaType) };

    public void Json(string route, string content, string mediaType = "application/json") =>
        _routes[route] = (_, _) => Respond(HttpStatusCode.OK, content, mediaType);

    public void On(string route, Func<HttpRequestMessage, JsonObject?, HttpResponseMessage> respond) => _routes[route] = respond;

    /// <summary>Routes a request to a handler that may wait (for example, to model a server that never answers); it sees the request's cancellation token.</summary>
    public void OnAsync(string route, Func<HttpRequestMessage, JsonObject?, CancellationToken, Task<HttpResponseMessage>> respond) => _asyncRoutes[route] = respond;

    public Func<HttpRequestMessage, JsonObject?, HttpResponseMessage> RouteFor(string route) => _routes[route];

    public IEnumerable<JsonObject> BodiesFor(string path) => Requests.Where(r => r.Path == path).Select(r => r.Body!);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (RefuseConnections)
        {
            throw new HttpRequestException(
                "No connection could be made because the target machine actively refused it.",
                new SocketException((int)SocketError.ConnectionRefused));
        }

        var path = request.RequestUri!.AbsolutePath.TrimStart('/');
        JsonObject? body = null;
        if (request.Content is not null)
        {
            body = JsonNode.Parse(await request.Content.ReadAsStringAsync(cancellationToken))!.AsObject();
        }

        Requests.Enqueue((path, body));
        if (_asyncRoutes.TryGetValue($"{request.Method} {path}", out var respondAsync))
        {
            return await respondAsync(request, body, cancellationToken);
        }

        return _routes.TryGetValue($"{request.Method} {path}", out var respond)
            ? respond(request, body)
            : Respond(HttpStatusCode.NotFound, "404 page not found", "text/plain");
    }
}
