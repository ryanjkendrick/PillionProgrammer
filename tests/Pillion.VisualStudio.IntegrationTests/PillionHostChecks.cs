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

/// <summary>What <see cref="PillionHostChecks.ConversationSurvivesHostRestartAsync"/> saw.</summary>
/// <param name="ConversationId">The id of the conversation the check created.</param>
/// <param name="Restart">The host restart in between.</param>
/// <param name="TitleAfter">The conversation's title after the restart, or <see langword="null"/> when the new host did not have it.</param>
/// <param name="PinNamesAfter">The names of its pins after the restart.</param>
/// <param name="ListedAfter">Whether the new host listed it.</param>
public sealed record ConversationRestartReport(string ConversationId, HostRestartReport Restart, string? TitleAfter, string[] PinNamesAfter, bool ListedAfter);

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

    /// <summary>The name of the environment variable that switches the product's <see cref="UiThreadTimings"/> on (a copy of its constant, so a method that only names this does not load Pillion.VisualStudio.Core).</summary>
    public const string UiThreadTimingsVariable = "PILLION_UI_THREAD_TIMINGS";

    /// <summary>Puts the timings variable back to what it was before the package-load measurement and sets the switch to match (after the package has loaded).</summary>
    /// <param name="previous">The variable's earlier value, or <see langword="null"/>.</param>
    public static void RestoreUiThreadTimingsVariable(string? previous)
    {
        Environment.SetEnvironmentVariable(UiThreadTimingsVariable, previous);
        UiThreadTimings.Enabled = string.Equals(previous, "1", StringComparison.Ordinal);
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

    /// <summary>
    /// Describes Pillion's own timed work around the package load, from what <see cref="UiThreadTimings"/> recorded between
    /// two <see cref="Stopwatch.GetTimestamp"/> moments. The timings are recorded only while the switch is on, which for
    /// the package load means <c>PILLION_UI_THREAD_TIMINGS=1</c> in devenv.exe's environment when the package loads (the
    /// load test sets it in-process before it opens the folder; the gate script sets it for the whole run).
    /// </summary>
    /// <param name="fromTimestamp">The start.</param>
    /// <param name="toTimestamp">The end.</param>
    /// <param name="uiThreadId">The managed thread id of the UI thread.</param>
    /// <returns>The summary.</returns>
    public static PackageLoadTimings SummarizeLoad(long fromTimestamp, long toTimestamp, int uiThreadId)
    {
        var timings = UiThreadTimings.Snapshot().Where(t => t.StartTimestamp >= fromTimestamp && t.EndTimestamp <= toTimestamp).ToList();
        var preAwait = timings.FirstOrDefault(t => t.Name == "package.init.preawait");

        // A name with a "/" is a part of the visit before it (its time is inside that visit's): listed, not added again.
        var mainThread = timings.Where(t => t.ThreadId == uiThreadId && t.Name.IndexOf('/') < 0).ToList();
        var stretches = timings.Where(t => t.ThreadId == uiThreadId)
            .GroupBy(t => t.Name)
            .Select(g => new LoadStretch(g.Key, g.Count(), TimeSpan.FromTicks(g.Sum(t => t.Duration.Ticks))))
            .OrderBy(s => s.Name, StringComparer.Ordinal) // A part sorts right after its visit.
            .ToList();
        var elsewhere = timings
            .Where(t => t.ThreadId != uiThreadId)
            .GroupBy(t => t.Name)
            .Select(g => new LoadStretch(g.Key, g.Count(), TimeSpan.FromTicks(g.Sum(t => t.Duration.Ticks))))
            .OrderByDescending(s => s.Total)
            .ToList();
        return new PackageLoadTimings(
            UiThreadTimings.Enabled,
            TimeSpan.FromTicks(mainThread.Sum(t => t.Duration.Ticks)),
            mainThread.Count,
            preAwait?.Duration,
            preAwait is not null && preAwait.ThreadId == uiThreadId,
            stretches)
        {
            OffThread = elsewhere,
        };
    }

    /// <summary>
    /// Saves a conversation through the host's <see cref="IConversationService"/> (the shipped host, with the Local Store in the
    /// user's data folder), kills the host, and reads the conversation from the new host. The conversation is deleted again (only
    /// that one: never Delete all) whatever the outcome.
    /// </summary>
    /// <param name="readyTimeout">How long, from the kill, the host gets to be Ready again.</param>
    /// <param name="cancellationToken">Cancels the check.</param>
    /// <returns>What was seen.</returns>
    public static async Task<ConversationRestartReport> ConversationSurvivesHostRestartAsync(TimeSpan readyTimeout, CancellationToken cancellationToken)
    {
        var connection = await GetConnectionAsync().ConfigureAwait(false);
        await WaitForStateAsync(connection, HostState.Ready, TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);

        var id = "ide-test-" + Guid.NewGuid().ToString("N");
        var before = connection.GetProxy<IConversationService>();
        try
        {
            await before.CreateConversationAsync(new ConversationCreateRequest { Id = id, Title = "Integration test conversation" }, cancellationToken).ConfigureAwait(false);
            await before.SetPinsAsync(id, [new ConversationPin { Kind = MentionKinds.File, Target = @"C:\integration\Test.cs", DisplayName = "Test.cs", TokenCount = 42 }], cancellationToken).ConfigureAwait(false);

            var restart = await KillHostAndWaitForReadyAsync(readyTimeout, cancellationToken).ConfigureAwait(false);

            var after = connection.GetProxy<IConversationService>();
            var detail = await after.GetConversationAsync(id, cancellationToken).ConfigureAwait(false);
            var page = await after.ListConversationsAsync(new ConversationListRequest { Limit = 500 }, cancellationToken).ConfigureAwait(false);
            return new ConversationRestartReport(
                id,
                restart,
                detail?.Conversation.Title,
                detail is null ? [] : [.. detail.Pins.Select(p => p.DisplayName)],
                page.Items.Any(c => c.Id == id));
        }
        finally
        {
            try
            {
                await connection.GetProxy<IConversationService>().DeleteConversationAsync(id, CancellationToken.None).ConfigureAwait(false);
            }
#pragma warning disable CA1031 // Clean-up must not hide the check's own result.
            catch (Exception)
#pragma warning restore CA1031
            {
            }
        }
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

/// <summary>One kind of timed stretch on the UI thread during the package load: how many visits and their total time.</summary>
/// <param name="Name">The stretch (for example <c>package.init.commands</c>).</param>
/// <param name="Count">How many times it ran.</param>
/// <param name="Total">Their total wall-clock time.</param>
public sealed record LoadStretch(string Name, int Count, TimeSpan Total);

/// <summary>What Pillion's own timed work did on the UI thread between the folder open and Ready.</summary>
/// <param name="Enabled">Whether the timings were recorded at all.</param>
/// <param name="MainThreadTotal">The total wall-clock time of Pillion's timed stretches on the UI thread (an upper bound of its CPU time).</param>
/// <param name="MainThreadVisits">How many timed stretches ran on the UI thread: each is one visit (a switch to the UI thread, or a stretch run inline on it).</param>
/// <param name="PreAwait">The time <c>InitializeAsync</c> spent before its first switch to the UI thread, or <see langword="null"/> when not recorded.</param>
/// <param name="PreAwaitOnUiThread">Whether that part ran on the UI thread (a synchronous load: it blocks the shell) rather than on a pool thread.</param>
/// <param name="Stretches">The UI-thread stretches by name, longest first.</param>
public sealed record PackageLoadTimings(bool Enabled, TimeSpan MainThreadTotal, int MainThreadVisits, TimeSpan? PreAwait, bool PreAwaitOnUiThread, IReadOnlyList<LoadStretch> Stretches)
{
    /// <summary>Gets the timed stretches that ran on other threads (the background part of initialization), longest first: they do not block the shell.</summary>
    public IReadOnlyList<LoadStretch> OffThread { get; init; } = [];

    /// <inheritdoc/>
    public override string ToString()
    {
        if (!Enabled)
        {
            return "not recorded (PILLION_UI_THREAD_TIMINGS was not 1 when the package loaded)";
        }

        return FormattableString.Invariant($"{MainThreadTotal.TotalMilliseconds:0.0} ms on the UI thread in {MainThreadVisits} timed visits ({string.Join(", ", Stretches.Select(s => FormattableString.Invariant($"{s.Name} x{s.Count} {s.Total.TotalMilliseconds:0.0} ms")))}); InitializeAsync before its first switch: {(PreAwait is { } p ? FormattableString.Invariant($"{p.TotalMilliseconds:0.0} ms") : "not seen")} on {(PreAwaitOnUiThread ? "the UI thread (blocks the shell)" : "a pool thread (does not block the shell)")}; timed off the UI thread: {(OffThread.Count == 0 ? "none" : string.Join(", ", OffThread.Select(s => FormattableString.Invariant($"{s.Name} x{s.Count} {s.Total.TotalMilliseconds:0.0} ms"))))}");
    }
}
