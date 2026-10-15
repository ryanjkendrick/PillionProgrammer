using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using Pillion.Contracts;
using Task = System.Threading.Tasks.Task;

namespace Pillion.VisualStudio.IntegrationTests;

/// <summary>
/// Workspace queries (work item 2.14): find-references and callers-of answered by the extension's service from the live
/// Roslyn workspace, in a scratch C# project with one known method and three calls of it. Checks the answers, that no part
/// of a query ran on the UI thread (the product's <c>UiThreadTimings</c> and its own UI-thread check, and the dispatcher
/// operations seen by <see cref="UiThreadMonitor"/>), and that a second query while one is in flight fails at once with
/// <see cref="PillionErrorCode.Cancelled"/>. Prints each query's latency and the p50 and p95.
/// </summary>
public sealed partial class PillionPackageTests
{
    private const int QueryRounds = 10;

    [IdeFact]
    [TestOrder(12)]
    public async Task WorkspaceQueries_FindReferencesAndCallersOf_AnswerFromRoslyn_OffTheUiThread_OneInFlight()
    {
        var folder = CreateQueriesSolution();
        try
        {
            await OpenSolutionAsync(folder);
            await WaitForPackageLoadedAsync(TimeSpan.FromSeconds(60));
            await CheckWorkspaceQueriesAsync(folder);
        }
        finally
        {
            await CloseSolutionAsync();
            TryDelete(folder);
        }
    }

    // Uses Pillion.VisualStudio.Core types, so it must not run (be compiled by the JIT) before the package has loaded them.
    private async Task CheckWorkspaceQueriesAsync(string folder)
    {
        var orderService = Path.Combine(folder, "OrderService.cs");
        var program = Path.Combine(folder, "Program.cs");
        var checkout = Path.Combine(folder, "Checkout.cs");
        var service = await WorkspaceQueryChecks.GetServiceAsync(TimeSpan.FromSeconds(30), HangMitigatingCancellationToken);
        var callers = new CallersOfRequest { SymbolName = "Place", Container = "Shop.OrderService" };

        // The declaration: line 4 of OrderService.cs, "        public int Place(int quantity)".
        var references = new FindReferencesRequest { Location = new SourceLocation { FilePath = orderService, Line = 4, Column = 19 } };
        await WorkspaceQueryChecks.WaitForWorkspaceAsync(service, callers, expected: 3, TimeSpan.FromSeconds(90), Output.WriteLine, HangMitigatingCancellationToken);

        await JoinableTaskFactory.SwitchToMainThreadAsync(HangMitigatingCancellationToken);
        var uiThreadId = Environment.CurrentManagedThreadId;
        var visitsBefore = WorkspaceQueryChecks.UiThreadVisits(service);
        var timeline = Stopwatch.StartNew();
        var timingsWere = WorkspaceQueryChecks.StartTimings();
        using var monitor = UiThreadMonitor.Start(Application.Current.Dispatcher, timeline, TimeSpan.Zero);
        var windowStart = timeline.Elapsed;

        var (runs, lastReferences, lastCallers) = await WorkspaceQueryChecks.RunAsync(service, references, callers, QueryRounds, HangMitigatingCancellationToken);
        var (secondError, secondTook, first, attempts) = await WorkspaceQueryChecks.RunConcurrentAsync(service, callers, HangMitigatingCancellationToken);

        await JoinableTaskFactory.SwitchToMainThreadAsync(HangMitigatingCancellationToken);
        var windowEnd = timeline.Elapsed;
        var watched = monitor.Stop();
        var timings = WorkspaceQueryChecks.StopTimings(timingsWere);
        var visitsAfter = WorkspaceQueryChecks.UiThreadVisits(service);

        // The figures first, so a failure still reports them.
        foreach (var run in runs)
        {
            Output.WriteLine($"WorkspaceQuery: {run.Query,-16} {run.Latency.TotalMilliseconds,7:0.0} ms  results={run.Results} truncated={run.Truncated}");
        }

        foreach (var kind in new[] { "find_references", "callers_of" })
        {
            var latencies = runs.Where(r => r.Query == kind).Select(r => r.Latency.TotalMilliseconds).OrderBy(ms => ms).ToList();
            Output.WriteLine($"WorkspaceQueryLatency: {kind} n={latencies.Count} p50={Percentile(latencies, 0.50):0.0} ms p95={Percentile(latencies, 0.95):0.0} ms max={latencies.Last():0.0} ms");
        }

        Output.WriteLine($"Concurrent: second call refused with {secondError?.Code.ToString() ?? "nothing"} ({secondError?.Message}) after {secondTook.TotalMilliseconds:0.0} ms (attempt {attempts}); first answered {first.Callers.Length} call sites.");
        var onUi = timings.Where(t => t.ThreadId == uiThreadId).ToList();
        Output.WriteLine($"UiThreadTimings: {timings.Count} query stretches, {onUi.Count} on the UI thread ({onUi.Sum(t => t.Duration.TotalMilliseconds):0.0} ms); threads {string.Join(", ", timings.Select(t => t.ThreadId).Distinct())}, UI thread {uiThreadId}.");
        var queryOperations = watched.Operations
            .Where(o => o.IsPillion && o.Start >= windowStart && o.Start <= windowEnd && o.Description.IndexOf("WorkspaceQuer", StringComparison.Ordinal) >= 0)
            .ToList();
        var pillionOperations = watched.Operations.Where(o => o.IsPillion && o.Start >= windowStart && o.Start <= windowEnd).ToList();
        Output.WriteLine($"Dispatcher: {watched.Operations.Count} operations in the window, {pillionOperations.Count} of Pillion's ({pillionOperations.Sum(o => o.Duration.TotalMilliseconds):0.0} ms), {queryOperations.Count} of the workspace queries.");
        foreach (var operation in pillionOperations.Take(20))
        {
            Output.WriteLine("  " + operation);
        }

        Output.WriteLine("Service: " + WorkspaceQueryChecks.Describe(service));
        foreach (var site in lastCallers.Callers)
        {
            Output.WriteLine($"  caller {Path.GetFileName(site.FilePath)}:{site.Line} {site.CallerName} | {site.Snippet}");
        }

        foreach (var reference in lastReferences.References)
        {
            Output.WriteLine($"  reference {Path.GetFileName(reference.FilePath)}:{reference.Line}:{reference.Column}");
        }

        // The answers.
        Assert.Equal(
            [("Checkout.cs", 6, "Shop.Checkout.Run"), ("Checkout.cs", 7, "Shop.Checkout.Run"), ("Program.cs", 6, "Shop.Program.Main")],
            lastCallers.Callers.Select(c => (Path.GetFileName(c.FilePath), c.Line, c.CallerName)).OrderBy(c => c.Item1, StringComparer.Ordinal).ThenBy(c => c.Line));
        Assert.All(lastCallers.Callers, c => Assert.True(SamePath(c.FilePath, program) || SamePath(c.FilePath, checkout), c.FilePath));
        Assert.True(lastCallers.IsFound);
        Assert.False(lastCallers.Truncated);
        Assert.All(lastCallers.Callers, c => Assert.False(c.IsApproximate));
        Assert.Equal("var total = 0; total += orders.Place(2);", lastCallers.Callers.Single(c => SamePath(c.FilePath, checkout) && c.Line == 6).Snippet);
        Assert.Equal(3, lastReferences.References.Count);
        Assert.Contains(lastReferences.References, r => SamePath(r.FilePath, program) && r.Line == 6);
        Assert.Equal(2, lastReferences.References.Count(r => SamePath(r.FilePath, checkout)));

        // The budgets: every query answered within its 2 s budget (plus scheduling slack), none was cut short.
        Assert.All(runs, r => Assert.True(r.Latency < TimeSpan.FromSeconds(2.5), $"{r.Query} took {r.Latency.TotalMilliseconds:0} ms."));
        Assert.All(runs, r => Assert.False(r.Truncated));

        // Off the UI thread: the product's check, its timings, and the dispatcher.
        Assert.Equal(visitsBefore, visitsAfter);
        Assert.Equal((QueryRounds * 2) + attempts, timings.Count);
        Assert.Empty(onUi);
        Assert.Empty(queryOperations);

        // One in flight: the second call failed at once with Cancelled; the first one answered.
        Assert.NotNull(secondError);
        Assert.Equal(PillionErrorCode.Cancelled, secondError!.Code);
        Assert.True(secondTook < TimeSpan.FromMilliseconds(100), $"The refusal took {secondTook.TotalMilliseconds:0} ms.");
        Assert.Equal(3, first.Callers.Length);
    }

    private static double Percentile(System.Collections.Generic.List<double> sorted, double fraction)
    {
        if (sorted.Count == 0)
        {
            return 0;
        }

        var rank = (int)Math.Ceiling(fraction * sorted.Count) - 1;
        return sorted[Math.Max(0, Math.Min(sorted.Count - 1, rank))];
    }

    /// <summary>
    /// Writes a classic C# console project with <c>Shop.OrderService.Place</c> and three calls of it: one in
    /// <c>Program.Main</c> (line 6) and two in <c>Checkout.Run</c> (lines 6 and 7). Lines are zero-based.
    /// </summary>
    private static string CreateQueriesSolution()
    {
        var folder = Path.Combine(Path.GetTempPath(), "pillion-ide-tests", "queries-" + Guid.NewGuid().ToString("N").Substring(0, 12));
        Directory.CreateDirectory(folder);
        var nl = Environment.NewLine;
        File.WriteAllText(
            Path.Combine(folder, "OrderService.cs"),
            "namespace Shop" + nl
            + "{" + nl
            + "    public class OrderService" + nl
            + "    {" + nl
            + "        public int Place(int quantity)" + nl
            + "        {" + nl
            + "            return quantity * 2;" + nl
            + "        }" + nl
            + "    }" + nl
            + "}" + nl);
        File.WriteAllText(
            Path.Combine(folder, "Program.cs"),
            "namespace Shop" + nl
            + "{" + nl
            + "    internal static class Program" + nl
            + "    {" + nl
            + "        private static void Main()" + nl
            + "        {" + nl
            + "            System.Console.WriteLine(new OrderService().Place(1));" + nl
            + "        }" + nl
            + "    }" + nl
            + "}" + nl);
        File.WriteAllText(
            Path.Combine(folder, "Checkout.cs"),
            "namespace Shop" + nl
            + "{" + nl
            + "    public class Checkout" + nl
            + "    {" + nl
            + "        public int Run(OrderService orders)" + nl
            + "        {" + nl
            + "            var total = 0; total += orders.Place(2);" + nl
            + "            total += orders.Place(3);" + nl
            + "            return total;" + nl
            + "        }" + nl
            + "    }" + nl
            + "}" + nl);
        WriteScratchProject(folder, ["OrderService.cs", "Program.cs", "Checkout.cs"]);
        return folder;
    }
}
