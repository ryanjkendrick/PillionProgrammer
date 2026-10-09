using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Pillion.Common.Connectivity;
using Pillion.Common.Hardware;
using Pillion.Common.Secrets;
using Pillion.Common.Settings;
using Pillion.Contracts;

namespace Pillion.ModelGateway;

/// <summary>A configured provider that is not routed to, and why.</summary>
/// <param name="Settings">The provider's settings.</param>
/// <param name="Reason">A user-facing reason (disabled, unknown kind, no API key).</param>
public sealed record HiddenProvider(ProviderSettings Settings, string Reason);

/// <summary>The providers built from one version of the settings.</summary>
public sealed class ProviderRegistrySnapshot
{
    internal ProviderRegistrySnapshot(EffectiveSettings settings, IReadOnlyList<IModelProvider> providers, IReadOnlyList<HiddenProvider> hidden)
    {
        Settings = settings;
        Providers = providers;
        Hidden = hidden;
    }

    /// <summary>Gets the effective settings the snapshot was built from.</summary>
    public EffectiveSettings Settings { get; }

    /// <summary>Gets the providers that may be routed to, in settings order.</summary>
    public IReadOnlyList<IModelProvider> Providers { get; }

    /// <summary>Gets the configured providers that are hidden from routing.</summary>
    public IReadOnlyList<HiddenProvider> Hidden { get; }

    /// <summary>Finds a routable provider by id.</summary>
    /// <param name="providerId">The provider id.</param>
    /// <returns>The provider, or <see langword="null"/>.</returns>
    public IModelProvider? Find(string providerId) =>
        Providers.FirstOrDefault(p => string.Equals(p.Id, providerId, StringComparison.OrdinalIgnoreCase));

    /// <summary>Finds a provider's settings by id, routable or not.</summary>
    /// <param name="providerId">The provider id.</param>
    /// <returns>The settings, or <see langword="null"/>.</returns>
    public ProviderSettings? FindSettings(string providerId) =>
        Settings.Settings.Providers.FirstOrDefault(p => string.Equals(p.Id, providerId, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Holds the providers built from the <c>providers</c> setting.</summary>
public interface IProviderRegistry
{
    /// <summary>Gets the current providers, rebuilding them if the settings changed.</summary>
    /// <returns>The snapshot.</returns>
    ProviderRegistrySnapshot GetSnapshot();
}

/// <summary>
/// Builds providers from settings with the <see cref="IModelProviderFactory"/> registered for each kind. A provider
/// whose kind has no factory is skipped with a warning; one that needs an API key and has none in the
/// <see cref="ISecretStore"/> (the secret name is the provider id) is hidden from routing. So is a provider that a
/// feature flag switches off: every cloud provider with <see cref="FeatureNames.CloudProviders"/> off, and the providers
/// of a factory whose <see cref="IModelProviderFactory.FeatureFlag"/> is off. Providers whose settings did
/// not change are reused across rebuilds.
/// </summary>
public sealed partial class ProviderRegistry : IProviderRegistry, IDisposable
{
    private readonly object _gate = new();
    private readonly IGatewaySettingsSource _settings;
    private readonly ISecretStore _secrets;
    private readonly ModelCatalog _catalog;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _logger;
    private readonly ProviderRuntime _runtime;
    private readonly Dictionary<ProviderKind, IModelProviderFactory> _factories = [];
    private readonly ConcurrentDictionary<string, (string Fingerprint, IModelProvider Provider)> _built = new(StringComparer.OrdinalIgnoreCase);
    private ProviderRegistrySnapshot? _snapshot;
    private bool _disposed;

    /// <summary>Initializes a new instance of the <see cref="ProviderRegistry"/> class.</summary>
    /// <param name="settings">The settings source.</param>
    /// <param name="secrets">The secret store holding API keys.</param>
    /// <param name="factories">The provider factories; the last one registered for a kind wins.</param>
    /// <param name="catalog">The model catalog.</param>
    /// <param name="loggerFactory">Creates loggers.</param>
    /// <param name="progress">Receives the load and unload progress of local providers; dropped when <see langword="null"/>.</param>
    /// <param name="gpuTierDetector">Detects the GPU tier that local residency rules use under <c>models.gpuTier</c> auto; <see cref="GpuTierDetector.Shared"/> when <see langword="null"/>.</param>
    /// <param name="time">The clock of the residency rules.</param>
    /// <param name="residency">The map local providers register with, for the status bar and diagnostics.</param>
    public ProviderRegistry(
        IGatewaySettingsSource settings,
        ISecretStore secrets,
        IEnumerable<IModelProviderFactory> factories,
        ModelCatalog catalog,
        ILoggerFactory? loggerFactory = null,
        IProgressSink? progress = null,
        IGpuTierDetector? gpuTierDetector = null,
        TimeProvider? time = null,
        LocalResidencyMap? residency = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(factories);
        ArgumentNullException.ThrowIfNull(catalog);

        _settings = settings;
        _secrets = secrets;
        _catalog = catalog;
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        _logger = _loggerFactory.CreateLogger<ProviderRegistry>();
        foreach (var factory in factories)
        {
            _factories[factory.Kind] = factory;
        }

        var detector = gpuTierDetector ?? GpuTierDetector.Shared;
        _runtime = new ProviderRuntime(
            progress ?? new NoProgressSink(),
            () => GpuTiers.Resolve(_settings.GetCurrent().Settings.Models.GpuTier, detector).Tier,
            model => FindResidentContext(_settings.GetCurrent().Settings.Models.ResidentContext, model),
            time ?? TimeProvider.System,
            residency);
        _settings.Changed += OnSettingsChanged;
    }

    /// <inheritdoc/>
    public ProviderRegistrySnapshot GetSnapshot()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _snapshot ??= Build();
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _settings.Changed -= OnSettingsChanged;
            foreach (var (_, provider) in _built.Values)
            {
                provider.Dispose();
            }

            _built.Clear();
        }
    }

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        lock (_gate)
        {
            _snapshot = null;
        }
    }

    private ProviderRegistrySnapshot Build()
    {
        var effective = _settings.GetCurrent();
        var providers = new List<IModelProvider>();
        var hidden = new List<HiddenProvider>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var settings in effective.Settings.Providers)
        {
            if (string.IsNullOrWhiteSpace(settings.Id) || !seen.Add(settings.Id))
            {
                LogInvalidId(_logger, settings.Id);
                hidden.Add(new HiddenProvider(settings, "The provider id is empty or used more than once."));
                continue;
            }

            if (!settings.Enabled)
            {
                hidden.Add(new HiddenProvider(settings, "The provider is disabled."));
                continue;
            }

            if (!effective.IsFeatureEnabled(FeatureNames.CloudProviders) && !ProviderClassifier.IsLocal(settings))
            {
                hidden.Add(new HiddenProvider(settings, "Cloud providers are turned off by settings or policy (feature cloudProviders)."));
                continue;
            }

            if (!_factories.TryGetValue(settings.Kind, out var factory))
            {
                LogUnknownKind(_logger, settings.Id, ProviderKinds.ToName(settings.Kind));
                hidden.Add(new HiddenProvider(settings, $"Provider kind '{ProviderKinds.ToName(settings.Kind)}' is not supported by this version of Pillion."));
                continue;
            }

            if (factory.FeatureFlag is { } flag && !effective.IsFeatureEnabled(flag))
            {
                hidden.Add(new HiddenProvider(settings, $"{ProviderKinds.ToName(settings.Kind)} providers are turned off by settings or policy (feature {flag})."));
                continue;
            }

            var apiKey = ReadKey(settings.Id);
            if (factory.RequiresApiKey(settings) && string.IsNullOrEmpty(apiKey))
            {
                LogNoKey(_logger, settings.Id);
                hidden.Add(new HiddenProvider(settings, "No API key is stored for this provider."));
                continue;
            }

            var fingerprint = Fingerprint(settings, apiKey);
            if (_built.TryGetValue(settings.Id, out var existing) && existing.Fingerprint == fingerprint)
            {
                providers.Add(existing.Provider);
                continue;
            }

            try
            {
                var context = new ProviderCreationContext(apiKey, _catalog, _loggerFactory, FindAutocompleteModel(effective.Settings, settings), _runtime);
                var provider = factory.Create(settings, context);
                _built[settings.Id] = (fingerprint, provider);
                providers.Add(provider);
            }
            catch (Exception ex) when (ex is ArgumentException or FormatException or UriFormatException or InvalidOperationException)
            {
                LogCreateFailed(_logger, settings.Id, ex.Message);
                hidden.Add(new HiddenProvider(settings, $"The provider could not be created: {ex.Message}"));
            }
        }

        // Providers removed from settings are not disposed here: an in-flight request may still hold one. They hold
        // only an HttpClient, whose idle connections time out on their own.
        foreach (var id in _built.Keys.Where(id => !providers.Any(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase))).ToList())
        {
            _built.TryRemove(id, out _);
            _runtime.Residency?.Unregister(id);
        }

        return new ProviderRegistrySnapshot(effective, providers, hidden);
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

    /// <summary>Finds the <c>models.residentContext</c> entry of a model; a trailing <c>:latest</c> and letter case do not matter.</summary>
    internal static int? FindResidentContext(IReadOnlyDictionary<string, int>? overrides, string modelId)
    {
        if (overrides is null || overrides.Count == 0)
        {
            return null;
        }

        foreach (var (id, tokens) in overrides)
        {
            if (tokens > 0 && ModelInventory.ModelIdsEqual(id.Trim(), modelId))
            {
                return tokens;
            }
        }

        return null;
    }

    private string? FindAutocompleteModel(PillionSettings settings, ProviderSettings provider)
    {
        if (settings.Routing.Profiles.TryGetValue(Profile.LocalFast, out var configured) && !string.IsNullOrWhiteSpace(configured))
        {
            var (providerId, modelId) = QualifiedModelId.Parse(configured, settings.Providers.Select(p => p.Id));
            if (providerId is null || string.Equals(providerId, provider.Id, StringComparison.OrdinalIgnoreCase))
            {
                return modelId;
            }
        }

        return _catalog.GetDefaultModel(provider.Kind, Profile.LocalFast);
    }

    private static string Fingerprint(ProviderSettings settings, string? apiKey)
    {
        var json = JsonSerializer.Serialize(settings);
        var keyHash = apiKey is null ? string.Empty : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(apiKey)));
        return json + "|" + keyHash;
    }

    [LoggerMessage(EventId = 400, Level = LogLevel.Warning, Message = "Model provider '{ProviderId}' has kind '{Kind}', which this version does not support; it is skipped.")]
    private static partial void LogUnknownKind(ILogger logger, string providerId, string kind);

    [LoggerMessage(EventId = 401, Level = LogLevel.Information, Message = "Model provider '{ProviderId}' has no API key in the secret store; it is hidden from routing.")]
    private static partial void LogNoKey(ILogger logger, string providerId);

    [LoggerMessage(EventId = 402, Level = LogLevel.Warning, Message = "Model provider '{ProviderId}' could not be created: {Reason}")]
    private static partial void LogCreateFailed(ILogger logger, string providerId, string reason);

    [LoggerMessage(EventId = 403, Level = LogLevel.Warning, Message = "A model provider has an empty or duplicate id ('{ProviderId}'); it is skipped.")]
    private static partial void LogInvalidId(ILogger logger, string providerId);
}

/// <summary>
/// The models each provider last reported, used by the router to tell installed from missing models and to read
/// provider-reported capabilities. Filled by <see cref="IModelGateway.ListModelsAsync"/>.
/// </summary>
public sealed class ModelInventory
{
    private readonly ConcurrentDictionary<string, IReadOnlyList<ProviderModel>> _models = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Records the models a provider reported.</summary>
    /// <param name="providerId">The provider id.</param>
    /// <param name="models">The models.</param>
    public void Update(string providerId, IReadOnlyList<ProviderModel> models)
    {
        ArgumentNullException.ThrowIfNull(providerId);
        ArgumentNullException.ThrowIfNull(models);
        _models[providerId] = models;
    }

    /// <summary>Gets the models a provider last reported.</summary>
    /// <param name="providerId">The provider id.</param>
    /// <param name="models">The models, when the provider has been listed.</param>
    /// <returns><see langword="true"/> when the provider has been listed.</returns>
    public bool TryGet(string providerId, out IReadOnlyList<ProviderModel> models)
    {
        if (_models.TryGetValue(providerId, out var found))
        {
            models = found;
            return true;
        }

        models = [];
        return false;
    }

    /// <summary>Finds a model a provider reported.</summary>
    /// <param name="providerId">The provider id.</param>
    /// <param name="modelId">The model id.</param>
    /// <returns>The model, or <see langword="null"/> when unknown.</returns>
    public ProviderModel? Find(string providerId, string modelId) =>
        TryGet(providerId, out var models) ? models.FirstOrDefault(m => ModelIdsEqual(m.ModelId, modelId)) : null;

    /// <summary>Compares model ids, treating Ollama's implicit <c>:latest</c> tag as equal to no tag.</summary>
    /// <param name="a">One id.</param>
    /// <param name="b">The other id.</param>
    /// <returns><see langword="true"/> when they name the same model.</returns>
    public static bool ModelIdsEqual(string a, string b) =>
        string.Equals(Normalise(a), Normalise(b), StringComparison.OrdinalIgnoreCase);

    private static string Normalise(string id) =>
        id.EndsWith(":latest", StringComparison.OrdinalIgnoreCase) ? id[..^":latest".Length] : id;
}
