using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;
using Pillion.Contracts;
using Pillion.VisualStudio.Core;

namespace Pillion.VisualStudio.Tests;

public sealed class HostSupervisorTests : IDisposable
{
    private readonly JoinableTaskContext _context = new();
    private readonly FakeClock _clock = new();
    private readonly FakeLauncher _launcher = new();
    private readonly List<HostStateChangedEventArgs> _changes = [];
    private readonly HostSupervisor _supervisor;

    public HostSupervisorTests()
    {
        _supervisor = new HostSupervisor(_launcher, _context.Factory, new PillionLog(directory: null), _clock);
        _supervisor.StateChanged += (_, e) =>
        {
            lock (_changes)
            {
                _changes.Add(e);
            }
        };
    }

    private IReadOnlyList<HostStateChangedEventArgs> Changes
    {
        get
        {
            lock (_changes)
            {
                return [.. _changes];
            }
        }
    }

    private HostStateChangedEventArgs LastChange
    {
        get
        {
            var changes = Changes;
            return changes[changes.Count - 1];
        }
    }

    public void Dispose()
    {
        _supervisor.Dispose();
        _context.Dispose();
    }

    [Fact]
    public async Task Connectivity_FollowsTheSession_AndIsForgottenWhenItEnds()
    {
        var reported = new List<HostConnectivityChangedEventArgs>();
        _supervisor.ConnectivityChanged += (_, e) =>
        {
            lock (reported)
            {
                reported.Add(e);
            }
        };
        _launcher.InitialState = HostState.Starting;

        _supervisor.Start();
        await Wait.UntilAsync(() => _supervisor.HostProcessId is not null, "connected");
        Assert.Null(_supervisor.Connectivity);

        var first = _launcher.LastSession;
        first.ReportConnectivity(ConnectivityState.Online);
        Assert.Equal(ConnectivityState.Online, _supervisor.Connectivity);
        first.ReportConnectivity(ConnectivityState.LocalOnly);
        first.ReportConnectivity(ConnectivityState.LocalOnly); // No change, no event.
        Assert.Equal(ConnectivityState.LocalOnly, _supervisor.Connectivity);

        first.Crash();
        await Wait.UntilAsync(() => _supervisor.Connectivity is null, "connectivity forgotten with the session");

        // The property is cleared just before the event is raised; wait for the event itself.
        await Wait.UntilAsync(
            () =>
            {
                lock (reported)
                {
                    return reported.Count >= 3;
                }
            },
            "connectivity change reported when the session ended");
        lock (reported)
        {
            Assert.Equal(
                [(null, ConnectivityState.Online), (ConnectivityState.Online, ConnectivityState.LocalOnly), (ConnectivityState.LocalOnly, (ConnectivityState?)null)],
                reported.Select(e => (e.Previous, e.Current)));
        }
    }

    [Fact]
    public async Task Connectivity_ReportedBeforeTheSessionIsAdopted_IsPickedUp()
    {
        _launcher.Behaviour = (launch, _) =>
        {
            var session = _launcher.CreateSession(launch);
            session.ReportConnectivity(ConnectivityState.Offline); // During the handshake, before the supervisor subscribes.
            return Task.FromResult<IHostSession>(session);
        };

        _supervisor.Start();
        await Wait.UntilAsync(() => _supervisor.Connectivity == ConnectivityState.Offline, "the early report is used");
    }

    [Fact]
    public async Task Start_LaunchesAndMirrorsTheHostState()
    {
        _launcher.InitialState = HostState.Starting;

        _supervisor.Start();
        await Wait.UntilAsync(() => _supervisor.HostProcessId is not null, "connected");
        Assert.Equal(HostState.Starting, _supervisor.State);

        _launcher.LastSession.Report(HostState.Ready);
        await _supervisor.WhenReadyAsync(CancellationToken.None).WithTimeout(TimeSpan.FromSeconds(10));

        Assert.Equal(HostState.Ready, _supervisor.State);
        Assert.Equal(1, _launcher.Launches);
        Assert.Equal(HostState.Ready, LastChange.State);
        Assert.Equal(HostState.Starting, LastChange.PreviousState);
    }

    [Fact]
    public async Task Start_ReturnsBeforeTheHostIsUp()
    {
        var release = new TaskCompletionSource<IHostSession>();
#pragma warning disable VSTHRD003 // The test completes this task itself; there is no main thread here.
        _launcher.Behaviour = (_, _) => release.Task;
#pragma warning restore VSTHRD003

        _supervisor.Start();

        Assert.Equal(HostState.Starting, _supervisor.State);
        var ex = Assert.Throws<PillionRpcException>(() => _supervisor.GetProxy<IHostLifecycleService>());
        Assert.Equal(PillionErrorCode.HostRestarting, ex.Error.Code);

        release.SetResult(_launcher.CreateSession(1));
        await _supervisor.WhenReadyAsync(CancellationToken.None).WithTimeout(TimeSpan.FromSeconds(10));
        Assert.NotNull(_supervisor.GetProxy<IHostLifecycleService>());
    }

    [Fact]
    public async Task WhenReadyAsync_CanBeCancelled()
    {
        _launcher.Behaviour = (_, ct) => Task.Delay(Timeout.Infinite, ct).ContinueWith<IHostSession>(_ => throw new OperationCanceledException(), TaskScheduler.Default);
        _supervisor.Start();
        using var cancellation = new CancellationTokenSource(50);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _supervisor.WhenReadyAsync(cancellation.Token));
    }

    [Fact]
    public async Task Degraded_IsMirroredAndCountsAsUsable()
    {
        _launcher.InitialState = HostState.Degraded;

        _supervisor.Start();
        await _supervisor.WhenReadyAsync(CancellationToken.None).WithTimeout(TimeSpan.FromSeconds(10));

        Assert.Equal(HostState.Degraded, _supervisor.State);
        Assert.Equal("Pillion: Degraded", HostStatusText.Format(LastChange));
    }

    [Fact]
    public async Task Crash_RestartsAfterOneSecondAndRecovers()
    {
        _supervisor.Start();
        await Wait.UntilAsync(() => _supervisor.State == HostState.Ready, "ready");
        var first = _launcher.LastSession;

        first.Crash("Pillion Host exited with code 7");
        await Wait.UntilAsync(() => _launcher.Launches == 2 && _supervisor.State == HostState.Ready, "restarted");

        Assert.True(first.Disposed);
        Assert.Equal([TimeSpan.FromSeconds(1)], _clock.Delays);
        var restarting = Assert.Single(Changes, c => c.State == HostState.Restarting);
        Assert.Equal(1, restarting.RestartAttempt);
        Assert.Equal(3, restarting.MaxRestartAttempts);
        Assert.Equal("Pillion Host exited with code 7", restarting.Reason);
        Assert.Equal("Pillion: Restarting (1/3)", HostStatusText.Format(restarting));
        Assert.Equal(1002, _supervisor.HostProcessId);
    }

    [Fact]
    public async Task ThreeCrashesWithinFiveMinutes_EnterFailed()
    {
        _supervisor.Start();
        for (var launch = 1; launch <= 3; launch++)
        {
            await Wait.UntilAsync(() => _launcher.Launches == launch && _supervisor.State == HostState.Ready, "launch " + launch);
            _launcher.LastSession.Crash();
        }

        await Wait.UntilAsync(() => _supervisor.State == HostState.Failed, "failed");

        Assert.Equal(3, _launcher.Launches);
        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5)], _clock.Delays);
        Assert.Equal(
            ["Pillion: Restarting (1/3)", "Pillion: Restarting (2/3)"],
            Changes.Where(c => c.State == HostState.Restarting).Select(HostStatusText.Format));
        Assert.Contains("3 times within 5 minutes", _supervisor.FailureReason);
        Assert.Equal(_supervisor.FailureReason, LastChange.Reason);
        Assert.Equal("Pillion: Failed, see Tools > Pillion: Open Logs", HostStatusText.Format(LastChange));
        Assert.All(_launcher.Sessions, s => Assert.True(s.Disposed));

        await Task.Delay(100);
        Assert.Equal(3, _launcher.Launches);
    }

    [Fact]
    public async Task CrashesSpreadOut_KeepRestartingWithTheThirtySecondBackoff()
    {
        _supervisor.Start();
        for (var launch = 1; launch <= 4; launch++)
        {
            await Wait.UntilAsync(() => _launcher.Launches == launch && _supervisor.State == HostState.Ready, "launch " + launch);
            _clock.Advance(TimeSpan.FromMinutes(4));
            _launcher.LastSession.Crash();
        }

        await Wait.UntilAsync(() => _launcher.Launches == 5 && _supervisor.State == HostState.Ready, "launch 5");

        Assert.Equal(
            [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30)],
            _clock.Delays);
    }

    [Fact]
    public async Task RetryableLaunchFailures_CountAsCrashes()
    {
        _launcher.Behaviour = (launch, _) => Task.FromException<IHostSession>(new HostLaunchException("Pillion Host exited with code 2 before accepting a connection."));

        _supervisor.Start();
        await Wait.UntilAsync(() => _supervisor.State == HostState.Failed, "failed");

        Assert.Equal(3, _launcher.Launches);
        Assert.Contains("exited with code 2", _supervisor.FailureReason);
    }

    [Fact]
    public async Task NonRetryableLaunchFailure_EntersFailedWithoutRetrying()
    {
        _launcher.Behaviour = (_, _) => Task.FromException<IHostSession>(
            new HostLaunchException("Pillion Host rejected the connection: contract mismatch", isRetryable: false));

        _supervisor.Start();
        await Wait.UntilAsync(() => _supervisor.State == HostState.Failed, "failed");
        await Task.Delay(100);

        Assert.Equal(1, _launcher.Launches);
        Assert.Empty(_clock.Delays);
        Assert.Equal("Pillion Host rejected the connection: contract mismatch", _supervisor.FailureReason);
        Assert.DoesNotContain(Changes, c => c.State == HostState.Restarting);
    }

    [Fact]
    public async Task HostReportingFailed_EntersFailedAndClosesTheSession()
    {
        _supervisor.Start();
        await Wait.UntilAsync(() => _supervisor.State == HostState.Ready, "ready");

        _launcher.LastSession.Report(HostState.Failed);
        await Wait.UntilAsync(() => _supervisor.State == HostState.Failed, "failed");

        Assert.True(_launcher.LastSession.Disposed);
        Assert.Equal(1, _launcher.Launches);
        Assert.Contains("reported that it has failed", _supervisor.FailureReason);
        Assert.Throws<PillionRpcException>(() => _supervisor.GetProxy<IHostLifecycleService>());
    }

    [Fact]
    public async Task Retry_AfterFailed_ForgetsCrashesAndRelaunches()
    {
        _supervisor.Start();
        for (var launch = 1; launch <= 3; launch++)
        {
            await Wait.UntilAsync(() => _launcher.Launches == launch && _supervisor.State == HostState.Ready, "launch " + launch);
            _launcher.LastSession.Crash();
        }

        await Wait.UntilAsync(() => _supervisor.State == HostState.Failed, "failed");

        await _supervisor.RetryAsync();
        await _supervisor.WhenReadyAsync(CancellationToken.None).WithTimeout(TimeSpan.FromSeconds(10));
        Assert.Equal(4, _launcher.Launches);
        Assert.Null(_supervisor.FailureReason);

        // The crash counter was reset: the next crash is attempt 1 again, not a fourth strike.
        _launcher.LastSession.Crash();
        await Wait.UntilAsync(() => _launcher.Launches == 5 && _supervisor.State == HostState.Ready, "restarted after retry");
        Assert.Equal(1, Changes.Last(c => c.State == HostState.Restarting).RestartAttempt);
    }

    [Fact]
    public async Task Retry_WhileRunning_ReplacesTheHost()
    {
        _supervisor.Start();
        await Wait.UntilAsync(() => _supervisor.State == HostState.Ready, "ready");
        var first = _launcher.LastSession;

        await _supervisor.RetryAsync();
        await Wait.UntilAsync(() => _launcher.Launches == 2 && _supervisor.State == HostState.Ready, "relaunched");

        Assert.True(first.Disposed);
        Assert.Empty(_clock.Delays);
    }

    [Fact]
    public async Task Shutdown_AsksTheHostToExitAndStopsSupervising()
    {
        _supervisor.Start();
        await Wait.UntilAsync(() => _supervisor.State == HostState.Ready, "ready");
        var session = _launcher.LastSession;

        await _supervisor.ShutdownAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(TimeSpan.FromSeconds(2), session.ShutdownTimeout);
        Assert.True(session.Disposed);
        Assert.Throws<ObjectDisposedException>(() => _supervisor.GetProxy<IHostLifecycleService>());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => _supervisor.WhenReadyAsync(CancellationToken.None));
        await Task.Delay(100);
        Assert.Equal(1, _launcher.Launches);
        Assert.DoesNotContain(Changes, c => c.State == HostState.Restarting);
    }

    [Fact]
    public async Task Shutdown_WhileLaunching_CancelsTheLaunch()
    {
        var cancelled = new TaskCompletionSource<bool>();
        _launcher.Behaviour = async (_, ct) =>
        {
            // Observe the cancellation through the awaited delay, not through a ct.Register callback of our own: Task.Delay
            // registers on the same token, its callback can run first and resume this method inline on the cancelling
            // thread, and disposing our registration at that point stops our callback from ever running.
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
                cancelled.TrySetResult(true);
                throw;
            }

            throw new InvalidOperationException("unreachable");
        };
        _supervisor.Start();
        await Wait.UntilAsync(() => _launcher.Launches == 1, "launching");

        await _supervisor.ShutdownAsync(TimeSpan.FromSeconds(2)).WithTimeout(TimeSpan.FromSeconds(10));

        Assert.True(cancelled.Task.IsCompleted);
    }

    [Fact]
    public async Task StateChangedHandlerExceptions_DoNotStopSupervision()
    {
        _supervisor.StateChanged += (_, _) => throw new InvalidOperationException("bad subscriber");

        _supervisor.Start();
        await Wait.UntilAsync(() => _supervisor.State == HostState.Ready, "ready");
        _launcher.LastSession.Crash();

        await Wait.UntilAsync(() => _launcher.Launches == 2 && _supervisor.State == HostState.Ready, "restarted");
    }
}
