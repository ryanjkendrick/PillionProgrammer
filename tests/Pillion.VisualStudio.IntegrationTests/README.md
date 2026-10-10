# Visual Studio integration tests

Tests that run inside a real Visual Studio: the Experimental Instance of Visual Studio 2026 (18.x) or Visual Studio
2022 (17.14 or later), with the Pillion VSIX installed. They check what unit tests cannot: that the package loads
when a folder opens, that Pillion Host starts and recovers, that the UI thread stays responsive when the host dies,
and that the commands and tool windows work. Design document section 15 (Test levels) and the phase 1 plan (work
item 1.16, Gate 1 rows "CI" and "ghost text works").

## How it works

The tests use the integration testing framework that Roslyn and the VS SDK samples use
(`Microsoft.VisualStudio.Extensibility.Testing.Xunit`, with `[IdeFact]` and `[IdeTheory]`). The framework starts
`devenv.exe /rootsuffix Exp` of the version a test targets, installs its own small test service into that instance,
loads the test assembly into `devenv.exe` and runs each test on Visual Studio's UI thread. All tests of a run share
one instance, in `[TestOrder]` order, and the framework closes it at the end.

No single version of the framework drives both Visual Studio versions, so the same sources build twice:

| Project | Visual Studio | Framework version | Why |
| --- | --- | --- | --- |
| `tests/Pillion.VisualStudio.IntegrationTests` | 2026 (18.x) | 5.3.0-2.26228.15 (built by dotnet/roslyn) | Installs its test service with the stock `VSIXInstaller.exe`; that service targets 18.x only |
| `tests/Pillion.VisualStudio.IntegrationTests.VS2022` | 2022 (17.14+) | 0.1.800-beta (last microsoft/vs-extension-testing release) | Its own installer works on 17.x; on 18.10 it fails to load `Microsoft.VisualStudio.ExtensionManager.Implementation` 17.0 |

Both are pinned in `Directory.Packages.props`, which explains the choice. The framework is not on nuget.org; it comes
from Microsoft's public `vssdk` Azure Artifacts feed, mapped in `nuget.config` to `Microsoft.VisualStudio.Extensibility.Testing.*`
only. `IdeTarget.Version` (`VS18`, or `VS2022` when `IDE_VS2022` is defined) sets the version for every test class
through `[IdeSettings]`.

The tests install nothing of Pillion's: they expect the VSIX to be deployed to that Experimental Instance already,
and they need no model (Pillion Host reaches Ready with no provider, as on the CI runners, which have no Ollama).
They open scratch folders under `%TEMP%\pillion-ide-tests`. Pillion.VisualStudio.Core and Pillion.Contracts are
compile-time references only: inside `devenv.exe` the tests use the copies the installed extension loaded, so the
`IHostConnection` the package registers as a service casts to the test's `IHostConnection`.

`dotnet test PillionProgrammer.NoVsix.slnf` builds both projects but does not run them (they are not test projects
unless `RunIdeTests` is `true`), so the unit-test run stays fast and never starts Visual Studio.

## What is tested

| Test | Checks |
| --- | --- |
| `VisualStudio_IsAtLeast17_14` | The instance under test is 17.14 or later |
| `Package_LoadsOnFolderOpen_WithNoActivityLogErrors` | The package is not loaded before a folder opens, loads by itself after `File > Open > Folder`, and the in-memory Activity Log has no error about Pillion |
| `HostConnection_ReachesReady_Within30Seconds` | The `IHostConnection` service reaches `Ready` within 30 s with one `Pillion.Host.exe` child |
| `KillingTheHost_KeepsTheUIResponsive_AndReconnectsWithin15Seconds` | After `Pillion.Host.exe` is killed, a low-priority dispatcher operation posted every 250 ms for 15 s always runs, and the connection is `Ready` again within 15 s with a new host process. The UI gap is judged against a control (see below) |
| `OpenChatCommand_ShowsThePillionChatWindow` | `Tools.PillionOpenChat` shows the "Pillion Chat" tool window |
| `IdeSignals_DocumentEvents_FireForOpenActivateSaveAndClose` | Work item 1.11: opening, activating, saving and closing a document in a scratch solution raises `IIdeSignals.DocumentOpened`, `ActiveDocumentChanged`, `DocumentSaved` and `DocumentClosed` with the file's path |
| `IdeSignals_BuildWithACompileError_RaisesBuildEvents_AndAnswersDiagnosticAndOutputQueries` | `DTE.Solution.SolutionBuild.Build(true)` on a scratch project with one compile error raises `BuildStarted`, `BuildStateChanged` and `BuildFinished(success=false)`; `GetDiagnosticsAsync` returns CS0029 for the solution and the active document, `DiagnosticsChanged` fires, and `GetOutputPaneTextAsync("Build", 4000)` contains the error |
| `AutocompleteAndInlineEditTests.ProposalSource_*`, `IntelliCodeCheck_*` | Skipped placeholders for work item 1.1 (proposal source with IntelliCode or Copilot on, completion-list suppression, the IntelliCode check on 17.x) |
| `InlineDifferenceView_EnterAppliesOneEditWithOneUndoStep` | Inline chat on a selected method with a canned inline edit service (no model): the proposal is shown in the inline difference view with the buffer untouched; Enter applies it as one undo unit named "Pillion inline edit", and one Edit.Undo restores the text |
| `InlineDifferenceView_EscDiscards_LeavingNothingToUndo` | The same, then Esc: the adornment closes, the buffer's version and the undo stack are unchanged |

### Reading the host kill test's output

The design rule is that the UI thread never waits on the host (design document section 13), with a budget of 2 s. A
shared CI runner can stall a whole process for longer than that, so the test runs a control beside the dispatcher probe,
on one clock, for the same 15 s: `BackgroundHeartbeat`, a dedicated thread that sleeps 250 ms and records when it wakes.
The test output shows:

- `UI thread:` the dispatcher probe: operations answered, the longest gap between two answers and when it happened, the
  slowest answer, and the longest time between two posts (a large value means the posting thread itself was starved).
- `Background thread:` the heartbeat's longest gap and when it happened.
- `UI thread CPU time over the ... window:` the CPU time the UI thread used. A long gap with little CPU time means
  the UI thread was blocked or starved, not busy.
- every posted operation with its post and run times, and `<-- slow` on those that waited 500 ms or more, so a failure
  shows where in the window (before or after the kill, the restart and Ready) the gap fell. The kill and each state
  change are listed above with their times.

`Invoke-IdeTests.ps1` prints the kill, state change, `UI thread:`, `Background thread:` and `WARNING:` lines from the TRX
file after every run (the console logger shows a passing test's output only in the TRX), and under GitHub Actions turns
a `WARNING:` line into a workflow warning annotation. The per-operation lines are in the TRX
(`ide-test-results-vs18` artifact).

The verdict (`ResponsivenessVerdict`): the test fails when the UI gap is over 5 s, when the UI gap exceeds the background
gap by 2 s or more, or when the UI gap reaches 2 s while the background gap is under 500 ms. When the UI gap reaches
2 s but the background thread stalled about as long, the machine stalled and the test cannot attribute that to Pillion's
code: it passes with a `WARNING:` line in the output. Posted and answered operations must still match, and Ready must
return within 15 s.

## Running locally

Prerequisites: Visual Studio 2026 and, for the 17.14 run, Visual Studio 2022 17.14 or later; PowerShell 7.2 or
later. Close every Experimental Instance of the version under test first (the scripts refuse to run otherwise).
Other Visual Studio instances, including Experimental Instances with another root suffix, are left alone.

1. Build the full solution (README, "Building and testing locally"):
   `msbuild PillionProgrammer.sln /restore /p:Configuration=Release /m`.
2. Deploy the VSIX to the Experimental Instance of the version under test:

   ```
   pwsh tools/vs-tests/Install-ToExp.ps1 -DevEnvPath "C:\Program Files\Microsoft Visual Studio\18\Community\Common7\IDE\devenv.exe"
   pwsh tools/vs-tests/Install-ToExp.ps1 -DevEnvPath "C:\Program Files\Microsoft Visual Studio\2022\Community\Common7\IDE\devenv.exe"
   ```

   It uses that version's `VSIXInstaller.exe /rootSuffix:Exp`, then `devenv /rootsuffix Exp /updateconfiguration`.
   `-Uninstall` removes the extension again.
3. Run the tests:

   ```
   pwsh tools/vs-tests/Invoke-IdeTests.ps1 -DevEnvPath "C:\Program Files\Microsoft Visual Studio\18\Community\Common7\IDE\devenv.exe"
   pwsh tools/vs-tests/Invoke-IdeTests.ps1 -DevEnvPath "C:\Program Files\Microsoft Visual Studio\2022\Community\Common7\IDE\devenv.exe"
   ```

   The script picks the matching build, runs it with that version's `vstest.console.exe` (filtered to the version's
   test cases), and writes `TestResults/ide-VS18` or `TestResults/ide-VS2022`: the TRX file, and under `logs/` the
   Activity Log saved after every test (`ActivityLog-VS18.xml`), the framework's installer log and failure
   screenshots, and the Pillion logs written during the run. `-TestCaseFilter "FullyQualifiedName~KillingTheHost"`
   runs a subset. It backs up `%LOCALAPPDATA%\PillionProgrammer\settings.json` and restores it byte for byte, and
   checks that no Experimental Instance or `Pillion.Host.exe` from the run is left (it stops any and fails the run).

A run takes one to two minutes per version; most of it is the framework preparing the instance (`/clearcache`,
`/updateconfiguration`, `/resetsettings`, which resets the Experimental Instance's settings to General).

Use the script rather than `dotnet test -p:RunIdeTests=true` or Test Explorer: only the script backs up
`settings.json`, pins the installation and checks for leftover processes.

## How CI runs them

`.github/workflows/ci.yml`:

- **Build and test** (`windows-latest`, Visual Studio 2026): after the build and the unit tests, installs the built
  VSIX into the runner's 18.x Experimental Instance with `Install-ToExp.ps1` and runs `Invoke-IdeTests.ps1`. It
  uploads the VS2022 test build (`ide-tests-vs2022`) and the results (`ide-test-results-vs18`: TRX, Activity Logs,
  Pillion logs, screenshots).
- **Integration tests (Visual Studio 2022)** (`windows-2022`): checks with `vswhere` that the runner has Visual Studio
  17.14 or later with IntelliCode and skips with a notice if not; downloads the VSIX and the VS2022 test build from
  the first job (this runner cannot build the solution: the .NET 10 SDK needs MSBuild 18), installs and runs them the
  same way, and uploads `ide-test-results-vs2022`.

## Adding a test

1. Add an `[IdeFact]` (or `[IdeTheory]`) method to a class in this folder that derives from `PillionIdeTest` and
   carries `[IdeSettings(MinVersion = IdeTarget.Version, MaxVersion = IdeTarget.Version)]`. Both builds pick it up.
   To limit a test to one version, set `MinVersion` or `MaxVersion` on its `[IdeFact]`.
2. The test runs on the UI thread. Use `JoinableTaskFactory.SwitchToMainThreadAsync` after awaiting work elsewhere,
   `TestServices.Shell`, `TestServices.SolutionExplorer` and `TestServices.Editor` (generated by the framework's
   source generator) for common IDE actions, and `OpenScratchFolderAsync`, `EnsurePackageLoadedAsync`,
   `GetActivityLogAsync` and `WaitUntilAsync` from `PillionIdeTest`.
3. Code that uses Pillion.VisualStudio.Core or Pillion.Contracts types must run after `EnsurePackageLoadedAsync` and
   live outside the test class (see `PillionHostChecks`): test discovery runs outside Visual Studio, and inside it
   those assemblies are only available once the package has loaded them.
4. Tests share one instance and one Pillion Host per run. Leave the instance as you found it (close tool windows you
   opened; the base class closes the folder), and use `[TestOrder]` in `PillionPackageTests` if order matters. A test
   that kills the host counts towards the supervisor's three-crashes-in-five-minutes limit.
5. Nothing may need a model, the network or a key: CI has none.
