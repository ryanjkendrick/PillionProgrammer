# Pillion.CommandRunner

The Command Runner library (design sections 8 and 19, work item 3.11): shell commands for agent sessions, run under a
Windows pseudo-console (ConPTY) inside a Job Object, rendered through a VT parser into a bounded screen buffer with
scrollback, and handed to the model as a tail-truncated, redacted view. It is a .NET 10 class library with no Visual
Studio dependency and no third-party package: ConPTY and Job Objects are Windows APIs, called through `LibraryImport`.
Pillion Host references it behind `features.commandRunner` and composes it as `HostCommandSessions`
(`src/Pillion.Host/CommandSessions`), which fills in the launching Visual Studio's installation path from the handshake. The
agent tools (`run_command`, `run_build`, `run_tests`, the background-process tools; work item 3.12) and the Terminal
window (`ITerminalService`, `terminalScreenChanged`; work item 3.15) are built on it; this library has no RPC surface of
its own. The user-facing description is [docs/command-runner.md](../../docs/command-runner.md).

## Public API

```text
ICommandRunner                      registered in the host; CreateSessionAsync(options) -> ICommandSession
  CommandRunner                     the implementation; refuses with PolicyDenied when features.commandRunner is off
  CommandRunnerServiceCollectionExtensions.AddPillionCommandRunner()

CommandSessionOptions               working directory (required), Shell (PowerShell7 | WindowsPowerShell | Cmd),
                                    VisualStudioInstallationPath, LoadDeveloperEnvironment, Environment overrides,
                                    Columns (120) x Rows (40), ScrollbackLines (10,000), Timeout (10 min),
                                    ModelViewByteCap (16 KB), PromptIdle (5 s), KillTimeout (1 s), LogDirectory, KeepLogs

ICommandSession : IAsyncDisposable  one Job Object, one environment, one shell, one terminal buffer per agent session
  RunAsync(command, CommandRunOptions?, ct)                -> CommandResult
  WriteInputAsync(text, ct)                                keyboard input to the running command ("\r" is Enter)
  Resize(columns, rows)
  StartBackgroundProcessAsync(BackgroundProcessOptions, ct) -> BackgroundProcessSnapshot
  StopBackgroundProcessAsync(id, ct)                       -> bool
  BackgroundProcesses                                      the snapshots, in start order
  event PromptWaiting(PromptWaitingEventArgs)              the command seems to wait for input
  Terminal : ITerminalBuffer                               the full buffer for the Terminal window
  Environment, DeveloperEnvironmentLoaded, Shell, Warnings, LogDirectory, IsRunning, Id

CommandRunOptions                   Kind (Command | Build | Test), Timeout, WorkingDirectory, Environment, ModelViewByteCap
CommandResult                       Command, CommandLine, Outcome (Completed | TimedOut), ExitCode, Duration,
                                    ModelView, Output (TerminalRange into Terminal), BinaryLogPath, TrxPath, Warnings
ModelView                           Text, TotalLines, IncludedLines, OmittedLines, Truncated, Redacted, ByteCount;
                                    static Build(lines, byteCap) and Build(buffer, fromLine, byteCap)

ITerminalBuffer                     Columns, Rows, Generation, FirstRowIndex, EndLineIndex, OldestLineIndex, Title,
                                    LastChangedAt, Changed event, Snapshot(), GetLines(from, toExclusive),
                                    GetLinesToEnd(from), RenderScreenText()
TerminalSnapshot, TerminalLine, TerminalSpan, CellAttributes, TerminalColor, CellStyle

DeveloperEnvironment                static CaptureAsync(installationPath) -> DeveloperEnvironmentResult; FindScript(path)
DeveloperEnvironmentCache           one capture per installation per host; shared singleton
VisualStudioLocator                 FindNewestInstallation() through vswhere.exe, the fallback when the handshake names none
```

## How a command runs

1. `RunAsync` writes the command into a script file in the session's log folder (`run-<n>.ps1` or `.cmd`) and starts
   the shell on it: `pwsh.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File <script>` (Windows PowerShell with the
   same switches; `cmd.exe /d /c <script>`). A script file sidesteps the quoting rules of `-Command` and `/c`, so the
   command runs exactly as the caller wrote it. The PowerShell script ends with `exit $LASTEXITCODE` when a native
   command set one, otherwise `exit 0` or `exit 1` from `$?`; the batch file ends with `exit /b %ERRORLEVEL%`.
   `CommandResult.CommandLine` is the exact command line that was started; `CommandResult.Command` is what the caller
   gave, which is what the ledger shows.
2. The process is created suspended on a fresh pseudo-console the size of the buffer, with
   `STARTF_USESTDHANDLES` and no handles (otherwise a parent whose own standard handles are redirected, such as the host
   under devenv.exe, hands copies of them to the child, whose output then bypasses the console), with the session's
   environment block and working directory. It is assigned to the session's Job Object and then to a Job Object of its
   own nested in the session's, and resumed. Jobs nest: the host's process is already in the job devenv.exe created
   for it, the session's job nests in that, the run's in the session's.
3. A pump thread reads the pseudo-console's output into the `TerminalBuffer`; a watcher raises `PromptWaiting` when the
   buffer has not changed for `PromptIdle` and the cursor sits after text on its line (no trailing newline) on the main
   screen. `WriteInputAsync` writes to the console's input pipe; terminal queries the application sends (cursor position,
   device attributes, window size) are answered by the parser through the same pipe.
4. On exit the output is left to settle (conhost renders asynchronously; closing the pseudo-console too early discards
   what it has not rendered), the pseudo-console is closed and drained, and the result is built: the run's lines from
   the buffer (`Output` is their history range), `ModelView.Build` over them, the exit code and the duration.
5. A timeout (`CommandRunOptions.Timeout`, else the session's, default 10 minutes) terminates the run's Job Object, waits
   `KillTimeout` (1 s) for the shell to go, and returns `Outcome = TimedOut` with exit code -1 and the partial view. A
   cancelled token does the same kill and then throws `OperationCanceledException`; the tree is dead when it is thrown.
   Disposing the session terminates its job (everything it started, background processes included) and deletes the log
   folder unless `KeepLogs`.

Commands run one at a time per session (`InvalidOperationException` otherwise). Every run starts on a clean screen:
the previous screen is moved into the scrollback and a header line `> <command>` is appended to the history, so the
Terminal window shows a continuous transcript and the model's view of a run starts with its command.

### Builds and tests

`CommandKind.Build` and `CommandKind.Test` set `MSBUILDTERMINALLOGGER=off` and `NO_COLOR=1` for the run and append
`-bl:"<log folder>\run-<n>.binlog"` or `--logger "trx;LogFileName=<log folder>\run-<n>.trx"` to the command; the paths
come back as `BinaryLogPath` and `TrxPath` (the files exist only if the tool honoured the switch). Work item 3.12 reads
diagnostics from them instead of scraping text; `dotnet build` with the Terminal Logger on still renders to a readable
view (the fixtures in the tests), it is just not what the model is given by default.

### Environment

The session's environment is the Developer PowerShell environment of `VisualStudioInstallationPath` when it loaded
(`DeveloperEnvironment.CaptureAsync` runs `Common7\Tools\VsDevCmd.bat -arch=<machine> -host_arch=<machine>` under
`cmd.exe` and parses `set`; `Launch-VsDevShell.ps1` wraps the same batch file), otherwise the host's own environment
with a warning in `Warnings`. The capture takes seconds, so `DeveloperEnvironmentCache` does it once per installation
per host and every session of the same Visual Studio shares the dictionary. `CommandSessionOptions.Environment` and
`CommandRunOptions.Environment` layer on top (a `null` value removes a variable). The block handed to `CreateProcess`
is sorted case-insensitively as Windows expects.

## The terminal

`VtParser` is a bounded state machine after the VT500 model: Ground, Escape, CSI (entry, parameter, intermediate,
ignore), OSC and the other string states (DCS, SOS, PM, APC, consumed to their terminator), and charset designations.
It applies cursor movement (CUU, CUD, CUF, CUB, CNL, CPL, CHA, VPA, CUP, HVP), erase in display and line (ED, EL, ECH),
insert and delete (ICH, DCH, IL, DL), the scroll region and scrolling (DECSTBM, SU, SD, IND, RI, NEL), SGR with the
16, 256 and 24-bit colours in both the `;` and the `:` forms (kept as `CellAttributes` on each cell, never in the text),
save and restore cursor (DECSC, DECRC, SCOSC, SCORC), CR, LF, VT, FF, BS, HT, the OSC title (0 and 2), the alternate
screen (47, 1047, 1049), cursor visibility (25), DECSTR and RIS. Bracketed paste (2004), the keypad and cursor-key
modes, focus and win32-input modes, hyperlinks (OSC 8), progress (OSC 9;4) and every unknown sequence are consumed
without touching the buffer; CAN and SUB abort a sequence; a sequence may span reads. No control byte or escape
sequence ever reaches the text of a line. Output is UTF-8 (a character may span reads too); each UTF-16 code unit
takes one cell, so East Asian wide characters may drift the column count by one, which affects alignment only.

`ScreenBuffer` is the grid (default 120 by 40, at most 1,000 by 500) with a cursor, a scroll region, deferred wrap at
the right margin, an alternate screen (blank, no scrollback) and a bounded scrollback (default 10,000 lines). Lines have
**history indices**: every line that scrolls off the top of the main screen gets the next index, the screen rows follow
(`FirstRowIndex` is the top row's), and `OldestLineIndex` is the oldest line still held; a consumer keeps the indices it
has seen and asks for the lines between. A full-screen erase on the main screen (`cls`, a logger starting over) moves
the erased rows into the scrollback instead of losing them, and ED 3 (clear scrollback) is a no-op: the scrollback is
Pillion's record of the session, not the application's. Resizing keeps the content anchored at the top left and moves
the rows that no longer fit above the cursor into the scrollback.

`TerminalBuffer` wraps both behind a lock, decodes the bytes, counts a `Generation` per change and raises `Changed`
(on the pump thread; handlers must be quick). `Snapshot()` is the Terminal window's frame; `GetLines(from, to)` its
scrollback delta. Mapping to contract 1.4's `TerminalScreen`: `Lines` = `Snapshot().Rows`, `ScrollbackTotal` =
`FirstRowIndex`, `ScrollbackDelta` = `GetLines(lastSeenFirstRowIndex, FirstRowIndex)`, `Sequence` = `Generation`,
`IsWaitingForInput` from `PromptWaiting`, `Title` = `Title`. A `BackgroundProcessSnapshot` maps onto the contract's
`BackgroundProcessInfo` field by field (`State` to `Status`, `Failure` to `Message`).

Memory is bounded by the screen size and the scrollback: a run printing 100,000 lines (10 MB through the console, in
the tests) leaves 540 lines in a session with a 500-line scrollback and grows the managed heap by well under 32 MB.

## The model's view

`ModelView.Build(lines, byteCap)` keeps whole lines from the end while they fit in the cap (default 16 KB, design
section 8's tool-result cap; the Agent Runtime can page the rest with `read_more` from the buffer), prefixes an
omission note (`[... N earlier lines omitted; the full output is in the Terminal window ...]`) when it had to drop any,
and runs the result through `LogRedactor` (one-way: `<redacted:kind>`, failing closed to `<redacted:scan-failed>` on a
scanner timeout). `Redacted` says whether anything changed. The buffer keeps the unredacted text for the Terminal
window, which stays on the developer's machine. `CommandResult.Output` is the run's range in the buffer, so
`get_terminal_output` can build another view of any part of it later.

## Background processes

`StartBackgroundProcessAsync` starts the command the same way (its own buffer with a 2,000-line scrollback, its own
nested job) and waits for readiness: the loopback port accepts a TCP connection, then `GET` on the health URL answers
2xx or 3xx (5 s per request, retried every 250 ms), whichever are given, within `ReadinessTimeout` (60 s). A process
that exits first is `Failed` with its exit code; one that is not ready in time is stopped and `Failed` with the
reason; otherwise it is `Ready` and stays for the session. `StopBackgroundProcessAsync` kills its tree;
`DisposeAsync` stops them all. The snapshots carry `Output`, the process's own buffer, for `get_terminal_output`.

## Tests

`tests/Pillion.CommandRunner.Tests` runs in CI on the real pseudo-console (the Windows runners have ConPTY, PowerShell 7,
Windows PowerShell, cmd and the .NET SDK; no model is involved, so nothing is `Model` or `Live`): exit codes and
output in all three shells, a three-level process tree killed on cancel and on timeout (verified by the children's
ids), prompt detection with `Read-Host` and the answer delivered, readiness against an `HttpListener` the test starts
(503 twice, then 200) and the stop, redaction of a planted key, the byte cap, the scrollback bound under 100,000 lines,
the environment and the build and test loggers, real `dotnet build` runs with the Terminal Logger on and off and with a
compile error, the Developer PowerShell environment when a Visual Studio installation is present (skipped with a
reason otherwise), and the parser and buffer units. `Fixtures\*.vt` are raw pseudo-console recordings (`dotnet build`
with the Terminal Logger on, off and with an error, a `Write-Progress` bar, `git log --color`) replayed through the
parser; `FixtureTests.RecordFixtures` rewrites them when `PILLION_RECORD_TERMINAL_FIXTURES` names the folder.
