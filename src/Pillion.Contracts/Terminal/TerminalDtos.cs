using System;
using System.Runtime.Serialization;

namespace Pillion.Contracts;

/// <summary>The shell a terminal runs (design section 8: PowerShell 7, Windows PowerShell or cmd). An integer on the wire. Added in contract 1.4.</summary>
public enum TerminalShell
{
    /// <summary>The user's configured default (PowerShell 7 when installed, otherwise Windows PowerShell).</summary>
    Default,

    /// <summary>PowerShell 7 (<c>pwsh.exe</c>).</summary>
    PowerShell7,

    /// <summary>Windows PowerShell 5.1 (<c>powershell.exe</c>).</summary>
    WindowsPowerShell,

    /// <summary>The command prompt (<c>cmd.exe</c>).</summary>
    Cmd,
}

/// <summary>Opens a developer shell (<see cref="ITerminalService.OpenDeveloperShellAsync"/>). Added in contract 1.4.</summary>
[DataContract]
public sealed class OpenDeveloperShellRequest
{
    /// <summary>Gets or sets the shell.</summary>
    [DataMember(Order = 0)]
    public TerminalShell Shell { get; set; }

    /// <summary>Gets or sets the working directory, or <see langword="null"/> for the solution root.</summary>
    [DataMember(Order = 1)]
    public string? WorkingDirectory { get; set; }

    /// <summary>Gets or sets the initial size in cells; 0 means the host default (120 by 30).</summary>
    [DataMember(Order = 2)]
    public int Columns { get; set; }

    /// <summary>Gets or sets the initial height in cells; 0 means the host default.</summary>
    [DataMember(Order = 3)]
    public int Rows { get; set; }
}

/// <summary>A terminal the host has open. Added in contract 1.4.</summary>
[DataContract]
public sealed class TerminalInfo
{
    /// <summary>Gets or sets the terminal id, unique within the host.</summary>
    [DataMember(Order = 0)]
    public string TerminalId { get; set; } = string.Empty;

    /// <summary>Gets or sets the agent session the terminal belongs to, or empty for a shell the user opened.</summary>
    [DataMember(Order = 1)]
    public string SessionId { get; set; } = string.Empty;

    /// <summary>Gets or sets the title for the Terminal window's tab (the command line, the shell's name, or the background process's).</summary>
    [DataMember(Order = 2)]
    public string Title { get; set; } = string.Empty;

    /// <summary>Gets or sets the shell.</summary>
    [DataMember(Order = 3)]
    public TerminalShell Shell { get; set; }

    /// <summary>Gets or sets the width in cells.</summary>
    [DataMember(Order = 4)]
    public int Columns { get; set; }

    /// <summary>Gets or sets the height in cells.</summary>
    [DataMember(Order = 5)]
    public int Rows { get; set; }

    /// <summary>Gets or sets the operating-system process id of the shell or command, or 0 once it has exited.</summary>
    [DataMember(Order = 6)]
    public int ProcessId { get; set; }

    /// <summary>Gets or sets a value indicating whether the process is still running.</summary>
    [DataMember(Order = 7)]
    public bool IsRunning { get; set; }

    /// <summary>Gets or sets the background process this terminal belongs to (<see cref="BackgroundProcessInfo.BackgroundProcessId"/>), or empty.</summary>
    [DataMember(Order = 8)]
    public string BackgroundProcessId { get; set; } = string.Empty;

    /// <summary>Gets or sets when the terminal was opened.</summary>
    [DataMember(Order = 9)]
    public DateTime OpenedUtc { get; set; }
}

/// <summary>
/// A terminal's rendered screen and the scrollback since the previous notification: the one argument of the host-to-VSIX
/// notification <c>terminalScreenChanged</c>, sent whenever the screen changes, at most 30 times a second per terminal
/// (coalesced), and the reply of <see cref="ITerminalService.GetScreenAsync"/> (then with the whole scrollback). Rendered by
/// the host's VT parser; the full buffer stays in the host, and the model sees a tail-truncated, redacted copy (design section
/// 8). Added in contract 1.4.
/// </summary>
[DataContract]
public sealed class TerminalScreen
{
    /// <summary>Gets or sets the terminal.</summary>
    [DataMember(Order = 0)]
    public string TerminalId { get; set; } = string.Empty;

    /// <summary>Gets or sets a counter the host increments per terminal on every notification, so a gap can be detected and the screen fetched again.</summary>
    [DataMember(Order = 1)]
    public long Sequence { get; set; }

    /// <summary>Gets or sets the width in cells.</summary>
    [DataMember(Order = 2)]
    public int Columns { get; set; }

    /// <summary>Gets or sets the height in cells.</summary>
    [DataMember(Order = 3)]
    public int Rows { get; set; }

    /// <summary>Gets or sets the screen, one string per row (<see cref="Rows"/> of them), without trailing spaces and without colour.</summary>
    [DataMember(Order = 4)]
    public string[] Lines { get; set; } = Array.Empty<string>();

    /// <summary>Gets or sets the cursor's zero-based row.</summary>
    [DataMember(Order = 5)]
    public int CursorRow { get; set; }

    /// <summary>Gets or sets the cursor's zero-based column.</summary>
    [DataMember(Order = 6)]
    public int CursorColumn { get; set; }

    /// <summary>
    /// Gets or sets the lines that scrolled off the top of the screen since the previous notification (every scrollback line in
    /// a <see cref="ITerminalService.GetScreenAsync"/> reply), oldest first; the window appends them to its scrollback.
    /// </summary>
    [DataMember(Order = 7)]
    public string[] ScrollbackDelta { get; set; } = Array.Empty<string>();

    /// <summary>Gets or sets the total number of lines that have scrolled off the top since the terminal opened, after this delta.</summary>
    [DataMember(Order = 8)]
    public long ScrollbackTotal { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the host detected a prompt waiting for input (no trailing newline and 5 s idle
    /// after the screen stopped changing; design section 8), which the Terminal window surfaces in its input line.
    /// </summary>
    [DataMember(Order = 9)]
    public bool IsWaitingForInput { get; set; }

    /// <summary>Gets or sets the exit code once the process has exited, or <see langword="null"/> while it runs.</summary>
    [DataMember(Order = 10)]
    public int? ExitCode { get; set; }

    /// <summary>Gets or sets the title the running program set (the window title sequence), or empty.</summary>
    [DataMember(Order = 11)]
    public string Title { get; set; } = string.Empty;
}

/// <summary>Where a background process is in its life (design section 8, <c>start_background_process</c>). An integer on the wire. Added in contract 1.4.</summary>
public enum BackgroundProcessStatus
{
    /// <summary>Started; the port probe or the HTTP check has not succeeded yet.</summary>
    Starting,

    /// <summary>The port answered and the HTTP check succeeded; the agent and the browser may use it.</summary>
    Ready,

    /// <summary>The process exited on its own; <see cref="BackgroundProcessInfo.ExitCode"/> says how.</summary>
    Exited,

    /// <summary>Stopped by <c>stop_background_process</c>, <see cref="ITerminalService.CloseTerminalAsync"/> or the end of the session.</summary>
    Stopped,

    /// <summary>Never became ready within the readiness timeout and was killed; <see cref="BackgroundProcessInfo.Message"/> says why.</summary>
    Failed,
}

/// <summary>
/// A background process of an agent session (an app server started with <c>start_background_process</c>): the one argument of the
/// host-to-VSIX notification <c>backgroundProcessChanged</c>, sent on every change of <see cref="Status"/>, and listed in the
/// Agent window. Added in contract 1.4.
/// </summary>
[DataContract]
public sealed class BackgroundProcessInfo
{
    /// <summary>Gets or sets Pillion's id for the process, unique within the host (not the operating-system process id).</summary>
    [DataMember(Order = 0)]
    public string BackgroundProcessId { get; set; } = string.Empty;

    /// <summary>Gets or sets the session that started it.</summary>
    [DataMember(Order = 1)]
    public string SessionId { get; set; } = string.Empty;

    /// <summary>Gets or sets the terminal that shows its output.</summary>
    [DataMember(Order = 2)]
    public string TerminalId { get; set; } = string.Empty;

    /// <summary>Gets or sets the exact command line, as shown before it ran.</summary>
    [DataMember(Order = 3)]
    public string CommandLine { get; set; } = string.Empty;

    /// <summary>Gets or sets the state.</summary>
    [DataMember(Order = 4)]
    public BackgroundProcessStatus Status { get; set; }

    /// <summary>Gets or sets the port the readiness probe waits for, or 0 when none.</summary>
    [DataMember(Order = 5)]
    public int Port { get; set; }

    /// <summary>Gets or sets the URL the HTTP check requests (and the browser opens), or <see langword="null"/> when none.</summary>
    [DataMember(Order = 6)]
    public string? Url { get; set; }

    /// <summary>Gets or sets the operating-system process id, or 0 once it has exited.</summary>
    [DataMember(Order = 7)]
    public int ProcessId { get; set; }

    /// <summary>Gets or sets when it was started.</summary>
    [DataMember(Order = 8)]
    public DateTime StartedUtc { get; set; }

    /// <summary>Gets or sets the exit code, or <see langword="null"/> while it runs or when it was killed.</summary>
    [DataMember(Order = 9)]
    public int? ExitCode { get; set; }

    /// <summary>Gets or sets why it failed or was stopped, or <see langword="null"/>.</summary>
    [DataMember(Order = 10)]
    public string? Message { get; set; }
}
