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
    private readonly Dictionary<string, RootIndex> _retiring = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _rebuild = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _suppressed = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _stopping = new();
    private WorkspaceDescription? _workspace;
    private string[] _workspaceRoots = [];
    private string? _currentModelId;
    private int _workspaceVersion;
    private int _appliedWorkspaceVersion = -1;
    private Task _loop = Task.CompletedTask;
    private ITimer? _powerTimer;
    private ITimer? _retentionTimer;
    private DateTimeOffset? _nextRetentionSweep;
    private int _retentionSweeps;
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

    /// <summary>Gets the report of the last scheduled retention sweep (issue #99), for logs and tests; <see langword="null"/> before the first.</summary>
    public IndexRetentionReport? LastRetentionReport { get; private set; }

    /// <summary>Gets how many scheduled retention sweeps finished (tests wait for the next one).</summary>
    internal int RetentionSweeps => Volatile.Read(ref _retentionSweeps);

    /// <summary>Gets how many full runs finished (tests wait for the next one).</summary>
    public int FullRuns => Volatile.Read(ref _fullRuns);

    /// <summary>Gets the pipeline's scheduler (tests check its concurrency).</summary>
    internal PipelineScheduler Scheduler => _scheduler;

    /// <summary>Gets the pause reasons (tests).</summary>
    internal PauseGate Pause => _pause;

    /// <summary>Gets the memory governor (tests).</summary>
    internal MemoryGovernor Memory => _memory;

    /// <summary>Gets the roots open now under the configured embedding model and chunker version (tests search their storage directly).</summary>
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
    /// Gets the previous indexes still open read-only (issue #104, work item 3.2): when a root's embedding model or chunker
    /// version changes, its previous index stays open, is not indexed or watched, and serves searches until the new index's
    /// first full sweep completes; then it is closed and retention retires it.
    /// </summary>
    internal IReadOnlyList<RootIndex> RetiringRoots
    {
        get
        {
            lock (_gate)
            {
                return [.. _retiring.Values];
            }
        }
    }

    /// <summary>
    /// Gets, per root, the index that searches and mentions read now: the previous index while the new one has no completed
    /// full sweep, the new one from then on (and a root with no previous index as it is).
    /// </summary>
    internal IReadOnlyList<RootIndex> ServingRoots
    {
        get
        {
            lock (_gate)
            {
                var serving = new List<RootIndex>(_roots.Count + _retiring.Count);
                foreach (var (key, root) in _roots)
                {
                    serving.Add(_retiring.TryGetValue(key, out var previous) && !IsComplete(root) ? previous : root);
                }

                // A previous index whose root has no current index open (the new one failed to open): it still serves.
                foreach (var (key, previous) in _retiring)
                {
                    if (!_roots.ContainsKey(key))
                    {
                        serving.Add(previous);
                    }
                }

                return serving;
            }
        }
    }

    /// <summary>
    /// Whether an index has ever completed a full sweep (it was built to the end, now or in an earlier session). For an index
    /// another host owns, the in-memory state is the snapshot taken at open; <see cref="CheckReadersAsync"/> re-reads the
    /// owner's <c>state.json</c> at every poll and sets <see cref="RootIndex.OwnerSweepUtc"/> when the owner has finished.
    /// </summary>
    private static bool IsComplete(RootIndex root) => root.Store.State.LastFullSweepUtc is not null || root.OwnerSweepUtc is not null;

    /// <summary>
    /// Gets the repository roots of the solution or folder open in Visual Studio (full paths, as <see cref="SetWorkspace"/>
    /// resolved them); empty while nothing is open. Retrieval, the mentions and the repo map read only these roots' indexes
    /// (design sections 7 and 12; issue #98): an index of another folder on this machine never reaches a prompt.
    /// </summary>
    internal IReadOnlyList<string> WorkspaceRoots
    {
        get
        {
            lock (_gate)
            {
                return _workspaceRoots;
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
        _retentionTimer?.Dispose();
        _memory.Dispose();
        List<RootIndex> roots;
        lock (_gate)
        {
            roots = [.. _roots.Values, .. _retiring.Values];
            _roots.Clear();
            _retiring.Clear();
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
        var resolved = RepositoryRoots.Resolve(WorkspacePaths(workspace)).Select(r => Key(r.Path)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var wanted = resolved.ToHashSet(StringComparer.OrdinalIgnoreCase);
        lock (_gate)
        {
            _workspace = workspace;
            _workspaceRoots = resolved;

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

            // A root's previous index (issue #104) can be deleted before the switch completes: searches then read the new one.
            if (open is null)
            {
                foreach (var (key, previous) in _retiring)
                {
                    if (string.Equals(previous.Store.Identity.Id, indexId, StringComparison.Ordinal))
                    {
                        open = previous;
                        _retiring.Remove(key);
                        break;
                    }
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
            open = [.. _roots.Values.Select(r => r.Store.Identity.Id), .. _retiring.Values.Select(r => r.Store.Identity.Id)];
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
        var modelChanged = EmbeddingModelChanged();
        lock (_gate)
        {
            // The exclusions, the model or the flag may have changed: look at the roots again.
            _workspaceVersion++;

            // New exclusions: the watcher's filter follows, and a sweep applies them to what is stored.
            foreach (var root in _roots.Values)
            {
                if (root.SetExclusions(exclude) && root.IsWriter)
                {
                    root.NeedsRun = true;
                }
            }

            // A model switch takes effect now rather than when the current run ends (issue #104): the run of an index that is
            // about to become the root's previous one is cancelled; the files it stored are kept.
            if (modelChanged)
            {
                _runCancellation?.Cancel();
            }
        }

        Wake();
    }

    /// <summary>Whether the configured embedding model differs from the one the open indexes were resolved for.</summary>
    private bool EmbeddingModelChanged()
    {
        string? current;
        lock (_gate)
        {
            current = _currentModelId;
        }

        if (current is null)
        {
            return false;
        }

        try
        {
            return !string.Equals(_embedder.ResolveModel().ModelId, current, StringComparison.Ordinal);
        }
        catch (PillionRpcException)
        {
            return false;
        }
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
                await SweepRetentionIfDueAsync(stopping).ConfigureAwait(false);
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

        // Close what is no longer wanted (a root's previous index goes with it).
        List<RootIndex> closing;
        lock (_gate)
        {
            var keep = wanted.Select(r => Key(r.Path)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            closing = [.. _roots.Where(kv => !keep.Contains(kv.Key)).Select(kv => kv.Value), .. _retiring.Where(kv => !keep.Contains(kv.Key)).Select(kv => kv.Value)];
            foreach (var root in closing)
            {
                _roots.Remove(Key(root.Path));
                _retiring.Remove(Key(root.Path));
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

        lock (_gate)
        {
            _currentModelId = model.ModelId;
        }

        var projects = Projects(workspace!);
        foreach (var repository in wanted)
        {
            stopping.ThrowIfCancellationRequested();
            var key = Key(repository.Path);
            var identity = new IndexIdentity(repository.Path, model.ModelId, ContextEngineVersions.ChunkerVersion);
            RootIndex? retire = null;
            RootIndex? discard = null;
            RootIndex? reinstate = null;
            closing = [];
            lock (_gate)
            {
                if (_roots.TryGetValue(key, out var existing))
                {
                    if (string.Equals(existing.Store.Identity.Id, identity.Id, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    // The embedding model (or the chunker version) changed: the index is a different one (design section 10).
                    // The one open now becomes the root's previous index: it stays open read-only and serves searches until
                    // the new index's first full sweep completes (issue #104). A root keeps one previous index at most.
                    _roots.Remove(key);
                    retire = existing;
                }

                if (_retiring.TryGetValue(key, out var previous))
                {
                    if (string.Equals(previous.Store.Identity.Id, identity.Id, StringComparison.Ordinal))
                    {
                        // Switched back to the previous index's model while it was still open: it is the current index again.
                        _retiring.Remove(key);
                        reinstate = previous;
                    }
                    else if (retire is not null)
                    {
                        // A third model before the second index completed: the previous index that stays is the one that can
                        // serve, the complete one, and the newer of the two when both are complete. A complete previous index
                        // is never closed in favour of an incomplete one, which would leave the root with nothing to search.
                        if (IsComplete(retire) || !IsComplete(previous))
                        {
                            _retiring.Remove(key);
                            closing.Add(previous);
                        }
                        else
                        {
                            discard = retire;
                            retire = null;
                        }
                    }
                }
            }

            foreach (var old in closing)
            {
                await old.DisposeAsync().ConfigureAwait(false);
            }

            if (discard is not null)
            {
                // The incomplete index gives way to the complete previous one: stop its run, then close it.
                await StopRunAsync(discard, stopping).ConfigureAwait(false);
                await discard.DisposeAsync().ConfigureAwait(false);
                LogDiscarded(_logger, discard.Store.Identity.Id, discard.Path, discard.Model.ModelId);
            }

            if (retire is not null)
            {
                await RetireAsync(key, retire, stopping).ConfigureAwait(false);
            }

            if (reinstate is not null)
            {
                Reinstate(key, reinstate);
                await CloseRetiredAsync(key, stopping).ConfigureAwait(false);
                continue;
            }

            lock (_gate)
            {
                if (_suppressed.Contains(identity.Id))
                {
                    continue;
                }
            }

            var opened = await OpenAsync(repository, model, projects, stopping).ConfigureAwait(false);
            if (opened is null)
            {
                continue;
            }

            lock (_gate)
            {
                _roots[key] = opened;
            }

            bool hasPrevious;
            lock (_gate)
            {
                hasPrevious = _retiring.ContainsKey(key);
            }

            if (IsComplete(opened))
            {
                // Built to the end in an earlier session: nothing to wait for.
                await CloseRetiredAsync(key, stopping).ConfigureAwait(false);
            }
            else if (!hasPrevious)
            {
                // A host restart in the middle of a switch: the previous index is on disk, complete and unused; serve it
                // read-only until this one's first full sweep completes, as if the switch had happened in this session. Not
                // when the root already has a previous index open (one retired in this session, or kept when an incomplete
                // newer index was discarded above): that one serves, and an older index on disk never replaces it.
                await AdoptPreviousAsync(key, repository, model, projects, stopping).ConfigureAwait(false);
            }
        }

        _appliedWorkspaceVersion = version;
    }

    /// <summary>Cancels a run in progress on the root, if any, and waits for it to stop.</summary>
    private async Task StopRunAsync(RootIndex root, CancellationToken stopping)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_currentRoot, root))
            {
                _runCancellation?.Cancel();
            }
        }

        await WaitForRunToStopAsync(root, stopping).ConfigureAwait(false);
    }

    /// <summary>
    /// Turns the root's current index into its previous one (issue #104): its run is stopped, its watcher closed and its
    /// queued changes dropped; it is never indexed again, only searched, until <see cref="CloseRetiredAsync"/> closes it.
    /// </summary>
    private async Task RetireAsync(string key, RootIndex root, CancellationToken stopping)
    {
        await StopRunAsync(root, stopping).ConfigureAwait(false);
        root.Watcher?.Dispose();
        root.Watcher = null;
        root.ClearPending();
        root.IsRetiring = true;
        root.NeedsRun = false;
        lock (_gate)
        {
            _retiring[key] = root;
        }

        LogRetiring(_logger, root.Store.Identity.Id, root.Path, root.Model.ModelId);
    }

    /// <summary>Makes a previous index the root's current index again (the model was switched back while it was still open).</summary>
    private void Reinstate(string key, RootIndex root)
    {
        root.IsRetiring = false;
        root.NeedsRun = true;
        lock (_gate)
        {
            _roots[key] = root;
        }

        if (root.IsWriter && _options.WatchFileSystem)
        {
            StartWatcher(root);
        }

        LogReinstated(_logger, root.Store.Identity.Id, root.Path, root.Model.ModelId);
    }

    /// <summary>
    /// Closes the root's previous index once the current one has completed a full sweep. From then on retention retires the
    /// previous index's folder by the age rule (it is no longer protected as an open index), or the Index window deletes it.
    /// </summary>
    private async Task CloseRetiredAsync(string key, CancellationToken stopping)
    {
        RootIndex? previous;
        lock (_gate)
        {
            if (!_retiring.TryGetValue(key, out previous) || !_roots.TryGetValue(key, out var current) || !IsComplete(current))
            {
                return;
            }

            _retiring.Remove(key);
        }

        stopping.ThrowIfCancellationRequested();
        await previous.DisposeAsync().ConfigureAwait(false);
        LogRetired(_logger, previous.Store.Identity.Id, previous.Path, previous.Model.ModelId);
        Publish();
    }

    /// <summary>
    /// After a host restart in the middle of a switch: opens the most recently used complete index of the root under another
    /// model or chunker version, if no live host holds it, as the root's previous index. Failing to is not an error: the new
    /// index simply serves from its first stored batch, as before issue #104. A previous index the root already has open is
    /// never replaced (it would be left open and detached, holding its lock, while an older index served in its place): with
    /// one present nothing is opened, and one that appears meanwhile keeps its place.
    /// </summary>
    private async Task AdoptPreviousAsync(string key, RepositoryRoot repository, IndexEmbeddingModel model, IReadOnlyList<IndexedProject> projects, CancellationToken stopping)
    {
        lock (_gate)
        {
            if (_retiring.ContainsKey(key))
            {
                return;
            }
        }

        IndexUsage? candidate;
        try
        {
            var normalized = IndexIdentity.NormalizeRoot(repository.Path);
            candidate = _retention.GetUsage()
                .Where(u => !u.InUse && !string.IsNullOrEmpty(u.EmbeddingModel) && !string.IsNullOrEmpty(u.ChunkerVersion) && !string.IsNullOrWhiteSpace(u.RootPath))
                .Where(u => !(string.Equals(u.EmbeddingModel, model.ModelId, StringComparison.Ordinal) && string.Equals(u.ChunkerVersion, ContextEngineVersions.ChunkerVersion, StringComparison.Ordinal)))
                .Where(u => SameRoot(u.RootPath!, normalized))
                .Where(u => IndexStateFile.Read(Path.Combine(u.Directory, IndexFolder.StateFileName))?.LastFullSweepUtc is not null)
                .OrderByDescending(u => u.LastUsedUtc)
                .FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogPreviousNotAdopted(_logger, repository.Path, ex.Message);
            return;
        }

        if (candidate is null)
        {
            return;
        }

        try
        {
            var store = await _factory.OpenAsync(new IndexRequest(repository.Path, candidate.EmbeddingModel!, candidate.ChunkerVersion!, _pipeName), stopping).ConfigureAwait(false);
            var previous = new RootIndex(repository, store, model with { ModelId = candidate.EmbeddingModel! }, projects) { IsRetiring = true, NeedsRun = false };
            await previous.RefreshCountsAsync(stopping).ConfigureAwait(false);
            bool adopted;
            lock (_gate)
            {
                adopted = _retiring.TryAdd(key, previous);
            }

            if (!adopted)
            {
                await previous.DisposeAsync().ConfigureAwait(false);
                return;
            }

            _ = WarmAsync(previous, stopping);
            LogPreviousAdopted(_logger, store.Identity.Id, repository.Path, candidate.EmbeddingModel!);
        }
        catch (Exception ex) when (ex is LocalStoreException or IOException or UnauthorizedAccessException)
        {
            LogPreviousNotAdopted(_logger, repository.Path, ex.Message);
        }

        static bool SameRoot(string rootPath, string normalized)
        {
            try
            {
                return string.Equals(IndexIdentity.NormalizeRoot(rootPath), normalized, StringComparison.Ordinal);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return false;
            }
        }
    }

    /// <summary>
    /// The scheduled retention sweep (design section 10, issue #99): once the workspace the extension reported is applied (its
    /// indexes open), then every <see cref="IndexingOptions.RetentionSweepInterval"/>. Indexes whose root is gone, or unopened
    /// for <c>indexing.retentionDays</c>, are deleted; an index a live host holds is never deleted (<see cref="IndexRetention"/>
    /// keeps it), nor one this host has open, nor the index a root of the open workspace would open now (see
    /// <see cref="IsProtectedFromRetention"/>; a root's index under an older model or chunker version retires by the age rule
    /// once its switch completed, issue #104). Runs on the pipeline loop, so no root is opened while it deletes. Logs every
    /// index it deletes.
    /// </summary>
    private async Task SweepRetentionIfDueAsync(CancellationToken stopping)
    {
        var interval = _options.RetentionSweepInterval;
        if (interval <= TimeSpan.Zero)
        {
            return;
        }

        lock (_gate)
        {
            // Not before the workspace's indexes are open: the extension reported it and this loop applied it.
            if (_workspace is null || _appliedWorkspaceVersion != _workspaceVersion)
            {
                return;
            }
        }

        var now = _time.GetUtcNow();
        if (_nextRetentionSweep is { } next && now < next)
        {
            return;
        }

        _nextRetentionSweep = now + interval;
        _retentionTimer ??= _time.CreateTimer(_ => Wake(), null, interval, interval);
        var days = _settings.GetCurrent().Settings.Indexing.RetentionDays;
        IndexRetentionReport report;
        try
        {
            report = await _retention.SweepAsync(dryRun: false, retentionDays: days, cancellationToken: stopping, protect: IsProtectedFromRetention).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogRetentionFailed(_logger, ex.GetType().Name, ex.Message);
            return;
        }

        LastRetentionReport = report;
        Interlocked.Increment(ref _retentionSweeps);
        if (report.Skipped)
        {
            return;
        }

        var deleted = 0;
        foreach (var item in report.Items.Where(i => i.WantsDelete))
        {
            var reason = item.Decision == RetentionDecision.DeleteRootMissing ? "its repository root no longer exists" : $"not opened for more than {days} days";
            if (item.Deleted)
            {
                deleted++;
                LogRetentionDeleted(_logger, item.Usage.Id, item.Usage.RootPath ?? "(unknown root)", reason, item.Usage.DiskBytes / 1048576.0);
            }
            else
            {
                LogRetentionDeleteFailed(_logger, item.Usage.Id, item.Usage.RootPath ?? "(unknown root)", reason, item.Error ?? "unknown");
            }
        }

        LogRetentionSwept(_logger, report.Items.Count, deleted, report.BytesReclaimed / 1048576.0, interval.TotalHours);
    }

    /// <summary>
    /// Whether retention must keep an index: one this host has open (a root's current or previous index), or the index a root
    /// of the open workspace would open now (the configured model and this build's chunker version; it may be closed because
    /// its open failed or it was just deleted). Another index of an open root, under an older model or chunker version, is
    /// not protected once it is closed: that is how a previous index retires after a model switch (issue #104), by the age rule.
    /// </summary>
    private bool IsProtectedFromRetention(IndexUsage usage)
    {
        HashSet<string> ids;
        HashSet<string> roots;
        string? modelId;
        lock (_gate)
        {
            ids = [.. _roots.Values.Select(r => r.Store.Identity.Id), .. _retiring.Values.Select(r => r.Store.Identity.Id)];
            roots = new HashSet<string>(_roots.Values.Select(r => r.Store.Identity.NormalizedRoot), StringComparer.Ordinal);
            foreach (var root in _workspaceRoots)
            {
                roots.Add(IndexIdentity.NormalizeRoot(root));
            }

            modelId = _currentModelId;
        }

        if (ids.Contains(usage.Id))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(usage.RootPath))
        {
            return false;
        }

        // The model is unknown until the first reconcile resolved it; until then every index of an open root is kept.
        var current = modelId is null
            || (string.Equals(usage.EmbeddingModel, modelId, StringComparison.Ordinal) && string.Equals(usage.ChunkerVersion, ContextEngineVersions.ChunkerVersion, StringComparison.Ordinal));
        try
        {
            return current && roots.Contains(IndexIdentity.NormalizeRoot(usage.RootPath));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
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

                    // The first full sweep of a root's new index completed: its previous index has served its purpose.
                    await CloseRetiredAsync(Key(root.Path), stopping).ConfigureAwait(false);
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
            var key = Key(reader.Path);
            if (reader.Store.GetLiveWriter() is not null)
            {
                // The owner is alive. While this host serves the root's previous index (issue #104), the owner's progress on
                // the new index is read from its state.json: the in-memory state is the snapshot taken at open, and the
                // previous index must close here too once the owner has completed the first full sweep.
                bool retiring;
                lock (_gate)
                {
                    retiring = _retiring.ContainsKey(key);
                }

                if (retiring && !IsComplete(reader) && IndexStateFile.Read(reader.Store.Folder.StateFile)?.LastFullSweepUtc is { } swept)
                {
                    reader.OwnerSweepUtc = swept;
                    LogOwnerCompleted(_logger, reader.Store.Identity.Id, reader.Path);
                    await CloseRetiredAsync(key, stopping).ConfigureAwait(false);
                }

                continue;
            }

            // The owner is gone: open again, which reclaims the stale lock; this host becomes the writer and sweeps.
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

                // Reopened with the state the owner left behind: a completed sweep lets the previous index close now.
                await CloseRetiredAsync(key, stopping).ConfigureAwait(false);
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

            IndexRootStatus Status(RootIndex r) => new()
            {
                RootPath = r.Path,
                IndexId = r.Store.Identity.Id,
                Role = r.ContractRole,
                IndexedFileCount = (int)Math.Min(int.MaxValue, r.IndexedFileCount),
                TotalFileCount = Math.Max(0, r.TotalFileCount),
                ExcludedFileCount = r.Excluded.Count,
                SecretFileCount = r.SecretFileCount,
                ChunkCount = r.ChunkCount,
                RedactedChunkCount = r.RedactedChunkCount,
                DiskBytes = withDiskUse ? r.Store.Folder.GetDiskUsageBytes() : 0,
                LastFullSweepUtc = (r.Store.State.LastFullSweepUtc ?? r.OwnerSweepUtc)?.UtcDateTime,
                EmbeddingModelId = r.Model.ModelId,
                ChunkerVersion = r.Store.Identity.ChunkerVersion,
                SchemaVersion = r.Store.Database.SchemaVersion,
            };

            // The current indexes carry the totals; a root's previous index (issue #104) is listed after its current one
            // (as a reader) so the Index window shows both during the switch, without counting the root twice.
            var current = _roots.Values.OrderBy(r => r.Path, StringComparer.OrdinalIgnoreCase).Select(Status).ToArray();
            var roots = _roots.Values.Concat(_retiring.Values)
                .OrderBy(r => r.Path, StringComparer.OrdinalIgnoreCase)
                .ThenBy(r => r.IsRetiring ? 1 : 0)
                .Select(r => r.IsRetiring ? Status(r) : current.First(c => string.Equals(c.IndexId, r.Store.Identity.Id, StringComparison.Ordinal)))
                .ToArray();

            var lastUpdated = _roots.Values.Select(r => r.LastUpdatedUtc).Where(t => t is not null).DefaultIfEmpty(null).Max();
            return new IndexStatus
            {
                IsIndexing = running && !paused,
                IsPaused = paused,
                IndexedFileCount = current.Sum(r => r.IndexedFileCount),
                TotalFileCount = current.Sum(r => r.TotalFileCount),
                LastUpdatedUtc = lastUpdated?.UtcDateTime,
                Phase = phase,
                PauseReason = phase == IndexPhase.Paused ? _pause.Current : IndexPauseReason.None,
                Roots = roots,
                ExcludedSecretFileCount = current.Sum(r => r.SecretFileCount),
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

    [LoggerMessage(EventId = 2970, Level = LogLevel.Information, Message = "Index retention deleted index {IndexId} for {Root}: {Reason} ({Megabytes:F1} MB).")]
    private static partial void LogRetentionDeleted(ILogger logger, string indexId, string root, string reason, double megabytes);

    [LoggerMessage(EventId = 2971, Level = LogLevel.Warning, Message = "Index retention could not delete index {IndexId} for {Root} ({Reason}): {Error}. It is tried again at the next sweep.")]
    private static partial void LogRetentionDeleteFailed(ILogger logger, string indexId, string root, string reason, string error);

    [LoggerMessage(EventId = 2972, Level = LogLevel.Information, Message = "Index retention swept {Indexes} indexes: {Deleted} deleted, {Megabytes:F1} MB reclaimed; the next sweep in {Hours:F0} hours.")]
    private static partial void LogRetentionSwept(ILogger logger, int indexes, int deleted, double megabytes, double hours);

    [LoggerMessage(EventId = 2973, Level = LogLevel.Warning, Message = "Index retention could not list the index folders ({ExceptionType}: {Reason}); it is tried again at the next sweep.")]
    private static partial void LogRetentionFailed(ILogger logger, string exceptionType, string reason);

    [LoggerMessage(EventId = 2974, Level = LogLevel.Information, Message = "Index {IndexId} for {Root} (model {Model}) is now the root's previous index: read-only, serving searches until the new index's first full sweep completes.")]
    private static partial void LogRetiring(ILogger logger, string indexId, string root, string model);

    [LoggerMessage(EventId = 2975, Level = LogLevel.Information, Message = "Index {IndexId} for {Root} (model {Model}) is closed: the root's new index completed its first full sweep. Retention retires the folder by the age rule.")]
    private static partial void LogRetired(ILogger logger, string indexId, string root, string model);

    [LoggerMessage(EventId = 2976, Level = LogLevel.Information, Message = "Index {IndexId} for {Root} (model {Model}) is the root's current index again: the embedding model was switched back.")]
    private static partial void LogReinstated(ILogger logger, string indexId, string root, string model);

    [LoggerMessage(EventId = 2977, Level = LogLevel.Information, Message = "Index {IndexId} for {Root} (model {Model}), complete from an earlier session, serves searches read-only until the root's new index completes its first full sweep.")]
    private static partial void LogPreviousAdopted(ILogger logger, string indexId, string root, string model);

    [LoggerMessage(EventId = 2978, Level = LogLevel.Debug, Message = "No previous index of {Root} could be opened to serve searches during the switch: {Reason}. The new index serves from its first stored batch.")]
    private static partial void LogPreviousNotAdopted(ILogger logger, string root, string reason);

    [LoggerMessage(EventId = 2979, Level = LogLevel.Information, Message = "Index {IndexId} for {Root} (model {Model}) is closed without completing: the model changed again and the root's complete previous index keeps serving.")]
    private static partial void LogDiscarded(ILogger logger, string indexId, string root, string model);

    [LoggerMessage(EventId = 2980, Level = LogLevel.Information, Message = "The owner of index {IndexId} for {Root} completed its first full sweep; this host stops serving the root's previous index.")]
    private static partial void LogOwnerCompleted(ILogger logger, string indexId, string root);

    [LoggerMessage(EventId = 2914, Level = LogLevel.Debug, Message = "The battery saver state could not be read; indexing is not paused for it.")]
    private static partial void LogPowerFailed(ILogger logger, Exception exception);
}
