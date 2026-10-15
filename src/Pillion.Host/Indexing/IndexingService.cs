using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Pillion.Common.Settings;
using Pillion.ContextEngine;
using Pillion.Contracts;
using Pillion.Host.Context;
using Pillion.Host.Rpc;
using Pillion.Indexing.Discovery;
using Pillion.Indexing.Ownership;
using Pillion.Indexing.Retention;
using Pillion.Indexing.Roots;
using Pillion.Indexing.Sweep;
using Pillion.LocalStore;
using Pillion.ModelGateway;

namespace Pillion.Host.Indexing;

/// <summary>
/// The indexing pipeline's owner in the host (work item 2.9, design sections 7, 10 and 13), behind <c>features.indexing</c>.
/// When the extension reports the open solution or folder (<see cref="SetWorkspace"/>), it resolves the repository roots,
/// opens each root's index through <see cref="IndexStoreFactory"/> (as its writer, or read-only when another host owns it)
/// and runs an <see cref="IndexPass"/> for every root it owns, one root at a time, on below-normal threads. It pauses for the
/// user, for builds, test runs and debugging (the <c>ideActivityChanged</c> notification), for battery saver (when
/// <c>indexing.pauseOnBattery</c> is set) and for the memory cap, and publishes its status through
/// <see cref="IndexStatusNotifier"/>.
/// </summary>
/// <remarks>
/// A host that only reads an index does not index it and forwards nothing: it checks every
/// <see cref="IndexingOptions.ReaderPollInterval"/> whether the owner is still alive and, when it is not, opens the index
/// again (taking <c>writer.lock</c>) and runs a full sweep, which picks up whatever changed meanwhile. Forwarding requests to
/// the owner over its pipe needs a host-to-host handshake that does not exist yet; see the host README.
/// </remarks>
internal sealed partial class IndexingService : IHostedService, IAsyncDisposable, IDisposable
{
    /// <summary>What a rebuild runs before indexing again: every file (and by cascade its chunks, vectors and symbols), and the meta rows the old content fixed.</summary>
    private static readonly string[] WipeStatements =
    [
        "DELETE FROM files",
        "DELETE FROM meta WHERE key IN ('symbols_version', 'vector_dimensions')",
    ];

    /// <summary>The client-to-host notification for a document saved in Visual Studio (one positional argument, the full path; contract 1.3, work item 2.10).</summary>
    public const string DocumentSavedNotification = "documentSaved";

    /// <summary>The client-to-host notification with every document open in Visual Studio (one positional argument, a string array of full paths; contract 1.3, work item 2.10).</summary>
    public const string OpenDocumentsChangedNotification = "openDocumentsChanged";

    /// <summary>How many reader connections are opened ahead of the first search (the Local Store keeps up to four).</summary>
    private const int WarmReaderConnections = 2;

    private readonly IndexStoreFactory _factory;
    private readonly IndexRetention _retention;
    private readonly IIndexEmbedder _embedder;
    private readonly IGatewaySettingsSource _settings;
    private readonly IFeatureFlags _features;
    private readonly IndexStatusNotifier _notifier;
    private readonly IdeActivityMonitor _activity;
    private readonly IPowerStatus _power;
    private readonly IndexingOptions _options;
    private readonly string _pipeName;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly ILogger _watcherLogger;
    private readonly IVsixConnection? _vsix;
    private readonly PauseGate _pause = new();
    private readonly MemoryGovernor _memory;
    private readonly PipelineScheduler _scheduler;
    private readonly SemaphoreSlim _wake = new(0, int.MaxValue);
    private readonly Lock _gate = new();
    private readonly Dictionary<string, RootIndex> _roots = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _rebuild = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _suppressed = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _stopping = new();
    private WorkspaceDescription? _workspace;
    private int _workspaceVersion;
    private int _appliedWorkspaceVersion = -1;
    private Task _loop = Task.CompletedTask;
    private ITimer? _powerTimer;
    private CancellationTokenSource? _runCancellation;
    private IndexPass? _currentPass;
    private RootIndex? _currentRoot;
    private string? _lastError;
    private string[] _openDocuments = [];
    private bool _failed;
    private bool _started;
    private int _disposed;
    private int _incrementalRuns;
    private int _savesReceived;
    private int _fullRuns;

    /// <summary>Initializes a new instance of the <see cref="IndexingService"/> class.</summary>
    /// <param name="factory">Opens indexes.</param>
    /// <param name="retention">Lists and deletes index folders.</param>
    /// <param name="embedder">Embeds chunks.</param>
    /// <param name="settings">The effective settings (exclusions, memory cap, battery rule).</param>
    /// <param name="features">The feature flags (<c>indexing</c>).</param>
    /// <param name="notifier">Publishes the status.</param>
    /// <param name="activity">What the IDE is busy with.</param>
    /// <param name="power">Reads battery saver.</param>
    /// <param name="memory">Samples the working set.</param>
    /// <param name="options">The tunables.</param>
    /// <param name="hostPipeName">This host's pipe name, recorded in <c>writer.lock</c>.</param>
    /// <param name="time">The clock.</param>
    /// <param name="loggerFactory">The logger factory.</param>
    /// <param name="vsix">The extension connection: IDE activity is forgotten when it drops. Optional.</param>
    public IndexingService(
        IndexStoreFactory factory,
        IndexRetention retention,
        IIndexEmbedder embedder,
        IGatewaySettingsSource settings,
        IFeatureFlags features,
        IndexStatusNotifier notifier,
        IdeActivityMonitor activity,
        IPowerStatus power,
        IMemorySampler memory,
        IndexingOptions options,
        string hostPipeName,
        TimeProvider? time = null,
        ILoggerFactory? loggerFactory = null,
        IVsixConnection? vsix = null)
    {
        _factory = factory;
        _retention = retention;
        _embedder = embedder;
        _settings = settings;
        _features = features;
        _notifier = notifier;
        _activity = activity;
        _power = power;
        _options = options;
        _pipeName = hostPipeName;
        _time = time ?? TimeProvider.System;
        _vsix = vsix;
        loggerFactory ??= NullLoggerFactory.Instance;
        _logger = loggerFactory.CreateLogger<IndexingService>();
        _watcherLogger = loggerFactory.CreateLogger<RootWatcher>();
        _memory = new MemoryGovernor(memory, _pause, MemoryCapBytes, options, _time, _logger, embedder.ReleaseMemory);
        _scheduler = new PipelineScheduler(options.MaxDegreeOfParallelism);
    }

    /// <summary>Gets the statistics of the last run that finished, for logs and tests.</summary>
    public PassStatistics? LastRunStatistics { get; private set; }

    /// <summary>Gets the statistics of the last incremental run (work item 2.10: watched and saved files), for logs and tests.</summary>
    public PassStatistics? LastIncrementalStatistics { get; private set; }

    /// <summary>Gets what the last incremental run found, for logs and tests.</summary>
    public IncrementalOutcome? LastIncrementalOutcome { get; private set; }

    /// <summary>Gets the statistics of the last incremental run that indexed a save relayed from Visual Studio, for logs and tests.</summary>
    public PassStatistics? LastSaveStatistics { get; private set; }

    /// <summary>Gets the time from the last indexed save's arrival to its file being searchable (work item 2.10; Gate 2: 500 ms).</summary>
    public TimeSpan? LastSaveToSearchable { get; private set; }

    /// <summary>Gets how many incremental runs finished (tests wait for the next one).</summary>
    public int IncrementalRuns => Volatile.Read(ref _incrementalRuns);

    /// <summary>Gets how many saves Visual Studio relayed (tests of the notification).</summary>
    internal int SavesReceived => Volatile.Read(ref _savesReceived);

    /// <summary>Gets the documents Visual Studio last reported open (tests of the notification).</summary>
    internal IReadOnlyList<string> OpenDocuments
    {
        get
        {
            lock (_gate)
            {
                return _openDocuments;
            }
        }
    }

    /// <summary>Gets how many full runs finished (tests wait for the next one).</summary>
    public int FullRuns => Volatile.Read(ref _fullRuns);

    /// <summary>Gets the pipeline's scheduler (tests check its concurrency).</summary>
    internal PipelineScheduler Scheduler => _scheduler;

    /// <summary>Gets the pause reasons (tests).</summary>
    internal PauseGate Pause => _pause;

    /// <summary>Gets the memory governor (tests).</summary>
    internal MemoryGovernor Memory => _memory;

    /// <summary>Gets the roots open now (tests search their storage directly).</summary>
    internal IReadOnlyList<RootIndex> OpenRoots
    {
        get
        {
            lock (_gate)
            {
                return [.. _roots.Values];
            }
        }
    }

    /// <summary>
    /// Asks for another run over the roots this host owns (all of them, or one): a full sweep that indexes whatever changed.
    /// The watcher of work item 2.10 calls it; tests use it to re-index after editing the tree.
    /// </summary>
    /// <param name="rootPath">The root, or <see langword="null"/> for every root.</param>
    internal void RequestRun(string? rootPath = null)
    {
        lock (_gate)
        {
            foreach (var (key, root) in _roots)
            {
                if (rootPath is null || string.Equals(key, Key(rootPath), StringComparison.OrdinalIgnoreCase))
                {
                    root.NeedsRun = true;
                }
            }
        }

        Wake();
    }

    /// <inheritdoc/>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_started)
            {
                return Task.CompletedTask;
            }

            _started = true;
        }

        _activity.Changed += OnActivityChanged;
        _pause.Changed += OnPauseChanged;
        _features.Changed += OnSettingsChanged;
        _settings.Changed += OnSettingsChanged;
        if (_vsix is not null)
        {
            _vsix.ConnectionChanged += OnVsixConnectionChanged;
        }

        ApplyActivity();
        ApplyPower();
        _powerTimer = _time.CreateTimer(_ => ApplyPower(), null, _options.PowerPollInterval, _options.PowerPollInterval);
        _loop = Task.Run(() => LoopAsync(_stopping.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        lock (_gate)
        {
            _runCancellation?.Cancel();
        }

        try
        {
            await _loop.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Stopping: the loop ends at its next checkpoint.
        }
    }

    /// <inheritdoc/>
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        _activity.Changed -= OnActivityChanged;
        _pause.Changed -= OnPauseChanged;
        _features.Changed -= OnSettingsChanged;
        _settings.Changed -= OnSettingsChanged;
        if (_vsix is not null)
        {
            _vsix.ConnectionChanged -= OnVsixConnectionChanged;
        }

        _powerTimer?.Dispose();
        _memory.Dispose();
        List<RootIndex> roots;
        lock (_gate)
        {
            roots = [.. _roots.Values];
            _roots.Clear();
        }

        foreach (var root in roots)
        {
            await root.DisposeAsync().ConfigureAwait(false);
        }

        _scheduler.Dispose();
        _wake.Dispose();
        _stopping.Dispose();
    }

    /// <summary>Takes what is open in Visual Studio and starts indexing it (<see cref="IContextService.SetWorkspaceAsync"/>).</summary>
    /// <param name="workspace">The solution and projects, or the folder.</param>
    public void SetWorkspace(WorkspaceDescription workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        var wanted = RepositoryRoots.Resolve(WorkspacePaths(workspace)).Select(r => Key(r.Path)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        lock (_gate)
        {
            _workspace = workspace;

            // The same roots (the extension sends the workspace again after every project load): nothing to restart.
            if (!_failed && _suppressed.Count == 0 && _appliedWorkspaceVersion == _workspaceVersion && wanted.SetEquals(_roots.Keys))
            {
                return;
            }

            _workspaceVersion++;
            _suppressed.Clear();
            _runCancellation?.Cancel();
        }

        LogWorkspace(_logger, workspace.SolutionPath is not null ? "solution" : workspace.FolderPath is not null ? "folder" : "nothing", workspace.ProjectPaths.Length);
        Wake();
    }

    /// <summary>
    /// Takes a document save relayed from Visual Studio (the <c>documentSaved</c> notification, work item 2.10): the file goes
    /// to its root's incremental indexer at once, without the watcher's debounce, and is searchable within
    /// <see cref="IndexingOptions.SaveSearchableBudget"/> of now. A file outside every root this host owns (another
    /// repository, or an index another Visual Studio owns), or excluded by the discovery rules, is ignored.
    /// </summary>
    /// <param name="fullPath">The saved file's full path.</param>
    public void NotifySaved(string? fullPath)
    {
        var savedAt = Stopwatch.GetTimestamp();
        Interlocked.Increment(ref _savesReceived);
        if (string.IsNullOrWhiteSpace(fullPath) || !_features.IsEnabled(FeatureNames.Indexing))
        {
            return;
        }

        string full;
        try
        {
            full = Path.GetFullPath(fullPath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return;
        }

        RootIndex? root = null;
        lock (_gate)
        {
            foreach (var candidate in _roots.Values)
            {
                if (candidate.IsWriter
                    && full.Length > candidate.Path.Length + 1
                    && full.StartsWith(candidate.Path, StringComparison.OrdinalIgnoreCase)
                    && full[candidate.Path.Length] is '\\' or '/'
                    && (root is null || candidate.Path.Length > root.Path.Length))
                {
                    root = candidate;
                }
            }
        }

        if (root is null)
        {
            LogSaveIgnored(_logger);
            return;
        }

        var relative = PathClassifier.Normalize(full[(root.Path.Length + 1)..]);
        try
        {
            if (!root.Classifier.ClassifyByName(relative).IsIncluded)
            {
                return;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The root went away: its watcher (and the next workspace change) deal with that.
            return;
        }

        root.Watcher?.Forget(relative);
        root.AddPending([relative], savedAt);
        Wake();
    }

    /// <summary>
    /// Takes the documents open in Visual Studio (the <c>openDocumentsChanged</c> notification, work item 2.10): a run indexes
    /// the open ones among its added and changed files first, so after a branch switch they are searchable before the rest.
    /// </summary>
    /// <param name="paths">The full paths of every open document; <see langword="null"/> or empty for none.</param>
    public void SetOpenDocuments(string[]? paths)
    {
        var open = (paths ?? []).Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        lock (_gate)
        {
            _openDocuments = open;
        }
    }

    /// <summary>Pauses or resumes indexing for the user (<see cref="IContextService.PauseIndexingAsync"/>).</summary>
    /// <param name="pause">Whether to pause.</param>
    public void SetUserPause(bool pause) => _pause.Set(IndexPauseReason.User, pause);

    /// <summary>Wipes a root's index and indexes it again (<see cref="IContextService.RebuildIndexAsync"/>).</summary>
    /// <param name="rootPath">The root.</param>
    /// <exception cref="PillionRpcException">The flag is off, the root is not open, or another host owns its index.</exception>
    public void Rebuild(string rootPath)
    {
        EnsureEnabled();
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        var key = Key(rootPath);
        lock (_gate)
        {
            if (!_roots.TryGetValue(key, out var root))
            {
                // Not open: a root of the workspace whose index was deleted (or failed to open) is opened again, from scratch.
                var inWorkspace = _workspace is not null
                    && RepositoryRoots.Resolve(WorkspacePaths(_workspace)).Any(r => string.Equals(Key(r.Path), key, StringComparison.OrdinalIgnoreCase));
                if (!inWorkspace)
                {
                    throw Denied($"'{rootPath}' is not a repository root of the open solution or folder, so there is no index to rebuild.");
                }

                _suppressed.Clear();
                _workspaceVersion++;
            }
            else if (!root.IsWriter)
            {
                throw Denied("Another Visual Studio instance owns this index; rebuild it from there (or close that instance and try again).");
            }
            else
            {
                _rebuild.Add(key);
                if (ReferenceEquals(_currentRoot, root))
                {
                    _runCancellation?.Cancel();
                }
            }
        }

        Wake();
    }

    /// <summary>Deletes an index folder (<see cref="IContextService.DeleteIndexAsync"/>); an index this host has open is closed first.</summary>
    /// <param name="indexId">The index id.</param>
    /// <param name="cancellationToken">Cancels waiting for the run to stop.</param>
    /// <returns><see langword="true"/> when deleted; <see langword="false"/> when there is no such index.</returns>
    /// <exception cref="PillionRpcException">The flag is off, another live host owns the index, or the folder could not be deleted.</exception>
    public async Task<bool> DeleteIndexAsync(string indexId, CancellationToken cancellationToken)
    {
        EnsureEnabled();
        RootIndex? open = null;
        lock (_gate)
        {
            foreach (var (key, root) in _roots)
            {
                if (string.Equals(root.Store.Identity.Id, indexId, StringComparison.Ordinal))
                {
                    if (!root.IsWriter)
                    {
                        throw Denied("Another Visual Studio instance owns this index; it cannot be deleted while that instance has it open.");
                    }

                    open = root;
                    _roots.Remove(key);
                    _suppressed.Add(indexId);
                    if (ReferenceEquals(_currentRoot, root))
                    {
                        _runCancellation?.Cancel();
                    }

                    break;
                }
            }
        }

        if (open is not null)
        {
            await WaitForRunToStopAsync(open, cancellationToken).ConfigureAwait(false);
            await open.DisposeAsync().ConfigureAwait(false);
        }

        var outcome = _retention.Delete(indexId);
        Publish();
        return outcome switch
        {
            IndexDeleteOutcome.Deleted => true,
            IndexDeleteOutcome.NotFound => false,
            IndexDeleteOutcome.InUse => throw Denied("Another Visual Studio instance owns this index; it cannot be deleted while that instance has it open."),
            _ => throw new PillionRpcException(new PillionError
            {
                Code = PillionErrorCode.StorageUnavailable,
                Message = "The index folder could not be deleted completely; a file in it is in use. Try again later.",
                Details = indexId,
            }),
        };
    }

    /// <summary>Lists every index folder on the machine (<see cref="IContextService.ListIndexesAsync"/>).</summary>
    /// <returns>The indexes, ordered by root path.</returns>
    /// <exception cref="PillionRpcException">The flag is off.</exception>
    public IndexInfo[] ListIndexes()
    {
        EnsureEnabled();
        HashSet<string> open;
        lock (_gate)
        {
            open = [.. _roots.Values.Select(r => r.Store.Identity.Id)];
        }

        return [.. _retention.GetUsage()
            .Select(u => new IndexInfo
            {
                IndexId = u.Id,
                RootPath = u.RootPath,
                EmbeddingModelId = u.EmbeddingModel,
                ChunkerVersion = u.ChunkerVersion,
                DiskBytes = u.DiskBytes,
                LastUsedUtc = u.LastUsedUtc.UtcDateTime,
                RootExists = u.RootExists,
                IsInUse = u.InUse,
                IsOpen = open.Contains(u.Id),
            })
            .OrderBy(i => i.RootPath ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ThenBy(i => i.IndexId, StringComparer.Ordinal)];
    }

    /// <summary>Pages the files a root's last sweep excluded (<see cref="IContextService.ListExcludedFilesAsync"/>).</summary>
    /// <param name="request">The root and the page.</param>
    /// <returns>The page; empty for a root that is not open or not swept yet.</returns>
    /// <exception cref="PillionRpcException">The flag is off.</exception>
    public ExcludedFilesPage ListExcludedFiles(ExcludedFilesRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureEnabled();
        IReadOnlyList<ExcludedFile> excluded;
        lock (_gate)
        {
            excluded = string.IsNullOrWhiteSpace(request.RootPath) || !_roots.TryGetValue(Key(request.RootPath), out var root) ? [] : root.Excluded;
        }

        var matching = request.SecretOnly ? excluded.Where(e => e.IsSecretBearing).ToList() : excluded;
        var limit = request.Limit <= 0 ? 200 : Math.Min(request.Limit, 1000);
        var offset = Math.Max(0, request.Offset);
        return new ExcludedFilesPage
        {
            Items = [.. matching.Skip(offset).Take(limit)],
            TotalCount = matching.Count,
            HasMore = offset + limit < matching.Count,
        };
    }

    /// <summary>Builds the status as <see cref="IContextService.GetIndexStatusAsync"/> returns it.</summary>
    /// <returns>The status.</returns>
    public IndexStatus GetStatus() => BuildStatus(withDiskUse: true);

    private static string Key(string path)
    {
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
    }

    private static PillionRpcException Denied(string message) =>
        new(new PillionError { Code = PillionErrorCode.PolicyDenied, Message = message });

    private static List<string> WorkspacePaths(WorkspaceDescription workspace)
    {
        var paths = new List<string>();
        if (!string.IsNullOrWhiteSpace(workspace.FolderPath))
        {
            paths.Add(workspace.FolderPath);
        }

        if (!string.IsNullOrWhiteSpace(workspace.SolutionPath))
        {
            paths.Add(workspace.SolutionPath);
        }

        paths.AddRange(workspace.ProjectPaths.Where(p => !string.IsNullOrWhiteSpace(p)));
        return paths;
    }

    private static List<IndexedProject> Projects(WorkspaceDescription workspace)
    {
        var projects = new List<IndexedProject>();
        foreach (var path in workspace.ProjectPaths.Where(p => !string.IsNullOrWhiteSpace(p)))
        {
            try
            {
                projects.Add(Directory.Exists(path)
                    ? new IndexedProject(Path.GetFileName(Path.TrimEndingDirectorySeparator(path)), path)
                    : IndexedProject.FromProjectFile(path));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                // A path that is not usable gives no partition.
            }
        }

        return projects;
    }

    private long MemoryCapBytes()
    {
        var mb = _settings.GetCurrent().Settings.Indexing.MaxMemoryMb;
        return mb <= 0 ? 0 : mb * 1024L * 1024L;
    }

    private void EnsureEnabled()
    {
        if (!_features.IsEnabled(FeatureNames.Indexing))
        {
            throw Denied(IndexStoreFactory.IndexingDisabledMessage);
        }
    }

    private void Wake()
    {
        try
        {
            _wake.Release();
        }
        catch (ObjectDisposedException)
        {
            // Disposed.
        }
    }

    private void OnActivityChanged(object? sender, EventArgs e) => ApplyActivity();

    private void OnPauseChanged(object? sender, EventArgs e)
    {
        Publish();

        // Resumed: the changes queued while paused are applied now, coalesced.
        if (!_pause.IsPaused)
        {
            Wake();
        }
    }

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        ApplyPower();
        var exclude = _settings.GetCurrent().Exclude;
        lock (_gate)
        {
            // The exclusions or the flag may have changed: look at the roots again.
            _workspaceVersion++;

            // New exclusions: the watcher's filter follows, and a sweep applies them to what is stored.
            foreach (var root in _roots.Values)
            {
                if (root.SetExclusions(exclude) && root.IsWriter)
                {
                    root.NeedsRun = true;
                }
            }
        }

        Wake();
    }

    private void OnVsixConnectionChanged(object? sender, EventArgs e)
    {
        if (_vsix is { IsConnected: false })
        {
            _activity.Set(IdeActivity.None);
            SetOpenDocuments(null);
        }
    }

    private void ApplyActivity()
    {
        var activity = _activity.Current;
        _pause.Set(IndexPauseReason.Build, activity.HasFlag(IdeActivity.Build));
        _pause.Set(IndexPauseReason.Test, activity.HasFlag(IdeActivity.Test));
        _pause.Set(IndexPauseReason.Debug, activity.HasFlag(IdeActivity.Debug));
    }

    private void ApplyPower()
    {
        bool saver;
        try
        {
            saver = _settings.GetCurrent().Settings.Indexing.PauseOnBattery && _power.IsBatterySaverOn();
        }
#pragma warning disable CA1031 // Reading the power state must never take indexing down.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogPowerFailed(_logger, ex);
            saver = false;
        }

        _pause.Set(IndexPauseReason.Battery, saver);
    }

    private async Task LoopAsync(CancellationToken stopping)
    {
        while (!stopping.IsCancellationRequested)
        {
            try
            {
                await ReconcileAsync(stopping).ConfigureAwait(false);
                await RunDueRootsAsync(stopping).ConfigureAwait(false);
                await CheckReadersAsync(stopping).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
                break;
            }
#pragma warning disable CA1031 // The loop must survive anything one root throws; the error is reported in the status.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                Fail(ex);
            }

            Publish();
            try
            {
                await _wake.WaitAsync(HasReaders() ? _options.ReaderPollInterval : Timeout.InfiniteTimeSpan, stopping).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private bool HasReaders()
    {
        lock (_gate)
        {
            return _roots.Values.Any(r => !r.IsWriter);
        }
    }

    /// <summary>Opens the indexes of the workspace's roots and closes the others (or all of them while the flag is off).</summary>
    private async Task ReconcileAsync(CancellationToken stopping)
    {
        WorkspaceDescription? workspace;
        int version;
        lock (_gate)
        {
            workspace = _workspace;
            version = _workspaceVersion;
        }

        if (version == _appliedWorkspaceVersion)
        {
            return;
        }

        var enabled = _features.IsEnabled(FeatureNames.Indexing);

        var wanted = new List<RepositoryRoot>();
        if (enabled && workspace is not null)
        {
            wanted.AddRange(RepositoryRoots.Resolve(WorkspacePaths(workspace)).Where(r => Directory.Exists(r.Path)));
        }

        // Close what is no longer wanted.
        List<RootIndex> closing;
        lock (_gate)
        {
            var keep = wanted.Select(r => Key(r.Path)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            closing = [.. _roots.Where(kv => !keep.Contains(kv.Key)).Select(kv => kv.Value)];
            foreach (var root in closing)
            {
                _roots.Remove(Key(root.Path));
            }
        }

        foreach (var root in closing)
        {
            await root.DisposeAsync().ConfigureAwait(false);
        }

        if (wanted.Count == 0)
        {
            _appliedWorkspaceVersion = version;
            ClearFailure();
            return;
        }

        IndexEmbeddingModel model;
        try
        {
            model = _embedder.ResolveModel();
        }
        catch (PillionRpcException ex)
        {
            _appliedWorkspaceVersion = version;
            Fail(ex);
            return;
        }

        var projects = Projects(workspace!);
        foreach (var repository in wanted)
        {
            stopping.ThrowIfCancellationRequested();
            var key = Key(repository.Path);
            lock (_gate)
            {
                if (_roots.TryGetValue(key, out var existing))
                {
                    if (string.Equals(existing.Model.ModelId, model.ModelId, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    // The embedding model changed: the index is a different one (design section 10).
                    _roots.Remove(key);
                    closing = [existing];
                }
                else
                {
                    closing = [];
                }
            }

            foreach (var old in closing)
            {
                await old.DisposeAsync().ConfigureAwait(false);
            }

            var identity = new IndexIdentity(repository.Path, model.ModelId, ContextEngineVersions.ChunkerVersion);
            lock (_gate)
            {
                if (_suppressed.Contains(identity.Id))
                {
                    continue;
                }
            }

            var opened = await OpenAsync(repository, model, projects, stopping).ConfigureAwait(false);
            if (opened is not null)
            {
                lock (_gate)
                {
                    _roots[key] = opened;
                }
            }
        }

        _appliedWorkspaceVersion = version;
    }

    private async Task<RootIndex?> OpenAsync(RepositoryRoot repository, IndexEmbeddingModel model, IReadOnlyList<IndexedProject> projects, CancellationToken stopping)
    {
        try
        {
            var store = await _factory.OpenAsync(new IndexRequest(repository.Path, model.ModelId, ContextEngineVersions.ChunkerVersion, _pipeName), stopping).ConfigureAwait(false);
            var root = new RootIndex(repository, store, model, projects);
            root.SetExclusions(_settings.GetCurrent().Exclude);
            await root.RefreshCountsAsync(stopping).ConfigureAwait(false);
            LogOpened(_logger, store.Identity.Id, root.Path, root.IsWriter ? "writer" : "reader");
            if (root.IsWriter && _options.WatchFileSystem)
            {
                StartWatcher(root);
            }

            _ = WarmAsync(root, stopping);
            return root;
        }
        catch (Exception ex) when (ex is LocalStoreException or IOException or UnauthorizedAccessException)
        {
            Fail(ex);
            return null;
        }
    }

    /// <summary>
    /// Opens the index's reader connections (each pays the cipher's key derivation once) and loads the in-memory vector copy,
    /// off the query path, so the first search after the workspace opens is not slowed down by either.
    /// </summary>
    private async Task WarmAsync(RootIndex root, CancellationToken stopping)
    {
        try
        {
            await Task.Yield();
            var readers = Enumerable.Range(0, WarmReaderConnections).Select(_ => root.Store.Database.ReadAsync(
                (connection, _) =>
                {
                    using var command = connection.CreateCommand();
                    command.CommandText = "SELECT count(*) FROM files";
                    return Task.FromResult(command.ExecuteScalar());
                },
                stopping));
            await Task.WhenAll(readers).ConfigureAwait(false);
            var bytes = await root.Storage.WarmVectorCacheAsync(stopping).ConfigureAwait(false);
            LogWarmed(_logger, root.Store.Identity.Id, bytes);
        }
#pragma warning disable CA1031 // Warming is an optimisation: a failure (the index closed meanwhile) only costs the first search.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogWarmFailed(_logger, ex);
        }
    }

    private async Task RunDueRootsAsync(CancellationToken stopping)
    {
        while (!stopping.IsCancellationRequested && _features.IsEnabled(FeatureNames.Indexing))
        {
            RootIndex? root;
            bool rebuild = false;
            bool incremental = false;
            lock (_gate)
            {
                // A full run (opened, rebuilt, a branch switch, an overflow) first: its sweep covers every queued change.
                root = _roots.Values.FirstOrDefault(r => r.IsWriter && (r.NeedsRun || _rebuild.Contains(Key(r.Path))));
                if (root is null && !_pause.IsPaused)
                {
                    // Then the changes the watcher and Visual Studio's saves queued (work item 2.10). While paused they stay
                    // queued and are applied, coalesced, on resume. The first full run of a session must have filled the
                    // symbol builder first.
                    root = _roots.Values.FirstOrDefault(r => r.IsWriter && r.SymbolsLoaded && r.HasPendingChanges);
                    incremental = root is not null;
                }

                if (root is null)
                {
                    return;
                }

                if (!incremental)
                {
                    rebuild = _rebuild.Remove(Key(root.Path));
                    root.NeedsRun = false;
                }

                _runCancellation = CancellationTokenSource.CreateLinkedTokenSource(stopping);
                _currentRoot = root;
            }

            // Only the owner of writer.lock writes: a host whose lock was taken from it stops writing and watching.
            if (!root.Store.HoldsWriterLock())
            {
                lock (_gate)
                {
                    _currentRoot = null;
                    _runCancellation?.Dispose();
                    _runCancellation = null;
                }

                await LoseWriterAsync(root, stopping).ConfigureAwait(false);
                continue;
            }

            var token = _runCancellation.Token;
            var pass = new IndexPass(root, _embedder, _options, _scheduler, _pause, _memory, _settings.GetCurrent().Exclude, Publish, _logger)
            {
                BeforeSweep = root.ClearPending,
                PriorityPaths = OpenDocumentsUnder(root),
            };
            Dictionary<string, long?>? pending = null;
            try
            {
                if (rebuild)
                {
                    await WipeAsync(root, token).ConfigureAwait(false);
                }

                // An incremental run is reported as Storing with its file count from the start (it has no sweep).
                if (incremental)
                {
                    pass.Progress.Advance(IndexPhase.Storing);
                }

                lock (_gate)
                {
                    _currentPass = pass;
                }

                ClearFailure();

                Publish();
                if (incremental)
                {
                    pending = root.TakePending();
                    var outcome = await pass.RunIncrementalAsync(pending, token).ConfigureAwait(false);
                    LastIncrementalStatistics = pass.Statistics;
                    LastIncrementalOutcome = outcome;
                    Interlocked.Increment(ref _incrementalRuns);
                    if (outcome.SaveToSearchable is { } searchable)
                    {
                        LastSaveStatistics = pass.Statistics;
                        LastSaveToSearchable = searchable;
                        LogSaveSearchable(_logger, searchable.TotalMilliseconds);
                    }

                    if (outcome.SweepNeeded)
                    {
                        lock (_gate)
                        {
                            root.NeedsRun = true;
                        }
                    }

                    if (outcome.Retry.Count > 0)
                    {
                        _ = RetryLaterAsync(root, outcome.Retry, stopping);
                    }
                }
                else
                {
                    await pass.RunAsync(token).ConfigureAwait(false);
                    LastRunStatistics = pass.Statistics;
                    Interlocked.Increment(ref _fullRuns);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested && !stopping.IsCancellationRequested)
            {
                // Cancelled by a workspace change, a rebuild or a delete: the next round decides what runs.
                lock (_gate)
                {
                    if (_roots.ContainsValue(root) && !_rebuild.Contains(Key(root.Path)))
                    {
                        if (incremental)
                        {
                            Requeue(root, pending);
                        }
                        else
                        {
                            root.NeedsRun = true;
                        }
                    }
                }
            }
#pragma warning disable CA1031 // One root's failure is reported in the status; the others still run.
            catch (Exception ex) when (!stopping.IsCancellationRequested)
#pragma warning restore CA1031
            {
                Fail(ex);
            }
            finally
            {
                lock (_gate)
                {
                    _currentPass = null;
                    _currentRoot = null;
                    _runCancellation?.Dispose();
                    _runCancellation = null;
                }

                Publish();
            }

            // A workspace change cancelled the run: reconcile before running anything else.
            if (_workspaceVersion != _appliedWorkspaceVersion)
            {
                return;
            }
        }
    }

    /// <summary>The open documents under a root, relative to it.</summary>
    private HashSet<string> OpenDocumentsUnder(RootIndex root)
    {
        string[] open;
        lock (_gate)
        {
            open = _openDocuments;
        }

        var under = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in open)
        {
            try
            {
                var full = Path.GetFullPath(path);
                if (full.Length > root.Path.Length + 1 && full.StartsWith(root.Path, StringComparison.OrdinalIgnoreCase) && full[root.Path.Length] is '\\' or '/')
                {
                    under.Add(PathClassifier.Normalize(full[(root.Path.Length + 1)..]));
                }
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                // Not a usable path: not prioritised.
            }
        }

        return under;
    }

    private static void Requeue(RootIndex root, Dictionary<string, long?>? pending)
    {
        if (pending is null)
        {
            return;
        }

        foreach (var (path, savedAt) in pending)
        {
            root.AddPending([path], savedAt);
        }
    }

    /// <summary>Starts the watcher of a root this host owns (work item 2.10).</summary>
    private void StartWatcher(RootIndex root)
    {
        var watcher = new RootWatcher(
            root.Path,
            root.Root.GitDirectory,
            () => root.Classifier,
            _options,
            _time,
            _watcherLogger,
            paths =>
            {
                root.AddPending(paths);
                Wake();
            },
            reason =>
            {
                LogSweepRequested(_logger, root.Store.Identity.Id, reason);
                lock (_gate)
                {
                    root.NeedsRun = true;
                }

                Wake();
            });
        root.Watcher = watcher;
        watcher.Start();
    }

    /// <summary>A file that was locked when the incremental run read it is queued again a moment later.</summary>
    private async Task RetryLaterAsync(RootIndex root, List<string> paths, CancellationToken stopping)
    {
        try
        {
            await Task.Delay(_options.WatcherDebounce, _time, stopping).ConfigureAwait(false);
            root.AddPending(paths);
            Wake();
        }
        catch (OperationCanceledException)
        {
            // Stopping.
        }
    }

    /// <summary>
    /// This host no longer holds the root's <c>writer.lock</c> (deleted, or reclaimed by a host that wrongly took this one
    /// for dead): the watcher stops with the root, and the index is opened again, as a reader when another live host owns it.
    /// </summary>
    private async Task LoseWriterAsync(RootIndex root, CancellationToken stopping)
    {
        LogLostLock(_logger, root.Store.Identity.Id, root.Path);
        var key = Key(root.Path);
        lock (_gate)
        {
            if (!_roots.TryGetValue(key, out var current) || !ReferenceEquals(current, root))
            {
                return;
            }

            _roots.Remove(key);
        }

        await root.DisposeAsync().ConfigureAwait(false);
        WorkspaceDescription? workspace;
        lock (_gate)
        {
            workspace = _workspace;
        }

        var reopened = await OpenAsync(root.Root, root.Model, workspace is null ? [] : Projects(workspace), stopping).ConfigureAwait(false);
        if (reopened is not null)
        {
            lock (_gate)
            {
                _roots[key] = reopened;
            }
        }
    }

    private async Task WipeAsync(RootIndex root, CancellationToken cancellationToken)
    {
        LogRebuild(_logger, root.Store.Identity.Id);
        await root.Storage.WriteAsync(
            (session, _) =>
            {
                foreach (var sql in WipeStatements)
                {
                    using var command = session.Connection.CreateCommand();
                    command.Transaction = session.Transaction;
                    command.CommandText = sql;
                    command.ExecuteNonQuery();
                }

                return true;
            },
            cancellationToken).ConfigureAwait(false);
        await root.Store.Database.VacuumAsync(cancellationToken).ConfigureAwait(false);
        root.ResetSession();
        root.Excluded = [];
        root.TotalFileCount = -1;
        await root.RefreshCountsAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task CheckReadersAsync(CancellationToken stopping)
    {
        List<RootIndex> readers;
        lock (_gate)
        {
            readers = [.. _roots.Values.Where(r => !r.IsWriter)];
        }

        foreach (var reader in readers)
        {
            if (reader.Store.GetLiveWriter() is not null)
            {
                continue;
            }

            // The owner is gone: open again, which reclaims the stale lock; this host becomes the writer and sweeps.
            var key = Key(reader.Path);
            lock (_gate)
            {
                _roots.Remove(key);
            }

            await reader.DisposeAsync().ConfigureAwait(false);
            WorkspaceDescription? workspace;
            lock (_gate)
            {
                workspace = _workspace;
            }

            var reopened = await OpenAsync(reader.Root, reader.Model, workspace is null ? [] : Projects(workspace), stopping).ConfigureAwait(false);
            if (reopened is not null)
            {
                lock (_gate)
                {
                    _roots[key] = reopened;
                }

                Wake();
            }
        }
    }

    private async Task WaitForRunToStopAsync(RootIndex root, CancellationToken cancellationToken)
    {
        var deadline = Stopwatch.GetTimestamp();
        while (true)
        {
            lock (_gate)
            {
                if (!ReferenceEquals(_currentRoot, root))
                {
                    return;
                }
            }

            if (Stopwatch.GetElapsedTime(deadline) > TimeSpan.FromSeconds(30))
            {
                return;
            }

            await Task.Delay(20, cancellationToken).ConfigureAwait(false);
        }
    }

    private void ClearFailure()
    {
        lock (_gate)
        {
            _failed = false;
            _lastError = null;
        }
    }

    private void Fail(Exception ex)
    {
        var message = ex is PillionRpcException rpc ? rpc.Error.Message : ex.Message;
        lock (_gate)
        {
            _failed = true;
            _lastError = message;
        }

        LogFailed(_logger, ex, message);
    }

    private void Publish()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        _notifier.Publish(BuildStatus(withDiskUse: false));
    }

    private IndexStatus BuildStatus(bool withDiskUse)
    {
        if (!_features.IsEnabled(FeatureNames.Indexing))
        {
            return new IndexStatus();
        }

        lock (_gate)
        {
            var pass = _currentPass;
            var paused = _pause.IsPaused;
            var running = pass is not null;
            // Paused whenever a reason holds and there is an index to work on, so the Index window can say why.
            var phase = paused && (running || _roots.Count > 0)
                ? IndexPhase.Paused
                : running
                    ? (pass!.Progress.Phase == IndexPhase.Idle ? IndexPhase.Discovering : pass.Progress.Phase)
                    : _failed ? IndexPhase.Failed : IndexPhase.Idle;

            var roots = _roots.Values.OrderBy(r => r.Path, StringComparer.OrdinalIgnoreCase).Select(r => new IndexRootStatus
            {
                RootPath = r.Path,
                IndexId = r.Store.Identity.Id,
                Role = r.ContractRole,
                IndexedFileCount = (int)Math.Min(int.MaxValue, r.IndexedFileCount),
                TotalFileCount = Math.Max(0, r.TotalFileCount),
                ExcludedFileCount = r.Excluded.Count,
                SecretFileCount = r.SecretFileCount,
                ChunkCount = r.ChunkCount,
                DiskBytes = withDiskUse ? r.Store.Folder.GetDiskUsageBytes() : 0,
                LastFullSweepUtc = r.Store.State.LastFullSweepUtc?.UtcDateTime,
                EmbeddingModelId = r.Model.ModelId,
                ChunkerVersion = r.Store.Identity.ChunkerVersion,
                SchemaVersion = r.Store.Database.SchemaVersion,
            }).ToArray();

            var lastUpdated = _roots.Values.Select(r => r.LastUpdatedUtc).Where(t => t is not null).DefaultIfEmpty(null).Max();
            return new IndexStatus
            {
                IsIndexing = running && !paused,
                IsPaused = paused,
                IndexedFileCount = roots.Sum(r => r.IndexedFileCount),
                TotalFileCount = roots.Sum(r => r.TotalFileCount),
                LastUpdatedUtc = lastUpdated?.UtcDateTime,
                Phase = phase,
                PauseReason = phase == IndexPhase.Paused ? _pause.Current : IndexPauseReason.None,
                Roots = roots,
                ExcludedSecretFileCount = roots.Sum(r => r.SecretFileCount),
                LastError = phase == IndexPhase.Failed ? _lastError : null,
                Progress = running
                    ? new IndexProgress
                    {
                        FilesDone = pass!.Progress.FilesDone,
                        FilesTotal = pass.Progress.FilesTotal,
                        ChunksDone = pass.Progress.ChunksDone,
                        EstimatedRemainingSeconds = pass.Progress.EstimateRemainingSeconds(),
                    }
                    : null,
            };
        }
    }

    [LoggerMessage(EventId = 2910, Level = LogLevel.Information, Message = "The extension reported the workspace: {Kind} with {Projects} projects.")]
    private static partial void LogWorkspace(ILogger logger, string kind, int projects);

    [LoggerMessage(EventId = 2911, Level = LogLevel.Information, Message = "Index {IndexId} for {Root} is open as the {Role}.")]
    private static partial void LogOpened(ILogger logger, string indexId, string root, string role);

    [LoggerMessage(EventId = 2912, Level = LogLevel.Warning, Message = "Indexing failed: {Reason}")]
    private static partial void LogFailed(ILogger logger, Exception exception, string reason);

    [LoggerMessage(EventId = 2913, Level = LogLevel.Information, Message = "Index {IndexId} is wiped for a rebuild.")]
    private static partial void LogRebuild(ILogger logger, string indexId);

    [LoggerMessage(EventId = 2915, Level = LogLevel.Debug, Message = "Index {IndexId} is warmed for search ({Bytes} bytes of vectors in memory).")]
    private static partial void LogWarmed(ILogger logger, string indexId, long bytes);

    [LoggerMessage(EventId = 2916, Level = LogLevel.Debug, Message = "Warming an index for search failed; the first search pays for it.")]
    private static partial void LogWarmFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 2917, Level = LogLevel.Information, Message = "Index {IndexId} is swept: {Reason}.")]
    private static partial void LogSweepRequested(ILogger logger, string indexId, string reason);

    [LoggerMessage(EventId = 2918, Level = LogLevel.Warning, Message = "This host no longer holds the writer lock of index {IndexId} for {Root}; it stops writing and watching, and opens the index again.")]
    private static partial void LogLostLock(ILogger logger, string indexId, string root);

    [LoggerMessage(EventId = 2919, Level = LogLevel.Debug, Message = "A saved file was searchable {Milliseconds:0} ms after the save arrived.")]
    private static partial void LogSaveSearchable(ILogger logger, double milliseconds);

    [LoggerMessage(EventId = 2969, Level = LogLevel.Debug, Message = "A saved document is outside every repository root this host indexes; it is not indexed here.")]
    private static partial void LogSaveIgnored(ILogger logger);

    [LoggerMessage(EventId = 2914, Level = LogLevel.Debug, Message = "The battery saver state could not be read; indexing is not paused for it.")]
    private static partial void LogPowerFailed(ILogger logger, Exception exception);
}
