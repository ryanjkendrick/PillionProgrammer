using System;
using System.Collections.Generic;
using System.Threading;

namespace Pillion.VisualStudio.Core.WorkspaceQueries;

/// <summary>
/// The results a workspace query has found so far: filled by an <see cref="IWorkspaceQueryEngine"/> as it goes (from any
/// thread), and read by <see cref="WorkspaceQueryService"/> when the query ends or its 2 s budget runs out, so a query that
/// is stopped still returns what it found.
/// </summary>
/// <typeparam name="T">The result type (<see cref="Contracts.SourceLocation"/> or <see cref="Contracts.CallSite"/>).</typeparam>
public sealed class WorkspaceQueryResults<T>
    where T : class
{
    private readonly object _gate = new();
    private readonly List<T> _items = [];
    private readonly HashSet<string> _keys = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<T, string> _keyOf;
    private bool _isFound;
    private bool _overflowed;

    /// <summary>Initializes a new instance of the <see cref="WorkspaceQueryResults{T}"/> class.</summary>
    /// <param name="capacity">The most results kept; one more sets <see cref="Overflowed"/>.</param>
    /// <param name="keyOf">The identity of a result (results with the same key are kept once).</param>
    public WorkspaceQueryResults(int capacity, Func<T, string> keyOf)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "The capacity must be positive.");
        }

        Capacity = capacity;
        _keyOf = keyOf ?? throw new ArgumentNullException(nameof(keyOf));
    }

    /// <summary>Gets the most results kept.</summary>
    public int Capacity { get; }

    /// <summary>Gets a value indicating whether the symbol was resolved (set by the engine before it searches).</summary>
    public bool IsFound
    {
        get
        {
            lock (_gate)
            {
                return _isFound;
            }
        }
    }

    /// <summary>Gets a value indicating whether more results were offered than <see cref="Capacity"/>.</summary>
    public bool Overflowed
    {
        get
        {
            lock (_gate)
            {
                return _overflowed;
            }
        }
    }

    /// <summary>Gets how many results are kept.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _items.Count;
            }
        }
    }

    /// <summary>Records that the symbol was resolved.</summary>
    public void MarkFound()
    {
        lock (_gate)
        {
            _isFound = true;
        }
    }

    /// <summary>Adds a result unless one with the same key is already kept.</summary>
    /// <param name="item">The result.</param>
    /// <returns><see langword="false"/> once <see cref="Capacity"/> is reached (the engine may stop searching).</returns>
    public bool TryAdd(T item)
    {
        if (item is null)
        {
            throw new ArgumentNullException(nameof(item));
        }

        var key = _keyOf(item);
        lock (_gate)
        {
            if (_keys.Contains(key))
            {
                return _items.Count < Capacity;
            }

            if (_items.Count >= Capacity)
            {
                _overflowed = true;
                return false;
            }

            _keys.Add(key);
            _items.Add(item);
            return true;
        }
    }

    /// <summary>Returns the results kept so far.</summary>
    /// <returns>A copy of the list, in the order the results were added.</returns>
    public T[] ToArray()
    {
        lock (_gate)
        {
            return _items.ToArray();
        }
    }
}

/// <summary>A token bucket: at most <c>capacity</c> takes at once, refilled at <c>ratePerSecond</c>. Thread-safe.</summary>
public sealed class TokenBucket
{
    private readonly object _gate = new();
    private readonly Func<TimeSpan> _clock;
    private readonly double _capacity;
    private readonly double _ratePerSecond;
    private double _tokens;
    private TimeSpan _last;

    /// <summary>Initializes a new instance of the <see cref="TokenBucket"/> class, full.</summary>
    /// <param name="capacity">The most tokens held (the largest burst).</param>
    /// <param name="ratePerSecond">How many tokens are added a second.</param>
    /// <param name="clock">A monotonic clock.</param>
    public TokenBucket(int capacity, double ratePerSecond, Func<TimeSpan> clock)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "The capacity must be positive.");
        }

        if (ratePerSecond <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ratePerSecond), ratePerSecond, "The rate must be positive.");
        }

        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _capacity = capacity;
        _ratePerSecond = ratePerSecond;
        _tokens = capacity;
        _last = clock();
    }

    /// <summary>Takes a token if one is available.</summary>
    /// <param name="retryAfter">When none is, how long until the next one.</param>
    /// <returns><see langword="true"/> when a token was taken.</returns>
    public bool TryTake(out TimeSpan retryAfter)
    {
        lock (_gate)
        {
            var now = _clock();
            var elapsed = (now - _last).TotalSeconds;
            if (elapsed > 0)
            {
                _tokens = Math.Min(_capacity, _tokens + (elapsed * _ratePerSecond));
                _last = now;
            }

            if (_tokens >= 1)
            {
                _tokens -= 1;
                retryAfter = TimeSpan.Zero;
                return true;
            }

            retryAfter = TimeSpan.FromSeconds((1 - _tokens) / _ratePerSecond);
            return false;
        }
    }
}

/// <summary>Counters of <see cref="WorkspaceQueryService"/>, for the Show Status text, the log and the integration tests.</summary>
public sealed class WorkspaceQueryStatistics
{
    private long _completed;
    private long _timedOut;
    private long _refusedBusy;
    private long _refusedRate;
    private long _refusedDisabled;
    private long _failed;
    private long _uiThreadVisits;
    private long _lastElapsedTicks;

    /// <summary>Gets how many queries ran to the end or to their budget.</summary>
    public long Completed => Interlocked.Read(ref _completed);

    /// <summary>Gets how many queries were stopped by the 2 s budget (counted in <see cref="Completed"/> too).</summary>
    public long TimedOut => Interlocked.Read(ref _timedOut);

    /// <summary>Gets how many calls were refused because another query was in flight.</summary>
    public long RefusedBusy => Interlocked.Read(ref _refusedBusy);

    /// <summary>Gets how many calls were refused by the rate limit.</summary>
    public long RefusedRate => Interlocked.Read(ref _refusedRate);

    /// <summary>Gets how many calls were refused because <c>features.workspaceQueries</c> is off.</summary>
    public long RefusedDisabled => Interlocked.Read(ref _refusedDisabled);

    /// <summary>Gets how many queries failed with an error.</summary>
    public long Failed => Interlocked.Read(ref _failed);

    /// <summary>
    /// Gets how many times the query path found itself on Visual Studio's UI thread. Always 0 unless a bug brought a query
    /// there; the integration test asserts it.
    /// </summary>
    public long UiThreadVisits => Interlocked.Read(ref _uiThreadVisits);

    /// <summary>Gets how long the last query took, from admission to its answer.</summary>
    public TimeSpan LastElapsed => TimeSpan.FromTicks(Interlocked.Read(ref _lastElapsedTicks));

    internal void OnCompleted(TimeSpan elapsed, bool timedOut)
    {
        Interlocked.Increment(ref _completed);
        if (timedOut)
        {
            Interlocked.Increment(ref _timedOut);
        }

        Interlocked.Exchange(ref _lastElapsedTicks, elapsed.Ticks);
    }

    internal void OnRefusedBusy() => Interlocked.Increment(ref _refusedBusy);

    internal void OnRefusedRate() => Interlocked.Increment(ref _refusedRate);

    internal void OnRefusedDisabled() => Interlocked.Increment(ref _refusedDisabled);

    internal void OnFailed() => Interlocked.Increment(ref _failed);

    internal void OnUiThreadVisit() => Interlocked.Increment(ref _uiThreadVisits);
}
