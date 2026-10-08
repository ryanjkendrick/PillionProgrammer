using System.Collections.Concurrent;
using Pillion.Common.Connectivity;
using Pillion.Common.Settings;

namespace Pillion.Common.Tests.Connectivity;

/// <summary>A clock and timer factory that only moves when the test says so.</summary>
internal sealed class FakeTimeProvider : TimeProvider
{
    private readonly object _lock = new();
    private readonly List<FakeTimer> _timers = [];
    private DateTimeOffset _now = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    /// <summary>Gets the number of timers that are not disposed and have a pending expiry.</summary>
    public int ActiveTimerCount
    {
        get
        {
            lock (_lock)
            {
                return _timers.Count(t => !t.Disposed && t.Due is not null);
            }
        }
    }

    public override DateTimeOffset GetUtcNow()
    {
        lock (_lock)
        {
            return _now;
        }
    }

    public override long GetTimestamp() => GetUtcNow().Ticks;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new FakeTimer(this, callback, state);
        lock (_lock)
        {
            _timers.Add(timer);
        }

        timer.Change(dueTime, period);
        return timer;
    }

    /// <summary>Moves the clock forward, firing every timer that comes due, in order.</summary>
    /// <param name="delta">How far to move.</param>
    public void Advance(TimeSpan delta)
    {
        DateTimeOffset target;
        lock (_lock)
        {
            target = _now + delta;
        }

        while (true)
        {
            FakeTimer? next;
            lock (_lock)
            {
                next = _timers
                    .Where(t => !t.Disposed && t.Due is not null && t.Due <= target)
                    .OrderBy(t => t.Due)
                    .FirstOrDefault();
                if (next is null)
                {
                    _now = target;
                    _timers.RemoveAll(t => t.Disposed);
                    return;
                }

                _now = next.Due!.Value;
                next.Due = next.Period > TimeSpan.Zero ? next.Due + next.Period : null;
            }

            next.Fire();
        }
    }

    private DateTimeOffset Now()
    {
        lock (_lock)
        {
            return _now;
        }
    }

    private sealed class FakeTimer(FakeTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        public DateTimeOffset? Due { get; set; }

        public TimeSpan Period { get; private set; }

        public bool Disposed { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner._lock)
            {
                if (Disposed)
                {
                    return false;
                }

                Due = dueTime == Timeout.InfiniteTimeSpan ? null : owner.Now() + dueTime;
                Period = period == Timeout.InfiniteTimeSpan ? TimeSpan.Zero : period;
                return true;
            }
        }

        public void Fire() => callback(state);

        public void Dispose()
        {
            lock (owner._lock)
            {
                Disposed = true;
                Due = null;
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>A prober whose answers the test controls.</summary>
internal sealed class FakeProber : IProviderProber
{
    private readonly ConcurrentDictionary<string, ProbeResult> _results = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<ProbeTarget> _calls = new();

    public Func<ProbeTarget, CancellationToken, Task<ProbeResult>>? Behavior { get; set; }

    public IReadOnlyCollection<ProbeTarget> Calls => _calls;

    public int CallCount => _calls.Count;

    public static ProbeResult Ok { get; } = ProbeResult.Success(TimeSpan.FromMilliseconds(10));

    public void Set(string providerId, ProbeResult result) => _results[providerId] = result;

    public int CallsFor(string providerId) => _calls.Count(c => string.Equals(c.ProviderId, providerId, StringComparison.OrdinalIgnoreCase));

    public Task<ProbeResult> ProbeAsync(ProbeTarget target, CancellationToken cancellationToken)
    {
        _calls.Enqueue(target);
        if (Behavior is { } behavior)
        {
            return behavior(target, cancellationToken);
        }

        return Task.FromResult(_results.TryGetValue(target.ProviderId, out var result) ? result : Ok);
    }
}

/// <summary>Settings the test edits; raises <see cref="Changed"/> after every edit like the real store.</summary>
internal sealed class FakeSettingsProvider : IConnectivitySettingsProvider
{
    private readonly object _lock = new();
    private readonly PillionSettings _settings = new();
    private readonly List<RepositoryPolicy> _policies = [];

    public event EventHandler? Changed;

    public bool Throw { get; set; }

    public EffectiveSettings GetEffectiveSettings()
    {
        lock (_lock)
        {
            return Throw
                ? throw new SettingsValidationException("settings.json is broken")
                : EffectiveSettings.Resolve(_settings, _policies);
        }
    }

    /// <summary>Edits the user settings without raising <see cref="Changed"/> (use before the monitor starts).</summary>
    /// <param name="edit">The edit.</param>
    public void Configure(Action<PillionSettings> edit)
    {
        lock (_lock)
        {
            edit(_settings);
        }
    }

    /// <summary>Edits the user settings and raises <see cref="Changed"/>.</summary>
    /// <param name="edit">The edit.</param>
    public void Update(Action<PillionSettings> edit)
    {
        Configure(edit);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void SetPolicies(params RepositoryPolicy[] policies)
    {
        lock (_lock)
        {
            _policies.Clear();
            _policies.AddRange(policies);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public static ProviderSettings Provider(string id, ProviderKind kind, string endpoint = "", bool enabled = true) => new()
    {
        Id = id,
        Kind = kind,
        DisplayName = id,
        Endpoint = endpoint,
        Enabled = enabled,
    };
}

/// <summary>A network-change source the test raises by hand.</summary>
internal sealed class FakeNetworkSource : INetworkChangeSource
{
    public event EventHandler? Changed;

    public int SubscriberCount => Changed?.GetInvocationList().Length ?? 0;

    public void Raise() => Changed?.Invoke(this, EventArgs.Empty);
}

/// <summary>A monitor wired to fakes, with the events it raised.</summary>
internal sealed class MonitorFixture : IDisposable
{
    public MonitorFixture(bool start = true, Action<FakeSettingsProvider>? configure = null)
    {
        configure?.Invoke(Settings);
        Monitor = new ConnectivityMonitor(Settings, Prober, Network, Time);
        Monitor.StateChanged += (_, e) => Events.Enqueue(e);
        if (start)
        {
            Start();
        }
    }

    public FakeTimeProvider Time { get; } = new();

    public FakeProber Prober { get; } = new();

    public FakeSettingsProvider Settings { get; } = new();

    public FakeNetworkSource Network { get; } = new();

    public ConnectivityMonitor Monitor { get; }

    public ConcurrentQueue<ConnectivityChangedEventArgs> Events { get; } = new();

    public void Start() => Monitor.Start();

    /// <summary>Waits until the monitor has no probe round running or queued.</summary>
    /// <returns>A task that completes when the monitor is idle.</returns>
    public Task IdleAsync() => Monitor.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));

    public async Task ProbeAsync()
    {
        await Monitor.ProbeNowAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
    }

    public string[] EventSummaries() =>
        [.. Events.Select(e => $"{e.OldState}->{e.NewState}: {e.Reason}")];

    public void Dispose() => Monitor.Dispose();

    public static async Task WaitUntilAsync(Func<bool> condition, string because)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Condition was not met: " + because);
            }

            await Task.Delay(5);
        }
    }
}
