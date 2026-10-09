using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Pillion.Common.Secrets;
using Pillion.Common.Settings;
using Pillion.ModelGateway.Providers.Cloud;

namespace Pillion.ModelGateway.Providers.Gemini;

/// <summary>
/// The Google Gemini adapter (design section 6): the Gemini API over <see cref="HttpClient"/>, no SDK. Chat is streamed
/// from <c>POST /v1beta/models/{model}:streamGenerateContent?alt=sse</c>; models come from <c>GET /v1beta/models</c>. The
/// key goes in the <c>x-goog-api-key</c> header and is read from the <see cref="ISecretStore"/> for every request.
/// Gemini has no fill-in-the-middle endpoint, so <see cref="GetFimClient"/> returns <see langword="null"/>. Embeddings
/// (<c>embedContent</c>) are not implemented: the gateway has no embedding path to plug them into yet.
/// </summary>
public sealed class GeminiProvider : IModelProvider
{
    /// <summary>The API version segment used unless the entry sets <c>"apiVersion"</c>.</summary>
    public const string DefaultApiVersion = "v1beta";

    /// <summary>The endpoint used when the entry sets none.</summary>
    public static readonly Uri DefaultEndpoint = new("https://generativelanguage.googleapis.com/");

    private const int MaxListPages = 5;

    private readonly HttpClient _httpClient;
    private readonly IDisposable? _owned;

    /// <summary>Initializes a new instance of the <see cref="GeminiProvider"/> class.</summary>
    /// <param name="settings">The provider settings.</param>
    /// <param name="endpoint">The base endpoint, ending with <c>/</c>.</param>
    /// <param name="httpClient">The HTTP client; the provider owns and disposes it.</param>
    /// <param name="keyProvider">Reads the API key for each request; the key is never kept.</param>
    /// <param name="catalog">The model catalog.</param>
    /// <param name="owned">Something the provider disposes with the client, or <see langword="null"/>.</param>
    /// <param name="logger">The logger.</param>
    public GeminiProvider(
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
        DisplayName = !string.IsNullOrWhiteSpace(settings.DisplayName) ? settings.DisplayName : "Google Gemini";
        ApiVersion = CloudTransport.ReadString(settings, "apiVersion") ?? DefaultApiVersion;
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

                request.Headers.TryAddWithoutValidation("x-goog-api-key", key);
                return new ValueTask<bool>(true);
            },
            new CloudErrorStyle
            {
                NotFoundMeansModel = true,

                // Google reports a bad key as 400 INVALID_ARGUMENT with reason API_KEY_INVALID.
                IsCredentialRejected = lower => lower.Contains("api key not valid", StringComparison.Ordinal) ||
                    lower.Contains("api_key_invalid", StringComparison.Ordinal) ||
                    lower.Contains("api key expired", StringComparison.Ordinal) ||
                    lower.Contains("api key was reported as leaked", StringComparison.Ordinal),
            },
            logger ?? NullLogger.Instance);
    }

    /// <inheritdoc/>
    public string Id { get; }

    /// <inheritdoc/>
    public ProviderKind Kind => ProviderKind.Gemini;

    /// <inheritdoc/>
    public string DisplayName { get; }

    /// <inheritdoc/>
    public bool IsLocal => false;

    /// <summary>Gets the API version segment of request paths (<c>v1beta</c> by default).</summary>
    public string ApiVersion { get; }

    /// <summary>Gets the base endpoint, ending with <c>/</c>.</summary>
    public Uri Endpoint { get; }

    internal CloudHttp Http { get; }

    internal ModelCatalog Catalog { get; }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<ProviderModel>> ListModelsAsync(CancellationToken cancellationToken)
    {
        var result = new List<ProviderModel>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? pageToken = null;
        for (var page = 0; page < MaxListPages; page++)
        {
            var uri = $"{ApiVersion}/models?pageSize=1000" + (pageToken is null ? string.Empty : "&pageToken=" + Uri.EscapeDataString(pageToken));
            using var document = await Http.GetJsonAsync(uri, cancellationToken).ConfigureAwait(false);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array)
            {
                break;
            }

            foreach (var entry in models.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object ||
                    ReadString(entry, "name") is not { Length: > 0 } name)
                {
                    continue;
                }

                var id = StripPrefix(name);
                if (!seen.Add(id))
                {
                    continue;
                }

                result.Add(new ProviderModel(Id, id, ReadString(entry, "displayName") ?? id, CapabilitiesFor(id, entry)));
            }

            pageToken = ReadString(root, "nextPageToken");
            if (string.IsNullOrEmpty(pageToken))
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
        return new GeminiChatClient(this, StripPrefix(modelId));
    }

    /// <inheritdoc/>
    public IFimClient? GetFimClient(string modelId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);

        // Chat-framed only (design section 6): Gemini has no fill-in-the-middle endpoint.
        return null;
    }

    /// <inheritdoc/>
    public async Task<ProviderProbeResult> ProbeAsync(CancellationToken cancellationToken) =>
        (await ProbeDetailedAsync(cancellationToken).ConfigureAwait(false)).ToProbeResult();

    /// <summary>
    /// Asks <c>GET /v1beta/models</c> with the key and reports whether the service answered and accepted the key. Never
    /// throws for an unreachable service. Google answers an invalid key with HTTP 400, which is reported as a rejected key.
    /// </summary>
    /// <param name="cancellationToken">A token that cancels the probe.</param>
    /// <returns>The result.</returns>
    public async Task<CloudProbeResult> ProbeDetailedAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            using var response = await Http.ProbeAsync($"{ApiVersion}/models?pageSize=1", timeout.Token).ConfigureAwait(false);
            if (response.Status == System.Net.HttpStatusCode.BadRequest)
            {
                return new CloudProbeResult(true, false, null, $"Reached {Endpoint}, but it rejected the request (HTTP 400), which Google also sends for an invalid API key. Check the key stored for this provider.");
            }

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

    /// <summary>Removes the <c>models/</c> prefix Google puts on model names.</summary>
    /// <param name="name">A model name or id.</param>
    /// <returns>The bare model id.</returns>
    internal static string StripPrefix(string name) =>
        name.StartsWith("models/", StringComparison.Ordinal) ? name["models/".Length..] : name;

    private static int? CountModels(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty("models", out var models) && models.ValueKind == JsonValueKind.Array
            ? models.GetArrayLength()
            : null;

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private ModelCapabilityDescriptor CapabilitiesFor(string modelId, JsonElement entry)
    {
        var capabilities = Catalog.GetCapabilities(modelId, isLocal: false);

        // The model list reports the token limits, which win over the catalog.
        if (entry.TryGetProperty("inputTokenLimit", out var input) && input.ValueKind == JsonValueKind.Number && input.TryGetInt32(out var window) && window > 0)
        {
            capabilities = capabilities with { ContextWindow = window };
        }

        if (entry.TryGetProperty("outputTokenLimit", out var output) && output.ValueKind == JsonValueKind.Number && output.TryGetInt32(out var max) && max > 0)
        {
            capabilities = capabilities with { MaxOutputTokens = max };
        }

        // supportedGenerationMethods names what the model can do: chat models list generateContent, embedding models embedContent.
        if (entry.TryGetProperty("supportedGenerationMethods", out var methods) && methods.ValueKind == JsonValueKind.Array)
        {
            var names = methods.EnumerateArray().Where(m => m.ValueKind == JsonValueKind.String).Select(m => m.GetString()).ToList();
            capabilities = capabilities with
            {
                Chat = names.Contains("generateContent", StringComparer.Ordinal) || names.Contains("streamGenerateContent", StringComparer.Ordinal),
                Embeddings = names.Contains("embedContent", StringComparer.Ordinal) || names.Contains("batchEmbedContents", StringComparer.Ordinal),
            };
        }

        return capabilities with { Fim = false };
    }
}

/// <summary>
/// Creates <see cref="GeminiProvider"/>s of kind <see cref="ProviderKind.Gemini"/>. A key is always required; it is read
/// from the <see cref="ISecretStore"/> (secret name = provider id) for every request.
/// </summary>
public sealed class GeminiProviderFactory : IModelProviderFactory
{
    private readonly ISecretStore _secrets;
    private readonly Func<HttpMessageHandler>? _handlerFactory;

    /// <summary>Initializes a new instance of the <see cref="GeminiProviderFactory"/> class with the real HTTP handler.</summary>
    /// <param name="secrets">The secret store holding API keys.</param>
    public GeminiProviderFactory(ISecretStore secrets)
        : this(secrets, null)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="GeminiProviderFactory"/> class.</summary>
    /// <param name="secrets">The secret store holding API keys.</param>
    /// <param name="handlerFactory">Creates the HTTP handler for each provider (tests pass a fake), or <see langword="null"/> for the real one with the proxy and CA bundle applied.</param>
    public GeminiProviderFactory(ISecretStore secrets, Func<HttpMessageHandler>? handlerFactory)
    {
        ArgumentNullException.ThrowIfNull(secrets);
        _secrets = secrets;
        _handlerFactory = handlerFactory;
    }

    /// <inheritdoc/>
    public ProviderKind Kind => ProviderKind.Gemini;

    /// <inheritdoc/>
    public string? FeatureFlag => FeatureNames.GeminiProvider;

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

        var endpoint = CloudTransport.ResolveEndpoint(settings, GeminiProvider.DefaultEndpoint);
        var client = CloudTransport.CreateClient(settings, _handlerFactory, out var owned);
        try
        {
            var providerId = settings.Id;
            return new GeminiProvider(
                settings,
                endpoint,
                client,
                () => CloudTransport.ReadKey(_secrets, providerId),
                context.Catalog,
                owned,
                context.LoggerFactory.CreateLogger<GeminiProvider>());
        }
        catch
        {
            client.Dispose();
            owned?.Dispose();
            throw;
        }
    }
}
