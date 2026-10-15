# Gate 2 report

Phase 2 closes at Gate 2 ([phase-2-plan.md](phase-2-plan.md)): "recall at 10 of 0.85 on fixture repos; 1,000,000 lines
indexed in 30 minutes on CPU" (design document section 17), plus the other section 13 budgets that phase 2 makes
measurable. This report collects the evidence for each criterion, says how it was measured and lists what only the product
owner can decide. Work item 2.17.

PLACEHOLDER-SUMMARY

## Part A: the suite made deterministic

Nothing in this report rests on a red test. Before the gate runs, the order- and load-sensitive tests of the integration
branch were found and fixed at the root, and the whole IDE suite was run on both Visual Studio versions for the first time
since the phase 2 items landed.

### Unit and component tests

Five runs of the whole `Category!=Live&Category!=Benchmark&Category!=Model` suite (`dotnet test PillionProgrammer.NoVsix.slnf`,
the test projects in parallel) on `feature/phase2-wave1` at `8f07fdf` before any change, then five after:

| Runs | Passed per run | Failures |
| --- | --- | --- |
| Before | 4,472 to 4,474 of 4,474 | run 1: `AutocompleteDisplayTests.SingleLine_IsShownWhenItsLineIsComplete_WithoutAGracePeriod` and `ConversationRealHostTests.TheWindowListsOpensAndContinuesTheHostsConversations_...`; run 3: `UiThreadTimingsTests.On_RecordsEachMeasurement_WithItsNameAndDuration`; run 5: `ConnectivityNotificationTests.EachNewConnection_IsToldTheCurrentStateOnce`. In run 4 `ChunkerPropertyTests.Random_files_satisfy_the_chunk_invariants` took 4 min 8 s (passed) |
| After (2.16 merged in) | 4,513 of 4,513, all five runs | none |

PLACEHOLDER-PROJECT-RUNS

Causes and fixes. All are test changes unless marked; no timeout was widened without the figure being logged.

| Test | Cause | Fix |
| --- | --- | --- |
| `UiThreadTimingsTests`, `WorkspaceQueryServiceTests.CalledFromTheUiThread_NoPartOfTheQueryRunsThere` | The timings switch and list are process-wide. `WorkspaceQueryService` records `workspace.query.*` timings while the switch is on, and one of its tests switches it on and off, but the class ran in parallel with the collection 2.15 made for the timings | `WorkspaceQueryServiceTests` joins the `UI thread timings` collection. The classes that record or switch timings no longer overlap (`IndexWindowRealHostTests` is in the real-process collection, which runs alone) |
| `AutocompleteDisplayTests.SingleLine_IsShownWhenItsLineIsComplete_WithoutAGracePeriod` | A real-time `Task.Delay(100)` between releasing the first chunk and advancing the manual clock. Under load the engine read the clock after the advance: 180 ms instead of 120 | The fake model counts how often a stream waits for its next chunk. The test waits for the second wait (the engine has handled the first chunk) instead of sleeping |
| `IndexWindowTests.ABurstOfNotifications_IsAppliedAsOneBatch_AndTheLastStatusWins`, `IndexFormatTests.TheIndicator_FoldsABurstOfNotifications_...` | A fixed bound on the number of UI visits for 40 notifications. On a loaded machine raising the burst took longer than eight apply delays | The bound follows the time the burst took to raise (one visit per apply delay, plus two) and must stay under 40, so batching is still checked |
| `ChatRenderingTests.Transcript_200MessagesVirtualiseAndScrollSmoothly` | The wall-clock p95 of a WPF scroll step against a 60 Hz frame, with other test processes running | Three cached passes, best of the three per step, before the p95 check: a preempted step is slow in one pass only. Every figure is still printed |
| `SecretScannerPerformanceTests.PathologicalInput_DoesNotTimeOutOrBlowUp` and the other scanner smoke tests | Wall-clock bounds while the other test projects run in their own processes | Timed by the scanning thread's CPU time (`GetThreadTimes`). A run that hits the scanner's own wall-clock regex timeout earns one more attempt; a pattern that really backtracks still fails every attempt |
| `HandshakeCompatibilityTests.AContractOnePointOneClient_...`, `ConnectivityNotificationTests` and every in-process host test that did not set a data root | `InProcessHost` composed the host on the developer's real `%LOCALAPPDATA%\PillionProgrammer`: the real settings (with real providers listed and probed), the real conversation store and index folders, shared with every other test process and with the Visual Studio runs on the machine | A temporary data root by default; a test that sets its own still replaces it |
| `ConnectivityNotificationTests.EachNewConnection_IsToldTheCurrentStateOnce` | The hosted service starts the connectivity monitor after a yield, so the host can be Ready before the monitor has started. A probe asked for before that does nothing and the state stays Online | The test starts the monitor (idempotent) before it probes |
| `InlineChatSessionTests.Escape_WhileStreaming_CancelsOnTheHostAndCloses` (Gate 1 open issue 13: failed whenever it ran first in its process) | On Esc the session forgets the request (so it stops reading the stream), then asks the host to cancel, and only then cancels its token. The scripted service reported "cancelled" only for a cancelled token, so when the stream was disposed first, nothing was reported | The fake counts a stream the session stopped reading before its end as cancelled. The product's behaviour is unchanged and correct |
| `ConversationRealHostTests.TheWindowListsOpensAndContinuesTheHostsConversations_...` | Failed once in the five baseline runs (the new conversation not marked as saved after its reply). Not reproduced in the eight full and project runs since, alone or under load | Not found. The assertion now names the reply's state, error and the storage state, so a recurrence says why (open issue 2) |
| `ChunkerPropertyTests.Random_files_satisfy_the_chunk_invariants` | A random seed: one run in five spent 4 min 8 s in it. A hunt over 20,000 samples of its generator found an 8 KB file of random tokens that **the TSX chunker takes about six minutes** to chunk, at any cap (40 ms as `.ts`, under 1 ms as `.txt`) | A product defect, not fixed here (open issue 1). The input is kept as a skipped regression test (`ChunkerRegressionTests`, fixture `Fixtures/regressions/tsx-slow-parse.txt`) |

### Build and the IDE suites

`msbuild PillionProgrammer.sln /restore /p:Configuration=Release /m` with Visual Studio 2026's MSBuild: 0 warnings, 0 errors.

Before the first IDE run the Experimental Instances were cleaned: Pillion uninstalled from both hives with
`VSIXInstaller /uninstall`, the extension folders and the `ComponentModelCache` folders deleted. Each hive held one copy
(no duplicate this time). Every run then deployed the build afresh.

The first run on Visual Studio 2026 18.11 (`tools/vs-tests/Invoke-IdeTests.ps1`, no gate scenarios) passed 14 of 20:

- **The Chat window did not open at all.** `OpenChatCommand_ShowsThePillionChatWindow` failed, and so did every test that
  opens the window (the mention, citation and mentions v2 tests). The Activity Log reads "Construction of frame content
  failed ... Set connectionId threw an exception ... Unable to cast object of type 'System.Windows.Controls.MenuItem' to
  type 'System.Windows.Controls.TextBox'". The conversation picker's Rename and Delete menu items (work item 2.2) carried
  `Click` attributes inside a `Setter.Value` of the item container style. The WPF markup compiler gave those handlers the
  connection id of `RenameBox`, so building the window threw. **VSIX fix:** the menu items carry a `Tag` and get their
  handlers in code when the menu opens, through an `EventSetter` for `ContextMenuOpening`, which styles support. No unit test
  loads the XAML, and the IDE tests written by 2.2, 2.12 and 2.13 had never run.
- **`IndexWindow_ShowsTheRootRow_AndPauseAndResumeGoThroughTheHost`** timed out waiting for the paused header. Its `Click`
  helper raised `ButtonBase.ClickEvent`, which runs Click handlers but not the button's command: WPF executes the command
  in `OnClick`. **Test fix:** the helper raises Click and then executes the command.

After the fixes, 18.11 passed **19 of 19** and 17.14 passed **15 of 15** (the mention, Index window, citation and mentions
v2 tests are built for 18.x only). That includes the IDE tests written but never run by 2.2
(`ASavedConversation_SurvivesKillingTheHost`), 2.12 (`CitedAnswer_ShowsTheSpanLink_AndTheSourcesLine_AndOpensTheSpanSelected`),
2.13 (`MentionsV2_OutputBuild_PinsTheBuildPanesText`, `MentionsV2_Build_PinsTheLastBuildsOutcomeAndOutput`), 2.14
(`WorkspaceQueries_FindReferencesAndCallersOf_AnswerFromRoslyn_OffTheUiThread_OneInFlight`) and 2.15
(`IndexWindow_ShowsTheRootRow_AndPauseAndResumeGoThroughTheHost`). The gate runs below repeat the suite with the scenarios.

PLACEHOLDER-BODY
