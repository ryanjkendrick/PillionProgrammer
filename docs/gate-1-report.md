# Gate 1 report

Phase 1 closes at Gate 1 ([phase-1-plan.md](phase-1-plan.md)): "autocomplete without retrieval and chat within budget,
online and offline" (design document section 17). This report collects the evidence for each criterion, says how it was
measured and lists what only the product owner can do. Work item 1.18.

Date: 2026-10-10. Machine: the reference machine (Ryzen 7 3700X, 8 cores and 16 threads, 32 GB, RTX 2080 SUPER 8 GB,
driver 617.42, Windows 11 Pro 26300); Visual Studio 2026 18.10.3 and Visual Studio 2022 17.14.41 with IntelliCode; Ollama
0.40.1 with `qwen2.5-coder:7b` (digest `dae161e27b0e`, 7.6B, Q4_K_M), `qwen2.5-coder:1.5b` (`d7372fd82851`, Q4_K_M) and
`gemma4:e2b`. Code: branch `feature/gate1-evidence`: `develop` at `4845f06` (every phase 1 work item through PR 39) plus
the scripts and tests of this work item for runs 1 to 5, and after merging `develop` at `f6b6796` (PR 40, Pillion's own
UI-thread timings) for runs 6 and 7. VSIX 0.1.0, Pillion Host 0.1.0, contract 1.2. No provider key is
stored on this machine (`cmdkey /list:PillionProgrammer/*` lists only two `test-probe-*` credentials left by tests), so the
cloud rows are pending.

**Summary.** Of the 17 criteria, 9 pass (keystroke latency only borderline on 17.14), 4 fail and 4 are pending the
product owner. The
failures are the 7B autocomplete p50 (about 0.4 to 0.56 s against 300 ms in the IDE), the CPU-only 1.5B p50 (about 1.0 s
against 600 ms, expected), the prefix cache hit rate in the IDE (2 to 3% against 70%) and the VSIX's UI-thread CPU (not
shown to be under 50 ms; the upper bound measured is 78 to 266 ms). Behind the first and third is a finding that matters
more than the numbers: **the ghost text the local models produce in the IDE is not code** (open issues 1 and 2).

## Autocomplete quality fixes (2026-10-11)

Branch `feature/autocomplete-quality` (open issues 1 to 4; decisions in the [phase 1 plan's](phase-1-plan.md) decision
log, 2026-10-11). The numbers further down are the 2026-10-10 runs, kept for comparison; where this work changes a row,
the row says so.

**Root cause of the prose and the fences.** Ollama's `qwen2.5-coder` template takes the fill-in-the-middle path only
when `suffix` is not empty (`{{- if .Suffix }}`). The typing scenario types a new file with brace completion off, so at
every line-break pause the caret was at the end of the document, the suffix was empty, and the prompt went to the model
as a chat turn, which the instruct-tuned models answered. The header of sentences (`// Recent edit: C:\...`) made the
answers about the file. The adapter now sends a line break as the suffix when it is empty; the header is terse code
comments with file names only, and off for the 1.5B (catalog `fimHeader`); the gateway drops a reply that opens in prose
and strips a leading fence; the extension never shows a fence line.

**Measured on 20 fixed prefixes** (8 C#, 6 TypeScript, 6 Python, eight of them at the end of the document;
`AutocompletePromptQualityLiveTests`, `Category=Live`, seed 42, temperature 0.2, both models resident on the reference
machine). Raw replies (prose / fence / code) and, for what the user would see after the gateway and the extension's block
rule, how many suggestions leave the document parsing (Roslyn, Node's TypeScript stripping, Python's `ast`):

| Model | Gate 1 request (sentence header, empty suffix passed on) | Terse header, suffix still empty | Shipped: terse header | Shipped: no header |
| --- | --- | --- | --- | --- |
| `qwen2.5-coder:1.5b` | 3 / 5 / 12; shown and parsing 12 of 12 (8 empty after the new filter) | 2 / 6 / 12 | 0 / 0 / 20; 19 of 20 parse; median 173 ms | **0 / 0 / 20; 20 of 20 parse; median 149 ms** (shipped for the 1.5B) |
| `qwen2.5-coder:7b` | 5 / 3 / 12; 12 of 12 (8 empty) | 3 / 5 / 12 | **0 / 0 / 20; 19 of 20; median 356 ms** (shipped for the 7B) | 0 / 0 / 20; 19 of 20; median 351 ms |

The one suggestion that does not parse in either shipped column is a single-line ` {` after an arrow function's `=>`,
a legitimate start of a block body. Before the fix the filter alone would already have kept all of the prose and fences
off the screen (the "Gate 1 request" column shows nothing in prose or fenced after it), but at the cost of 8 empty
suggestions in 20.

**IDE re-measurement: not obtained.** Three runs of `Invoke-Gate1.ps1 -SkipBuild -SkipTests -SkipCost` were made, each
started only after no `devenv.exe` had run for 90 s:

- `tools/gate1/out/20261011-054553-gate1` (18.10): the Experimental Instance held five copies of Pillion (four left by
  other agents' installs of other branches the same morning), and the MEF composition failed for Pillion's editor parts
  (`Unable to find method: PillionProposalSourceProvider..ctor(AutocompleteHost)`, "The Visual Studio component cache is
  out of date" in `ide-VS18/logs/ActivityLog-VS18.xml`). Pillion's proposal source was never created: no request in the
  typing passes, the canned ghost-text test and the inline chat tests failed. The scenario figures of this run are
  therefore not evidence for or against this work.
- `tools/gate1/out/20261011-060203-gate1` (17.14): the package did not load at all ("Timed out after 60 s waiting for
  the Pillion package to load"; the Activity Log shows the extension manager failing to load `PkgDefMgmt.dll`).
- An earlier attempt stopped at the install check (the bin DLL and the packaged VSIX differed after an interrupted
  build).

Deleting other agents' extension folders was out of bounds, so the rows below keep the 2026-10-10 IDE figures. To
re-measure: with no Visual Studio running and the stale `Pillion*` folders removed from the two Experimental Instances
(`%LOCALAPPDATA%\Microsoft\VisualStudio\18.0_*Exp\Extensions` and `17.0_*Exp\Extensions`), run
`pwsh tools/gate1/Invoke-Gate1.ps1 -SkipTests -SkipCost` (and with `-DevEnvPath` for 17.14). The typing scenario now
records `nonMatchingProposals`, `proseProposals` and `fencedProposals`, asserts no prose and no fence with autocomplete
on, and asserts that the pass with it off sent no request to the extension's pipeline or the host.

## How the evidence is produced

| Script | What it does | Who runs it |
| --- | --- | --- |
| `tools/gate1/Invoke-Gate1.ps1` | a. builds the full solution (Release, Visual Studio 2026 MSBuild, no deployment) and records the versions and the models by digest; b. unit and component tests (`Category!=Live`) with TRX, then the live tests (`Category=Live`) of `Pillion.ModelGateway.Tests` and `Pillion.Host.Tests` one class at a time from an idle Ollama, collecting the figures they print; c. backs up `settings.json` and writes the run's settings (Ollama provider, `qwen2.5-coder:7b` as Local-strong for chat and inline edits, `qwen2.5-coder:1.5b` as Local-fast for autocomplete, phase 1 flags on); d to k. the Visual Studio integration tests in the Experimental Instance of the driven version (the whole suite, including the host-kill test) with the Gate 1 scenarios switched on; g. the VSIX cost launches in `/rootsuffix PillionG1` (installed) against `PillionG1Off` (not installed); j. the degradation component test's result; l. the evaluation baseline (pending); m. the latest CI runs on `develop` (`gh`); n. restores `settings.json` byte for byte, uninstalls from `PillionG1`, checks for leftover processes and leaves `qwen2.5-coder:7b` resident. `-SkipBuild`, `-SkipTests`, `-SkipLiveTests`, `-SkipIde`, `-SkipCost`, `-DevEnvPath`, `-CloudProviderId`, `-KeepInstanceOpen` | Anyone with Ollama and the two models; the agent ran it |
| `tools/gate1/Gate1.Scenarios.psm1` | The helpers: TRX reading, the IDE run, the cost launches, model digests, CI, and the criteria table | |
| `tools/gate0/Invoke-OfflineRun.ps1 -Phase 1` | The Gate 0 offline checks plus, with the adapters still disabled, the integration tests and the Gate 1 scenarios | The product owner, elevated, with `-IAmReady` |
| `tools/gate0/Gate0.Checks.psm1` | Shared by both gates: run folder, logging, results, settings backup and restore, Visual Studio discovery and VSIX installation, now with a root suffix other than `Exp` | |

Each run writes `tools/gate1/out/<timestamp>-gate1/` (git-ignored): `run.log`, `gate1-results.json`, `gate1-summary.md`
(the criteria table first), `build.log`, `test-unit.log`, `test-live.log`, the TRX files under `tests-unit/` and
`tests-live/`, `ide-VS<major>/` (the IDE TRX file, `invoke-idetests.log`, the scenario JSON files under `scenarios/`,
`metrics-during-run.jsonl` with the metrics log lines written during the run, Activity Logs and Pillion logs under
`logs/`), the host and extension log excerpts and the settings backup.

**The scenarios run inside Visual Studio.** Typing into the editor from outside needs the foreground and real key
presses, which would fight the developer and other agents for the desktop. So the IDE scenarios are `[IdeFact]` tests in
`tests/Pillion.VisualStudio.IntegrationTests/PillionPackageTests.Gate1.cs` (logic in `GateScenarioChecks.cs`), skipped
unless `PILLION_GATE_SCENARIOS=1` is set when the tests are discovered, so CI reports them as skipped. They use the real
Pillion Host and the real models: nothing sets `AutocompleteOverrides` or `InlineChatOverrides`. The gate script runs the
whole suite with them on, so one Visual Studio launch covers the integration tests, the host-kill test and the scenarios.
They run in the shared `Exp` hive only when no `devenv.exe` is running at all (`Wait-NoDevenv`); the cost launches use the
private hives `PillionG1` and `PillionG1Off`.

| Scenario | How it is driven | What is read |
| --- | --- | --- |
| Autocomplete typing (7B, then 1.5B) | settings.json routes autocomplete to Local-fast with the model (the host announces `settingsChanged`); a warm-up suggestion in a separate file; then a fixed 40-line C# file (`GateScenarioChecks.Snippet`, 704 characters without indentation) typed into an empty file of a scratch C# project through the view's command chain (`IOleCommandTarget` TypeChar and Return, the path keystrokes take), one character every 80 ms on a fixed schedule, with a 1.2 s pause after each line break; brace completion off for the view so closing braces are not doubled; an open completion list is dismissed before a character that would commit it; a Pillion proposal that equals the next lines is accepted | After a reset of the extension's figures (`IAutocompleteMetrics.Reset`): time to first chunk and to suggestion (p50, p95, nearest rank), requests, shown, accepted, rejected, displaced, degradation, and the text of **Pillion: Show Status**; the host's counters before and after (`GetAutocompleteCountersAsync`: cache hits, type-through hits, misses, prompt tokens counted and estimated, resident models); the pass's Outbound records (model, outcome); per keystroke the time TypeChar took on the UI thread (the character is in the buffer when it returns; checked) and how late the UI thread let it start |
| Keystrokes with autocomplete off | The same typing with `features.autocomplete` false | TypeChar times, for the difference |
| Inline chat | Select `Place` in `Orders.cs`, **Pillion: Inline Chat** (the command), type the instruction in the prompt, Enter; five runs each of "extract the validation into a method" and `/doc`, Esc after each | From Enter: first `ProposalChanged` with text, first complete line, Preview state; the buffer version after Esc |
| Chat first token | **Pillion: Open Chat**, pick `qwen2.5-coder:7b`, type a one-sentence question, Enter, five times in a cleared conversation | Time to the first visible reply text (the window flushes every 50 ms) |
| Mentions and inspection | Build the scratch project (one compile error), select `Place` in an editor, then in the chat input `@Prog` and Down/Enter, `@errors` Enter, `@selection` Enter; **Inspect**; send | Pins and their token badges, the inspector's items, the reply; the host's Outbound records grouped by task class |

## Runs

| Run folder (`tools/gate1/out/`) | Visual Studio | Unit and component | Live tests | IDE suite (with scenarios) | VSIX cost | Result |
| --- | --- | --- | --- | --- | --- | --- |
| `20261010-202116-gate1` | 18.10.3 | 2790/0/0 | 13/6/19: Ollama could not load `gemma4:e2b` beside the two coder models my earlier trial runs had left pinned, and the CPU test timed out | 20/0/0 | Pass | 2 failed (live tests; step n's load request hung): both script bugs, fixed |
| `20261010-225359-gate1` | 18.10.3 | 2790/0/0 | 20/1/17: `OllamaResidencyLiveTests.ChatThatNeeds8192...` (see open issue 6); live classes ran side by side | 18/2/0: the two typing passes hit the framework's four-minute hang token | Pass | 2 failed: script bugs, fixed (keystrokes on a fixed schedule, live classes serial) |
| `20261010-232940-gate1` | 18.10.3 | 2790/0/0 | 21/0/17 | 20/0/0 | Pass | **Pass**, 1 pending |
| `20261010-234936-gate1` | 17.14.41 (`-DevEnvPath`) | 2790/0/0 | 18/1/19: `InlineEditLiveTests.Gemma_ExtractsTheValidation...` (open issue 8) | 20/0/0 | Pass | 1 failed (that live test), 1 pending |
| `20261011-000922-gate1` | 18.10.3 | 2790/0/0 | 18/1/19: the same Gemma live test | 20/0/0 | Pass | 1 failed (that live test), 1 pending |
| `20261011-003242-gate1` (after merging `develop`) | 18.10.3 | 2811/0/0 | 21/0/17 | 20/0/0 | Pass | **Pass**, 1 pending |
| `20261011-005306-gate1` (after merging, `-SkipTests -SkipCost`) | 17.14.41 | skipped | skipped | 20/0/0 | skipped | **Pass**, 1 pending |

The skipped live tests are the cloud adapters' (no keys). In every run `settings.json` came back with its original hash
(`562D8573...A9A4CFE`), no `devenv.exe` or `Pillion.Host.exe` of the run was left, the extension was uninstalled from
`PillionG1`, and `qwen2.5-coder:7b` was resident at the end. No timeout was widened: the first two runs' failures were
fixed in the script.

## Gate 1 criteria

18.10 figures are from runs `20261010-202116`, `20261010-232940`, `20261011-000922` and `20261011-003242` ("runs 1, 3, 5
and 6"; run 2's typing passes did not finish), 17.14 figures from `20261010-234936` and `20261011-005306` ("runs 4 and 7");
the evidence paths are relative to those run folders.

| Criterion | Target | Status | Measured: 18.10 | Measured: 17.14 | How measured | Evidence |
| --- | --- | --- | --- | --- | --- | --- |
| Ghost text works on 17.14 (IntelliCode on) and 18.x for C#, TypeScript and Python | Multi-line and single-line, Tab, Esc, word accept, next/previous; none while a completion item is selected; Tab never taken from another provider | **Pass (C#)**; TypeScript and Python **Pending product owner** (by hand). 2026-10-11: growing the displayed ghost text with `TryUpdateProposal` does not work on either version (CI run 38114630716), so single-line suggestions are shown when their line is complete (see "Autocomplete quality fixes") | `ProposalSource_ShowsGhostText_WithTheCompetitorEnabled` and `ProposalSource_NoProposal_WhileACompletionListItemIsSelected` passed with Copilot on, every run; real model: a Pillion proposal at 24 to 27 of 40 line-break pauses (show rate 68 to 81%) | Same two tests passed with IntelliCode on; a Pillion proposal at 34 and 35 of 40 pauses | IDE integration tests (canned completions); the typing scenario on the real host | `ide-VS18/ide-VS18.trx`, `ide-VS17/ide-VS2022.trx`, `scenarios/autocomplete-*-VS*.json` |
| IntelliCode run-time check on 17.x | Status bar and Output pane report it; nothing crashes | **Pass** | n/a | `IntelliCodeCheck_ReportsWhenAutomaticCompletionsAreOff` passed | IDE integration test with the option off | `ide-VS17/ide-VS2022.trx` |
| Autocomplete latency, 1.5B on the CPU | p50 600 ms, p95 1,500 ms, prompt within 1,024 tokens | **Fail** (p50) | First token p50 1,025 / 940 ms, p95 1,057 / 1,037 ms (runs 3 / 5); 1,123 / 13,825 ms in run 2 with live classes in parallel; prompt 991 to 993 estimated tokens | 980 / 1,008 ms | `AutocompleteLiveTests.Latency_Small_OnTheCpu_Direct` (the gateway's 1,024-token prompt posted with `num_gpu` 0). Not measured in the IDE: an Ollama provider entry has no `num_gpu` setting, so the host cannot force the CPU | `tests-live/*.trx` |
| Autocomplete latency, 7B on the GPU | p50 300 ms, p95 800 ms, prompt within 2,048 tokens with a cached prefix | **Fail** (p50) on 2026-10-10; **to re-measure**: from 2026-10-11 "time to suggestion" is the first visible text (the first chunk, p50 57 to 171 ms in these runs, for a single-line request; the 300 ms grace is gone), but because the displayed ghost text cannot be grown (CI run 38114630716) a single-line suggestion is shown when its line is complete, so the IDE p50 should fall by the 300 ms grace only where it applied (multi-line), and the single-line figure stays near the host's total time (p50 382 ms in run 3) | IDE time to suggestion p50 434 / 557 / 420 / 611 ms, p95 776 / 760 / 730 / 730 ms (runs 1 / 3 / 5 / 6, 23 to 30 suggestions each); first chunk p50 57 to 171 ms; visible after the line break (200 ms debounce included) p50 641 to 662 ms; largest prompt 286 tokens. Live, gateway only: first token p50 278 to 281 ms, p95 289 to 321 ms; total p50 421 to 424 ms, p95 443 to 478 ms at 2,043 to 2,045 tokens | IDE p50 420 / 568 ms, p95 730 / 739 ms (runs 4 / 7); live 277 / 319 ms | Typing scenario: `IAutocompleteMetrics` time to suggestion (from the request leaving the debounce to the first suggestion worth showing); live test `Latency_Large_OnTheGpu` | `scenarios/autocomplete-strong-VS*.json` (its `showStatusText` is the Show Status section), `metrics-during-run.jsonl` |
| Autocomplete latency, cloud Fast | p50 400 ms, p95 1,000 ms; 3 s timeout dropped silently | **Pending product owner** | No key stored | No key stored | `Gate1_Cloud_FastAutocompleteAndChatFirstToken` with `-CloudProviderId` | `scenarios/cloud-VS*.json` ("pending") |
| Keystroke latency with autocomplete on | +0 ms p50, +2 ms p95 | **Pass** on 18.10; **borderline** on 17.14 (p95 +1.3 to +2.3 ms) | TypeChar on the UI thread, on (1.5B) against off: p50 3.37 / 3.61 ms, p95 10.71 / 11.47 ms (run 1: -0.24 / -0.76 ms); run 3: p50 3.10 / 4.21, p95 4.29 / 12.72 ms; run 5: 3.08 / 3.18, 4.61 / 11.13 ms (on is not slower; the off pass, last in each run, was noisier) | Run 4: p50 2.54 / 2.52 ms, p95 5.80 / 4.43 ms, **+0.02 ms p50, +1.37 ms p95** (7B: +0.15 / +1.30 ms); run 7: **+0.09 / +1.82 ms** (7B: +0.31 / **+2.28** ms) | Typing scenario: each TypeChar timed until it returns, 704 per pass; the character is in the buffer when it does (0 misses) | `scenarios/autocomplete-{strong,fast,off}-VS*.json`, `typing.typeChar` |
| Prefix cache hit rate during steady typing | 70% or more | **Fail** in the IDE on 2026-10-10; **to re-measure** now that suggestions can be accepted (open issue 5) | Host counters over each pass: 2 to 3% (1 repeat in 31 to 51 requests; runs 1, 3, 5); live test (a request per keystroke, as the row was designed): 33 of 35 (94%) | 2% and 2% | Gateway counters (`GetAutocompleteCountersAsync`) before and after each typing pass; live tests' steady-typing loop | `scenarios/autocomplete-*-VS*.json` `autocomplete.host` |
| VSIX cost in devenv.exe | 0 ms blocking, under 50 ms UI-thread CPU, 60 MB or less added working set | **Fail** (UI-thread CPU not shown under 50 ms); blocking and working set pass | Pillion's own timed UI-thread work (PR 40's `UiThreadTimings`, run 6) from the folder open to Ready: **2.6 ms** in 10 stretches (Output pane flushes, status bar), which does not include the package's own initialization on the UI thread (not timed); UI thread CPU from "Begin package load" to Ready 78 / 203 / 109 ms (runs 1 / 3 / 5; 109 ms also in run 2), idle baseline 0 to 31 ms per second: an upper bound, since Visual Studio's own work on that thread in the same second is counted; working set on against off +2.3 / +17.2 / -9.8 / -5.2 MB (runs 1, 2, 3, 5); on/off UI-thread CPU deltas -258 / -156 / +70 / -48 ms (launch-to-launch noise is a few hundred ms); background load (Begin and End package load after the folder opened) | Run 7: timed Pillion work 9.1 ms in 12 stretches (Output pane, status bar, the IntelliCode check); upper bound 266 / 344 ms (runs 4 / 7); working set +20.4 MB | `PackageLoad_UsesLittleUiThreadCpu` (UI thread sampled every 10 ms from a background thread; generous 500 ms assertion, passed); extension-on (`PillionG1`) against extension-off (`PillionG1Off`) launches, two pairs | IDE TRX output line, `gate1-results.json` `data.cost` |
| Chat first token, cloud | p50 1.5 s, p95 3 s, including context assembly | **Pending product owner** | Local `qwen2.5-coder:7b` (information): first visible text p50 90 / 99 / 92 ms, p95 162 / 175 / 218 ms (5 messages a run) | Local p50 93 ms, p95 437 ms | Chat window: input, Enter, first reply text | `scenarios/chat-mentions-VS*.json` |
| Context assembly without retrieval | p50 150 ms, p95 400 ms | **Pass** | Slowest case (exact tiktoken, 128k window, 69,144 prompt tokens): p50 38 to 48 ms, p95 238 to 268 ms; calibrated estimate p50 5 to 7 ms | Same tests (they do not depend on Visual Studio) | `ContextAssemblyBenchmarkTests`, 40 runs after 3 warm-ups | `tests-unit/*.trx` |
| Inline chat preview, first line | p50 2 s, p95 5 s; Enter one edit and one undo step; Esc leaves nothing to undo | **Pass** | First line p50 352 / 370 / 348 ms, p95 928 / 827 / 1,080 ms; first text p50 198 / 229 / 206 ms (10 inline chat runs each: extract and `/doc`); buffer untouched after Esc 10 of 10; Enter and Esc tests passed | First line p50 272 ms, p95 1,289 ms; Enter and Esc tests passed | Inline chat on the real host (command, prompt, Enter, `ProposalChanged`); IDE integration tests | `scenarios/inline-chat-VS*.json` |
| Mentions and inspection | `@file`, `@selection`, `@errors` resolve; inspector before sending; Outbound entry for every request of every task class | **Pass** | Pins `Program.cs` (45 tokens), `Errors (1 errors, 0 warnings)`, `Selection`; inspector lists system prompt, three pins and the message (295 of 6,144 tokens); Outbound records for Chat, InlineEdit and Autocomplete; `Mentions_TypeAtPickAFileAndSend...` passed | Same pins, inspector and task classes (the fake-host mention test is 18.x only) | Chat window on the real host; `GetOutboundRecordsAsync` | `scenarios/chat-mentions-VS*.json` |
| Degradation | p95 over 1.5 s for five minutes: debounce 350 ms, single-line; recovers | **Pass** | New component test `AutocompleteRealHostTests.SlowHost_DegradesToTheLongerDebounceAndSingleLine_AndRecoversWhenTheSlowSamplesAge` (real pipe, fake host at 1.6 s a chunk: degraded after 20 slow suggestions, next request 350 ms and single-line, 200 ms and multi-line once the samples age out), plus the rule's unit tests | Same | Component test, no IDE (the brief's choice) | `tests-unit/*.trx` |
| Completion evaluation baseline | Golden set against the recommended local model | **Pending product owner** | No golden-set repository (1.17) | | | |
| Offline run | Every check above with the network disabled | **Pending product owner** | `Invoke-OfflineRun.ps1 -Phase 1` written; dry-run only | | Elevated, by the product owner | Dry-run output below |
| CI | Green on develop with the 18.x job and a 17.14 job | **Pass** | Latest develop runs green: https://github.com/ryanjkendrick/PillionProgrammer/actions/runs/38075756824 (`4845f06`) and https://github.com/ryanjkendrick/PillionProgrammer/actions/runs/38089244576 (`f6b6796`), both jobs success. The VS 2022 integration job's host-kill UI-thread stall was investigated in PR 40 (merged; the test now attributes gaps) | | `gh run list`, `gh run view` | `gate1-results.json` `data.ci` |
| Feature flags | Every work item behind a flag; tests green | **Pass** | 2,790 unit and component tests passed, 0 failed, every run; flags in the README. But see open issue 3: with `autocomplete` off the extension still sent requests on 18.x (fixed 2026-10-11: every request path passes the availability gate; unit tests `AutocompleteDisplayTests.Gate_*`; the off pass now asserts none) | | Test run; README | `tests-unit/*.trx` |


## What the product owner must do

1. **Cloud rows** (autocomplete on the Fast profile, chat first token). Add a cloud provider on **Tools > Options >
   Pillion Programmer > Providers and keys** and store its key (Windows Credential Manager, `PillionProgrammer/<id>`),
   then run `pwsh tools/gate1/Invoke-Gate1.ps1 -SkipTests -CloudProviderId <id>`. The script only asks
   `cmdkey /list:PillionProgrammer/<id>` whether the key exists. It routes autocomplete to the Fast profile with the
   provider's first model (and the autocomplete cloud switch on) for one typing pass and sends five chat messages to the
   same model (`Gate1_Cloud_FastAutocompleteAndChatFirstToken`; results in `ide-VS18/scenarios/autocomplete-cloud-VS18.json`
   and `cloud-VS18.json`, rows in `gate1-summary.md`). This path has not run here (no key), so its first run also checks
   the script.
2. **Offline run.** After a normal Gate 1 run (it installs the build in the Experimental Instance), from an elevated
   PowerShell 7 prompt in the repository root: `pwsh tools/gate0/Invoke-OfflineRun.ps1 -IAmReady -Phase 1`. It disables the
   adapters that are Up (here `Ethernet` and `vEthernet (Default Switch)`), runs the Gate 0 checks (local chat, host kill,
   Local-only), closes that instance, runs the integration tests and the Gate 1 scenarios with the network still off,
   and re-enables the adapters in a `finally` block; about 25 minutes, results in `tools/gate1/out/<timestamp>-offline/`.
   Dry run first with `-WhatIf` (no elevation, nothing changes; its output is below). If the window is closed mid-run:
   `Get-NetAdapter | Where-Object Status -eq Disabled | Enable-NetAdapter`.
3. **Decide what to do about the ghost-text content** (open issues 1 and 2) before judging the latency and cache rows.
4. **Golden-set repository** (work item 1.17): say where the private repository lives; the completion slice and its
   baseline follow.
5. **Ordering default**: confirm or change `autocomplete.ordering` = `pillionFirst` (decision log, 2026-10-10).
6. **Try ghost text and inline chat by hand for feel**, in a private hive so no test run disturbs it:
   `"C:\Program Files\Microsoft Visual Studio\18\Community\Common7\IDE\VSIXInstaller.exe" /rootSuffix:PillionManual1 src\Pillion.VisualStudio\bin\Release\net48\Pillion.VisualStudio.vsix`,
   `devenv /rootsuffix PillionManual1 /updateconfiguration`, then `devenv /rootsuffix PillionManual1`. With Ollama running
   and Local-fast set to `ollama/qwen2.5-coder:7b` on the Models and routing page, in a new C# console project:
   - Below `static void Main() {`, type `var numbers = new List<int> { 3, 1, 2 };`, Enter, and wait about half a second
     for grey text. **Tab** takes it, **Esc** drops it, **Ctrl+Right** takes one word, **Alt+]** and **Alt+[** switch
     between suggestions.
   - Type `numbers.` so the completion list opens with an item selected: no ghost text while it is.
   - Type `public static int Sum(IEnumerable<int> values)`, Enter, `{`, Enter: a multi-line body should be offered.
   - Select a method, **Ctrl+Alt+Shift+P, I**, type `extract the loop into a helper`, Enter: the difference view shows
     the proposal; Enter applies it (one Ctrl+Z undoes it), Esc leaves nothing to undo. Try `/doc` too.
   - In Pillion Chat type `@` and pin a file, `@errors` and `@selection`, press **Inspect**, then send.
   - **Tools > Pillion: Show Status** shows the autocomplete figures this report quotes.
   Repeat the ghost-text steps on Visual Studio 2022 with IntelliCode on, and once in a TypeScript and a Python file
   (no automated ghost-text test covers those two languages).
7. **Gate decision.** Record pass or fail in the decision log.

Dry run of the offline script (`pwsh tools/gate0/Invoke-OfflineRun.ps1 -IAmReady -Phase 1 -WhatIf`):

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
```

## Open issues

1. **Ghost text from `qwen2.5-coder:1.5b` is prose, not code** (bug, found by this run). In every fast-model pass, every
   Pillion proposal that did not match the next line began like "It looks like you're encountering an issue with the
   code in `TypeFast.cs` at line 40..." or "It looks like you're working with a C# code file named `WarmTypeFast.cs`...
   The recent edits indicate...". The model is answering the comment header the autocomplete prompt puts above the code
   (`// File: ...`, `// Open files: ...`, `// Recent edit: ...`, `AutocompletePromptBuilder.BuildHeader` in
   `src/Pillion.ModelGateway`). The Ollama tag `qwen2.5-coder:1.5b` is the instruct-tuned model. The live gateway tests
   do not see this because their requests carry no recent edits. Nothing was accepted in any pass. For the autocomplete
   owners: try the prompt without the edit lines (or the base model), and add a completion-shaping test that drops prose.
   **Fixed 2026-10-11** (`feature/autocomplete-quality`): the cause was the empty suffix (Ollama's template then makes
   the prompt a chat turn), not only the header. Live, on 20 prefixes: prose in 3 of 20 raw replies before, 0 after, on
   the 1.5B (5 and 0 on the 7B); the gateway also drops a reply that opens in prose. IDE count not yet re-measured.
2. **Ghost text from `qwen2.5-coder:7b` opens with a Markdown fence** (bug, found by this run): most non-matching
   proposals began with "```csharp". Neither the host nor `CompletionShaper` strips it. Together with issue 1 this is
   why no proposal was accepted, why the host's cache never served a type-through, and why the time to suggestion runs
   past the first chunk. **Fixed 2026-10-11**: same root cause (empty suffix); live, fences in 5 of 20 raw replies before
   and 0 after on the 1.5B (3 and 0 on the 7B); the gateway strips a leading fence and stops at a closing one, and the
   extension never shows a fence line.
3. **With `features.autocomplete` off, Visual Studio 2026 still sends completion requests** (bug, found by this run). The
   off passes on 18.10 recorded 21, 31 and 20 requests, all failing at the host (the flag denies them), matching the pauses
   where another provider's proposal (Copilot's) was displayed (21, 29 and 20 pauses); on 17.14, where IntelliCode showed
   nothing at those pauses, the off pass sent none. The coexistence path `ViewAutocomplete.RequestOutsideBroker`
   (`src/Pillion.VisualStudio/Autocomplete`) starts a session without the document suppression the broker path checks
   (`SuppressionRules.EvaluateDocument` returns `FeatureDisabled`). The README says the extension stops asking.
   **Fixed 2026-10-11**: `AutocompleteSession.Start`, the one door every path goes through, checks the flag and the
   disabled languages first and again after the debounce, and `RequestOutsideBroker` and the coexistence actions check the
   document first. Unit-tested; the IDE assertion has not run yet.
4. **The 7B p50 target and the engine's definition of "suggestion"**: the first chunk arrives at a p50 of 57 to 171 ms,
   but the engine records the suggestion when a line completes or 300 ms (`AutocompleteEngine.PartialGrace`) after the
   first chunk, so with this model the p50 lands at 420 to 560 ms. Whether the 300 ms budget means first visible text or
   first complete line is a design question for the lead and the product owner; with issue 2 fixed the number will move.
   **Decided and implemented 2026-10-11** (lead's decision): the first visible text counts; a single-line suggestion is
   shown at its first chunk and grows (at most one update per 50 ms), a multi-line one at its first complete line.
   The CI integration tests then showed that `TryUpdateProposal` does not change the displayed ghost text on 18.x or
   17.14 (a suggestion shown at "var total = Calculat" stayed there and Tab took it), so single-line suggestions are shown
   when their line is complete (`ShowSingleLineEarly` off); multi-line ones at their first complete line, with the same
   limit. Making the ghost text grow needs another mechanism (to be found).
5. **The cache hit rate row cannot be met the way the IDE asks.** With the 200 ms debounce, requests leave only at pauses,
   so consecutive requests rarely share a prefix the host has seen (2 to 3%). Typed-through text is served by the
   extension's own continuation cache and never reaches the host's counters. The live test's request-per-keystroke loop
   gives 94%. The row needs a definition that counts the extension's cache too, or a scenario that accepts suggestions
   (blocked by issues 1 and 2).
6. **VSIX UI-thread CPU is not shown under 50 ms.** After merging PR 40, Pillion's timed UI-thread stretches between the
   folder open and Ready add up to 2.6 ms (18.10) and 9.1 ms (17.14), but `PillionPackage.InitializeAsync`'s own section on
   the UI thread (after its `SwitchToMainThreadAsync`: services, commands, the status bar hookup) is not timed; the
   in-process window from "Begin package load" to Ready (78 to 344 ms) also holds the rest of Visual Studio's folder-open
   work, and the on/off launches are too noisy for 50 ms. Wrapping that section in `UiThreadTimings.Measure` would settle
   the row; this work item does not change product code.
7. **The CPU-tier autocomplete p50 misses** (about 1.0 s against 600 ms) for the 1.5B model with `num_gpu` 0, as expected
   on this CPU; the p95 passes. The IDE cannot reproduce it: an Ollama provider entry has no `num_gpu` setting.
8. **Flaky live tests** (not product failures): `OllamaResidencyLiveTests.ChatThatNeeds8192_ReloadsTheCoderOnce...`
   failed once with the live classes running side by side (the host loaded a second 8,192-token instance beside the pinned
   one rather than reloading; the test expects the reload), and passed run serially; `InlineEditLiveTests.Gemma_ExtractsTheValidation...`
   failed in the last two runs (17.14's and run 5) because `gemma4:e2b` returned code that does not parse (it passed in
   run 3); the `qwen2.5-coder:7b` inline-edit tests, which the product routes to, passed every time. The live tests also need an idle
   Ollama: the gate script unloads resident models first and runs the classes one at a time.
9. **Visual Studio 2022 host-kill stall** (investigated in PR 40, merged during this work). In these runs the host-kill
   test passed on both versions: longest UI gap 277 to 309 ms on 18.10 and 278 to 279 ms on 17.14, Ready again after about
   1.7 s.
10. **No persistence of conversations** until phase 2's Local Store (decision log, 2026-10-08).
11. Prompt tokens for the coder models are estimated, not counted (`Prompts: 0 counted with the tokenizer`); the live
    test shows the estimate about 12% above Ollama's count (2,045 against 1,827), which only errs on the safe side.
12. Two `PillionProgrammer/test-probe-*` credentials are left in the Windows Credential Manager, probably from an
    interrupted `SecretStoreTests` run (the probe deletes its credential in a `finally`).

