# Gate 2 report

Phase 2 closes at Gate 2 ([phase-2-plan.md](phase-2-plan.md)): "recall at 10 of 0.85 on fixture repos; 1,000,000 lines
indexed in 30 minutes on CPU" (design document section 17), plus the other section 13 budgets that phase 2 makes
measurable. This report collects the evidence for each criterion, says how it was measured and lists what only the product
owner can decide. Work item 2.17.

**Summary.** Of the 19 criteria, on Visual Studio 2026 18.11, **14 pass** (CI only on `develop`, where phase 2 has not landed yet),
**4 fail** and **1 is pending the product owner** (the offline run). Three of the failures are decisions the figures force and
that only the product owner can take: indexing throughput on **real code** misses on the CPU tier (3.86 minutes per 100,000
lines against 3; 35.3 minutes per 1,000,000 against 30; generated code passes both at 1.5 and 15.7 minutes) and the index is
2.2 KB a chunk on real code against about 1.5 (issue #90). The fourth is the keystroke p95 with autocomplete on (+4.2 ms against
+2 on 18.11; +0.96 ms on 17.14, which passes). Every latency budget of the phase holds in the IDE on both versions: search
p50 82 / 78 ms on a 121,001-chunk index, context assembly with retrieval p50 53 / 70 ms, a saved file searchable within 272 ms,
the realistic 500-file branch switch idle in 26.5 / 26.3 s, workspace queries p50 14 / 15 ms, keystrokes unchanged while
indexing. Persistence, encryption at rest, secret-file exclusion, two instances on one index and cited answers pass. DirectML,
as shipped, is not faster than the CPU on real code (issue #78). Part A made the suite deterministic (5 of 5 green full runs)
and fixed two VSIX defects found on the way: the Chat window did not open, and the Index window's status bar item cost up to
83 ms of UI-thread time at start-up on CI. New open issues: the TSX chunker can take minutes on a file, and retrieval reads
indexes outside the open workspace.

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

Three runs of the Visual Studio test project alone after the first fixes found one more: `InlineChatSessionTests.Escape_WhileStreaming_CancelsOnTheHostAndCloses`
(below), which then failed every time it ran alone. Each gate run repeats the suite (step b): 4,513 passed, 0 failed, 1
skipped (the TSX regression test) in the 18.11 run. The CI runs of the wave branch and of this pull request showed more failures that only a loaded runner produces, also fixed
here (the rows marked CI in the table).

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
| `HybridRetrieverTests.Fusion_IsReciprocalRankFusion_WithTheRanksOfEachList` (CI only) | The tests' "generous" 10 s budget gives the candidate legs a share of it; the first search of a cold CI process missed about 5 s and came back degraded | 60 s for the tests that are not about the budget |
| `WorkspaceQueryServiceTests.CalledFromTheUiThread_NoPartOfTheQueryRunsThere` (CI only) | **Product:** when the search finished before the service awaited it, `await` of the completed task carried on synchronously, and the response was built on the caller's thread (the UI thread, for a caller there) | **VSIX fix:** the service leaves the caller's thread (`await TaskScheduler.Default`) before it waits; only the admission check runs on the caller's thread, as its documentation says |
| `ConversationRealHostTests.TheWindowListsOpens...` (CI) | On CI the reply list was empty after `SendAsync`: the window could not send yet (the model list not read), so the send was a no-op | The test waits for `SendCommand.CanExecute` before it sends. This is the cause of the baseline failure too |
| `RealProcessTests.ChatAndOptions_WorkOverTheRealWireAgainstTheFakeHost` (CI) | The page's "Saved." status is set on the UI thread just after the save returns | The status recorder waits for "Saved." |
| `BundledEmbeddingModelTests.DirectML_AgreesWithTheCpu_OrFallsBackToTheCpu`, `OnRequestModelLiveTests.WithTheOptionalFp16Graph_...` (CI) | DirectML was asserted wherever the DirectML build runs; a runner without a hardware adapter falls back to the CPU, as the test name says | DirectML is asserted (and the FP16 test runs) only with a hardware adapter (`DxgiProbe`) |
| `IndexingWatcherModelTests.SaveToSearchable_...` and the other `Category=Model` tests (CI) | CI ran the Model tests (its filter excluded only `Benchmark`): the bundled ONNX model on a runner without a GPU, minutes per test | CI excludes `Category=Model` as well; the gate runs them (step c), as the plan's decision log intends for the benchmarks |
| `PillionPackageTests.PackageLoad_UsesLittleUiThreadCpu` (CI, both versions) | **Product:** the Index window's status bar item (2.15) searched the whole main window's visual tree for the status bar panel at start-up: 52 to 83 ms of Pillion's UI-thread time on the runners (1.1 ms here), against the 50 ms budget | **VSIX fix:** the search visits children last first; the status bar is the last child at each level on the way down |
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

## How the evidence is produced

| Script | What it does | Who runs it |
| --- | --- | --- |
| `tools/gate2/Invoke-Gate2.ps1` | a. builds the solution (Release, Visual Studio 2026 MSBuild, no deployment) and records the VSIX, the host, the bundled model files by SHA-256 and the Ollama models by digest; b. the unit and component tests (`Category!=Live&Category!=Benchmark&Category!=Model`) with TRX files; c. the Benchmark and Model tests one project at a time after unloading every Ollama model, with the figures they print; d. the generated-repository indexing runs (`Gate2IndexingTests`): 100,000 and 1,000,000 lines, synthetic C# and copies of this repository's own `src`, on the CPU and, while issue #78 is open, with DirectML; the synthetic 1,000,000-line index is built in the real data folder so the IDE scenarios find a 100,000-chunk index ready; e. backs up `settings.json`, routes Local-strong and Local-fast to Ollama as Gate 1 did with every phase 1 and phase 2 flag on, creates the generated solution, and runs the whole IDE suite with `PILLION_GATE_SCENARIOS=1` and `PILLION_GATE2_SCENARIOS=1` (the Gate 1 scenarios, now with retrieval on, then the Gate 2 ones); e1. a second Visual Studio session for the restart check; e2. encryption at rest; e3. two Visual Studio instances on one index in the private hive `PillionG2`; f. the retrieval row quoted from `docs/eval-retrieval-baseline.md`; g. CI on `develop`; h. the flag list; i. restores `settings.json` byte for byte, deletes the gate's indexes (and the indexes the IDE tests' deleted scratch folders left), uninstalls from `PillionG2` and checks for processes. `-SkipBuild`, `-SkipTests`, `-SkipBenchmarks`, `-SkipIndexing`, `-SkipIde`, `-SkipTwoInstances`, `-KeepIndexes` (for a second Visual Studio version with `-SkipIndexing`), `-DevEnvPath`, `-DirectML auto/yes/no`, `-TestCaseFilter`, `-Quick` | Anyone with Ollama, the Gate 1 models, git and Python; the agent ran it |
| `tools/gate2/Gate2.Scenarios.psm1` | The generated solution (600 C# files of about 170 lines, a classic project, `.env` and `appsettings.Development.json` with fake secrets, a git repository with a `realistic` branch that changes two methods in each of 500 files and a `rewrite` branch that rewrites the same 500 files), the index folder helpers, the plain-SQLite, ACL and plain-text checks, the two-instance check (reads `writer.lock` from disk) and the criteria table | |
| `tests/Pillion.Host.Tests/Indexing/Gate2IndexingTests.cs` | One generated-repository run: the real `IndexingService` composed in-process (as the other indexing tests do) with the real gateway and the bundled `bge-small-en-v1.5` int8, the real working set governing the memory cap; writes time, throughput, first searchable, chunks searchable at one minute, peak working set, bytes per chunk, memory pauses and the execution provider loaded. Skipped unless `PILLION_GATE2_INDEX=1` | |
| `tests/Pillion.VisualStudio.IntegrationTests/PillionPackageTests.Gate2.cs` | The Gate 2 IDE scenarios, against the real host from inside devenv.exe; skipped unless `PILLION_GATE_SCENARIOS=1` and `PILLION_GATE2_SCENARIOS=1` (`Gate2ScenarioFactAttribute`), so CI reports them as skipped | |
| `tools/gate0/Invoke-OfflineRun.ps1 -Phase 2` | The Gate 0 offline checks, then with the adapters still disabled the integration tests with the Gate 1 and Gate 2 scenarios (the big index and the generated solution are prepared before the network goes off), then the encryption checks | The product owner, elevated, with `-IAmReady` |

| Scenario | How it is driven | What is read |
| --- | --- | --- |
| Generated solution indexed; secret files | Open `Gate2.sln`; wait for the host to own the root, swept and idle | Time from open to idle; the root's files, chunks, disk bytes, model; `ListExcludedFilesAsync` (secret only and all); `ResolveMentionAsync(@file .env)` without and with `ApprovedSensitive`; a lexical search for the secrets' text |
| Keystrokes while indexing | The Gate 1 typing pass (autocomplete off, 80 ms a character, 400 ms line pauses) in a file of the generated solution while a background loop rebuilds the index whenever it goes idle; then the same pass with the index idle | TypeChar time on the UI thread, p50 and p95; the share of status samples that were busy |
| Save to searchable | Ten one-member edits and three whole-file rewrites in files the branches do not touch, each saved with DTE `Document.Save` (the IDE save path: `documentSaved` to the host) | From just before Save to the first lexical `SearchDetailedAsync` hit (polled every 10 ms) holding the new identifier in that file |
| Branch switch | Documents closed; `git checkout realistic` (or `rewrite`) run from the test with Visual Studio open; then back to master | git's own time; until five sampled files' new identifiers are searchable; until the host is idle again |
| Search on the 100,000-chunk index | Open the 1,000,000-line folder (index built by step d); 20 questions and identifiers, twice, after 3 warm-ups | Round trip from devenv.exe and the host's `SearchResponse.TotalMs`, p50 and p95; degraded responses |
| Context assembly with retrieval | `PreviewContextAsync` (what the Context Inspector shows) with retrieval on, 8 chunks, Local-strong; 40 after 3 warm-ups | Round trip, p50 and p95; retrieved items and kinds |
| Workspace queries | 20 `@symbol` resolutions (after 4 warm-ups, 250 ms apart) on the generated solution: the host asks the VSIX's `IWorkspaceQueryService` (Roslyn) | Round trip, p50 and p95; the 2.14 integration test for the UI thread, one-in-flight and rate rules |
| Persistence | Create a conversation, pin a file, one chat message (Ollama); kill the host; read it back; in a second Visual Studio session read it again | Messages, pins and the request's Outbound records after each |
| Chat that cites | One question about the generated code with retrieval on (Local-strong, Ollama) | The final chunk's citations (explicit and implicit) |
| Index window | **Pillion: Open Index** with the generated solution open | The view model's root rows and counts |

Each run writes `tools/gate2/out/<timestamp>-gate2/` (git-ignored): `run.log`, `gate2-results.json`, `gate2-summary.md`
(the criteria table first), `build.log`, `test-*.log`, the TRX files under `tests-*`, one `index-<source>-<lines>-<ep>.json`
per indexing run, `ide-VS<major>/` (the IDE TRX, `invoke-idetests.log`, the scenario JSON files under `scenarios/`, logs),
`restart/` for the second session, the log excerpts and the settings backup.

## Runs

Date: 2026-10-15. Machine: the reference machine (Ryzen 7 3700X, 8 cores and 16 threads, 32 GB, RTX 2080 SUPER 8 GB, driver
617.42, Windows 11 Pro 26300); Visual Studio 2026 18.11.0 (18.11.12306.390) and Visual Studio 2022 17.14 (17.14.37806.7);
Ollama 0.40.4 with `qwen2.5-coder:7b` (digest `dae161e27b0e`, Q4_K_M) and `qwen2.5-coder:1.5b` (`d7372fd82851`); the bundled
`bge-small-en-v1.5` int8 (`model_quantized.onnx` sha256 `6c9c6101...ee7dbfe4`, the hash 2.16 pinned); ONNX Runtime 1.24.4
(DirectML package). Code: branch `feature/gate2-evidence` on `feature/phase2-wave1` at `71c6714` (every phase 2 item, 2.16
included). VSIX 0.1.0, Pillion Host 0.1.0, contract 1.3. `settings.json` came back byte for byte after every run, and no
`devenv.exe` or `Pillion.Host.exe` of a run was left.

| Run folder (`tools/gate2/out/`) | Visual Studio | Unit and component | Benchmark and Model | Generated repositories | IDE suite (with scenarios) | Restart, encryption, two instances | Result |
| --- | --- | --- | --- | --- | --- | --- | --- |
| `20261015-164218-gate2` | 18.11 | 4,513 / 0 / 1 skipped (the step was marked failed by a script error after the tests, fixed) | 22 / **1** (`RetrievalSmokeTests`: my regression input had landed in the evaluation's fixtures corpus, 25 files instead of 24; moved, passes) | 6 runs; the 1,000,000-line DirectML run stopped on purpose (see the indexing section) | **38 / 0** / 1 skipped (the restart check, which runs in its own session) | Pass, Pass, Pass | 3 steps failed for the reasons given, none a product failure; every criterion measured |
| `20261015-182457-gate2` | 17.14 (`-SkipBenchmarks -SkipIndexing -SkipTwoInstances`, the 18.11 run's indexes kept with `-KeepIndexes`) | 4,513 / 0 / 1 skipped | skipped | reused | **34 / 0** / 1 skipped | Pass, Pass, skipped | **Pass**: 14 steps, 0 failed |

Two quick runs (`20261015-135923-gate2`, `20261015-162318-gate2`, with `-Quick` and the Gate 2 scenarios only) debugged the
script and the scenarios; their figures are not evidence.

## Gate 2 criteria

18.11 figures are from run `20261015-164218-gate2`, 17.14 figures from run `20261015-182457-gate2` (the 17.14 run reused the indexes of the
18.11 run, so the indexing rows have one set of figures). Evidence paths are relative to the run folders.

| Criterion | Target | Status | Measured: 18.11 | Measured: 17.14 | How measured | Evidence |
| --- | --- | --- | --- | --- | --- | --- |
| Retrieval quality | Recall@10 of 0.85 and MRR, bundled model; 0.90 with the code model if it ships; int8 loss within 2 points | **Pass on this repository's sets**; the three external fixture repositories wait on issue #50 | Hybrid recall@10 by file / MRR on `pillion-src` (60 queries): bge-small **0.967 / 0.831**, qwen3-embedding 1.000 / 0.814, jina 0.967 / 0.826, e5 0.950 / 0.764; int8 loss **0.0 points** for every model; `fixtures` 1.000 for every model (a sanity set) | Same (not version dependent) | Work item 2.16's harness, quoted, not re-run | [eval-retrieval-baseline.md](eval-retrieval-baseline.md); `tools/eval/out/20261015-122402/` (bge-small), `-122845/` (qwen3), `-123609/` (jina), `-125115/` (e5) |
| Initial index, 100,000 lines (about 10,000 chunks), bundled model, CPU | 3 minutes or less | **Fail on real code** (generated code passes); decision pending product owner (#90) | Generated C#: 101,101 lines, 12,101 chunks in **1.48 min** (135.9 chunks/s, 69 tokens a chunk). This repository's `src` (real code): 112,311 lines, 10,469 chunks in **3.86 min** (45.2 chunks/s, 172 tokens a chunk). In the IDE (devenv.exe, real host, the default execution provider): the generated solution (102,600 lines, 9,605 chunks) indexed in **79 s** | Same indexes | `Gate2IndexingTests` (real pipeline, bundled int8, CPU); the IDE scenario | `index-synthetic-100000-cpu.json`, `index-real-100000-cpu.json`, `ide-VS18/scenarios/gate2-index-solution-VS18.json` |
| Initial index, 1,000,000 lines, CPU | 30 minutes or less, in the background, partial results searchable after the first minute | **Fail on real code** (generated code passes); partial results pass; decision pending product owner (#90) | Generated C#: 1,011,001 lines, 121,001 chunks in **15.7 min**. Real code (11 copies of `src`): 1,010,791 lines, 94,227 chunks in **35.3 min** (44.5 chunks/s). First results searchable after 3.1 s and 9.5 s; 7,866 and 2,812 chunks searchable at the one-minute mark | Same indexes | As above; the pipeline at below-normal priority on 8 of 16 threads | `index-synthetic-1000000-cpu.json`, `index-real-1000000-cpu.json` |
| One saved file becomes searchable | 500 ms or less | **Pass** (lexical in full; vectors in part for a large new file, as decided in 2.10) | One-member edits: p50 **224 ms**, p95 272 ms, max **272 ms** (10 saves); whole-file rewrites (every chunk new): p50 360 ms, max 408 ms | Edits p50 **217 ms**, max **272 ms**; rewrites p50 334 ms, max 352 ms | DTE `Document.Save` in the IDE, then lexical `SearchDetailedAsync` every 10 ms until the new identifier is found in that file. The host-side figure (no RPC polling): median 33 ms, max 81 ms (`IndexingWatcherModelTests`) | `gate2-save-searchable-VS18.json`, `tests-benchmark/*.trx` |
| Branch switch touching 500 files | 30 s or less | **Pass for the realistic diff**; the whole-file rewrite misses; definition pending product owner | Realistic (two members changed in each of 500 files): searchable after **22.9 s**, idle after **26.5 s** (git itself 6.2 s). Whole-file rewrite: searchable after 78.3 s, idle after **92.1 s**. Host-side (no IDE): 17.0 s and 73.4 s | Realistic: searchable 22.8 s, idle **26.3 s**; rewrite idle 91.3 s | `git checkout` run from the IDE test with Visual Studio open: the host's HEAD watcher and hash sweep; `IndexingWatcherModelTests` | `gate2-branch-realistic-VS18.json`, `gate2-branch-rewrite-VS18.json` |
| Semantic search query | p50 100 ms, p95 300 ms on a 100,000-chunk index, hybrid without the reranker | **Pass** | 121,001 chunks, from devenv.exe: p50 **82 ms**, p95 **140 ms** (host `TotalMs` p50 82 ms, p95 139 ms); 1 of 40 degraded. Component benchmark (hashing embedder): p50 33.5 ms, p95 42.7 ms | p50 **78 ms**, p95 **180 ms**; 2 of 40 degraded | `SearchDetailedAsync` from the IDE, 40 queries after 3 warm-ups; `RetrievalBenchmarkTests` | `gate2-search-VS18.json` |
| Context assembly with retrieval | p50 150 ms, p95 400 ms on a 100,000-chunk index | **Pass** | From devenv.exe: p50 **53 ms**, p95 **160 ms**, 8 retrieved chunks and the repo map per request. Component benchmark with the bundled model on the CPU: p50 **150.9 ms**, p95 164 ms (query embedding 132 ms of it), a hair over the p50 | p50 **70 ms**, p95 **176 ms** | `PreviewContextAsync` (the Context Inspector) from the IDE; `ContextAssemblyModelBenchmarkTests` | `gate2-context-assembly-VS18.json`, `tests-benchmark/*.trx` |
| Workspace query answered by devenv.exe | p50 300 ms, p95 2 s, off the UI thread, one in flight, at most 5 a second | **Pass** | `@symbol` through the host on the generated solution: p50 **14 ms**, p95 16 ms; the 2.14 integration test (UI thread untouched, one in flight, the rate) passed | p50 **15 ms**, p95 41 ms; integration test passed | `ResolveMentionAsync(@symbol)` x20; `WorkspaceQueries_FindReferencesAndCallersOf_...` | `gate2-workspace-queries-VS18.json`, `ide-VS18/ide-VS18.trx` |
| Host memory while indexing | 1.5 GB or less by default; batch halves under pressure, then indexing pauses | **Pass** | Peak working set of the indexing process: 332 to 392 MB at 100,000 lines, **681 MB** (generated) and **853 MB** (real) at 1,000,000 lines, 498 to 546 MB with DirectML; no memory pause, batch 32 throughout. The halving and pausing are covered by the 2.9 component tests | Same | `Gate2IndexingTests` with the real working set governing the cap | `index-*.json` |
| Index on disk | About 1.5 KB per chunk | **Fail on real code**; decision pending product owner (#90) | Generated code 1,222 to 1,271 bytes a chunk (1,483 to 1,723 with the WAL); real code **2,204 to 2,220** (2,492 to 2,933 with the WAL); the IDE's own index of the generated solution 2,012 | Same | Database size over chunks | `index-*.json` |
| Indexing never disturbs the IDE | Below-normal priority, at most half the cores; paused during build, test, debug and battery saver; keystroke latency unchanged with indexing running | **Pass** | TypeChar on the UI thread while the index was rebuilt the whole time (97% of status samples busy): p50 3.21 ms, p95 4.18 ms; with the index idle p50 3.09 ms, p95 3.93 ms: **+0.12 ms p50, +0.26 ms p95**. Priority, cores and the pauses: 2.9's component tests (step b) | +0.09 ms p50, +0.06 ms p95 (99% busy) | The Gate 1 typing pass (autocomplete off) in a file of the generated solution with a background loop rebuilding the index | `gate2-keystrokes-indexing-VS18.json`, `gate2-keystrokes-idle-VS18.json` |
| Persistence | Conversations, pins and the Outbound log survive a host restart and a Visual Studio restart; two hosts share one index with one writer; a stale lock is reclaimed | **Pass** | Host kill: the conversation back with 2 messages and its pin, the request's Outbound record kept, Ready again after the kill. Visual Studio restart (a second session): found with 2 messages. Two instances: one `writer.lock` owner of the two hosts; the owner's Visual Studio and host killed hard (stale lock left on disk); the other host took the index over after **14.3 s** | Host kill and restart: found, 2 messages, the pin, the Outbound record; the two-instance check ran on 18.11 only | IDE scenario; a second vstest session; two `devenv /rootsuffix PillionG2` on one folder, `writer.lock` read from disk | `gate2-persistence-host-VS18.json`, `restart/ide-VS18/scenarios/gate2-persistence-restart-VS18.json`, `gate2-results.json` step e3 timeline |
| Encryption at rest | `index.db` and `conversations.db` unreadable without the DPAPI-protected key; key file user-only; no plain chunk text outside the database | **Pass** | Three `index.db` and `conversations.db`, copied and opened with Python's plain `sqlite3`: "file is not a database" for each. Four key files: one access rule, the user, full control (inheritance off). A sentence only the generated code holds was found in 0 of 162 files under `%LOCALAPPDATA%\PillionProgrammer` (logs, settings, state files, databases, WAL) | Same result (four databases, four key files, 0 of 178 files) | Scripted check after the IDE run | `gate2-results.json` step e2 |
| Secret-file exclusion | Not indexed, listed in the Index window, per-file approval as a pin | **Pass** | `.env` and `appsettings.Development.json` (fake secrets, committed to the repository) listed first as secret-bearing (`SecretBearing`); `@file .env` without approval: "It looks like a secret file ... Approve it to pin the file", no content; with approval: resolved, 46 tokens; a search for their text found nothing in them; the Index window test passed | Same; generated solution indexed in 78.6 s | IDE scenario: `ListExcludedFilesAsync`, `ResolveMentionAsync` with and without `ApprovedSensitive`, `SearchDetailedAsync` | `gate2-index-solution-VS18.json` |
| Cited answers | Replies that used retrieved chunks cite `file:line` spans; citations open the location | **Pass**, with open issue 3 | A question about the generated code (Local-strong, `qwen2.5-coder:7b`): 4 citations, all implicit (the model named no `path:line`), e.g. `src\Module10\File22.cs:12-50`; one of them from an index outside the open workspace (open issue 3). Opening a span: `CitedAnswer_ShowsTheSpanLink_...` passed | 7 citations, all implicit; 4 of them from outside the workspace (open issue 3). The window test is built for 18.x only | `ChatAsync` with retrieval on; the 2.12 integration test (fake host, the window) | `gate2-chat-cites-VS18.json` |
| Autocomplete prefix stability | At most three snippets before the prefix, unchanged across keystrokes in a member; the Gate 1 autocomplete rows do not regress | **Fail on 18.11 (keystroke p95), Pass on 17.14**; the latency rows hold on both (open issue 5) | With retrieval on: 7B time to suggestion p50 **217 ms**, p95 521 ms (Gate 1: 184 to 277 / 421 to 498 ms); 1.5B p50 124 ms; 0 prose, 0 fenced; prompt-prefix reuse **59%** (13 of 22, 7B) and **61%** (11 of 18, 1.5B); host cache hits 0; keystrokes on against off **+0.14 ms p50, +4.17 ms p95** (on 8.20 ms, off 4.03 ms; Gate 1's on-pass p95 was 11.1 to 11.4 ms against an off-pass of 11.7 to 11.9 ms) | 7B p50 **216 ms**, p95 640 ms; 1.5B p50 103 ms; prefix reuse 62% (7B) and 79% (1.5B); keystrokes **+0.20 ms p50, +0.96 ms p95** (passes here) | The Gate 1 typing scenarios in the same IDE run, retrieval on and the scratch solution indexed | `autocomplete-{strong,fast,off}-VS18.json` |
| Offline run | Every check above with the network adapter disabled | **Pending product owner** | `tools/gate0/Invoke-OfflineRun.ps1 -Phase 2` written; dry run below | | Elevated, by the product owner | |
| CI | Green on develop with the 18.x job and the 17.14 integration-test job | **Pass on `develop`** (phase 1 code); the phase 2 branch: this pull request's CI (see Part A) | `develop` at `9037705`: run 38659980451, both jobs success. Phase 2 is not on `develop` yet: PR 82 (`feature/phase2-wave1`) is red on CI (see Part A) | | `gh run list` | `gate2-results.json` step g |
| Feature flags | Every work item behind a flag; tests green | **Pass** | The README names all eight phase 2 flags; 4,513 tests passed, 0 failed | 4,513 passed, 0 failed | README; test results | `tests-unit/*.trx` |

**Counts (18.11): 13 pass, 4 fail (three are decisions pending the product owner with the figures below; one is the keystroke p95 of the autocomplete row), 1 pending the product owner (the offline run), 1 measured on `develop` only (CI).** On 17.14 every criterion it measured held, the autocomplete row included (the indexing rows are not version dependent and were not re-run; the two-instance check ran on 18.11).

## Indexing: the figures behind issue #90 and issue #78

Every run below is `Gate2IndexingTests`: the real `IndexingService` composed in-process, the real gateway, the bundled int8
model, the real working set governing the memory cap, the pipeline on 8 of 16 threads at below-normal priority. "Real code" is
this repository's `src` (one copy for 100,000 lines, eleven for 1,000,000, each under its own folder).

| Run | Lines | Chunks | Tokens a chunk | Time | Chunks a second | First searchable | Searchable at 1 min | Bytes a chunk (with WAL) | Peak working set |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| Generated, CPU | 101,101 | 12,101 | 69 | 1.48 min | 135.9 | 2.5 s | 7,866 | 1,222 (1,723) | 332 MB |
| Real code, CPU | 112,311 | 10,469 | 172 | **3.86 min** | 45.2 | 7.6 s | 2,790 | **2,220** (2,933) | 392 MB |
| Generated, CPU | 1,011,001 | 121,001 | 71 | 15.7 min | 128.8 | 3.1 s | 7,866 | 1,271 (1,483) | 681 MB |
| Real code, CPU | 1,010,791 | 94,227 | 173 | **35.3 min** | 44.5 | 9.5 s | 2,812 | **2,204** (2,492) | 853 MB |
| Generated, DirectML (int8) | 101,101 | 12,101 | 69 | 1.37 min | 147.0 | 3.2 s | 7,382 | 1,221 (1,649) | 498 MB |
| Real code, DirectML (int8) | 112,311 | 10,469 | 172 | **5.20 min** | **33.6** | 8.7 s | 2,301 | 2,324 (2,999) | 546 MB |

- **The CPU path on real code is 1.3 times over both throughput budgets**: 3.86 minutes per 100,000 lines (target 3) and 35.3
  minutes per 1,000,000 (target 30). Embedding is 95% of the time. Generated code, with chunks of about 70 tokens, meets both
  with room to spare; the difference is the chunk length, as 2.9 found.
- **DirectML did not help on real code.** In the pipeline it was *slower* than the CPU (33.6 against 45.2 chunks a second)
  and only 8% faster on generated code, although the isolated benchmark of full 512-token batches gives 55.2 chunks a second
  on DirectML against 14.1 on the CPU (3.9 times). The likely cause is the 512² attention-cell cap per run that 2.9 set for
  the CPU's memory: with real chunks of mixed length it splits the work into many small runs, and DirectML pays its dispatch
  cost per run. Not investigated further here. The FP16 graph (an optional 67 MB download) was not measured. Because the
  100,000-line run already answered the question, the 1,000,000-line DirectML run was stopped after 48 s to save 50 minutes.
- **Index size**: real code is 2.2 KB a chunk (the chunk text is longer), generated code 1.2 to 1.3 KB.
- **Partial results**: searchable within 10 s in every run; at the one-minute mark 2,800 chunks (real code) or 7,900
  (generated) were stored and searchable.
- The DirectML runs were on the same RTX 2080 SUPER Ollama uses; Ollama models were unloaded before each run.

## IDE scenarios: what else was seen

- **The generated solution in the IDE**: 607 files (102,600 lines of C# plus the project files) indexed by the real host in
  79 s from the solution opening, 9,605 chunks, with the default execution provider; the Index window showed the root as
  "Writer (this host owns the index); files 607 of 607; chunks 9,615; 15.9 MB; bundled/bge-small-en-v1.5".
- **The 1,000,000-line folder** opened with its prebuilt index and was idle 19.4 s later (a hash sweep of 1,001 files and the
  vector cache load).
- **Gate 1 rows that are not Gate 2 criteria**, re-run in the same IDE session with retrieval on (information): inline chat
  first line p50 468 ms, p95 1,163 ms (Gate 1: 333 to 366 / 818 to 895 ms); local chat first text p50 95 ms, p95 654 ms
  (Gate 1: 87 to 99 / 176 to 185 ms); mentions pinned `Program.cs` (45 tokens), `Errors` (50) and `Selection` (49), as in Gate 1;
  host kill: longest UI gap 278 ms, Ready again after 1,788 ms; Pillion's own UI-thread time from the folder open to Ready
  **8.6 ms** (18.11). On 17.14 (with the status bar fix of Part A): inline chat first line p50 443 ms, p95 1,046 ms; chat first text p50 129 ms, p95 1,035 ms; host kill: longest UI gap 264 ms, Ready again after 1,833 ms; Pillion's own UI-thread time to Ready **13.4 ms** (the status bar item's search 1.0 ms). The p95 of inline chat and chat first token are higher than in Gate 1 but within their budgets (5 s, 3 s);
  with retrieval on, both now assemble context and search.

## Decisions the figures force (pending the product owner)

| Decision | Figures | Options |
| --- | --- | --- |
| **Indexing throughput and index size on the CPU tier (issue #90)** | Real code: 3.86 min per 100,000 lines (budget 3), 35.3 min per 1,000,000 (budget 30), 2.2 KB a chunk (budget about 1.5); generated code: 1.48 min, 15.7 min, 1.2 to 1.3 KB. Embedding is 95% of the time; real chunks average 172 bge tokens | As in issue #90: restate the budgets per tier and for real code (about 4 and 36 minutes, 2.2 KB), embed a truncated view of long chunks (2.16's harness can measure the recall cost), or fund more ONNX tuning |
| **DirectML (issue #78)** | In the pipeline DirectML int8 is slower than the CPU on real code (33.6 against 45.2 chunks a second) and 8% faster on generated code; the isolated full-batch benchmark gives 3.9 times the CPU. So DirectML as shipped does not rescue the CPU budgets | Decide #78's licence question on its own; whatever the answer, the pipeline's attention-cell cap (2.9) should be measured per execution provider before DirectML is claimed as a speed-up (and the FP16 graph measured) |
| **Branch-switch definition** | Realistic diff (two members in each of 500 files): 26.5 s in the IDE, 17.0 s host-side. Whole-file rewrite of 500 files: 92.1 s in the IDE, 73.4 s host-side; open documents searchable within about 5 s either way | The lead's working definition (realistic diff judged, rewrite reported) or a budget for the rewrite case |
| **Partial vectors within 500 ms** | One-member edits searchable in 224 ms p50 and 272 ms max through the IDE; a file whose chunks are all new is searchable lexically within 408 ms, its vectors in part, the rest right after | Confirm the 2.10 rule (lexical in full within the budget) |
| **Default embedding model (issue #95)** | From 2.16: bge-small 0.967 recall, qwen3-embedding 1.000, jina 0.967, e5 0.950; bge-small is the cheapest to index and query | As in issue #95 |

## Open issues

1. **The TSX chunker takes minutes on some inputs** (product, found by this item). An 8 KB file of random code-like tokens
   (`tests/Pillion.ContextEngine.Tests/Regressions/tsx-slow-parse.txt`) takes about six minutes to chunk as `.tsx`, at any
   cap; 40 ms as `.ts`. One such file in a repository would hold the indexing pipeline (there is no per-file time limit). The
   chunker property test hits it about one run in five (4 min 8 s once). A skipped regression test is in place.
2. **`ConversationRealHostTests` cause found on CI** (the window could not send yet); fixed in the test. The same race would
   make a fast user's first Send a no-op while the model list loads; whether the window should queue or disable Send visibly
   is a product question (the Send button is disabled while it cannot send, so a user sees it).
3. **Retrieval reads indexes outside the open workspace** (product). A chat in the generated solution cited
   `pillion-ide-tests\queries-...\Checkout.cs` (an IDE test's deleted scratch folder) and, in a quick run,
   `repo-synthetic-50000\...` (another folder indexed on this machine); on 17.14, 4 of 7 citations came from other folders (two Gate 1 scratch projects and an IDE test's folder). Retrieval asks the catalog for every index on disk when
   it is given no roots (2.12 noted that an empty root list means "every index"). Another repository's code can then reach
   a prompt. Chat retrieval should be scoped to the workspace's roots.
4. **Indexes of deleted folders accumulate** in `%LOCALAPPDATA%\PillionProgrammer\index` (every IDE test creates one), and a
   root that disappears leaves its index in `Failed` ("the repository root does not exist") until the workspace changes. The
   gate script deletes them at the end; the Index window's Delete exists for users. Retention of stale indexes is a 2.18 or
   phase 3 item.
5. **Keystroke p95 with autocomplete on**: on 8.2 ms against off 4.0 ms (+4.2 ms; budget +2). The on-pass is like Gate 1's
   (11.1 to 11.4 ms); Gate 1's off-pass was noisier (11.7 to 11.9 ms), which hid the difference. Not caused by retrieval as
   far as these runs show; to look into with the UI-thread timings (the slowest keystrokes are the ones that accept a
   suggestion, 213 ms max).
6. **Prompt-prefix reuse is 59 to 61%** with retrieval on (issue #47 option 1's counter), under the 70% the cache row asked
   for; the host's own cache had 0 hits in the typing passes, as in Gate 1. Issue #47 stays open.
7. **`ContentInspector.cs` is excluded from indexing as generated code** (issue #96, found by 2.16): it contains the marker
   text it looks for. Not fixed here.
8. **CI on the phase 2 branch**: PR 82 (`feature/phase2-wave1`) is red. This branch fixes every failure the CI logs show
   (Part A); its own CI result is in the pull request. `HostWorkspaceQueries` judged a find-references answer partial by
   `took >= budget`; on CI one answer was not marked partial (`WorkspaceQueriesTests.FindReferences_ThatTookTheWholeBudget_...`,
   seen once in the wave branch's CI, not since); not reproduced, so not changed.
9. **`IndexingWatcherModelTests` and the other Model tests no longer run in CI** (this item's change to `ci.yml`), only in the
   gate's step c.
10. **The context assembly component benchmark with the bundled model** is at its p50 limit (150.9 ms against 150; the
    query embedding takes 132 ms of it on the CPU). The IDE figure (53 ms p50) is well inside; issue #64 tracks the margin.
11. **Not measured**: the FP16 DirectML graph; the reranker (off, licence review); the host's idle working set (150 MB budget);
    the offline run (the product owner's); the TypeScript and Python ghost text by hand (Gate 1's item 6).
12. Issue #56 (`InlineChatSessionTests.Escape_WhileStreaming...` order-dependent) is fixed by this item and can be closed when
    it merges.

## What the product owner must do

1. **Decide issue #90** (throughput and index size on the CPU tier) with the indexing table above, and **issue #78** (DirectML),
   now knowing that DirectML as shipped is not faster on real code.
2. **Confirm the branch-switch definition** (realistic diff judged, whole-file rewrite reported) and **the partial-vectors rule**
   for large saves.
3. **Run the offline gate**, elevated, after a normal run: `pwsh tools/gate0/Invoke-OfflineRun.ps1 -IAmReady -Phase 2` (about 60
   minutes: 20 to build the big index before the network goes off, then the Gate 0 checks and the IDE scenarios offline). If the
   window is closed mid-run: `Get-NetAdapter | Where-Object Status -eq Disabled | Enable-NetAdapter`. Dry run:

```
Network adapters this run disables (and re-enables at the end):
  Ethernet                       Up           00-D8-61-9C-FE-C0  Realtek PCIe GbE Family Controller
  vEthernet (Default Switch)     Up           00-15-5D-44-7A-00  Hyper-V Virtual Ethernet Adapter

Dry run (-WhatIf): nothing is changed, Visual Studio is not started.
What if: Disable-NetAdapter 'Ethernet'
What if: Disable-NetAdapter 'vEthernet (Default Switch)'
Then: launch the Experimental Instance, wait up to 70 s for 'Offline', run the local chat, host-kill and Local-only checks, re-enable the adapters, close the instance and restore settings.json.
Phase 1: settings route autocomplete to ollama/qwen2.5-coder:1.5b (Local-fast) and chat and inline edits to ollama/qwen2.5-coder:7b (Local-strong).
         After the Local-only check, close the instance and, with the adapters still disabled, run tools/vs-tests/Invoke-IdeTests.ps1
         with PILLION_GATE_SCENARIOS=1: the integration tests plus typing with ghost text (strong, fast, off), inline chat
         (extract a method, /doc), chat first token, @file/@errors/@selection with the Context Inspector and the Outbound
         records of every task class. Results in tools/gate1/out/<timestamp>-offline/.
Phase 2: before the adapters go down, create the Gate 2 generated solution and index 1,000,000 generated lines into the data
         folder with the bundled model on the CPU (about 20 minutes). With the adapters down, the IDE run also has
         PILLION_GATE2_SCENARIOS=1 (indexing, keystrokes while indexing, save to searchable, branch switches, search and
         context assembly on the big index, a cited chat on Ollama, the Index window, workspace queries, persistence),
         then the encryption-at-rest checks. The gate indexes are deleted at the end. Results in tools/gate2/out/<timestamp>-offline/.
```

4. **Say where the private golden-set repository lives (issue #50)**, so the retrieval row can be measured on the three
   external fixture repositories, and choose the default embedding model (issue #95).
5. **Gate decision**: record pass or fail in the decision log.

To reproduce the runs: `pwsh tools/gate2/Invoke-Gate2.ps1 -KeepIndexes` on Visual Studio 2026, then
`pwsh tools/gate2/Invoke-Gate2.ps1 -DevEnvPath "C:\Program Files\Microsoft Visual Studio\2022\Community\Common7\IDE\devenv.exe" -SkipBenchmarks -SkipIndexing -SkipTwoInstances`
(about 3 hours and 45 minutes in all, from an idle machine with no Visual Studio open).
