# Command Runner

How Pillion runs commands for an agent session (design sections 8 and 19, F7; work item 3.11). This is the engine
behind the agent's `run_command`, `run_build`, `run_tests`, `start_background_process` and `stop_background_process`
tools (work item 3.12) and the Pillion Terminal window (work item 3.15); those work items add the approval prompts,
the deny-list and the window. The code is `src/Pillion.CommandRunner` (its README has the API and the internals).

## What runs where

- Every agent session gets one **command session**: one shell, one working directory (the solution root), one
  environment, one terminal buffer and one Windows Job Object. Commands run one at a time in that session.
- The shell is **PowerShell 7** (`pwsh.exe`) by default, or **Windows PowerShell** or **cmd**, each started with no
  profile and no logo on a script file holding the command, so the command runs exactly as written and its exit code
  comes back unchanged (`exit /b %ERRORLEVEL%`; `exit $LASTEXITCODE`, or 1 for a failed cmdlet). When PowerShell 7 is
  not installed, Windows PowerShell runs the command and the session carries a warning.
- Commands run on a real **pseudo-console** (ConPTY, the same console Windows Terminal uses), so programs behave as in a
  terminal: colours, progress bars, the Terminal Logger, `Read-Host` and pagers all work, and their output is captured
  faithfully rather than scraped from a pipe. The console is 120 by 40 cells unless the Terminal window resizes it.
- Each command's process tree lives in a **Job Object** nested in the session's, which is nested in the one Visual
  Studio put the host in. A **timeout** (10 minutes by default, per command or per session) or a **cancel** terminates
  the command's job, which kills every process of that tree at once, and waits up to 1 s for it; the session's end
  kills everything the session started; Visual Studio closing kills the host and with it every job. Nothing an agent
  started outlives the agent.
- The exact command line is part of the result (`CommandLine`), beside the command as the agent gave it (`Command`),
  so the ledger shows what ran.

## Environment

- Sessions load the **Developer PowerShell environment** of the Visual Studio that launched the host: the handshake
  (contract 1.4) names its installation path; the host runs that installation's `Common7\Tools\VsDevCmd.bat` once per
  installation (not per session or per command) and gives every command the variables it sets, so MSBuild, the
  compilers, the Windows SDK and the Visual Studio tools are on `PATH` as in a Developer PowerShell. A Visual Studio
  2022 extension (contract 1.3) sends no path; then the newest installation `vswhere.exe` reports is used. When no
  installation can be found, or the script fails, commands run with the host's plain environment and the session
  carries a warning that says so.
- Agent-run **builds and tests** get `MSBUILDTERMINALLOGGER=off` and `NO_COLOR=1`, a binary log (`-bl:<path>`) for
  builds and a TRX logger (`--logger "trx;LogFileName=<path>"`) for `dotnet test`. The paths are returned with the
  result, so diagnostics are read from structured logs rather than scraped text (the self-healing pipeline, work item
  3.16, reads the binary log and the TRX file). The log files live in a per-session folder under
  `%TEMP%\Pillion\command-runner\` that the session deletes when it ends.
- A session or a run can set or remove further variables; the agent's tools decide which (none in this work item).

## Limits

| Limit | Default | Where |
| --- | --- | --- |
| Command timeout | 10 minutes | per session, per run |
| Kill wait after a timeout or cancel | 1 s | per session |
| Terminal size | 120 by 40 | per session; the Terminal window can resize |
| Scrollback kept | 10,000 lines (2,000 per background process) | per session |
| The model's view of a run | 16 KB, the tail | per session, per run |
| Prompt detection | 5 s idle with no trailing newline | per session |
| Background readiness | 60 s: the port accepts, then the health URL answers 2xx or 3xx | per process |

The screen buffer and the scrollback are the only memory a command can fill: a command printing 100 MB leaves at
most the screen plus the scrollback in the host (the tests push 10 MB through a 500-line scrollback and measure the
heap). The host's memory ceilings of design section 13 are unaffected by what a command prints.

## How output reaches the model

1. The pseudo-console's output (text with VT escape sequences) goes through Pillion's VT parser into a screen buffer
   with scrollback, so what the model sees is what a person would see on the screen: a progress bar that redrew itself
   fifty times is one line, the Terminal Logger's live region is its final state, colours are attributes rather than
   escape codes in the text, and an unknown sequence is dropped, never leaked into the text.
2. When the command ends, the model receives the run's lines, **tail-truncated** to the byte cap (the end of a build
   or test run is where the errors and the summary are), with a first line saying how many earlier lines were left out
   when any were, and passed through the **secret scanner** (`<redacted:kind>` placeholders; a scanner timeout fails
   closed). The full, unredacted buffer stays in the host for the Terminal window, which never leaves the machine.
   `get_terminal_output` (work item 3.12) builds further views from any part of the buffer, the earlier lines included.
3. A command that seems to **wait for input** (the screen has not changed for 5 s and the cursor sits after text with
   no newline, as after `Name:` or `Password:`) is reported to the session; the agent never answers it. The Terminal
   window's input line does (work item 3.15), and the keystrokes go to the running command's console.
4. **Background processes** (an app server started for the length of the session) are started the same way, each
   with its own buffer, and count as ready when their port accepts a connection and their health URL answers; one
   that exits first, or is not ready within the readiness timeout, is reported as failed with its output. They are
   listed with their state and stopped on request or when the session ends.

## The flag

`features.commandRunner` (on by default; a repository policy can switch it off) gates the whole library: with it off
the host refuses to create a session with `PolicyDenied` and a message that names the flag, so no shell is started,
no pseudo-console is created and no developer environment is loaded. The tools and the window of the later work items
sit behind their own flags (`agent.disabledTools`, `features.terminalWindow`) on top of this one.
