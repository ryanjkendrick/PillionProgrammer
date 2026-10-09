using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Pillion.Common.Secrets;
using Pillion.Common.Settings;
using Pillion.Contracts;
using Pillion.ModelGateway.Tests.Providers.OpenAICompatible;
using Pillion.ModelGateway.Tests.TestSupport;

namespace Pillion.ModelGateway.Tests.Providers.Cloud;

/// <summary>One request the fake received.</summary>
/// <param name="Method">The HTTP method.</param>
/// <param name="Path">The absolute path without the leading slash (for example <c>v1/messages</c>).</param>
/// <param name="Query">The query string without the question mark.</param>
/// <param name="Headers">The request headers, names lower-cased, values joined with a comma.</param>
/// <param name="Body">The JSON body, if any.</param>
/// <param name="Host">The host the request was addressed to.</param>
internal sealed record CloudRequest(string Method, string Path, string Query, IReadOnlyDictionary<string, string> Headers, JsonObject? Body, string Host)
{
    public string? Header(string name) => Headers.TryGetValue(name.ToLowerInvariant(), out var value) ? value : null;
}

/// <summary>An <see cref="HttpMessageHandler"/> that answers cloud API calls from fixtures.</summary>
internal sealed class FakeCloudHandler : HttpMessageHandler
{
    private readonly ConcurrentDictionary<string, Func<CloudRequest, HttpResponseMessage>> _routes = new(StringComparer.Ordinal);

    public ConcurrentQueue<CloudRequest> Requests { get; } = new();

    /// <summary>Gets or sets a value indicating whether every request fails as if nothing listened on the port.</summary>
    public bool RefuseConnections { get; set; }

    public static string Fixture(string provider, string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", provider, name));

    public static HttpResponseMessage Respond(HttpStatusCode status, string content, string mediaType = "application/json") =>
        new(status) { Content = new StringContent(content, Encoding.UTF8, mediaType) };

    /// <summary>Answers a route with a fixture.</summary>
    /// <param name="route">The method and path, for example <c>POST v1/messages</c>.</param>
    /// <param name="provider">The fixture folder.</param>
    /// <param name="fixture">The fixture file.</param>
    /// <param name="mediaType">The content type.</param>
    public FakeCloudHandler Serve(string route, string provider, string fixture, string? mediaType = null)
    {
        mediaType ??= fixture.EndsWith(".sse", StringComparison.Ordinal) ? "text/event-stream" : "application/json";
        var content = Fixture(provider, fixture);
        _routes[route] = _ => Respond(HttpStatusCode.OK, content, mediaType);
        return this;
    }

    /// <summary>Answers a route with an error status and a fixture body.</summary>
    public FakeCloudHandler Fail(string route, HttpStatusCode status, string provider, string fixture)
    {
        var content = Fixture(provider, fixture);
        _routes[route] = _ => Respond(status, content);
        return this;
    }

    public FakeCloudHandler FailText(string route, HttpStatusCode status, string content, string mediaType)
    {
        _routes[route] = _ => Respond(status, content, mediaType);
        return this;
    }

    public FakeCloudHandler On(string route, Func<CloudRequest, HttpResponseMessage> respond)
    {
        _routes[route] = respond;
        return this;
    }

    public IEnumerable<CloudRequest> RequestsTo(string path) => Requests.Where(r => r.Path == path);

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

        var headers = request.Headers.ToDictionary(h => h.Key.ToLowerInvariant(), h => string.Join(",", h.Value));
        var path = request.RequestUri!.AbsolutePath.TrimStart('/');
        var received = new CloudRequest(request.Method.Method, Uri.UnescapeDataString(path), request.RequestUri.Query.TrimStart('?'), headers, body, request.RequestUri.Host);
        Requests.Enqueue(received);
        return _routes.TryGetValue($"{request.Method} {received.Path}", out var respond)
            ? respond(received)
            : Respond(HttpStatusCode.NotFound, "{\"error\":{\"message\":\"no such route\"}}");
    }
}

/// <summary>
/// A gateway (router, registry, breakers, cache, Outbound log) around one cloud provider factory, with a fake HTTP
/// handler under it. Used to prove that each cloud adapter routes through the real gateway.
/// </summary>
internal sealed class CloudRig : IDisposable
{
    public CloudRig(
        FakeCloudHandler handler,
        ProviderSettings provider,
        Func<ISecretStore, FakeCloudHandler, IModelProviderFactory> factory,
        string? key = "unit-test-key-0001",
        Action<PillionSettings>? configure = null)
    {
        Handler = handler;
        Settings.Providers.Add(provider);
        if (key is not null)
        {
            Secrets.Set(provider.Id, key);
        }

        configure?.Invoke(Settings);
        var options = new ModelGatewayOptions { RetryBaseDelay = TimeSpan.FromMilliseconds(1), RetryMaxDelay = TimeSpan.FromMilliseconds(5) };
        Source = new StaticGatewaySettingsSource(Settings);
        Registry = new ProviderRegistry(Source, Secrets, [factory(Secrets, handler)], ModelCatalog.Default);
        var inventory = new ModelInventory();
        var breakers = new CircuitBreakerRegistry(options);
        OutboundLog = new InMemoryOutboundLog(options);
        var router = new ModelRouter(Registry, ModelCatalog.Default, inventory, new AlwaysReachable(), breakers, new NoCostCaps());
        Gateway = new ModelGateway(
            Registry,
            router,
            inventory,
            ModelCatalog.Default,
            PromptTemplates.Default,
            new AutocompleteCache(options),
            breakers,
            new ConcurrencyLimiter(options),
            OutboundLog,
            new AlwaysReachable(),
            new NoCostCaps(),
            new NoProgressSink(),
            options);
    }

    public FakeCloudHandler Handler { get; }

    public PillionSettings Settings { get; } = new();

    public InMemorySecretStore Secrets { get; } = new();

    public StaticGatewaySettingsSource Source { get; }

    public ProviderRegistry Registry { get; }

    public InMemoryOutboundLog OutboundLog { get; }

    public ModelGateway Gateway { get; }

    public Task<List<ChatChunk>> ChatAsync(string text, string? modelId = null, Profile profile = Profile.Auto) =>
        Gateway.ChatAsync(GatewayHarness.ChatRequest(text, modelId, profile), TaskClass.Chat, CancellationToken.None).CollectAsync();

    public void Dispose() => Registry.Dispose();
}

/// <summary>A response body that never finishes, for cancellation tests (see <see cref="StallingContent"/>).</summary>
internal static class Stalling
{
    public static HttpResponseMessage Sse(string prefix) =>
        new(HttpStatusCode.OK) { Content = new StallingContent(prefix, "text/event-stream") };
}
