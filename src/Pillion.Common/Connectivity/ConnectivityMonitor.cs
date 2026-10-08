using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Pillion.Common.Settings;

namespace Pillion.Common.Connectivity;

/// <summary>
/// Works out the <see cref="ConnectivityState"/> by probing the cloud providers the user has configured.
/// </summary>
/// <remarks>
/// <para>
/// Only enabled cloud providers are monitored (see <see cref="ProviderClassifier"/>); providers that are not in the
/// settings, are disabled or run locally are never probed. A probe is one HTTP request to the endpoint's origin with a
/// 2 second timeout, and any HTTP answer counts as reachable. Rounds run when the monitor starts, every
/// <see cref="ConnectivityMonitorOptions.ProbeInterval"/>, shortly after a network change, when the providers in the
/// settings change, and on <see cref="ProbeNowAsync"/>. Rounds never overlap.
/// </para>
/// <para>
/// The feature flags <see cref="FeatureNames.ConnectivityProbes"/> and <see cref="FeatureNames.CloudProviders"/> switch
/// probing off the same way: the state stays <see cref="ConnectivityState.Online"/>, as with no cloud provider configured.
/// </para>
/// <para>
/// In <see cref="ConnectivityState.LocalOnly"/> (the user's <c>privacy.localOnly</c> or any repository policy with
/// <c>localOnly</c>) nothing is probed, the timers are stopped, in-flight probes are cancelled and their results are
/// discarded. Otherwise the state is <see cref="ConnectivityState.Online"/> when every monitored provider is
/// reachable or there are none (nothing to reach), <see cref="ConnectivityState.Degraded"/> when some are, and
/// <see cref="ConnectivityState.Offline"/> when none is. A provider that has not been probed yet counts as reachable.
/// </para>
/// <para>
/// The class is a plain object so it works with any host: call <see cref="Start"/> and <see cref="Stop"/>, or run
/// <see cref="RunAsync"/> from a <c>BackgroundService</c>. All members are thread-safe and nothing escapes a probe round.
/// </para>
/// </remarks>
public sealed partial class ConnectivityMonitor : IConnectivityMonitor, IDisposable
{
    private static readonly TimeSpan HardCapGrace = TimeSpan.FromMilliseconds(500);

    private readonly Lock _gate = new();
    private readonly object _raiseGate = new();
    private readonly Queue<ConnectivityChangedEventArgs> _pending = new();
    private readonly IConnectivitySettingsProvider _settings;
    private readonly IProviderProber _prober;
    private readonly INetworkChangeSource _network;
    private readonly TimeProvider _time;
    private readonly ConnectivityMonitorOptions _options;
    private readonly ILogger _logger;

    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _localIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _cloudIds = new(StringComparer.OrdinalIgnoreCase);

    private ConnectivityState _state = ConnectivityState.Online;
    private bool _started;
    private bool _disposed;
    private bool _localOnly;
    private string? _fingerprint;
    private ITimer? _periodic;
    private ITimer? _debounce;
    private CancellationTokenSource _cycleCts = new();
    private Task _tail = Task.CompletedTask;

    /// <summary>Initializes a new instance of the <see cref="ConnectivityMonitor"/> class. Call <see cref="Start"/> to begin.</summary>
    /// <param name="settings">Supplies the effective settings and says when they change.</param>
    /// <param name="prober">Probes a provider's origin.</param>
    /// <param name="network">Raises an event when the network changes.</param>
    /// <param name="timeProvider">The clock and timer factory, or <see langword="null"/> for <see cref="TimeProvider.System"/>.</param>
    /// <param name="options">Timing options, or <see langword="null"/> for the defaults.</param>
    /// <param name="logger">An optional logger.</param>
    public ConnectivityMonitor(
        IConnectivitySettingsProvider settings,
        IProviderProber prober,
        INetworkChangeSource network,
        TimeProvider? timeProvider = null,
        ConnectivityMonitorOptions? options = null,
        ILogger<ConnectivityMonitor>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(prober);
        ArgumentNullException.ThrowIfNull(network);

        _settings = settings;
        _prober = prober;
        _network = network;
        _time = timeProvider ?? TimeProvider.System;
        _options = options ?? new ConnectivityMonitorOptions();
        _logger = logger ?? NullLogger<ConnectivityMonitor>.Instance;
    }

    /// <inheritdoc/>
    public event EventHandler<ConnectivityChangedEventArgs>? StateChanged;

    /// <inheritdoc/>
    public ConnectivityState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    /// <inheritdoc/>
    public IReadOnlyDictionary<string, ProviderReachability> Snapshot
    {
        get
        {
            lock (_gate)
            {
                return SnapshotLocked();
            }
        }
    }

    /// <summary>Gets a task that completes when no probe round is running or queued (for tests).</summary>
    /// <returns>A task that completes when the monitor is idle.</returns>
    internal async Task WhenIdleAsync()
    {
        while (true)
        {
            Task tail;
            lock (_gate)
            {
                tail = _tail;
            }

            await tail.ConfigureAwait(false);
            lock (_gate)
            {
                if (ReferenceEquals(tail, _tail))
                {
                    return;
                }
            }
        }
    }

    /// <summary>
    /// Reads the settings, sets the initial state, subscribes to settings and network changes and starts the first
    /// probe round (unless local-only mode is on). Does nothing when already started.
    /// </summary>
    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_started)
            {
                return;
            }

            _started = true;
            if (_cycleCts.IsCancellationRequested)
            {
                _cycleCts = new CancellationTokenSource();
            }
        }

        _settings.Changed += OnSettingsChanged;
        _network.Changed += OnNetworkChanged;
        ApplySettings(force: true);
    }

    /// <summary>Stops the timers, cancels running probes and unsubscribes. Does nothing when not started.</summary>
    public void Stop()
    {
        lock (_gate)
        {
            if (!_started)
            {
                return;
            }

            _started = false;
            DisposeTimersLocked();
            _cycleCts.Cancel();
            _fingerprint = null;
        }

        _settings.Changed -= OnSettingsChanged;
        _network.Changed -= OnNetworkChanged;
    }

    /// <summary>Runs the monitor until <paramref name="stoppingToken"/> is cancelled, then stops it. Suitable for <c>BackgroundService.ExecuteAsync</c>.</summary>
    /// <param name="stoppingToken">Ends the run.</param>
    /// <returns>A task that completes when the monitor has stopped.</returns>
    public async Task RunAsync(CancellationToken stoppingToken)
    {
        Start();
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Stopping.
        }
        finally
        {
            Stop();
        }
    }

    /// <inheritdoc/>
    public bool IsProviderReachable(string providerId)
    {
        ArgumentNullException.ThrowIfNull(providerId);

        lock (_gate)
        {
            if (_localIds.Contains(providerId))
            {
                return true;
            }

            if (_localOnly)
            {
                return false;
            }

            if (_entries.TryGetValue(providerId, out var entry))
            {
                return entry.Reachability.Reachable;
            }

            // A cloud provider without a usable endpoint cannot be probed; do not claim it is down.
            return _cloudIds.Contains(providerId);
        }
    }

    /// <inheritdoc/>
    public async Task ProbeNowAsync(CancellationToken cancellationToken)
    {
        var round = Trigger();
        await round.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public void ReportFailure(string providerId)
    {
        ArgumentNullException.ThrowIfNull(providerId);

        lock (_gate)
        {
            if (!_started || _localOnly || !_entries.TryGetValue(providerId, out var entry))
            {
                return;
            }

            entry.Failures++;
            if (entry.Failures < _options.FailureThreshold || entry.BreakerTripped)
            {
                return;
            }

            entry.BreakerTripped = true;
            var wasReachable = entry.Reachability.Reachable;
            entry.Reachability = entry.Reachability with
            {
                Reachable = false,
                ErrorCategory = ProbeErrorCategory.CircuitBreaker,
                Latency = null,
            };

            if (wasReachable)
            {
                Evaluate($"{entry.Id} unreachable: {ProbeErrorCategory.CircuitBreaker.ToDescription()}");
            }
        }

        RaiseQueued();
    }

    /// <inheritdoc/>
    public void ReportSuccess(string providerId)
    {
        ArgumentNullException.ThrowIfNull(providerId);

        lock (_gate)
        {
            if (!_started || _localOnly || !_entries.TryGetValue(providerId, out var entry))
            {
                return;
            }

            entry.Failures = 0;
            entry.BreakerTripped = false;
            if (entry.Reachability.Reachable)
            {
                return;
            }

            // A real request just succeeded, which is better evidence than the last probe.
            entry.Reachability = entry.Reachability with { Reachable = true, ErrorCategory = ProbeErrorCategory.None };
            Evaluate($"{entry.Id} reachable");
        }

        RaiseQueued();
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        Stop();
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _cycleCts.Dispose();
        }
    }

    private static string Describe(string providerId, ProbeResult result) =>
        result.Reachable
            ? providerId + " reachable"
            : string.Create(CultureInfo.InvariantCulture, $"{providerId} unreachable: {result.ErrorCategory.ToDescription()}");

    private void OnSettingsChanged(object? sender, EventArgs e) => ApplySettings(force: false);

    private void OnNetworkChanged(object? sender, EventArgs e)
    {
        lock (_gate)
        {
            if (!_started || _localOnly)
            {
                return;
            }

            // Restart the countdown: a burst of adapter events becomes one probe round.
            _debounce?.Dispose();
            _debounce = _time.CreateTimer(OnDebounceElapsed, state: null, _options.NetworkChangeDebounce, Timeout.InfiniteTimeSpan);
        }
    }

    private void OnDebounceElapsed(object? state)
    {
        lock (_gate)
        {
            _debounce?.Dispose();
            _debounce = null;
            if (!_started || _localOnly)
            {
                return;
            }
        }

        LogNetworkChanged(_logger);
        _ = Trigger();
    }

    private void OnPeriodicElapsed(object? state)
    {
        lock (_gate)
        {
            // Skip the tick when the previous round is still running; the next tick catches up.
            if (!_started || _localOnly || !_tail.IsCompleted)
            {
                return;
            }
        }

        _ = Trigger();
    }

    /// <summary>Re-reads the settings and applies them. The providers or local-only flag changing starts a probe round.</summary>
    private void ApplySettings(bool force)
    {
        EffectiveSettings effective;
        try
        {
            effective = _settings.GetEffectiveSettings();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LogSettingsUnavailable(_logger, ex);
            if (!force)
            {
                return;
            }

            effective = EffectiveSettings.Resolve(new PillionSettings(), policies: null);
        }

        var localOnly = effective.LocalOnly;

        // Feature flags: with cloudProviders off there is nothing to reach, and with connectivityProbes off nothing is
        // probed. Either way the state is what it is with no cloud provider configured (Online), until a flag changes.
        var cloudEnabled = effective.IsFeatureEnabled(FeatureNames.CloudProviders);
        var probesEnabled = cloudEnabled && effective.IsFeatureEnabled(FeatureNames.ConnectivityProbes);
        var local = new List<string>();
        var cloud = new List<string>();
        var targets = new List<ProbeTarget>();
        foreach (var provider in effective.Settings.Providers)
        {
            if (!provider.Enabled || string.IsNullOrWhiteSpace(provider.Id))
            {
                continue;
            }

            if (ProviderClassifier.IsLocal(provider))
            {
                local.Add(provider.Id);
                continue;
            }

            if (!cloudEnabled)
            {
                continue;
            }

            cloud.Add(provider.Id);
            if (probesEnabled
                && ProviderClassifier.TryGetProbeOrigin(provider, out var origin)
                && !targets.Exists(t => string.Equals(t.ProviderId, provider.Id, StringComparison.OrdinalIgnoreCase)))
            {
                targets.Add(new ProbeTarget(provider.Id, origin));
            }
        }

        var fingerprint = localOnly
            ? "local-only"
            : string.Join('\n', targets.Select(t => t.ProviderId.ToUpperInvariant() + "|" + t.Origin.AbsoluteUri).Order(StringComparer.Ordinal));

        var probe = false;
        lock (_gate)
        {
            if (!_started)
            {
                return;
            }

            ReplaceSet(_localIds, local);
            ReplaceSet(_cloudIds, cloud);
            if (!force && string.Equals(fingerprint, _fingerprint, StringComparison.Ordinal))
            {
                return;
            }

            var cause = localOnly
                ? "local-only mode turned on"
                : _localOnly ? "local-only mode turned off" : "providers changed";
            _fingerprint = fingerprint;
            _localOnly = localOnly;

            if (localOnly)
            {
                // Nothing may be probed: stop the timers, cancel running probes and forget what they learned.
                DisposeTimersLocked();
                _cycleCts.Cancel();
                _cycleCts = new CancellationTokenSource();
                _entries.Clear();
            }
            else
            {
                Reconcile(targets);
                _periodic ??= _time.CreateTimer(OnPeriodicElapsed, state: null, _options.ProbeInterval, _options.ProbeInterval);
                probe = true;
            }

            Evaluate(cause);
        }

        RaiseQueued();
        if (probe)
        {
            _ = Trigger();
        }
    }

    private static void ReplaceSet(HashSet<string> set, List<string> values)
    {
        set.Clear();
        foreach (var value in values)
        {
            set.Add(value);
        }
    }

    /// <summary>Makes the monitored entries match <paramref name="targets"/>, keeping what is known about unchanged providers.</summary>
    private void Reconcile(List<ProbeTarget> targets)
    {
        var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var target in targets)
        {
            wanted.Add(target.ProviderId);
            if (_entries.TryGetValue(target.ProviderId, out var existing) && existing.Origin == target.Origin)
            {
                continue;
            }

            _entries[target.ProviderId] = new Entry(target.ProviderId, target.Origin);
        }

        foreach (var id in _entries.Keys.Where(id => !wanted.Contains(id)).ToList())
        {
            _entries.Remove(id);
        }
    }

    private void DisposeTimersLocked()
    {
        _periodic?.Dispose();
        _periodic = null;
        _debounce?.Dispose();
        _debounce = null;
    }

    /// <summary>Starts a probe round after the one in flight, if any. The returned task never faults.</summary>
    private Task Trigger()
    {
        lock (_gate)
        {
            if (!_started || _localOnly)
            {
                return Task.CompletedTask;
            }

            var previous = _tail;
            var token = _cycleCts.Token;
            _tail = Task.Run(() => RunRoundAsync(previous, token), CancellationToken.None);
            return _tail;
        }
    }

#pragma warning disable CA1031 // A probe round runs on a timer or thread-pool thread; nothing may escape it.
    private async Task RunRoundAsync(Task previous, CancellationToken token)
    {
        try
        {
            await previous.ConfigureAwait(false);

            ProbeTarget[] targets;
            lock (_gate)
            {
                if (!_started || _localOnly || token.IsCancellationRequested)
                {
                    return;
                }

                targets = [.. _entries.Values.Select(e => new ProbeTarget(e.Id, e.Origin))];
            }

            if (targets.Length == 0)
            {
                return;
            }

            // Different providers are probed at the same time, so one slow provider does not delay the others.
            var results = await Task.WhenAll(targets.Select(t => ProbeOneAsync(t, token))).ConfigureAwait(false);
            ApplyResults(targets, results, token);
        }
        catch (Exception ex)
        {
            LogRoundFailed(_logger, ex);
        }
    }

    /// <summary>Probes one provider. Returns <see langword="null"/> when the round was cancelled; never throws.</summary>
    private async Task<ProbeResult?> ProbeOneAsync(ProbeTarget target, CancellationToken token)
    {
        try
        {
            using var timeout = new CancellationTokenSource(_options.ProbeTimeout, _time);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, timeout.Token);
            Task<ProbeResult>? probe = null;
            try
            {
                probe = _prober.ProbeAsync(target, linked.Token);
                var result = await probe
                    .WaitAsync(_options.ProbeTimeout + HardCapGrace, _time, token)
                    .ConfigureAwait(false);
                return result ?? ProbeResult.Failure(ProbeErrorCategory.Other);
            }
            finally
            {
                // WaitAsync registers on the round token after the linked source does, and cancellation callbacks run
                // newest first: when the round is cancelled this method can resume and dispose the linked source (which
                // unregisters it) before its own callback has run, so the prober would never be told. Cancel it here,
                // before disposal, whenever the probe is still running (round cancelled or the hard cap hit).
                if (probe is not { IsCompleted: true })
                {
                    CancelQuietly(linked, target.ProviderId);
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
        {
            return ProbeResult.Failure(ProbeErrorCategory.Timeout);
        }
        catch (Exception ex)
        {
            LogProbeThrew(_logger, ex, target.ProviderId);
            return ProbeResult.Failure(ProbeErrorCategory.Other);
        }
    }

    /// <summary>Cancels a probe's token; a cancellation callback that throws is logged, never propagated.</summary>
    private void CancelQuietly(CancellationTokenSource source, string providerId)
    {
        try
        {
            source.Cancel();
        }
        catch (Exception ex)
        {
            LogProbeThrew(_logger, ex, providerId);
        }
    }
#pragma warning restore CA1031

    private void ApplyResults(ProbeTarget[] targets, ProbeResult?[] results, CancellationToken token)
    {
        lock (_gate)
        {
            if (!_started || _localOnly || token.IsCancellationRequested)
            {
                return;
            }

            var now = _time.GetUtcNow();
            var changes = new List<string>();
            for (var i = 0; i < targets.Length; i++)
            {
                if (results[i] is not { } result
                    || !_entries.TryGetValue(targets[i].ProviderId, out var entry)
                    || entry.Origin != targets[i].Origin)
                {
                    continue;
                }

                var wasReachable = entry.Reachability.Reachable;
                if (result.Reachable)
                {
                    entry.Failures = 0;
                    entry.BreakerTripped = false;
                    entry.Reachability = new ProviderReachability(true, now, ProbeErrorCategory.None, result.Latency);
                }
                else
                {
                    entry.Reachability = new ProviderReachability(false, now, result.ErrorCategory, null);
                }

                LogProbed(_logger, entry.Id, result.Reachable, result.ErrorCategory);
                if (wasReachable != result.Reachable)
                {
                    changes.Add(Describe(entry.Id, result));
                }
            }

            Evaluate(changes.Count > 0 ? string.Join("; ", changes) : "probe round completed");
        }

        RaiseQueued();
    }

    /// <summary>Recomputes the state from the entries; queues a <see cref="StateChanged"/> event when it differs. Caller holds the lock.</summary>
    private void Evaluate(string reason)
    {
        ConnectivityState next;
        if (_localOnly)
        {
            next = ConnectivityState.LocalOnly;
        }
        else
        {
            var reachable = _entries.Values.Count(e => e.Reachability.Reachable);
            next = _entries.Count == 0 || reachable == _entries.Count
                ? ConnectivityState.Online
                : reachable == 0 ? ConnectivityState.Offline : ConnectivityState.Degraded;
        }

        if (next == _state)
        {
            return;
        }

        var previous = _state;
        _state = next;
        _pending.Enqueue(new ConnectivityChangedEventArgs(previous, next, reason, SnapshotLocked()));
        LogStateChanged(_logger, previous, next, reason);
    }

    private Dictionary<string, ProviderReachability> SnapshotLocked()
    {
        var snapshot = new Dictionary<string, ProviderReachability>(_entries.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var entry in _entries.Values)
        {
            snapshot[entry.Id] = entry.Reachability;
        }

        return snapshot;
    }

    /// <summary>Raises the queued events in order, outside the state lock.</summary>
    private void RaiseQueued()
    {
        lock (_raiseGate)
        {
            while (true)
            {
                ConnectivityChangedEventArgs? args;
                lock (_gate)
                {
                    if (!_pending.TryDequeue(out args))
                    {
                        return;
                    }
                }

                foreach (var handler in (StateChanged?.GetInvocationList() ?? []).Cast<EventHandler<ConnectivityChangedEventArgs>>())
                {
                    try
                    {
                        handler(this, args);
                    }
                    catch (Exception ex)
                    {
                        LogHandlerThrew(_logger, ex);
                    }
                }
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Connectivity {Previous} -> {Current}: {Reason}")]
    private static partial void LogStateChanged(ILogger logger, ConnectivityState previous, ConnectivityState current, string reason);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Probed provider {ProviderId}: reachable={Reachable} ({Category}).")]
    private static partial void LogProbed(ILogger logger, string providerId, bool reachable, ProbeErrorCategory category);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Network change detected; probing providers.")]
    private static partial void LogNetworkChanged(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The settings could not be read for the connectivity monitor; keeping the previous configuration.")]
    private static partial void LogSettingsUnavailable(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The prober threw while probing provider {ProviderId}; counting it as unreachable.")]
    private static partial void LogProbeThrew(ILogger logger, Exception exception, string providerId);

    [LoggerMessage(Level = LogLevel.Error, Message = "A probe round failed unexpectedly.")]
    private static partial void LogRoundFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "A connectivity StateChanged handler threw an exception.")]
    private static partial void LogHandlerThrew(ILogger logger, Exception exception);

    private sealed class Entry(string id, Uri origin)
    {
        public string Id { get; } = id;

        public Uri Origin { get; } = origin;

        public ProviderReachability Reachability { get; set; } = ProviderReachability.Unknown;

        public int Failures { get; set; }

        public bool BreakerTripped { get; set; }
    }
}
