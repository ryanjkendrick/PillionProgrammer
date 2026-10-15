using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Pillion.Contracts;
using StreamJsonRpc;

namespace Pillion.VisualStudio.Core.Signals;

/// <summary>
/// Tells the host what to index and when to hold back (work item 2.9, contract 1.3): calls
/// <see cref="IContextService.SetWorkspaceAsync"/> with the open solution, projects or folder, and sends the
/// <c>ideActivityChanged</c> notification with every IDE activity in progress (builds from <see cref="IIdeSignals"/>, the
/// debugger and later test runs from <see cref="IIdeActivitySource"/>), so the host pauses indexing while any is set; and
/// sends the <c>documentSaved</c> notification for every document saved (work item 2.10), so the host indexes it at once
/// instead of waiting for its file watcher's debounce, and <c>openDocumentsChanged</c> with every open document, so the host
/// indexes those first after a branch switch.
/// </summary>
/// <remarks>
/// <para>
/// When it sends: after every transition of the host to Ready or Degraded (a new handshaken session), both the activity
/// set and the workspace, whatever was sent before; afterwards the activity set whenever it actually differs from what this
/// session last received, a saved document's path as soon as <see cref="IIdeSignals.DocumentSaved"/> reports it (directly,
/// not through the pump: a save must not wait behind a slow workspace call), and the workspace after the workspace source
/// reports a change and has been quiet for
/// <see cref="WorkspaceQuietPeriod"/> (at most <see cref="WorkspaceMaxWait"/> after the first change of a burst), again
/// only when it differs; the open documents the same way (after <see cref="WorkspaceQuietPeriod"/> of quiet, only when the
/// set differs, the documents opened since the relay started). Nothing is sent while the host is not connected; the next
/// connection sends the current state.
/// </para>
/// <para>
/// Everything runs on thread-pool threads, one send at a time and in order (a single pump), so the host always ends up with
/// the latest set. Failures are logged and swallowed: a 1.2 host answers <c>SetWorkspaceAsync</c> with method-not-found
/// (logged once per session) and ignores the notification. File names are never logged, only counts.
/// </para>
/// </remarks>
public sealed class IndexingRelay : IDisposable
{
    /// <summary>The client-to-host notification with the IDE activity set (one positional argument, an <see cref="IdeActivity"/>).</summary>
    public const string IdeActivityChangedMethod = "ideActivityChanged";

    /// <summary>The client-to-host notification for a saved document (one positional argument, its full path; contract 1.3, work item 2.10).</summary>
    public const string DocumentSavedMethod = "documentSaved";

    /// <summary>The client-to-host notification with every open document (one positional argument, a string array of full paths; contract 1.3, work item 2.10).</summary>
    public const string OpenDocumentsChangedMethod = "openDocumentsChanged";

    /// <summary>How long the workspace source must be quiet before the workspace is read and sent again.</summary>
    public static readonly TimeSpan WorkspaceQuietPeriod = TimeSpan.FromMilliseconds(500);

    /// <summary>The longest a workspace change is held back while the source keeps reporting changes.</summary>
    public static readonly TimeSpan WorkspaceMaxWait = TimeSpan.FromSeconds(5);

    /// <summary>How long the host gets to answer <see cref="IContextService.SetWorkspaceAsync"/>.</summary>
    public static readonly TimeSpan SetWorkspaceTimeout = TimeSpan.FromSeconds(30);

    private readonly object _gate = new();
    private readonly IHostConnection _connection;
    private readonly IIdeSignals? _signals;
    private readonly IWorkspaceDescriptionSource _workspace;
    private readonly IIdeActivitySource? _activity;
    private readonly PillionLog? _log;
    private readonly Debouncer _workspaceDebouncer;
    private readonly Debouncer _openDebouncer;
    private readonly HashSet<string> _open = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _disposal = new();
    private readonly CancellationToken _disposalToken;

    private bool _started;
    private bool _disposed;
    private bool _connected;
    private int _generation;
    private bool _running;
    private bool _activityPending;
    private bool _workspacePending;
    private bool _openPending;
    private string[]? _sentOpen;
    private IdeActivity? _sentActivity;
    private WorkspaceDescription? _sentWorkspace;
    private bool _workspaceUnsupported;

    /// <summary>Initializes a new instance of the <see cref="IndexingRelay"/> class. Call <see cref="Start"/> next.</summary>
    /// <param name="connection">The connection to Pillion Host.</param>
    /// <param name="signals">The IDE signals (build state), or <see langword="null"/> when there are none.</param>
    /// <param name="workspace">Reads what is open in Visual Studio.</param>
    /// <param name="activity">Further activity (the debugger), or <see langword="null"/>.</param>
    /// <param name="clock">The time source for the workspace debounce.</param>
    /// <param name="log">The extension log, or <see langword="null"/>.</param>
    public IndexingRelay(IHostConnection connection, IIdeSignals? signals, IWorkspaceDescriptionSource workspace, IIdeActivitySource? activity, ISupervisorClock clock, PillionLog? log)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        _signals = signals;
        _activity = activity;
        _log = log;
        _workspaceDebouncer = new Debouncer(clock ?? throw new ArgumentNullException(nameof(clock)), WorkspaceQuietPeriod, WorkspaceMaxWait, OnWorkspaceQuiet);
        _openDebouncer = new Debouncer(clock, WorkspaceQuietPeriod, WorkspaceMaxWait, () => Schedule(activity: false, workspace: false, open: true));
        _disposalToken = _disposal.Token; // Read once: the token stays usable (cancelled) after the source is disposed.
    }

    /// <summary>Gets every IDE activity in progress now, as the relay reports it.</summary>
    public IdeActivity CurrentActivity =>
        (_signals?.BuildActivity.IsBuilding == true ? IdeActivity.Build : IdeActivity.None) | (_activity?.Current ?? IdeActivity.None);

    /// <summary>Starts following the host and the IDE; sends at once when the host is already connected. Returns at once.</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_started || _disposed)
            {
                return;
            }

            _started = true;
        }

        _connection.StateChanged += OnHostStateChanged;
        if (_signals is not null)
        {
            _signals.BuildStateChanged += OnBuildStateChanged;
            _signals.DocumentSaved += OnDocumentSaved;
            _signals.DocumentOpened += OnDocumentOpened;
            _signals.DocumentClosed += OnDocumentClosed;
        }

        if (_activity is not null)
        {
            _activity.Changed += OnActivityChanged;
        }

        _workspace.Changed += OnWorkspaceChanged;
        if (IsUsable(_connection.State))
        {
            OnConnected();
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        bool started;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _connected = false;
            started = _started;
        }

        if (started)
        {
            _connection.StateChanged -= OnHostStateChanged;
            if (_signals is not null)
            {
                _signals.BuildStateChanged -= OnBuildStateChanged;
                _signals.DocumentSaved -= OnDocumentSaved;
                _signals.DocumentOpened -= OnDocumentOpened;
                _signals.DocumentClosed -= OnDocumentClosed;
            }

            if (_activity is not null)
            {
                _activity.Changed -= OnActivityChanged;
            }

            _workspace.Changed -= OnWorkspaceChanged;
        }

        _workspaceDebouncer.Dispose();
        _openDebouncer.Dispose();
        _disposal.Cancel();
        _disposal.Dispose();
    }

    /// <summary>Completes when nothing is waiting to be sent (for tests).</summary>
    /// <returns>A task.</returns>
    internal async Task WhenIdleAsync()
    {
        while (true)
        {
            lock (_gate)
            {
                if (!_running)
                {
                    return;
                }
            }

            await Task.Delay(1).ConfigureAwait(false);
        }
    }

    private static bool IsUsable(HostState state) => state is HostState.Ready or HostState.Degraded;

    private static WorkspaceDescription Normalize(WorkspaceDescription? workspace) => new()
    {
        SolutionPath = string.IsNullOrEmpty(workspace?.SolutionPath) ? null : workspace!.SolutionPath,
        FolderPath = string.IsNullOrEmpty(workspace?.FolderPath) ? null : workspace!.FolderPath,
        ProjectPaths = (workspace?.ProjectPaths ?? [])
            .Where(p => !string.IsNullOrEmpty(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToArray(),
    };

    private static bool Same(WorkspaceDescription a, WorkspaceDescription b) =>
        string.Equals(a.SolutionPath, b.SolutionPath, StringComparison.OrdinalIgnoreCase)
        && string.Equals(a.FolderPath, b.FolderPath, StringComparison.OrdinalIgnoreCase)
        && a.ProjectPaths.SequenceEqual(b.ProjectPaths, StringComparer.OrdinalIgnoreCase);

    private static string KindOf(WorkspaceDescription workspace) =>
        workspace.SolutionPath is not null ? "solution" : workspace.FolderPath is not null ? "folder" : "none";

    private void OnHostStateChanged(object? sender, HostStateChangedEventArgs e)
    {
        if (IsUsable(e.State) && !IsUsable(e.PreviousState))
        {
            OnConnected();
        }
        else if (!IsUsable(e.State))
        {
            lock (_gate)
            {
                _connected = false;
            }
        }
    }

    /// <summary>A new handshaken session: forget what the previous one received and send everything.</summary>
    private void OnConnected()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _connected = true;
            _generation++;
            _sentActivity = null;
            _sentWorkspace = null;
            _sentOpen = null;
            _workspaceUnsupported = false;
        }

        Schedule(activity: true, workspace: true, open: true);
    }

    private void OnBuildStateChanged(object? sender, BuildStateChangedEventArgs e) => Schedule(activity: true, workspace: false);

    private void OnActivityChanged(object? sender, EventArgs e) => Schedule(activity: true, workspace: false);

    private void OnDocumentOpened(object? sender, DocumentEventArgs e) => ChangeOpen(e.Path, opened: true);

    private void OnDocumentClosed(object? sender, DocumentEventArgs e) => ChangeOpen(e.Path, opened: false);

    private void ChangeOpen(string? path, bool opened)
    {
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        bool connected;
        lock (_gate)
        {
            var changed = opened ? _open.Add(path!) : _open.Remove(path!);
            if (!changed || _disposed)
            {
                return;
            }

            connected = _connected;
        }

        if (connected)
        {
            _openDebouncer.Trigger();
        }
    }

    private void OnDocumentSaved(object? sender, DocumentEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Path))
        {
            return;
        }

        lock (_gate)
        {
            if (_disposed || !_connected)
            {
                return; // The host sweeps what changed when it next opens the index; its watcher sees the file too.
            }
        }

        _ = SendSavedAsync(e.Path!);
    }

    private async Task SendSavedAsync(string path)
    {
        try
        {
            if (!await _connection.NotifyAsync(DocumentSavedMethod, path).ConfigureAwait(false))
            {
                _log?.Debug("indexing.saved.not_sent", "The host is not connected; its file watcher picks the save up.");
            }
        }
#pragma warning disable CA1031 // A failed notification only costs the save its head start; the host's watcher still sees the file.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _log?.Warning("indexing.saved.failed", "Telling the host about a saved document failed.", ("error", ex.GetType().Name), ("detail", ex.Message));
        }
    }

    private void OnWorkspaceChanged(object? sender, EventArgs e)
    {
        lock (_gate)
        {
            if (!_connected)
            {
                return; // The next connection reads the workspace anyway.
            }
        }

        _workspaceDebouncer.Trigger();
    }

    private void OnWorkspaceQuiet() => Schedule(activity: false, workspace: true);

    /// <summary>Notes what needs sending and starts the pump unless it is already running.</summary>
    private void Schedule(bool activity, bool workspace, bool open = false)
    {
        lock (_gate)
        {
            if (_disposed || !_connected)
            {
                return;
            }

            _activityPending |= activity;
            _workspacePending |= workspace;
            _openPending |= open;
            if (_running)
            {
                return;
            }

            _running = true;
        }

        _ = Task.Run(PumpAsync);
    }

    /// <summary>Sends what is pending, one item at a time, until nothing is; the activity set goes first.</summary>
    private async Task PumpAsync()
    {
        while (true)
        {
            bool activity;
            bool workspace;
            bool open;
            int generation;
            lock (_gate)
            {
                activity = _activityPending;
                workspace = _workspacePending;
                open = _openPending;
                _activityPending = false;
                _workspacePending = false;
                _openPending = false;
                if (_disposed || !_connected || (!activity && !workspace && !open))
                {
                    _running = false;
                    return;
                }

                generation = _generation;
            }

            try
            {
                if (activity)
                {
                    await SendActivityAsync(generation).ConfigureAwait(false);
                }

                if (workspace)
                {
                    await SendWorkspaceAsync(generation).ConfigureAwait(false);
                }

                if (open)
                {
                    await SendOpenAsync(generation).ConfigureAwait(false);
                }
            }
#pragma warning disable CA1031 // The pump must keep going; every failure is already reported where it happens.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                _log?.Warning("indexing.relay.failed", "Relaying the IDE state to the host failed.", ("error", ex.GetType().Name), ("detail", ex.Message));
            }
        }
    }

    private async Task SendActivityAsync(int generation)
    {
        var current = CurrentActivity;
        lock (_gate)
        {
            if (generation != _generation || _sentActivity == current)
            {
                return;
            }
        }

        if (await _connection.NotifyAsync(IdeActivityChangedMethod, current).ConfigureAwait(false))
        {
            lock (_gate)
            {
                if (generation == _generation)
                {
                    _sentActivity = current;
                }
            }

            _log?.Debug("indexing.activity.sent", "Told the host what the IDE is busy with.", ("activity", current));
        }
        else
        {
            _log?.Debug("indexing.activity.not_sent", "The host is not connected; the activity set goes with the next connection.", ("activity", current));
        }
    }

    private async Task SendOpenAsync(int generation)
    {
        string[] open;
        lock (_gate)
        {
            open = _open.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray();
            if (generation != _generation || (_sentOpen is { } sent && sent.SequenceEqual(open, StringComparer.OrdinalIgnoreCase)))
            {
                return;
            }
        }

        if (await _connection.NotifyAsync(OpenDocumentsChangedMethod, open).ConfigureAwait(false))
        {
            lock (_gate)
            {
                if (generation == _generation)
                {
                    _sentOpen = open;
                }
            }

            _log?.Debug("indexing.open.sent", "Told the host which documents are open.", ("count", open.Length));
        }
    }

    private async Task SendWorkspaceAsync(int generation)
    {
        lock (_gate)
        {
            if (generation != _generation || _workspaceUnsupported)
            {
                return;
            }
        }

        var token = _disposalToken;
        WorkspaceDescription workspace;
        try
        {
            workspace = Normalize(await _workspace.GetAsync(token).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return;
        }
#pragma warning disable CA1031 // Without a description nothing is sent; the next change or connection tries again.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _log?.Warning("indexing.workspace.read_failed", "Could not read the open solution or folder.", ("error", ex.GetType().Name), ("detail", ex.Message));
            return;
        }

        lock (_gate)
        {
            if (generation != _generation || !_connected || (_sentWorkspace is { } sent && Same(sent, workspace)))
            {
                return;
            }
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(SetWorkspaceTimeout);
            await _connection.GetProxy<IContextService>().SetWorkspaceAsync(workspace, timeout.Token).ConfigureAwait(false);
            lock (_gate)
            {
                if (generation == _generation)
                {
                    _sentWorkspace = workspace;
                }
            }

            _log?.Info("indexing.workspace.sent", "Told the host what is open.", ("kind", KindOf(workspace)), ("projects", workspace.ProjectPaths.Length));
        }
        catch (RemoteMethodNotFoundException)
        {
            lock (_gate)
            {
                if (generation == _generation)
                {
                    _workspaceUnsupported = true;
                }
            }

            _log?.Info("indexing.workspace.unsupported", "The host does not take the workspace (contract 1.2); it will not index from the IDE's solution.");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Shutting down.
        }
        catch (Exception ex) when (ex is PillionRpcException or ConnectionLostException or ObjectDisposedException)
        {
            _log?.Debug("indexing.workspace.not_sent", "The host went away; the workspace goes with the next connection.", ("error", ex.GetType().Name));
        }
#pragma warning disable CA1031 // A failing host call must not break the relay; the next change or connection tries again.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _log?.Warning("indexing.workspace.failed", "The host did not take the workspace.", ("error", ex.GetType().Name), ("detail", ex.Message));
        }
    }
}
