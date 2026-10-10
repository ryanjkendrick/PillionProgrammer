namespace Pillion.Host.Tests.TestSupport;

/// <summary>
/// A clock and timer factory that only moves when the test says so, for code that debounces on a timer: the test
/// decides when the window ends, so a slow disk or a starved thread between two steps cannot split a burst.
/// </summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private readonly Lock _lock = new();
    private readonly List<ManualTimer> _timers = [];
    private DateTimeOffset _now = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);

    /// <inheritdoc/>
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

    /// <inheritdoc/>
    public override DateTimeOffset GetUtcNow()
    {
        lock (_lock)
        {
            return _now;
        }
    }

    /// <inheritdoc/>
    public override long GetTimestamp() => GetUtcNow().Ticks;

    /// <inheritdoc/>
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        lock (_lock)
        {
            _timers.Add(timer);
        }

        timer.Change(dueTime, period);
        return timer;
    }

    /// <summary>Moves the clock forward, running the callback of every timer that comes due on the calling thread, in order.</summary>
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
            ManualTimer? next;
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

    private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
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

                Due = dueTime == Timeout.InfiniteTimeSpan ? null : owner._now + dueTime;
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
