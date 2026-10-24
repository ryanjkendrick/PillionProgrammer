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
| `PackageLoad_UsesLittleUiThreadCpu` | Gate 1 row "VSIX cost": switches the product's `UiThreadTimings` on in-process before it opens the first folder, then reports from the folder open to the host's Ready: Pillion's own time on the UI thread (the sum of its timed stretches, an upper bound of CPU time), split by stretch, with the number of visits to the UI thread; the time `InitializeAsync` spends before its first switch away from the caller's thread (and on which thread); the stretches timed off the UI thread (the background part of initialization); what Pillion deferred until its start-up was over (the Output pane, the Error List connection) in the three seconds after Ready; and, beside them, the whole UI thread's CPU time sampled every 10 ms from a background thread, from the Activity Log's "Begin package load" to Ready, against an idle baseline of the same length (that figure holds Visual Studio's own work too). It asserts Pillion's own UI-thread time under 50 ms (the design budget), `InitializeAsync`'s part before its first switch under 10 ms, and prints (without asserting) the whole UI thread's CPU, an upper bound that holds Visual Studio's own work; the numbers are printed first, as a `PackageLoadTimings:` JSON line too. The extension-on against extension-off launches of `tools/gate1` are in the Gate 1 report |
| `HostConnection_ReachesReady_Within30Seconds` | The `IHostConnection` service reaches `Ready` within 30 s with one `Pillion.Host.exe` child |
| `KillingTheHost_KeepsTheUIResponsive_AndReconnectsWithin15Seconds` | After `Pillion.Host.exe` is killed, a low-priority dispatcher operation posted every 250 ms for 15 s always runs, and the connection is `Ready` again within 15 s with a new host process. The UI gap is judged against a control (see below) |
| `OpenChatCommand_ShowsThePillionChatWindow` | `Tools.PillionOpenChat` shows the "Pillion Chat" tool window |
| `ASavedConversation_SurvivesKillingTheHost` | Work item 2.2: a conversation (title and one pin) made through the shipped host's `IConversationService`, with the Local Store in the user's data folder, is read back by the new host after `Pillion.Host.exe` is killed and restarted, and is listed. The test deletes the one conversation it made (never Delete all). Needs `features.localStore` on, its default. It counts as one host kill towards the supervisor's limit |
| `IdeSignals_DocumentEvents_FireForOpenActivateSaveAndClose` | Work item 1.11: opening, activating, saving and closing a document in a scratch solution raises `IIdeSignals.DocumentOpened`, `ActiveDocumentChanged`, `DocumentSaved` and `DocumentClosed` with the file's path |
| `IdeSignals_BuildWithACompileError_RaisesBuildEvents_AndAnswersDiagnosticAndOutputQueries` | `DTE.Solution.SolutionBuild.Build(true)` on a scratch project with one compile error raises `BuildStarted`, `BuildStateChanged` and `BuildFinished(success=false)`; `GetDiagnosticsAsync` returns CS0029 for the solution and the active document, `DiagnosticsChanged` fires, and `GetOutputPaneTextAsync("Build", 4000)` contains the error |
| `WorkspaceQueries_FindReferencesAndCallersOf_AnswerFromRoslyn_OffTheUiThread_OneInFlight` | Work item 2.14: in a scratch C# project with `Shop.OrderService.Place` and three calls of it, the extension's workspace query service (`PillionPackage.WorkspaceQueries`, called from the thread pool as the host's RPC calls arrive) answers ten find-references and ten callers-of queries from the live Roslyn workspace with the three sites, their snippets and calling members (not approximate, not truncated, each within 2.5 s), and prints each latency with the p50 and p95 (`WorkspaceQueryLatency:` lines); no query's `UiThreadTimings` stretch ran on the UI thread, the service's own UI-thread check counted no visit, and no product dispatcher operation of the queries ran; a second callers-of while one is in flight fails at once (under 100 ms) with `Cancelled` |
| `AutocompleteAndInlineEditTests.ProposalSource_ShowsGhostText_WithTheCompetitorEnabled` | Work items 1.1 and 1.4: with a canned completion service in `AutocompleteOverrides.ModelService` (no model) and the instance's competitor left on (Copilot on 18.x, IntelliCode on 17.x), typing through the view's command chain shows Pillion's proposal; Tab (`Edit.AcceptSuggestion` on 18.x) inserts it as one edit, Esc dismisses it and leaves the buffer unchanged, and Pillion: Suggest shows one again |
| `AutocompleteAndInlineEditTests.ProposalSource_NoProposal_WhileACompletionListItemIsSelected` | Typing opens Roslyn's completion list with an item selected; for two seconds (checked every 50 ms) no Pillion proposal is displayed |
| `AutocompleteAndInlineEditTests.IntelliCodeCheck_ReportsWhenAutomaticCompletionsAreOff` | 17.x only: switching IntelliCode's automatic completions off through the settings manager makes the status bar and the Pillion Output pane report it when the Tools menu's commands are queried, and the status bar clears when it is switched back |
| `InlineDifferenceView_EnterAppliesOneEditWithOneUndoStep` | Inline chat on a selected method with a canned inline edit service (no model): the proposal is shown in the inline difference view with the buffer untouched; Enter applies it as one undo unit named "Pillion inline edit", and one Edit.Undo restores the text |
| `InlineDifferenceView_EscDiscards_LeavingNothingToUndo` | The same, then Esc: the adornment closes, the buffer's version and the undo stack are unchanged |
| `Mentions_TypeAtPickAFileAndSend_TheHostReceivesOnePin` | Work item 1.9, Visual Studio 2026 only: with `tests/Pillion.FakeHost` put behind the package for this test, typing `@` in the Chat window's input lists the kinds, typing `@Prog` lists `Program.cs` of the open scratch folder, Down and Enter pin it (the typed mention leaves the input), Enter sends, the reply completes and the Outbound log shows the request, and the fake host's record file holds one chat request with one `file` pin named `Program.cs`. The shipped host is restored afterwards |
| `IndexWindow_ShowsTheRootRow_AndPauseAndResumeGoThroughTheHost` | Work item 2.15, Visual Studio 2026 only: with `tests/Pillion.FakeHost` put behind the package and `PILLION_FAKEHOST_INDEX=ready`, `Tools.PillionOpenIndex` shows the "Pillion Index" window; its one root row is `C:ake` (writer, 40 of 40 files, 320 chunks, `bge-small-en-v1.5`), every visible button has an automation name, clicking Pause makes the header read "Paused: you paused it" (the fake host answers the call and raises `indexStatusChanged`) and enables Resume, clicking Resume returns to "Idle (up to date)", and the Excluded files tab lists the six canned entries with the two secret-bearing files first. Written and built, not run in CI by the work item that added it. The shipped host is restored afterwards |

### The Gate 1 scenarios

`PillionPackageTests.Gate1.cs` holds the Gate 1 performance scenarios (work item 1.18). Unlike every other test here they
need a local model and the real Pillion Host, so they are `[GateScenarioFact]`s: skipped unless `PILLION_GATE_SCENARIOS=1`
is set when the tests are discovered, as `tools/gate1/Invoke-Gate1.ps1` does (it also writes the Ollama provider into
`settings.json` first and restores the file afterwards). They run after the other tests of the class (test order 30 to
35) in a scratch C# project, and write their figures as JSON to `%PILLION_GATE_OUT%`:

| Test | What it measures |
| --- | --- |
| `Gate1_Autocomplete_StrongModel_TypingScenario`, `Gate1_Autocomplete_FastModel_TypingScenario` | Autocomplete routed to one model (settings.json, Local-fast): a warm-up suggestion, then a fixed 40-line C# file typed through the view's command chain at 80 ms a character with a 1.2 s pause after each line break; a Pillion proposal that equals the next lines is accepted. The extension's figures after a reset (`IAutocompleteMetrics`: time to suggestion, show and acceptance counts, degradation; the Show Status text), the host's counters over the pass (cache hit rate, prompt tokens counted against estimated), the Outbound records, and each TypeChar's time on the UI thread |
| `Gate1_Keystrokes_AutocompleteOff_TypingScenario` | The same typing with `features.autocomplete` off, for the keystroke-latency difference |
| `Gate1_InlineChat_PreviewFirstLine` | Five inline chat runs each of "extract the validation into a method" and `/doc` on the real host: first preview text, first complete line and the whole proposal, then Esc (the buffer must be untouched) |
| `Gate1_Chat_FirstToken_Mentions_AndOutboundLog` | Five chat messages to the local model (first visible reply text); `@file`, `@errors` (after a build with one compile error) and `@selection` pinned from the picker, the Context Inspector opened, the message sent; the host's Outbound records for chat, autocomplete and inline edits |
| `Gate1_Cloud_FastAutocompleteAndChatFirstToken` | Only with a cloud provider whose key is stored (`Invoke-Gate1.ps1 -CloudProviderId`): the typing pass on the Fast profile and the chat first token on the cloud model; otherwise it records the rows as pending |

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
- whose code held the UI thread (`UiThreadMonitor`): every dispatcher operation of the window is timed through
  `Dispatcher.Hooks` (with the async method or delegate behind it), grouped by what ran; `Pillion's own timed UI thread
  work:` lists the stretches the product's `UiThreadTimings` debug switch timed (status bar updates, Output pane flushes,
  command status queries, ...), which the test switches on for the window; for the longest gap, the operations and
  Pillion's stretches inside it; and the UI thread's managed stack, sampled every 100 ms once it has started or finished
  no dispatcher operation for 200 ms (`(no managed frames: native code only)` means it was in Visual Studio's native
  code). `Pillion's own UI thread time within the longest gap:` is the union of Pillion's operations, Pillion's timed
  stretches and the samples with a Pillion frame, or `unknown` when the monitor did not watch the whole gap.

`Invoke-IdeTests.ps1` prints the kill, state change, `UI thread`, `Background thread:`, `Pillion's own` and `WARNING:`
lines from the TRX file after every run (the console logger shows a passing test's output only in the TRX), and under
GitHub Actions turns a `WARNING:` line into a workflow warning annotation. The per-operation lines, operation groups and
stack samples are in the TRX (`ide-test-results-vs18` and `ide-test-results-vs2022` artifacts).

The verdict (`ResponsivenessVerdict`): the UI gap is a failure when it is over 5 s, when it exceeds the background gap
by 2 s or more, or when it reaches 2 s while the background gap is under 500 ms; such a gap passes with a `WARNING:`
line instead when the monitor watched it and Pillion's own code held the UI thread for 500 ms of it or less (another
component stalled it), and fails when Pillion's share was more or could not be measured. When the UI gap reaches 2 s
but the background thread stalled about as long, the machine stalled: it passes with a `WARNING:` line. Posted and
answered operations must still match, and Ready must return within 15 s.

On the GitHub-hosted runners the probe's `Background`-priority operations can wait seconds while the UI thread is idle:
Visual Studio's `MainWindow.OnTierChanged` runs at `Render` priority 130 to 190 times a second there (WPF's render tier
keeps changing on a runner without a GPU), and WPF does not run `Background` work while a posted message is waiting
(`Dispatcher.IsInputPending` counts `QS_POSTMESSAGE`). The operation groups of such a gap show hundreds of
`OnTierChanged` operations taking a few milliseconds in all, and Pillion's stretches under a millisecond.

### Tests that use the fake host

The package launches the executable named by the environment variable `PILLION_TEST_HOST_PATH` of devenv.exe instead of
its own `Host\Pillion.Host.exe` when the variable is set at launch time (`HostLauncherSelector`; the launch protocol and
the signature check are the same, so a signed release build only launches an executable signed with its certificate). A
test sets the variable inside devenv.exe, together with `PILLION_FAKEHOST_RECORD` (a file the fake host appends each chat
request's pins to), restarts the host with the supervisor's Retry, and clears the variables and restarts again at the end
(`MentionChecks`). The fake host comes from the full solution build (`tests/Pillion.FakeHost/bin/Release/net10.0`); the
Visual Studio 2022 CI job runs a downloaded test build without it, so these tests are compiled for Visual Studio 2026 only.

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
   checks that no Experimental Instance or `Pillion.Host.exe` from the run is left (it stops any and fails the run). A host is the run's own only when its parent process is an Experimental Instance the run started; another run's host, or one whose parent cannot be determined, is logged and left alone.

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
