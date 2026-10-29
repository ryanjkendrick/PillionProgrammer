using Pillion.CommandRunner.Native;
using Pillion.CommandRunner.Terminal;

namespace Pillion.CommandRunner;

/// <summary>A background process of a session: its pseudo-console, its own buffer and Job Object, and its state.</summary>
internal sealed class BackgroundProcess : IDisposable
{
    private readonly object _gate = new();
    private BackgroundProcessState _state = BackgroundProcessState.Starting;
    private int? _exitCode;
    private string? _failure;
    private bool _disposed;

    /// <summary>Initializes a new instance of the <see cref="BackgroundProcess"/> class.</summary>
    /// <param name="id">The id.</param>
    /// <param name="options">The start options.</param>
    /// <param name="startedAt">When it was started.</param>
    /// <param name="terminal">Its buffer.</param>
    /// <param name="console">Its pseudo-console.</param>
    /// <param name="process">The shell process.</param>
    /// <param name="job">Its own kill-on-close job, nested in the session's.</param>
    public BackgroundProcess(string id, BackgroundProcessOptions options, DateTimeOffset startedAt, TerminalBuffer terminal, PseudoConsole console, ChildProcess process, JobObject job)
    {
        Id = id;
        Options = options;
        StartedAt = startedAt;
        Terminal = terminal;
        Console = console;
        Process = process;
        Job = job;
    }

    /// <summary>Gets the id.</summary>
    public string Id { get; }

    /// <summary>Gets the start options.</summary>
    public BackgroundProcessOptions Options { get; }

    /// <summary>Gets when it was started.</summary>
    public DateTimeOffset StartedAt { get; }

    /// <summary>Gets its buffer.</summary>
    public TerminalBuffer Terminal { get; }

    /// <summary>Gets its pseudo-console.</summary>
    public PseudoConsole Console { get; }

    /// <summary>Gets the shell process.</summary>
    public ChildProcess Process { get; }

    /// <summary>Gets its job.</summary>
    public JobObject Job { get; }

    /// <summary>Gets or sets the task that drains the output pipe; completes at end of stream.</summary>
    public Task Pump { get; set; } = Task.CompletedTask;

    /// <summary>Gets the state.</summary>
    public BackgroundProcessState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    /// <summary>Takes a snapshot for the caller.</summary>
    /// <returns>The info.</returns>
    public BackgroundProcessSnapshot Info()
    {
        lock (_gate)
        {
            return new BackgroundProcessSnapshot(Id, Process.ProcessId, Options.Command, Options.Port, Options.HealthUrl, _state, StartedAt, _exitCode, _failure, Terminal);
        }
    }

    /// <summary>Marks it ready.</summary>
    public void MarkReady()
    {
        lock (_gate)
        {
            if (_state == BackgroundProcessState.Starting)
            {
                _state = BackgroundProcessState.Ready;
            }
        }
    }

    /// <summary>Records that it exited on its own.</summary>
    /// <param name="exitCode">The exit code.</param>
    public void MarkExited(int exitCode)
    {
        lock (_gate)
        {
            _exitCode = exitCode;
            if (_state == BackgroundProcessState.Starting)
            {
                _state = BackgroundProcessState.Failed;
                _failure = $"The process exited with code {exitCode} before it was ready.";
            }
            else if (_state == BackgroundProcessState.Ready)
            {
                _state = BackgroundProcessState.Exited;
            }
        }
    }

    /// <summary>Records that it was stopped, or that it failed if a reason is given.</summary>
    /// <param name="failure">Why it failed, or <see langword="null"/> for a plain stop.</param>
    public void MarkStopped(string? failure)
    {
        lock (_gate)
        {
            if (_state is BackgroundProcessState.Starting or BackgroundProcessState.Ready)
            {
                _state = failure is null ? BackgroundProcessState.Stopped : BackgroundProcessState.Failed;
                _failure = failure;
            }
        }
    }

    /// <summary>Kills the process tree and waits for the shell to die.</summary>
    /// <param name="killTimeout">How long to wait.</param>
    /// <returns><see langword="true"/> when the shell is gone.</returns>
    public bool Kill(TimeSpan killTimeout)
    {
        Job.Terminate();
        if (Process.WaitForExit(killTimeout))
        {
            return true;
        }

        Process.Terminate();
        Job.Terminate();
        return Process.WaitForExit(killTimeout);
    }

    /// <summary>Closes the console, drains the output and releases the handles. Idempotent.</summary>
    /// <returns>A task that completes when the output pump has finished.</returns>
    public async Task CloseAsync()
    {
        if (_disposed)
        {
            return;
        }

        await OutputDrain.FinishAsync(Console, Terminal, Pump).ConfigureAwait(false);
        Dispose();
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Console.Dispose();
        Job.Dispose();
        Process.Dispose();
    }
}
