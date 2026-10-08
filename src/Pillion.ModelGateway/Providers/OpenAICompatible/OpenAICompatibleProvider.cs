using System.Net;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Pillion.Common.Secrets;
using Pillion.Common.Settings;
using Pillion.Contracts;

namespace Pillion.ModelGateway.Providers.OpenAICompatible;

/// <summary>What <see cref="OpenAICompatibleProvider.ProbeDetailedAsync"/> learned about the server.</summary>
/// <param name="IsReachable">Whether the server answered at all.</param>
/// <param name="IsAuthenticated">
/// <see langword="true"/> when a key was sent and accepted, <see langword="false"/> when the server rejected the key or
/// wanted one, and <see langword="null"/> when no key was sent and none was needed.
/// </param>
/// <param name="Server">The server's self-identification (the <c>Server</c> response header), if present.</param>
/// <param name="ModelCount">The number of models <c>/models</c> listed, or <see langword="null"/> when it could not be read.</param>
/// <param name="Message">A user-facing description.</param>
public sealed record OpenAIProbeResult(bool IsReachable, bool? IsAuthenticated, string? Server, int? ModelCount, string Message);

/// <summary>
/// The OpenAI-compatible adapter (design section 6): the <c>/v1</c> protocol over <see cref="HttpClient"/>, for OpenAI,
/// LM Studio, the llama.cpp server, vLLM, OpenRouter and enterprise gateways. Models come from <c>GET /models</c> (the
/// configured model ids when the server has no such endpoint); chat streams from <c>POST /chat/completions</c>;
/// fill-in-the-middle is available only on a local server that declares <c>supportsFim</c>.
/// </summary>
public sealed class OpenAICompatibleProvider : IModelProvider
{
    private static readonly string[] ContextFields = ["context_length", "max_model_len", "context_window", "max_context_length"];

    private readonly HttpClient _httpClient;
    private readonly IDisposable? _owned;
    private readonly OpenAIHttp _http;
    private readonly ModelCatalog _catalog;
    private readonly IReadOnlyList<string> _configuredModels;

    /// <summary>Initializes a new instance of the <see cref="OpenAICompatibleProvider"/> class.</summary>
    /// <param name="settings">The provider settings.</param>
    /// <param name="options">The options read from the settings.</param>
    /// <param name="httpClient">The HTTP client; the provider owns and disposes it.</param>
    /// <param name="keyProvider">Reads the API key for each request; the key is never kept.</param>
    /// <param name="catalog">The model catalog.</param>
    /// <param name="owned">Something the provider disposes with the client (the CA bundle certificates), or <see langword="null"/>.</param>
    /// <param name="logger">The logger.</param>
    public OpenAICompatibleProvider(
        ProviderSettings settings,
        OpenAICompatibleOptions options,
        HttpClient httpClient,
        Func<string?> keyProvider,
        ModelCatalog catalog,
        IDisposable? owned = null,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(keyProvider);
        ArgumentNullException.ThrowIfNull(catalog);

        Options = options;
        Id = settings.Id;
        Kind = options.Kind;
        DisplayName = !string.IsNullOrWhiteSpace(settings.DisplayName)
            ? settings.DisplayName
            : options.Kind == ProviderKind.OpenAi ? "OpenAI" : "OpenAI-compatible server";
        IsLocal = options.IsLoopback;
        _httpClient = httpClient;
        _owned = owned;
        _catalog = catalog;
        _configuredModels = [.. settings.Models];
        _http = new OpenAIHttp(httpClient, options.Endpoint, Id, DisplayName, keyProvider, logger ?? NullLogger.Instance);
    }

    /// <inheritdoc/>
    public string Id { get; }

    /// <inheritdoc/>
    public ProviderKind Kind { get; }

    /// <inheritdoc/>
    public string DisplayName { get; }

    /// <summary>Gets a value indicating whether the server is on this machine (a loopback endpoint). Any other host is a cloud boundary.</summary>
    public bool IsLocal { get; }

    /// <summary>Gets the options.</summary>
    public OpenAICompatibleOptions Options { get; }

    internal OpenAIHttp Http => _http;

    /// <inheritdoc/>
    public async Task<IReadOnlyList<ProviderModel>> ListModelsAsync(CancellationToken cancellationToken)
    {
        JsonDocument document;
        try
        {
            document = await _http.GetJsonAsync("models", cancellationToken).ConfigureAwait(false);
        }
        catch (ProviderException ex) when (ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotImplemented)
        {
            // Some gateways have no /models: offer the configured models, or the catalog's defaults for the kind.
            return ConfiguredModels();
        }

        using (document)
        {
            var result = new List<ProviderModel>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("data", out var data) &&
                data.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in data.EnumerateArray())
                {
                    if (entry.ValueKind != JsonValueKind.Object ||
                        !entry.TryGetProperty("id", out var idElement) ||
                        idElement.ValueKind != JsonValueKind.String ||
                        idElement.GetString() is not { Length: > 0 } id ||
                        !seen.Add(id))
                    {
                        continue;
                    }

                    var name = entry.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(n.GetString()) ? n.GetString()! : id;
                    result.Add(new ProviderModel(Id, id, name, CapabilitiesFor(id, entry)));
                }
            }

            return result;
        }
    }

    /// <inheritdoc/>
    public IChatClient GetChatClient(string modelId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        return new OpenAIChatClient(this, modelId);
    }

    /// <inheritdoc/>
    public IFimClient? GetFimClient(string modelId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);

        // Cloud endpoints are chat-framed only (design section 6); a local server must declare that it implements
        // /completions with suffix.
        return IsLocal && Options.SupportsFim ? new OpenAIFimClient(this, modelId) : null;
    }

    /// <inheritdoc/>
    public async Task<ProviderProbeResult> ProbeAsync(CancellationToken cancellationToken)
    {
        var probe = await ProbeDetailedAsync(cancellationToken).ConfigureAwait(false);

        // A server that rejects the key answered, but cannot be used.
        return new ProviderProbeResult(probe.IsReachable && probe.IsAuthenticated != false, probe.Server, probe.Message);
    }

    /// <summary>
    /// Asks <c>GET /models</c> (with the key, if there is one) and reports whether the server answered, whether it accepted
    /// the key, and how it identifies itself. Never throws for an unreachable server.
    /// </summary>
    /// <param name="cancellationToken">A token that cancels the probe.</param>
    /// <returns>The result.</returns>
    public async Task<OpenAIProbeResult> ProbeDetailedAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));

        HttpStatusCode status;
        string? server;
        JsonDocument? body;
        bool keySent;
        try
        {
            (status, server, body, keySent) = await _http.ProbeAsync("models", timeout.Token).ConfigureAwait(false);
        }
        catch (ProviderException ex)
        {
            return new OpenAIProbeResult(false, null, null, null, ex.Message);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new OpenAIProbeResult(false, null, null, null, $"{DisplayName} at {Options.Endpoint} did not respond within 15 seconds.");
        }

        using (body)
        {
            var identity = server is null ? string.Empty : $" ({server})";
            var number = (int)status;
            if (status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                var message = keySent
                    ? $"Reached {Options.Endpoint}{identity}, but it rejected the API key (HTTP {number}). Check the key stored for this provider."
                    : $"Reached {Options.Endpoint}{identity}, but it requires an API key (HTTP {number}) and none is stored.";
                return new OpenAIProbeResult(true, false, server, null, message);
            }

            if (status is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotImplemented)
            {
                return new OpenAIProbeResult(
                    true,
                    keySent ? true : null,
                    server,
                    null,
                    $"Reached {Options.Endpoint}{identity}, but it has no model list (HTTP {number}); the configured models are used. If this is unexpected, check that the endpoint includes the API version, for example /v1.");
            }

            if ((int)status is < 200 or >= 300)
            {
                return new OpenAIProbeResult(false, null, server, null, $"{DisplayName} at {Options.Endpoint}{identity} answered HTTP {number}.");
            }

            int? count = null;
            if (body is not null && body.RootElement.ValueKind == JsonValueKind.Object && body.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            {
                count = data.GetArrayLength();
            }

            var authentication = keySent ? "the API key was accepted" : "no API key needed";
            var models = count is { } c ? $" {c} model{(c == 1 ? string.Empty : "s")} listed." : string.Empty;
            return new OpenAIProbeResult(true, keySent ? true : null, server, count, $"Connected to {Options.Endpoint}{identity}; {authentication}.{models}");
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _httpClient.Dispose();
        _owned?.Dispose();
    }

    private ModelCapabilityDescriptor CapabilitiesFor(string modelId, JsonElement entry)
    {
        var capabilities = _catalog.GetCapabilities(modelId, IsLocal);

        // The /models entries of OpenRouter, vLLM and others carry the context window.
        foreach (var field in ContextFields)
        {
            if (entry.ValueKind == JsonValueKind.Object && entry.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var length) && length > 0)
            {
                capabilities = capabilities with { ContextWindow = length };
                break;
            }
        }

        return capabilities with { Fim = capabilities.Fim && IsLocal && Options.SupportsFim };
    }

    private List<ProviderModel> ConfiguredModels()
    {
        var ids = _configuredModels.Count > 0
            ? _configuredModels.ToList()
            : Enum.GetValues<Profile>()
                .Where(profile => profile != Profile.Auto)
                .Select(profile => _catalog.GetDefaultModel(Kind, profile))
                .OfType<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        return ids.Select(id => new ProviderModel(Id, id, id, CapabilitiesFor(id, default))).ToList();
    }
}

/// <summary>
/// Creates <see cref="OpenAICompatibleProvider"/>s of kind <see cref="ProviderKind.OpenAiCompatible"/>. A key is required
/// unless the endpoint is a loopback address; it is read from the <see cref="ISecretStore"/> (secret name = provider id)
/// for every request.
/// </summary>
public class OpenAICompatibleProviderFactory : IModelProviderFactory
{
    private readonly ISecretStore _secrets;
    private readonly Func<OpenAICompatibleOptions, HttpMessageHandler>? _handlerFactory;

    /// <summary>Initializes a new instance of the <see cref="OpenAICompatibleProviderFactory"/> class with the real HTTP handler.</summary>
    /// <param name="secrets">The secret store holding API keys.</param>
    public OpenAICompatibleProviderFactory(ISecretStore secrets)
        : this(secrets, null)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="OpenAICompatibleProviderFactory"/> class.</summary>
    /// <param name="secrets">The secret store holding API keys.</param>
    /// <param name="handlerFactory">Creates the HTTP handler for each provider (tests pass a fake), or <see langword="null"/> for the real one with the proxy and CA bundle applied.</param>
    public OpenAICompatibleProviderFactory(ISecretStore secrets, Func<OpenAICompatibleOptions, HttpMessageHandler>? handlerFactory)
    {
        ArgumentNullException.ThrowIfNull(secrets);
        _secrets = secrets;
        _handlerFactory = handlerFactory;
    }

    /// <inheritdoc/>
    public virtual ProviderKind Kind => ProviderKind.OpenAiCompatible;

    /// <inheritdoc/>
    public bool RequiresApiKey(ProviderSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        try
        {
            // Local servers rarely need a key; an unreadable endpoint is reported by Create, not as a missing key.
            return OpenAICompatibleOptions.ReadEndpoint(settings) is { IsLoopback: false };
        }
        catch (UriFormatException)
        {
            return false;
        }
    }

    /// <inheritdoc/>
    public IModelProvider Create(ProviderSettings settings, ProviderCreationContext context)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(context);

        var kindSettings = settings.Kind == Kind ? settings : throw new ArgumentException($"The provider '{settings.Id}' is not of kind {ProviderKinds.ToName(Kind)}.", nameof(settings));
        var options = OpenAICompatibleOptions.FromSettings(kindSettings);

        HttpMessageHandler handler;
        DisposableCertificates? owned = null;
        if (_handlerFactory is not null)
        {
            handler = _handlerFactory(options);
        }
        else
        {
            handler = OpenAITransport.CreateHandler(options, out var roots);
            owned = roots is null ? null : new DisposableCertificates(roots);
        }

        // Streaming responses can take minutes; the gateway applies its own time limits.
        var client = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
        try
        {
            var providerId = settings.Id;
            return new OpenAICompatibleProvider(
                settings,
                options,
                client,
                () => ReadKey(providerId),
                context.Catalog,
                owned,
                context.LoggerFactory.CreateLogger<OpenAICompatibleProvider>());
        }
        catch
        {
            client.Dispose();
            owned?.Dispose();
            throw;
        }
    }

    private string? ReadKey(string providerId)
    {
        try
        {
            return _secrets.Get(providerId);
        }
        catch (Exception ex) when (ex is ArgumentException or SecretStoreException)
        {
            return null;
        }
    }
}

/// <summary>Creates <see cref="OpenAICompatibleProvider"/>s of kind <see cref="ProviderKind.OpenAi"/>; the endpoint defaults to <c>https://api.openai.com/v1</c>.</summary>
public sealed class OpenAIProviderFactory : OpenAICompatibleProviderFactory
{
    /// <summary>Initializes a new instance of the <see cref="OpenAIProviderFactory"/> class with the real HTTP handler.</summary>
    /// <param name="secrets">The secret store holding API keys.</param>
    public OpenAIProviderFactory(ISecretStore secrets)
        : base(secrets)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="OpenAIProviderFactory"/> class.</summary>
    /// <param name="secrets">The secret store holding API keys.</param>
    /// <param name="handlerFactory">Creates the HTTP handler for each provider (tests pass a fake), or <see langword="null"/>.</param>
    public OpenAIProviderFactory(ISecretStore secrets, Func<OpenAICompatibleOptions, HttpMessageHandler>? handlerFactory)
        : base(secrets, handlerFactory)
    {
    }

    /// <inheritdoc/>
    public override ProviderKind Kind => ProviderKind.OpenAi;
}
