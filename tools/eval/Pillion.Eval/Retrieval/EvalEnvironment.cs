using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Pillion.Common;
using Pillion.Common.Secrets;
using Pillion.Common.Settings;
using Pillion.ContextEngine.Retrieval;
using Pillion.Contracts;
using Pillion.Eval.Models;
using Pillion.Host.Connectivity;
using Pillion.Host.Context;
using Pillion.Host.Context.Retrieval;
using Pillion.Host.Indexing;
using Pillion.Indexing.Ownership;
using Pillion.Indexing.Retention;
using Pillion.ModelGateway;
using Pillion.ModelGateway.Providers.Bundled;

namespace Pillion.Eval.Retrieval;

/// <summary>
/// The host services one evaluation run needs, composed in process the way Pillion Host composes them (and the host tests
/// do): the Model Gateway with the bundled provider (and Ollama when the model is an Ollama one), Local-only on, the
/// Embedding task class pinned to the evaluated model through <c>routing.profiles</c>; the indexing pipeline
/// (<see cref="IndexingService"/> over an encrypted index in a temporary data folder, with the real
/// <see cref="GatewayIndexEmbedder"/>); and retrieval over the pipeline's open indexes (<see cref="LiveIndexCatalog"/>,
/// <see cref="GatewayQueryEmbedder"/>, <see cref="RetrievalService"/>). Only the IDE-facing signals are stand-ins: no
/// extension is connected, the IDE is never busy and the machine is never on battery saver.
/// </summary>
internal sealed class EvalEnvironment : IAsyncDisposable
{
    private readonly ServiceProvider _services;
    private readonly ILoggerFactory _loggers;
    private readonly EvalFeatureFlags _flags;
    private readonly IndexStatusNotifier _notifier;

    private EvalEnvironment(ServiceProvider services, PillionSettings settings, Float32CapturingGateway gateway, IndexingService indexing, EvalFeatureFlags flags, IndexStatusNotifier notifier)
    {
        _services = services;
        _loggers = services.GetRequiredService<ILoggerFactory>();
        Settings = settings;
        Gateway = gateway;
        Indexing = indexing;
        _flags = flags;
        _notifier = notifier;
        Catalog = new LiveIndexCatalog(indexing, flags);
        QueryEmbedder = new GatewayQueryEmbedder(gateway);
    }

    /// <summary>Gets the settings the run uses (Local-only on).</summary>
    public PillionSettings Settings { get; }

    /// <summary>Gets the gateway (the real one, wrapped to keep float32 document vectors).</summary>
    public Float32CapturingGateway Gateway { get; }

    /// <summary>Gets the indexing pipeline.</summary>
    public IndexingService Indexing { get; }

    /// <summary>Gets the catalog of the pipeline's open indexes.</summary>
    public LiveIndexCatalog Catalog { get; }

    /// <summary>Gets the query embedder retrieval uses.</summary>
    public IQueryEmbedder QueryEmbedder { get; }

    /// <summary>Gets the bundled models' runtime.</summary>
    public BundledModelRuntime BundledRuntime => _services.GetRequiredService<BundledModelRuntime>();

    /// <summary>Composes the services for one model.</summary>
    /// <param name="model">The model.</param>
    /// <param name="options">The run options.</param>
    /// <param name="dataRoot">A fresh data folder (the index lives under it).</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The started environment, with no workspace yet.</returns>
    public static async Task<EvalEnvironment> CreateAsync(ModelSpec model, RetrievalRunOptions options, string dataRoot, CancellationToken cancellationToken)
    {
        var settings = new PillionSettings();
        settings.Privacy.LocalOnly = true;
        if (model.Provider == ModelSpec.Ollama)
        {
            settings.Providers.Add(new ProviderSettings { Id = ModelSpec.Ollama, Kind = ProviderKind.Ollama, DisplayName = "Ollama", Endpoint = options.OllamaEndpoint });
        }

        // The Embedding task class routes through LocalFast; a model named there wins over the catalog's default order.
        settings.Routing.Profiles[Profile.LocalFast] = model.QualifiedId;
        settings.Indexing.EmbeddingTokenCap = options.EmbeddingTokenCap;
        var settingsSource = new StaticGatewaySettingsSource(settings);

        var services = new ServiceCollection();
        services.AddLogging(b => b.AddSimpleConsole(o => o.SingleLine = true).SetMinimumLevel(options.Verbose ? LogLevel.Information : LogLevel.Warning));
        services.AddSingleton<IGatewaySettingsSource>(settingsSource);
        services.AddSingleton<ISecretStore>(new InMemorySecretStore());
        services.AddSingleton(new BundledModelOptions
        {
            InstallModelsDirectory = Path.Combine(AppContext.BaseDirectory, "models"),
            UserModelsDirectory = options.ModelsDirectory,
            ExecutionProvider = options.DirectML ? BundledExecutionProvider.Auto : BundledExecutionProvider.Cpu,
            IdleUnloadAfter = TimeSpan.Zero,
        });
        services.AddPillionModelGateway();
        var provider = services.BuildServiceProvider();
        try
        {
            var gateway = new Float32CapturingGateway(provider.GetRequiredService<IModelGateway>()) { Capture = options.Int8Loss };

            // Fills the model inventory, so the router knows what the Ollama server has installed.
            await gateway.ListModelsAsync(cancellationToken).ConfigureAwait(false);

            var embedder = new GatewayIndexEmbedder(
                provider.GetRequiredService<IModelRouter>(),
                gateway,
                provider.GetRequiredService<ModelCatalog>(),
                provider.GetRequiredService<BundledModelRuntime>());
            var resolved = embedder.ResolveModel();
            if (!string.Equals(resolved.ModelId, model.QualifiedId, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"The Embedding task class routes to {resolved.ModelId}, not {model.QualifiedId}; is the model installed?");
            }

            var loggers = provider.GetRequiredService<ILoggerFactory>();
            var flags = new EvalFeatureFlags(settings.Features);
            var paths = new PillionPaths(dataRoot);
            var notifier = new IndexStatusNotifier(new NoClientSender(), TimeProvider.System, loggers.CreateLogger<IndexStatusNotifier>());
            var indexing = new IndexingService(
                new IndexStoreFactory(paths, flags, loggers),
                new IndexRetention(paths, flags),
                embedder,
                settingsSource,
                flags,
                notifier,
                new IdeActivityMonitor(),
                new NeverOnBatterySaver(),
                new ProcessMemorySampler(),
                new IndexingOptions(),
                "pillion-eval-" + Guid.NewGuid().ToString("N"),
                TimeProvider.System,
                loggers);
            await indexing.StartAsync(cancellationToken).ConfigureAwait(false);
            return new EvalEnvironment(provider, settings, gateway, indexing, flags, notifier);
        }
        catch
        {
            await provider.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Creates a retrieval service with its own query vector cache (one per mode, so a mode never reuses another's query embeddings).</summary>
    /// <param name="budget">The search time budget.</param>
    /// <returns>The service.</returns>
    public IRetrievalService CreateRetrievalService(TimeSpan budget) =>
        new RetrievalService(Catalog, _flags, QueryEmbedder, RetrievalOptions.Default with { TimeBudget = budget }, logger: _loggers.CreateLogger<RetrievalService>());

    /// <summary>Indexes a folder and waits until every file is indexed and the sweep is done.</summary>
    /// <param name="folder">The corpus root.</param>
    /// <param name="timeout">The longest to wait.</param>
    /// <param name="log">Receives progress lines.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>The final status.</returns>
    public async Task<IndexStatus> IndexAsync(string folder, TimeSpan timeout, TextWriter log, CancellationToken cancellationToken)
    {
        Indexing.SetWorkspace(new WorkspaceDescription { FolderPath = folder });
        var clock = Stopwatch.StartNew();
        var lastReport = TimeSpan.Zero;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var status = Indexing.GetStatus();
            if (status.Phase == IndexPhase.Idle && status.Roots.Length > 0
                && status.Roots.All(r => r.Role != Contracts.IndexRole.Writer || r.LastFullSweepUtc is not null)
                && Indexing.OpenRoots.Count == status.Roots.Length)
            {
                return status;
            }

            if (clock.Elapsed > timeout)
            {
                throw new TimeoutException($"Indexing did not finish within {timeout}: phase {status.Phase}, pause {status.PauseReason}, error {status.LastError}, files {status.IndexedFileCount}/{status.TotalFileCount}.");
            }

            if (clock.Elapsed - lastReport >= TimeSpan.FromSeconds(15))
            {
                lastReport = clock.Elapsed;
                await log.WriteLineAsync(string.Create(CultureInfo.InvariantCulture, $"    indexing: {clock.Elapsed.TotalSeconds:0} s, phase {status.Phase}, files {status.IndexedFileCount}/{status.TotalFileCount}{(status.LastError is null ? string.Empty : ", last error: " + status.LastError)}")).ConfigureAwait(false);
            }

            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        await Catalog.DisposeAsync().ConfigureAwait(false);
        await Indexing.DisposeAsync().ConfigureAwait(false);
        _notifier.Dispose();
        await _services.DisposeAsync().ConfigureAwait(false);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    /// <summary>The settings' feature flags (all defaults, so <c>reranker</c> stays off).</summary>
    private sealed class EvalFeatureFlags(FeatureSettings features) : IFeatureFlags
    {
        public event EventHandler? Changed
        {
            add { }
            remove { }
        }

        public bool IsEnabled(string feature) => features.IsEnabled(feature);
    }

    private sealed class NoClientSender : INotificationSender
    {
        public event EventHandler? ClientConnected
        {
            add { }
            remove { }
        }

        public Task<bool> TrySendAsync(string method, object? argument) => Task.FromResult(false);
    }

    private sealed class NeverOnBatterySaver : IPowerStatus
    {
        public bool IsBatterySaverOn() => false;
    }
}
