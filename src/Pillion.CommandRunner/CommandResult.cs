using Pillion.CommandRunner.Terminal;

namespace Pillion.CommandRunner;

/// <summary>How a run ended.</summary>
public enum CommandOutcome
{
    /// <summary>The command exited on its own; <see cref="CommandResult.ExitCode"/> is its exit code.</summary>
    Completed,

    /// <summary>The timeout expired and the process tree was killed.</summary>
    TimedOut,
}

/// <summary>A range of history indices in a session's <see cref="ITerminalBuffer"/>: a handle to a run's full output.</summary>
/// <param name="Start">The history index of the run's first line.</param>
/// <param name="End">One past the history index of the run's last line.</param>
public readonly record struct TerminalRange(long Start, long End)
{
    /// <summary>Gets the number of lines in the range.</summary>
    public long Length => End - Start;
}

/// <summary>The result of <see cref="ICommandSession.RunAsync"/>.</summary>
public sealed class CommandResult
{
    /// <summary>Initializes a new instance of the <see cref="CommandResult"/> class.</summary>
    /// <param name="command">The command as given.</param>
    /// <param name="commandLine">The command line that was started.</param>
    /// <param name="outcome">How the run ended.</param>
    /// <param name="exitCode">The exit code, or -1 after a timeout.</param>
    /// <param name="duration">The wall time from start to exit.</param>
    /// <param name="modelView">The model's view.</param>
    /// <param name="output">The run's lines in the session buffer.</param>
    /// <param name="binaryLogPath">The binary log of a build.</param>
    /// <param name="trxPath">The TRX file of a test run.</param>
    /// <param name="warnings">Warnings about the run.</param>
    public CommandResult(
        string command,
        string commandLine,
        CommandOutcome outcome,
        int exitCode,
        TimeSpan duration,
        ModelView modelView,
        TerminalRange output,
        string? binaryLogPath,
        string? trxPath,
        IReadOnlyList<string> warnings)
    {
        Command = command;
        CommandLine = commandLine;
        Outcome = outcome;
        ExitCode = exitCode;
        Duration = duration;
        ModelView = modelView;
        Output = output;
        BinaryLogPath = binaryLogPath;
        TrxPath = trxPath;
        Warnings = warnings;
    }

    /// <summary>Gets the command as the caller gave it (what the ledger shows).</summary>
    public string Command { get; }

    /// <summary>Gets the exact command line that was started: the shell, its switches and the script that runs the command.</summary>
    public string CommandLine { get; }

    /// <summary>Gets how the run ended.</summary>
    public CommandOutcome Outcome { get; }

    /// <summary>Gets the exit code (-1 after a timeout).</summary>
    public int ExitCode { get; }

    /// <summary>Gets a value indicating whether the command completed with exit code 0.</summary>
    public bool Succeeded => Outcome == CommandOutcome.Completed && ExitCode == 0;

    /// <summary>Gets the wall time from start to exit (or to the kill).</summary>
    public TimeSpan Duration { get; }

    /// <summary>Gets the model's view: the run's lines, tail-truncated to the byte cap and passed through the secret scanner.</summary>
    public ModelView ModelView { get; }

    /// <summary>Gets the run's lines in the session's <see cref="ICommandSession.Terminal"/> (the full, unredacted output for the Terminal window).</summary>
    public TerminalRange Output { get; }

    /// <summary>Gets the path of the binary log a <see cref="CommandKind.Build"/> was asked to write, or <see langword="null"/>. The file exists only if the build tool honoured <c>-bl</c>.</summary>
    public string? BinaryLogPath { get; }

    /// <summary>Gets the path of the TRX file a <see cref="CommandKind.Test"/> was asked to write, or <see langword="null"/>. The file exists only if the test tool honoured the logger.</summary>
    public string? TrxPath { get; }

    /// <summary>Gets warnings about the run (a shell fallback, a missing developer environment).</summary>
    public IReadOnlyList<string> Warnings { get; }
}

/// <summary>The state of a background process.</summary>
public enum BackgroundProcessState
{
    /// <summary>Started; readiness not yet established.</summary>
    Starting,

    /// <summary>The port accepted a connection and the health URL answered (whichever were asked for).</summary>
    Ready,

    /// <summary>It exited on its own.</summary>
    Exited,

    /// <summary>It was stopped by <see cref="ICommandSession.StopBackgroundProcessAsync"/> or at the end of the session.</summary>
    Stopped,

    /// <summary>It exited before it was ready, or readiness timed out and it was stopped. See <see cref="BackgroundProcessSnapshot.Failure"/>.</summary>
    Failed,
}

/// <summary>A background process of a session, as listed in the Agent window.</summary>
/// <param name="Id">The id to stop it with.</param>
/// <param name="ProcessId">The shell's process id.</param>
/// <param name="Command">The command.</param>
/// <param name="Port">The port probed for readiness, if any.</param>
/// <param name="HealthUrl">The URL checked for readiness, if any.</param>
/// <param name="State">The state.</param>
/// <param name="StartedAt">When it was started.</param>
/// <param name="ExitCode">The exit code once it has exited.</param>
/// <param name="Failure">Why it is <see cref="BackgroundProcessState.Failed"/>.</param>
/// <param name="Output">Its own terminal buffer (full, unredacted).</param>
public sealed record BackgroundProcessSnapshot(
    string Id,
    int ProcessId,
    string Command,
    int? Port,
    Uri? HealthUrl,
    BackgroundProcessState State,
    DateTimeOffset StartedAt,
    int? ExitCode,
    string? Failure,
    ITerminalBuffer Output);

/// <summary>Raised when a running command seems to wait for input (design section 8: no trailing newline and 5 s idle).</summary>
public sealed class PromptWaitingEventArgs : EventArgs
{
    /// <summary>Initializes a new instance of the <see cref="PromptWaitingEventArgs"/> class.</summary>
    /// <param name="command">The command that is waiting.</param>
    /// <param name="promptLine">The text of the cursor line (the prompt, usually), unredacted.</param>
    /// <param name="generation">The buffer generation the prompt was seen at.</param>
    public PromptWaitingEventArgs(string command, string promptLine, long generation)
    {
        Command = command;
        PromptLine = promptLine;
        Generation = generation;
    }

    /// <summary>Gets the command that is waiting.</summary>
    public string Command { get; }

    /// <summary>Gets the text of the cursor line.</summary>
    public string PromptLine { get; }

    /// <summary>Gets the buffer generation the prompt was seen at; another event follows only after the screen changes again.</summary>
    public long Generation { get; }
}
