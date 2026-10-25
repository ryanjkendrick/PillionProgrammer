using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Pillion.VisualStudio.Core.Signals;

/// <summary>
/// The platform-neutral half of the IDE signals: receives what the Visual Studio adapters report (<c>Notify*</c>),
/// keeps the build state and the ring buffer, debounces diagnostics changes, raises the <see cref="IIdeSignals"/>
/// events on the thread pool in the order they were reported, and answers the queries through the sources the adapters
/// supply. Contains no Visual Studio types, so it is unit tested with fake sources.
/// </summary>
public sealed class IdeSignalsHub : IIdeSignals, IDisposable
{
    /// <summary>How long the Error List must be quiet before <see cref="DiagnosticsChanged"/> is raised.</summary>
    public static readonly TimeSpan DiagnosticsQuietPeriod = TimeSpan.FromMilliseconds(500);

    /// <summary>The longest <see cref="DiagnosticsChanged"/> is held back while the Error List keeps changing.</summary>
    public static readonly TimeSpan DiagnosticsMaxWait = TimeSpan.FromSeconds(3);

    private const string FilterNote = "Error List entries are only the ones the developer's Error List filters show (its scope, the Build + IntelliSense setting and the severity buttons).";

    private readonly ISupervisorClock _clock;
    private readonly PillionLog? _log;
    private readonly SignalRingBuffer _ring = new();
    private readonly Debouncer _diagnosticsDebouncer;
    private readonly ConcurrentQueue<Action> _queue = new();
    private readonly object _stateGate = new();
    private int _draining;
    private readonly object _connectGate = new();
    private Task? _errorListConnecting;
    private Task? _outputConnecting;
    private BuildActivity _activity = new(false, null);
    private int _projectsBuilt;
    private string? _activeDocument;
    private LastBuildRecord? _lastBuild;

    /// <summary>Initializes a new instance of the <see cref="IdeSignalsHub"/> class.</summary>
    /// <param name="clock">The time source.</param>
    /// <param name="log">The extension log, or <see langword="null"/>.</param>
    public IdeSignalsHub(ISupervisorClock clock, PillionLog? log)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _log = log;
        _diagnosticsDebouncer = new Debouncer(clock, DiagnosticsQuietPeriod, DiagnosticsMaxWait, OnDiagnosticsQuiet);
    }

    /// <inheritdoc/>
    public event EventHandler? BuildStarted;

    /// <inheritdoc/>
    public event EventHandler<BuildFinishedEventArgs>? BuildFinished;

    /// <inheritdoc/>
    public event EventHandler<BuildStateChangedEventArgs>? BuildStateChanged;

    /// <inheritdoc/>
    public event EventHandler<DocumentEventArgs>? DocumentOpened;

    /// <inheritdoc/>
    public event EventHandler<DocumentEventArgs>? DocumentSaved;

    /// <inheritdoc/>
    public event EventHandler<DocumentEventArgs>? DocumentClosed;

    /// <inheritdoc/>
    public event EventHandler<DocumentEventArgs>? ActiveDocumentChanged;

    /// <inheritdoc/>
    public event EventHandler? DiagnosticsChanged;

    /// <summary>Gets or sets the Error List reader; <see langword="null"/> until the adapter has connected the Error List.</summary>
    public IErrorListSource? ErrorList { get; set; }

    /// <summary>
    /// Gets or sets the adapter's Error List connection, run once by <see cref="EnsureDiagnosticsConnectedAsync"/> (and so
    /// by the first <see cref="GetDiagnosticsAsync"/>); it sets <see cref="ErrorList"/>. <see langword="null"/> when the
    /// adapter connects eagerly or has nothing to connect. Issue #49: Visual Studio creates the Error List's table control
    /// on first use, so the connection is made when diagnostics are first asked for, not at package load.
    /// </summary>
    public Func<CancellationToken, Task>? ErrorListConnector { get; set; }

    /// <summary>
    /// Gets or sets the adapter's Output window connection, run once by the first <see cref="GetOutputPaneTextAsync"/>; it
    /// sets <see cref="Output"/>. <see langword="null"/> when the adapter connects eagerly.
    /// </summary>
    public Func<CancellationToken, Task>? OutputConnector { get; set; }

    /// <summary>Gets or sets the Roslyn diagnostics reader; <see langword="null"/> when there is none.</summary>
    public IRoslynDiagnosticsSource? Roslyn { get; set; }

    /// <summary>Gets or sets the Output window reader; <see langword="null"/> until the adapter has started.</summary>
    public IOutputPaneSource? Output { get; set; }

    /// <inheritdoc/>
    public BuildActivity BuildActivity
    {
        get
        {
            lock (_stateGate)
            {
                return _activity;
            }
        }
    }

    /// <inheritdoc/>
    public string? ActiveDocumentPath
    {
        get
        {
            lock (_stateGate)
            {
                return _activeDocument;
            }
        }
    }

    /// <inheritdoc/>
    public string DiagnosticsNote
    {
        get
        {
            var builder = new StringBuilder();
            builder.Append(
                ErrorList is not null ? FilterNote
                : ErrorListConnector is not null ? "The Error List is not connected yet; it connects on the first diagnostics query."
                : "The Error List is not available yet.");
            var roslyn = Roslyn;
            builder.Append(' ');
            builder.Append(
                roslyn is null ? "Roslyn diagnostics are not available."
                : roslyn.UnavailableReason is { } reason ? "Roslyn diagnostics are not available (" + reason + "); the Error List is used for documents."
                : "Roslyn diagnostics are available for open C# and Visual Basic documents.");
            return builder.ToString();
        }
    }

    /// <inheritdoc/>
    public IReadOnlyList<IdeSignalEvent> RecentEvents => _ring.Snapshot();

    /// <summary>Gets the text for <c>Pillion: Show Status</c>: the build state, the diagnostics sources and the latest signals.</summary>
    public string StatusText
    {
        get
        {
            var recent = _ring.Snapshot();
            var builder = new StringBuilder();
            builder.Append("IDE signals: ")
                .Append(BuildActivity.IsBuilding ? "building" : "no build running")
                .Append("; ")
                .Append(recent.Count.ToString(CultureInfo.InvariantCulture))
                .Append(" recent signals. ")
                .Append(DiagnosticsNote);
            foreach (var item in recent.Skip(Math.Max(0, recent.Count - 5)))
            {
                builder.Append(Environment.NewLine).Append("  ").Append(item);
            }

            return builder.ToString();
        }
    }

    /// <summary>The most characters of a pane kept for a mention (<c>@output</c>, <c>@build</c>, <c>@tests</c>).</summary>
    public const int MentionPaneChars = 64 * 1024;

    /// <inheritdoc/>
    public LastBuildRecord? LastBuild
    {
        get
        {
            lock (_stateGate)
            {
                return _lastBuild;
            }
        }
    }

    /// <summary>Reports that a solution build, rebuild or clean began.</summary>
    public void NotifyBuildStarted()
    {
        var now = _clock.UtcNow;
        bool changed;
        lock (_stateGate)
        {
            _projectsBuilt = 0;
            changed = !_activity.IsBuilding;
            _activity = new BuildActivity(true, now);
        }

        Record("BuildStarted", string.Empty);
        Enqueue(() => Raise(BuildStarted, EventArgs.Empty, nameof(BuildStarted)));
        if (changed)
        {
            Enqueue(() => Raise(BuildStateChanged, new BuildStateChangedEventArgs(true), nameof(BuildStateChanged)));
        }
    }

    /// <summary>Reports that one project started building (counted for <see cref="BuildFinishedEventArgs.ProjectCount"/>).</summary>
    public void NotifyProjectBuildStarted()
    {
        lock (_stateGate)
        {
            _projectsBuilt++;
        }
    }

    /// <summary>Reports that the build ended.</summary>
    /// <param name="success">Whether it succeeded.</param>
    public void NotifyBuildFinished(bool success)
    {
        var now = _clock.UtcNow;
        BuildFinishedEventArgs args;
        bool changed;
        lock (_stateGate)
        {
            var duration = _activity.StartedAt is { } started && now > started ? now - started : TimeSpan.Zero;
            args = new BuildFinishedEventArgs(success, _projectsBuilt, duration);
            changed = _activity.IsBuilding;
            _activity = new BuildActivity(false, null);
            _projectsBuilt = 0;
        }

        var record = new LastBuildRecord(success, args.ProjectCount, args.Duration, now, output: null);
        lock (_stateGate)
        {
            _lastBuild = record;
        }

        // Keep the Build pane's tail as it is now, so @build still has it after a debug session or a cleared pane.
        if (Output is not null)
        {
            _ = Task.Run(() => CaptureBuildOutputAsync(record));
        }

        Record("BuildFinished", $"success={success.ToString().ToLowerInvariant()} projects={args.ProjectCount} duration={args.Duration.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)}s");
        Enqueue(() => Raise(BuildFinished, args, nameof(BuildFinished)));
        if (changed)
        {
            Enqueue(() => Raise(BuildStateChanged, new BuildStateChangedEventArgs(false), nameof(BuildStateChanged)));
        }

        // The Error List fills as the build reports; make sure listeners look again once it has settled.
        _diagnosticsDebouncer.Trigger();
    }

    /// <summary>Reports that a document was opened.</summary>
    /// <param name="path">The document's full path.</param>
    public void NotifyDocumentOpened(string path) => NotifyDocument("DocumentOpened", path, () => DocumentOpened);

    /// <summary>Reports that a document was saved.</summary>
    /// <param name="path">The document's full path.</param>
    public void NotifyDocumentSaved(string path) => NotifyDocument("DocumentSaved", path, () => DocumentSaved);

    /// <summary>Reports that a document was closed.</summary>
    /// <param name="path">The document's full path.</param>
    public void NotifyDocumentClosed(string path)
    {
        NotifyDocument("DocumentClosed", path, () => DocumentClosed);
        lock (_stateGate)
        {
            if (PathsEqual(_activeDocument, path))
            {
                _activeDocument = null;
            }
        }
    }

    /// <summary>Reports that the active document changed; repeats of the same path are ignored.</summary>
    /// <param name="path">The new active document's full path, or <see langword="null"/> when none is active.</param>
    public void NotifyActiveDocumentChanged(string? path)
    {
        lock (_stateGate)
        {
            if (PathsEqual(_activeDocument, path))
            {
                return;
            }

            _activeDocument = path;
        }

        Record("ActiveDocumentChanged", path ?? "(none)");
        Enqueue(() => Raise(ActiveDocumentChanged, new DocumentEventArgs(path), nameof(ActiveDocumentChanged)));
    }

    /// <summary>Reports that the Error List (or Roslyn's diagnostics) may have changed; the event is debounced.</summary>
    public void NotifyDiagnosticsMayHaveChanged() => _diagnosticsDebouncer.Trigger();

    /// <summary>Completes when every event reported so far has been raised (for tests).</summary>
    /// <returns>A task.</returns>
    public async Task WhenIdleAsync()
    {
        while (!_queue.IsEmpty || Volatile.Read(ref _draining) != 0)
        {
            await Task.Delay(1).ConfigureAwait(false);
        }
    }

    /// <inheritdoc/>
    public Task EnsureDiagnosticsConnectedAsync(CancellationToken cancellationToken) =>
        EnsureConnectedAsync(ErrorListConnector, () => ErrorList is not null, ref _errorListConnecting, cancellationToken);

    /// <summary>Connects the Output window now, if it is not connected yet (the first pane query does this).</summary>
    /// <param name="cancellationToken">A token that cancels the connection.</param>
    /// <returns>A task that completes when the Output window is connected (or could not be).</returns>
    public Task EnsureOutputConnectedAsync(CancellationToken cancellationToken) =>
        EnsureConnectedAsync(OutputConnector, () => Output is not null, ref _outputConnecting, cancellationToken);

    /// <inheritdoc/>
    public async Task<IReadOnlyList<DiagnosticInfo>> GetDiagnosticsAsync(DiagnosticScope scope, CancellationToken cancellationToken)
    {
        await EnsureDiagnosticsConnectedAsync(cancellationToken).ConfigureAwait(false);
        var errorList = ErrorList;
        var all = errorList is null
            ? new List<DiagnosticInfo>()
            : new List<DiagnosticInfo>(await errorList.ReadAsync(cancellationToken).ConfigureAwait(false));

        if (scope == DiagnosticScope.Solution)
        {
            return Order(all);
        }

        var path = ActiveDocumentPath;
        if (path is null)
        {
            return Array.Empty<DiagnosticInfo>();
        }

        var result = new List<DiagnosticInfo>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (Roslyn is { UnavailableReason: null } roslyn)
        {
            var fromRoslyn = await roslyn.ReadAsync(path, cancellationToken).ConfigureAwait(false);
            if (fromRoslyn is not null)
            {
                foreach (var item in fromRoslyn)
                {
                    if (seen.Add(Key(item)))
                    {
                        result.Add(item);
                    }
                }
            }
        }

        foreach (var item in all.Where(d => d.File is not null && PathsEqual(d.File, path)))
        {
            if (seen.Add(Key(item)))
            {
                result.Add(item);
            }
        }

        return Order(result);
    }

    /// <inheritdoc/>
    public async Task<string?> GetOutputPaneTextAsync(string paneName, int maxChars, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(paneName))
        {
            throw new ArgumentException("A pane name is required.", nameof(paneName));
        }

        if (maxChars < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxChars), maxChars, "The limit must be at least 1.");
        }

        await EnsureOutputConnectedAsync(cancellationToken).ConfigureAwait(false);
        if (Output is not { } output)
        {
            return null;
        }

        var text = await output.ReadTailAsync(paneName.Trim(), maxChars, cancellationToken).ConfigureAwait(false);
        return text is null ? null : OutputTail.Truncate(text, maxChars);
    }

    /// <inheritdoc/>
    public void Dispose() => _diagnosticsDebouncer.Dispose();

    /// <summary>Compares two file paths the way Windows does (case-insensitive, separators and relative parts normalised).</summary>
    /// <param name="a">One path, or <see langword="null"/>.</param>
    /// <param name="b">The other path, or <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when both are the same file.</returns>
    public static bool PathsEqual(string? a, string? b)
    {
        if (a is null || b is null)
        {
            return a is null && b is null;
        }

        return string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
    }

    private static string Key(DiagnosticInfo item) =>
        (item.Code ?? string.Empty) + "|" + item.Line.ToString(CultureInfo.InvariantCulture) + "|" + item.Column.ToString(CultureInfo.InvariantCulture)
        + (item.Code is null ? "|" + item.Message : string.Empty);

    private static List<DiagnosticInfo> Order(List<DiagnosticInfo> items) =>
        items
            .OrderBy(d => d.Severity)
            .ThenBy(d => d.File, StringComparer.OrdinalIgnoreCase)
            .ThenBy(d => d.Line)
            .ThenBy(d => d.Column)
            .ToList();

    private void NotifyDocument(string kind, string path, Func<EventHandler<DocumentEventArgs>?> handler)
    {
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        Record(kind, path);
        Enqueue(() => Raise(handler(), new DocumentEventArgs(path), kind));
    }

    /// <summary>
    /// Runs a connector once: concurrent callers share the one run; a run that ended without connecting (it failed, or
    /// was cancelled) is forgotten, so the next caller tries again. Without a connector there is nothing to do.
    /// </summary>
    private Task EnsureConnectedAsync(Func<CancellationToken, Task>? connector, Func<bool> connected, ref Task? running, CancellationToken cancellationToken)
    {
        if (connector is null || connected())
        {
            return Task.CompletedTask;
        }

        Task run;
        lock (_connectGate)
        {
            if (connected())
            {
                return Task.CompletedTask;
            }

            running ??= Task.Run(() => connector(cancellationToken), CancellationToken.None);
            run = running;
        }

        return WaitForConnectionAsync(run, connected, cancellationToken);
    }

    private async Task WaitForConnectionAsync(Task run, Func<bool> connected, CancellationToken cancellationToken)
    {
        try
        {
            // The connector honours the first caller's token; a later caller that is cancelled stops waiting without
            // cancelling the shared run.
            if (await Task.WhenAny(run, Task.Delay(Timeout.Infinite, cancellationToken)).ConfigureAwait(false) != run)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            await run.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // The adapter logs its own failures; the hub answers without that source and tries again next time.
        // (A run cancelled by the first caller's token lands here for the other waiters; they just retry next time.)
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _log?.Warning("signals.connect.failed", "A lazy connection of the IDE signals failed.", ("error", ex.GetType().Name), ("detail", ex.Message));
        }
        finally
        {
            if (run.IsCompleted && !connected())
            {
                lock (_connectGate)
                {
                    if (ReferenceEquals(_errorListConnecting, run))
                    {
                        _errorListConnecting = null;
                    }

                    if (ReferenceEquals(_outputConnecting, run))
                    {
                        _outputConnecting = null;
                    }
                }
            }
        }
    }

    private void OnDiagnosticsQuiet()
    {
        Record("DiagnosticsChanged", string.Empty);
        Enqueue(() => Raise(DiagnosticsChanged, EventArgs.Empty, nameof(DiagnosticsChanged)));
    }

    private void Record(string kind, string detail) => _ring.Add(new IdeSignalEvent(_clock.UtcNow, kind, detail));

    private async Task CaptureBuildOutputAsync(LastBuildRecord record)
    {
        try
        {
            var text = await GetOutputPaneTextAsync("Build", MentionPaneChars, CancellationToken.None).ConfigureAwait(false);
            lock (_stateGate)
            {
                if (ReferenceEquals(_lastBuild, record))
                {
                    _lastBuild = new LastBuildRecord(record.Success, record.ProjectCount, record.Duration, record.FinishedAt, text ?? string.Empty);
                }
            }
        }
#pragma warning disable CA1031 // The capture is best effort; @build then reads the pane as it is.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _log?.Debug("signals.build.capture.failed", "Keeping the Build pane's tail failed.", ("error", ex.GetType().Name));
        }
    }

    /// <summary>Queues work so events are raised one at a time, in order, on the thread pool.</summary>
    private void Enqueue(Action work)
    {
        _queue.Enqueue(work);
        if (Interlocked.CompareExchange(ref _draining, 1, 0) == 0)
        {
            _ = Task.Run(Drain);
        }
    }

    private void Drain()
    {
        while (true)
        {
            while (_queue.TryDequeue(out var work))
            {
                try
                {
                    work();
                }
#pragma warning disable CA1031 // Listeners are already isolated in Raise; nothing may stop the queue from draining.
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    _log?.Warning("signals.dispatch.failed", "Raising a signal failed.", ("error", ex.GetType().Name), ("detail", ex.Message));
                }
            }

            Volatile.Write(ref _draining, 0);

            // Work queued between the last dequeue and the reset would otherwise wait for the next Enqueue.
            if (_queue.IsEmpty || Interlocked.CompareExchange(ref _draining, 1, 0) != 0)
            {
                return;
            }
        }
    }

    private void Raise(EventHandler? handler, EventArgs args, string name)
    {
        if (handler is null)
        {
            return;
        }

        foreach (var target in handler.GetInvocationList().Cast<EventHandler>())
        {
            try
            {
                target(this, args);
            }
#pragma warning disable CA1031 // A failing listener must not stop the others or the queue.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                _log?.Warning("signals.handler.failed", "A listener of " + name + " threw.", ("error", ex.GetType().Name), ("detail", ex.Message));
            }
        }
    }

    private void Raise<T>(EventHandler<T>? handler, T args, string name)
        where T : EventArgs
    {
        if (handler is null)
        {
            return;
        }

        foreach (var target in handler.GetInvocationList().Cast<EventHandler<T>>())
        {
            try
            {
                target(this, args);
            }
#pragma warning disable CA1031 // A failing listener must not stop the others or the queue.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                _log?.Warning("signals.handler.failed", "A listener of " + name + " threw.", ("error", ex.GetType().Name), ("detail", ex.Message));
            }
        }
    }
}
