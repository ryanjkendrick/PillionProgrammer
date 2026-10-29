namespace Pillion.CommandRunner;

/// <summary>The shell a session runs its commands in.</summary>
public enum ShellKind
{
    /// <summary>PowerShell 7 (<c>pwsh.exe</c>). Falls back to Windows PowerShell with a warning when it is not installed.</summary>
    PowerShell7,

    /// <summary>Windows PowerShell 5.1 (<c>powershell.exe</c>).</summary>
    WindowsPowerShell,

    /// <summary>The command processor (<c>cmd.exe</c>).</summary>
    Cmd,
}

/// <summary>What a command is, which decides the environment and the structured logs it gets (design section 8).</summary>
public enum CommandKind
{
    /// <summary>An ordinary command: the environment as the session has it.</summary>
    Command,

    /// <summary>
    /// A build (<c>dotnet build</c>, <c>msbuild</c>): <c>MSBUILDTERMINALLOGGER=off</c>, <c>NO_COLOR=1</c> and a binary log
    /// (<c>-bl:&lt;path&gt;</c>) appended to the command, its path returned in the result.
    /// </summary>
    Build,

    /// <summary>
    /// A test run (<c>dotnet test</c>): <c>MSBUILDTERMINALLOGGER=off</c>, <c>NO_COLOR=1</c> and a TRX logger
    /// (<c>--logger "trx;LogFileName=&lt;path&gt;"</c>) appended to the command, its path returned in the result.
    /// </summary>
    Test,
}

/// <summary>
/// How a <see cref="ICommandSession"/> is set up: where commands start, which shell runs them, which environment they
/// get, how big the terminal is and the limits that bound every run.
/// </summary>
public sealed class CommandSessionOptions
{
    /// <summary>The default timeout of a command (design section 8: ordinary commands time out after 10 minutes).</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(10);

    /// <summary>The default idle time after which an unterminated last line counts as a prompt waiting for input.</summary>
    public static readonly TimeSpan DefaultPromptIdle = TimeSpan.FromSeconds(5);

    /// <summary>The default time the whole process tree is given to die after a cancel or a timeout.</summary>
    public static readonly TimeSpan DefaultKillTimeout = TimeSpan.FromSeconds(1);

    /// <summary>The default byte cap of the model's view (design section 8: tool results are capped at 16 KB).</summary>
    public const int DefaultModelViewByteCap = 16 * 1024;

    /// <summary>Initializes a new instance of the <see cref="CommandSessionOptions"/> class.</summary>
    /// <param name="workingDirectory">The directory commands start in: the solution root.</param>
    public CommandSessionOptions(string workingDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        WorkingDirectory = workingDirectory;
    }

    /// <summary>Gets the directory commands start in.</summary>
    public string WorkingDirectory { get; }

    /// <summary>Gets or sets the shell. Default <see cref="ShellKind.PowerShell7"/>.</summary>
    public ShellKind Shell { get; set; } = ShellKind.PowerShell7;

    /// <summary>
    /// Gets or sets the installation path of the Visual Studio that launched the host (from the handshake), whose
    /// Developer PowerShell environment the session loads. <see langword="null"/> or a path without
    /// <c>Common7\Tools\VsDevCmd.bat</c> gives the plain environment and a warning.
    /// </summary>
    public string? VisualStudioInstallationPath { get; set; }

    /// <summary>Gets or sets a value indicating whether the Developer PowerShell environment is loaded at all. Default on.</summary>
    public bool LoadDeveloperEnvironment { get; set; } = true;

    /// <summary>Gets the variables set on top of the captured environment for every command; a <see langword="null"/> value removes a variable.</summary>
    public Dictionary<string, string?> Environment { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Gets or sets the terminal width in cells. Default 120.</summary>
    public int Columns { get; set; } = 120;

    /// <summary>Gets or sets the terminal height in rows. Default 40.</summary>
    public int Rows { get; set; } = 40;

    /// <summary>Gets or sets the number of lines kept after they scroll off the screen. Default 10,000.</summary>
    public int ScrollbackLines { get; set; } = 10_000;

    /// <summary>Gets or sets the scrollback kept for each background process's own buffer. Default 2,000.</summary>
    public int BackgroundScrollbackLines { get; set; } = 2_000;

    /// <summary>Gets or sets the timeout of a command when the run does not name one. Default 10 minutes; <see cref="Timeout.InfiniteTimeSpan"/> for none.</summary>
    public TimeSpan Timeout { get; set; } = DefaultTimeout;

    /// <summary>Gets or sets the byte cap of the model's view of a run. Default 16 KB.</summary>
    public int ModelViewByteCap { get; set; } = DefaultModelViewByteCap;

    /// <summary>Gets or sets how long the screen must stay unchanged, with the cursor after text on its line, before <see cref="ICommandSession.PromptWaiting"/> is raised. Default 5 s.</summary>
    public TimeSpan PromptIdle { get; set; } = DefaultPromptIdle;

    /// <summary>Gets or sets how long a cancel or a timeout waits for the process tree to die before reporting it. Default 1 s.</summary>
    public TimeSpan KillTimeout { get; set; } = DefaultKillTimeout;

    /// <summary>
    /// Gets or sets the folder for the per-run script files, binary logs and TRX files. Default: a folder named after the
    /// session under <c>%TEMP%\Pillion\command-runner</c>, deleted when the session is disposed unless <see cref="KeepLogs"/>.
    /// </summary>
    public string? LogDirectory { get; set; }

    /// <summary>Gets or sets a value indicating whether the log folder is kept after the session is disposed. Default off.</summary>
    public bool KeepLogs { get; set; }
}

/// <summary>Per-run overrides of a session's defaults.</summary>
public sealed class CommandRunOptions
{
    /// <summary>Gets or sets what the command is. Default <see cref="CommandKind.Command"/>.</summary>
    public CommandKind Kind { get; set; }

    /// <summary>Gets or sets the timeout; <see langword="null"/> for the session's.</summary>
    public TimeSpan? Timeout { get; set; }

    /// <summary>Gets or sets the starting directory; <see langword="null"/> for the session's.</summary>
    public string? WorkingDirectory { get; set; }

    /// <summary>Gets the variables set for this run on top of the session's environment; a <see langword="null"/> value removes a variable.</summary>
    public Dictionary<string, string?> Environment { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Gets or sets the byte cap of the model's view; <see langword="null"/> for the session's.</summary>
    public int? ModelViewByteCap { get; set; }
}

/// <summary>How a background process is started and when it counts as ready.</summary>
public sealed class BackgroundProcessOptions
{
    /// <summary>Initializes a new instance of the <see cref="BackgroundProcessOptions"/> class.</summary>
    /// <param name="command">The command that starts the process (an app server, typically).</param>
    public BackgroundProcessOptions(string command)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        Command = command;
    }

    /// <summary>Gets the command.</summary>
    public string Command { get; }

    /// <summary>Gets or sets the TCP port on the loopback interface that must accept a connection before the process is ready; <see langword="null"/> to skip the probe.</summary>
    public int? Port { get; set; }

    /// <summary>Gets or sets the URL whose <c>GET</c> must answer with a 2xx or 3xx status before the process is ready; probed after <see cref="Port"/>. <see langword="null"/> to skip.</summary>
    public Uri? HealthUrl { get; set; }

    /// <summary>Gets or sets how long readiness may take. On expiry the process is stopped and reported as failed. Default 60 s.</summary>
    public TimeSpan ReadinessTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Gets or sets the starting directory; <see langword="null"/> for the session's.</summary>
    public string? WorkingDirectory { get; set; }

    /// <summary>Gets the variables set for this process on top of the session's environment.</summary>
    public Dictionary<string, string?> Environment { get; } = new(StringComparer.OrdinalIgnoreCase);
}
