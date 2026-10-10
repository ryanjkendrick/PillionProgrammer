using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;

namespace Pillion.VisualStudio.Tests;

/// <summary>
/// A stand-in for Visual Studio's UI thread: one thread that runs the work posted to it, one item at a time, and times
/// every item. A <see cref="JoinableTaskContext"/> built on it (<see cref="CreateContext"/>) makes
/// <c>JoinableTaskFactory.SwitchToMainThreadAsync</c> land here, so a test can tell how much of the supervisor's work
/// reaches the "UI thread" and for how long it keeps it busy.
/// </summary>
internal sealed class MainThreadPump : IDisposable
{
    private readonly BlockingCollection<Item> _queue = new();
    private readonly Thread _thread;
    private readonly List<TimeSpan> _workDurations = [];
    private readonly List<TimeSpan> _probeLatencies = [];
    private readonly object _gate = new();
    private readonly PumpContext _context;

    public MainThreadPump()
    {
        _context = new PumpContext(this);
        _thread = new Thread(Run) { Name = "Test main thread", IsBackground = true };
        _thread.Start();
    }

    public int ThreadId => _thread.ManagedThreadId;

    /// <summary>Gets the number of items other than probes that ran on the thread.</summary>
    public int WorkItems
    {
        get
        {
            lock (_gate)
            {
                return _workDurations.Count;
            }
        }
    }

    /// <summary>Gets how long the longest item other than a probe kept the thread busy.</summary>
    public TimeSpan LongestWork
    {
        get
        {
            lock (_gate)
            {
                return _workDurations.DefaultIfEmpty(TimeSpan.Zero).Max();
            }
        }
    }

    /// <summary>Gets how long the slowest probe waited from posting to running.</summary>
    public TimeSpan LongestProbeLatency
    {
        get
        {
            lock (_gate)
            {
                return _probeLatencies.DefaultIfEmpty(TimeSpan.Zero).Max();
            }
        }
    }

    public int Probes
    {
        get
        {
            lock (_gate)
            {
                return _probeLatencies.Count;
            }
        }
    }

    public JoinableTaskContext CreateContext() => new(_thread, _context);

    /// <summary>Runs a function on the thread and returns when it has finished.</summary>
    public Task<T> InvokeAsync<T>(Func<T> function)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Add(new Item(
            () =>
            {
                try
                {
                    completion.SetResult(function());
                }
#pragma warning disable CA1031 // Reported through the task.
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    completion.SetException(ex);
                }
            },
            isProbe: true,
            Stopwatch.GetTimestamp()));
        return completion.Task;
    }

    /// <summary>Posts a probe to the thread every <paramref name="interval"/> until cancelled; each records its latency.</summary>
    public Task ProbeAsync(TimeSpan interval, CancellationToken cancellationToken) => Task.Run(
        async () =>
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    var posted = Stopwatch.GetTimestamp();
                    _queue.Add(new Item(() => RecordProbe(posted), isProbe: true, posted));
                    await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
            }
        },
        CancellationToken.None);

    public void Dispose() => _queue.CompleteAdding();

    private static TimeSpan Since(long timestamp) => TimeSpan.FromSeconds((Stopwatch.GetTimestamp() - timestamp) / (double)Stopwatch.Frequency);

    private void RecordProbe(long posted)
    {
        lock (_gate)
        {
            _probeLatencies.Add(Since(posted));
        }
    }

    private void Run()
    {
        SynchronizationContext.SetSynchronizationContext(_context);
        foreach (var item in _queue.GetConsumingEnumerable())
        {
            var started = Stopwatch.GetTimestamp();
            item.Work();
            if (!item.IsProbe)
            {
                lock (_gate)
                {
                    _workDurations.Add(Since(started));
                }
            }
        }
    }

    private sealed class Item(Action work, bool isProbe, long postedAt)
    {
        public Action Work { get; } = work;

        public bool IsProbe { get; } = isProbe;

        public long PostedAt { get; } = postedAt;
    }

    private sealed class PumpContext(MainThreadPump pump) : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) =>
            pump._queue.Add(new Item(() => d(state), isProbe: false, Stopwatch.GetTimestamp()));

        public override void Send(SendOrPostCallback d, object? state)
        {
            if (Environment.CurrentManagedThreadId == pump.ThreadId)
            {
                d(state);
                return;
            }

            using var done = new ManualResetEventSlim();
            Post(
                _ =>
                {
                    d(state);
                    done.Set();
                },
                null);
            done.Wait();
        }
    }
}
