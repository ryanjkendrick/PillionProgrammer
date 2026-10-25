using Pillion.CommandRunner.Terminal;

namespace Pillion.CommandRunner;

/// <summary>
/// One agent session's command runner (design section 8, F7): one Job Object, one working directory, one environment
/// and one shell, one terminal buffer that every command's output lands in, and the background processes started for
/// the session. Commands run one at a time; a cancel or a timeout kills the whole process tree of the command within
/// <see cref="CommandSessionOptions.KillTimeout"/>; disposing the session kills everything it started.
/// </summary>
public interface ICommandSession : IAsyncDisposable
{
    /// <summary>Raised when the running command seems to wait for input: the screen stopped changing for <see cref="CommandSessionOptions.PromptIdle"/> and the cursor sits after text on its line. Raised once per quiet period; the agent never answers it, the Terminal window's input line does.</summary>
    event EventHandler<PromptWaitingEventArgs>? PromptWaiting;

    /// <summary>Gets the session id (also the name of its log folder).</summary>
    string Id { get; }

    /// <summary>Gets the options the session was created with.</summary>
    CommandSessionOptions Options { get; }

    /// <summary>Gets the environment every command starts with: the Developer PowerShell environment when it loaded, the host's otherwise, with the session's overrides applied.</summary>
    IReadOnlyDictionary<string, string> Environment { get; }

    /// <summary>Gets a value indicating whether the Developer PowerShell environment of <see cref="CommandSessionOptions.VisualStudioInstallationPath"/> was loaded.</summary>
    bool DeveloperEnvironmentLoaded { get; }

    /// <summary>Gets the shell actually used (<see cref="ShellKind.PowerShell7"/> falls back to <see cref="ShellKind.WindowsPowerShell"/> when <c>pwsh.exe</c> is missing).</summary>
    ShellKind Shell { get; }

    /// <summary>Gets the warnings raised while the session was set up (a missing developer environment, a shell fallback).</summary>
    IReadOnlyList<string> Warnings { get; }

    /// <summary>Gets the full terminal buffer for the Terminal window: every command's output, the screen and the scrollback, unredacted.</summary>
    ITerminalBuffer Terminal { get; }

    /// <summary>Gets the folder with the per-run scripts, binary logs and TRX files.</summary>
    string LogDirectory { get; }

    /// <summary>Gets a value indicating whether a command is running.</summary>
    bool IsRunning { get; }

    /// <summary>Gets the background processes started in this session, in start order, with their current state.</summary>
    IReadOnlyList<BackgroundProcessInfo> BackgroundProcesses { get; }

    /// <summary>
    /// Runs a command in the session's shell on the pseudo-console and waits for it to exit, the timeout or the token.
    /// </summary>
    /// <param name="command">The command, as typed at the shell's prompt. It is shown in the buffer first.</param>
    /// <param name="options">Per-run overrides; <see langword="null"/> for the session's defaults.</param>
    /// <param name="cancellationToken">A cancel kills the command's process tree and then throws <see cref="OperationCanceledException"/>.</param>
    /// <returns>The exit code, the duration, the model's view and the handle to the full output. A timeout is a result, not an exception.</returns>
    /// <exception cref="OperationCanceledException">The token was cancelled; the process tree is dead when this is thrown.</exception>
    /// <exception cref="InvalidOperationException">Another command is running.</exception>
    /// <exception cref="ObjectDisposedException">The session was disposed.</exception>
    Task<CommandResult> RunAsync(string command, CommandRunOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>Writes keyboard input to the running command (answering a prompt). Enter is <c>"\r"</c>.</summary>
    /// <param name="text">The text.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task that completes when the input was handed to the console.</returns>
    /// <exception cref="InvalidOperationException">No command is running.</exception>
    Task WriteInputAsync(string text, CancellationToken cancellationToken = default);

    /// <summary>Changes the terminal size for the buffer and the running command, if any.</summary>
    /// <param name="columns">The width in cells.</param>
    /// <param name="rows">The height in rows.</param>
    void Resize(int columns, int rows);

    /// <summary>
    /// Starts a process that lives for the length of the session (an app server) and waits until it is ready: the port
    /// accepts a connection, then the health URL answers, whichever are given, within the readiness timeout.
    /// </summary>
    /// <param name="options">The command and the readiness checks.</param>
    /// <param name="cancellationToken">A cancel stops the process.</param>
    /// <returns>The process as <see cref="BackgroundProcessState.Ready"/>, or <see cref="BackgroundProcessState.Failed"/> with the reason and its output when it exited first or was not ready in time (it is stopped then).</returns>
    Task<BackgroundProcessInfo> StartBackgroundProcessAsync(BackgroundProcessOptions options, CancellationToken cancellationToken = default);

    /// <summary>Stops a background process (its whole tree) and waits for it to die.</summary>
    /// <param name="id">The id from <see cref="BackgroundProcessInfo.Id"/>.</param>
    /// <param name="cancellationToken">Cancels the wait, not the kill.</param>
    /// <returns><see langword="true"/> when the process existed and is now stopped; <see langword="false"/> for an unknown id.</returns>
    Task<bool> StopBackgroundProcessAsync(string id, CancellationToken cancellationToken = default);
}

/// <summary>Creates command sessions. Registered in Pillion Host behind <c>features.commandRunner</c>.</summary>
public interface ICommandRunner
{
    /// <summary>
    /// Creates a session: resolves the shell, loads the Developer PowerShell environment (once per Visual Studio
    /// installation per host; later sessions reuse it) and creates the session's Job Object. Nothing runs yet.
    /// </summary>
    /// <param name="options">The session options.</param>
    /// <param name="cancellationToken">Cancels the environment capture.</param>
    /// <returns>The session.</returns>
    /// <exception cref="Pillion.Contracts.PillionRpcException"><c>features.commandRunner</c> is off (<c>PolicyDenied</c>).</exception>
    /// <exception cref="DirectoryNotFoundException">The working directory does not exist.</exception>
    Task<ICommandSession> CreateSessionAsync(CommandSessionOptions options, CancellationToken cancellationToken = default);
}
