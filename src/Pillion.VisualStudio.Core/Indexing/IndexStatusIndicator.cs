using System;
using System.Threading;
using System.Threading.Tasks;
using Pillion.Contracts;
using Pillion.VisualStudio.Core.Autocomplete;

namespace Pillion.VisualStudio.Core;

/// <summary>
/// The text of the status bar's indexing indicator (work item 2.15): a glyph and the percentage while indexing runs, a paused
/// glyph with the reason, a warning glyph after an error, and nothing when the index is idle. It follows the host's
/// <c>indexStatusChanged</c> notification (at most twice a second, folded again here so the UI thread is visited at most that
/// often), starts from one <see cref="IContextService.GetIndexStatusAsync"/> when the host becomes ready, and goes quiet while
/// the host is away or the <c>indexWindow</c> flag is off.
/// </summary>
/// <remarks>The package shows <see cref="Text"/> in the status bar next to the host state; <see cref="Changed"/> is raised on a background thread.</remarks>
public sealed class IndexStatusIndicator : IDisposable
{
    private static readonly TimeSpan MinInterval = TimeSpan.FromMilliseconds(500);

    private readonly IHostConnection _connection;
    private readonly ChatAvailability? _availability;
    private readonly PillionLog? _log;
    private readonly CoalescingNotifier _notifier;
    private readonly object _gate = new();
    private IndexStatus? _status;
    private string _text = string.Empty;
    private string _tooltip = string.Empty;
    private int _generation;
    private bool _started;
    private bool _disposed;

    /// <summary>Initializes a new instance of the <see cref="IndexStatusIndicator"/> class. Call <see cref="Start"/> next.</summary>
    /// <param name="connection">The connection to Pillion Host.</param>
    /// <param name="availability">The feature flags (the <c>indexWindow</c> flag), or <see langword="null"/> for always on.</param>
    /// <param name="log">The logger, or <see langword="null"/>.</param>
    /// <param name="clock">The clock the notifier uses; defaults to the system clock.</param>
    public IndexStatusIndicator(IHostConnection connection, ChatAvailability? availability, PillionLog? log = null, ISupervisorClock? clock = null)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _availability = availability;
        _log = log;
        _notifier = new CoalescingNotifier(clock ?? SystemClock.Instance, MinInterval, Recompute);
    }

    /// <summary>Raised, on a background thread, when <see cref="Text"/> changed.</summary>
    public event EventHandler? Changed;

    /// <summary>Gets the indicator text, for example "↻ Indexing 42%"; empty when there is nothing to show. Safe to read on any thread.</summary>
    public string Text
    {
        get
        {
            lock (_gate)
            {
                return _text;
            }
        }
    }

    /// <summary>Gets the sentence for the tooltip and for screen readers; empty when <see cref="Text"/> is.</summary>
    public string ToolTip
    {
        get
        {
            lock (_gate)
            {
                return _tooltip;
            }
        }
    }

    /// <summary>Gets the latest status the host reported, or <see langword="null"/>.</summary>
    public IndexStatus? Status
    {
        get
        {
            lock (_gate)
            {
                return _status;
            }
        }
    }

    /// <summary>Starts following the host. Returns at once; the first status is read in the background.</summary>
    public void Start()
    {
        if (_started || _disposed)
        {
            return;
        }

        _started = true;
        _connection.IndexStatusChanged += OnStatusChanged;
        _connection.StateChanged += OnHostStateChanged;
        if (_availability is not null)
        {
            _availability.Changed += OnAvailabilityChanged;
        }

        if (_connection.State is HostState.Ready or HostState.Degraded)
        {
            _ = ReadAsync();
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_started)
        {
            _connection.IndexStatusChanged -= OnStatusChanged;
            _connection.StateChanged -= OnHostStateChanged;
            if (_availability is not null)
            {
                _availability.Changed -= OnAvailabilityChanged;
            }
        }
    }

    private void OnStatusChanged(object? sender, HostIndexStatusChangedEventArgs e)
    {
        Interlocked.Increment(ref _generation);
        lock (_gate)
        {
            _status = e.Status;
        }

        _notifier.Poke();
    }

    private void OnHostStateChanged(object? sender, HostStateChangedEventArgs e)
    {
        if (e.State is HostState.Ready or HostState.Degraded)
        {
            if (e.PreviousState is not (HostState.Ready or HostState.Degraded))
            {
                _ = ReadAsync();
            }
        }
        else
        {
            // The numbers are stale while the host is away.
            lock (_gate)
            {
                _status = null;
            }

            _notifier.Poke();
        }
    }

    private void OnAvailabilityChanged(object? sender, EventArgs e) => _notifier.Poke();

    /// <summary>Reads the status once when the host becomes ready (a notification that arrives meanwhile wins).</summary>
    private async Task ReadAsync()
    {
        var generation = Volatile.Read(ref _generation);
        try
        {
            await Task.Yield();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var status = await _connection.GetProxy<IContextService>().GetIndexStatusAsync(timeout.Token).ConfigureAwait(false);
            if (generation != Volatile.Read(ref _generation) || _disposed)
            {
                return;
            }

            lock (_gate)
            {
                _status = status;
            }

            _notifier.Poke();
        }
#pragma warning disable CA1031 // A host that cannot answer (a 1.2 host, a stub) just means no indicator.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _log?.Debug("index.statusbar.read.failed", "Could not read the index status for the status bar.", ("error", ex.GetType().Name));
        }
    }

    private void Recompute()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            RecomputeCore();
        }
#pragma warning disable CA1031 // Runs on a timer or notification thread: a failure must never take Visual Studio down.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _log?.Warning("index.statusbar.recompute.failed", "The indexing indicator could not be updated.", ("error", ex.GetType().Name));
        }
    }

    private void RecomputeCore()
    {
        string text;
        string tooltip;
        lock (_gate)
        {
            var enabled = _availability?.IsIndexWindowEnabled ?? true;
            var hostUp = _connection.State is HostState.Ready or HostState.Degraded;
            text = enabled && hostUp ? IndexFormat.StatusBarText(_status) : string.Empty;
            tooltip = text.Length == 0
                ? string.Empty
                : IndexStrings.StatusBarOpenName + ". " + IndexFormat.Summary(_status);
            if (string.Equals(text, _text, StringComparison.Ordinal) && string.Equals(tooltip, _tooltip, StringComparison.Ordinal))
            {
                return;
            }

            _text = text;
            _tooltip = tooltip;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }
}
