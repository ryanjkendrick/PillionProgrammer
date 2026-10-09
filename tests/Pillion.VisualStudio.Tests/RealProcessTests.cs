using System;
using System.Collections.Concurrent;
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
    private const string ChunkDelayVariable = "PILLION_FAKEHOST_CHUNK_DELAY_MS";

    private readonly JoinableTaskContext _context = new();
    private readonly string _logDirectory = Path.Combine(Path.GetTempPath(), "pillion-vsix-tests-" + Guid.NewGuid().ToString("N"));
    private readonly PillionLog _log;

    public RealProcessTests()
    {
        _log = new PillionLog(_logDirectory);
        Environment.SetEnvironmentVariable(ModeVariable, null);
        Environment.SetEnvironmentVariable(ChunkDelayVariable, null);
    }

    private static string FakeHostPath => typeof(RealProcessTests).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .Single(a => a.Key == "FakeHostPath").Value!;

    private string LogText => ReadShared(_log.CurrentFilePath);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(ModeVariable, null);
        Environment.SetEnvironmentVariable(ChunkDelayVariable, null);
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

    /// <summary>
    /// Design rule (section 13): the UI thread never waits on the host. Runs the real launcher and a real host process
    /// under a stand-in for Visual Studio's UI thread, kills the host, and checks that the restart (the signature check,
    /// Process.Start, the job object, the pipe connect and verify, the handshake, every state change) never runs on that
    /// thread and posts no work to it, and that a probe posted to it every 10 ms is never kept waiting.
    /// </summary>
    [Fact]
    public async Task KillAndRestart_NeverRunsOrPostsAnythingOnTheMainThread()
    {
        Assert.True(File.Exists(FakeHostPath), "Build tests/Pillion.FakeHost first: " + FakeHostPath);
        using var pump = new MainThreadPump();
        using var context = pump.CreateContext();
        var launchThreads = new ConcurrentQueue<int>();
        var handlerThreads = new ConcurrentQueue<int>();
        using var supervisor = new HostSupervisor(new ThreadRecordingLauncher(CreateLauncher(), launchThreads), context.Factory, _log, SystemClock.Instance, new RestartPolicy());
        supervisor.StateChanged += (_, _) => handlerThreads.Enqueue(Environment.CurrentManagedThreadId);

        // The package calls Start() on the main thread; it must return at once.
        var startTook = await pump.InvokeAsync(() =>
        {
            var stopwatch = Stopwatch.StartNew();
            supervisor.Start();
            return stopwatch.Elapsed;
        });
        await supervisor.WhenReadyAsync(CancellationToken.None).WithTimeout(TimeSpan.FromSeconds(30));
        var firstPid = supervisor.HostProcessId!.Value;

        using var stopProbing = new CancellationTokenSource();
        var probing = pump.ProbeAsync(TimeSpan.FromMilliseconds(10), stopProbing.Token);
        using (var first = Process.GetProcessById(firstPid))
        {
            first.Kill();
        }

        await Wait.UntilAsync(() => supervisor.State == HostState.Restarting, "restarting");
        await Wait.UntilAsync(
            () => supervisor.State == HostState.Ready && supervisor.HostProcessId is { } pid && pid != firstPid,
            "ready again with a new host",
            timeoutMs: 30000);
        stopProbing.Cancel();
        await probing;

        Assert.True(startTook < TimeSpan.FromMilliseconds(250), $"Start() kept the main thread for {startTook.TotalMilliseconds:0} ms.");
        Assert.True(launchThreads.Count >= 2, "the supervisor launched the host twice");
        Assert.DoesNotContain(pump.ThreadId, launchThreads);
        Assert.True(handlerThreads.Count >= 4, "Starting, Ready, Restarting, Starting, Ready");
        Assert.DoesNotContain(pump.ThreadId, handlerThreads);
        Assert.True(pump.Probes > 20, $"only {pump.Probes} probes ran on the main thread");
        Assert.True(
            pump.WorkItems == 0 && pump.LongestWork < TimeSpan.FromMilliseconds(100),
            $"The supervisor posted {pump.WorkItems} work items to the main thread, the longest taking {pump.LongestWork.TotalMilliseconds:0} ms.");
        Assert.True(
            pump.LongestProbeLatency < TimeSpan.FromSeconds(1),
            $"A probe waited {pump.LongestProbeLatency.TotalMilliseconds:0} ms for the main thread (this machine was busy, or the restart kept it busy).");

        await supervisor.ShutdownAsync(TimeSpan.FromSeconds(2));
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

    [Fact]
    public async Task ChatAndOptions_WorkOverTheRealWireAgainstTheFakeHost()
    {
        Environment.SetEnvironmentVariable(ModeVariable, "quiet");
        Environment.SetEnvironmentVariable(ChunkDelayVariable, "40");
        using var uiContext = await Task.Run(() => new JoinableTaskContext());
        using var supervisor = CreateSupervisor(new RestartPolicy());
        supervisor.Start();
        await supervisor.WhenReadyAsync(CancellationToken.None).WithTimeout(TimeSpan.FromSeconds(30));

        // connectivityChanged reaches the supervisor (its handler is registered before the handshake).
        await Wait.UntilAsync(() => supervisor.Connectivity == ConnectivityState.Online, "the host reports its connectivity", timeoutMs: 30000);

        using var chat = new ChatViewModel(supervisor, uiContext.Factory, new ChatViewModelOptions { Log = _log });
        chat.Start();
        await Wait.UntilAsync(() => chat.ModelPicker.Models.Count == 3, "Auto and the fake host's models are listed", timeoutMs: 30000);
        Assert.Equal([ModelOption.AutoId, "ollama/fake-local-coder", "fake-cloud/fake-cloud-chat"], chat.ModelPicker.Models.Select(m => m.Id));
        Assert.Equal("Ollama (fake)", chat.ModelPicker.SelectedModel?.ProviderName);
        Assert.Contains("Digest: sha256:fake0local0digest", chat.ModelPicker.SelectedModel?.ToolTip, StringComparison.Ordinal);
        Assert.Equal("Ready. Online.", chat.StatusText);

        // A full streamed reply (IAsyncEnumerable over MessagePack) that names the model.
        chat.InputText = "Hello";
        await chat.SendAsync().WithTimeout(TimeSpan.FromSeconds(30));
        var reply = chat.Turns.Last();
        Assert.Equal(ChatTurnState.Complete, reply.State);
        Assert.Equal("Fake Local Coder via Ollama (fake), local", reply.Header);
        Assert.StartsWith("Hello from Fake Local Coder (model fake-local-coder", reply.Text, StringComparison.Ordinal);
        Assert.Contains(reply.Segments, s => s.IsCode && s.Language == "csharp" && s.Text.Contains("Console.WriteLine"));

        // Auto sends no model id; the header names the model the host says answered.
        chat.ModelPicker.SelectedModel = chat.ModelPicker.Models.Single(m => m.IsAuto);
        chat.InputText = "Pick one for me";
        await chat.SendAsync().WithTimeout(TimeSpan.FromSeconds(30));
        Assert.Equal(ChatTurnState.Complete, chat.Turns.Last().State);
        Assert.Equal("Fake Local Coder via Ollama (fake), local", chat.Turns.Last().Header);

        // A stream that fails after some text keeps the text and shows the error inline.
        chat.InputText = "/partial please";
        await chat.SendAsync().WithTimeout(TimeSpan.FromSeconds(30));
        var partial = chat.Turns.Last();
        Assert.Equal(ChatTurnState.Failed, partial.State);
        Assert.StartsWith("Hello from Fake", partial.Text, StringComparison.Ordinal);
        Assert.Contains("dropped the connection part way", partial.ErrorMessage, StringComparison.Ordinal);
        Assert.True(partial.CanRetry);

        // The cloud model, then Stop part-way through.
        chat.ModelPicker.SelectedModel = chat.ModelPicker.Models.Single(m => !m.IsLocal && !m.IsAuto);
        chat.InputText = "Tell me more";
        var send = chat.SendAsync();
        await Wait.UntilAsync(() => chat.Turns.Last().Text.Length > 0, "the cloud reply starts streaming", timeoutMs: 30000);
        Assert.Equal("Fake Cloud Chat via Fake Cloud, cloud", chat.Turns.Last().Header);
        await chat.StopAsync();
        await send.WithTimeout(TimeSpan.FromSeconds(30));
        var stopped = chat.Turns.Last();
        Assert.Equal(ChatTurnState.Stopped, stopped.State);
        Assert.DoesNotContain("That is the end of the reply", stopped.Text);
        await Wait.UntilAsync(() => LogText.Contains("fakehost: cancel " + stopped.RequestId), "the host was asked to cancel");

        // A typed host error crosses the wire and is shown inline with its retry hint.
        chat.InputText = "/error please";
        await chat.SendAsync().WithTimeout(TimeSpan.FromSeconds(30));
        var failed = chat.Turns.Last();
        Assert.Equal(ChatTurnState.Failed, failed.State);
        Assert.Equal("The fake provider for Fake Cloud Chat is unreachable.", failed.ErrorMessage);
        Assert.StartsWith("Check the provider", failed.ErrorHint, StringComparison.Ordinal);
        Assert.True(failed.CanRetry);

        // The Options pages over the same connection.
        var credentials = new InMemoryCredentialStore();
        using var providers = new ProvidersOptionsViewModel(supervisor, uiContext.Factory, credentials, _log);
        await providers.LoadAsync();
        Assert.Equal(OptionsLoadState.Loaded, providers.LoadState);
        Assert.Equal(["ollama", "fake-cloud"], providers.Providers.Select(p => p.Id));
        providers.Add();
        providers.SelectedProvider!.Id = "work-gateway";
        providers.SelectedProvider.ApiKey = "sk-test-not-a-real-key";
        await providers.SaveAsync();
        Assert.Equal("Saved.", providers.StatusMessage);
        Assert.Equal("sk-test-not-a-real-key", credentials.Keys["work-gateway"]);
        await providers.TestSelectedAsync();
        Assert.Equal("Connection succeeded. The fake host reached provider 'work-gateway'.", providers.SelectedProvider.TestResult);

        using var privacy = new PrivacyOptionsViewModel(supervisor, uiContext.Factory, _log);
        await privacy.LoadAsync();
        privacy.LocalOnly = true;
        await privacy.SaveAsync();
        Assert.Equal("Saved.", privacy.StatusMessage);

        // Local-only now greys the cloud model out in the picker.
        await chat.RefreshModelsAsync();
        Assert.Equal(ChatModelPolicy.LocalOnlyReason, chat.ModelPicker.Models.Single(m => !m.IsLocal && !m.IsAuto).UnavailableReason);
        Assert.DoesNotContain("sk-test-not-a-real-key", LogText);

        await supervisor.ShutdownAsync(TimeSpan.FromSeconds(2));
    }

    /// <summary>Reads the log while the logger may be appending to it (File.ReadAllText would deny it write access).</summary>
    private static string ReadShared(string? path)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                if (path is null || !File.Exists(path))
                {
                    return string.Empty;
                }

                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                return reader.ReadToEnd();
            }
            catch (IOException) when (attempt < 20)
            {
                Thread.Sleep(10);
            }
        }
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

/// <summary>Wraps a launcher and records the thread each launch begins on, which is where its synchronous part (signature check, Process.Start, job object) runs.</summary>
internal sealed class ThreadRecordingLauncher(IHostLauncher inner, ConcurrentQueue<int> threads) : IHostLauncher
{
    public Task<IHostSession> LaunchAsync(CancellationToken cancellationToken)
    {
        threads.Enqueue(Environment.CurrentManagedThreadId);
        return inner.LaunchAsync(cancellationToken);
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class RealProcessTestGroup
{
    public const string Name = "Real host processes";
}
