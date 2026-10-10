using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Pillion.Common.Settings;
using Pillion.Contracts;

namespace Pillion.ModelGateway.Providers.Ollama;

/// <summary>Per-provider Ollama options read from the provider's settings.</summary>
public sealed class OllamaOptions
{
    /// <summary>The default endpoint.</summary>
    public static readonly Uri DefaultEndpoint = new("http://localhost:11434/");

    /// <summary>Gets or sets the server endpoint.</summary>
    public Uri Endpoint { get; set; } = DefaultEndpoint;

    /// <summary>
    /// Gets or sets the context size (<c>num_ctx</c>) per profile. Every local request sends a fixed size, because changing
    /// it reloads the model and the server default silently truncates prompts (section 6, Local model residency).
    /// Defaults: Local-fast 4,096, Local-strong 8,192, every other profile 8,192.
    /// </summary>
    public Dictionary<Profile, int> ContextSizes { get; set; } = new()
    {
        [Profile.LocalFast] = 4096,
        [Profile.LocalStrong] = 8192,
    };

    /// <summary>Gets or sets the context size for profiles not in <see cref="ContextSizes"/>. Default 8,192.</summary>
    public int DefaultContextSize { get; set; } = 8192;

    /// <summary>Gets or sets the residency of chat models (<c>keep_alive</c>). Default 30 minutes.</summary>
    public string ChatKeepAlive { get; set; } = "30m";

    /// <summary>Gets or sets the residency of the autocomplete model (<c>keep_alive</c>). Default -1, kept loaded.</summary>
    public string AutocompleteKeepAlive { get; set; } = "-1";

    /// <summary>
    /// Gets or sets how long a load of a model may take before the request fails with a message instead of waiting on. Default
    /// 120 seconds; set <c>"loadTimeoutSeconds"</c> in the provider entry to change it. A cold load of a 7B model from an SSD takes
    /// about ten seconds, from a hard disk about a minute.
    /// </summary>
    public TimeSpan LoadTimeout { get; set; } = TimeSpan.FromSeconds(120);

    /// <summary>Gets or sets how long Ollama may take to unload a model before the request fails. Default 30 seconds (<c>"releaseTimeoutSeconds"</c>).</summary>
    public TimeSpan ReleaseTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Gets or sets how often a long load reports that it is still going. Default 5 seconds.</summary>
    public TimeSpan ProgressInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Gets the context size for a profile.</summary>
    /// <param name="profile">The profile, or <see langword="null"/>.</param>
    /// <returns>The <c>num_ctx</c> to send.</returns>
    public int ContextSizeFor(Profile? profile) =>
        profile is { } p && ContextSizes.TryGetValue(p, out var size) ? size : DefaultContextSize;

    /// <summary>
    /// Reads the options from a provider's settings: the endpoint, and an optional <c>numCtx</c> object in the provider
    /// entry such as <c>"numCtx": { "localFast": 4096, "localStrong": 16384 }</c> that overrides the per-profile sizes.
    /// </summary>
    /// <param name="settings">The provider settings.</param>
    /// <returns>The options.</returns>
    /// <exception cref="UriFormatException">The endpoint is not an absolute http or https URL.</exception>
    public static OllamaOptions FromSettings(ProviderSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var options = new OllamaOptions();
        if (!string.IsNullOrWhiteSpace(settings.Endpoint))
        {
            var text = settings.Endpoint.Trim();
            if (!text.EndsWith('/'))
            {
                text += "/";
            }

            if (!Uri.TryCreate(text, UriKind.Absolute, out var endpoint) || (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps))
            {
                throw new UriFormatException($"The Ollama endpoint '{settings.Endpoint}' is not an http or https URL.");
            }

            options.Endpoint = endpoint;
        }

        if (settings.ExtensionData?.TryGetValue("numCtx", out var numCtx) == true && numCtx.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in numCtx.EnumerateObject())
            {
                if (Enum.TryParse<Profile>(property.Name.Replace("-", string.Empty, StringComparison.Ordinal), ignoreCase: true, out var profile) &&
                    property.Value.TryGetInt32(out var size) && size > 0)
                {
                    options.ContextSizes[profile] = size;
                }
            }
        }

        options.LoadTimeout = ReadSeconds(settings, "loadTimeoutSeconds", options.LoadTimeout);
        options.ReleaseTimeout = ReadSeconds(settings, "releaseTimeoutSeconds", options.ReleaseTimeout);
        options.ProgressInterval = ReadSeconds(settings, "progressIntervalSeconds", options.ProgressInterval);
        return options;
    }

    private static TimeSpan ReadSeconds(ProviderSettings settings, string name, TimeSpan fallback) =>
        settings.ExtensionData?.TryGetValue(name, out var value) == true && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var seconds) && seconds > 0
            ? TimeSpan.FromSeconds(seconds)
            : fallback;
}

/// <summary>
/// The Ollama adapter (design sections 6 and 11): the native HTTP API with <see cref="HttpClient"/>. Chat streams from
/// <c>/api/chat</c>; fill-in-the-middle uses <c>/api/generate</c> with <c>suffix</c> (Ollama applies the model's own FIM
/// template); models and capabilities come from <c>/api/tags</c> and <c>/api/show</c>.
/// </summary>
public sealed partial class OllamaProvider : IModelProvider
{
    /// <summary>
    /// The minimum supported Ollama version. 0.6.4 is the first release whose <c>/api/show</c> returns the
    /// <c>capabilities</c> field (completion, insert, vision, tools, embedding), which Pillion reads to tell FIM-capable
    /// models from chat-only ones; <c>suffix</c> on <c>/api/generate</c> is older (0.3 series), so 0.6.4 covers both.
    /// </summary>
    public static readonly Version MinimumVersion = new(0, 6, 4);

    private readonly HttpClient _httpClient;
    private readonly OllamaHttp _http;
    private readonly ModelCatalog _catalog;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, (string Digest, ModelCapabilityDescriptor Capabilities)> _shown = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _autocompleteModels = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _versionGate = new(1, 1);
    private Version? _verifiedVersion;

    /// <summary>Initializes a new instance of the <see cref="OllamaProvider"/> class.</summary>
    /// <param name="settings">The provider settings.</param>
    /// <param name="httpClient">The HTTP client; the provider owns and disposes it.</param>
    /// <param name="catalog">The model catalog, for capabilities Ollama does not report.</param>
    /// <param name="autocompleteModelId">The model routed to autocomplete, kept resident even when used for chat.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="runtime">Gateway services for residency (progress sink, GPU tier, overrides); <see cref="ProviderRuntime.Default"/> when <see langword="null"/>.</param>
    public OllamaProvider(ProviderSettings settings, HttpClient httpClient, ModelCatalog catalog, string? autocompleteModelId = null, ILogger<OllamaProvider>? logger = null, ProviderRuntime? runtime = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(catalog);

        Options = OllamaOptions.FromSettings(settings);
        Id = settings.Id;
        DisplayName = string.IsNullOrWhiteSpace(settings.DisplayName) ? "Ollama" : settings.DisplayName;
        IsLocal = Options.Endpoint.IsLoopback;
        _httpClient = httpClient;
        _http = new OllamaHttp(httpClient, Options.Endpoint);
        _catalog = catalog;
        _logger = logger ?? NullLogger<OllamaProvider>.Instance;
        if (!string.IsNullOrWhiteSpace(autocompleteModelId))
        {
            _autocompleteModels[autocompleteModelId] = 0;
        }

        Residency = new OllamaResidency(this, runtime ?? ProviderRuntime.Default, _logger);
    }

    /// <inheritdoc/>
    public string Id { get; }

    /// <inheritdoc/>
    public ProviderKind Kind => ProviderKind.Ollama;

    /// <inheritdoc/>
    public string DisplayName { get; }

    /// <summary>Gets a value indicating whether the server is on this machine (a loopback endpoint). A remote Ollama server is treated as a cloud boundary.</summary>
    public bool IsLocal { get; }

    /// <summary>Gets the options.</summary>
    public OllamaOptions Options { get; }

    /// <summary>Gets the residency manager: which models are loaded, with what context, and the rules that keep one resident configuration per model.</summary>
    internal OllamaResidency Residency { get; }

    /// <summary>Gets the model catalog.</summary>
    internal ModelCatalog Catalog => _catalog;

    /// <summary>Parses an Ollama version string such as <c>0.40.1</c> or <c>0.6.4-rc1</c>.</summary>
    /// <param name="text">The version text.</param>
    /// <returns>The version, or <see langword="null"/> when it cannot be parsed.</returns>
    public static Version? ParseVersion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var core = text.Trim().TrimStart('v');
        var end = core.IndexOfAny(['-', '+', ' ']);
        if (end >= 0)
        {
            core = core[..end];
        }

        return Version.TryParse(core, out var version) ? version : null;
    }

    /// <summary>Checks the server version once (and again after a failure) before the first real request.</summary>
    /// <param name="cancellationToken">A token that cancels the check.</param>
    /// <returns>The server version.</returns>
    /// <exception cref="ProviderException">The server is unreachable or older than <see cref="MinimumVersion"/>.</exception>
    public async Task<Version> EnsureVersionAsync(CancellationToken cancellationToken)
    {
        if (_verifiedVersion is { } verified)
        {
            return verified;
        }

        await _versionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_verifiedVersion is { } again)
            {
                return again;
            }

            using var document = await _http.GetJsonAsync("api/version", modelId: null, cancellationToken).ConfigureAwait(false);
            var text = document.RootElement.TryGetProperty("version", out var v) ? v.GetString() : null;
            var version = ParseVersion(text)
                ?? throw new ProviderException(PillionErrorCode.ProviderUnavailable, $"Ollama at {Options.Endpoint} reported an unreadable version '{text}'.", isProviderFault: false);
            if (version < MinimumVersion)
            {
                LogTooOld(_logger, Id, version, MinimumVersion);
                throw new ProviderException(
                    PillionErrorCode.ProviderUnavailable,
                    $"Ollama {version} is older than the minimum supported version {MinimumVersion}. Update Ollama and try again.",
                    isProviderFault: false);
            }

            _verifiedVersion = version;
            return version;
        }
        finally
        {
            _versionGate.Release();
        }
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<ProviderModel>> ListModelsAsync(CancellationToken cancellationToken)
    {
        await EnsureVersionAsync(cancellationToken).ConfigureAwait(false);
        using var tags = await _http.GetJsonAsync("api/tags", modelId: null, cancellationToken).ConfigureAwait(false);
        var entries = new List<(string Name, string Digest)>();

        // /api/tags can list one name twice (an older and a newer digest under the same tag). The first entry is the one
        // the tag currently points at, so it wins and the other is dropped; ":latest" and no tag are the same model.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (tags.RootElement.TryGetProperty("models", out var models) && models.ValueKind == JsonValueKind.Array)
        {
            foreach (var model in models.EnumerateArray())
            {
                var name = model.TryGetProperty("name", out var n) ? n.GetString() : model.TryGetProperty("model", out var m) ? m.GetString() : null;
                if (!string.IsNullOrWhiteSpace(name) && seen.Add(ModelNameKey(name)))
                {
                    entries.Add((name, model.TryGetProperty("digest", out var d) ? d.GetString() ?? string.Empty : string.Empty));
                }
            }
        }

        var result = new List<ProviderModel>(entries.Count);
        foreach (var (name, digest) in entries)
        {
            var capabilities = await GetCapabilitiesAsync(name, digest, cancellationToken).ConfigureAwait(false);
            result.Add(new ProviderModel(Id, name, name, capabilities, string.IsNullOrEmpty(digest) ? null : "sha256:" + digest));
        }

        return result;
    }

    private static string ModelNameKey(string name)
    {
        var trimmed = name.Trim();
        return trimmed.EndsWith(":latest", StringComparison.OrdinalIgnoreCase) ? trimmed[..^":latest".Length] : trimmed;
    }

    /// <inheritdoc/>
    public IChatClient GetChatClient(string modelId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        return new OllamaChatClient(this, modelId);
    }

    /// <inheritdoc/>
    public IFimClient? GetFimClient(string modelId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        if (_shown.TryGetValue(modelId, out var known) && !known.Capabilities.Fim)
        {
            return null;
        }

        return new OllamaFimClient(this, modelId);
    }

    /// <inheritdoc/>
    public async Task<ProviderProbeResult> ProbeAsync(CancellationToken cancellationToken)
    {
        _verifiedVersion = null;
        try
        {
            var version = await EnsureVersionAsync(cancellationToken).ConfigureAwait(false);
            return new ProviderProbeResult(true, version.ToString(), $"Connected to Ollama {version} at {Options.Endpoint}.");
        }
        catch (ProviderException ex)
        {
            return new ProviderProbeResult(false, null, ex.Message);
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        Residency.Dispose();
        _httpClient.Dispose();
        _versionGate.Dispose();
    }

    /// <summary>Gets the <c>keep_alive</c> for a request: the autocomplete model stays resident, chat models for 30 minutes.</summary>
    internal string KeepAliveFor(string modelId, bool isFim)
    {
        if (isFim)
        {
            _autocompleteModels[modelId] = 0;
            return Options.AutocompleteKeepAlive;
        }

        return _autocompleteModels.ContainsKey(modelId) ? Options.AutocompleteKeepAlive : Options.ChatKeepAlive;
    }

    /// <summary>Determines whether a model is the one routed to autocomplete (or has served a completion), which stays resident.</summary>
    internal bool IsCompletionModel(string modelId) =>
        _autocompleteModels.Keys.Any(id => ModelInventory.ModelIdsEqual(id, modelId));

    /// <summary>Gets the context window of a model: what <c>/api/show</c> reported when the model was listed, else the catalog.</summary>
    internal int ContextWindowOf(string modelId) =>
        _shown.TryGetValue(modelId, out var known) ? known.Capabilities.ContextWindow : _catalog.GetCapabilities(modelId, IsLocal).ContextWindow;

    internal OllamaHttp Http => _http;

    /// <summary>Adds <c>keep_alive</c> to a request body (a number for -1, otherwise a duration string).</summary>
    internal static void SetKeepAlive(JsonObject body, string keepAlive) =>
        body["keep_alive"] = int.TryParse(keepAlive, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds) ? JsonValue.Create(seconds) : JsonValue.Create(keepAlive);

    private async Task<ModelCapabilityDescriptor> GetCapabilitiesAsync(string name, string digest, CancellationToken cancellationToken)
    {
        if (_shown.TryGetValue(name, out var cached) && cached.Digest == digest)
        {
            return cached.Capabilities;
        }

        var fromCatalog = _catalog.GetCapabilities(name, IsLocal);
        ModelCapabilityDescriptor capabilities;
        try
        {
            using var show = await _http.PostJsonAsync("api/show", new JsonObject { ["model"] = name }, name, cancellationToken).ConfigureAwait(false);
            capabilities = Merge(fromCatalog, show.RootElement);
        }
        catch (ProviderException ex) when (ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest)
        {
            capabilities = fromCatalog;
        }

        _shown[name] = (digest, capabilities);
        return capabilities;
    }

    /// <summary>Overrides the catalog's flags with what <c>/api/show</c> reports.</summary>
    internal static ModelCapabilityDescriptor Merge(ModelCapabilityDescriptor catalog, JsonElement show)
    {
        var result = catalog;
        if (show.TryGetProperty("capabilities", out var caps) && caps.ValueKind == JsonValueKind.Array)
        {
            var set = caps.EnumerateArray().Select(c => c.GetString() ?? string.Empty).ToHashSet(StringComparer.OrdinalIgnoreCase);
            result = result with
            {
                Chat = set.Contains("completion"),
                Fim = set.Contains("insert"),
                Vision = set.Contains("vision"),
                Embeddings = set.Contains("embedding"),
                ToolCalling = set.Contains("tools") ? ToolCallingSupport.Native : ToolCallingSupport.None,
            };
        }

        if (show.TryGetProperty("model_info", out var info) && info.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in info.EnumerateObject())
            {
                if (property.Name.EndsWith(".context_length", StringComparison.Ordinal) && property.Value.TryGetInt32(out var length) && length > 0)
                {
                    result = result with { ContextWindow = length };
                    break;
                }
            }
        }

        return result;
    }

    [LoggerMessage(EventId = 440, Level = LogLevel.Warning, Message = "Ollama provider '{ProviderId}' runs version {Version}, older than the minimum {Minimum}.")]
    private static partial void LogTooOld(ILogger logger, string providerId, Version version, Version minimum);
}

/// <summary>Creates <see cref="OllamaProvider"/>s. Ollama needs no API key.</summary>
public sealed class OllamaProviderFactory : IModelProviderFactory
{
    private readonly Func<HttpMessageHandler> _handlerFactory;

    /// <summary>Initializes a new instance of the <see cref="OllamaProviderFactory"/> class with the default HTTP handler.</summary>
    public OllamaProviderFactory()
        : this(CreateDefaultHandler)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="OllamaProviderFactory"/> class.</summary>
    /// <param name="handlerFactory">Creates the HTTP handler for each provider (tests pass a fake).</param>
    public OllamaProviderFactory(Func<HttpMessageHandler> handlerFactory)
    {
        ArgumentNullException.ThrowIfNull(handlerFactory);
        _handlerFactory = handlerFactory;
    }

    /// <inheritdoc/>
    public ProviderKind Kind => ProviderKind.Ollama;

    /// <inheritdoc/>
    public string? FeatureFlag => FeatureNames.OllamaProvider;

    /// <inheritdoc/>
    public bool RequiresApiKey(ProviderSettings settings) => false;

    /// <inheritdoc/>
    public IModelProvider Create(ProviderSettings settings, ProviderCreationContext context)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(context);

        // Streaming responses can take minutes on a slow local model; the gateway applies its own time limits.
        var client = new HttpClient(_handlerFactory(), disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
        try
        {
            return new OllamaProvider(settings, client, context.Catalog, context.AutocompleteModelId, context.LoggerFactory.CreateLogger<OllamaProvider>(), context.Runtime);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private static SocketsHttpHandler CreateDefaultHandler() => new()
    {
        ConnectTimeout = TimeSpan.FromSeconds(5),
        PooledConnectionIdleTimeout = TimeSpan.FromMinutes(1),
    };
}
