using Microsoft.Extensions.Logging;
using Pillion.Indexing.Discovery;
using Pillion.Indexing.Roots;

namespace Pillion.Host.Indexing;

/// <summary>
/// Watches one repository root this host owns (work item 2.10, design section 7 step 7, section 10: the writer runs the
/// indexer and the watcher). A recursive <see cref="FileSystemWatcher"/> on the root feeds a <see cref="ChangeQueue"/>
/// (500 ms debounce per path, 1 s coalescing window) after the discovery rules have filtered each event by name
/// (<see cref="PathClassifier.ClassifyByName"/>), so events under <c>bin</c>, <c>obj</c>, <c>.git</c> and every excluded
/// path cost one string check and nothing more. A second watcher on the real git directory (through the <c>.git</c> file of
/// a worktree or submodule) sees <c>HEAD</c> change and <c>index.lock</c> come and go: when git is done and the checked-out
/// commit moved (a branch switch, pull, reset or rebase) the root gets a hash sweep, never a rebuild, and the file events
/// of the checkout are dropped (the sweep sees them all).
/// </summary>
/// <remarks>
/// <para>
/// A buffer overflow schedules a sweep. Any other watcher error (the root went away, the watcher handle failed) never
/// takes the host down: the watcher is closed, a sweep is scheduled, and every
/// <see cref="IndexingOptions.WatcherFallbackInterval"/> (5 minutes) the watcher is started again or, while that fails,
/// another sweep runs. A change to a <c>.gitignore</c> or <c>.pillionignore</c> forgets the cached ignore rules and asks for
/// a sweep, since it can change decisions anywhere below it.
/// </para>
/// <para>
/// The watcher only hands batches over; the indexing service queues them per root while indexing is paused and applies
/// them, coalesced, when it resumes. Disposing it (the root closed, the host lost the writer lock, the workspace closed)
/// stops everything. Event ids 2960-2968.
/// </para>
/// </remarks>
internal sealed partial class RootWatcher : IDisposable
{
    private readonly string _root;
    private readonly string? _gitDirectory;
    private readonly Func<PathClassifier> _classifier;
    private readonly IndexingOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly Action<IReadOnlyList<string>> _changes;
    private readonly Action<string> _sweep;
    private readonly ChangeQueue _queue;
    private readonly Lock _gate = new();
    private FileSystemWatcher? _tree;
    private FileSystemWatcher? _git;
    private ITimer? _flushTimer;
    private ITimer? _gitTimer;
    private ITimer? _fallbackTimer;
    private long _flushDue;
    private string? _head;
    private long _gitBusySince;
    private bool _disposed;
    private bool _broken;
    private long _eventsSeen;
    private long _eventsQueued;
    private int _batches;

    /// <summary>Initializes a new instance of the <see cref="RootWatcher"/> class. Call <see cref="Start"/> next.</summary>
    /// <param name="root">The repository root (full path).</param>
    /// <param name="gitDirectory">The real git directory, or <see langword="null"/> for a folder without git.</param>
    /// <param name="classifier">Gives the current discovery rules (they change with <c>indexing.exclude</c>).</param>
    /// <param name="options">The debounce, window, buffer and fallback settings.</param>
    /// <param name="time">The clock and timers.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="changes">Receives each batch of changed paths (relative, <c>/</c>-separated), on a timer thread.</param>
    /// <param name="sweep">Asked for a full hash sweep, with the reason, on any thread.</param>
    public RootWatcher(string root, string? gitDirectory, Func<PathClassifier> classifier, IndexingOptions options, TimeProvider time, ILogger logger, Action<IReadOnlyList<string>> changes, Action<string> sweep)
    {
        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        _gitDirectory = gitDirectory;
        _classifier = classifier;
        _options = options;
        _time = time;
        _logger = logger;
        _changes = changes;
        _sweep = sweep;
        _queue = new ChangeQueue(options.WatcherDebounce, options.WatcherCoalescingWindow, time);
    }

    /// <summary>Gets a value indicating whether the watcher failed and the root is swept periodically instead.</summary>
    public bool IsBroken => Volatile.Read(ref _broken);

    /// <summary>Gets the file-system events seen on the tree (tests).</summary>
    public long EventsSeen => Interlocked.Read(ref _eventsSeen);

    /// <summary>Gets the events that passed the filter and were queued (tests: excluded paths queue nothing).</summary>
    public long EventsQueued => Interlocked.Read(ref _eventsQueued);

    /// <summary>Gets the batches handed over (tests: a burst coalesces into one).</summary>
    public int Batches => Volatile.Read(ref _batches);

    /// <summary>Gets the paths waiting in the queue.</summary>
    public int Pending => _queue.Count;

    /// <summary>Starts watching. Never throws: a watcher that cannot start is broken from the start (periodic sweeps).</summary>
    public void Start()
    {
        _head = _gitDirectory is null ? null : GitHead.Read(_gitDirectory);
        if (!TryStartWatchers(out var error))
        {
            Break(error!);
        }
    }

    /// <summary>Forgets a path the indexer took directly (a save relayed from Visual Studio), so its own event does not repeat the work.</summary>
    /// <param name="relativePath">The path relative to the root.</param>
    public void Forget(string relativePath) => _queue.Remove(relativePath);

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
        }

        StopWatchers();
        _flushTimer?.Dispose();
        _gitTimer?.Dispose();
        _fallbackTimer?.Dispose();
        _queue.Clear();
    }

    /// <summary>Feeds a watcher error as the watcher would raise it (tests: an overflow, a broken handle).</summary>
    /// <param name="error">The error.</param>
    internal void RaiseError(Exception error) => OnError(this, new ErrorEventArgs(error));

    private static bool IsUnder(string relativePath, string top) =>
        relativePath.Length >= top.Length
        && relativePath.StartsWith(top, StringComparison.OrdinalIgnoreCase)
        && (relativePath.Length == top.Length || relativePath[top.Length] == '/');

    private bool TryStartWatchers(out Exception? error)
    {
        error = null;
        FileSystemWatcher? tree = null;
        FileSystemWatcher? git = null;
        try
        {
            tree = new FileSystemWatcher(_root)
            {
                IncludeSubdirectories = true,
                InternalBufferSize = _options.WatcherBufferBytes,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
            };
            tree.Created += OnTreeEvent;
            tree.Changed += OnTreeEvent;
            tree.Deleted += OnTreeEvent;
            tree.Renamed += OnTreeEvent;
            tree.Error += OnError;

            if (_gitDirectory is not null && Directory.Exists(_gitDirectory))
            {
                git = new FileSystemWatcher(_gitDirectory)
                {
                    IncludeSubdirectories = false,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                };
                git.Filters.Add(GitHead.HeadFileName);
                git.Filters.Add(GitHead.IndexLockFileName);
                git.Created += OnGitEvent;
                git.Changed += OnGitEvent;
                git.Deleted += OnGitEvent;
                git.Renamed += OnGitEvent;
                git.Error += OnError;
            }

            lock (_gate)
            {
                if (_disposed)
                {
                    tree.Dispose();
                    git?.Dispose();
                    return true;
                }

                _tree = tree;
                _git = git;
            }

            tree.EnableRaisingEvents = true;
            if (git is not null)
            {
                git.EnableRaisingEvents = true;
            }

            LogStarted(_logger, _root, git is not null);
            return true;
        }
#pragma warning disable CA1031 // A watcher that cannot start (root gone, handle limit) falls back to periodic sweeps.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            tree?.Dispose();
            git?.Dispose();
            lock (_gate)
            {
                if (ReferenceEquals(_tree, tree))
                {
                    _tree = null;
                    _git = null;
                }
            }

            error = ex;
            return false;
        }
    }

    private void StopWatchers()
    {
        FileSystemWatcher? tree;
        FileSystemWatcher? git;
        lock (_gate)
        {
            tree = _tree;
            git = _git;
            _tree = null;
            _git = null;
        }

        foreach (var watcher in new[] { tree, git })
        {
            if (watcher is null)
            {
                continue;
            }

            try
            {
                watcher.EnableRaisingEvents = false;
                watcher.Dispose();
            }
#pragma warning disable CA1031 // Closing a failed watcher must not throw.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogStopFailed(_logger, ex);
            }
        }
    }

    private void OnTreeEvent(object sender, FileSystemEventArgs e)
    {
        try
        {
            if (e is RenamedEventArgs renamed)
            {
                Consider(renamed.OldFullPath, WatcherChangeTypes.Deleted);
            }

            Consider(e.FullPath, e.ChangeType);
        }
#pragma warning disable CA1031 // An event handler must never take the host down.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogEventFailed(_logger, ex);
        }
    }

    private void Consider(string fullPath, WatcherChangeTypes change)
    {
        if (Volatile.Read(ref _disposed) || fullPath.Length <= _root.Length + 1
            || !fullPath.StartsWith(_root, StringComparison.OrdinalIgnoreCase) || (fullPath[_root.Length] != '\\' && fullPath[_root.Length] != '/'))
        {
            return;
        }

        Interlocked.Increment(ref _eventsSeen);
        var relative = PathClassifier.Normalize(fullPath[(_root.Length + 1)..]);

        // The git directory has its own watcher; nothing under .git is source.
        if (IsUnder(relative, ".git"))
        {
            return;
        }

        var classifier = _classifier();
        if (PathClassifier.IsIgnoreFile(relative))
        {
            var slash = relative.LastIndexOf('/');
            if (slash < 0 || classifier.ClassifyByName(relative[..slash], isDirectory: true).IsIncluded)
            {
                classifier.Invalidate();
                LogIgnoreFileChanged(_logger, relative);
                RequestSweep("an ignore file changed");
            }

            return;
        }

        if (!classifier.ClassifyByName(relative).IsIncluded)
        {
            return;
        }

        // A folder's own "changed" event (its entries changed) says nothing the entries' events do not.
        if (change == WatcherChangeTypes.Changed && Directory.Exists(fullPath))
        {
            return;
        }

        Interlocked.Increment(ref _eventsQueued);
        _queue.Add(relative);
        ScheduleFlush(_options.WatcherDebounce);
    }

    private void ScheduleFlush(TimeSpan due)
    {
        var at = _time.GetTimestamp() + (long)(due.TotalSeconds * _time.TimestampFrequency);
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            // Never push a flush that is already due sooner further away (a steady stream of events would starve it).
            var pending = _flushDue != 0 && _flushDue > _time.GetTimestamp();
            if (pending && _flushDue <= at)
            {
                return;
            }

            _flushDue = at;
            _flushTimer ??= _time.CreateTimer(_ => Flush(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _flushTimer.Change(due < TimeSpan.Zero ? TimeSpan.Zero : due, Timeout.InfiniteTimeSpan);
        }
    }

    private void Flush()
    {
        try
        {
            lock (_gate)
            {
                _flushDue = 0;
                if (_disposed)
                {
                    return;
                }
            }

            // Git is rewriting the tree: hold the events until it is done; a moved HEAD turns them into a sweep.
            if (Interlocked.Read(ref _gitBusySince) != 0)
            {
                ScheduleFlush(_options.GitSettleDelay);
                return;
            }

            var batch = _queue.TakeDue(out var next);
            if (batch is not null)
            {
                Interlocked.Increment(ref _batches);
                LogBatch(_logger, batch.Count);
                _changes(batch);
            }

            if (next is { } wait)
            {
                ScheduleFlush(wait);
            }
        }
#pragma warning disable CA1031 // A timer callback must never take the host down.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogEventFailed(_logger, ex);
        }
    }

    private void OnGitEvent(object sender, FileSystemEventArgs e)
    {
        try
        {
            if (string.Equals(e.Name, GitHead.IndexLockFileName, StringComparison.OrdinalIgnoreCase) && e.ChangeType == WatcherChangeTypes.Created)
            {
                Interlocked.CompareExchange(ref _gitBusySince, _time.GetTimestamp(), 0);
            }

            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _gitTimer ??= _time.CreateTimer(_ => CheckGit(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                _gitTimer.Change(_options.GitSettleDelay, Timeout.InfiniteTimeSpan);
            }
        }
#pragma warning disable CA1031 // An event handler must never take the host down.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogEventFailed(_logger, ex);
        }
    }

    /// <summary>After git's files settle: a moved HEAD means a sweep; otherwise the held events go to the indexer.</summary>
    private void CheckGit()
    {
        try
        {
            if (_gitDirectory is null || Volatile.Read(ref _disposed))
            {
                return;
            }

            var busySince = Interlocked.Read(ref _gitBusySince);
            if (File.Exists(Path.Combine(_gitDirectory, GitHead.IndexLockFileName))
                && (busySince == 0 || _time.GetElapsedTime(busySince) < _options.GitBusyMaxWait))
            {
                Interlocked.CompareExchange(ref _gitBusySince, _time.GetTimestamp(), 0);
                lock (_gate)
                {
                    if (!_disposed)
                    {
                        _gitTimer?.Change(_options.GitSettleDelay, Timeout.InfiniteTimeSpan);
                    }
                }

                return;
            }

            Interlocked.Exchange(ref _gitBusySince, 0);
            var head = GitHead.Read(_gitDirectory);
            if (head is not null && !string.Equals(head, _head, StringComparison.Ordinal))
            {
                _head = head;
                _queue.Clear();
                LogHeadMoved(_logger, _root);
                RequestSweep("the checked-out commit moved");
                return;
            }

            ScheduleFlush(TimeSpan.Zero);
        }
#pragma warning disable CA1031 // A timer callback must never take the host down.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogEventFailed(_logger, ex);
        }
    }

    private void OnError(object sender, ErrorEventArgs e)
    {
        try
        {
            var error = e.GetException();
            if (error is InternalBufferOverflowException)
            {
                // Events were lost: only a sweep knows what changed.
                _queue.Clear();
                LogOverflow(_logger, _root, _options.WatcherBufferBytes);
                RequestSweep("the watcher's buffer overflowed");
                return;
            }

            Break(error);
        }
#pragma warning disable CA1031 // An error handler must never take the host down.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogEventFailed(_logger, ex);
        }
    }

    private void Break(Exception error)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _broken = true;
        }

        LogBroken(_logger, error, _root, _options.WatcherFallbackInterval.TotalMinutes);
        StopWatchers();
        _queue.Clear();
        RequestSweep("the watcher failed");
        lock (_gate)
        {
            if (!_disposed)
            {
                _fallbackTimer ??= _time.CreateTimer(_ => Fallback(), null, _options.WatcherFallbackInterval, _options.WatcherFallbackInterval);
            }
        }
    }

    /// <summary>While broken: start the watcher again, or sweep once more when it still cannot start.</summary>
    private void Fallback()
    {
        try
        {
            if (Volatile.Read(ref _disposed) || !Volatile.Read(ref _broken))
            {
                return;
            }

            if (TryStartWatchers(out _))
            {
                lock (_gate)
                {
                    _broken = false;
                    _fallbackTimer?.Dispose();
                    _fallbackTimer = null;
                }

                LogRestarted(_logger, _root);
                RequestSweep("the watcher started again");
            }
            else
            {
                RequestSweep("periodic sweep while the watcher is broken");
            }
        }
#pragma warning disable CA1031 // A timer callback must never take the host down.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogEventFailed(_logger, ex);
        }
    }

    private void RequestSweep(string reason)
    {
        if (!Volatile.Read(ref _disposed))
        {
            _sweep(reason);
        }
    }

    [LoggerMessage(EventId = 2960, Level = LogLevel.Debug, Message = "Watching {Root} (git directory watched: {Git}).")]
    private static partial void LogStarted(ILogger logger, string root, bool git);

    [LoggerMessage(EventId = 2961, Level = LogLevel.Debug, Message = "The watcher hands {Count} changed paths to the indexer.")]
    private static partial void LogBatch(ILogger logger, int count);

    [LoggerMessage(EventId = 2962, Level = LogLevel.Information, Message = "The checked-out commit of {Root} moved; the root is swept.")]
    private static partial void LogHeadMoved(ILogger logger, string root);

    [LoggerMessage(EventId = 2963, Level = LogLevel.Warning, Message = "The watcher of {Root} overflowed its {Bytes}-byte buffer; the root is swept.")]
    private static partial void LogOverflow(ILogger logger, string root, int bytes);

    [LoggerMessage(EventId = 2964, Level = LogLevel.Warning, Message = "The watcher of {Root} failed; the root is swept now and every {Minutes} minutes until the watcher starts again.")]
    private static partial void LogBroken(ILogger logger, Exception exception, string root, double minutes);

    [LoggerMessage(EventId = 2965, Level = LogLevel.Information, Message = "The watcher of {Root} started again.")]
    private static partial void LogRestarted(ILogger logger, string root);

    [LoggerMessage(EventId = 2966, Level = LogLevel.Information, Message = "The ignore file {Path} changed; the root is swept.")]
    private static partial void LogIgnoreFileChanged(ILogger logger, string path);

    [LoggerMessage(EventId = 2967, Level = LogLevel.Debug, Message = "A watcher event could not be handled.")]
    private static partial void LogEventFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 2968, Level = LogLevel.Debug, Message = "A watcher could not be closed cleanly.")]
    private static partial void LogStopFailed(ILogger logger, Exception exception);
}
