using System.Diagnostics;
using Pillion.Host.Rpc;
using Pillion.Host.Tests.TestSupport;
using Xunit.Abstractions;

namespace Pillion.Host.Tests;

/// <summary>Tests that launch the real Pillion.Host.exe.</summary>
public class HostProcessTests(ITestOutputHelper output)
{
    private static readonly TimeSpan ExitWait = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task RealHost_HandshakeThenShutdown_ExitsWithZeroAndWritesALog()
    {
        using var logs = new TempDirectory();
        var token = LaunchToken.CreateValue();
        var pipeName = NewPipeName();
        using var host = HostProcess.Start(pipeName, Environment.ProcessId, logs.Path, token);

        await using (var client = await TestRpcClient.ConnectAsync(pipeName))
        {
            var response = await client.HandshakeAsync(token);
            Assert.True(response.Accepted, response.Reason);
            Assert.Equal(host.Process.Id, response.HostProcessId);

            await client.Lifecycle.ShutdownAsync(CancellationToken.None);
            await host.Process.WaitForExitAsync().WaitAsync(ExitWait);
        }

        Assert.True(0 == host.Process.ExitCode, $"Exit code {host.Process.ExitCode}. Output:{Environment.NewLine}{host.Output}");

        var logFile = Assert.Single(Directory.GetFiles(logs.Path, "host-*.log"));
        Assert.Matches(@"host-\d{8}\.log$", logFile);
        var log = await File.ReadAllTextAsync(logFile);
        Assert.Contains("\"category\":\"Pillion.Host.Rpc.RpcConnection\"", log, StringComparison.Ordinal);
        Assert.Contains("handshake accepted", log, StringComparison.Ordinal);
        Assert.Contains("Host stop requested", log, StringComparison.Ordinal);
        Assert.DoesNotContain(token, log, StringComparison.Ordinal);
        Assert.DoesNotContain(token, host.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RealHost_ExitsByItselfWhenTheParentExits()
    {
        using var logs = new TempDirectory();

        // A short-lived parent (about one second).
        using var parent = Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 2 127.0.0.1 >nul")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        using var host = HostProcess.Start(NewPipeName(), parent.Id, logs.Path, LaunchToken.CreateValue());

        await parent.WaitForExitAsync().WaitAsync(ExitWait);
        var parentExited = Stopwatch.StartNew();
        await host.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        output.WriteLine($"Host exited {parentExited.ElapsedMilliseconds} ms after its parent.");

        Assert.True(0 == host.Process.ExitCode, $"Exit code {host.Process.ExitCode}. Output:{Environment.NewLine}{host.Output}");
    }

    [Fact]
    public async Task RealHost_WhoseParentHasAlreadyExited_ExitsImmediately()
    {
        using var logs = new TempDirectory();
        using var parent = Process.Start(new ProcessStartInfo("cmd.exe", "/c exit") { UseShellExecute = false, CreateNoWindow = true })!;
        await parent.WaitForExitAsync().WaitAsync(ExitWait);

        // The Process object keeps the parent's handle open, so its id cannot be reused meanwhile.
        using var host = HostProcess.Start(NewPipeName(), parent.Id, logs.Path, LaunchToken.CreateValue());
        await host.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(0, host.Process.ExitCode);
    }

    [Fact]
    public async Task RealHost_WithoutAToken_FailsWithExitCodeOne()
    {
        using var logs = new TempDirectory();
        using var host = HostProcess.Start(NewPipeName(), Environment.ProcessId, logs.Path, token: null);

        await host.Process.WaitForExitAsync().WaitAsync(ExitWait);

        Assert.True(1 == host.Process.ExitCode, $"Exit code {host.Process.ExitCode}. Output:{Environment.NewLine}{host.Output}");
        Assert.Contains("could not read the launch token", host.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RealHost_WithMissingArguments_ExitsWithCodeTwo()
    {
        var startInfo = new ProcessStartInfo(HostProcess.FindHostExecutable())
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        startInfo.ArgumentList.Add("--pipe");
        startInfo.ArgumentList.Add("p");
        using var process = Process.Start(startInfo)!;
        var error = process.StandardError.ReadToEndAsync();

        await process.WaitForExitAsync().WaitAsync(ExitWait);

        Assert.Equal(2, process.ExitCode);
        Assert.Contains("--parent-pid <pid> is required.", await error, StringComparison.Ordinal);
    }

    private static string NewPipeName() => "pillion-test-" + Guid.NewGuid().ToString("N");
}
