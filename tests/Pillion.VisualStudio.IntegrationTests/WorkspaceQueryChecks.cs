using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Pillion.Contracts;
using Pillion.VisualStudio.Core;
using Pillion.VisualStudio.Core.WorkspaceQueries;

namespace Pillion.VisualStudio.IntegrationTests;

/// <summary>One workspace query the IDE test ran, with its wall-clock latency measured by the caller (a pool thread).</summary>
/// <param name="Query">find_references or callers_of.</param>
/// <param name="Latency">From the call to the answer.</param>
/// <param name="Results">How many references or call sites came back.</param>
/// <param name="Truncated">Whether the answer was cut short (callers-of only).</param>
public sealed record WorkspaceQueryRun(string Query, TimeSpan Latency, int Results, bool Truncated);

/// <summary>
/// Drives the extension's workspace query service (work item 2.14) inside Visual Studio. Kept out of the test class so test
/// discovery, which runs outside Visual Studio, never needs Pillion.VisualStudio.Core; use it only after the package has
/// loaded. The service is internal to the package (<c>PillionPackage.Instance.WorkspaceQueries</c>) and read by reflection,
/// as the host's RPC connection would reach it.
/// </summary>
public static class WorkspaceQueryChecks
{
    /// <summary>The prefix of the <see cref="UiThreadTimings"/> names the service records.</summary>
    public const string TimingPrefix = "workspace.query.";

    /// <summary>Gets the service the package registered for the host.</summary>
    /// <param name="timeout">How long to wait for the package to create it.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>The service.</returns>
    public static async Task<WorkspaceQueryService> GetServiceAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();
        while (true)
        {
            var assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => string.Equals(a.GetName().Name, "Pillion.VisualStudio", StringComparison.Ordinal));
            var packageType = assembly?.GetType("Pillion.VisualStudio.PillionPackage");
            var package = packageType?.GetProperty("Instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(null);
            var service = package is null ? null : packageType!.GetProperty("WorkspaceQueries", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(package);
            if (service is WorkspaceQueryService typed)
            {
                return typed;
            }

            if (service is not null)
            {
                throw new InvalidOperationException($"PillionPackage.WorkspaceQueries is a {service.GetType().AssemblyQualifiedName}, not the test's {typeof(WorkspaceQueryService).AssemblyQualifiedName}.");
            }

            if (clock.Elapsed > timeout)
            {
                throw new TimeoutException("The package's workspace query service was not created (PillionPackage.Instance.WorkspaceQueries).");
            }

            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Waits until Roslyn has loaded the scratch project: callers-of answers with the expected number of call sites. Calls
    /// are spaced so the rate limit never refuses them; a refusal (busy, rate, workspace not found yet) is retried.
    /// </summary>
    /// <param name="service">The service.</param>
    /// <param name="request">The callers-of request.</param>
    /// <param name="expected">How many call sites the project has.</param>
    /// <param name="timeout">How long to wait.</param>
    /// <param name="log">Receives a line per attempt.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>A task that completes when the workspace answers.</returns>
    public static Task WaitForWorkspaceAsync(WorkspaceQueryService service, CallersOfRequest request, int expected, TimeSpan timeout, Action<string> log, CancellationToken cancellationToken) =>
        Task.Run(
            async () =>
            {
                var clock = Stopwatch.StartNew();
                while (true)
                {
                    string outcome;
                    try
                    {
                        var response = await service.CallersOfAsync(request, cancellationToken).ConfigureAwait(false);
                        if (response.IsFound && response.Callers.Length >= expected)
                        {
                            log($"Workspace ready after {clock.Elapsed.TotalSeconds:0.0} s.");
                            return;
                        }

                        outcome = $"found={response.IsFound} callers={response.Callers.Length}";
                    }
                    catch (PillionRpcException ex)
                    {
                        outcome = $"{ex.Error.Code}: {ex.Error.Message} {ex.Error.Details}";
                    }

                    log($"  waiting for the workspace ({clock.Elapsed.TotalSeconds:0.0} s): {outcome}");
                    if (clock.Elapsed > timeout)
                    {
                        throw new TimeoutException($"Roslyn did not answer callers-of with {expected} call sites within {timeout.TotalSeconds:0} s (last: {outcome}).");
                    }

                    await Task.Delay(500, cancellationToken).ConfigureAwait(false);
                }
            },
            cancellationToken);

    /// <summary>
    /// Runs find-references and callers-of alternately from the thread pool (as the host's RPC calls arrive), spaced to
    /// stay under five a second, and times each one.
    /// </summary>
    /// <param name="service">The service.</param>
    /// <param name="references">The find-references request.</param>
    /// <param name="callers">The callers-of request.</param>
    /// <param name="rounds">How many pairs to run.</param>
    /// <param name="cancellationToken">Cancels the runs.</param>
    /// <returns>The runs, with the last answers of each kind.</returns>
    public static Task<(List<WorkspaceQueryRun> Runs, FindReferencesResponse References, CallersOfResponse Callers)> RunAsync(
        WorkspaceQueryService service,
        FindReferencesRequest references,
        CallersOfRequest callers,
        int rounds,
        CancellationToken cancellationToken) =>
        Task.Run(
            async () =>
            {
                var runs = new List<WorkspaceQueryRun>();
                FindReferencesResponse? lastReferences = null;
                CallersOfResponse? lastCallers = null;
                for (var i = 0; i < rounds; i++)
                {
                    var clock = Stopwatch.StartNew();
                    lastReferences = await service.FindReferencesAsync(references, cancellationToken).ConfigureAwait(false);
                    runs.Add(new WorkspaceQueryRun("find_references", clock.Elapsed, lastReferences.References.Count, false));

                    await Task.Delay(250, cancellationToken).ConfigureAwait(false);
                    clock.Restart();
                    lastCallers = await service.CallersOfAsync(callers, cancellationToken).ConfigureAwait(false);
                    runs.Add(new WorkspaceQueryRun("callers_of", clock.Elapsed, lastCallers.Callers.Length, lastCallers.Truncated));
                    await Task.Delay(250, cancellationToken).ConfigureAwait(false);
                }

                return (runs, lastReferences!, lastCallers!);
            },
            cancellationToken);

    /// <summary>
    /// Starts one callers-of query and, while it is in flight, a second one, from the thread pool. Returns how the second
    /// one ended (it must be refused at once) and the first one's answer.
    /// </summary>
    /// <param name="service">The service.</param>
    /// <param name="request">The request.</param>
    /// <param name="cancellationToken">Cancels the queries.</param>
    /// <returns>The second call's error (or <see langword="null"/> when it was not refused), how long it took, and the first call's answer.</returns>
    public static Task<(PillionError? SecondError, TimeSpan SecondTook, CallersOfResponse First, int Attempts)> RunConcurrentAsync(
        WorkspaceQueryService service,
        CallersOfRequest request,
        CancellationToken cancellationToken) =>
        Task.Run(
            async () =>
            {
                for (var attempt = 1; ; attempt++)
                {
                    await Task.Delay(1200, cancellationToken).ConfigureAwait(false); // A full rate bucket.
                    var first = service.CallersOfAsync(request, cancellationToken);
                    var clock = Stopwatch.StartNew();
                    var second = service.CallersOfAsync(request, cancellationToken);
                    PillionError? error = null;
                    try
                    {
                        await second.ConfigureAwait(false);
                    }
                    catch (PillionRpcException ex)
                    {
                        error = ex.Error;
                    }

                    var took = clock.Elapsed;
                    var answer = await first.ConfigureAwait(false);

                    // The first query cannot have ended before the second call was made unless it answered synchronously,
                    // which a real search never does; try again rather than fail on a freak.
                    if (error is not null || attempt == 3)
                    {
                        return (error, took, answer, attempt);
                    }
                }
            },
            cancellationToken);

    /// <summary>Switches the product's <see cref="UiThreadTimings"/> on, forgetting earlier timings.</summary>
    /// <returns>Whether they were on before.</returns>
    public static bool StartTimings()
    {
        var was = UiThreadTimings.Enabled;
        UiThreadTimings.Clear();
        UiThreadTimings.Enabled = true;
        return was;
    }

    /// <summary>Returns the workspace query timings and restores the switch.</summary>
    /// <param name="wasEnabled">The switch's earlier state.</param>
    /// <returns>Each query's timed stretch: name and the managed thread it started on.</returns>
    public static IReadOnlyList<(string Name, int ThreadId, TimeSpan Duration)> StopTimings(bool wasEnabled)
    {
        var timings = UiThreadTimings.Snapshot()
            .Where(t => t.Name.StartsWith(TimingPrefix, StringComparison.Ordinal))
            .Select(t => (t.Name, t.ThreadId, t.Duration))
            .ToList();
        UiThreadTimings.Enabled = wasEnabled;
        return timings;
    }

    /// <summary>The service's counters, as a line for the test output.</summary>
    /// <param name="service">The service.</param>
    /// <returns>The line.</returns>
    public static string Describe(WorkspaceQueryService service)
    {
        var s = service.Statistics;
        return $"completed={s.Completed} timedOut={s.TimedOut} refusedBusy={s.RefusedBusy} refusedRate={s.RefusedRate} refusedDisabled={s.RefusedDisabled} failed={s.Failed} uiThreadVisits={s.UiThreadVisits}";
    }

    /// <summary>Gets the number of times the query path found itself on the UI thread.</summary>
    /// <param name="service">The service.</param>
    /// <returns>The count.</returns>
    public static long UiThreadVisits(WorkspaceQueryService service) => service.Statistics.UiThreadVisits;
}
