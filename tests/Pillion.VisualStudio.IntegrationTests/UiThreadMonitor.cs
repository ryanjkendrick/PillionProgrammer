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

    /// <summary>Gets a value indicating whether the stack could not be read.</summary>
    public bool Failed => Frames.Count == 1 && Frames[0].StartsWith(UiThreadMonitor.FailedSamplePrefix, StringComparison.Ordinal);
}

/// <summary>What <see cref="UiThreadMonitor"/> saw.</summary>
/// <param name="Operations">The dispatcher operations that ran for at least the monitor's threshold.</param>
/// <param name="Samples">The stack samples taken while the UI thread was stuck.</param>
/// <param name="SampleInterval">The time between two samples of one stall.</param>
/// <param name="PillionHops">
/// Pillion's own timed stretches of UI-thread work (the product's <c>UiThreadTimings</c> debug switch), on the monitor's
/// clock; they name work that reaches the UI thread through Visual Studio's task scheduler, which the dispatcher
/// operations cannot.
/// </param>
public sealed record UiThreadMonitorResult(
    IReadOnlyList<UiOperationTiming> Operations,
    IReadOnlyList<UiStackSample> Samples,
    TimeSpan SampleInterval,
    IReadOnlyList<UiOperationTiming>? PillionHops = null)
{
    /// <summary>The longest stretch without a dispatcher event or a stack sample that still counts as watched.</summary>
    public static readonly TimeSpan MaxUnwatched = TimeSpan.FromMilliseconds(400);

    /// <summary>
    /// Estimates how much of an interval Pillion's product code held the UI thread: the union of Pillion's dispatcher
    /// operations, Pillion's own timed hops, and one sample interval before every stack sample with a Pillion frame,
    /// clipped to the interval. Returns <see langword="null"/> when the monitor did not watch the whole interval (a stack
    /// sample in it failed, or it saw neither a dispatcher event nor a sample for more than <see cref="MaxUnwatched"/>),
    /// so the caller never takes "not seen" for "not Pillion".
    /// </summary>
    /// <param name="from">The start of the interval, on the monitor's clock.</param>
    /// <param name="to">The end of the interval.</param>
    /// <returns>The estimate, or <see langword="null"/>.</returns>
    public TimeSpan? PillionTimeWithin(TimeSpan from, TimeSpan to)
    {
        var samples = Samples.Where(s => s.At >= from && s.At <= to).ToList();
        if (samples.Any(s => s.Failed))
        {
            return null;
        }

        var hops = PillionHops ?? [];
        var seen = Operations.SelectMany(o => new[] { o.Start, o.End })
            .Concat(hops.SelectMany(h => new[] { h.Start, h.End }))
            .Concat(samples.Select(s => s.At))
            .Where(t => t > from && t < to)
            .OrderBy(t => t)
            .ToList();
        var previous = from;
        foreach (var time in seen.Append(to))
        {
            if (time - previous > MaxUnwatched && !Covered(time, previous))
            {
                return null;
            }

            previous = time;
        }

        var pillion = Operations.Where(o => o.IsPillion).Select(o => (o.Start, o.End))
            .Concat(hops.Select(h => (h.Start, h.End)))
            .Concat(samples.Where(s => s.HasPillionFrame).Select(s => (Start: s.At - SampleInterval, End: s.At)));
        return UnionWithin(pillion, from, to);
    }

    private static TimeSpan Overlap(IEnumerable<UiOperationTiming> operations, TimeSpan from, TimeSpan to) =>
        UnionWithin(operations.Select(o => (o.Start, o.End)), from, to);

    private static TimeSpan UnionWithin(IEnumerable<(TimeSpan Start, TimeSpan End)> intervals, TimeSpan from, TimeSpan to)
    {
        var total = TimeSpan.Zero;
        var reached = from;
        foreach (var (start, end) in intervals.OrderBy(i => i.Start))
        {
            var clippedStart = start > reached ? start : reached;
            var clippedEnd = end < to ? end : to;
            if (clippedEnd > clippedStart)
            {
                total += clippedEnd - clippedStart;
                reached = clippedEnd;
            }
        }

        return total;
    }

    /// <summary>Returns whether one dispatcher operation spans a stretch (a long operation is watched while it runs).</summary>
    private bool Covered(TimeSpan end, TimeSpan start) => Operations.Any(o => o.Start <= start && o.End >= end);
    /// <summary>Formats the operations and the stack samples (identical consecutive stacks are folded together).</summary>
    /// <param name="listFrom">Operations at least this long are listed one by one.</param>
    /// <param name="samplesFrom">The first time, on the monitor's clock, whose stack samples are printed.</param>
    /// <param name="samplesTo">The last time whose stack samples are printed.</param>
    /// <param name="maxFrames">How many innermost frames to print per stack.</param>
    /// <returns>The lines.</returns>
    public IEnumerable<string> Format(TimeSpan listFrom, TimeSpan samplesFrom, TimeSpan samplesTo, int maxFrames = 40)
    {
        yield return FormattableString.Invariant(
            $"UI thread dispatcher operations timed: {Operations.Count}, {Operations.Sum(o => o.Duration.TotalMilliseconds):0} ms in all");
        foreach (var group in Operations.Where(o => !o.IsPillion).GroupBy(o => o.Description).OrderByDescending(g => g.Sum(o => o.Duration.Ticks)).Take(12))
        {
            yield return FormattableString.Invariant(
                $"  {group.Count(),3} x, {group.Sum(o => o.Duration.TotalMilliseconds),7:0.0} ms in all, longest {group.Max(o => o.Duration.TotalMilliseconds),6:0.0} ms: {group.Key}");
        }

        var pillion = Operations.Where(o => o.IsPillion).ToList();
        yield return pillion.Count == 0
            ? "Pillion's dispatcher operations on the UI thread: none"
            : FormattableString.Invariant(
                $"Pillion's dispatcher operations on the UI thread: {pillion.Count}, {pillion.Sum(o => o.Duration.TotalMilliseconds):0} ms in all, longest {pillion.Max(o => o.Duration.TotalMilliseconds):0.0} ms:");
        foreach (var group in pillion.GroupBy(o => o.Description).OrderByDescending(g => g.Sum(o => o.Duration.Ticks)))
        {
            yield return FormattableString.Invariant(
                $"  {group.Count(),3} x, {group.Sum(o => o.Duration.TotalMilliseconds),7:0.0} ms in all, longest {group.Max(o => o.Duration.TotalMilliseconds),6:0.0} ms: {group.Key}");
        }

        if (PillionHops is { } hops)
        {
            yield return hops.Count == 0
                ? "Pillion's own timed UI thread work: none"
                : FormattableString.Invariant(
                    $"Pillion's own timed UI thread work: {hops.Count} stretches, {hops.Sum(h => h.Duration.TotalMilliseconds):0.0} ms in all, longest {hops.Max(h => h.Duration.TotalMilliseconds):0.0} ms:");
            foreach (var group in hops.GroupBy(h => h.Description).OrderByDescending(g => g.Sum(h => h.Duration.Ticks)))
            {
                yield return FormattableString.Invariant(
                    $"  {group.Count(),3} x, {group.Sum(h => h.Duration.TotalMilliseconds),7:0.0} ms in all, longest {group.Max(h => h.Duration.TotalMilliseconds),6:0.0} ms: {group.Key}");
            }
        }

        var slow = Operations.Where(o => o.Duration >= listFrom).ToList();
        yield return slow.Count == 0
            ? FormattableString.Invariant($"UI thread dispatcher operations of {listFrom.TotalMilliseconds:0} ms or more: none")
            : FormattableString.Invariant($"UI thread dispatcher operations of {listFrom.TotalMilliseconds:0} ms or more (start, duration, priority, what):");
        foreach (var operation in slow)
        {
            yield return "  " + operation + (operation.IsPillion ? "  <-- Pillion" : string.Empty);
        }

        var inGap = Operations.Where(o => o.End > samplesFrom && o.Start < samplesTo).ToList();
        yield return FormattableString.Invariant(
            $"UI thread dispatcher operations from {samplesFrom.TotalMilliseconds:0} to {samplesTo.TotalMilliseconds:0} ms: {inGap.Count}, {Overlap(inGap, samplesFrom, samplesTo).TotalMilliseconds:0} ms in all{(inGap.Count > 0 ? " (most frequent first):" : string.Empty)}");
        foreach (var group in inGap.GroupBy(o => (o.Priority, o.Description)).OrderByDescending(g => g.Count()).Take(8))
        {
            yield return FormattableString.Invariant(
                $"  {group.Count(),4} x {group.Key.Priority,-11} {group.Sum(o => o.Duration.TotalMilliseconds),7:0.0} ms in all, longest {group.Max(o => o.Duration.TotalMilliseconds),6:0.0} ms: {group.Key.Description}")
                + (group.First().IsPillion ? "  <-- Pillion" : string.Empty);
        }

        foreach (var operation in inGap.Where(o => o.Duration >= TimeSpan.FromMilliseconds(10)).OrderByDescending(o => o.Duration).Take(10))
        {
            yield return "  longest: " + operation + (operation.IsPillion ? "  <-- Pillion" : string.Empty);
        }

        if (PillionHops is { } allHops)
        {
            var hopsInGap = allHops.Where(h => h.End > samplesFrom && h.Start < samplesTo).ToList();
            yield return FormattableString.Invariant(
                $"Pillion's own timed UI thread work from {samplesFrom.TotalMilliseconds:0} to {samplesTo.TotalMilliseconds:0} ms: {hopsInGap.Count}, {Overlap(hopsInGap, samplesFrom, samplesTo).TotalMilliseconds:0.0} ms in all");
            foreach (var hop in hopsInGap.OrderByDescending(h => h.Duration).Take(10))
            {
                yield return "  " + hop;
            }
        }

        var shown = Samples.Where(s => s.At >= samplesFrom && s.At <= samplesTo).ToList();
        yield return FormattableString.Invariant(
            $"UI thread stack samples (taken every {SampleInterval.TotalMilliseconds:0} ms while the UI thread started or finished no dispatcher operation for 200 ms): {Samples.Count} in all, {Samples.Count(s => s.HasPillionFrame)} with a Pillion frame; {shown.Count} from {samplesFrom.TotalMilliseconds:0} to {samplesTo.TotalMilliseconds:0} ms{(shown.Count > 0 ? " (innermost frame first):" : string.Empty)}");
        for (var i = 0; i < shown.Count;)
        {
            var first = shown[i];
            var j = i + 1;
            while (j < shown.Count && shown[j].Frames.SequenceEqual(first.Frames))
            {
                j++;
            }

            var last = shown[j - 1];
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
/// when the UI thread has started or finished no operation for 200 ms, samples its managed stack every 100 ms from a background
/// thread. Together they say whose code held the UI thread in a gap: an operation of Pillion's, another component's, or
/// none at all (native code, a modal COM wait, or a blocked thread).
/// </summary>
/// <remarks>
/// Stack sampling suspends the UI thread for a moment (<see cref="Thread.Suspend"/>); that is acceptable in a test, never
/// in the product. Start and stop the monitor on the UI thread.
/// </remarks>
public sealed class UiThreadMonitor : IDisposable
{
    /// <summary>How long the UI thread must start or finish no dispatcher operation before its stack is sampled.</summary>
    public static readonly TimeSpan StuckAfter = TimeSpan.FromMilliseconds(200);

    /// <summary>The text a failed stack sample starts with.</summary>
    public const string FailedSamplePrefix = "(could not sample";

    private readonly Dispatcher _dispatcher;
    private readonly Thread _uiThread;
    private readonly Stopwatch _clock;
    private readonly TimeSpan _threshold;
    private readonly TimeSpan _sampleInterval = TimeSpan.FromMilliseconds(100);
    private readonly object _gate = new();
    private static readonly FieldInfo? OperationMethodField = typeof(DispatcherOperation).GetField("_method", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly FieldInfo? OperationArgsField = typeof(DispatcherOperation).GetField("_args", BindingFlags.Instance | BindingFlags.NonPublic);
    private static Type? protectorType;
    private static FieldInfo? protectorDelegateField;
    private static FieldInfo? protectorStateField;

    private readonly List<(object?[] Payload, long Started, long Duration, DispatcherPriority Priority)> _operations = [];
    private readonly List<UiStackSample> _samples = [];
    private readonly Stack<(DispatcherOperation Operation, object?[] Payload, long Started)> _running = new();
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
    /// <param name="threshold">Operations shorter than this are not kept (zero keeps them all).</param>
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
    /// <param name="pillionHops">Pillion's own timed UI thread work over the same window, on the same clock, if it was recorded.</param>
    /// <returns>The result.</returns>
    public UiThreadMonitorResult Stop(IReadOnlyList<UiOperationTiming>? pillionHops = null)
    {
        Dispose();
        lock (_gate)
        {
            var operations = _operations.Select(o => new UiOperationTiming(TicksToTimeSpan(o.Started), TicksToTimeSpan(o.Duration), o.Priority, Describe(o.Payload)));
            return new UiThreadMonitorResult(operations.OrderBy(o => o.Start).ToList(), _samples.ToList(), _sampleInterval, pillionHops);
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

    /// <summary>
    /// Reads what an operation will run before it runs: the delegate and argument, and when the argument is a
    /// JoinableTaskFactory work item, the continuation inside it (the work item clears it once executed). Cheap: a few
    /// cached field reads on the UI thread.
    /// </summary>
    private static object?[] CapturePayload(DispatcherOperation operation)
    {
        var method = OperationMethodField?.GetValue(operation);
        var args = OperationArgsField?.GetValue(operation);
        object? inner = null;
        object? innerState = null;
        if (args is not null)
        {
            var type = args.GetType();
            if (protectorType is null && type.FullName == "Microsoft.VisualStudio.Threading.JoinableTaskFactory+SingleExecuteProtector")
            {
                protectorDelegateField = type.GetField("invokeDelegate", BindingFlags.Instance | BindingFlags.NonPublic);
                protectorStateField = type.GetField("state", BindingFlags.Instance | BindingFlags.NonPublic);
                protectorType = type;
            }

            if (type == protectorType)
            {
                inner = protectorDelegateField?.GetValue(args);
                innerState = protectorStateField?.GetValue(args);
            }
        }

        return [inner, innerState, method, args];
    }

    private static string Describe(object?[] payload)
    {
        try
        {
            var parts = new List<string>();
            foreach (var item in payload)
            {
                DescribeInto(item, parts, 0);
            }

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
            frames.Clear();
            frames.Add(FailedSamplePrefix + ": " + ex.GetType().Name + ": " + ex.Message + ")");
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
        _running.Push((e.Operation, CapturePayload(e.Operation), now));
    }

    private void OnOperationCompleted(object? sender, DispatcherHookEventArgs e)
    {
        var now = _clock.ElapsedTicks;
        Interlocked.Exchange(ref _lastActivity, now);
        if (_running.Count == 0 || !ReferenceEquals(_running.Peek().Operation, e.Operation))
        {
            return;
        }

        // Described after the measurement (reflection is slow), so the UI thread only pays for a timestamp here.
        var (operation, payload, started) = _running.Pop();
        if (TicksToTimeSpan(now - started) < _threshold)
        {
            return;
        }

        lock (_gate)
        {
            _operations.Add((payload, started, now - started, operation.Priority));
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
