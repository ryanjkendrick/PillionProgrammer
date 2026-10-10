using System;
using System.Threading;
using System.Threading.Tasks;

namespace Pillion.VisualStudio.Core.Signals;

/// <summary>
/// Turns a burst of triggers into one action: the action runs once the triggers have been quiet for the quiet period,
/// but never later than the maximum wait after the first trigger of the burst (so a list that changes without pause,
/// like the Error List during a build, still produces an event). Thread-safe.
/// </summary>
public sealed class Debouncer : IDisposable
{
    private readonly object _gate = new();
    private readonly ISupervisorClock _clock;
    private readonly TimeSpan _quiet;
    private readonly TimeSpan _maxWait;
    private readonly Action _action;
    private CancellationTokenSource? _pending;
    private DateTimeOffset? _firstTrigger;
    private bool _disposed;

    /// <summary>Initializes a new instance of the <see cref="Debouncer"/> class.</summary>
    /// <param name="clock">The time source.</param>
    /// <param name="quiet">How long triggers must stop before the action runs.</param>
    /// <param name="maxWait">The longest the action is held back after the first trigger of a burst.</param>
    /// <param name="action">The action; it runs on a thread-pool thread and must not throw.</param>
    public Debouncer(ISupervisorClock clock, TimeSpan quiet, TimeSpan maxWait, Action action)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _action = action ?? throw new ArgumentNullException(nameof(action));
        _quiet = quiet;
        _maxWait = maxWait < quiet ? quiet : maxWait;
    }

    /// <summary>Notes a trigger: restarts the quiet period.</summary>
    public void Trigger()
    {
        CancellationToken token;
        TimeSpan delay;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            var now = _clock.UtcNow;
            _firstTrigger ??= now;
            var left = _maxWait - (now - _firstTrigger.Value);
            delay = left < _quiet ? (left > TimeSpan.Zero ? left : TimeSpan.Zero) : _quiet;
            _pending?.Cancel();
            _pending?.Dispose();
            _pending = new CancellationTokenSource();
            token = _pending.Token;
        }

        _ = WaitAndRunAsync(delay, token);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _pending?.Cancel();
            _pending?.Dispose();
            _pending = null;
        }
    }

    private async Task WaitAndRunAsync(TimeSpan delay, CancellationToken token)
    {
        try
        {
            await _clock.DelayAsync(delay, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        lock (_gate)
        {
            // A later trigger cancels this token under the same lock, so a cancelled token here means "superseded".
            if (_disposed || token.IsCancellationRequested)
            {
                return;
            }

            _firstTrigger = null;
        }

        _action();
    }
}
