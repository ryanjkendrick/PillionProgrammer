using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace Pillion.VisualStudio.IntegrationTests;

/// <summary>What <see cref="DispatcherProbe"/> measured.</summary>
/// <param name="Posted">How many operations were posted.</param>
/// <param name="Answered">How many of them ran.</param>
/// <param name="LongestGap">The longest time between two answered operations (or the start and the first one).</param>
/// <param name="SlowestAnswer">The longest time one operation waited from posting to running.</param>
public sealed record DispatcherProbeResult(int Posted, int Answered, TimeSpan LongestGap, TimeSpan SlowestAnswer)
{
    /// <inheritdoc/>
    public override string ToString() =>
        $"{Answered}/{Posted} operations answered, longest gap {LongestGap.TotalMilliseconds:0} ms, slowest answer {SlowestAnswer.TotalMilliseconds:0} ms";
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
    /// <param name="duration">How long to keep posting.</param>
    /// <param name="interval">The time between two posts.</param>
    /// <param name="cancellationToken">Cancels the probe.</param>
    /// <returns>The measurement.</returns>
    public static Task<DispatcherProbeResult> RunAsync(Dispatcher dispatcher, TimeSpan duration, TimeSpan interval, CancellationToken cancellationToken) =>
        Task.Run(() => ProbeAsync(dispatcher, duration, interval, cancellationToken), cancellationToken);

    private static async Task<DispatcherProbeResult> ProbeAsync(Dispatcher dispatcher, TimeSpan duration, TimeSpan interval, CancellationToken cancellationToken)
    {
        var gate = new object();
        var answeredAt = new List<TimeSpan>();
        var waits = new List<TimeSpan>();
        var operations = new List<DispatcherOperation>();
        var clock = Stopwatch.StartNew();

        while (clock.Elapsed < duration)
        {
            var postedAt = clock.Elapsed;

            // Deliberately the raw dispatcher with an explicit low priority: the probe measures the UI thread's message
            // loop itself, which JoinableTaskFactory.SwitchToMainThreadAsync would hide behind its own scheduling.
#pragma warning disable VSTHRD001 // Avoid legacy thread switching APIs
            operations.Add(dispatcher.BeginInvoke(
                DispatcherPriority.Background,
                new Action(() =>
                {
                    var now = clock.Elapsed;
                    lock (gate)
                    {
                        answeredAt.Add(now);
                        waits.Add(now - postedAt);
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
            var points = new List<TimeSpan> { TimeSpan.Zero };
            points.AddRange(answeredAt.OrderBy(t => t));
            if (answeredAt.Count < operations.Count)
            {
                // Something never ran: the gap runs to the end of the measurement.
                points.Add(end);
            }

            var longestGap = points.Zip(points.Skip(1), (a, b) => b - a).DefaultIfEmpty(TimeSpan.Zero).Max();
            var slowest = waits.DefaultIfEmpty(TimeSpan.Zero).Max();
            return new DispatcherProbeResult(operations.Count, answeredAt.Count, longestGap, slowest);
        }
    }
}
