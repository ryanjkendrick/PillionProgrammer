using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Pillion.Common.Secrets;
using Pillion.Common.Settings;
using Pillion.Contracts;
using Pillion.ModelGateway.Providers.Cloud;

namespace Pillion.ModelGateway.Providers.AzureOpenAI;

/// <summary>How an Azure OpenAI provider authenticates.</summary>
public enum AzureAuthentication
{
    /// <summary>Use the stored API key when there is one, else Microsoft Entra ID when the host supplies a credential provider.</summary>
    Auto,

    /// <summary>Use only the API key (<c>api-key</c> header).</summary>
    ApiKey,

    /// <summary>Use only a Microsoft Entra ID bearer token from the <see cref="ITokenCredentialProvider"/>.</summary>
    Entra,
}

/// <summary>
/// The Azure OpenAI adapter (design section 6): chat completions on a deployment,
/// <c>POST {endpoint}/openai/deployments/{deployment}/chat/completions?api-version={version}</c>, over
/// <see cref="HttpClient"/>, no SDK. The entry's <c>models</c> are deployment names, and listing returns them. The request
/// body and the stream parser are the OpenAI-compatible adapter's. Authentication is the <c>api-key</c> header with the
/// stored key, or a Microsoft Entra ID bearer token from an <see cref="ITokenCredentialProvider"/>. Fill-in-the-middle is
/// not offered (chat-framed only).
/// </summary>
public sealed class AzureOpenAIProvider : IModelProvider
{
    /// <summary>
    /// The <c>api-version</c> sent unless the entry sets <c>"apiVersion"</c>: the latest generally available data plane
    /// inference version on Microsoft Learn (October 2026).
    /// </summary>
    public const string DefaultApiVersion = "2024-10-21";

    /// <summary>The token scope requested from the credential provider unless the entry sets <c>"tokenScope"</c>.</summary>
    public const string DefaultTokenScope = "https://cognitiveservices.azure.com/.default";

    private readonly HttpClient _httpClient;
    private readonly IDisposable? _owned;
    private readonly IReadOnlyList<string> _deployments;
    private readonly Dictionary<string, string> _families;

    /// <summary>Initializes a new instance of the <see cref="AzureOpenAIProvider"/> class.</summary>
    /// <param name="settings">The provider settings; <c>models</c> are the deployment names.</param>
    /// <param name="endpoint">The resource endpoint, ending with <c>/</c>.</param>
    /// <param name="httpClient">The HTTP client; the provider owns and disposes it.</param>
    /// <param name="keyProvider">Reads the API key for each request; the key is never kept.</param>
    /// <param name="credentials">The Entra ID token source, or <see langword="null"/> when the host has none.</param>
    /// <param name="catalog">The model catalog.</param>
    /// <param name="owned">Something the provider disposes with the client, or <see langword="null"/>.</param>
    /// <param name="logger">The logger.</param>
    /// <exception cref="InvalidOperationException">Entra ID authentication is configured and the host has no credential provider.</exception>
    public AzureOpenAIProvider(
        ProviderSettings settings,
        Uri endpoint,
        HttpClient httpClient,
        Func<string?> keyProvider,
        ITokenCredentialProvider? credentials,
        ModelCatalog catalog,
        IDisposable? owned = null,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(keyProvider);
        ArgumentNullException.ThrowIfNull(catalog);

        Authentication = ReadAuthentication(settings);
        if (Authentication == AzureAuthentication.Entra && credentials is null)
        {
            throw new InvalidOperationException("Microsoft Entra ID authentication is set for this provider, but this host has no Entra ID credential provider. Use an API key or update Pillion.");
        }

        Id = settings.Id;
        DisplayName = !string.IsNullOrWhiteSpace(settings.DisplayName) ? settings.DisplayName : "Azure OpenAI";
        ApiVersion = CloudTransport.ReadString(settings, "apiVersion") ?? DefaultApiVersion;
        TokenScope = CloudTransport.ReadString(settings, "tokenScope") ?? DefaultTokenScope;
        Endpoint = endpoint;
        Catalog = catalog;
        _httpClient = httpClient;
        _owned = owned;
        _deployments = [.. settings.Models.Where(m => !string.IsNullOrWhiteSpace(m)).Distinct(StringComparer.OrdinalIgnoreCase)];
        _families = ReadFamilies(settings);
        var providerId = Id;
        var authentication = Authentication;
        var scope = TokenScope;
        Http = new CloudHttp(
            httpClient,
            endpoint,
            providerId,
            DisplayName,
            async (request, cancellationToken) =>
            {
                var key = authentication == AzureAuthentication.Entra ? null : CloudHttp.CheckKey(keyProvider(), providerId);
                if (key is not null)
                {
                    request.Headers.TryAddWithoutValidation("api-key", key);
                    return true;
                }

                if (credentials is null || authentication == AzureAuthentication.ApiKey)
                {
                    return false;
                }

                string token;
                try
                {
                    token = await credentials.GetTokenAsync(scope, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    throw new ProviderException(
                        PillionErrorCode.ProviderUnavailable,
                        $"Pillion could not get a Microsoft Entra ID token for Azure OpenAI ({ex.GetType().Name}). Sign in to Azure (for example with the Azure CLI) and try again.",
                        statusCode: null,
                        ex,
                        isProviderFault: false);
                }

                token = CloudHttp.CheckKey(token, providerId) ?? throw new ProviderException(
                    PillionErrorCode.ProviderUnavailable,
                    "The Microsoft Entra ID credential provider returned an empty token.",
                    isProviderFault: false);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                return true;
            },
            new CloudErrorStyle
            {
                CredentialName = "credential",
                NotFoundMeansModel = true,
                IsModelMissing = lower => lower.Contains("deploymentnotfound", StringComparison.Ordinal),
            },
            logger ?? NullLogger.Instance);
    }

    /// <inheritdoc/>
    public string Id { get; }

    /// <inheritdoc/>
    public ProviderKind Kind => ProviderKind.AzureOpenAi;

    /// <inheritdoc/>
    public string DisplayName { get; }

    /// <inheritdoc/>
    public bool IsLocal => false;

    /// <summary>Gets the <c>api-version</c> this provider sends.</summary>
    public string ApiVersion { get; }

    /// <summary>Gets the token scope requested for Entra ID authentication.</summary>
    public string TokenScope { get; }

    /// <summary>Gets how the provider authenticates.</summary>
    public AzureAuthentication Authentication { get; }

    /// <summary>Gets the resource endpoint, ending with <c>/</c>.</summary>
    public Uri Endpoint { get; }

    internal CloudHttp Http { get; }

    internal ModelCatalog Catalog { get; }

    /// <summary>Reads how the entry authenticates (<c>"authentication"</c>: <c>"auto"</c>, <c>"apiKey"</c> or <c>"entra"</c>).</summary>
    /// <param name="settings">The provider settings.</param>
    /// <returns>The mode.</returns>
    /// <exception cref="InvalidOperationException">The value is not one of the three.</exception>
    public static AzureAuthentication ReadAuthentication(ProviderSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return CloudTransport.ReadString(settings, "authentication")?.ToLowerInvariant() switch
        {
            null or "auto" => AzureAuthentication.Auto,
            "apikey" or "key" => AzureAuthentication.ApiKey,
            "entra" or "entraid" => AzureAuthentication.Entra,
            var other => throw new InvalidOperationException($"The authentication '{other}' is not known. Use \"auto\", \"apiKey\" or \"entra\"."),
        };
    }

    /// <summary>Builds the request path of a chat completion on a deployment (relative to <see cref="Endpoint"/>).</summary>
    /// <param name="deployment">The deployment name.</param>
    /// <returns>The path with the <c>api-version</c> query.</returns>
    public string ChatPath(string deployment) =>
        $"openai/deployments/{Uri.EscapeDataString(deployment)}/chat/completions?api-version={Uri.EscapeDataString(ApiVersion)}";

    /// <inheritdoc/>
    public Task<IReadOnlyList<ProviderModel>> ListModelsAsync(CancellationToken cancellationToken)
    {
        // The deployments of a resource are not listed by the inference API: the configured models are the deployments.
        IReadOnlyList<ProviderModel> models = [.. _deployments.Select(d => new ProviderModel(Id, d, d, CapabilitiesFor(d)))];
        return Task.FromResult(models);
    }

    /// <inheritdoc/>
    public IChatClient GetChatClient(string modelId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        return new AzureOpenAIChatClient(this, modelId);
    }

    /// <inheritdoc/>
    public IFimClient? GetFimClient(string modelId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);

        // Chat-framed only (design section 6).
        return null;
    }

    /// <summary>Builds the request path of an embedding request on a deployment (relative to <see cref="Endpoint"/>).</summary>
    /// <param name="deployment">The deployment name.</param>
    /// <returns>The path with the <c>api-version</c> query.</returns>
    public string EmbeddingsPath(string deployment) =>
        $"openai/deployments/{Uri.EscapeDataString(deployment)}/embeddings?api-version={Uri.EscapeDataString(ApiVersion)}";

    /// <inheritdoc/>
    public IEmbeddingGenerator<string, Embedding<float>>? GetEmbeddingGenerator(string modelId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);

        // The deployment is in the path, so the body does not name the model.
        return new OpenAIShapedEmbeddingGenerator(
            modelId,
            DisplayName,
            Endpoint,
            sendModel: false,
            (deployment, body, cancellationToken) => Http.PostJsonAsync(EmbeddingsPath(deployment), body, deployment, cancellationToken));
    }

    /// <inheritdoc/>
    public async Task<ProviderProbeResult> ProbeAsync(CancellationToken cancellationToken) =>
        (await ProbeDetailedAsync(cancellationToken).ConfigureAwait(false)).ToProbeResult();

    /// <summary>
    /// Asks <c>GET /openai/models</c> with the credential and reports whether the resource answered and accepted it.
    /// A 404 or 405 means the resource is reachable but the credential could not be verified. Never throws for an
    /// unreachable resource.
    /// </summary>
    /// <param name="cancellationToken">A token that cancels the probe.</param>
    /// <returns>The result.</returns>
    public async Task<CloudProbeResult> ProbeDetailedAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            using var response = await Http.ProbeAsync($"openai/models?api-version={Uri.EscapeDataString(ApiVersion)}", timeout.Token).ConfigureAwait(false);
            var result = CloudTransport.Interpret(response, DisplayName, Endpoint, "credential", CountModels);
            if (result.IsReachable && result.IsAuthenticated == true && _deployments.Count == 0)
            {
                return result with { Message = result.Message + " No deployments are listed in this provider's models, so nothing can be routed to it yet." };
            }

            return result;
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

    private static Dictionary<string, string> ReadFamilies(ProviderSettings settings)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (settings.ExtensionData is { } data &&
            data.TryGetValue("modelFamilies", out var element) &&
            element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.String && property.Value.GetString() is { Length: > 0 } family)
                {
                    map[property.Name] = family;
                }
            }
        }

        return map;
    }

    private ModelCapabilityDescriptor CapabilitiesFor(string deployment) =>
        Catalog.GetCapabilities(_families.TryGetValue(deployment, out var family) ? family : deployment, isLocal: false) with { Fim = false };
}

/// <summary>
/// Creates <see cref="AzureOpenAIProvider"/>s of kind <see cref="ProviderKind.AzureOpenAi"/>. The provider is routable
/// when a key is stored (secret name = provider id) or the host supplies an <see cref="ITokenCredentialProvider"/>;
/// otherwise the registry hides it for lack of a key.
/// </summary>
public sealed class AzureOpenAIProviderFactory : IModelProviderFactory
{
    private readonly ISecretStore _secrets;
    private readonly ITokenCredentialProvider? _credentials;
    private readonly Func<HttpMessageHandler>? _handlerFactory;

    /// <summary>Initializes a new instance of the <see cref="AzureOpenAIProviderFactory"/> class that authenticates with keys only.</summary>
    /// <param name="secrets">The secret store holding API keys.</param>
    public AzureOpenAIProviderFactory(ISecretStore secrets)
        : this(secrets, (ITokenCredentialProvider?)null, null)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="AzureOpenAIProviderFactory"/> class for dependency injection.</summary>
    /// <param name="secrets">The secret store holding API keys.</param>
    /// <param name="credentialProviders">The registered Entra ID credential providers (none until the host registers one); the last is used.</param>
    public AzureOpenAIProviderFactory(ISecretStore secrets, IEnumerable<ITokenCredentialProvider> credentialProviders)
        : this(secrets, credentialProviders?.LastOrDefault(), null)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="AzureOpenAIProviderFactory"/> class.</summary>
    /// <param name="secrets">The secret store holding API keys.</param>
    /// <param name="credentials">The Entra ID token source, or <see langword="null"/>.</param>
    /// <param name="handlerFactory">Creates the HTTP handler for each provider (tests pass a fake), or <see langword="null"/> for the real one with the proxy and CA bundle applied.</param>
    public AzureOpenAIProviderFactory(ISecretStore secrets, ITokenCredentialProvider? credentials, Func<HttpMessageHandler>? handlerFactory)
    {
        ArgumentNullException.ThrowIfNull(secrets);
        _secrets = secrets;
        _credentials = credentials;
        _handlerFactory = handlerFactory;
    }

    /// <inheritdoc/>
    public ProviderKind Kind => ProviderKind.AzureOpenAi;

    /// <inheritdoc/>
    public string? FeatureFlag => FeatureNames.AzureOpenAIProvider;

    /// <inheritdoc/>
    public bool RequiresApiKey(ProviderSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        try
        {
            return AzureOpenAIProvider.ReadAuthentication(settings) switch
            {
                AzureAuthentication.ApiKey => true,
                AzureAuthentication.Entra => false, // Create explains a missing credential provider.
                _ => _credentials is null,
            };
        }
        catch (InvalidOperationException)
        {
            // An unreadable setting is reported by Create, not as a missing key.
            return false;
        }
    }

    /// <inheritdoc/>
    public IModelProvider Create(ProviderSettings settings, ProviderCreationContext context)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(context);

        if (settings.Kind != Kind)
        {
            throw new ArgumentException($"The provider '{settings.Id}' is not of kind {ProviderKinds.ToName(Kind)}.", nameof(settings));
        }

        // The endpoint is the resource, https://<resource>.openai.azure.com; it has no default.
        var endpoint = CloudTransport.ResolveEndpoint(settings, defaultEndpoint: null);
        var client = CloudTransport.CreateClient(settings, _handlerFactory, out var owned);
        try
        {
            var providerId = settings.Id;
            return new AzureOpenAIProvider(
                settings,
                endpoint,
                client,
                () => CloudTransport.ReadKey(_secrets, providerId),
                _credentials,
                context.Catalog,
                owned,
                context.LoggerFactory.CreateLogger<AzureOpenAIProvider>());
        }
        catch
        {
            client.Dispose();
            owned?.Dispose();
            throw;
        }
    }
}
