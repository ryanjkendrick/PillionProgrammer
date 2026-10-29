using System.Net;

namespace Pillion.CommandRunner.Tests;

/// <summary>Background processes: readiness on a port and an HTTP check against a listener the test starts, failures, and stops.</summary>
public sealed class BackgroundProcessTests : IDisposable
{
    private readonly TempDirectory _directory = new();

    public void Dispose() => _directory.Dispose();

    [Fact]
    public async Task Ready_WhenThePortAcceptsAndTheHealthUrlAnswers_AfterRetries_ThenStopped()
    {
        await using var session = await TestSessions.CreateAsync(_directory);
        var port = TestSessions.FreePort();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        var requests = 0;
        var serving = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                HttpListenerContext context;
                try
                {
                    context = await listener.GetContextAsync();
                }
                catch (Exception exception) when (exception is HttpListenerException or ObjectDisposedException)
                {
                    return;
                }

                // Not ready for the first two checks, then healthy: readiness must retry until it is.
                var n = Interlocked.Increment(ref requests);
                context.Response.StatusCode = n <= 2 ? 503 : 200;
                context.Response.Close();
            }
        });

        var info = await session.StartBackgroundProcessAsync(new BackgroundProcessOptions("Write-Output serving; Start-Sleep -Seconds 600")
        {
            Port = port,
            HealthUrl = new Uri($"http://127.0.0.1:{port}/health"),
            ReadinessTimeout = TimeSpan.FromSeconds(60),
        });

        Assert.Equal(BackgroundProcessState.Ready, info.State);
        Assert.Null(info.Failure);
        Assert.Equal(port, info.Port);
        Assert.True(Volatile.Read(ref requests) >= 3);
        Assert.True(info.ProcessId > 0);
        var listed = Assert.Single(session.BackgroundProcesses);
        Assert.Equal(info.Id, listed.Id);
        Assert.True(await TestSessions.WaitForLineAsync(info.Output, l => l == "serving", TestSessions.Wait), "the output never showed the first line");
        var tree = TestSessions.TreeOf(info.ProcessId);

        Assert.True(await session.StopBackgroundProcessAsync(info.Id));
        Assert.Equal(BackgroundProcessState.Stopped, session.BackgroundProcesses.Single().State);
        Assert.True(await TestSessions.WaitUntilDeadAsync(tree, TimeSpan.FromSeconds(2)), "the background process survived the stop");
        Assert.False(await session.StopBackgroundProcessAsync("bg-unknown"));
        Assert.True(await session.StopBackgroundProcessAsync(info.Id), "stopping twice is harmless");

        listener.Stop();
        await serving.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task ExitingBeforeReady_IsFailed_WithTheExitCode()
    {
        await using var session = await TestSessions.CreateAsync(_directory);

        var info = await session.StartBackgroundProcessAsync(new BackgroundProcessOptions("Write-Output 'cannot bind'; exit 4")
        {
            Port = TestSessions.FreePort(),
            ReadinessTimeout = TimeSpan.FromSeconds(60),
        });

        Assert.Equal(BackgroundProcessState.Failed, info.State);
        Assert.Equal(4, info.ExitCode);
        Assert.Contains("exited with code 4", info.Failure, StringComparison.Ordinal);
        Assert.True(await TestSessions.WaitForLineAsync(info.Output, l => l == "cannot bind", TestSessions.Wait), "the output never showed the line");
    }

    [Fact]
    public async Task NotReadyInTime_IsStoppedAndFailed()
    {
        await using var session = await TestSessions.CreateAsync(_directory);

        var info = await session.StartBackgroundProcessAsync(new BackgroundProcessOptions("Start-Sleep -Seconds 600")
        {
            Port = TestSessions.FreePort(),
            ReadinessTimeout = TimeSpan.FromSeconds(2),
        });

        Assert.Equal(BackgroundProcessState.Failed, info.State);
        Assert.StartsWith("Not ready within 2 s", info.Failure, StringComparison.Ordinal);
        // The pseudo-console host (a child of this process too) exits shortly after the console is closed.
        Assert.True(await TestSessions.WaitUntilDeadAsync(TestSessions.TreeOf(info.ProcessId), TimeSpan.FromSeconds(10)), "the process was not stopped");
        Assert.False(ProcessIsAlive(info.ProcessId));
    }

    [Fact]
    public async Task WithoutChecks_ItIsReadyAtOnce_AndDisposingTheSessionStopsIt()
    {
        var session = await TestSessions.CreateAsync(_directory);

        var info = await session.StartBackgroundProcessAsync(new BackgroundProcessOptions("Start-Sleep -Seconds 600"));
        Assert.Equal(BackgroundProcessState.Ready, info.State);
        var tree = TestSessions.TreeOf(info.ProcessId);

        await session.DisposeAsync();

        Assert.True(await TestSessions.WaitUntilDeadAsync(tree, TimeSpan.FromSeconds(2)), "the background process survived the session");
    }

    [Fact]
    public async Task ExitingAfterReady_IsExited()
    {
        await using var session = await TestSessions.CreateAsync(_directory);

        var info = await session.StartBackgroundProcessAsync(new BackgroundProcessOptions("Start-Sleep -Milliseconds 500; exit 0"));
        Assert.Equal(BackgroundProcessState.Ready, info.State);

        var deadline = DateTime.UtcNow + TestSessions.Wait;
        while (session.BackgroundProcesses.Single().State == BackgroundProcessState.Ready && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }

        var final = session.BackgroundProcesses.Single();
        Assert.Equal(BackgroundProcessState.Exited, final.State);
        Assert.Equal(0, final.ExitCode);
    }

    private static bool ProcessIsAlive(int processId) => Native.ProcessTree.IsAlive(processId);
}
