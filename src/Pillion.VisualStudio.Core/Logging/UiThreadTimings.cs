using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace Pillion.VisualStudio.Core;

/// <summary>One stretch of Pillion's own work on Visual Studio's UI thread, timed by <see cref="UiThreadTimings"/>.</summary>
public sealed class UiThreadTiming
{
    /// <summary>Initializes a new instance of the <see cref="UiThreadTiming"/> class.</summary>
    /// <param name="name">What ran, for example <c>package.statusbar</c>.</param>
    /// <param name="startTimestamp">When it started, in <see cref="Stopwatch.GetTimestamp"/> ticks.</param>
    /// <param name="endTimestamp">When it ended, in <see cref="Stopwatch.GetTimestamp"/> ticks.</param>
    /// <param name="threadId">The managed thread id it ran on (0 when not recorded).</param>
    public UiThreadTiming(string name, long startTimestamp, long endTimestamp, int threadId = 0)
    {
        Name = name;
        StartTimestamp = startTimestamp;
        EndTimestamp = endTimestamp;
        ThreadId = threadId;
    }

    /// <summary>Gets the managed thread id the stretch ran on (0 when not recorded). Start-up stretches that may run on a pool thread (the part of package initialization before its first switch) are told apart by it.</summary>
    public int ThreadId { get; }

    /// <summary>Gets what ran.</summary>
    public string Name { get; }

    /// <summary>Gets when it started, in <see cref="Stopwatch.GetTimestamp"/> ticks.</summary>
    public long StartTimestamp { get; }

    /// <summary>Gets when it ended, in <see cref="Stopwatch.GetTimestamp"/> ticks.</summary>
    public long EndTimestamp { get; }

    /// <summary>Gets how long it ran (wall-clock: an upper bound of the UI thread's CPU time in it).</summary>
    public TimeSpan Duration => TimeSpan.FromTicks((long)((EndTimestamp - StartTimestamp) * (10_000_000.0 / Stopwatch.Frequency)));
}

/// <summary>
/// A debug switch that times Pillion's own work on Visual Studio's UI thread: every place where Pillion's code runs on
/// that thread (a status bar update, an Output pane flush, a command's status query, the IntelliCode check, ...) wraps
/// its synchronous part in <see cref="Measure"/>. The integration tests switch it on to tell Pillion's share of a UI
/// stall from Visual Studio's; <c>PILLION_UI_THREAD_TIMINGS=1</c> switches it on for a manual investigation.
/// </summary>
/// <remarks>
/// Off by default, when <see cref="Measure"/> costs one static read. When on, a measurement costs two timestamps and one
/// lock-free enqueue on the UI thread; nothing is formatted or written to disk there. The most recent
/// <see cref="Capacity"/> timings are kept.
/// </remarks>
public static class UiThreadTimings
{
    /// <summary>The environment variable that switches the timings on for the whole process (<c>1</c>).</summary>
    public const string Variable = "PILLION_UI_THREAD_TIMINGS";

    /// <summary>How many timings are kept; older ones are dropped.</summary>
    public const int Capacity = 10_000;

    private static readonly ConcurrentQueue<UiThreadTiming> Recorded = new();
    private static int count;
    private static volatile bool enabled = string.Equals(Environment.GetEnvironmentVariable(Variable), "1", StringComparison.Ordinal);

    /// <summary>Gets or sets a value indicating whether timings are recorded.</summary>
    public static bool Enabled
    {
        get => enabled;
        set => enabled = value;
    }

    /// <summary>Starts timing a stretch of work on the UI thread; dispose the result when it ends.</summary>
    /// <param name="name">What runs, as a short dotted name (for example <c>output.flush</c>).</param>
    /// <returns>The measurement (a no-op while the timings are off).</returns>
    public static Scope Measure(string name) => enabled ? new Scope(name, Stopwatch.GetTimestamp(), Environment.CurrentManagedThreadId) : default;

    /// <summary>Returns the timings recorded so far, oldest first.</summary>
    /// <returns>The timings.</returns>
    public static IReadOnlyList<UiThreadTiming> Snapshot() => Recorded.ToArray();

    /// <summary>Forgets every timing recorded so far.</summary>
    public static void Clear()
    {
        while (Recorded.TryDequeue(out _))
        {
            Interlocked.Decrement(ref count);
        }
    }

    private static void Record(UiThreadTiming timing)
    {
        Recorded.Enqueue(timing);
        if (Interlocked.Increment(ref count) > Capacity && Recorded.TryDequeue(out _))
        {
            Interlocked.Decrement(ref count);
        }
    }

    /// <summary>One measurement in progress; <see cref="Dispose"/> records it.</summary>
    public readonly struct Scope : IDisposable
    {
        private readonly string? _name;
        private readonly long _start;
        private readonly int _threadId;

        internal Scope(string name, long start, int threadId)
        {
            _name = name;
            _start = start;
            _threadId = threadId;
        }

        /// <summary>Records the measurement (nothing for a no-op scope).</summary>
        public void Dispose()
        {
            if (_name is not null)
            {
                Record(new UiThreadTiming(_name, _start, Stopwatch.GetTimestamp(), _threadId));
            }
        }
    }
}
