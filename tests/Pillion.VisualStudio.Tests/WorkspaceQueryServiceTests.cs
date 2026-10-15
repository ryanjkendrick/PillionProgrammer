using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Pillion.Contracts;
using Pillion.VisualStudio.Core;
using Pillion.VisualStudio.Core.WorkspaceQueries;

namespace Pillion.VisualStudio.Tests;

#pragma warning disable VSTHRD003 // The tests complete these tasks themselves; there is no main thread here.

/// <summary>
/// The VSIX's workspace query budgets (work item 2.14, design section 13): the flag, one query in flight, five a second,
/// the 2 s budget with partial results, and that no part of a query runs on the UI thread. The Roslyn calls are behind
/// <see cref="IWorkspaceQueryEngine"/>; the real engine is covered by the IDE tests.
/// </summary>
public sealed class WorkspaceQueryServiceTests
{
    private static readonly TimeSpan ShortBudget = TimeSpan.FromMilliseconds(300);

    [Fact]
    public async Task FlagOff_RefusesWithPolicyDenied_WithoutCallingTheEngine()
    {
        var engine = new FakeEngine();
        var service = new WorkspaceQueryService(engine, () => false);

        var callers = await Assert.ThrowsAsync<PillionRpcException>(() => service.CallersOfAsync(Callers("Place"), CancellationToken.None));
        var references = await Assert.ThrowsAsync<PillionRpcException>(() => service.FindReferencesAsync(References(), CancellationToken.None));

        Assert.Equal(PillionErrorCode.PolicyDenied, callers.Error.Code);
        Assert.Contains("workspaceQueries", callers.Error.Message, StringComparison.Ordinal);
        Assert.Equal(PillionErrorCode.PolicyDenied, references.Error.Code);
        Assert.Equal(0, engine.Calls);
        Assert.Equal(2, service.Statistics.RefusedDisabled);
    }

    [Fact]
    public async Task FlagChanges_ApplyToTheNextCall()
    {
        var enabled = false;
        var service = new WorkspaceQueryService(new FakeEngine(), () => enabled);

        await Assert.ThrowsAsync<PillionRpcException>(() => service.CallersOfAsync(Callers("Place"), CancellationToken.None));
        enabled = true;
        var response = await service.CallersOfAsync(Callers("Place"), CancellationToken.None);

        Assert.True(response.IsFound);
    }

    [Fact]
    public async Task SecondCall_WhileOneIsInFlight_FailsAtOnceWithCancelled()
    {
        var engine = new FakeEngine { Gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously) };
        var service = new WorkspaceQueryService(engine, () => true);

        var first = service.CallersOfAsync(Callers("Place"), CancellationToken.None);
        await engine.Started.Task;
        var started = DateTime.UtcNow;
        var second = await Assert.ThrowsAsync<PillionRpcException>(() => service.FindReferencesAsync(References(), CancellationToken.None));
        var refusedAfter = DateTime.UtcNow - started;
        engine.Gate.SetResult(true);
        var answer = await first;
        var third = await service.CallersOfAsync(Callers("Place"), CancellationToken.None);

        Assert.Equal(PillionErrorCode.Cancelled, second.Error.Code);
        Assert.Equal(WorkspaceQueryService.BusyMessage, second.Error.Message);
        Assert.False(string.IsNullOrEmpty(second.Error.RetryHint));
        Assert.True(refusedAfter < TimeSpan.FromMilliseconds(100), $"The refusal took {refusedAfter.TotalMilliseconds:0} ms.");
        Assert.True(answer.IsFound);
        Assert.True(third.IsFound);
        Assert.Equal(1, service.Statistics.RefusedBusy);
        Assert.Equal(2, service.Statistics.Completed);
    }

    [Fact]
    public async Task RateLimit_AllowsFiveAtOnce_ThenOnePerFifthOfASecond()
    {
        var clock = TimeSpan.FromSeconds(100);
        var service = new WorkspaceQueryService(new FakeEngine(), () => true, options: new WorkspaceQueryOptions { Clock = () => clock });

        for (var i = 0; i < 5; i++)
        {
            Assert.True((await service.CallersOfAsync(Callers("Place"), CancellationToken.None)).IsFound);
        }

        var refused = await Assert.ThrowsAsync<PillionRpcException>(() => service.CallersOfAsync(Callers("Place"), CancellationToken.None));
        clock += TimeSpan.FromMilliseconds(100);
        await Assert.ThrowsAsync<PillionRpcException>(() => service.CallersOfAsync(Callers("Place"), CancellationToken.None));
        clock += TimeSpan.FromMilliseconds(110);
        var admitted = await service.CallersOfAsync(Callers("Place"), CancellationToken.None);
        await Assert.ThrowsAsync<PillionRpcException>(() => service.CallersOfAsync(Callers("Place"), CancellationToken.None));
        clock += TimeSpan.FromSeconds(5);
        for (var i = 0; i < 5; i++)
        {
            await service.FindReferencesAsync(References(), CancellationToken.None);
        }

        Assert.Equal(PillionErrorCode.Cancelled, refused.Error.Code);
        Assert.Equal(WorkspaceQueryService.RateMessage, refused.Error.Message);
        Assert.Contains("ms", refused.Error.RetryHint, StringComparison.Ordinal);
        Assert.True(admitted.IsFound);
        Assert.Equal(3, service.Statistics.RefusedRate);
        Assert.Equal(11, service.Statistics.Completed);
    }

    [Fact]
    public async Task ARefusedCall_DoesNotSpendARateToken()
    {
        var clock = TimeSpan.FromSeconds(1);
        var engine = new FakeEngine { Gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously) };
        var service = new WorkspaceQueryService(engine, () => true, options: new WorkspaceQueryOptions { Clock = () => clock });

        var first = service.CallersOfAsync(Callers("Place"), CancellationToken.None);
        await engine.Started.Task;
        for (var i = 0; i < 10; i++)
        {
            await Assert.ThrowsAsync<PillionRpcException>(() => service.CallersOfAsync(Callers("Place"), CancellationToken.None));
        }

        engine.Gate.SetResult(true);
        await first;
        engine.Gate = null;
        for (var i = 0; i < 4; i++)
        {
            await service.CallersOfAsync(Callers("Place"), CancellationToken.None);
        }

        Assert.Equal(10, service.Statistics.RefusedBusy);
        Assert.Equal(0, service.Statistics.RefusedRate);
    }

    [Fact]
    public async Task Budget_StopsTheSearch_AndReturnsWhatItFound_MarkedTruncated()
    {
        var engine = new FakeEngine { CallSites = [Site("b.cs", 9), Site("a.cs", 3)], HangAfterResults = true };
        var service = new WorkspaceQueryService(engine, () => true, options: new WorkspaceQueryOptions { Budget = ShortBudget });

        var started = DateTime.UtcNow;
        var response = await service.CallersOfAsync(Callers("Place"), CancellationToken.None);
        var elapsed = DateTime.UtcNow - started;

        Assert.True(response.IsFound);
        Assert.True(response.Truncated);
        Assert.Equal(["a.cs:3", "b.cs:9"], response.Callers.Select(c => c.FilePath + ":" + c.Line));
        Assert.True(elapsed >= ShortBudget - TimeSpan.FromMilliseconds(50), $"Answered after {elapsed.TotalMilliseconds:0} ms.");
        Assert.True(elapsed < ShortBudget + TimeSpan.FromSeconds(1), $"Answered after {elapsed.TotalMilliseconds:0} ms.");
        Assert.Equal(1, service.Statistics.TimedOut);
        Assert.Same(engine.SawCancellation.Task, await Task.WhenAny(engine.SawCancellation.Task, Task.Delay(TimeSpan.FromSeconds(5))));
    }

    [Fact]
    public async Task Budget_FindReferences_ReturnsThePartialList()
    {
        var engine = new FakeEngine { References = [Location("x.cs", 4, 2), Location("x.cs", 1, 8)], HangAfterResults = true };
        var service = new WorkspaceQueryService(engine, () => true, options: new WorkspaceQueryOptions { Budget = ShortBudget });

        var response = await service.FindReferencesAsync(References(), CancellationToken.None);

        Assert.Equal(["x.cs:1:8", "x.cs:4:2"], response.References.Select(r => $"{r.FilePath}:{r.Line}:{r.Column}"));
        Assert.Equal(1, service.Statistics.TimedOut);
    }

    [Fact]
    public async Task Budget_AnEngineThatIgnoresCancellation_StillAnswersOnTime_AndKeepsTheSlotUntilItEnds()
    {
        var engine = new FakeEngine
        {
            CallSites = [Site("a.cs", 1)],
            Gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously),
            IgnoreCancellation = true,
        };
        var service = new WorkspaceQueryService(engine, () => true, options: new WorkspaceQueryOptions { Budget = ShortBudget });

        var response = await service.CallersOfAsync(Callers("Place"), CancellationToken.None);
        var busy = await Assert.ThrowsAsync<PillionRpcException>(() => service.CallersOfAsync(Callers("Place"), CancellationToken.None));
        engine.Gate.SetResult(true);
        await Wait.UntilAsync(() => engine.Finished, "the engine to finish", 5000);
        engine.Gate = null;
        var after = await RetryWhileBusyAsync(() => service.CallersOfAsync(Callers("Place"), CancellationToken.None));

        Assert.True(response.Truncated);
        Assert.Single(response.Callers);
        Assert.Equal(WorkspaceQueryService.BusyMessage, busy.Error.Message);
        Assert.True(after.IsFound);
    }

    [Fact]
    public async Task ResultCap_TruncatesAtMaxResults_AndDeduplicatesALine()
    {
        var engine = new FakeEngine { CallSites = [Site("a.cs", 1), Site("a.cs", 1), Site("a.cs", 2), Site("a.cs", 3)] };
        var service = new WorkspaceQueryService(engine, () => true);

        var request = Callers("Place");
        request.MaxResults = 2;
        var response = await service.CallersOfAsync(request, CancellationToken.None);

        Assert.True(response.Truncated);
        Assert.Equal([1, 2], response.Callers.Select(c => c.Line));
        Assert.Equal(0, service.Statistics.TimedOut);
    }

    [Fact]
    public async Task CompleteAnswer_IsNotTruncated_AndPassesTheRequestThrough()
    {
        var engine = new FakeEngine { CallSites = [Site("a.cs", 1)] };
        var service = new WorkspaceQueryService(engine, () => true);

        var response = await service.CallersOfAsync(
            new CallersOfRequest { SymbolName = " Place ", Container = "Shop.Orders", FilePath = "o.cs", Line = 12 },
            CancellationToken.None);

        Assert.False(response.Truncated);
        Assert.True(response.IsFound);
        Assert.Equal("Place", engine.LastCallers!.SymbolName);
        Assert.Equal("Shop.Orders", engine.LastCallers.Container);
        Assert.Equal(12, engine.LastCallers.Line);
        Assert.Equal(WorkspaceQueryService.DefaultMaxCallers, engine.LastCallers.MaxResults);
    }

    [Fact]
    public async Task UnknownSymbol_IsNotFound_AndEmptyRequestsAreAnsweredWithoutAQuery()
    {
        var engine = new FakeEngine { Resolves = false };
        var service = new WorkspaceQueryService(engine, () => true);

        var unknown = await service.CallersOfAsync(Callers("Nothing"), CancellationToken.None);
        var empty = await service.CallersOfAsync(new CallersOfRequest(), CancellationToken.None);
        var noLocation = await service.FindReferencesAsync(new FindReferencesRequest(), CancellationToken.None);

        Assert.False(unknown.IsFound);
        Assert.Empty(unknown.Callers);
        Assert.False(empty.IsFound);
        Assert.Empty(noLocation.References);
        Assert.Equal(1, engine.Calls);
    }

    [Fact]
    public async Task CallerCancellation_IsPassedOn_AndReleasesTheSlot()
    {
        var engine = new FakeEngine { Gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously) };
        var service = new WorkspaceQueryService(engine, () => true);
        using var cancel = new CancellationTokenSource();

        var call = service.CallersOfAsync(Callers("Place"), cancel.Token);
        await engine.Started.Task;
        cancel.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
        engine.Gate = null;
        var next = await RetryWhileBusyAsync(() => service.CallersOfAsync(Callers("Place"), CancellationToken.None));
        Assert.True(next.IsFound);
    }

    [Fact]
    public async Task EngineRefusal_IsPassedOn_AndReleasesTheSlot()
    {
        var engine = new FakeEngine
        {
            Throw = new PillionRpcException(new PillionError { Code = PillionErrorCode.Cancelled, Message = "The Roslyn workspace is not available." }),
        };
        var service = new WorkspaceQueryService(engine, () => true);

        var ex = await Assert.ThrowsAsync<PillionRpcException>(() => service.CallersOfAsync(Callers("Place"), CancellationToken.None));
        engine.Throw = null;
        var next = await RetryWhileBusyAsync(() => service.CallersOfAsync(Callers("Place"), CancellationToken.None));

        Assert.Equal(PillionErrorCode.Cancelled, ex.Error.Code);
        Assert.True(next.IsFound);
        Assert.Equal(1, service.Statistics.Failed);
    }

    [Fact]
    public async Task CalledFromTheUiThread_NoPartOfTheQueryRunsThere()
    {
        using var pump = new MainThreadPump();
        var engine = new FakeEngine { CallSites = [Site("a.cs", 1)], References = [Location("a.cs", 2, 0)], YieldFirst = true };
        var service = new WorkspaceQueryService(
            engine,
            () => true,
            options: new WorkspaceQueryOptions { IsUiThread = () => Environment.CurrentManagedThreadId == pump.ThreadId });
        var timingsWere = UiThreadTimings.Enabled;
        UiThreadTimings.Clear();
        UiThreadTimings.Enabled = true;
        try
        {
            var callers = await await pump.InvokeAsync(() => service.CallersOfAsync(Callers("Place"), CancellationToken.None));
            var references = await await pump.InvokeAsync(() => service.FindReferencesAsync(References(), CancellationToken.None));

            Assert.Single(callers.Callers);
            Assert.Single(references.References);
            Assert.Equal(0, service.Statistics.UiThreadVisits);
            Assert.Equal(0, pump.WorkItems); // Nothing was posted back to the "UI thread".
            Assert.All(engine.Threads, id => Assert.NotEqual(pump.ThreadId, id));
            var timings = UiThreadTimings.Snapshot().Where(t => t.Name.StartsWith("workspace.query.", StringComparison.Ordinal)).ToList();
            Assert.Equal(["workspace.query.callers_of", "workspace.query.find_references"], timings.Select(t => t.Name));
            Assert.All(timings, t => Assert.NotEqual(pump.ThreadId, t.ThreadId));
        }
        finally
        {
            UiThreadTimings.Enabled = timingsWere;
        }
    }

    [Fact]
    public async Task UiThreadCheck_CountsAVisit()
    {
        var service = new WorkspaceQueryService(new FakeEngine(), () => true, options: new WorkspaceQueryOptions { IsUiThread = () => true });

        await service.CallersOfAsync(Callers("Place"), CancellationToken.None);

        Assert.True(service.Statistics.UiThreadVisits > 0);
    }

    [Fact]
    public void TokenBucket_RefillsAtItsRate_UpToItsCapacity()
    {
        var clock = TimeSpan.Zero;
        var bucket = new TokenBucket(2, 4, () => clock);

        Assert.True(bucket.TryTake(out _));
        Assert.True(bucket.TryTake(out _));
        Assert.False(bucket.TryTake(out var retry));
        Assert.Equal(250, retry.TotalMilliseconds, 1);
        clock += TimeSpan.FromSeconds(10);
        Assert.True(bucket.TryTake(out _));
        Assert.True(bucket.TryTake(out _));
        Assert.False(bucket.TryTake(out _));
    }

    [Fact]
    public void HostFeatureFlag_ReadsOnlyAnExplicitFalseAsOff()
    {
        Assert.True(HostFeatureFlag.ReadFlag(null, "workspaceQueries"));
        Assert.True(HostFeatureFlag.ReadFlag("{\"chat\":false}", "workspaceQueries"));
        Assert.True(HostFeatureFlag.ReadFlag("{\"workspaceQueries\":true}", "workspaceQueries"));
        Assert.False(HostFeatureFlag.ReadFlag("{\"workspaceQueries\":false}", "workspaceQueries"));
        Assert.True(HostFeatureFlag.ReadFlag("{\"workspaceQueries\":\"no\"}", "workspaceQueries"));
    }

    private static CallersOfRequest Callers(string name) => new() { SymbolName = name };

    private static FindReferencesRequest References() => new() { Location = new SourceLocation { FilePath = "x.cs", Line = 3, Column = 5 } };

    private static CallSite Site(string path, int line) => new() { FilePath = path, Line = line, Snippet = "Place();", CallerName = "Shop.Checkout.Run" };

    private static SourceLocation Location(string path, int line, int column) => new() { FilePath = path, Line = line, Column = column };

    /// <summary>Retries while the slot of a search that just ended has not been released yet (it is released by a continuation).</summary>
    private static async Task<T> RetryWhileBusyAsync<T>(Func<Task<T>> call)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await call().ConfigureAwait(false);
            }
            catch (PillionRpcException ex) when (ex.Error.Message == WorkspaceQueryService.BusyMessage && attempt < 50)
            {
                await Task.Delay(20).ConfigureAwait(false);
            }
        }
    }

    private sealed class FakeEngine : IWorkspaceQueryEngine
    {
        private int _calls;

        public TaskCompletionSource<bool>? Gate { get; set; }

        public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> SawCancellation { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public CallSite[] CallSites { get; set; } = [];

        public SourceLocation[] References { get; set; } = [];

        public bool Resolves { get; set; } = true;

        public bool HangAfterResults { get; set; }

        public bool IgnoreCancellation { get; set; }

        public bool YieldFirst { get; set; }

        public Exception? Throw { get; set; }

        public bool Finished { get; private set; }

        public CallersOfRequest? LastCallers { get; private set; }

        public ConcurrentBag<int> Threads { get; } = [];

        public int Calls => Volatile.Read(ref _calls);

        public Task FindReferencesAsync(SourceLocation location, WorkspaceQueryResults<SourceLocation> results, CancellationToken cancellationToken) =>
            RunAsync(results, References, cancellationToken);

        public Task CallersOfAsync(CallersOfRequest request, WorkspaceQueryResults<CallSite> results, CancellationToken cancellationToken)
        {
            LastCallers = request;
            return RunAsync(results, CallSites, cancellationToken);
        }

        private async Task RunAsync<T>(WorkspaceQueryResults<T> results, T[] items, CancellationToken cancellationToken)
            where T : class
        {
            Interlocked.Increment(ref _calls);
            Threads.Add(Environment.CurrentManagedThreadId);
            Finished = false;
            try
            {
                if (YieldFirst)
                {
                    await Task.Yield();
                    Threads.Add(Environment.CurrentManagedThreadId);
                }

                Started.TrySetResult(true);
                if (Throw is { } exception)
                {
                    throw exception;
                }

                if (!Resolves)
                {
                    return;
                }

                results.MarkFound();
                foreach (var item in items)
                {
                    results.TryAdd(item);
                }

                if (Gate is { } gate)
                {
                    await (IgnoreCancellation ? gate.Task : gate.Task.WithCancellationAsync(cancellationToken)).ConfigureAwait(false);
                }

                if (HangAfterResults)
                {
                    try
                    {
                        await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        SawCancellation.TrySetResult(true);
                        throw;
                    }
                }
            }
            finally
            {
                Finished = true;
            }
        }
    }
}

/// <summary>Cancellation for a task that does not take a token.</summary>
internal static class TaskCancellationExtensions
{
    public static async Task WithCancellationAsync(this Task task, CancellationToken cancellationToken)
    {
        var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (cancellationToken.Register(() => cancelled.TrySetResult(true)))
        {
            if (await Task.WhenAny(task, cancelled.Task).ConfigureAwait(false) != task)
            {
                throw new OperationCanceledException(cancellationToken);
            }
        }

        await task.ConfigureAwait(false);
    }
}
