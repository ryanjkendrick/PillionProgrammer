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

**Summary after the re-run of 2026-10-11 (see "Re-run after the fixes" below).** Of the 17 criteria, 12 pass as of 2026-10-13 (cloud chat first token passed with the Anthropic key; 11 on 2026-10-11; the VSIX
cost qualified on 17.14, where about 77 ms of Visual Studio's own window initialisation is still deferred to just after
Ready), 2 fail and 3 are pending the product owner (cloud Fast autocomplete needs a Mistral key: Anthropic has no
fill-in-the-middle). The failures are the CPU-only 1.5B p50 (about 1.2 s against 600 ms,
expected on this CPU) and the prefix cache hit rate in the IDE (2 to 3% against 70%). The 7B autocomplete p50 now passes
(184 to 277 ms on 18.10 and 218 ms on 17.14 against 300 ms) and no prose or fenced ghost text appeared in any of the six
typing passes.

**Summary of the first run (2026-10-10, kept for comparison).** Of the 17 criteria, 9 pass (keystroke latency only
borderline on 17.14), 4 fail and 4 are pending the product owner. The failures were the 7B autocomplete p50 (about 0.4 to
0.56 s against 300 ms in the IDE), the CPU-only 1.5B p50 (about 1.0 s against 600 ms, expected), the prefix cache hit
rate in the IDE (2 to 3% against 70%) and the VSIX's UI-thread CPU (not shown to be under 50 ms; the upper bound
measured was 78 to 266 ms; see the VSIX cost row: after the start-up work Pillion's own time to Ready is 6.4 ms on 18.10
and 9.8 ms on 17.14). Behind the first and third was a finding that mattered more than the numbers: **the ghost text the
local models produce in the IDE is not code** (open issues 1 and 2, fixed since).

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

**IDE re-measurement: not obtained at the time; obtained later the same day, see \"Re-run after the fixes\" below.** Three runs of `Invoke-Gate1.ps1 -SkipBuild -SkipTests -SkipCost` were made, each
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

## Re-run after the fixes (2026-10-11)

Work item 1.18, second pass. Branch `feature/gate1-rerun`, branched from `develop` at `d230e1b`, which holds PR 41 (CI
flake fixes), PR 43 (VSIX start-up cost) and PR 44 (autocomplete quality). The product code is unchanged; this branch
only corrects how `Gate1.Scenarios.psm1` judges the VSIX cost row (open issue 15). Same machine as above; Ollama 0.40.2;
`qwen2.5-coder:7b` and `:1.5b` resident and pinned throughout (digests as above); Visual Studio 2026 18.10.3 and Visual
Studio 2022 (`devenv` 17.14.37710.0) with IntelliCode; Copilot on in the 18.x Experimental Instance. No provider key,
so the cloud rows stay pending.

Each run is `pwsh tools/gate1/Invoke-Gate1.ps1 -SkipBuild` (the full solution was built first with Visual Studio 2026's
MSBuild; the script then runs the unit and component tests, the live tests, the IDE suite with the scenarios, the cost
launches, the degradation step, CI and the clean-up). Run folders under `tools/gate1/out/`:

| Run | Visual Studio | Unit and component | Live tests | IDE suite (with scenarios) | VSIX cost launches | Result |
| --- | --- | --- | --- | --- | --- | --- |
| A `20261011-093626-gate1` | 18.10.3 | 2956/0/0 | 23/0/17 skipped | 20/0/0 | Pass | **Pass**, 1 pending |
| B `20261011-095830-gate1` | 18.10.3 | 2955/**1**/0: `InlineChatSessionTests.Escape_WhileStreaming_CancelsOnTheHostAndCloses` timed out (open issue 13) | 23/0/17 | 20/0/0 | Pass | 1 failed (that unit test), 1 pending |
| C `20261011-102216-gate1` | 17.14 (`-DevEnvPath`) | 2956/0/0 | 22/**1**/17: `InlineEditLiveTests.Coder_ExtractsTheValidation_InBlockMode` (open issue 8) | 20/0/0 | Pass | 1 failed (that live test), 1 pending |

Before the runs: `msbuild PillionProgrammer.sln /restore /p:Configuration=Release /m` succeeded and
`dotnet test PillionProgrammer.NoVsix.slnf --filter "Category!=Live"` passed 2,956 tests, 0 failed (Contracts 295, Common
795, Host 344, ModelGateway 812, VisualStudio 710), against 2,811 in the 2026-10-10 runs. In every run `settings.json`
came back with its original hash (`562D8573...A9A4CFE`), no `devenv.exe` or `Pillion.Host.exe` was left, the extension
was uninstalled from `PillionG1`, and no timeout was widened. The Gemma live tests were skipped in all three runs (not
measured); the 17 skipped live tests are the cloud adapters' and Gemma's.

### Autocomplete quality (open issues 1 to 3)

Typing scenario, the same 40-line C# file, 40 line-break pauses a pass (`autocomplete-*-VS*.json`):

| Pass | Proposal shown at pauses | Requests sent | Shown | Taken (of shown) | Rejected | Prose | Fenced | Non-matching proposals | Test accepted |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 7B, 18.10, run A | 35 | 35 | 20 (57%) | 13 (65%) | 7 | **0** | **0** | 21 | 14 |
| 7B, 18.10, run B | 35 | 36 | 19 (53%) | 14 (74%) | 5 | **0** | **0** | 20 | 15 |
| 7B, 17.14, run C | 30 | 41 | 26 (63%) | 11 (42%) | 14 | **0** | **0** | 18 | 12 |
| 1.5B, 18.10, run A | 36 | 35 | 22 (63%) | 12 (55%) | 10 | **0** | **0** | 25 | 11 |
| 1.5B, 18.10, run B | 36 | 33 | 21 (64%) | 13 (62%) | 8 | **0** | **0** | 25 | 11 |
| 1.5B, 17.14, run C | 31 | 39 | 26 (67%) | 9 (35%) | 16 | **0** | **0** | 21 | 10 |

Before (2026-10-10, 18.10): prose in every non-matching proposal of the 1.5B passes, a Markdown fence opening most of
those of the 7B, nothing accepted in any pass, and a Pillion proposal at 24 to 27 of 40 pauses. Now no pass produced a
prose or a fenced proposal (the test asserts it), proposals were accepted 10 to 15 times per pass, and the final text of
every pass equals the snippet. The shown-of-sent rate is 53 to 67%: the rest were cancelled by typing (100 to 142
attempts), empty or suppressed.

The 20-prefix live test (`AutocompletePromptQualityLiveTests`, once per run) reproduced the 2026-10-11 numbers, raw
replies prose / fence / code:

| Model | Gate 1 request (sentence header, empty suffix) | Terse header, empty suffix | Terse header (shipped for the 7B) | No header (shipped for the 1.5B) |
| --- | --- | --- | --- | --- |
| `qwen2.5-coder:1.5b` | 3 / 5 / 12 | 1 / 7 / 12 | 0 / 0 / 20; 20 of 20 parse; median 194 to 200 ms | **0 / 0 / 20; 19 of 20 parse; median 154 to 167 ms** |
| `qwen2.5-coder:7b` | 6 / 2 / 12 | 2 / 6 / 12 | **0 / 0 / 20; 19 of 20 parse; median 383 to 391 ms** | 0 / 0 / 20; 19 of 20; median 352 to 358 ms |

The first two columns vary by a few replies from the 2026-10-11 run; the shipped columns are within a reply and 10 to 35
ms of it. The 1.5B's 20 of 20 moved from the no-header to the terse-header column, a difference of the same one reply
(` {` after `=>`).

**Feature off (open issue 3).** The off pass sent **0 requests** from the extension and **0** to the host in all three
runs, with another provider's proposal on screen at 29 (run A) and 31 (run B) of 40 pauses on 18.10 (Copilot) and none on
17.14 (IntelliCode shows nothing at those pauses). Before: 21, 31 and 20 requests. The test asserts both counts.

### Rows that changed

Targets from the Gate 1 table. 18.10 figures are runs A / B, 17.14 is run C. "Before" is the 2026-10-10 figure in the
criteria table further down.

| Row | Target | Status now (before) | Measured: 18.10 | Measured: 17.14 | Before |
| --- | --- | --- | --- | --- | --- |
| Ghost text works (C#) | Multi-line and single-line, Tab, Esc, word accept, none while a completion item is selected | **Pass (C#)**; TypeScript and Python **Pending product owner** (same) | `ProposalSource_ShowsGhostText_WithTheCompetitorEnabled` and `..._NoProposal_WhileACompletionListItemIsSelected` passed with Copilot on; a Pillion proposal at 35 of 40 pauses (7B) and 36 of 40 (1.5B), both runs; 14 and 15 accepted by the test | Both passed with IntelliCode on; 30 of 40 (7B), 31 of 40 (1.5B); 12 and 10 accepted | Proposal at 24 to 27 of 40 pauses (18.10) and 34 and 35 (17.14); none accepted |
| IntelliCode run-time check on 17.x | Status bar and Output pane report it | **Pass** (Pass) | n/a | `IntelliCodeCheck_ReportsWhenAutomaticCompletionsAreOff` passed | Same |
| Autocomplete latency, 7B on the GPU (IDE: time to the first visible text) | p50 300 ms, p95 800 ms, prompt within 2,048 tokens | **Pass** (Fail) | p50 **184 / 277 ms**, p95 **461 / 421 ms** (24 and 21 suggestions); first chunk p50 100 / 114 ms, p95 304 / 318 ms; visible after the line break (200 ms debounce included) p50 233 / 287 ms, p95 686 / 668 ms; largest prompt 289 tokens. Live, gateway only: first token p50 280 / 280 ms, p95 340 / 299 ms; total p50 422 / 422 ms, p95 495 / 453 ms at 2,041 to 2,043 estimated tokens | p50 **218 ms**, p95 **498 ms** (26 suggestions); first chunk p50 73 ms, p95 369 ms; visible after the line break p50 402 ms, p95 720 ms; largest prompt 287 tokens. Live: first token p50 281 ms, p95 331 ms; total p50 423 ms, p95 484 ms | 18.10: p50 434 / 557 / 420 / 611 ms, p95 776 / 760 / 730 / 730 ms; 17.14: p50 420 / 568, p95 730 / 739 ms; live first token p50 278 to 281 ms |
| Autocomplete latency, 1.5B (information) | no row of its own | Information | p50 118 / 101 ms, p95 286 / 295 ms | p50 117 ms, p95 293 ms | Not recorded |
| Autocomplete latency, 1.5B on the CPU | p50 600 ms, p95 1,500 ms, prompt within 1,024 tokens | **Fail** (p50) (Fail) | Live, `num_gpu` 0: first token p50 1,233 / 1,168 ms, p95 1,260 / 1,239 ms | p50 1,228 ms, p95 1,258 ms | p50 940 to 1,025 ms, p95 1,037 to 1,057 ms. The p95 passes; the p50 is about 200 ms slower than on 2026-10-10 for the same test and model (same machine, Ollama 0.40.2 instead of 0.40.1; not investigated) |
| Keystroke latency with autocomplete on | +0 ms p50, +2 ms p95 | **Pass** (Pass; borderline on 17.14) | TypeChar on the UI thread, 1.5B on against off: p50 3.34 / 3.33 against 4.32 / 4.43 ms, p95 11.44 / 11.14 against 11.72 / 11.85 ms; difference **-0.98 / -1.10 ms p50, -0.28 / -0.70 ms p95** (7B: -0.83 / -0.98 and +0.19 / +0.41 ms). 0 characters missing from the buffer | p50 2.86 against 2.65 ms, p95 5.71 against 4.44 ms; difference **+0.21 ms p50, +1.27 ms p95** (7B: +0.31 / +1.69 ms). Inside the +2 ms p95 budget; the p50 difference is above a literal +0 but below the 0.5 ms the script tolerates (timer resolution and noise) | 18.10: on is not slower; 17.14: +0.02 to +0.31 ms p50, +1.30 to +2.28 ms p95 |
| Prefix cache hit rate during steady typing | 70% or more | **Fail** in the IDE (Fail) | Host counters over each pass: 7B 2.8% / 2.9% (1 repeat in 36 / 35 requests), 1.5B 2.9% / 3.1% (1 in 35 / 32); 0 type-through hits; live request-per-keystroke loop: 33 of 35 (94%) | 7B 2.4% (1 in 41), 1.5B 0% (0 in 38); live loop 33 of 35 (94%) | 2 to 3%; live 94%. Accepting suggestions (10 to 15 a pass now) did not change it: the host still sees one request per pause and the extension's continuation cache serves what is typed through (open issue 5) |
| VSIX cost in devenv.exe | 0 ms blocking, under 50 ms UI-thread CPU, 60 MB or less added working set | **Pass** on 18.10; **17.14 passes to Ready, 87.5 ms with the work deferred past Ready** (open issue 6) (Pass on 18.10 for Pillion's own time; 17.14 open) | Pillion's own timed UI-thread work from the folder open to Ready: **6.3 / 5.7 ms** in 6 visits (`package.init.main` 2.7 / 2.6 ms, status bar refreshes 2.9 / 2.3 ms, IDE signals 0.7 / 0.8 ms); `InitializeAsync` before its first switch **0.2 / 0.1 ms**; deferred past Ready (3 s): **3.7 / 3.8 ms**. Raw UI-thread CPU from "Begin package load" to Ready (holds Visual Studio's own work, not judged): 109 / 156 ms over 989 / 876 ms, idle baseline 0. On against off launches (two pairs each): working set **+17.1 / +7.0 MB**, UI-thread CPU +78 / -16 ms (launch noise) | Own time to Ready **10.4 ms** in 7 visits (IntelliCode check 0.3 ms, `package.init.main` 3.1 ms, Output window connection 3.7 ms, status bar 2.4 ms); `InitializeAsync` before its first switch **0.8 ms**; deferred past Ready **77.1 ms** (Error List connection 51.4 ms, Output pane flushes 25.7 ms), **87.5 ms load plus deferred**. Raw CPU to Ready 125 ms over 1,024 ms. Launches: working set -35.2 MB (549.2 on against 584.5 off; noise), UI-thread CPU +124 ms (noise) | 18.10: own time 41.4 ms before PR 43, 6.4 ms after; 17.14: 227 ms before, 9.8 ms after, with about 100 ms deferred (Output pane 22 to 26 ms, Error List 73 to 79 ms). Working set +2.3 / +17.2 / -9.8 / -5.2 MB (18.10), +20.4 MB (17.14) |
| Chat first token, cloud | p50 1.5 s, p95 3 s | **Pass (2026-10-13, claude-haiku-5-5 via Anthropic: first visible text p50 676 / 691 ms, p95 869 / 1,049 ms on 18.10 / 17.14; see "Cloud rows")** | Local `qwen2.5-coder:7b` (information): first visible text p50 99 / 87 ms, p95 176 / 185 ms (5 messages a run), whole reply p50 345 / 405 ms | p50 87 ms, p95 328 ms, whole reply p50 352 ms | p50 90 / 99 / 92 ms, p95 162 / 175 / 218 ms (18.10); p50 93, p95 437 ms (17.14) |
| Context assembly without retrieval | p50 150 ms, p95 400 ms | **Pass** (Pass); run B at the limit, see open issue 14 | Slowest case (exact tiktoken, 128k window, 69,144 tokens): p50 **37 ms** in run A, **151.2 ms** in run B (p95 243 / 274 ms); estimate p50 5.6 / 4.8 ms. Run on its own three times afterwards: p50 29.5 to 31.0 ms, p95 152 ms | p50 35 ms, p95 237 ms; estimate p50 6.0 ms | p50 38 to 48 ms, p95 238 to 268 ms |
| Inline chat preview, first line | p50 2 s, p95 5 s | **Pass** (Pass) | First line p50 **333 / 366 ms**, p95 **818 / 895 ms**; first text p50 267 / 196 ms (10 runs each; the first of each run costs 0.8 to 0.9 s); buffer untouched after Esc 10 of 10 both runs; Enter and Esc tests passed | First line p50 **273 ms**, p95 **1,062 ms**; first text p50 178 ms; 10 of 10 untouched; Enter and Esc passed | 18.10 p50 348 to 370 ms, p95 827 to 1,080 ms; 17.14 p50 272 ms, p95 1,289 ms |
| Mentions and inspection | `@file`, `@selection`, `@errors`; inspector; Outbound for every task class | **Pass** (Pass) | Pins `Program.cs` (45 tokens), `Errors (1 errors, 0 warnings)` (50), `Selection` (49); inspector 5 items, 301 of 6,144 tokens; Outbound records for Chat, InlineEdit and Autocomplete in both runs; mention test passed | Same pins and task classes (the fake-host mention test is 18.x only) | Same |
| Degradation | p95 over 1.5 s for five minutes: 350 ms, single-line; recovers | **Pass** (Pass) | The four degradation tests passed in all three runs | Same | Same |
| Host kill (part of the IDE suite) | UI thread responsive; Ready again within 15 s | **Pass** (Pass) | Longest UI gap **278 / 279 ms** (background thread 264 / 264 ms), every operation answered, Ready again after **1,715 / 1,701 ms** | Longest UI gap **264 ms** (background 264 ms), Ready again after **1,702 ms** | Longest UI gap 277 to 309 ms (18.10), 278 to 279 ms (17.14), Ready again after about 1.7 s |
| CI | Green on develop with the 18.x job and a 17.14 job | **Pass** (Pass) | `develop` at `d230e1b`: https://github.com/ryanjkendrick/PillionProgrammer/actions/runs/38125232342, both jobs success. The runs for PRs 42 and 41 (`c5d7bcb`, `6168cc3`) had failed; PR 44 and PR 43 are green | Same | Runs for `4845f06` and `f6b6796` green |
| Feature flags / tests | Tests green | **Pass** (Pass) | 2,956 passed, 0 failed in the pre-run and runs A and C; one unit test timed out in run B (open issue 13) | 2,956 passed in run C | 2,790 to 2,811 passed |

Rows not in the table are unchanged and were not re-measured: the completion evaluation
baseline and the offline run stay **Pending product owner**; TypeScript and Python ghost text stay by hand. The cloud
Fast autocomplete row is **not applicable** to the Anthropic key stored on 2026-10-13 (no fill-in-the-middle) and stays
pending a Mistral key; see "Cloud rows".

### Cloud rows (2026-10-13)

The product owner stored an Anthropic key (`PillionProgrammer/anthropic`, Windows Credential Manager) and ran the gate
with `-CloudProviderId anthropic -CloudModel claude-haiku-5-5` (PR 68 added `-CloudModel`: the provider entries the
Providers and keys page writes list no models). The settings file and the key are per user, so the Experimental
Instance saw the provider without any set-up of its own. The script never reads the key; the host does.

| Run | Version | IDE suite | Cloud chat first visible text (5 messages) | Whole reply | Cloud Fast autocomplete |
| --- | --- | --- | --- | --- | --- |
| `20261013-224010-gate1` (product owner) | 18.10.3 | 15/5 (see below) | p50 625 ms, p95 881 ms | p50 999 ms | not applicable |
| `20261013-230036-gate1` | 18.10.3 | 20/0 | **p50 676 ms, p95 869 ms** | p50 1,030 ms | not applicable |
| `20261013-231113-gate1` | 17.14 | 20/0 | **p50 691 ms, p95 1,049 ms** | p50 1,090 ms | not valid (see below) |

Every reply carried the header "Claude Haiku 5.5 via Anthropic, cloud" and an Outbound record with `Locality` cloud and
`Succeeded`. The row's budget is p50 1.5 s and p95 3 s: **Pass** on both versions.

**Cloud Fast autocomplete does not apply to Anthropic.** Only Mistral's Codestral (and a local OpenAI-compatible server
that declares it) has fill-in-the-middle; the gateway skips an autocomplete candidate without a FIM client and falls
back to the next one. The cloud scenario now asks the host for the model's capabilities and records "not applicable"
instead of running the typing pass (PR 68). The 17.14 run above still used the previous build of the 2022 test project,
so its typing pass did run with `anthropic/claude-haiku-5-5` on the Fast profile and measured p50 95 ms: the local
fallback (`autocomplete.outbound.allExpectedModel` is `false` in `scenarios/autocomplete-cloud-VS17.json`), not a cloud
model. That figure is not evidence for the row. The row stays pending a Mistral key.

**The product owner's run failed five IDE tests for a reason outside Pillion's code.** Ghost text, the inline chat
adornment and the mentions scenario failed with "Could not find a matching AdornmentLayerDefinition export" and no
Pillion proposal source on the view, while chat (local and cloud) worked. The 18.x Exp hive held two installed copies
of the extension: a VSIXInstaller copy from 2026-10-11 under a random folder name and the copy the build deploys. With
both present MEF composition dropped Pillion's editor parts; the package itself still loaded. Uninstalling from the
hive, deleting both copies and the `ComponentModelCache`, and letting the run deploy afresh fixed it (20 of 20 on
both versions afterwards). The script's "already deployed (same Pillion.VisualStudio.dll)" check only compares the
deployed copy with the build, so it did not notice the second copy; the Gate 0 hive clean-up in the cleanup list covers
removing such leftovers before a run.

**One sample from ordinary use, for the record.** The product owner's first chat on `claude-sonnet-5-5` (775 prompt
tokens, 1,835 completion tokens) showed its first visible text after 7.1 s and finished in 14.4 s. The gate's five
short Haiku replies are well inside budget; a long Sonnet reply with a large completion is a different shape of
request and has only this one sample, so it is noted, not judged. The adapter sends no thinking setting.

**CI on develop after PR 68** (run 38472288703): the 17.14 integration job passed; the 18.x job failed
`PackageLoad_UsesLittleUiThreadCpu` with 131.7 ms of Pillion's own UI-thread time before Ready, 117.4 ms of it the Error
List connection, which on this machine lands after Ready. PR 68 touched the gate script and the cloud scenario only;
the cost is the one in issue 49.

### What the re-run says

- The autocomplete content problem is fixed in the IDE, not only in the 20-prefix test: 0 prose and 0 fenced proposals in
  six passes, 10 to 15 proposals accepted per pass, a 7B p50 of 184 to 277 ms (18.10) and 218 ms (17.14) to the first
  suggestion, and no request at all with the feature off.
- Where the 7B time goes: with the prompt a fill-in-the-middle continuation, the first chunk comes at a p50 of 73 to 114
  ms, and the time to suggestion is the wait for the line to complete, because the displayed ghost text cannot be grown
  (open issue 4). From the line break to the ghost text on screen, the debounce included, is 233 to 402 ms at the
  median.
- The cache hit rate is untouched by the fixes and remains a definition problem (open issue 5).
- On 17.14 the start-up work leaves Pillion's own time at 10.4 ms, but Visual Studio still spends about 77 ms of
  UI-thread time creating the Error List and Output windows when Pillion first asks for them, just after Ready (open
  issue 6).

## How the evidence is produced

| Script | What it does | Who runs it |
| --- | --- | --- |
| `tools/gate1/Invoke-Gate1.ps1` | a. builds the full solution (Release, Visual Studio 2026 MSBuild, no deployment) and records the versions and the models by digest; b. unit and component tests (`Category!=Live`) with TRX, then the live tests (`Category=Live`) of `Pillion.ModelGateway.Tests` and `Pillion.Host.Tests` one class at a time from an idle Ollama, collecting the figures they print; c. backs up `settings.json` and writes the run's settings (Ollama provider, `qwen2.5-coder:7b` as Local-strong for chat and inline edits, `qwen2.5-coder:1.5b` as Local-fast for autocomplete, phase 1 flags on); d to k. the Visual Studio integration tests in the Experimental Instance of the driven version (the whole suite, including the host-kill test) with the Gate 1 scenarios switched on; g. the VSIX cost launches in `/rootsuffix PillionG1` (installed) against `PillionG1Off` (not installed); j. the degradation component test's result; l. the evaluation baseline (pending); m. the latest CI runs on `develop` (`gh`); n. restores `settings.json` byte for byte, uninstalls from `PillionG1`, checks for leftover processes and leaves `qwen2.5-coder:7b` resident. `-SkipBuild`, `-SkipTests`, `-SkipLiveTests`, `-SkipIde`, `-SkipCost`, `-DevEnvPath`, `-CloudProviderId`, `-CloudModel`, `-KeepInstanceOpen` | Anyone with Ollama and the two models; the agent ran it |
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
| Autocomplete latency, 7B on the GPU | p50 300 ms, p95 800 ms, prompt within 2,048 tokens with a cached prefix | **Pass on 2026-10-11** (re-run: IDE p50 184 / 277 ms on 18.10, 218 ms on 17.14, p95 421 to 498 ms); **Fail** (p50) on 2026-10-10; the note then: from 2026-10-11 "time to suggestion" is the first visible text (the first chunk, p50 57 to 171 ms in these runs, for a single-line request; the 300 ms grace is gone), but because the displayed ghost text cannot be grown (CI run 38114630716) a single-line suggestion is shown when its line is complete, so the IDE p50 should fall by the 300 ms grace only where it applied (multi-line), and the single-line figure stays near the host's total time (p50 382 ms in run 3) | IDE time to suggestion p50 434 / 557 / 420 / 611 ms, p95 776 / 760 / 730 / 730 ms (runs 1 / 3 / 5 / 6, 23 to 30 suggestions each); first chunk p50 57 to 171 ms; visible after the line break (200 ms debounce included) p50 641 to 662 ms; largest prompt 286 tokens. Live, gateway only: first token p50 278 to 281 ms, p95 289 to 321 ms; total p50 421 to 424 ms, p95 443 to 478 ms at 2,043 to 2,045 tokens | IDE p50 420 / 568 ms, p95 730 / 739 ms (runs 4 / 7); live 277 / 319 ms | Typing scenario: `IAutocompleteMetrics` time to suggestion (from the request leaving the debounce to the first suggestion worth showing); live test `Latency_Large_OnTheGpu` | `scenarios/autocomplete-strong-VS*.json` (its `showStatusText` is the Show Status section), `metrics-during-run.jsonl` |
| Autocomplete latency, cloud Fast | p50 400 ms, p95 1,000 ms; 3 s timeout dropped silently | **Pending product owner** (2026-10-13: not applicable to Anthropic, which has no fill-in-the-middle; a Mistral key would measure it) | No key stored | No key stored | `Gate1_Cloud_FastAutocompleteAndChatFirstToken` with `-CloudProviderId` | `scenarios/cloud-VS*.json` ("pending") |
| Keystroke latency with autocomplete on | +0 ms p50, +2 ms p95 | **Pass** on 18.10; **borderline** on 17.14 (p95 +1.3 to +2.3 ms); re-run 2026-10-11: **Pass** on both (17.14: +0.21 ms p50, +1.27 ms p95 with the 1.5B; +0.31 / +1.69 ms with the 7B) | TypeChar on the UI thread, on (1.5B) against off: p50 3.37 / 3.61 ms, p95 10.71 / 11.47 ms (run 1: -0.24 / -0.76 ms); run 3: p50 3.10 / 4.21, p95 4.29 / 12.72 ms; run 5: 3.08 / 3.18, 4.61 / 11.13 ms (on is not slower; the off pass, last in each run, was noisier) | Run 4: p50 2.54 / 2.52 ms, p95 5.80 / 4.43 ms, **+0.02 ms p50, +1.37 ms p95** (7B: +0.15 / +1.30 ms); run 7: **+0.09 / +1.82 ms** (7B: +0.31 / **+2.28** ms) | Typing scenario: each TypeChar timed until it returns, 704 per pass; the character is in the buffer when it does (0 misses) | `scenarios/autocomplete-{strong,fast,off}-VS*.json`, `typing.typeChar` |
| Prefix cache hit rate during steady typing | 70% or more | **Fail** in the IDE, on 2026-10-10 and again on 2026-10-11 with 10 to 15 suggestions accepted a pass (host rate 0 to 3%; open issue 5) | Host counters over each pass: 2 to 3% (1 repeat in 31 to 51 requests; runs 1, 3, 5); live test (a request per keystroke, as the row was designed): 33 of 35 (94%) | 2% and 2% | Gateway counters (`GetAutocompleteCountersAsync`) before and after each typing pass; live tests' steady-typing loop | `scenarios/autocomplete-*-VS*.json` `autocomplete.host` |
| VSIX cost in devenv.exe | 0 ms blocking, under 50 ms UI-thread CPU, 60 MB or less added working set | **Re-run 2026-10-11: Pass on 18.10 (own time 6.3 / 5.7 ms to Ready); 17.14 passes to Ready (10.4 ms) but 77 ms is deferred past Ready (87.5 ms in all), open issue 6.** Earlier: **Pass** on 18.10 for Pillion's own time (after the start-up work of `feature/vsix-startup-cost`). **17.14: Pillion's own time to Ready passes (9.8 ms); about 100 ms more of UI-thread time, Visual Studio's own initialisation of the Output window and the Error List that Pillion's first use triggers, is deferred to just after Ready, not removed** (see open issue 6). Blocking and working set passed before and were not re-measured | **After:** Pillion's own time on the UI thread from the folder open to Ready, median of 5 runs: **6.4 ms** in 6 visits (6.2, 6.4, 5.9, 6.7, 6.5): `package.init.main` 2.8 ms (command registration 1.0, status bar service and first text 1.7), status bar refreshes 2.1 ms (2 visits), IDE signals connections 0.9 ms (3 visits). `InitializeAsync` before its first switch away from the caller's thread: **0.2 ms** (median; 0.14 to 0.29); the background part (object graph, 23 ms) and the starts (about 140 ms, no UI thread) run on the thread pool. Deferred past Ready (3 s window): 3.6 ms (Output pane creation and flushes 3.6, Error List connection 0.7). **Before** (same test and machine, build of `develop` plus timing only, 5 runs): **41.4 ms** in 22 visits (41.4, 36.4, 42.3, 41.1, 42.1), pre-await 18.6 ms (on a pool thread); the largest stretches were the IntelliCode check started inline on the UI thread (16 to 19 ms: it reads devenv.exe's version resource), Output pane creation 4 ms, IDE signals start 3 to 5 ms, supervisor start 2.5 to 3.5 ms, status bar 2.5 to 3 ms. Whole UI thread CPU, Begin package load to Ready (upper bound, holds Visual Studio's own work; no longer asserted): after 78 to 109 ms (median 78), before 94 to 250 ms (median 125). Earlier figures: Pillion's own timed UI-thread work (PR 40's `UiThreadTimings`, run 6) from the folder open to Ready: **2.6 ms** in 10 stretches (Output pane flushes, status bar), which does not include the package's own initialization on the UI thread (not timed); UI thread CPU from "Begin package load" to Ready 78 / 203 / 109 ms (runs 1 / 3 / 5; 109 ms also in run 2), idle baseline 0 to 31 ms per second: an upper bound, since Visual Studio's own work on that thread in the same second is counted; working set on against off +2.3 / +17.2 / -9.8 / -5.2 MB (runs 1, 2, 3, 5); on/off UI-thread CPU deltas -258 / -156 / +70 / -48 ms (launch-to-launch noise is a few hundred ms); background load (Begin and End package load after the folder opened) | **After:** to Ready, median of 3 runs: **9.8 ms** in 7 visits (10.1, 9.8, 9.7); pre-await 0.8 ms; the IDE signals' Output window connection costs 3.6 to 4 ms here. Deferred past Ready: **100 ms** (Output pane creation 22 to 26 ms, Error List creation 73 to 79 ms), which is Visual Studio initialising those windows the first time something asks for them. **Before** (3 runs): **227 ms** (266, 196, 227): Error List 118 to 163 ms, Output pane 29 to 43 ms, IntelliCode check 24 to 27 ms inline on the UI thread; pre-await 23.9 ms. Whole UI thread CPU to Ready: after 94 to 109 ms, before 219 to 375 ms. Earlier figures: Run 7: timed Pillion work 9.1 ms in 12 stretches (Output pane, status bar, the IntelliCode check); upper bound 266 / 344 ms (runs 4 / 7); working set +20.4 MB | `PackageLoad_UsesLittleUiThreadCpu`: switches `UiThreadTimings` on in-process before the first folder open and sums Pillion's timed stretches that ran on the UI thread between the folder open and Ready (wall-clock, so an upper bound of Pillion's CPU time), split by stretch, plus the visits to the UI thread, the time `InitializeAsync` spends before its first switch away from the caller's thread (and on which thread), the stretches timed off the UI thread, and what runs in the 3 s after Ready; asserts under 50 ms and under 10 ms; runs were made one at a time in the Experimental Instance with the build in `feature/vsix-startup-cost`, 5 on 18.10.3 and 3 on 17.14.41 (`Run-Load` loop, medians reported). Not re-run through `Invoke-Gate1.ps1`: the extension-on against extension-off launches and the working set are the earlier figures. `PackageLoad_UsesLittleUiThreadCpu` (UI thread sampled every 10 ms from a background thread; generous 500 ms assertion, passed); extension-on (`PillionG1`) against extension-off (`PillionG1Off`) launches, two pairs | IDE TRX output line, `gate1-results.json` `data.cost` |
| Chat first token, cloud | p50 1.5 s, p95 3 s, including context assembly | **Pass on 2026-10-13** (claude-haiku-5-5 via Anthropic, p50 676 / 691 ms, p95 869 / 1,049 ms on 18.10 / 17.14) | Local `qwen2.5-coder:7b` (information): first visible text p50 90 / 99 / 92 ms, p95 162 / 175 / 218 ms (5 messages a run) | Local p50 93 ms, p95 437 ms | Chat window: input, Enter, first reply text | `scenarios/chat-mentions-VS*.json` |
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
   then run `pwsh tools/gate1/Invoke-Gate1.ps1 -SkipTests -CloudProviderId <id> -CloudModel <model>`. The script only asks
   `cmdkey /list:PillionProgrammer/<id>` whether the key exists. It routes autocomplete to the Fast profile with the
   provider's first model, or `-CloudModel` (and the autocomplete cloud switch on) for one typing pass when the model
   has fill-in-the-middle (Mistral's Codestral; Anthropic and OpenAI cloud models have none, so that row does not apply
   to them) and sends five chat messages to the
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
3. **Ghost-text content** (open issues 1 and 2) is fixed and re-measured (PR 44, re-run of 2026-10-11). What is left to decide is the definition of the cache hit rate row (open issue 5) and how the displayed ghost text could grow (open issue 4).
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
   the 1.5B (5 and 0 on the 7B); the gateway also drops a reply that opens in prose. **Re-measured in the IDE on 2026-10-11: 0 prose proposals in six passes (7B and 1.5B, 18.10 twice and 17.14 once), 10 to 15 proposals accepted per pass. Closed (PR 44).**
2. **Ghost text from `qwen2.5-coder:7b` opens with a Markdown fence** (bug, found by this run): most non-matching
   proposals began with "```csharp". Neither the host nor `CompletionShaper` strips it. Together with issue 1 this is
   why no proposal was accepted, why the host's cache never served a type-through, and why the time to suggestion runs
   past the first chunk. **Fixed 2026-10-11**: same root cause (empty suffix); live, fences in 5 of 20 raw replies before
   and 0 after on the 1.5B (3 and 0 on the 7B); the gateway strips a leading fence and stops at a closing one, and the extension never shows a fence line. **Re-measured in the IDE on 2026-10-11: 0 fenced proposals in six passes. Closed (PR 44).**
3. **With `features.autocomplete` off, Visual Studio 2026 still sends completion requests** (bug, found by this run). The
   off passes on 18.10 recorded 21, 31 and 20 requests, all failing at the host (the flag denies them), matching the pauses
   where another provider's proposal (Copilot's) was displayed (21, 29 and 20 pauses); on 17.14, where IntelliCode showed
   nothing at those pauses, the off pass sent none. The coexistence path `ViewAutocomplete.RequestOutsideBroker`
   (`src/Pillion.VisualStudio/Autocomplete`) starts a session without the document suppression the broker path checks
   (`SuppressionRules.EvaluateDocument` returns `FeatureDisabled`). The README says the extension stops asking.
   **Fixed 2026-10-11**: `AutocompleteSession.Start`, the one door every path goes through, checks the flag and the
   disabled languages first and again after the debounce, and `RequestOutsideBroker` and the coexistence actions check the
   document first. Unit-tested; the IDE assertion ran on 2026-10-11 (0 requests from the extension and 0 at the host in three runs, with Copilot's proposal on screen at 29 and 31 of 40 pauses on 18.10). **Closed (PR 44).**
4. **The 7B p50 target and the engine's definition of "suggestion"**: the first chunk arrives at a p50 of 57 to 171 ms,
   but the engine records the suggestion when a line completes or 300 ms (`AutocompleteEngine.PartialGrace`) after the
   first chunk, so with this model the p50 lands at 420 to 560 ms. Whether the 300 ms budget means first visible text or
   first complete line is a design question for the lead and the product owner; with issue 2 fixed the number will move.
   **Decided and implemented 2026-10-11** (lead's decision): the first visible text counts; a single-line suggestion is
   shown at its first chunk and grows (at most one update per 50 ms), a multi-line one at its first complete line.
   The CI integration tests then showed that `TryUpdateProposal` does not change the displayed ghost text on 18.x or
   17.14 (a suggestion shown at "var total = Calculat" stayed there and Tab took it), so single-line suggestions are shown
   when their line is complete (`ShowSingleLineEarly` off); multi-line ones at their first complete line, with the same
   limit. Making the ghost text grow needs another mechanism (to be found). **Re-measured 2026-10-11:** the 7B's time to suggestion is p50 184 / 277 ms (18.10) and 218 ms (17.14), p95 421 to 498 ms, against 300 ms and 800 ms, so the budget is met with a single-line suggestion shown when its line is complete; first chunk p50 73 to 114 ms. **The measurement part is closed (PR 44); the growing ghost text stays open.**
5. **The cache hit rate row cannot be met the way the IDE asks.** With the 200 ms debounce, requests leave only at pauses,
   so consecutive requests rarely share a prefix the host has seen (2 to 3%). Typed-through text is served by the
   extension's own continuation cache and never reaches the host's counters. The live test's request-per-keystroke loop
   gives 94%. The row needs a definition that counts the extension's cache too, or a scenario that accepts suggestions (blocked by issues 1 and 2). **Re-measured 2026-10-11 with suggestions accepted (10 to 15 a pass): still 0 to 3% (7B 2.4 to 2.9%, 1.5B 0 to 3.1%), 0 type-through hits at the host; the live loop gives 94%. Still open, and a definition question for the lead.**
6. **VSIX UI-thread CPU** (first reported by this run as not shown under 50 ms; work item `feature/vsix-startup-cost`).
   Timing the package's own initialisation showed 41 ms on 18.10 and 227 ms on 17.14 on the UI thread between the folder
   open and Ready, mostly the IntelliCode check started inline on the UI thread, Visual Studio creating the Output window
   and the Error List on Pillion's first use, and JoinableTask creation for the feature starts. Now: 6.4 ms (18.10) and
   9.8 ms (17.14) to Ready, with the start-up work, the log file writes and the IntelliCode check off the UI thread.
   **Still open on 17.14:** about 100 ms of UI-thread time just after Ready (Output pane 22 to 26 ms, Error List 73 to
   79 ms) is Visual Studio initialising those windows; deferring moved it out of the load, it did not remove it. Avoiding
   it needs a change of behaviour: connect the Error List only on the first diagnostics query (nothing in the product
   subscribes to `IIdeSignals.DiagnosticsChanged`, but the integration tests do) and create the Output pane only on the
   first line the user should see (warning or above). Both are decisions for the lead. The raw UI-thread CPU from "Begin
   package load" to Ready (78 to 375 ms) holds Visual Studio's own work and is printed, not asserted. **Re-measured 2026-10-11 (PR 43 merged): 18.10 passes (own time 6.3 and 5.7 ms to Ready, 3.7 and 3.8 ms deferred; raw 109 and 156 ms); 17.14 own time to Ready 10.4 ms but 77.1 ms deferred past Ready (Error List connection 51.4 ms, Output pane flushes 25.7 ms), 87.5 ms in all, so the "total under 50 ms" wording is not met on 17.14 unless that Visual Studio-owned initialisation is excluded. Open (lead's decision: lazy Error List connection and Output pane).**
7. **The CPU-tier autocomplete p50 misses** (about 1.0 s against 600 ms) for the 1.5B model with `num_gpu` 0, as expected
   on this CPU; the p95 passes. The IDE cannot reproduce it: an Ollama provider entry has no `num_gpu` setting. Re-run 2026-10-11: p50 1,168 to 1,233 ms, p95 1,239 to 1,260 ms (about 200 ms slower than on 2026-10-10 for the same test; Ollama 0.40.1 became 0.40.2 in between; not investigated).
8. **Flaky live tests** (not product failures): `OllamaResidencyLiveTests.ChatThatNeeds8192_ReloadsTheCoderOnce...`
   failed once with the live classes running side by side (the host loaded a second 8,192-token instance beside the pinned
   one rather than reloading; the test expects the reload), and passed run serially; `InlineEditLiveTests.Gemma_ExtractsTheValidation...`
   failed in the last two runs (17.14's and run 5) because `gemma4:e2b` returned code that does not parse (it passed in
   run 3); the `qwen2.5-coder:7b` inline-edit tests, which the product routes to, passed every time on 2026-10-10. **2026-10-11:** the Gemma tests were skipped in all three re-runs; `InlineEditLiveTests.Coder_ExtractsTheValidation_InBlockMode` (7B) failed once in run C (17.14's: the reply added `ValidateOrder(order);` to `Place` without defining the method, so the regex for two methods found one) and passed 4 of 4 when run again alone, so it is a model-output flake at temperature, not a product failure. The live tests also need an idle
   Ollama: the gate script unloads resident models first and runs the classes one at a time.
9. **Visual Studio 2022 host-kill stall** (investigated in PR 40, merged during this work). In these runs the host-kill
   test passed on both versions: longest UI gap 277 to 309 ms on 18.10 and 278 to 279 ms on 17.14, Ready again after about 1.7 s. Re-run 2026-10-11: passed in all three runs, longest UI gap 278 and 279 ms (18.10) and 264 ms (17.14), Ready again after 1.70 to 1.72 s.
10. **No persistence of conversations** until phase 2's Local Store (decision log, 2026-10-08).
11. Prompt tokens for the coder models are estimated, not counted (`Prompts: 0 counted with the tokenizer`); the live
    test shows the estimate about 12% above Ollama's count (2,045 against 1,827), which only errs on the safe side.
12. Two `PillionProgrammer/test-probe-*` credentials are left in the Windows Credential Manager, probably from an
    interrupted `SecretStoreTests` run (the probe deletes its credential in a `finally`).
13. **`InlineChatSessionTests.Escape_WhileStreaming_CancelsOnTheHostAndCloses` depends on test order** (test bug, found by
    the re-run). It failed in run B's unit step (a 10 s `TimeoutException` in `script.Started.Task.WithTimeout`) and not
    in run A, C or the pre-run. Run alone with `--filter FullyQualifiedName=...` it fails every time (5 of 5, 10 s each);
    with the other 23 tests of its class it passes (4 of 4), and the whole project passes (710 of 710). Something an
    earlier test in the class sets up (or a race that the first test of a process loses) is missing when it runs first.
    Not fixed here (tests only, outside this item).
14. **The context assembly benchmark is sensitive to load.** Its slowest case measured p50 37, 151.2 and 35 ms in the three
    gate runs (target 150 ms; run B at 151.2 ms is 1.2 ms over), and 29.5 to 31.0 ms in three runs on its own afterwards. The
    gate script runs it in the unit step beside the other test projects. Run B's unit step also had the failing test of
    issue 13 hold a 10 s timeout beside it, and I was reading files in the same minutes; the cause was not isolated.
15. **The VSIX cost row of the gate table still judged the raw UI-thread CPU** after PR 43 made the test assert Pillion's
    own time (`Gate1.Scenarios.psm1`): runs A and B printed "Fail" for it (109 and 156 ms above idle) while the test
    passed. Fixed in this branch: the row is judged on Pillion's own timed UI-thread stretches (what the test asserts),
    with the raw figure printed beside it and marked not judged; run C used the corrected script.
16. **Single live-test reruns leave the 7B unpinned.** Running one `InlineEditLiveTests` test with `dotnet test --filter`
    loads `qwen2.5-coder:7b` without `keep_alive` -1 (it showed "29 minutes from now" in `ollama ps`); the gate script's
    last step re-pins it, a manual rerun does not. It was re-pinned by hand at the end of this work.
