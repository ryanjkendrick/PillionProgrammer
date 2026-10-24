using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;
using Pillion.Contracts;
using Pillion.VisualStudio.Core;

namespace Pillion.VisualStudio.Tests;

/// <summary>The contract 1.3 <c>indexStatusChanged</c> notification on the VSIX side: the session raises it and the supervisor forwards the current session's.</summary>
public sealed class IndexStatusNotificationTests : IDisposable
{
    private readonly JoinableTaskContext _context = Task.Run(() => new JoinableTaskContext()).Result;

    public void Dispose() => _context.Dispose();

    [Fact]
    public void TheMethodName_IsIndexStatusChanged() =>
        Assert.Equal("indexStatusChanged", HostRpcSession.IndexStatusChangedMethod);

    [Fact]
    public void AMissingStatus_IsReadAsAnEmptyOne()
    {
        var args = new HostIndexStatusChangedEventArgs(null);

        Assert.Equal(IndexPhase.Idle, args.Status.Phase);
        Assert.Empty(args.Status.Roots);
    }

    [Fact]
    public async Task TheSupervisor_ForwardsTheCurrentSessionsIndexStatus_AndIgnoresAnEndedOne()
    {
        var launcher = new FakeLauncher { InitialState = HostState.Ready };
        using var supervisor = new HostSupervisor(launcher, _context.Factory, new PillionLog(directory: null), new FakeClock());
        var reported = new List<IndexStatus>();
        supervisor.IndexStatusChanged += (_, e) =>
        {
            lock (reported)
            {
                reported.Add(e.Status);
            }
        };
        supervisor.IndexStatusChanged += (_, _) => throw new InvalidOperationException("A faulty subscriber must not break the others.");

        supervisor.Start();
        await Wait.UntilAsync(() => supervisor.HostProcessId is not null, "connected");
        var first = launcher.LastSession;
        first.ReportIndexStatus(new IndexStatus { Phase = IndexPhase.Hashing, Progress = new IndexProgress { FilesDone = 1, FilesTotal = 4 } });

        lock (reported)
        {
            Assert.Equal(IndexPhase.Hashing, Assert.Single(reported).Phase);
        }

        first.Crash();
        await Wait.UntilAsync(() => first.Disposed, "the session ended");
        first.ReportIndexStatus(new IndexStatus { Phase = IndexPhase.Idle });

        lock (reported)
        {
            Assert.Single(reported);
        }
    }
}
