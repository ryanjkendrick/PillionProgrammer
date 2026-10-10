using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Pillion.Contracts;
using Pillion.VisualStudio.Core;

namespace Pillion.VisualStudio.IntegrationTests;

/// <summary>What <see cref="PillionHostChecks.WaitForReadyAsync"/> saw.</summary>
/// <param name="Elapsed">How long the wait took.</param>
/// <param name="State">The state at the end.</param>
/// <param name="ServiceAssembly">Where the IHostConnection type was loaded from (the installed extension).</param>
public sealed record HostReadyReport(TimeSpan Elapsed, string State, string ServiceAssembly);

/// <summary>What <see cref="PillionHostChecks.KillHostAndWaitForReadyAsync"/> saw.</summary>
/// <param name="KilledProcessId">The host process that was killed.</param>
/// <param name="NewProcessId">The host process running afterwards (0 when none).</param>
/// <param name="LeftReadyAfter">Time from the kill to the first state other than Ready.</param>
/// <param name="ReadyAgainAfter">Time from the kill to Ready again.</param>
/// <param name="Transitions">The state changes seen, with times since the kill.</param>
/// <param name="KilledAt">When the kill happened on the caller's timeline (see <c>timeline</c> of <see cref="PillionHostChecks.KillHostAndWaitForReadyAsync"/>); zero without one.</param>
public sealed record HostRestartReport(int KilledProcessId, int NewProcessId, TimeSpan LeftReadyAfter, TimeSpan ReadyAgainAfter, string[] Transitions, TimeSpan KilledAt = default);

/// <summary>
/// Checks that use the extension's <see cref="IHostConnection"/> service. Kept out of the test classes so that test
/// discovery, which runs outside Visual Studio, never needs Pillion.VisualStudio.Core; call these only after the
/// package has loaded (<see cref="PillionIdeTest.EnsurePackageLoadedAsync"/>).
/// </summary>
public static class PillionHostChecks
{
    /// <summary>Waits until the connection to Pillion Host is <see cref="HostState.Ready"/>.</summary>
    /// <param name="timeout">How long to wait.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>What was seen.</returns>
    /// <exception cref="TimeoutException">The host was not ready in time.</exception>
    public static async Task<HostReadyReport> WaitForReadyAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();
        var connection = await GetConnectionAsync().ConfigureAwait(false);
        var remaining = timeout - clock.Elapsed;
        await WaitForStateAsync(connection, HostState.Ready, remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero, cancellationToken).ConfigureAwait(false);
        return new HostReadyReport(clock.Elapsed, connection.State.ToString(), typeof(IHostConnection).Assembly.Location);
    }

    /// <summary>
    /// Kills the Pillion.Host.exe started by this Visual Studio and waits for the supervisor to leave Ready and come
    /// back to Ready with a new host process.
    /// </summary>
    /// <param name="readyTimeout">How long, from the kill, the host gets to be Ready again.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <param name="timeline">A clock shared with other probes; the time of the kill on it is reported as <see cref="HostRestartReport.KilledAt"/>.</param>
    /// <returns>What was seen.</returns>
    public static async Task<HostRestartReport> KillHostAndWaitForReadyAsync(TimeSpan readyTimeout, CancellationToken cancellationToken, Stopwatch? timeline = null)
    {
        var connection = await GetConnectionAsync().ConfigureAwait(false);
        await WaitForStateAsync(connection, HostState.Ready, TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);

        var hosts = await HostProcesses.FindChildrenAsync().ConfigureAwait(false);
        if (hosts.Count != 1)
        {
            throw new InvalidOperationException($"Expected one Pillion.Host.exe child of this devenv.exe, found {hosts.Count} ({string.Join(", ", hosts)}).");
        }

        var killed = hosts[0];
        var transitions = new ConcurrentQueue<string>();
        var leftReady = new TaskCompletionSource<TimeSpan>(TaskCreationOptions.RunContinuationsAsynchronously);
        var readyAgain = new TaskCompletionSource<TimeSpan>(TaskCreationOptions.RunContinuationsAsynchronously);
        var clock = new Stopwatch();
        var killedAt = TimeSpan.Zero;

        void OnStateChanged(object? sender, HostStateChangedEventArgs e)
        {
            var at = clock.Elapsed;
            transitions.Enqueue($"{at.TotalMilliseconds:0} ms: {e.PreviousState} -> {e.State}" + (e.Reason is null ? string.Empty : $" ({e.Reason})"));
            if (e.State != HostState.Ready)
            {
                leftReady.TrySetResult(at);
            }
            else if (leftReady.Task.IsCompleted)
            {
                readyAgain.TrySetResult(at);
            }
        }

        connection.StateChanged += OnStateChanged;
        try
        {
            using (var process = Process.GetProcessById(killed))
            {
                killedAt = timeline?.Elapsed ?? TimeSpan.Zero;
                clock.Start();
                process.Kill();
            }

            var leftAfter = await WithTimeoutAsync(leftReady.Task, readyTimeout, () => $"the supervisor to notice the kill (state {connection.State})", cancellationToken).ConfigureAwait(false);
            var remaining = readyTimeout - clock.Elapsed;
            var readyAfter = await WithTimeoutAsync(readyAgain.Task, remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero, () => $"Ready after the kill (state {connection.State}; transitions: {string.Join("; ", transitions)})", cancellationToken).ConfigureAwait(false);

            var now = await HostProcesses.FindChildrenAsync().ConfigureAwait(false);
            return new HostRestartReport(killed, now.Count > 0 ? now[0] : 0, leftAfter, readyAfter, [.. transitions], killedAt);
        }
        finally
        {
            connection.StateChanged -= OnStateChanged;
        }
    }

    /// <summary>Switches on the product's <see cref="UiThreadTimings"/> debug switch, forgetting earlier timings.</summary>
    public static void StartUiThreadTimings()
    {
        UiThreadTimings.Clear();
        UiThreadTimings.Enabled = true;
    }

    /// <summary>
    /// Switches the product's <see cref="UiThreadTimings"/> off again (unless <c>PILLION_UI_THREAD_TIMINGS</c> keeps it on)
    /// and returns what it timed, on a test clock.
    /// </summary>
    /// <param name="clock">The running clock to place the timings on.</param>
    /// <returns>Pillion's own stretches of UI-thread work, described as <c>Pillion.VisualStudio UI work: name</c>.</returns>
    public static IReadOnlyList<UiOperationTiming> StopUiThreadTimings(Stopwatch clock)
    {
        UiThreadTimings.Enabled = string.Equals(Environment.GetEnvironmentVariable(UiThreadTimings.Variable), "1", StringComparison.Ordinal);
        var origin = Stopwatch.GetTimestamp() - clock.ElapsedTicks;
        return UiThreadTimings.Snapshot()
            .Select(t => new UiOperationTiming(
                TimeSpan.FromTicks((long)((t.StartTimestamp - origin) * (10_000_000.0 / Stopwatch.Frequency))),
                t.Duration,
                System.Windows.Threading.DispatcherPriority.Invalid,
                "Pillion.VisualStudio UI work: " + t.Name))
            .ToList();
    }

    private static async Task<IHostConnection> GetConnectionAsync()
    {
        var service = await AsyncServiceProvider.GlobalProvider.GetServiceAsync(typeof(IHostConnection)).ConfigureAwait(false);
        if (service is null)
        {
            throw new InvalidOperationException("The IHostConnection service is not registered; is the Pillion package installed in this Experimental Instance?");
        }

        if (service is IHostConnection connection)
        {
            return connection;
        }

        throw new InvalidOperationException(
            $"The IHostConnection service is a {service.GetType().AssemblyQualifiedName} from {service.GetType().Assembly.Location}, "
            + $"which does not implement the test's IHostConnection from {typeof(IHostConnection).Assembly.Location}.");
    }

    private static async Task WaitForStateAsync(IHostConnection connection, HostState target, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var reached = new TaskCompletionSource<TimeSpan>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnStateChanged(object? sender, HostStateChangedEventArgs e)
        {
            if (e.State == target)
            {
                reached.TrySetResult(TimeSpan.Zero);
            }
        }

        connection.StateChanged += OnStateChanged;
        try
        {
            if (connection.State == target)
            {
                return;
            }

            await WithTimeoutAsync(reached.Task, timeout, () => $"Pillion Host to be {target} (state {connection.State})", cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            connection.StateChanged -= OnStateChanged;
        }
    }

    // The tasks awaited here are TaskCompletionSource tasks completed from StateChanged (raised on the thread pool) with
    // RunContinuationsAsynchronously; they never need the UI thread, so awaiting them cannot deadlock it.
#pragma warning disable VSTHRD003 // Avoid awaiting foreign Tasks
    private static async Task<T> WithTimeoutAsync<T>(Task<T> task, TimeSpan timeout, Func<string> what, CancellationToken cancellationToken)
    {
        using var delayCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var delay = Task.Delay(timeout, delayCancellation.Token);
        var winner = await Task.WhenAny(task, delay).ConfigureAwait(false);
        if (winner != task)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException($"Timed out after {timeout.TotalSeconds:0.#} s waiting for {what()}.");
        }

        delayCancellation.Cancel();
        return await task.ConfigureAwait(false);
    }
#pragma warning restore VSTHRD003
}
