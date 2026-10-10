using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace Pillion.VisualStudio.IntegrationTests;

/// <summary>One operation <see cref="DispatcherProbe"/> posted.</summary>
/// <param name="PostedAt">When it was posted, on the probe's clock.</param>
/// <param name="RanAt">When it ran on the dispatcher, on the probe's clock; <see langword="null"/> if it never ran.</param>
public sealed record DispatcherOperationTiming(TimeSpan PostedAt, TimeSpan? RanAt)
{
    /// <summary>Gets how long the operation waited from posting to running, or <see langword="null"/> if it never ran.</summary>
    public TimeSpan? Wait => RanAt - PostedAt;
}

/// <summary>What <see cref="DispatcherProbe"/> measured.</summary>
/// <param name="Posted">How many operations were posted.</param>
/// <param name="Answered">How many of them ran.</param>
/// <param name="LongestGap">The longest time between two answered operations (or the start and the first one).</param>
/// <param name="SlowestAnswer">The longest time one operation waited from posting to running.</param>
/// <param name="LongestGapStart">When the longest gap began, on the probe's clock.</param>
/// <param name="LongestGapEnd">When the longest gap ended, on the probe's clock.</param>
/// <param name="LongestPostInterval">The longest time between two posts: it grows when the posting thread itself was starved.</param>
/// <param name="Operations">Every operation, in posting order.</param>
public sealed record DispatcherProbeResult(
    int Posted,
    int Answered,
    TimeSpan LongestGap,
    TimeSpan SlowestAnswer,
    TimeSpan LongestGapStart,
    TimeSpan LongestGapEnd,
    TimeSpan LongestPostInterval,
    IReadOnlyList<DispatcherOperationTiming> Operations)
{
    /// <inheritdoc/>
    public override string ToString() => FormattableString.Invariant(
        $"{Answered}/{Posted} operations answered, longest gap {LongestGap.TotalMilliseconds:0} ms (from {LongestGapStart.TotalMilliseconds:0} to {LongestGapEnd.TotalMilliseconds:0} ms), slowest answer {SlowestAnswer.TotalMilliseconds:0} ms, longest time between posts {LongestPostInterval.TotalMilliseconds:0} ms");

    /// <summary>Formats every operation, one per line, marking the ones that waited at least <paramref name="slow"/>.</summary>
    /// <param name="slow">The wait from which an operation is marked.</param>
    /// <returns>The lines.</returns>
    public IEnumerable<string> FormatOperations(TimeSpan slow) =>
        Operations.Select((o, i) => o.RanAt is { } ran
            ? FormattableString.Invariant($"  #{i,2}: posted {o.PostedAt.TotalMilliseconds,6:0} ms, ran {ran.TotalMilliseconds,6:0} ms, waited {o.Wait!.Value.TotalMilliseconds,5:0} ms")
                + (o.Wait.Value >= slow ? "  <-- slow" : string.Empty)
            : FormattableString.Invariant($"  #{i,2}: posted {o.PostedAt.TotalMilliseconds,6:0} ms, never ran  <-- unanswered"));
}

/// <summary>What <see cref="BackgroundHeartbeat"/> measured.</summary>
/// <param name="Beats">How many times the thread woke up.</param>
/// <param name="LongestGap">The longest time between two beats (or the start and the first one).</param>
/// <param name="LongestGapStart">When the longest gap began, on the probe's clock.</param>
/// <param name="LongestGapEnd">When the longest gap ended, on the probe's clock.</param>
/// <param name="BeatTimes">When each beat happened, on the probe's clock.</param>
public sealed record BackgroundHeartbeatResult(int Beats, TimeSpan LongestGap, TimeSpan LongestGapStart, TimeSpan LongestGapEnd, IReadOnlyList<TimeSpan> BeatTimes)
{
    /// <inheritdoc/>
    public override string ToString() => FormattableString.Invariant(
        $"{Beats} beats, longest gap {LongestGap.TotalMilliseconds:0} ms (from {LongestGapStart.TotalMilliseconds:0} to {LongestGapEnd.TotalMilliseconds:0} ms)");
}

/// <summary>
/// Measures how responsive a WPF dispatcher (Visual Studio's UI thread) is: from a thread-pool thread, posts a
/// low-priority (<see cref="DispatcherPriority.Background"/>, below input) operation at a fixed interval and records
/// when each one runs. A hung UI thread shows up as a long gap.
/// </summary>
public static class DispatcherProbe
{
    /// <summary>Runs the probe on the thread pool. Await it from the UI thread so that thread stays free.</summary>
    /// <param name="dispatcher">The dispatcher to probe.</param>
    /// <param name="clock">The clock every time in the result is read from; share it with <see cref="BackgroundHeartbeat"/> to line the two up.</param>
    /// <param name="duration">How long to keep posting.</param>
    /// <param name="interval">The time between two posts.</param>
    /// <param name="cancellationToken">Cancels the probe.</param>
    /// <returns>The measurement.</returns>
    public static Task<DispatcherProbeResult> RunAsync(Dispatcher dispatcher, Stopwatch clock, TimeSpan duration, TimeSpan interval, CancellationToken cancellationToken) =>
        Task.Run(() => ProbeAsync(dispatcher, clock, duration, interval, cancellationToken), cancellationToken);

    /// <summary>Finds the longest interval between two consecutive points.</summary>
    /// <param name="points">The points, in ascending order.</param>
    /// <returns>The length of the longest interval and where it began and ended.</returns>
    internal static (TimeSpan Gap, TimeSpan Start, TimeSpan End) LongestGapOf(IReadOnlyList<TimeSpan> points)
    {
        var first = points.Count > 0 ? points[0] : TimeSpan.Zero;
        var best = (Gap: TimeSpan.Zero, Start: first, End: first);
        for (var i = 1; i < points.Count; i++)
        {
            var gap = points[i] - points[i - 1];
            if (gap > best.Gap)
            {
                best = (gap, points[i - 1], points[i]);
            }
        }

        return best;
    }

    private static async Task<DispatcherProbeResult> ProbeAsync(Dispatcher dispatcher, Stopwatch clock, TimeSpan duration, TimeSpan interval, CancellationToken cancellationToken)
    {
        var gate = new object();
        var started = clock.Elapsed;
        var until = started + duration;
        var postedAt = new List<TimeSpan>();
        var ranAt = new List<TimeSpan?>();
        var operations = new List<DispatcherOperation>();

        while (clock.Elapsed < until)
        {
            var now = clock.Elapsed;
            int index;
            lock (gate)
            {
                index = postedAt.Count;
                postedAt.Add(now);
                ranAt.Add(null);
            }

            // Deliberately the raw dispatcher with an explicit low priority: the probe measures the UI thread's message
            // loop itself, which JoinableTaskFactory.SwitchToMainThreadAsync would hide behind its own scheduling.
#pragma warning disable VSTHRD001 // Avoid legacy thread switching APIs
            operations.Add(dispatcher.BeginInvoke(
                DispatcherPriority.Background,
                new Action(() =>
                {
                    var ran = clock.Elapsed;
                    lock (gate)
                    {
                        ranAt[index] = ran;
                    }
                })));
#pragma warning restore VSTHRD001
            await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
        }

        // Give the last operations up to 5 s to run; any still pending then count as unanswered.
        var deadline = clock.Elapsed + TimeSpan.FromSeconds(5);
        while (operations.Any(o => o.Status is DispatcherOperationStatus.Pending or DispatcherOperationStatus.Executing) && clock.Elapsed < deadline)
        {
            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }

        var end = clock.Elapsed;
        lock (gate)
        {
            var timings = postedAt.Select((p, i) => new DispatcherOperationTiming(p, ranAt[i])).ToList();
            var answered = timings.Where(t => t.RanAt is not null).Select(t => t.RanAt!.Value).OrderBy(t => t).ToList();

            var points = new List<TimeSpan> { started };
            points.AddRange(answered);
            if (answered.Count < timings.Count)
            {
                // Something never ran: the gap runs to the end of the measurement.
                points.Add(end);
            }

            var (longestGap, gapStart, gapEnd) = LongestGapOf(points);
            var slowest = timings.Where(t => t.Wait is not null).Select(t => t.Wait!.Value).DefaultIfEmpty(TimeSpan.Zero).Max();
            var longestPostInterval = postedAt.Zip(postedAt.Skip(1), (a, b) => b - a).DefaultIfEmpty(TimeSpan.Zero).Max();
            return new DispatcherProbeResult(timings.Count, answered.Count, longestGap, slowest, gapStart, gapEnd, longestPostInterval, timings);
        }
    }
}

/// <summary>
/// A control for <see cref="DispatcherProbe"/>: a dedicated thread (not a pool thread, and nothing to do with Visual
/// Studio's UI thread) that sleeps and records when it wakes. If the whole machine or process stalls, as when a shared CI
/// runner is starved of CPU or a garbage collection suspends every managed thread, this thread shows the same gap as the
/// UI thread; a gap on the UI thread alone is the UI thread's own.
/// </summary>
public static class BackgroundHeartbeat
{
    /// <summary>Starts the heartbeat on its own thread.</summary>
    /// <param name="clock">The clock every time in the result is read from; share it with <see cref="DispatcherProbe"/>.</param>
    /// <param name="duration">How long to keep beating.</param>
    /// <param name="interval">The time between two beats.</param>
    /// <param name="cancellationToken">Cancels the heartbeat.</param>
    /// <returns>A task that completes with the measurement.</returns>
    public static Task<BackgroundHeartbeatResult> RunAsync(Stopwatch clock, TimeSpan duration, TimeSpan interval, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<BackgroundHeartbeatResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                completion.TrySetResult(Beat(clock, duration, interval, cancellationToken));
            }
#pragma warning disable CA1031 // Anything that goes wrong is reported through the task.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                completion.TrySetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = "Pillion test heartbeat",
        };
        thread.Start();
        return completion.Task;
    }

    private static BackgroundHeartbeatResult Beat(Stopwatch clock, TimeSpan duration, TimeSpan interval, CancellationToken cancellationToken)
    {
        var started = clock.Elapsed;
        var until = started + duration;
        var points = new List<TimeSpan> { started };
        while (clock.Elapsed < until && !cancellationToken.IsCancellationRequested)
        {
            Thread.Sleep(interval);
            points.Add(clock.Elapsed);
        }

        var (gap, start, end) = DispatcherProbe.LongestGapOf(points);
        return new BackgroundHeartbeatResult(points.Count - 1, gap, start, end, points.Skip(1).ToList());
    }
}

/// <summary>The verdict on a UI thread measurement given the control measurement taken beside it.</summary>
/// <param name="Failure">Why the test must fail, or <see langword="null"/>.</param>
/// <param name="Warning">What to note in the output when the test passes although the UI gap exceeded the budget, or <see langword="null"/>.</param>
public sealed record ResponsivenessVerdict(string? Failure, string? Warning)
{
    /// <summary>A UI gap over this fails the test whatever the background thread did: no machine stall explains it.</summary>
    public static readonly TimeSpan HardLimit = TimeSpan.FromSeconds(5);

    /// <summary>A background gap under this means the machine was not stalled, so the UI budget applies in full.</summary>
    public static readonly TimeSpan MachineStallThreshold = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// The most of a UI gap Pillion's own code may account for. A longer gap held by other code (Visual Studio's shell,
    /// IntelliCode, the editor) is reported, not failed: Pillion cannot fix another component's stall.
    /// </summary>
    public static readonly TimeSpan PillionShareLimit = TimeSpan.FromMilliseconds(500);

    /// <summary>Judges a measurement.</summary>
    /// <param name="uiGap">The longest gap on the UI thread.</param>
    /// <param name="backgroundGap">The longest gap on the background thread over the same window.</param>
    /// <param name="budget">The design's budget for the UI thread.</param>
    /// <param name="pillionShare">
    /// How long Pillion's own code held the UI thread within the longest gap (<see cref="UiThreadMonitorResult.PillionTimeWithin"/>),
    /// or <see langword="null"/> when that was not measured.
    /// </param>
    /// <returns>The verdict.</returns>
    public static ResponsivenessVerdict Judge(TimeSpan uiGap, TimeSpan backgroundGap, TimeSpan budget, TimeSpan? pillionShare = null)
    {
        var detail = FormattableString.Invariant($"UI thread longest gap {uiGap.TotalMilliseconds:0} ms, background thread longest gap {backgroundGap.TotalMilliseconds:0} ms");

        if (uiGap > HardLimit)
        {
            return new ResponsivenessVerdict(
                FormattableString.Invariant($"The UI thread stalled for more than {HardLimit.TotalSeconds:0} s after the host was killed ({detail}){Share(pillionShare)}."),
                null);
        }

        string? failure = null;
        if (uiGap - backgroundGap >= budget)
        {
            failure = FormattableString.Invariant($"The UI thread stalled {(uiGap - backgroundGap).TotalMilliseconds:0} ms longer than a background thread did after the host was killed ({detail}){Share(pillionShare)}; the budget is {budget.TotalSeconds:0.#} s.");
        }
        else if (uiGap >= budget && backgroundGap < MachineStallThreshold)
        {
            failure = FormattableString.Invariant($"The UI thread stalled after the host was killed while a background thread did not ({detail}){Share(pillionShare)}; the budget is {budget.TotalSeconds:0.#} s.");
        }

        if (failure is not null)
        {
            return pillionShare is { } share && share <= PillionShareLimit
                ? new ResponsivenessVerdict(
                    null,
                    FormattableString.Invariant($"WARNING: the UI thread gap reached the {budget.TotalSeconds:0.#} s budget while a background thread did not stall ({detail}), but Pillion's code held the UI thread for only {share.TotalMilliseconds:0} ms of it (the limit is {PillionShareLimit.TotalMilliseconds:0} ms), so other code stalled it; see the stack samples above."))
                : new ResponsivenessVerdict(failure, null);
        }

        if (uiGap >= budget)
        {
            return new ResponsivenessVerdict(
                null,
                FormattableString.Invariant($"WARNING: the UI thread gap reached the {budget.TotalSeconds:0.#} s budget, but a background thread stalled too ({detail}), so the machine stalled; this run cannot attribute it to Pillion's code."));
        }

        return new ResponsivenessVerdict(null, null);
    }

    private static string Share(TimeSpan? pillionShare) => pillionShare is { } share
        ? FormattableString.Invariant($"; Pillion's code held the UI thread for {share.TotalMilliseconds:0} ms of the gap (the limit is {PillionShareLimit.TotalMilliseconds:0} ms)")
        : string.Empty;
}
