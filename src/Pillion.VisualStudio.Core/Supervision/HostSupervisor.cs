using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;
using Pillion.Contracts;

namespace Pillion.VisualStudio.Core;

/// <summary>
/// Keeps one Pillion Host running for this devenv.exe (design doc section 4, "Failure modes"): launches it, mirrors
/// its state, restarts it after a crash or disconnect with a 1 s / 5 s / 30 s backoff, gives up after three crashes in
/// five minutes, and shuts it down with the package. Implements <see cref="IHostConnection"/> for the rest of the
/// extension.
/// </summary>
/// <remarks>
/// All launching, connecting and RPC awaits run on thread-pool threads inside tasks started with the package's
/// <see cref="JoinableTaskFactory"/>; nothing here touches the UI thread, and <see cref="Start"/> returns at once.
/// </remarks>
public sealed class HostSupervisor : IHostConnection, IDisposable
{
    private readonly object _gate = new();
    private readonly IHostLauncher _launcher;
    private readonly ISupervisorClock _clock;
    private readonly JoinableTaskFactory _joinableTaskFactory;
    private readonly PillionLog _log;
    private readonly RestartPolicy _policy;
    private readonly List<TaskCompletionSource<bool>> _readyWaiters = [];

    private HostState _state = HostState.Starting;
    private ConnectivityState? _connectivity;
    private int _restartAttempt;
    private string? _failureReason;
    private IHostSession? _session;
    private TaskCompletionSource<bool>? _hostReportedFailed;
    private CancellationTokenSource? _runCancellation;
    private JoinableTask? _run;
    private bool _disposed;

    /// <summary>Initializes a new instance of the <see cref="HostSupervisor"/> class.</summary>
    /// <param name="launcher">Starts the host.</param>
    /// <param name="joinableTaskFactory">The package's joinable task factory.</param>
    /// <param name="log">The logger.</param>
    /// <param name="clock">The clock; defaults to <see cref="SystemClock.Instance"/>.</param>
    /// <param name="policy">The restart policy; defaults to 1 s / 5 s / 30 s and three crashes in five minutes.</param>
    public HostSupervisor(IHostLauncher launcher, JoinableTaskFactory joinableTaskFactory, PillionLog log, ISupervisorClock? clock = null, RestartPolicy? policy = null)
    {
        _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        _joinableTaskFactory = joinableTaskFactory ?? throw new ArgumentNullException(nameof(joinableTaskFactory));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _clock = clock ?? SystemClock.Instance;
        _policy = policy ?? new RestartPolicy();
    }

    /// <inheritdoc/>
    public event EventHandler<HostStateChangedEventArgs>? StateChanged;

    /// <inheritdoc/>
    public event EventHandler<HostConnectivityChangedEventArgs>? ConnectivityChanged;

    /// <inheritdoc/>
    public event EventHandler<HostSettingsChangedEventArgs>? SettingsChanged;

    /// <inheritdoc/>
    public event EventHandler<HostIndexStatusChangedEventArgs>? IndexStatusChanged;

    /// <inheritdoc/>
    public ConnectivityState? Connectivity
    {
        get
        {
            lock (_gate)
            {
                return _connectivity;
            }
        }
    }

    /// <inheritdoc/>
    public HostState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    /// <summary>Gets the number of crashes in the current window while restarting; otherwise 0.</summary>
    public int RestartAttempt
    {
        get
        {
            lock (_gate)
            {
                return _restartAttempt;
            }
        }
    }

    /// <summary>Gets the crash limit within the window.</summary>
    public int MaxRestartAttempts => _policy.MaxCrashes;

    /// <summary>Gets why the supervisor entered <see cref="HostState.Failed"/>, if it did.</summary>
    public string? FailureReason
    {
        get
        {
            lock (_gate)
            {
                return _failureReason;
            }
        }
    }

    /// <summary>Gets the process id of the connected host, or <see langword="null"/> when not connected.</summary>
    public int? HostProcessId
    {
        get
        {
            lock (_gate)
            {
                return _session?.ProcessId;
            }
        }
    }

    /// <summary>Starts supervising: launches the host in the background. Does nothing if already running.</summary>
    public void Start()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_run is not null)
            {
                return;
            }

            StartRunLocked();
        }
    }

    /// <summary>
    /// The Retry command: forgets past crashes, stops the current host if any and launches a new one.
    /// </summary>
    /// <returns>A task that completes when the new launch has started (not when it is ready).</returns>
    public Task RetryAsync()
    {
        CancellationTokenSource? previousCancellation;
        JoinableTask? previousRun;
        lock (_gate)
        {
            if (_disposed)
            {
                return Task.CompletedTask;
            }

            previousCancellation = _runCancellation;
            previousRun = _run;
            _runCancellation = null;
            _run = null;
        }

        _log.Info("supervisor.retry", "Retry requested; restarting Pillion Host.");

        return _joinableTaskFactory.RunAsync(async () =>
        {
            await TaskScheduler.Default;
            previousCancellation?.Cancel();
            if (previousRun is not null)
            {
                await previousRun;
            }

            previousCancellation?.Dispose();

            lock (_gate)
            {
                if (_disposed || _run is not null)
                {
                    return;
                }

                _policy.Reset();
                StartRunLocked();
            }
        }).Task;
    }

    /// <inheritdoc/>
    public T GetProxy<T>()
        where T : class
    {
        IHostSession? session;
        HostState state;
        lock (_gate)
        {
            ThrowIfDisposed();
            session = _session;
            state = _state;
        }

        if (session is null)
        {
            throw new PillionRpcException(new PillionError
            {
                Code = PillionErrorCode.HostRestarting,
                Message = "Pillion Host is not connected (state: " + state + ").",
                RetryHint = "Wait for IHostConnection.WhenReadyAsync or a StateChanged event, then ask again.",
            });
        }

        try
        {
            return session.GetProxy<T>();
        }
        catch (ObjectDisposedException ex)
        {
            throw new PillionRpcException(
                new PillionError { Code = PillionErrorCode.HostRestarting, Message = "Pillion Host is restarting." },
                ex);
        }
    }

    /// <inheritdoc/>
    public async Task<bool> NotifyAsync(string method, object? argument)
    {
        IHostSession? session;
        lock (_gate)
        {
            session = _disposed || !IsUsableLocked() ? null : _session;
        }

        if (session is null)
        {
            return false;
        }

        try
        {
            return await session.NotifyAsync(method, argument).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            return false; // The session ended between the read above and the send.
        }
    }

    /// <inheritdoc/>
    public async Task WhenReadyAsync(CancellationToken cancellationToken)
    {
        var waiter = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            ThrowIfDisposed();
            if (IsUsableLocked())
            {
                return;
            }

            _readyWaiters.Add(waiter);
        }

        using (cancellationToken.Register(() =>
        {
            lock (_gate)
            {
                _readyWaiters.Remove(waiter);
            }

            waiter.TrySetCanceled(cancellationToken);
        }))
        {
            await waiter.Task.ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Stops supervising and shuts the host down: asks it to exit (<see cref="IHostLifecycleService.ShutdownAsync"/>),
    /// waits up to <paramref name="timeout"/>, then kills the process tree if it is still alive.
    /// </summary>
    /// <param name="timeout">How long to wait for a clean exit (the package uses 2 s).</param>
    /// <returns>A task that completes when the host is gone.</returns>
    public async Task ShutdownAsync(TimeSpan timeout)
    {
        IHostSession? session;
        CancellationTokenSource? cancellation;
        JoinableTask? run;
        List<TaskCompletionSource<bool>> waiters;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            session = _session;
            _session = null;
            cancellation = _runCancellation;
            run = _run;
            _runCancellation = null;
            _run = null;
            waiters = [.. _readyWaiters];
            _readyWaiters.Clear();
        }

        _log.Info("supervisor.shutdown", "Shutting down Pillion Host.", ("pid", session?.ProcessId));
        cancellation?.Cancel();
        foreach (var waiter in waiters)
        {
            waiter.TrySetException(new ObjectDisposedException(nameof(HostSupervisor)));
        }

        if (session is not null)
        {
            await session.ShutdownAsync(timeout).ConfigureAwait(false);
            session.Dispose();
        }

        if (run is not null)
        {
            await run.JoinAsync().ConfigureAwait(false);
        }

        cancellation?.Dispose();
        _log.Info("supervisor.shutdown.complete", "Pillion Host supervisor stopped.");
    }

    /// <summary>Stops supervising and kills the host without asking it to shut down. Prefer <see cref="ShutdownAsync"/>.</summary>
    public void Dispose()
    {
        IHostSession? session;
        CancellationTokenSource? cancellation;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            session = _session;
            _session = null;
            cancellation = _runCancellation;
            _runCancellation = null;
            _run = null;
            foreach (var waiter in _readyWaiters)
            {
                waiter.TrySetException(new ObjectDisposedException(nameof(HostSupervisor)));
            }

            _readyWaiters.Clear();
        }

        cancellation?.Cancel();
        session?.Dispose();
    }

    private static HostState Mirror(HostState hostState) => hostState switch
    {
        HostState.Ready => HostState.Ready,
        HostState.Degraded => HostState.Degraded,
        HostState.Restarting => HostState.Restarting,
        _ => HostState.Starting,
    };

    private void StartRunLocked()
    {
        var cancellation = new CancellationTokenSource();
        _runCancellation = cancellation;
        _run = _joinableTaskFactory.RunAsync(async () =>
        {
            await TaskScheduler.Default;
            await RunAsync(cancellation.Token).ConfigureAwait(false);
        });
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            SetState(HostState.Starting, restartAttempt: 0, reason: null);

            string crash;
            IHostSession? session = null;
            try
            {
                session = await _launcher.LaunchAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (HostLaunchException ex) when (!ex.IsRetryable)
            {
                EnterFailed(ex.Message);
                return;
            }
#pragma warning disable CA1031 // Every other launch failure is a crash for the restart policy.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                session = null;
                crash = ex.Message;
                _log.Warning("supervisor.launch_failed", "Pillion Host failed to start.", ("error", ex.GetType().Name), ("detail", ex.Message));
                if (!await BackOffAsync(crash, cancellationToken).ConfigureAwait(false))
                {
                    return;
                }

                continue;
            }

            var outcome = await SuperviseAsync(session, cancellationToken).ConfigureAwait(false);
            if (outcome is null)
            {
                return;
            }

            if (outcome.Value.HostFailed)
            {
                EnterFailed(outcome.Value.Description + "; it will not be restarted automatically.");
                return;
            }

            if (!await BackOffAsync(outcome.Value.Description, cancellationToken).ConfigureAwait(false))
            {
                return;
            }
        }
    }

    /// <summary>Adopts a session and waits until it ends.</summary>
    /// <returns>The end of the session, or <see langword="null"/> when supervision was cancelled.</returns>
    private async Task<(string Description, bool HostFailed)?> SuperviseAsync(IHostSession session, CancellationToken cancellationToken)
    {
        var hostFailed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            if (_disposed || cancellationToken.IsCancellationRequested)
            {
                session.Dispose();
                return null;
            }

            _session = session;
            _hostReportedFailed = hostFailed;
        }

        session.HostStateChanged += OnSessionHostStateChanged;
        session.ConnectivityChanged += OnSessionConnectivityChanged;
        session.SettingsChanged += OnSessionSettingsChanged;
        session.IndexStatusChanged += OnSessionIndexStatusChanged;
        try
        {
            // The connectivity notification may have arrived during the handshake, before this subscription.
            if (session.LatestConnectivity is { } connectivity)
            {
                OnSessionConnectivityChanged(session, connectivity);
            }

            OnSessionHostStateChanged(session, session.LatestHostState);

            var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (cancellationToken.Register(() => cancelled.TrySetResult(true)))
            {
#pragma warning disable VSTHRD003 // Session completion is driven by thread-pool callbacks; it never needs the main thread.
                var finished = await Task.WhenAny(session.Completion, hostFailed.Task, cancelled.Task).ConfigureAwait(false);
#pragma warning restore VSTHRD003
                if (finished == cancelled.Task)
                {
                    return null;
                }

                if (finished == hostFailed.Task)
                {
                    return ("Pillion Host reported that it has failed", true);
                }

#pragma warning disable VSTHRD003 // Already completed (see above).
                return (await session.Completion.ConfigureAwait(false), false);
#pragma warning restore VSTHRD003
            }
        }
        finally
        {
            session.HostStateChanged -= OnSessionHostStateChanged;
            session.ConnectivityChanged -= OnSessionConnectivityChanged;
            session.SettingsChanged -= OnSessionSettingsChanged;
            session.IndexStatusChanged -= OnSessionIndexStatusChanged;
            SetConnectivity(session, null, force: true);
            var owned = false;
            lock (_gate)
            {
                if (ReferenceEquals(_session, session))
                {
                    _session = null;
                    _hostReportedFailed = null;
                    owned = true;
                }
            }

            // Shutdown takes the session first and closes it cleanly itself; otherwise kill it here.
            if (owned)
            {
                session.Dispose();
            }
        }
    }

    /// <summary>Records a crash and waits for the backoff.</summary>
    /// <returns><see langword="false"/> when the supervisor gave up or was cancelled.</returns>
    private async Task<bool> BackOffAsync(string crash, CancellationToken cancellationToken)
    {
        RestartDecision decision;
        lock (_gate)
        {
            decision = _policy.RecordCrash(_clock.UtcNow);
        }

        if (decision.GiveUp)
        {
            EnterFailed(
                "Pillion Host stopped " + decision.CrashesInWindow + " times within " + _policy.Window.TotalMinutes
                + " minutes, so it will not be restarted automatically. Last error: " + crash.TrimEnd('.') + ".");
            return false;
        }

        _log.Warning(
            "supervisor.restart_scheduled",
            "Pillion Host stopped; restarting after a delay.",
            ("attempt", decision.CrashesInWindow),
            ("max", _policy.MaxCrashes),
            ("delayMs", (int)decision.Delay.TotalMilliseconds),
            ("reason", crash));
        SetState(HostState.Restarting, decision.CrashesInWindow, crash);

        try
        {
            await _clock.DelayAsync(decision.Delay, cancellationToken).ConfigureAwait(false);
            return !cancellationToken.IsCancellationRequested;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private void OnSessionHostStateChanged(object? sender, HostState hostState)
    {
        TaskCompletionSource<bool>? hostFailed;
        lock (_gate)
        {
            if (!ReferenceEquals(sender, _session))
            {
                return;
            }

            hostFailed = _hostReportedFailed;
        }

        if (hostState == HostState.Failed)
        {
            hostFailed?.TrySetResult(true);
            return;
        }

        SetState(Mirror(hostState), restartAttempt: 0, reason: null);
    }

    /// <summary>Forwards the current session's <c>settingsChanged</c> notification (a stale session's is ignored).</summary>
    private void OnSessionSettingsChanged(object? sender, HostSettingsChangedEventArgs e)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(sender, _session))
            {
                return;
            }
        }

        var handlers = SettingsChanged;
        if (handlers is null)
        {
            return;
        }

        foreach (EventHandler<HostSettingsChangedEventArgs> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, e);
            }
#pragma warning disable CA1031 // A faulty subscriber must not break supervision.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                _log.Error("supervisor.settings_handler_failed", "A SettingsChanged handler threw.", ("error", ex.GetType().Name), ("detail", ex.Message));
            }
        }
    }

    /// <summary>Forwards the current session's <c>indexStatusChanged</c> notification (a stale session's is ignored).</summary>
    private void OnSessionIndexStatusChanged(object? sender, HostIndexStatusChangedEventArgs e)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(sender, _session))
            {
                return;
            }
        }

        var handlers = IndexStatusChanged;
        if (handlers is null)
        {
            return;
        }

        foreach (EventHandler<HostIndexStatusChangedEventArgs> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, e);
            }
#pragma warning disable CA1031 // A faulty subscriber must not break supervision.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                _log.Error("supervisor.index_status_handler_failed", "An IndexStatusChanged handler threw.", ("error", ex.GetType().Name), ("detail", ex.Message));
            }
        }
    }

    private void OnSessionConnectivityChanged(object? sender, ConnectivityState connectivity) =>
        SetConnectivity(sender, connectivity, force: false);

    /// <summary>Records the connectivity the current session reported (or forgets it when the session ends) and raises the event.</summary>
    private void SetConnectivity(object? session, ConnectivityState? connectivity, bool force)
    {
        HostConnectivityChangedEventArgs change;
        lock (_gate)
        {
            // Only the current session may report; when a session ends (force) the value is cleared regardless.
            if ((!force && !ReferenceEquals(session, _session)) || _connectivity == connectivity)
            {
                return;
            }

            change = new HostConnectivityChangedEventArgs(_connectivity, connectivity);
            _connectivity = connectivity;
        }

        _log.Info("supervisor.connectivity", "Connectivity changed.", ("from", change.Previous), ("to", change.Current));
        var handlers = ConnectivityChanged;
        if (handlers is null)
        {
            return;
        }

        foreach (EventHandler<HostConnectivityChangedEventArgs> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, change);
            }
#pragma warning disable CA1031 // A faulty subscriber must not break supervision.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                _log.Error("supervisor.connectivity_handler_failed", "A ConnectivityChanged handler threw.", ("error", ex.GetType().Name), ("detail", ex.Message));
            }
        }
    }

    private void EnterFailed(string reason)
    {
        _log.Error("supervisor.failed", reason);
        SetState(HostState.Failed, restartAttempt: 0, reason);
    }

    private void SetState(HostState state, int restartAttempt, string? reason)
    {
        HostStateChangedEventArgs change;
        List<TaskCompletionSource<bool>>? ready = null;
        lock (_gate)
        {
            if (_disposed || (_state == state && _restartAttempt == restartAttempt))
            {
                return;
            }

            change = new HostStateChangedEventArgs(_state, state, restartAttempt, _policy.MaxCrashes, reason);
            _state = state;
            _restartAttempt = restartAttempt;
            _failureReason = state == HostState.Failed ? reason : null;

            if (IsUsableLocked() && _readyWaiters.Count > 0)
            {
                ready = [.. _readyWaiters];
                _readyWaiters.Clear();
            }
        }

        _log.Info(
            "supervisor.state",
            HostStatusText.Format(change),
            ("from", change.PreviousState),
            ("to", change.State),
            ("attempt", restartAttempt));

        if (ready is not null)
        {
            foreach (var waiter in ready)
            {
                waiter.TrySetResult(true);
            }
        }

        var handlers = StateChanged;
        if (handlers is null)
        {
            return;
        }

        foreach (EventHandler<HostStateChangedEventArgs> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, change);
            }
#pragma warning disable CA1031 // A faulty subscriber must not break supervision.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                _log.Error("supervisor.state_handler_failed", "A StateChanged handler threw.", ("error", ex.GetType().Name), ("detail", ex.Message));
            }
        }
    }

    private bool IsUsableLocked() => _session is not null && _state is HostState.Ready or HostState.Degraded;

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(HostSupervisor));
        }
    }
}
