namespace Pillion.Host.Indexing;

/// <summary>
/// The file-system events of one root waiting to become an incremental batch (work item 2.10, design section 7 step 7):
/// a path is ready once it has been quiet for the debounce (500 ms by default), and the ready paths are handed over
/// together when every path in the queue is ready, or when the burst has lasted the coalescing window (1 s) and some are,
/// so a burst of writes (a save-all, a code generator, a branch switch) becomes one batch, and a stream that never stops
/// still flushes once a window. Saves relayed from Visual Studio do not wait here (the indexing service takes them at
/// once and the queue forgets the path). Thread-safe.
/// </summary>
internal sealed class ChangeQueue
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, long> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeSpan _debounce;
    private readonly TimeSpan _window;
    private readonly TimeProvider _time;
    private long _burstStarted;

    /// <summary>Initializes a new instance of the <see cref="ChangeQueue"/> class.</summary>
    /// <param name="debounce">How long a path must be quiet before it is ready.</param>
    /// <param name="window">How long a burst may last before its ready paths are handed over anyway.</param>
    /// <param name="time">The clock.</param>
    public ChangeQueue(TimeSpan debounce, TimeSpan window, TimeProvider time)
    {
        _debounce = debounce;
        _window = window;
        _time = time;
    }

    /// <summary>Gets the number of paths waiting.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _pending.Count;
            }
        }
    }

    /// <summary>Records an event on a path (it is ready after the debounce, counted from this event).</summary>
    /// <param name="relativePath">The path relative to the root.</param>
    public void Add(string relativePath)
    {
        var now = _time.GetTimestamp();
        lock (_gate)
        {
            if (_pending.Count == 0)
            {
                _burstStarted = now;
            }

            _pending[relativePath] = now;
        }
    }

    /// <summary>Forgets one path (a save already took it to the indexer).</summary>
    /// <param name="relativePath">The path relative to the root.</param>
    public void Remove(string relativePath)
    {
        lock (_gate)
        {
            _pending.Remove(relativePath);
        }
    }

    /// <summary>Forgets everything (a sweep will look at the whole tree).</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _pending.Clear();
        }
    }

    /// <summary>Takes the batch that is due now, if any.</summary>
    /// <param name="nextDue">How long until something is due next, or <see langword="null"/> when the queue is empty.</param>
    /// <returns>The paths handed over, or <see langword="null"/> when nothing is due.</returns>
    public IReadOnlyList<string>? TakeDue(out TimeSpan? nextDue)
    {
        var now = _time.GetTimestamp();
        lock (_gate)
        {
            nextDue = null;
            if (_pending.Count == 0)
            {
                return null;
            }

            var ready = new List<string>();
            TimeSpan? soonest = null;
            foreach (var (path, at) in _pending)
            {
                var quiet = _time.GetElapsedTime(at, now);
                if (quiet >= _debounce)
                {
                    ready.Add(path);
                }
                else
                {
                    var wait = _debounce - quiet;
                    soonest = soonest is null || wait < soonest ? wait : soonest;
                }
            }

            var burst = _time.GetElapsedTime(_burstStarted, now);
            var due = ready.Count > 0 && (ready.Count == _pending.Count || burst >= _window);
            if (!due)
            {
                // Some are ready but others are still changing: wait for them, at most until the window closes.
                if (ready.Count > 0)
                {
                    var windowLeft = _window - burst;
                    soonest = soonest is null || windowLeft < soonest ? windowLeft : soonest;
                }

                nextDue = soonest is { } s && s > TimeSpan.Zero ? s : TimeSpan.FromMilliseconds(1);
                return null;
            }

            foreach (var path in ready)
            {
                _pending.Remove(path);
            }

            if (_pending.Count > 0)
            {
                _burstStarted = now;
                nextDue = soonest is { } s && s > TimeSpan.Zero ? s : TimeSpan.FromMilliseconds(1);
            }

            ready.Sort(StringComparer.OrdinalIgnoreCase);
            return ready;
        }
    }

}
