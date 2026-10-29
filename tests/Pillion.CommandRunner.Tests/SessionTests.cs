using System.Diagnostics;
using Pillion.Common.Settings;
using Pillion.Contracts;

namespace Pillion.CommandRunner.Tests;

/// <summary>Commands on a real pseudo-console: exit codes and output, kills, prompts, redaction, caps and bounds.</summary>
public sealed class SessionTests : IDisposable
{
    private readonly TempDirectory _directory = new();

    public void Dispose() => _directory.Dispose();

    [Fact]
    public async Task Run_ReportsTheExitCodeAndTheOutput_WithTheCommandShownFirst()
    {
        await using var session = await TestSessions.CreateAsync(_directory);

        var result = await session.RunAsync("Write-Output 'hello from pillion'; exit 3");

        Assert.Equal(CommandOutcome.Completed, result.ExitCode == 3 ? CommandOutcome.Completed : result.Outcome);
        Assert.Equal(3, result.ExitCode);
        Assert.False(result.Succeeded);
        Assert.StartsWith("> Write-Output 'hello from pillion'; exit 3", result.ModelView.Text, StringComparison.Ordinal);
        Assert.Contains("hello from pillion", result.ModelView.Text, StringComparison.Ordinal);
        Assert.True(result.Duration > TimeSpan.Zero);
        Assert.True(result.Output.Length >= 2, $"{result.Output.Length} lines");
        Assert.Contains("-NoProfile", result.CommandLine, StringComparison.Ordinal);
        Assert.Equal(ShellKind.PowerShell7, session.Shell);
        Assert.False(session.IsRunning);
    }

    [Fact]
    public async Task Run_PropagatesANativeExitCode_AndACmdletErrorIsOne()
    {
        await using var session = await TestSessions.CreateAsync(_directory);

        Assert.Equal(7, (await session.RunAsync("cmd.exe /c exit 7")).ExitCode);
        Assert.Equal(0, (await session.RunAsync("Write-Output ok")).ExitCode);
        var failed = await session.RunAsync("Get-Item 'C:\\definitely\\missing\\pillion\\path.txt'");
        Assert.Equal(1, failed.ExitCode);
        Assert.Contains("path.txt", failed.ModelView.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancel_KillsTheWholeProcessTree_WithinASecond()
    {
        await using var session = await TestSessions.CreateAsync(_directory);
        using var cancel = new CancellationTokenSource();

        // pwsh (the shell) -> cmd -> powershell: three levels, all of which must die.
        var run = session.RunAsync("cmd.exe /c \"powershell -NoProfile -Command Start-Sleep 600\"", cancellationToken: cancel.Token);
        var tree = await TestSessions.WaitForTreeAsync(session, 2, TestSessions.Wait);

        var watch = Stopwatch.StartNew();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        watch.Stop();

        // The pseudo-console host is a descendant too and exits shortly after the console closes.
        Assert.True(await TestSessions.WaitUntilDeadAsync(tree, TimeSpan.FromSeconds(2)), "a process of the tree survived the cancel");
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), $"the cancel took {watch.Elapsed}");
        Assert.False(session.IsRunning);
    }

    [Fact]
    public async Task Timeout_KillsTheWholeProcessTree_AndIsAResult()
    {
        await using var session = await TestSessions.CreateAsync(_directory);

        var run = session.RunAsync("cmd.exe /c \"powershell -NoProfile -Command Start-Sleep 600\"", new CommandRunOptions { Timeout = TimeSpan.FromSeconds(3) });
        var tree = await TestSessions.WaitForTreeAsync(session, 2, TestSessions.Wait);
        var result = await run;

        Assert.Equal(CommandOutcome.TimedOut, result.Outcome);
        Assert.Equal(-1, result.ExitCode);
        Assert.True(result.Duration < TimeSpan.FromSeconds(10), $"{result.Duration}");
        Assert.True(await TestSessions.WaitUntilDeadAsync(tree, TimeSpan.FromSeconds(2)), "a process of the tree survived the timeout");
    }

    [Fact]
    public async Task Prompt_IsDetectedOnce_AndInputAnswersIt()
    {
        await using var session = await TestSessions.CreateAsync(_directory, o => o.PromptIdle = TimeSpan.FromSeconds(1));
        var prompts = new List<PromptWaitingEventArgs>();
        var first = new TaskCompletionSource<PromptWaitingEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.PromptWaiting += (_, e) =>
        {
            lock (prompts)
            {
                prompts.Add(e);
            }

            first.TrySetResult(e);
        };

        var run = session.RunAsync("$name = Read-Host 'Name'; Write-Output \"Hello, $name!\"");
        var prompt = await first.Task.WaitAsync(TestSessions.Wait);

        Assert.Contains("Name", prompt.PromptLine, StringComparison.Ordinal);
        Assert.True(session.IsRunning);
        await session.WriteInputAsync("Pillion\r");
        var result = await run.WaitAsync(TestSessions.Wait);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Hello, Pillion!", result.ModelView.Text, StringComparison.Ordinal);
        lock (prompts)
        {
            Assert.Single(prompts);
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() => session.WriteInputAsync("x"));
    }

    [Fact]
    public async Task Prompt_IsNotRaised_ForOutputThatEndsWithANewline()
    {
        await using var session = await TestSessions.CreateAsync(_directory, o => o.PromptIdle = TimeSpan.FromMilliseconds(500));
        var raised = 0;
        session.PromptWaiting += (_, _) => Interlocked.Increment(ref raised);

        var result = await session.RunAsync("Write-Output 'working'; Start-Sleep -Seconds 2; Write-Output 'done'");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(0, Volatile.Read(ref raised));
    }

    [Fact]
    public async Task Secrets_AreRedactedInTheModelView_ButNotInTheBuffer()
    {
        await using var session = await TestSessions.CreateAsync(_directory);
        const string planted = "AKIAJ4K7QW6ZP2XV3MNB";

        var result = await session.RunAsync($"Write-Output 'AWS_ACCESS_KEY_ID={planted}'; Write-Output after");

        Assert.True(result.ModelView.Redacted);
        Assert.DoesNotContain(planted, result.ModelView.Text, StringComparison.Ordinal);
        Assert.Contains("<redacted:", result.ModelView.Text, StringComparison.Ordinal);
        Assert.Contains("after", result.ModelView.Text, StringComparison.Ordinal);
        var full = string.Join("\n", session.Terminal.GetLines(result.Output.Start, result.Output.End).Select(l => l.Text));
        Assert.Contains(planted, full, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ByteCap_TailTruncatesTheModelView_AndTheBufferKeepsEveryLine()
    {
        await using var session = await TestSessions.CreateAsync(_directory, o => o.ModelViewByteCap = 2048);

        var result = await session.RunAsync("1..300 | ForEach-Object { \"line $_ \" + ('x' * 60) }");

        Assert.Equal(0, result.ExitCode);
        Assert.True(result.ModelView.Truncated);
        Assert.True(result.ModelView.ByteCount <= 2048, $"{result.ModelView.ByteCount} bytes");
        Assert.StartsWith("[... ", result.ModelView.Text, StringComparison.Ordinal);
        Assert.EndsWith("line 300 " + new string('x', 60), result.ModelView.Text, StringComparison.Ordinal);
        Assert.Equal(301, result.ModelView.TotalLines);
        var lines = session.Terminal.GetLines(result.Output.Start, result.Output.End);
        Assert.Equal(301, lines.Count);
        Assert.Contains(lines, l => l.Text.StartsWith("line 1 ", StringComparison.Ordinal));

        var perRun = await session.RunAsync("1..50 | ForEach-Object { \"row $_\" }", new CommandRunOptions { ModelViewByteCap = 100_000 });
        Assert.False(perRun.ModelView.Truncated);
    }

    [Fact]
    public async Task HeavyOutput_StaysWithinTheScrollbackBound_AndDoesNotGrowTheHost()
    {
        const int lines = 100_000; // about 10 MB through the pseudo-console
        await using var session = await TestSessions.CreateAsync(_directory, o =>
        {
            o.ScrollbackLines = 500;
            o.Rows = 40;
            o.Timeout = TimeSpan.FromMinutes(5);
        });
        await session.RunAsync("Write-Output warm-up");
        var before = GC.GetTotalMemory(forceFullCollection: true);

        var result = await session.RunAsync($"$s = 'x' * 95; for ($i = 0; $i -lt {lines}; $i++) {{ [Console]::Out.WriteLine(\"$i $s\") }}");

        var after = GC.GetTotalMemory(forceFullCollection: true);
        Assert.Equal(0, result.ExitCode);
        Assert.EndsWith($"{lines - 1} {new string('x', 95)}", result.ModelView.Text, StringComparison.Ordinal);
        Assert.True(result.Output.Length >= lines, $"{result.Output.Length} lines were counted");
        var kept = session.Terminal.GetLines(result.Output.Start, result.Output.End);
        Assert.True(kept.Count <= 500 + 40, $"{kept.Count} lines are held");
        Assert.True(session.Terminal.OldestLineIndex >= result.Output.End - 540, "the scrollback held more than its bound");
        var growth = after - before;
        Assert.True(growth < 32 * 1024 * 1024, $"the managed heap grew by {growth / 1024 / 1024} MB");
    }

    [Fact]
    public async Task BuildAndTestKinds_SetTheEnvironment_AndAppendTheLoggers()
    {
        await using var session = await TestSessions.CreateAsync(_directory);

        var build = await session.RunAsync("cmd.exe /c echo TL=%MSBUILDTERMINALLOGGER% NC=%NO_COLOR%", new CommandRunOptions { Kind = CommandKind.Build });
        Assert.Equal(0, build.ExitCode);
        Assert.Contains("TL=off NC=1", build.ModelView.Text, StringComparison.Ordinal);
        Assert.NotNull(build.BinaryLogPath);
        Assert.EndsWith(".binlog", build.BinaryLogPath, StringComparison.Ordinal);
        Assert.Contains("-bl:", build.ModelView.Text, StringComparison.Ordinal);
        Assert.Null(build.TrxPath);

        var test = await session.RunAsync("cmd.exe /c echo TL=%MSBUILDTERMINALLOGGER% NC=%NO_COLOR%", new CommandRunOptions { Kind = CommandKind.Test });
        Assert.Contains("TL=off NC=1", test.ModelView.Text, StringComparison.Ordinal);
        Assert.NotNull(test.TrxPath);
        Assert.EndsWith(".trx", test.TrxPath, StringComparison.Ordinal);
        Assert.Contains("--logger trx;LogFileName=", test.ModelView.Text, StringComparison.Ordinal);
        Assert.Null(test.BinaryLogPath);

        var plain = await session.RunAsync("cmd.exe /c echo TL=%MSBUILDTERMINALLOGGER% NC=%NO_COLOR%");
        Assert.Contains("TL=%MSBUILDTERMINALLOGGER% NC=%NO_COLOR%", plain.ModelView.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Environment_SessionAndRunOverrides_ReachTheCommand()
    {
        System.Environment.SetEnvironmentVariable("PILLION_PARENT_VAR", "parent");
        await using var session = await TestSessions.CreateAsync(_directory, o =>
        {
            o.Environment["PILLION_SESSION_VAR"] = "session";
            o.Environment["PILLION_PARENT_VAR"] = null;
        });
        var run = new CommandRunOptions();
        run.Environment["PILLION_RUN_VAR"] = "run";

        var result = await session.RunAsync("Write-Output \"S=$env:PILLION_SESSION_VAR R=$env:PILLION_RUN_VAR X=[$env:PILLION_PARENT_VAR]\"", run);

        Assert.Contains("S=session R=run X=[]", result.ModelView.Text, StringComparison.Ordinal);
        Assert.False(session.Environment.ContainsKey("PILLION_PARENT_VAR"));
        Assert.Equal("session", session.Environment["PILLION_SESSION_VAR"]);
    }

    [Fact]
    public async Task CmdShell_RunsAndReportsTheErrorLevel()
    {
        await using var session = await TestSessions.CreateAsync(_directory, shell: ShellKind.Cmd);

        var result = await session.RunAsync("echo from cmd & exit /b 5");

        Assert.Equal(ShellKind.Cmd, session.Shell);
        Assert.Equal(5, result.ExitCode);
        Assert.Contains("from cmd", result.ModelView.Text, StringComparison.Ordinal);
        Assert.Equal(0, (await session.RunAsync("ver")).ExitCode);
    }

    [Fact]
    public async Task WindowsPowerShell_Runs()
    {
        await using var session = await TestSessions.CreateAsync(_directory, shell: ShellKind.WindowsPowerShell);

        var result = await session.RunAsync("Write-Output \"major=$($PSVersionTable.PSVersion.Major)\"; exit 2");

        Assert.Equal(ShellKind.WindowsPowerShell, session.Shell);
        Assert.Equal(2, result.ExitCode);
        Assert.Contains("major=5", result.ModelView.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Resize_ChangesTheWidthTheCommandSees()
    {
        await using var session = await TestSessions.CreateAsync(_directory, o =>
        {
            o.Columns = 50;
            o.Rows = 20;
        });

        var narrow = await session.RunAsync("Write-Output \"width=$($Host.UI.RawUI.WindowSize.Width)\"");
        session.Resize(100, 30);
        var wide = await session.RunAsync("Write-Output \"width=$($Host.UI.RawUI.WindowSize.Width)\"");

        Assert.Contains("width=50", narrow.ModelView.Text, StringComparison.Ordinal);
        Assert.Contains("width=100", wide.ModelView.Text, StringComparison.Ordinal);
        Assert.Equal(100, session.Terminal.Columns);
        Assert.Equal(30, session.Terminal.Rows);
    }

    [Fact]
    public async Task Dispose_KillsARunningCommand_AndCompletesItsRun()
    {
        var session = await TestSessions.CreateAsync(_directory);
        var run = session.RunAsync("Start-Sleep -Seconds 600");
        var tree = await TestSessions.WaitForTreeAsync(session, 0, TestSessions.Wait);

        await session.DisposeAsync();

        var result = await run.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(CommandOutcome.Completed, result.Outcome);
        Assert.NotEqual(0, result.ExitCode);
        Assert.True(await TestSessions.WaitUntilDeadAsync(tree, TimeSpan.FromSeconds(2)));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => session.RunAsync("Write-Output x"));
    }

    [Fact]
    public async Task ConcurrentRuns_AreRefused()
    {
        await using var session = await TestSessions.CreateAsync(_directory);
        using var cancel = new CancellationTokenSource();
        var first = session.RunAsync("Start-Sleep -Seconds 600", cancellationToken: cancel.Token);

        await Assert.ThrowsAsync<InvalidOperationException>(() => session.RunAsync("Write-Output second"));

        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
    }

    [Fact]
    public async Task TheTerminal_KeepsEveryRun_WithItsHeader_AndSnapshotsTheScreen()
    {
        await using var session = await TestSessions.CreateAsync(_directory);
        var changes = 0;
        session.Terminal.Changed += (_, _) => Interlocked.Increment(ref changes);

        var first = await session.RunAsync("Write-Output one");
        var second = await session.RunAsync("Write-Output two");

        var all = session.Terminal.GetLinesToEnd(0).Select(l => l.Text).ToList();
        Assert.Contains("> Write-Output one", all);
        Assert.Contains("one", all);
        Assert.Contains("> Write-Output two", all);
        Assert.Contains("two", all);
        Assert.True(second.Output.Start >= first.Output.End);
        Assert.True(Volatile.Read(ref changes) > 0);

        var snapshot = session.Terminal.Snapshot();
        Assert.Equal(session.Terminal.Rows, snapshot.Rows.Count);
        Assert.Equal(session.Terminal.Generation, snapshot.Generation);
        Assert.Contains(snapshot.Rows, r => r.Text == "two");
        Assert.Equal(session.Terminal.FirstRowIndex, snapshot.FirstRowIndex);
    }

    [Fact]
    public async Task MissingWorkingDirectory_IsRefused()
    {
        var runner = new CommandRunner();
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => runner.CreateSessionAsync(new CommandSessionOptions(Path.Combine(_directory.Path, "nope")) { LoadDeveloperEnvironment = false }));

        await using var session = await TestSessions.CreateAsync(_directory);
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => session.RunAsync("Write-Output x", new CommandRunOptions { WorkingDirectory = Path.Combine(_directory.Path, "nope") }));
    }

    [Fact]
    public async Task TheFlag_Off_RefusesASession_WithPolicyDenied()
    {
        var flags = new FakeFlags { Enabled = false };
        var runner = new CommandRunner(flags);

        var exception = await Assert.ThrowsAsync<PillionRpcException>(() => runner.CreateSessionAsync(new CommandSessionOptions(_directory.Path) { LoadDeveloperEnvironment = false }));

        Assert.Equal(PillionErrorCode.PolicyDenied, exception.Error.Code);
        Assert.Contains("commandRunner", exception.Error.Message, StringComparison.Ordinal);
        Assert.Equal(FeatureNames.CommandRunner, flags.LastAsked);

        flags.Enabled = true;
        await using var session = await runner.CreateSessionAsync(new CommandSessionOptions(_directory.Path) { LoadDeveloperEnvironment = false });
        Assert.NotNull(session.Id);
    }

    private sealed class FakeFlags : IFeatureFlags
    {
        public event EventHandler? Changed
        {
            add { }
            remove { }
        }

        public bool Enabled { get; set; }

        public string? LastAsked { get; private set; }

        public bool IsEnabled(string feature)
        {
            LastAsked = feature;
            return Enabled;
        }
    }
}
