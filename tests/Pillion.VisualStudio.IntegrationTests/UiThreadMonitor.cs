using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows.Threading;

namespace Pillion.VisualStudio.IntegrationTests;

/// <summary>A dispatcher operation that ran on the UI thread for a while.</summary>
/// <param name="Start">When it started, on the monitor's clock.</param>
/// <param name="Duration">How long it ran (wall-clock).</param>
/// <param name="Priority">Its dispatcher priority.</param>
/// <param name="Description">What it was: the async method or delegate behind it, as far as reflection can tell.</param>
public sealed record UiOperationTiming(TimeSpan Start, TimeSpan Duration, DispatcherPriority Priority, string Description)
{
    /// <summary>Gets when it ended, on the monitor's clock.</summary>
    public TimeSpan End => Start + Duration;

    /// <summary>Gets a value indicating whether Pillion's product code (not this test) ran it.</summary>
    public bool IsPillion => UiThreadMonitor.IsPillionProductText(Description);

    /// <inheritdoc/>
    public override string ToString() => FormattableString.Invariant(
        $"{Start.TotalMilliseconds,6:0} ms +{Duration.TotalMilliseconds,5:0} ms {Priority,-14} {Description}");
}

/// <summary>The UI thread's managed stack, sampled while it had started or finished no dispatcher operation for a while.</summary>
/// <param name="At">When it was taken, on the monitor's clock.</param>
/// <param name="Frames">The managed frames, innermost first (empty when the thread was in native code only).</param>
public sealed record UiStackSample(TimeSpan At, IReadOnlyList<string> Frames)
{
    /// <summary>Gets a value indicating whether a frame of Pillion's product code (not this test) is on the stack.</summary>
    public bool HasPillionFrame => Frames.Any(UiThreadMonitor.IsPillionProductText);
}

/// <summary>What <see cref="UiThreadMonitor"/> saw.</summary>
/// <param name="Operations">The dispatcher operations that ran for at least the monitor's threshold.</param>
/// <param name="Samples">The stack samples taken while the UI thread was stuck.</param>
/// <param name="SampleInterval">The time between two samples of one stall.</param>
public sealed record UiThreadMonitorResult(IReadOnlyList<UiOperationTiming> Operations, IReadOnlyList<UiStackSample> Samples, TimeSpan SampleInterval)
{
    /// <summary>
    /// Estimates how much of an interval Pillion's product code held the UI thread: the overlap of Pillion's dispatcher
    /// operations with it, plus one sample interval for every stack sample in it that has a Pillion frame and falls
    /// outside those operations.
    /// </summary>
    /// <param name="from">The start of the interval, on the monitor's clock.</param>
    /// <param name="to">The end of the interval.</param>
    /// <returns>The estimate.</returns>
    public TimeSpan PillionTimeWithin(TimeSpan from, TimeSpan to)
    {
        var pillionOperations = Operations.Where(o => o.IsPillion).ToList();
        var total = TimeSpan.Zero;
        foreach (var operation in pillionOperations)
        {
            var start = operation.Start > from ? operation.Start : from;
            var end = operation.End < to ? operation.End : to;
            if (end > start)
            {
                total += end - start;
            }
        }

        foreach (var sample in Samples.Where(s => s.At >= from && s.At <= to && s.HasPillionFrame))
        {
            if (!pillionOperations.Any(o => sample.At >= o.Start && sample.At <= o.End))
            {
                total += SampleInterval;
            }
        }

        return total;
    }

    /// <summary>Formats the operations and the stack samples (identical consecutive stacks are folded together).</summary>
    /// <param name="maxFrames">How many innermost frames to print per stack.</param>
    /// <returns>The lines.</returns>
    public IEnumerable<string> Format(int maxFrames = 30)
    {
        yield return Operations.Count == 0
            ? "UI thread dispatcher operations over the threshold: none"
            : "UI thread dispatcher operations over the threshold (start, duration, priority, what):";
        foreach (var operation in Operations)
        {
            yield return "  " + operation + (operation.IsPillion ? "  <-- Pillion" : string.Empty);
        }

        if (Samples.Count == 0)
        {
            yield return "UI thread stack samples: none (the UI thread never went 400 ms without running a dispatcher operation)";
            yield break;
        }

        yield return "UI thread stack samples while it started or finished no dispatcher operation for 400 ms (innermost frame first):";
        for (var i = 0; i < Samples.Count;)
        {
            var first = Samples[i];
            var j = i + 1;
            while (j < Samples.Count && Samples[j].Frames.SequenceEqual(first.Frames))
            {
                j++;
            }

            var last = Samples[j - 1];
            yield return FormattableString.Invariant($"  {first.At.TotalMilliseconds:0}-{last.At.TotalMilliseconds:0} ms ({j - i} samples){(first.HasPillionFrame ? "  <-- Pillion frame" : string.Empty)}:");
            if (first.Frames.Count == 0)
            {
                yield return "    (no managed frames: native code only)";
            }

            foreach (var frame in first.Frames.Take(maxFrames))
            {
                yield return "    " + frame;
            }

            if (first.Frames.Count > maxFrames)
            {
                yield return FormattableString.Invariant($"    ... {first.Frames.Count - maxFrames} more");
            }

            i = j;
        }
    }
}

/// <summary>
/// Watches Visual Studio's UI thread during a measurement: times every dispatcher operation through
/// <see cref="Dispatcher.Hooks"/> (keeping those over a threshold, with the async method or delegate behind them), and
/// when the UI thread has started or finished no operation for 400 ms, samples its managed stack from a background
/// thread. Together they say whose code held the UI thread in a gap: an operation of Pillion's, another component's, or
/// none at all (native code, a modal COM wait, or a blocked thread).
/// </summary>
/// <remarks>
/// Stack sampling suspends the UI thread for a moment (<see cref="Thread.Suspend"/>); that is acceptable in a test, never
/// in the product. Start and stop the monitor on the UI thread.
/// </remarks>
public sealed class UiThreadMonitor : IDisposable
{
    private static readonly TimeSpan StuckAfter = TimeSpan.FromMilliseconds(400);

    private readonly Dispatcher _dispatcher;
    private readonly Thread _uiThread;
    private readonly Stopwatch _clock;
    private readonly TimeSpan _threshold;
    private readonly TimeSpan _sampleInterval = TimeSpan.FromMilliseconds(100);
    private readonly object _gate = new();
    private readonly List<UiOperationTiming> _operations = [];
    private readonly List<UiStackSample> _samples = [];
    private readonly Stack<(DispatcherOperation Operation, long Started)> _running = new();
    private readonly Thread _sampler;
    private readonly ManualResetEventSlim _stop = new();
    private long _lastActivity;
    private int _stopped;

    private UiThreadMonitor(Dispatcher dispatcher, Stopwatch clock, TimeSpan threshold)
    {
        _dispatcher = dispatcher;
        _uiThread = Thread.CurrentThread;
        _clock = clock;
        _threshold = threshold;
        _lastActivity = clock.ElapsedTicks;
        _dispatcher.Hooks.OperationStarted += OnOperationStarted;
        _dispatcher.Hooks.OperationCompleted += OnOperationCompleted;
        _dispatcher.Hooks.OperationAborted += OnOperationCompleted;
        _sampler = new Thread(Sample) { IsBackground = true, Name = "Pillion test UI stack sampler" };
        _sampler.Start();
    }

    /// <summary>Starts watching the calling thread, which must be the dispatcher's (the UI thread).</summary>
    /// <param name="dispatcher">The UI thread's dispatcher.</param>
    /// <param name="clock">The clock every time in the result is read from; share it with the probes.</param>
    /// <param name="threshold">Operations shorter than this are not kept.</param>
    /// <returns>The monitor; call <see cref="Stop"/> on the UI thread.</returns>
    public static UiThreadMonitor Start(Dispatcher dispatcher, Stopwatch clock, TimeSpan threshold)
    {
        if (!dispatcher.CheckAccess())
        {
            throw new InvalidOperationException("Start the UI thread monitor on the UI thread.");
        }

        return new UiThreadMonitor(dispatcher, clock, threshold);
    }

    /// <summary>Returns whether a description or stack frame names Pillion's product code rather than this test's.</summary>
    /// <param name="text">The text.</param>
    /// <returns><see langword="true"/> for Pillion's product code.</returns>
    public static bool IsPillionProductText(string text) =>
        text.IndexOf("Pillion.", StringComparison.Ordinal) >= 0
        && text.IndexOf("Pillion.VisualStudio.IntegrationTests", StringComparison.Ordinal) < 0;

    /// <summary>Stops watching and returns what was seen.</summary>
    /// <returns>The result.</returns>
    public UiThreadMonitorResult Stop()
    {
        Dispose();
        lock (_gate)
        {
            return new UiThreadMonitorResult(_operations.OrderBy(o => o.Start).ToList(), _samples.ToList(), _sampleInterval);
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0)
        {
            return;
        }

        _dispatcher.Hooks.OperationStarted -= OnOperationStarted;
        _dispatcher.Hooks.OperationCompleted -= OnOperationCompleted;
        _dispatcher.Hooks.OperationAborted -= OnOperationCompleted;
        _stop.Set();
        _sampler.Join(TimeSpan.FromSeconds(5));
        _stop.Dispose();
    }

    private static string Describe(DispatcherOperation operation)
    {
        try
        {
            var method = GetField(operation, "_method") as Delegate;
            var args = GetField(operation, "_args");
            var parts = new List<string>();
            DescribeInto(method, parts, 0);
            DescribeInto(args, parts, 0);
            var interesting = parts.Where(p => !IsPlumbing(p)).Distinct().ToList();
            return interesting.Count > 0 ? string.Join(" / ", interesting.Take(3)) : string.Join(" / ", parts.Distinct().Take(3));
        }
#pragma warning disable CA1031 // Best effort: the description is for the log only.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            return "(undescribed: " + ex.GetType().Name + ")";
        }
    }

    private static void DescribeInto(object? value, List<string> parts, int depth)
    {
        if (value is null || depth > 5 || parts.Count > 12)
        {
            return;
        }

        if (value is Delegate d)
        {
            parts.Add((d.Method.DeclaringType?.FullName ?? "?") + "." + d.Method.Name);
            DescribeInto(d.Target, parts, depth + 1);
            return;
        }

        var type = value.GetType();
        if (typeof(System.Runtime.CompilerServices.IAsyncStateMachine).IsAssignableFrom(type))
        {
            parts.Add(type.FullName ?? type.Name);
            return;
        }

        if (type.IsPrimitive || value is string)
        {
            return;
        }

        parts.Add(type.FullName ?? type.Name);
        foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
        {
            if (field.FieldType.IsPrimitive || field.FieldType == typeof(string) || field.FieldType.IsEnum)
            {
                continue;
            }

            if (typeof(Delegate).IsAssignableFrom(field.FieldType)
                || typeof(System.Runtime.CompilerServices.IAsyncStateMachine).IsAssignableFrom(field.FieldType)
                || field.FieldType == typeof(object)
                || field.Name.IndexOf("state", StringComparison.OrdinalIgnoreCase) >= 0
                || field.Name.IndexOf("continuation", StringComparison.OrdinalIgnoreCase) >= 0
                || field.Name.IndexOf("action", StringComparison.OrdinalIgnoreCase) >= 0
                || field.Name.IndexOf("delegate", StringComparison.OrdinalIgnoreCase) >= 0
                || field.Name.IndexOf("job", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                DescribeInto(field.GetValue(value), parts, depth + 1);
            }
        }
    }

    private static bool IsPlumbing(string name) =>
        name.StartsWith("System.", StringComparison.Ordinal)
        || name.StartsWith("Microsoft.VisualStudio.Threading.", StringComparison.Ordinal)
        || name.StartsWith("MS.Internal.", StringComparison.Ordinal);

    private static object? GetField(object target, string name) =>
        target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(target);

    private static List<string> CaptureStack(Thread thread)
    {
        var frames = new List<string>();
#pragma warning disable CS0618 // Thread.Suspend and StackTrace(Thread, bool) are obsolete; they are the only in-process way to read another thread's stack on .NET Framework.
        var suspended = false;
        try
        {
            thread.Suspend();
            suspended = true;
            var trace = new StackTrace(thread, false);
            foreach (var frame in trace.GetFrames() ?? [])
            {
                var method = frame.GetMethod();
                frames.Add(method is null ? "?" : (method.DeclaringType?.FullName ?? "?") + "." + method.Name);
            }
        }
#pragma warning disable CA1031 // Best effort: a failed sample is recorded as such.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            frames.Add("(could not sample: " + ex.GetType().Name + ": " + ex.Message + ")");
        }
        finally
        {
            if (suspended)
            {
                thread.Resume();
            }
        }
#pragma warning restore CS0618

        return frames;
    }

    private void OnOperationStarted(object? sender, DispatcherHookEventArgs e)
    {
        var now = _clock.ElapsedTicks;
        Interlocked.Exchange(ref _lastActivity, now);
        _running.Push((e.Operation, now));
    }

    private void OnOperationCompleted(object? sender, DispatcherHookEventArgs e)
    {
        var now = _clock.ElapsedTicks;
        Interlocked.Exchange(ref _lastActivity, now);
        if (_running.Count == 0 || !ReferenceEquals(_running.Peek().Operation, e.Operation))
        {
            return;
        }

        var (operation, started) = _running.Pop();
        var duration = TicksToTimeSpan(now - started);
        if (duration < _threshold)
        {
            return;
        }

        var timing = new UiOperationTiming(TicksToTimeSpan(started), duration, operation.Priority, Describe(operation));
        lock (_gate)
        {
            _operations.Add(timing);
        }
    }

    private void Sample()
    {
        while (!_stop.Wait(_sampleInterval))
        {
            var idle = TicksToTimeSpan(_clock.ElapsedTicks - Interlocked.Read(ref _lastActivity));
            if (idle < StuckAfter)
            {
                continue;
            }

            var at = _clock.Elapsed;
            var frames = CaptureStack(_uiThread);
            lock (_gate)
            {
                if (_samples.Count < 400)
                {
                    _samples.Add(new UiStackSample(at, frames));
                }
            }
        }
    }

    private static TimeSpan TicksToTimeSpan(long stopwatchTicks) => TimeSpan.FromTicks((long)(stopwatchTicks * (10_000_000.0 / Stopwatch.Frequency)));}
