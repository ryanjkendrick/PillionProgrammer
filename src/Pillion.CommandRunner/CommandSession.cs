using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Pillion.CommandRunner.Native;
using Pillion.CommandRunner.Shells;
using Pillion.CommandRunner.Terminal;

namespace Pillion.CommandRunner;

/// <summary>The <see cref="ICommandSession"/> implementation; created by <see cref="CommandRunner"/>.</summary>
internal sealed class CommandSession : ICommandSession
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };

    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly ResolvedShell _shell;
    private readonly JobObject _job;
    private readonly TerminalScreen _terminal;
    private readonly Dictionary<string, string> _environment;
    private readonly List<string> _warnings;
    private readonly SemaphoreSlim _runGate = new(1, 1);
    private readonly object _gate = new();
    private readonly List<BackgroundProcess> _background = [];
    private ActiveRun? _current;
    private int _runCounter;
    private int _backgroundCounter;
    private bool _disposed;

    /// <summary>Initializes a new instance of the <see cref="CommandSession"/> class.</summary>
    /// <param name="options">The options.</param>
    /// <param name="shell">The resolved shell.</param>
    /// <param name="developerEnvironment">The captured developer environment, or <see langword="null"/> for the host's.</param>
    /// <param name="warnings">Set-up warnings.</param>
    /// <param name="loggerFactory">Where to log.</param>
    /// <param name="time">The clock.</param>
    internal CommandSession(
        CommandSessionOptions options,
        ResolvedShell shell,
        IReadOnlyDictionary<string, string>? developerEnvironment,
        IEnumerable<string> warnings,
        ILoggerFactory? loggerFactory,
        TimeProvider? time)
    {
        Options = options;
        _shell = shell;
        _logger = loggerFactory?.CreateLogger<CommandSession>() ?? NullLogger<CommandSession>.Instance;
        _time = time ?? TimeProvider.System;
        _warnings = [.. warnings];
        DeveloperEnvironmentLoaded = developerEnvironment is not null;
        Id = Guid.NewGuid().ToString("N")[..12];
        LogDirectory = options.LogDirectory ?? Path.Combine(Path.GetTempPath(), "Pillion", "command-runner", Id);

        _environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (developerEnvironment is not null)
        {
            foreach (var (key, value) in developerEnvironment)
            {
                _environment[key] = value;
            }
        }
        else
        {
            foreach (System.Collections.DictionaryEntry entry in System.Environment.GetEnvironmentVariables())
            {
                if (entry.Key is string key && entry.Value is string value)
                {
                    _environment[key] = value;
                }
            }
        }

        Apply(_environment, options.Environment);
        _terminal = new TerminalScreen(options.Columns, options.Rows, options.ScrollbackLines, RespondToTerminalQuery, _time);
        _job = JobObject.Create();
    }

    /// <inheritdoc/>
    public event EventHandler<PromptWaitingEventArgs>? PromptWaiting;

    /// <inheritdoc/>
    public string Id { get; }

    /// <inheritdoc/>
    public CommandSessionOptions Options { get; }

    /// <inheritdoc/>
    public IReadOnlyDictionary<string, string> Environment => _environment;

    /// <inheritdoc/>
    public bool DeveloperEnvironmentLoaded { get; }

    /// <inheritdoc/>
    public ShellKind Shell => _shell.Kind;

    /// <inheritdoc/>
    public IReadOnlyList<string> Warnings => _warnings;

    /// <inheritdoc/>
    public ITerminalBuffer Terminal => _terminal;

    /// <inheritdoc/>
    public string LogDirectory { get; }

    /// <inheritdoc/>
    public bool IsRunning => Volatile.Read(ref _current) is not null;

    /// <inheritdoc/>
    public IReadOnlyList<BackgroundProcessInfo> BackgroundProcesses
    {
        get
        {
            lock (_gate)
            {
                return _background.Select(process => process.Info()).ToList();
            }
        }
    }

    /// <inheritdoc/>
    public async Task<CommandResult> RunAsync(string command, CommandRunOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        ObjectDisposedException.ThrowIf(_disposed, this);
        options ??= new CommandRunOptions();

        if (!await _runGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("Another command is running in this session; commands run one at a time.");
        }

        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return await RunCoreAsync(command, options, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _runGate.Release();
        }
    }

    /// <inheritdoc/>
    public async Task WriteInputAsync(string text, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        var run = Volatile.Read(ref _current) ?? throw new InvalidOperationException("No command is running.");
        try
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            await run.Console.Input.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await run.Console.Input.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            throw new InvalidOperationException("The command is no longer accepting input.", exception);
        }
    }

    /// <inheritdoc/>
    public void Resize(int columns, int rows)
    {
        _terminal.Resize(columns, rows);
        var run = Volatile.Read(ref _current);
        if (run is null)
        {
            return;
        }

        try
        {
            run.Console.Resize(columns, rows);
        }
        catch (Win32Exception exception)
        {
            _logger.LogDebug(exception, "ResizePseudoConsole failed; the command has probably exited");
        }
    }

    /// <inheritdoc/>
    public async Task<BackgroundProcessInfo> StartBackgroundProcessAsync(BackgroundProcessOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (options.Port is { } port && (port < 1 || port > 65535))
        {
            throw new ArgumentOutOfRangeException(nameof(options), port, "The port must be between 1 and 65535.");
        }

        var workingDirectory = options.WorkingDirectory ?? Options.WorkingDirectory;
        if (!Directory.Exists(workingDirectory))
        {
            throw new DirectoryNotFoundException($"The working directory does not exist: {workingDirectory}");
        }

        var number = Interlocked.Increment(ref _backgroundCounter);
        var id = $"bg-{number}";
        var environment = new Dictionary<string, string>(_environment, StringComparer.OrdinalIgnoreCase);
        Apply(environment, options.Environment);
        var scriptPath = WriteScript($"background-{number}", options.Command);
        var commandLine = ShellLocator.CommandLine(_shell, scriptPath);

        var terminal = new TerminalScreen(Options.Columns, Options.Rows, Options.BackgroundScrollbackLines, null, _time);
        terminal.Write($"> {options.Command}\r\n".AsSpan());
        var started = Start(commandLine, workingDirectory, environment, terminal);
        var process = new BackgroundProcess(id, options, _time.GetUtcNow(), terminal, started.Console, started.Process, started.Job);
        process.Pump = StartPump(started.Console, terminal);
        lock (_gate)
        {
            _background.Add(process);
        }

        _ = WatchBackgroundExitAsync(process);
        _logger.LogInformation("Background process {Id} started (pid {Pid}): {Command}", id, started.Process.ProcessId, options.Command);

        try
        {
            var failure = await WaitForReadinessAsync(process, options, cancellationToken).ConfigureAwait(false);
            if (failure is null)
            {
                process.MarkReady();
                _logger.LogInformation("Background process {Id} is ready", id);
            }
            else if (process.State == BackgroundProcessState.Starting)
            {
                _logger.LogWarning("Background process {Id} failed readiness: {Failure}", id, failure);
                await StopAsync(process, failure).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            await StopAsync(process, "Cancelled while waiting for readiness.").ConfigureAwait(false);
            throw;
        }

        return process.Info();
    }

    /// <inheritdoc/>
    public async Task<bool> StopBackgroundProcessAsync(string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        BackgroundProcess? process;
        lock (_gate)
        {
            process = _background.FirstOrDefault(candidate => string.Equals(candidate.Id, id, StringComparison.Ordinal));
        }

        if (process is null)
        {
            return false;
        }

        await StopAsync(process, failure: null).WaitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // The running command, if any: its job dies and RunAsync completes (or throws, if its caller cancelled).
        Volatile.Read(ref _current)?.Job.Terminate();

        List<BackgroundProcess> background;
        lock (_gate)
        {
            background = [.. _background];
        }

        foreach (var process in background)
        {
            await StopAsync(process, failure: null).ConfigureAwait(false);
        }

        // Whatever is still in the session job (a run being torn down) dies with it.
        _job.Terminate();
        _job.Dispose();

        if (!Options.KeepLogs)
        {
            try
            {
                if (Directory.Exists(LogDirectory))
                {
                    Directory.Delete(LogDirectory, recursive: true);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _logger.LogDebug(exception, "The log folder {Path} could not be deleted", LogDirectory);
            }
        }
    }

    // ---- runs ----

    private async Task<CommandResult> RunCoreAsync(string command, CommandRunOptions options, CancellationToken cancellationToken)
    {
        var workingDirectory = options.WorkingDirectory ?? Options.WorkingDirectory;
        if (!Directory.Exists(workingDirectory))
        {
            throw new DirectoryNotFoundException($"The working directory does not exist: {workingDirectory}");
        }

        var number = Interlocked.Increment(ref _runCounter);
        var environment = new Dictionary<string, string>(_environment, StringComparer.OrdinalIgnoreCase);
        string? binaryLogPath = null;
        string? trxPath = null;
        var effectiveCommand = command;
        if (options.Kind is CommandKind.Build or CommandKind.Test)
        {
            // Structured logs instead of scraped text (design section 8); the Terminal Logger and colours would garble the view.
            environment["MSBUILDTERMINALLOGGER"] = "off";
            environment["NO_COLOR"] = "1";
            Directory.CreateDirectory(LogDirectory);
            if (options.Kind == CommandKind.Build)
            {
                binaryLogPath = Path.Combine(LogDirectory, $"run-{number}.binlog");
                effectiveCommand = $"{command} -bl:\"{binaryLogPath}\"";
            }
            else
            {
                trxPath = Path.Combine(LogDirectory, $"run-{number}.trx");
                effectiveCommand = $"{command} --logger \"trx;LogFileName={trxPath}\"";
            }
        }

        Apply(environment, options.Environment);
        var scriptPath = WriteScript($"run-{number}", effectiveCommand);
        var commandLine = ShellLocator.CommandLine(_shell, scriptPath);
        var timeout = options.Timeout ?? Options.Timeout;

        var mark = _terminal.BeginRun();
        _terminal.Write($"> {command}\r\n".AsSpan());

        var started = Start(commandLine, workingDirectory, environment, _terminal);
        var run = new ActiveRun(command, started.Console, started.Process, started.Job);
        Volatile.Write(ref _current, run);
        var pump = StartPump(started.Console, _terminal);
        using var promptStop = new CancellationTokenSource();
        var promptWatcher = WatchPromptAsync(run, promptStop.Token);
        var watch = Stopwatch.StartNew();
        _logger.LogInformation("Run {Number} started (pid {Pid}, timeout {Timeout}): {Command}", number, started.Process.ProcessId, timeout, command);

        var outcome = CommandOutcome.Completed;
        var exitCode = -1;
        var cancelled = false;
        try
        {
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (timeout != System.Threading.Timeout.InfiniteTimeSpan)
            {
                timeoutSource.CancelAfter(timeout);
            }

            try
            {
                exitCode = await started.Process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                var killed = KillTree(started.Process, started.Job);
                cancelled = cancellationToken.IsCancellationRequested;
                outcome = cancelled ? CommandOutcome.Completed : CommandOutcome.TimedOut;
                _logger.LogInformation("Run {Number} {Reason} after {Elapsed} ms; process tree killed: {Killed}", number, cancelled ? "cancelled" : "timed out", watch.ElapsedMilliseconds, killed);
                if (!killed)
                {
                    _warnings.Add($"Run {number}: the process tree did not die within {Options.KillTimeout.TotalSeconds:0.#} s of the kill.");
                }
            }
        }
        finally
        {
            watch.Stop();
            promptStop.Cancel();
            await promptWatcher.ConfigureAwait(false);
            await FinishPumpAsync(started.Console, pump).ConfigureAwait(false);
            Volatile.Write(ref _current, null);
            started.Job.Dispose();
            started.Process.Dispose();
        }

        if (cancelled)
        {
            throw new OperationCanceledException("The command was cancelled and its process tree killed.", cancellationToken);
        }

        var lines = _terminal.GetLinesToEnd(mark);
        var view = ModelView.Build(lines, options.ModelViewByteCap ?? Options.ModelViewByteCap);
        _logger.LogInformation("Run {Number} {Outcome} with exit code {ExitCode} in {Elapsed} ms; {Lines} lines, {Bytes} bytes to the model", number, outcome, exitCode, watch.ElapsedMilliseconds, lines.Count, view.ByteCount);
        return new CommandResult(
            command,
            commandLine,
            outcome,
            exitCode,
            watch.Elapsed,
            view,
            new TerminalRange(mark, mark + lines.Count),
            binaryLogPath,
            trxPath,
            [.. _warnings]);
    }

    private (PseudoConsole Console, ChildProcess Process, JobObject Job) Start(string commandLine, string workingDirectory, IReadOnlyDictionary<string, string> environment, TerminalScreen terminal)
    {
        var console = PseudoConsole.Create(terminal.Columns, terminal.Rows);
        ChildProcess? process = null;
        JobObject? job = null;
        try
        {
            process = ChildProcess.StartSuspended(commandLine, workingDirectory, environment, console);

            // The session job first (it nests in the host's own job), then the run's own job nested in the session's, so a
            // cancel kills one command's tree and the end of the session kills everything.
            _job.Assign(process.Handle);
            job = JobObject.Create();
            job.Assign(process.Handle);
            process.Resume();
            return (console, process, job);
        }
        catch
        {
            process?.Terminate();
            job?.Dispose();
            process?.Dispose();
            console.Dispose();
            throw;
        }
    }

    private bool KillTree(ChildProcess process, JobObject job)
    {
        job.Terminate();
        if (process.WaitForExit(Options.KillTimeout))
        {
            return true;
        }

        process.Terminate();
        job.Terminate();
        return process.WaitForExit(Options.KillTimeout);
    }

    private static Task StartPump(PseudoConsole console, TerminalScreen terminal)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var buffer = new byte[16 * 1024];
            try
            {
                while (true)
                {
                    var count = console.Output.Read(buffer, 0, buffer.Length);
                    if (count <= 0)
                    {
                        break;
                    }

                    terminal.Write(buffer.AsSpan(0, count));
                }
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException or OperationCanceledException)
            {
                // The pipe closed under the reader: the pseudo-console was disposed.
            }
            finally
            {
                completion.TrySetResult();
            }
        })
        {
            IsBackground = true,
            Name = "Pillion command output",
        };
        thread.Start();
        return completion.Task;
    }

    private static async Task FinishPumpAsync(PseudoConsole console, Task pump)
    {
        // Closing the pseudo-console gives the pump its end of stream once the pending output is drained.
        console.Close();
        try
        {
            await pump.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // Closing the output stream unblocks the reader.
        }

        console.Dispose();
    }

    private async Task WatchPromptAsync(ActiveRun run, CancellationToken cancellationToken)
    {
        var idle = Options.PromptIdle;
        var period = TimeSpan.FromMilliseconds(Math.Clamp(idle.TotalMilliseconds / 4, 50, 500));
        long lastRaised = -1;
        try
        {
            using var timer = new PeriodicTimer(period, _time);
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                var changedAt = _terminal.LastChangedAt;
                if (changedAt is null || _time.GetUtcNow() - changedAt.Value < idle)
                {
                    continue;
                }

                var generation = _terminal.Generation;
                if (generation == lastRaised || run.Process.HasExited)
                {
                    continue;
                }

                var (column, line, alternate) = _terminal.GetPromptState();
                if (column == 0 || alternate)
                {
                    continue;
                }

                lastRaised = generation;
                PromptWaiting?.Invoke(this, new PromptWaitingEventArgs(run.Command, line, generation));
            }
        }
        catch (OperationCanceledException)
        {
            // The run ended.
        }
    }

    private void RespondToTerminalQuery(string reply)
    {
        var run = Volatile.Read(ref _current);
        if (run is null)
        {
            return;
        }

        try
        {
            var bytes = Encoding.UTF8.GetBytes(reply);
            run.Console.Input.Write(bytes, 0, bytes.Length);
            run.Console.Input.Flush();
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            // The command has exited.
        }
    }

    private string WriteScript(string name, string command)
    {
        Directory.CreateDirectory(LogDirectory);
        var path = Path.Combine(LogDirectory, name + ShellLocator.ScriptExtension(_shell.Kind));
        // Windows PowerShell reads a UTF-8 script correctly only with a byte-order mark; cmd takes plain bytes.
        var encoding = _shell.Kind == ShellKind.Cmd ? new UTF8Encoding(false) : new UTF8Encoding(true);
        File.WriteAllText(path, ShellLocator.Script(_shell.Kind, command), encoding);
        return path;
    }

    private static void Apply(Dictionary<string, string> environment, Dictionary<string, string?> overrides)
    {
        foreach (var (key, value) in overrides)
        {
            if (value is null)
            {
                environment.Remove(key);
            }
            else
            {
                environment[key] = value;
            }
        }
    }

    // ---- background processes ----

    private async Task WatchBackgroundExitAsync(BackgroundProcess process)
    {
        try
        {
            var exitCode = await process.Process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            process.MarkExited(exitCode);
            _logger.LogInformation("Background process {Id} exited with {ExitCode}", process.Id, exitCode);
            await process.CloseAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is ObjectDisposedException or Win32Exception or InvalidOperationException)
        {
            // Stopped and disposed from the other side while waiting.
        }
    }

    private async Task<string?> WaitForReadinessAsync(BackgroundProcess process, BackgroundProcessOptions options, CancellationToken cancellationToken)
    {
        var deadline = _time.GetUtcNow() + options.ReadinessTimeout;
        var portReady = options.Port is null;
        var httpReady = options.HealthUrl is null;
        string? lastError = null;
        while (!portReady || !httpReady)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (process.State is not (BackgroundProcessState.Starting or BackgroundProcessState.Ready))
            {
                return process.Info().Failure ?? "The process exited before it was ready.";
            }

            if (_time.GetUtcNow() >= deadline)
            {
                var what = !portReady ? $"port {options.Port} did not accept a connection" : $"GET {options.HealthUrl} did not answer with a 2xx or 3xx status";
                return $"Not ready within {options.ReadinessTimeout.TotalSeconds:0} s: {what}{(lastError is null ? string.Empty : $" ({lastError})")}.";
            }

            if (!portReady)
            {
                portReady = await ProbePortAsync(options.Port!.Value, cancellationToken).ConfigureAwait(false);
            }
            else if (!httpReady)
            {
                (httpReady, lastError) = await ProbeHttpAsync(options.HealthUrl!, cancellationToken).ConfigureAwait(false);
            }

            if (!portReady || !httpReady)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), _time, cancellationToken).ConfigureAwait(false);
            }
        }

        return null;
    }

    private static async Task<bool> ProbePortAsync(int port, CancellationToken cancellationToken)
    {
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        attempt.CancelAfter(TimeSpan.FromSeconds(1));
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port, attempt.Token).ConfigureAwait(false);
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    private static async Task<(bool Ready, string? Error)> ProbeHttpAsync(Uri url, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            return status is >= 200 and < 400 ? (true, null) : (false, $"HTTP {status}");
        }
        catch (HttpRequestException exception)
        {
            return (false, exception.Message);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (false, "the request timed out");
        }
    }

    private async Task StopAsync(BackgroundProcess process, string? failure)
    {
        var alreadyDown = process.State is BackgroundProcessState.Exited or BackgroundProcessState.Failed or BackgroundProcessState.Stopped;
        if (!alreadyDown)
        {
            var killed = process.Kill(Options.KillTimeout);
            process.MarkStopped(failure);
            _logger.LogInformation("Background process {Id} stopped; process tree killed: {Killed}", process.Id, killed);
        }

        await process.CloseAsync().ConfigureAwait(false);
    }

    /// <summary>The command running now.</summary>
    private sealed record ActiveRun(string Command, PseudoConsole Console, ChildProcess Process, JobObject Job);
}
