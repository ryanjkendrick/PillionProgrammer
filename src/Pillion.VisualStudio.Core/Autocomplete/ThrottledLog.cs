using System;

namespace Pillion.VisualStudio.Core.Autocomplete;

/// <summary>
/// Logs autocomplete failures (host restarts, <c>PillionRpcException</c>) at most once per interval, so a host that is
/// away does not flood the log on every keystroke. The suppressed count is reported with the next line. Thread-safe.
/// </summary>
public sealed class ThrottledLog
{
    /// <summary>The default interval: one line per minute.</summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromMinutes(1);

    private readonly object _gate = new();
    private readonly PillionLog? _log;
    private readonly Func<DateTimeOffset> _now;
    private readonly TimeSpan _interval;
    private DateTimeOffset? _last;
    private int _suppressed;

    /// <summary>Initializes a new instance of the <see cref="ThrottledLog"/> class.</summary>
    /// <param name="log">The logger, or <see langword="null"/>.</param>
    /// <param name="now">The time source.</param>
    /// <param name="interval">The least time between two lines; defaults to <see cref="DefaultInterval"/>.</param>
    public ThrottledLog(PillionLog? log, Func<DateTimeOffset> now, TimeSpan? interval = null)
    {
        _log = log;
        _now = now ?? throw new ArgumentNullException(nameof(now));
        _interval = interval ?? DefaultInterval;
    }

    /// <summary>Gets the number of lines written (for tests).</summary>
    public int Written { get; private set; }

    /// <summary>Writes a warning unless one was written within the interval.</summary>
    /// <param name="eventName">The event name.</param>
    /// <param name="message">The message.</param>
    /// <param name="fields">The fields.</param>
    /// <returns><see langword="true"/> when the line was written.</returns>
    public bool Warning(string eventName, string message, params (string Key, object? Value)[] fields)
    {
        int suppressed;
        lock (_gate)
        {
            var now = _now();
            if (_last is { } last && now - last < _interval)
            {
                _suppressed++;
                return false;
            }

            _last = now;
            suppressed = _suppressed;
            _suppressed = 0;
            Written++;
        }

        var all = new (string Key, object? Value)[(fields?.Length ?? 0) + 1];
        fields?.CopyTo(all, 0);
        all[all.Length - 1] = ("suppressed", suppressed);
        _log?.Warning(eventName, message, all);
        return true;
    }
}
