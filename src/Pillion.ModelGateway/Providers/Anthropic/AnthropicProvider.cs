using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Pillion.Common.Secrets;
using Pillion.Common.Settings;
using Pillion.ModelGateway.Providers.Cloud;

namespace Pillion.ModelGateway.Providers.Anthropic;

/// <summary>
/// The Anthropic adapter (design section 6): the Messages API over <see cref="HttpClient"/>, no SDK. Chat is streamed
/// from <c>POST /v1/messages</c>; models come from <c>GET /v1/models</c>. Anthropic has no fill-in-the-middle endpoint and
/// no embeddings, so <see cref="GetFimClient"/> returns <see langword="null"/> (autocomplete on Claude is chat-framed
/// only, and only when the autocomplete cloud switch is on). The API key is read from the <see cref="ISecretStore"/> for
/// every request and never kept.
/// </summary>
public sealed class AnthropicProvider : IModelProvider
{
    /// <summary>The <c>anthropic-version</c> header value sent unless the entry sets <c>"anthropicVersion"</c>.</summary>
    public const string DefaultApiVersion = "2023-06-01";

    /// <summary>The endpoint used when the entry sets none.</summary>
    public static readonly Uri DefaultEndpoint = new("https://api.anthropic.com/");

    private const int MaxListPages = 5;

    private readonly HttpClient _httpClient;
    private readonly IDisposable? _owned;
    private readonly ModelCatalog _catalog;

    /// <summary>Initializes a new instance of the <see cref="AnthropicProvider"/> class.</summary>
    /// <param name="settings">The provider settings.</param>
    /// <param name="endpoint">The base endpoint, ending with <c>/</c>.</param>
    /// <param name="httpClient">The HTTP client; the provider owns and disposes it.</param>
    /// <param name="keyProvider">Reads the API key for each request; the key is never kept.</param>
    /// <param name="catalog">The model catalog.</param>
    /// <param name="owned">Something the provider disposes with the client, or <see langword="null"/>.</param>
    /// <param name="logger">The logger.</param>
    public AnthropicProvider(
        ProviderSettings settings,
        Uri endpoint,
        HttpClient httpClient,
        Func<string?> keyProvider,
        ModelCatalog catalog,
        IDisposable? owned = null,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(keyProvider);
        ArgumentNullException.ThrowIfNull(catalog);

        Id = settings.Id;
        DisplayName = !string.IsNullOrWhiteSpace(settings.DisplayName) ? settings.DisplayName : "Anthropic";
        ApiVersion = CloudTransport.ReadString(settings, "anthropicVersion") ?? DefaultApiVersion;
        Endpoint = endpoint;
        _httpClient = httpClient;
        _owned = owned;
        _catalog = catalog;
        var providerId = Id;
        Http = new CloudHttp(
            httpClient,
            endpoint,
            providerId,
            DisplayName,
            (request, _) =>
            {
                request.Headers.TryAddWithoutValidation("anthropic-version", ApiVersion);
                var key = CloudHttp.CheckKey(keyProvider(), providerId);
                if (key is null)
                {
                    return new ValueTask<bool>(false);
                }

                request.Headers.TryAddWithoutValidation("x-api-key", key);
                return new ValueTask<bool>(true);
            },
            new CloudErrorStyle { NotFoundMeansModel = true },
            logger ?? NullLogger.Instance);
    }

    /// <inheritdoc/>
    public string Id { get; }

    /// <inheritdoc/>
    public ProviderKind Kind => ProviderKind.Anthropic;

    /// <inheritdoc/>
    public string DisplayName { get; }

    /// <inheritdoc/>
    public bool IsLocal => false;

    /// <summary>Gets the <c>anthropic-version</c> header value this provider sends.</summary>
    public string ApiVersion { get; }

    /// <summary>Gets the base endpoint, ending with <c>/</c>.</summary>
    public Uri Endpoint { get; }

    internal CloudHttp Http { get; }

    internal ModelCatalog Catalog => _catalog;

    /// <inheritdoc/>
    public async Task<IReadOnlyList<ProviderModel>> ListModelsAsync(CancellationToken cancellationToken)
    {
        var result = new List<ProviderModel>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? after = null;
        for (var page = 0; page < MaxListPages; page++)
        {
            var uri = "v1/models?limit=1000" + (after is null ? string.Empty : "&after_id=" + Uri.EscapeDataString(after));
            using var document = await Http.GetJsonAsync(uri, cancellationToken).ConfigureAwait(false);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            {
                break;
            }

            foreach (var entry in data.EnumerateArray())
            {
                if (entry.ValueKind == JsonValueKind.Object &&
                    entry.TryGetProperty("id", out var idElement) &&
                    idElement.ValueKind == JsonValueKind.String &&
                    idElement.GetString() is { Length: > 0 } id &&
                    seen.Add(id))
                {
                    var name = ReadString(entry, "display_name") ?? id;
                    result.Add(new ProviderModel(Id, id, name, CapabilitiesFor(id, entry)));
                }
            }

            after = root.TryGetProperty("has_more", out var more) && more.ValueKind == JsonValueKind.True && root.TryGetProperty("last_id", out var last) && last.ValueKind == JsonValueKind.String
                ? last.GetString()
                : null;
            if (after is null)
            {
                break;
            }
        }

        return result;
    }

    /// <inheritdoc/>
    public IChatClient GetChatClient(string modelId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        return new AnthropicChatClient(this, modelId);
    }

    /// <inheritdoc/>
    public IFimClient? GetFimClient(string modelId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);

        // Chat-framed only (design section 6): Anthropic has no fill-in-the-middle endpoint.
        return null;
    }

    /// <inheritdoc/>
    public async Task<ProviderProbeResult> ProbeAsync(CancellationToken cancellationToken) =>
        (await ProbeDetailedAsync(cancellationToken).ConfigureAwait(false)).ToProbeResult();

    /// <summary>
    /// Asks <c>GET /v1/models</c> with the key and reports whether the service answered and accepted the key. Never throws
    /// for an unreachable service.
    /// </summary>
    /// <param name="cancellationToken">A token that cancels the probe.</param>
    /// <returns>The result.</returns>
    public async Task<CloudProbeResult> ProbeDetailedAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            using var response = await Http.ProbeAsync("v1/models?limit=1", timeout.Token).ConfigureAwait(false);
            return CloudTransport.Interpret(response, DisplayName, Endpoint, "API key", CountModels);
        }
        catch (ProviderException ex)
        {
            return CloudProbeResult.Unreachable(ex.Message);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return CloudProbeResult.Unreachable($"{DisplayName} at {Endpoint} did not respond within 15 seconds.");
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _httpClient.Dispose();
        _owned?.Dispose();
    }

    private static int? CountModels(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array
            ? data.GetArrayLength()
            : null;

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private ModelCapabilityDescriptor CapabilitiesFor(string modelId, JsonElement entry)
    {
        var capabilities = _catalog.GetCapabilities(modelId, isLocal: false);

        // The Models API reports the limits and the capabilities of each model, which win over the catalog.
        if (entry.TryGetProperty("max_input_tokens", out var input) && input.ValueKind == JsonValueKind.Number && input.TryGetInt32(out var window) && window > 0)
        {
            capabilities = capabilities with { ContextWindow = window };
        }

        if (entry.TryGetProperty("max_tokens", out var output) && output.ValueKind == JsonValueKind.Number && output.TryGetInt32(out var max) && max > 0)
        {
            capabilities = capabilities with { MaxOutputTokens = max };
        }

        if (entry.TryGetProperty("capabilities", out var flags) && flags.ValueKind == JsonValueKind.Object)
        {
            if (Supports(flags, "image_input") is { } vision)
            {
                capabilities = capabilities with { Vision = vision };
            }

            if (Supports(flags, "effort") is { } effort)
            {
                capabilities = capabilities with { ReasoningEffort = effort };
            }
        }

        return capabilities with { Fim = false, Embeddings = false, Chat = true };
    }

    private static bool? Supports(JsonElement capabilities, string name) =>
        capabilities.TryGetProperty(name, out var node) &&
        node.ValueKind == JsonValueKind.Object &&
        node.TryGetProperty("supported", out var supported) &&
        (supported.ValueKind is JsonValueKind.True or JsonValueKind.False)
            ? supported.GetBoolean()
            : null;
}

/// <summary>
/// Creates <see cref="AnthropicProvider"/>s of kind <see cref="ProviderKind.Anthropic"/>. A key is always required; it is
/// read from the <see cref="ISecretStore"/> (secret name = provider id) for every request.
/// </summary>
public sealed class AnthropicProviderFactory : IModelProviderFactory
{
    private readonly ISecretStore _secrets;
    private readonly Func<HttpMessageHandler>? _handlerFactory;

    /// <summary>Initializes a new instance of the <see cref="AnthropicProviderFactory"/> class with the real HTTP handler.</summary>
    /// <param name="secrets">The secret store holding API keys.</param>
    public AnthropicProviderFactory(ISecretStore secrets)
        : this(secrets, null)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="AnthropicProviderFactory"/> class.</summary>
    /// <param name="secrets">The secret store holding API keys.</param>
    /// <param name="handlerFactory">Creates the HTTP handler for each provider (tests pass a fake), or <see langword="null"/> for the real one with the proxy and CA bundle applied.</param>
    public AnthropicProviderFactory(ISecretStore secrets, Func<HttpMessageHandler>? handlerFactory)
    {
        ArgumentNullException.ThrowIfNull(secrets);
        _secrets = secrets;
        _handlerFactory = handlerFactory;
    }

    /// <inheritdoc/>
    public ProviderKind Kind => ProviderKind.Anthropic;

    /// <inheritdoc/>
    public string? FeatureFlag => FeatureNames.AnthropicProvider;

    /// <inheritdoc/>
    public bool RequiresApiKey(ProviderSettings settings) => true;

    /// <inheritdoc/>
    public IModelProvider Create(ProviderSettings settings, ProviderCreationContext context)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(context);

        if (settings.Kind != Kind)
        {
            throw new ArgumentException($"The provider '{settings.Id}' is not of kind {ProviderKinds.ToName(Kind)}.", nameof(settings));
        }

        var endpoint = CloudTransport.ResolveEndpoint(settings, AnthropicProvider.DefaultEndpoint);
        var client = CloudTransport.CreateClient(settings, _handlerFactory, out var owned);
        try
        {
            var providerId = settings.Id;
            return new AnthropicProvider(
                settings,
                endpoint,
                client,
                () => CloudTransport.ReadKey(_secrets, providerId),
                context.Catalog,
                owned,
                context.LoggerFactory.CreateLogger<AnthropicProvider>());
        }
        catch
        {
            client.Dispose();
            owned?.Dispose();
            throw;
        }
    }
}
