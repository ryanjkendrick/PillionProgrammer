using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;
using Pillion.Contracts;
using Pillion.VisualStudio.Core;

namespace Pillion.VisualStudio.Tests;

/// <summary>
/// End-to-end tests against a real process: tests/Pillion.FakeHost, which implements the launch protocol and the
/// handshake with StreamJsonRpc. The tests in this class run one at a time because they select the fake host's
/// behaviour through an environment variable that child processes inherit.
/// </summary>
[Collection(RealProcessTestGroup.Name)]
public sealed class RealProcessTests : IDisposable
{
    private const string ModeVariable = "PILLION_FAKEHOST_MODE";

    private readonly JoinableTaskContext _context = new();
    private readonly string _logDirectory = Path.Combine(Path.GetTempPath(), "pillion-vsix-tests-" + Guid.NewGuid().ToString("N"));
    private readonly PillionLog _log;

    public RealProcessTests()
    {
        _log = new PillionLog(_logDirectory);
        Environment.SetEnvironmentVariable(ModeVariable, null);
    }

    private static string FakeHostPath => typeof(RealProcessTests).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .Single(a => a.Key == "FakeHostPath").Value!;

    private string LogText => File.Exists(_log.CurrentFilePath) ? File.ReadAllText(_log.CurrentFilePath!) : string.Empty;

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(ModeVariable, null);
        _context.Dispose();
        try
        {
            Directory.Delete(_logDirectory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task Supervisor_LaunchesHandshakesRestartsAfterAKillAndShutsDownCleanly()
    {
        Assert.True(File.Exists(FakeHostPath), "Build tests/Pillion.FakeHost first: " + FakeHostPath);
        using var supervisor = CreateSupervisor(new RestartPolicy());

        // Launch and handshake.
        supervisor.Start();
        await supervisor.WhenReadyAsync(CancellationToken.None).WithTimeout(TimeSpan.FromSeconds(30));
        var firstPid = supervisor.HostProcessId!.Value;
        Assert.Equal(HostState.Ready, await supervisor.GetProxy<IHostLifecycleService>().GetStateAsync(CancellationToken.None));
        using var first = Process.GetProcessById(firstPid);
        Assert.Equal("Pillion.FakeHost", first.ProcessName);

        // The fake host calls back into the VSIX's local targets and reports the results on stderr, which the
        // launcher copies into the log.
        await Wait.UntilAsync(() => LogText.Contains("approval refused: -32000 PolicyDenied Not implemented in phase 0"), "the host's callbacks were logged");
        Assert.Contains("fakehost: open documents 0", LogText);
        Assert.Contains("unsigned development build, host signature check skipped", LogText);

        // Kill the host: the supervisor notices, backs off 1 s and launches a new one.
        first.Kill();
        await Wait.UntilAsync(() => supervisor.State == HostState.Restarting, "restarting");
        Assert.Equal(1, supervisor.RestartAttempt);
        await Wait.UntilAsync(
            () => supervisor.State == HostState.Ready && supervisor.HostProcessId is { } pid && pid != firstPid,
            "ready again with a new host",
            timeoutMs: 30000);
        var secondPid = supervisor.HostProcessId!.Value;
        using var second = Process.GetProcessById(secondPid);

        // Clean shutdown: the host is asked to exit and does so within the 2 s budget.
        var stopwatch = Stopwatch.StartNew();
        await supervisor.ShutdownAsync(TimeSpan.FromSeconds(2));
        Assert.True(second.WaitForExit(5000), "the host exited");
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(4), "shutdown took " + stopwatch.Elapsed);
        Assert.Contains("host.shutdown.exited", LogText);
        Assert.DoesNotContain("host.shutdown.killed", LogText);
        Assert.Contains("fakehost: shutdown requested", LogText);
    }

    [Fact]
    public async Task Launcher_PutsTheHostInAKillOnCloseJobAndNeverLogsTheToken()
    {
        var launcher = CreateLauncher();

        using var session = await launcher.LaunchCoreAsync(CancellationToken.None).WithTimeout(TimeSpan.FromSeconds(30));

        Assert.True(session.IsInJob);
        using var host = Process.GetProcessById(session.ProcessId);
        await Wait.UntilAsync(() => LogText.Contains("handshake from pid " + Process.GetCurrentProcess().Id + ", accepted True"), "handshake logged");
        Assert.Contains("token length 43", LogText);

        // The token is only ever written to the inherited pipe: no 43-character base64url run appears in the log.
        Assert.DoesNotContain(
            LogText.Split([' ', '"', '='], StringSplitOptions.RemoveEmptyEntries),
            word => LaunchProtocol.IsWellFormedToken(word));

        session.Dispose();
        Assert.True(host.WaitForExit(5000), "disposing the session kills the host");
    }

    [Fact]
    public async Task HandshakeRejected_EntersFailedWithoutRetrying()
    {
        Environment.SetEnvironmentVariable(ModeVariable, "reject");
        using var supervisor = CreateSupervisor(new RestartPolicy());

        supervisor.Start();
        await Wait.UntilAsync(() => supervisor.State == HostState.Failed, "failed", timeoutMs: 30000);
        await Task.Delay(500);

        Assert.Equal("Pillion Host rejected the connection: fake host rejects this client", supervisor.FailureReason);
        Assert.Equal(1, CountOccurrences(LogText, "host.launch.begin"));
    }

    [Fact]
    public async Task HostThatExitsBeforeConnecting_IsRetriedThenFails()
    {
        Environment.SetEnvironmentVariable(ModeVariable, "exit");
        var fast = new RestartPolicy([TimeSpan.FromMilliseconds(10)], maxCrashes: 3, TimeSpan.FromMinutes(5));
        using var supervisor = CreateSupervisor(fast);

        supervisor.Start();
        await Wait.UntilAsync(() => supervisor.State == HostState.Failed, "failed", timeoutMs: 30000);

        Assert.Equal(3, CountOccurrences(LogText, "host.launch.begin"));
        Assert.Contains("exited with code 3 before accepting a connection", supervisor.FailureReason);
    }

    [Fact]
    public async Task Job_KillsItsProcessesWhenClosed()
    {
        var cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        using var process = HostProcess.Start(cmd, ["/c", "ping -n 30 127.0.0.1 >nul"], standardErrorLine: null);
        var job = HostJob.Create();
        Assert.True(job.TryAssign(process, out var error), "assign failed with " + error);
        Assert.True(job.Contains(process));

        job.Dispose();

        Assert.True(await process.WaitForExitAsync(TimeSpan.FromSeconds(10)), "closing the job killed the process");
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        for (var index = text.IndexOf(value, StringComparison.Ordinal); index >= 0; index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    private HostProcessLauncher CreateLauncher() => new(
        new HostLauncherOptions(FakeHostPath, typeof(HostSupervisor).Assembly.Location, HostLocalTargets.CreateNotImplemented())
        {
            LogDirectory = _logDirectory,
        },
        _log);

    private HostSupervisor CreateSupervisor(RestartPolicy policy) =>
        new(CreateLauncher(), _context.Factory, _log, SystemClock.Instance, policy);
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class RealProcessTestGroup
{
    public const string Name = "Real host processes";
}
