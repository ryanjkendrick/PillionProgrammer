using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Pillion.Common.Secrets;
using Pillion.Common.Settings;
using Pillion.ModelGateway.Providers.Cloud;

namespace Pillion.ModelGateway.Providers.Mistral;

/// <summary>
/// The Mistral adapter (design section 6): chat over the OpenAI-shaped <c>POST /v1/chat/completions</c>, and
/// fill-in-the-middle for Codestral models over Mistral's own <c>POST /v1/fim/completions</c> (a dedicated adapter, not
/// an OpenAI-compatible endpoint). Models come from <c>GET /v1/models</c>. The key is read from the
/// <see cref="ISecretStore"/> for every request and never kept. The chat request body and the stream parser are the
/// OpenAI-compatible adapter's, shared as internal helpers, but the transport, the errors and the endpoint are not.
/// </summary>
public sealed class MistralProvider : IModelProvider
{
    /// <summary>The endpoint used when the entry sets none.</summary>
    public static readonly Uri DefaultEndpoint = new("https://api.mistral.ai/v1/");

    private readonly HttpClient _httpClient;
    private readonly IDisposable? _owned;

    /// <summary>Initializes a new instance of the <see cref="MistralProvider"/> class.</summary>
    /// <param name="settings">The provider settings.</param>
    /// <param name="endpoint">The base endpoint including the version segment, ending with <c>/</c>.</param>
    /// <param name="httpClient">The HTTP client; the provider owns and disposes it.</param>
    /// <param name="keyProvider">Reads the API key for each request; the key is never kept.</param>
    /// <param name="catalog">The model catalog.</param>
    /// <param name="owned">Something the provider disposes with the client, or <see langword="null"/>.</param>
    /// <param name="logger">The logger.</param>
    public MistralProvider(
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
        DisplayName = !string.IsNullOrWhiteSpace(settings.DisplayName) ? settings.DisplayName : "Mistral";
        Endpoint = endpoint;
        Catalog = catalog;
        _httpClient = httpClient;
        _owned = owned;
        var providerId = Id;
        Http = new CloudHttp(
            httpClient,
            endpoint,
            providerId,
            DisplayName,
            (request, _) =>
            {
                var key = CloudHttp.CheckKey(keyProvider(), providerId);
                if (key is null)
                {
                    return new ValueTask<bool>(false);
                }

                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
                return new ValueTask<bool>(true);
            },
            new CloudErrorStyle { NotFoundMeansModel = true },
            logger ?? NullLogger.Instance);
    }

    /// <inheritdoc/>
    public string Id { get; }

    /// <inheritdoc/>
    public ProviderKind Kind => ProviderKind.Mistral;

    /// <inheritdoc/>
    public string DisplayName { get; }

    /// <inheritdoc/>
    public bool IsLocal => false;

    /// <summary>Gets the base endpoint including the version segment, ending with <c>/</c>.</summary>
    public Uri Endpoint { get; }

    internal CloudHttp Http { get; }

    internal ModelCatalog Catalog { get; }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<ProviderModel>> ListModelsAsync(CancellationToken cancellationToken)
    {
        using var document = await Http.GetJsonAsync("models", cancellationToken).ConfigureAwait(false);
        var result = new List<ProviderModel>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        // The documented schema is {"object":"list","data":[...]}, but the documented example is a bare array: accept both.
        var root = document.RootElement;
        var data = root.ValueKind == JsonValueKind.Array
            ? root
            : root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var inner) ? inner : default;
        if (data.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var entry in data.EnumerateArray())
        {
            if (entry.ValueKind == JsonValueKind.Object &&
                ReadString(entry, "id") is { Length: > 0 } id &&
                seen.Add(id))
            {
                var name = ReadString(entry, "name") ?? id;
                result.Add(new ProviderModel(Id, id, name, CapabilitiesFor(id, entry)));
            }
        }

        return result;
    }

    /// <inheritdoc/>
    public IChatClient GetChatClient(string modelId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        return new MistralChatClient(this, modelId);
    }

    /// <inheritdoc/>
    public IFimClient? GetFimClient(string modelId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);

        // Fill-in-the-middle exists only for the Codestral family, which the catalog marks.
        return Catalog.GetCapabilities(modelId, isLocal: false).Fim ? new MistralFimClient(this, modelId) : null;
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
            using var response = await Http.ProbeAsync("models", timeout.Token).ConfigureAwait(false);
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

    private static int? CountModels(JsonElement root) => root.ValueKind switch
    {
        JsonValueKind.Array => root.GetArrayLength(),
        JsonValueKind.Object when root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array => data.GetArrayLength(),
        _ => null,
    };

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private ModelCapabilityDescriptor CapabilitiesFor(string modelId, JsonElement entry)
    {
        var capabilities = Catalog.GetCapabilities(modelId, isLocal: false);

        if (entry.TryGetProperty("max_context_length", out var length) && length.ValueKind == JsonValueKind.Number && length.TryGetInt32(out var window) && window > 0)
        {
            capabilities = capabilities with { ContextWindow = window };
        }

        // The listing's capabilities win over the catalog: completion_fim marks the Codestral models.
        if (entry.TryGetProperty("capabilities", out var flags) && flags.ValueKind == JsonValueKind.Object)
        {
            capabilities = capabilities with
            {
                Chat = Flag(flags, "completion_chat") ?? capabilities.Chat,
                Fim = Flag(flags, "completion_fim") ?? capabilities.Fim,
                Vision = Flag(flags, "vision") ?? capabilities.Vision,
                ToolCalling = Flag(flags, "function_calling") is { } tools ? (tools ? ToolCallingSupport.Native : ToolCallingSupport.None) : capabilities.ToolCalling,
            };
        }

        return capabilities;
    }

    private static bool? Flag(JsonElement flags, string name) =>
        flags.TryGetProperty(name, out var value) && (value.ValueKind is JsonValueKind.True or JsonValueKind.False) ? value.GetBoolean() : null;
}

/// <summary>
/// Creates <see cref="MistralProvider"/>s of kind <see cref="ProviderKind.Mistral"/>. A key is always required; it is
/// read from the <see cref="ISecretStore"/> (secret name = provider id) for every request.
/// </summary>
public sealed class MistralProviderFactory : IModelProviderFactory
{
    private readonly ISecretStore _secrets;
    private readonly Func<HttpMessageHandler>? _handlerFactory;

    /// <summary>Initializes a new instance of the <see cref="MistralProviderFactory"/> class with the real HTTP handler.</summary>
    /// <param name="secrets">The secret store holding API keys.</param>
    public MistralProviderFactory(ISecretStore secrets)
        : this(secrets, null)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="MistralProviderFactory"/> class.</summary>
    /// <param name="secrets">The secret store holding API keys.</param>
    /// <param name="handlerFactory">Creates the HTTP handler for each provider (tests pass a fake), or <see langword="null"/> for the real one with the proxy and CA bundle applied.</param>
    public MistralProviderFactory(ISecretStore secrets, Func<HttpMessageHandler>? handlerFactory)
    {
        ArgumentNullException.ThrowIfNull(secrets);
        _secrets = secrets;
        _handlerFactory = handlerFactory;
    }

    /// <inheritdoc/>
    public ProviderKind Kind => ProviderKind.Mistral;

    /// <inheritdoc/>
    public string? FeatureFlag => FeatureNames.MistralProvider;

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

        // A bare host means the usual /v1 root.
        var endpoint = CloudTransport.ResolveEndpoint(settings, MistralProvider.DefaultEndpoint, bareHostPath: "/v1/");
        var client = CloudTransport.CreateClient(settings, _handlerFactory, out var owned);
        try
        {
            var providerId = settings.Id;
            return new MistralProvider(
                settings,
                endpoint,
                client,
                () => CloudTransport.ReadKey(_secrets, providerId),
                context.Catalog,
                owned,
                context.LoggerFactory.CreateLogger<MistralProvider>());
        }
        catch
        {
            client.Dispose();
            owned?.Dispose();
            throw;
        }
    }
}
