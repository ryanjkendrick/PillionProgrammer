using System;
using System.Threading;
using System.Threading.Tasks;

namespace Pillion.VisualStudio.Core.Autocomplete;

/// <summary>
/// Runs an action at most once per interval however often it is poked: the first poke runs it at once, pokes during
/// the interval are folded into one run at its end, so the last state always gets shown. The status bar indicator uses
/// it so a slow model does not flood the status bar. Thread-safe.
/// </summary>
public sealed class CoalescingNotifier
{
    private readonly object _gate = new();
    private readonly ISupervisorClock _clock;
    private readonly TimeSpan _interval;
    private readonly Action _action;
    private DateTimeOffset? _last;
    private bool _scheduled;

    /// <summary>Initializes a new instance of the <see cref="CoalescingNotifier"/> class.</summary>
    /// <param name="clock">The clock.</param>
    /// <param name="interval">The least time between two runs.</param>
    /// <param name="action">The action; must not block, and runs on the calling thread or a pool thread.</param>
    public CoalescingNotifier(ISupervisorClock clock, TimeSpan interval, Action action)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _interval = interval;
        _action = action ?? throw new ArgumentNullException(nameof(action));
    }

    /// <summary>Asks for the action to run: now when the interval has passed, otherwise once at the end of the interval.</summary>
    public void Poke()
    {
        TimeSpan wait;
        lock (_gate)
        {
            if (_scheduled)
            {
                return;
            }

            var now = _clock.UtcNow;
            if (_last is not { } last || now - last >= _interval)
            {
                _last = now;
                wait = TimeSpan.Zero;
            }
            else
            {
                _scheduled = true;
                wait = last + _interval - now;
            }
        }

        if (wait == TimeSpan.Zero)
        {
            Run();
            return;
        }

        _ = Task.Run(async () =>
        {
            await _clock.DelayAsync(wait, CancellationToken.None).ConfigureAwait(false);
            lock (_gate)
            {
                _scheduled = false;
                _last = _clock.UtcNow;
            }

            Run();
        });
    }

    private void Run()
    {
        try
        {
            _action();
        }
#pragma warning disable CA1031 // A failing notification must not break the pipeline that poked it.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }
}
