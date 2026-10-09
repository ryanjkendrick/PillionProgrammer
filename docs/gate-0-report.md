# Gate 0 report

Phase 0 closes at Gate 0 ([phase-0-plan.md](phase-0-plan.md)). This report collects the evidence for each criterion,
says how it was measured and lists what only the product owner can do. Work item 0.13.

Date: 2026-10-08. Machine: the reference development machine (design document section 17). Code: branch
`phase0/gate0-evidence`, which is `phase0/integration` (PR 8, all of phase 0) plus the scripts in `tools/gate0/`. The two Gate 0
failures (CI, feature flags) and the duplicate picker entries were fixed afterwards in PR 15 (`phase0/gate0-fixes`); the rows
below say where they changed.

## How the evidence is produced

Everything below the build is repeatable with two scripts (PowerShell 7.5 or later, no extra modules), run from the
repository root:

| Script | What it does | Who runs it |
| --- | --- | --- |
| `tools/gate0/Invoke-Gate0.ps1` | a. builds the full solution (Release, Visual Studio 2026 MSBuild) and deploys the VSIX to the Experimental Instance (with `-DevEnvPath`, to that Visual Studio's Experimental Instance through its own `VSIXInstaller.exe`, for example Visual Studio 2022), recording the VSIX manifest version, `Pillion.Host.exe --version` and the Ollama version; b. `dotnet test PillionProgrammer.NoVsix.slnf` with TRX files and per-project counts; c. backs up `settings.json`; d. launches `devenv /rootsuffix Exp` on a scratch folder; e. local chat check; f. host-kill check; g. Local-only check; h. cloud chat check (only with `-CloudProviderId` and a stored key); i. closes the instance, checks that no `Pillion.Host.exe` survives and restores `settings.json` byte for byte | Anyone; the agents ran it |
| `tools/gate0/Invoke-OfflineRun.ps1` | Steps d to g with every network adapter that is Up disabled (design section 15, Offline run); expects the status to read Offline; re-enables the adapters in a `finally` block | The product owner, from an elevated prompt with `-IAmReady` |
| `tools/gate0/Gate0.Checks.psm1` | The shared checks and helpers both scripts use | |

Each run writes `tools/gate0/out/<timestamp>-gate0/` (git-ignored): `run.log`, `gate0-results.json`,
`gate0-summary.md`, `build.log`, `test.log`, the TRX files under `tests/`, `host-log-excerpt.txt`,
`vsix-log-excerpt.txt`, the settings backup and window screenshots under `screenshots/`.

How the scripts drive Visual Studio: Windows UI Automation (`System.Windows.Automation`), rooted at the main window of
the `devenv.exe` the script launched; no other window or process is touched. Menu commands go through the real menu
(**Tools > Pillion: Open Chat**, **Tools > Pillion: Retry**). The chat window is read through its automation names:
the status line ("Pillion Host status: ..."), the model picker entries (which carry the unavailability reason), the
transcript (header text and segment text) and the Send and Stop buttons. The Ollama provider is set up by writing
`settings.json` before launch rather than by driving the Options page: it is one atomic file write instead of a dozen
UI steps in a modal dialog, and the Options page is only a view over the same file. Local-only is switched the same
way (`privacy.localOnly` in `settings.json`, which Pillion Host's file watcher reloads), followed by **Refresh models**
in the chat window. When no cloud provider is configured, the run adds a key-less placeholder (`gate0-cloud`,
OpenAI-compatible, `https://api.openai.com/v1`, model `gpt-4o-mini`) so the picker has a cloud entry to grey out and
the connectivity monitor has an endpoint to probe. It gets only the connectivity probe (`GET /` on
`https://api.openai.com`, no credentials) and never a chat request, since it has no key. The original `settings.json`
comes back at the end. Pillion Host's connectivity monitor logs at Debug for the launched instance only (an
environment variable passed to `devenv.exe`), so probe rounds appear in the host log and "no probes in Local-only"
can be checked.

## Runs

Five runs of `Invoke-Gate0.ps1` were made while the scripts were written; the last two are clean end-to-end runs with
build, tests and every check. Earlier runs found bugs in the script, not in the product: an empty UI Automation
result read under strict mode, a log timestamp converted through the current culture, and log files chosen by `LastWriteTime`, which NTFS does not update while the host
keeps its log open. Both are fixed. No check needed a longer timeout.

| Run folder (`tools/gate0/out/`) | Build and tests | Local chat | Host kill (longest gap; Ready after kill 1 / kill 2; Retry) | Local-only | Result |
| --- | --- | --- | --- | --- | --- |
| `20261008-174111-gate0` | skipped | not reached: launch step stopped on a script bug (strict mode) | | | 1 failed (script) |
| `20261008-174228-gate0` | skipped | script bug (log file selection) | Pass (275 ms; 1854 / 5728 ms; 933 ms) | Pass | 1 failed (script) |
| `20261008-174556-gate0` | Pass, 1400/0/3 | script bug (same) | Pass | Pass | 1 failed (script) |
| `20261008-175013-gate0` | Pass, 1400/0/3 | Pass | Pass (269 ms; 1798 / 5703 ms; 888 ms) | Pass | **Pass**, cloud pending |
| `20261008-175334-gate0` | Pass, 1400/0/3 | Pass | Pass (277 ms; 1847 / 5706 ms; 1044 ms) | Pass | **Pass**, cloud pending |

In every run, `settings.json` was restored with the original hash (`1DCDD2EB...600C1`) and no `Pillion.Host.exe`
outlived the instance.

## Gate 0 criteria

| Criterion | Status | Evidence | How it was measured |
| --- | --- | --- | --- |
| Chat reply from an Ollama model, naming the model that answered | **Pass** | Run `20261008-175013-gate0`, step e: header "gemma4:e2b via Ollama, local", reply "pong"; Outbound line `ollama/gemma4:e2b Chat local, prompt ~110 tokens, completion 216 tokens, 0 redactions, 12981 ms, Succeeded (none)`; screenshot `screenshots/local-chat.png` | UI Automation picks "gemma4:e2b (Ollama)" in the picker, types "Reply with the single word pong", presses Send, waits for Stop to disable, reads the reply header and text from the transcript and the Outbound line (event 420) from the host log |
| Chat reply from a cloud model, naming the model that answered | **Pending product owner** | Step h records "pending: no cloud key stored" | Step h runs only with `-CloudProviderId` and a key that `cmdkey` reports; it then expects a header ending ", cloud" and a cloud Outbound line with its redaction count |
| Killing the host never hangs Visual Studio | **Pass** (scripted); manual check **Pending product owner** | Step f: longest gap between two answered UI Automation polls 269 ms (limit 2000 ms) over 3 kills of 58 polls each; slowest single poll 3 ms; 0 WM_NULL timeouts | After each kill, every 250 ms for 15 s: read the main window's Name, the chat status line and the status bar through UI Automation (served by Visual Studio's UI thread) and send WM_NULL with `SendMessageTimeout` (2 s); record the longest gap between answered polls |
| The supervisor restarts the host with backoff | **Pass** | Kill 1: Restarting (1/3) at 211 ms, Starting at 1270 ms, Ready at 1798 ms; kill 2: Restarting (2/3) at 188 ms, Starting at 5176 ms, Ready at 5703 ms (the 1 s and 5 s backoffs); a chat reply "pong" after each; new host pid each time; `vsix-log-excerpt.txt` has the `supervisor.state` lines | Status line and status bar sequences from the same polls, times relative to the kill; supervisor transitions from the extension log |
| Failed after three crashes in five minutes, with Open Logs; Retry recovers | **Pass** | Kill 3 (37.9 s after kill 1): status line "Pillion Host failed: Pillion Host stopped 3 times within 5 minutes, so it will not be restarted automatically. ..." at 198 ms, status bar "Pillion: Failed, see Tools > Pillion: Open Logs", Open Logs link shown in the chat window, no host process running; **Tools > Pillion: Retry** gave Ready in 888 ms and a reply "pong"; screenshot `screenshots/host-failed.png` | UI Automation: Hyperlink "Open Logs" present in the chat window, status bar text, `Pillion.Host.exe` children of the instance; Retry invoked through the real Tools menu |
| Proposals API spike green on VS 2022 17.14 and VS 2026 18.x | **Pass, on condition that IntelliCode is a 17.x requirement** (18.10: viable; 17.14.41: viable only with IntelliCode, whose automatic code completions must be on) | `spikes/ProposalsSpike/FINDINGS.md` on `phase0/proposals-spike` (PR 2, commit `9f6e62c`), "Results on VS 2022 17.14.41 with the managed desktop workload and IntelliCode". With the .NET desktop workload added (IntelliCode 2.2.3078.1097), the log shows `SpikeProposalSourceProvider created; SuggestionServiceBase=Microsoft.VisualStudio.IntelliCode.SuggestionService.Implementation.SuggestionService`. Multi-line, text right of the caret, replacement (diff preview), virtual space, after indentation, Tab (`AcceptedByCommand`) and Esc (`DismissedAfterUserEscape`) all pass; an IntelliSense-coupled proposal displays and is accepted (two Tabs). On 17.14 the broker is IntelliCode's, so with only the core editor workload (first run, commit `8c00bb4`) the provider is never created. Unchecking IntelliCode's *Automatically generate code completions in the Editor* also stops it. Differences from 18.10: padded virtual-space text is not stripped; Alt+. does not cycle; ghost text is grey, not classified. See [Visual Studio 2022 17.14](#visual-studio-2022-1714) | `tools/Run-Scenarios.ps1` as written against the 2022 `devenv.exe` (ten scenarios on a C# console project, DTE plus SendKeys), the spike log and screenshots, a session-local variant for the IntelliCode coexistence tests, the IntelliCode option toggled in the Exp instance, and a metadata scan of `Microsoft.VisualStudio.IntelliCode.dll` |
| Secret scanner round trip: no placeholder reaches disk, originals restored in model output | **Pass** | `Pillion.Common.Tests` 741 passed, 0 failed, including the `Redaction` tests (round trip, session, edge cases, corpus, behaviour, performance); 616 passing tests whose names mention Redaction or SecretScanner across the TRX files; gateway redaction middleware tests in `Pillion.ModelGateway.Tests` (`GatewayTests`) | `dotnet test` in step b; TRX files in `tests/` |
| Local-only mode routes nothing to a cloud provider and runs no probes | **Pass** (network on); with the adapter disabled **Pending product owner** | Step g: status "Ready. Local-only: nothing leaves this machine.", status bar "Pillion: Ready, Local-only"; the cloud entry "gpt-4o-mini (Gate 0 cloud placeholder), cloud model, unavailable: Local-only mode is on (Tools > Options > Pillion Programmer > Privacy)" disabled; Auto answered under "gemma4:e2b via Ollama, local"; 1 Outbound line, local; 0 probes in the 65 s Local-only window against 4 probe lines before it; host log `Connectivity Online -> LocalOnly: local-only mode turned on` then `LocalOnly -> Online` | `privacy.localOnly` set in `settings.json`, Refresh models, picker entries read through UI Automation, Outbound lines and Debug `Probed provider` lines read from the host log; held for one full probe cadence. The offline run (`Invoke-OfflineRun.ps1`) repeats it with the adapters disabled |
| Every work item behind a feature flag | **Pass** (PR 15) | Six flags in the `features` section of `settings.json`, all default on: `chat`, `modelGateway`, `connectivityProbes`, `cloudProviders`, `ollamaProvider`, `openAICompatibleProvider`; a repository policy switches any of them off with `disabledFeatures`. `chat` off disables the Open Chat commands and the window says "Chat is disabled by settings or policy"; `modelGateway` off fails every model call with `PolicyDenied`; `cloudProviders` off hides every non-local provider; `connectivityProbes` off stops probing; the two provider flags skip their factories. The effective flags (policies applied) are readable as `effective.features`. Tests: `FeatureFlagTests` in Common (defaults, policy union, effective resolution, migration of a file without the section, monitor effects), ModelGateway (factories skipped, cloud hidden), Host (each flag through the composed host, the virtual key, the real settings store) and VisualStudio (`ChatAvailabilityTests`: command state and window follow the flag). Documented in the README ("Feature flags") | `dotnet test PillionProgrammer.NoVsix.slnf`; the flag list is `FeatureNames.All` in `Pillion.Common` |
| Unit and component tests green | **Pass** | Step b: 1400 passed, 0 failed, 3 skipped (Contracts 178, Common 741, Host 128, ModelGateway 200 + 3 skipped, VisualStudio 153). The 3 skips are the OpenAI-compatible live tests (no endpoint configured). After PR 15 (feature flags, owner check, Ollama de-duplication) the same command gives 1460 passed, 0 failed, 3 skipped locally (Contracts 178, Common 758, Host 139, ModelGateway 209 + 3 skipped, VisualStudio 176); the same projects pass on CI (run 37815739685). Ollama live tests including fill-in-the-middle: **Pass** with `qwen2.5-coder:1.5b` pulled: `dotnet test tests\Pillion.ModelGateway.Tests --filter Category=Live` on `phase0/integration` passed the four Ollama live tests (run by the lead) | `dotnet test PillionProgrammer.NoVsix.slnf -c Release --logger trx` |
| Main builds on CI on 17.14 and 18.x | **Pass** for the red CI (PR 15, 18.x); the 17.14 leg and a run on `main` are still **Pending** | CI was red on every phase 0 pull request (PR 8, run 37807700407: 5 failures in `Pillion.VisualStudio.Tests`, "Pillion Host's pipe is owned by another user"). Cause confirmed as the elevated hosted runner: its objects are owned by `BUILTIN\Administrators`, and `HostProcessLauncher.VerifyServer` compared the owner only with the user SID. Fixed in PR 15 (the token owner is accepted too, any other SID is still refused). CI is green on PR 15: https://github.com/ryanjkendrick/PillionProgrammer/actions/runs/37815739685 (Build and test, 4m48s: full solution Release build with MSBuild 18, then Common 758, Contracts 178, ModelGateway 205 with 7 skipped live tests, Host 139, VisualStudio 176, all passing). There is still no 17.14 CI leg, and nothing from phase 0 has reached `main` yet, so the criterion is met for the build the PR runs on, not yet on `main` | `gh pr checks 15`, `gh run view 37815739685` |

## Results of the best run

Run `tools/gate0/out/20261008-175013-gate0` (overall Pass, 10 steps, 0 failed, 1 pending):

| Step | Status | Time | Result |
| --- | --- | --- | --- |
| Prerequisites | Pass | 0.5 s | Visual Studio Community 2026 18.10.3 (18.10.12224.181); Ollama 0.40.1 with gemma4:e2b |
| a. Build, deploy and versions | Pass | 10.5 s | VSIX manifest 0.1.0; `Pillion Host 0.1.0+8627171708d3e8e55a8f150daec6e248a4a891d6 (contract 1.1)`; Ollama 0.40.1; the deployed `Pillion.VisualStudio.dll` matches the build |
| b. Tests | Pass | 19.3 s | 1400 passed, 0 failed, 3 skipped |
| c. Settings | Pass | 0.1 s | Backup sha256 `1DCDD2EB...600C1`; Ollama provider and cloud placeholder added |
| d. Launch | Pass | 12.8 s | Experimental Instance on a scratch folder; host started as its child; chat status "Ready. Online." |
| e. Local chat | Pass | 14.6 s | "gemma4:e2b via Ollama, local" / "pong" (13.4 s, cold model load); routed line "Chat: explicit model ollama/gemma4:e2b" |
| f. Host kill | Pass | 59.2 s | Longest UI gap 269 ms; Ready 1798 ms after kill 1 and 5703 ms after kill 2; Failed with Open Logs after kill 3; Retry to Ready in 888 ms |
| g. Local-only | Pass | 65.6 s | As in the criteria table |
| h. Cloud chat | Pending | | No cloud key stored |
| i. Close and restore | Pass | 1.9 s | Closed by closing the main window; host pids seen 50680, 53336, 53356, 77212, none survived; `settings.json` restored, hash matches the backup |

Status sequences from that run (milliseconds after the kill):

```
kill 1  chat:       211 Pillion Host is restarting (1/3)... | 1270 Starting Pillion Host... | 1798 Ready. Online.
        status bar: 211 Pillion: Restarting (1/3) | 1270 Pillion: Starting | 1798 Pillion: Ready, Online
kill 2  chat:       188 Pillion Host is restarting (2/3)... | 5176 Starting Pillion Host... | 5703 Ready. Online.
kill 3  chat:       198 Pillion Host failed: Pillion Host stopped 3 times within 5 minutes, ...
        status bar: 198 Pillion: Failed, see Tools > Pillion: Open Logs
```

Extension log for kill 1 (`vsix-log-excerpt.txt`):

```
2026-10-08T16:51:11.691Z warn host.exited pid=50680 exitCode=-1 message="Pillion Host exited."
2026-10-08T16:51:11.698Z info supervisor.state from=Ready to=Restarting attempt=1 message="Pillion: Restarting (1/3)"
2026-10-08T16:51:12.710Z info supervisor.state from=Restarting to=Starting attempt=0 message="Pillion: Starting"
2026-10-08T16:51:12.726Z info host.process.started launch=2 pid=53336 pipe=pillion-79128-404a9efb breakaway=False message="Started Pillion Host."
2026-10-08T16:51:13.407Z info supervisor.state from=Starting to=Ready attempt=0 message="Pillion: Ready"
```

## Visual Studio 2022 17.14

Date: 2026-10-08. Branch `phase0/vs2022-verify` (from `phase0/integration`, commit `d15d0d3`).

**Machine and versions.** Visual Studio Community 2022 **17.14.41** (September 2026), installation version
17.14.37710.0, instance `cb15242f`, Experimental Instance hive `%LOCALAPPDATA%\Microsoft\VisualStudio\17.0_cb15242fExp`.
Beside it, Visual Studio Community 2026 18.10.3, whose MSBuild builds the solution (the .NET 10 SDK needs MSBuild 18).
VSIX manifest 0.1.0 (`InstallationTarget [17.14,)`); `Pillion.Host.exe --version`: `Pillion Host 0.1.0 (contract 1.1)`;
Ollama 0.40.1 with `gemma4:e2b`; PowerShell 7.6.6; .NET SDK 10.0.401.

**Workloads.** vswhere reports only the **core editor** workload (`Microsoft.VisualStudio.Workload.CoreEditor`) for
this install. It does **not** have the *Visual Studio extension development* workload
(`Microsoft.VisualStudio.Workload.VisualStudioExtension`), and it has no .NET or C++ workload, so no Roslyn, no managed
project system, no IntelliCode and no GitHub Copilot (the title-bar badge offers "Install GitHub Copilot components").
Nothing was installed. Building does not need the workload, because the 2026 MSBuild builds and 2022's own
`VSIXInstaller.exe` installs; but the missing IntelliCode matters for the Proposals spike (below).

Later the same day the product owner added the **.NET desktop development** workload. vswhere then lists
`Microsoft.VisualStudio.Workload.ManagedDesktop` 17.14.36518.2, `Microsoft.VisualStudio.Component.IntelliCode`
17.14.37709.7, `Microsoft.VisualStudio.IntelliCode` 2.2.3078.1097 and `Microsoft.VisualStudio.IntelliCode.CSharp`
1.1.0.2437901. Copilot is still not installed. The extension results below are from before that change; the
Proposals spike was rerun after it.

### Proposals API spike on 17.14: Pass with IntelliCode (rerun), Fail without it

**Rerun with IntelliCode.** Full write-up: `spikes/ProposalsSpike/FINDINGS.md`, "Results on VS 2022 17.14.41 with the
managed desktop workload and IntelliCode" (PR 2, commit `9f6e62c` on `phase0/proposals-spike`). Summary:

* The broker exists now. **IntelliCode provides it on 17.x**: `Microsoft.VisualStudio.IntelliCode.dll` 2.2.3078.1097
  defines `ProposalBrokerProvider`, `SuggestionService`, `SuggestionManager` and `InlineCompletionsListener` (the same type
  names 18.10 has in the platform), and no other 2022 assembly does. The spike's log reads
  `SpikeProposalSourceProvider created; SuggestionServiceBase=Microsoft.VisualStudio.IntelliCode.SuggestionService.Implementation.SuggestionService`.
* `tools/Run-Scenarios.ps1` ran as written against the 2022 `devenv.exe` on a C# console project (the managed project
  system now loads it). Multi-line, text right of the caret (single and multi-line), replacement (diff preview), virtual
  space, after indentation, Tab and Esc all pass with log evidence and screenshots. Differences from 18.10: padded text
  in virtual space is not stripped (the indent doubles; unpadded text is correct on both), Alt+. does not cycle to a second
  proposal, and ghost text is flat grey instead of italic and syntax-coloured.
* Coexistence with IntelliCode: every proposal is reported under the provider name `IntelliCodeLineCompletions`.
  IntelliCode's own source is `IntelliCodeCSharpProposalSource` (`[ContentType("CSharp")]`, no `[Order]`). When our source
  returns nothing, IntelliCode's whole-line completions show. When ours returns a proposal, ours is shown and
  IntelliCode's is not. But the broker keeps IntelliCode's earlier proposal queued: when ours diverges as the user types,
  IntelliCode's takes over without our source being asked again. A provider without `[Order]` behaved the same in the two
  tests.
* An IntelliSense-coupled proposal (completion list open, `CompletionState` passed through) **is displayed** on 17.14.
  The first Tab commits the item and the second accepts ours. That shape was not displayed on 18.10 and needs a retest there.
* **IntelliCode's *Automatically generate code completions in the Editor* option switches Pillion's ghost text off as
  well.** With it unchecked, the spike's manager and source were never called and nothing was displayed; re-checking it
  restored everything immediately.
* Verdict: **viable on 18.10; viable on 17.14 only with IntelliCode present and that option on.** The criterion row above
  is therefore **Pass, on condition that IntelliCode is a 17.x requirement**. That is a **product requirement for the
  product owner to decide** (see [What the product owner must do](#what-the-product-owner-must-do)).

**First run, core editor only.** Full write-up: `spikes/ProposalsSpike/FINDINGS.md`, "Results on VS 2022 17.14.41"
(commit `8c00bb4`). Summary:

* The spike was built with the 2026 MSBuild against the 17.14 SDK and installed with 2022's
  `VSIXInstaller.exe /quiet /rootSuffix:Exp`. Its DLL is in the Exp instance's MEF catalog.
* All ten scenarios (multi-line, text right of the caret, multi-line right of the caret, replacement, virtual space
  padded and unpadded, after indentation, Esc, two proposals with Alt+., IntelliSense list open) were driven in the 2022
  Experimental Instance on a C# file in an open folder. **`%TEMP%\ProposalsSpike.log` stayed empty**: the proposal
  source provider was never constructed and never asked for a proposal. No ghost text appeared; Tab inserted
  indentation. A TextMate word-completion list was open in the IntelliSense case and still nothing was requested.
* Cause: Visual Studio 2022 17.14 ships the Proposals **contract** (the runtime `Microsoft.VisualStudio.Language.dll`
  17.14.305 has exactly the API of the 17.14.249 SDK package) but **no implementation** of the proposal broker, the
  suggestion service or the inline-completions host in the platform: no assembly under the 2022 `Common7\IDE` defines
  them or imports `ProposalSourceProviderBase`. In 18.10 they are in the platform
  (`Microsoft.VisualStudio.Editor.Implementation.dll`). In 17.x they come with IntelliCode (confirmed by the rerun above).
* Coexistence: no inline-completion provider exists on this install (no IntelliCode, no Copilot), so there was nothing
  to coexist with.
* Verdict at the time: **not viable on 17.14 with only the core editor workload**. This still holds for such installs.

### The extension on 17.14: Pass (one unexplained empty reply, not reproduced)

How: the Release build from this worktree (2026 MSBuild, `/p:DeployExtension=false`, 0 warnings, 0 errors), installed
with `"C:\Program Files\Microsoft Visual Studio\2022\Community\Common7\IDE\VSIXInstaller.exe" /quiet /rootSuffix:Exp
Pillion.VisualStudio.vsix`, then driven with UI Automation rooted at the launched 2022 `devenv.exe`, using the
Gate 0 module. Scripted runs, all with `-DevEnvPath "C:\Program Files\Microsoft Visual Studio\2022\Community\Common7\IDE\devenv.exe"`:

| Run folder (`tools/gate0/out/`) | Command | Result |
| --- | --- | --- |
| `20261008-182121-gate0` | `Invoke-Gate0.ps1 -SkipTests -DevEnvPath ...` | launch failed: package not loaded (configuration not refreshed, see below; fixed in the module) |
| `20261008-182811-gate0` | `Invoke-Gate0.ps1 -SkipTests -DevEnvPath ...` | 1 failed (empty reply after Retry, see below), 1 pending (cloud); everything else Pass |
| `20261008-183257-smoke2022` | smoke script on the module functions: launch, status bar, chat, one kill, chat again | Pass; its Options step could not find the native tree through UI Automation, so the Output pane and Options checks were finished by hand in a separate session |
| `20261008-184307-gate0` | `Invoke-Gate0.ps1 -SkipBuild -SkipTests -UninstallAfter -DevEnvPath ...` | **Pass**, 0 failed, 1 pending (cloud): kills Ready in 1829 / 5726 ms, longest UI gap 264 ms, Retry to Ready in 928 ms and "pong" after it; the extension uninstalled at the end |

Finding during setup: after `VSIXInstaller` reinstalled the extension, **17.14 did not load the package**: no Pillion
commands on the Tools menu, no host process, no extension log lines. The activity log showed
`PkgDefCache fast check: timestamps are current`, so devenv kept its cached configuration (`extensions.configurationchanged`
in the hive was not touched by the reinstall). `devenv.exe /rootsuffix Exp /updateconfiguration` fixed it; the gate
module now runs it after every install. The first gate run (`20261008-182121-gate0`) failed on exactly this
("Pillion Host did not start as a child of the Experimental Instance within 90 s").

| Check | Result | Evidence |
| --- | --- | --- |
| Package loads with no error | **Pass** | Tools menu shows Pillion: Open Chat, Show Status, Retry, Open Logs; the Output window's "Pillion Programmer" pane shows the supervisor lines through `supervisor.state from=Starting to=Ready` (screenshot `output-pillion.png` in the smoke run folder). Every `warn`/`error` line in the extension log during the session comes from the deliberate host kills |
| Status bar reaches "Pillion: Ready, Online" | **Pass** | Status bar "Pillion: Ready, Online" 9.3 s after launch (smoke run; host pid started as a child of the 2022 `devenv.exe` from `17.0_cb15242fExp\Extensions\...\Host\Pillion.Host.exe`) |
| Tools > Pillion: Open Chat opens the window | **Pass** | UI Automation finds "Pillion Chat"; status line "Ready. Online." |
| Ollama answers "Reply with the single word pong" | **Pass** | Header **"gemma4:e2b via Ollama, local"**, reply "pong" (gate run: 12.8 s cold; smoke run: 2.1 s). Outbound line `ollama/gemma4:e2b Chat local, prompt ~110 tokens, completion 147 tokens, 0 redactions, 12497 ms, Succeeded (none)` |
| One host kill recovers to Ready | **Pass** | Smoke run: Restarting (1/3) at 233 ms, Starting at 1029 ms, **Ready, Online at 1819 ms**; longest UI gap 269 ms, 0 WM_NULL timeouts; "pong" afterwards. Gate run: Ready 1822 ms after kill 1 and 5958 ms after kill 2 (the 1 s and 5 s backoffs), Failed with Open Logs after kill 3, Retry to Ready in 996 ms |
| Local-only (gate step g) | **Pass** | Status "Ready. Local-only: nothing leaves this machine."; the cloud placeholder greyed with the Local-only reason; Auto answered locally; 0 probes in 65 s |
| Tools > Options > Pillion Programmer, both pages readable | **Pass** | Classic Options dialog (17.14 has no unified settings window, so the pages show directly, no link). Under the Dark theme the dialog stays light, and both pages render as dark text on a white background: **Providers and keys** (description, provider list, Add, Remove, Save, Reload, Id, Kind, Display name, Endpoint, Model ids) and **Privacy** (Local-only mode, Cloud use per task class with Chat, Inline edit, Autocomplete, Agent and their descriptions, Save, Reload). All text legible; both pages scroll vertically at the default dialog size. Screenshots `options-providers.png`, `options-privacy.png` in the smoke run folder |
| Close; no `Pillion.Host.exe` remains | **Pass** | Every run: no surviving host (gate run pids seen 83624, 69084, 81792, 60792; smoke 80040, 75480; manual session 84796) |
| Uninstall from the Exp instance | **Done** | `VSIXInstaller.exe /quiet /rootSuffix:Exp /uninstall:PillionProgrammer.e4d0d8fe-694c-4b1f-bcb1-31b346cda51e` (step i of the last gate run, `-UninstallAfter`), then `/updateconfiguration`; the hive's `Extensions` folder holds no Pillion files. Uninstalling while the instance is running only schedules the removal (exit 0, files stay until it closes) |

**Open: one empty reply after Retry.** In the gate run, the reply sent after **Tools > Pillion: Retry** came back under
the right header ("gemma4:e2b via Ollama, local") with **empty text**, although the host logged
`Outbound ...: ollama/gemma4:e2b Chat local, prompt ~110 tokens, completion 102 tokens, ..., Succeeded (none)`. That
made step f Fail ("no clean reply after Retry"); everything else in step f passed. The replies after kill 1 and kill 2,
the smoke run's reply after its kill, the reply after Retry in the next gate run (`20261008-184307-gate0`) and every
18.10 run were "pong". Not diagnosed: it may be the model emitting only
non-content tokens, or a transcript rendering race after a restart. Worth a look in phase 1 (the transcript should say
"empty reply" rather than show nothing).

**Gate script.** `tools/gate0/Invoke-Gate0.ps1` now takes `-DevEnvPath` (and `-UninstallAfter`); see the script help
and the README. With `-DevEnvPath` naming the 2022 `devenv.exe`, step a builds with the newest MSBuild and
`/p:DeployExtension=false`, installs the VSIX with 2022's `VSIXInstaller.exe /quiet /rootSuffix:Exp` (uninstalling the
previous copy first), runs `/updateconfiguration`, and the live checks drive the 2022 Experimental Instance
(`Find-VisualStudio -DevEnvPath`).

## What the product owner must do

1. **Cloud chat.** Store a key for one OpenAI-compatible provider on **Tools > Options > Pillion Programmer > Providers
   and keys** (it goes to the Windows Credential Manager as `PillionProgrammer/<id>`), then run
   `pwsh tools/gate0/Invoke-Gate0.ps1 -SkipBuild -SkipTests -CloudProviderId <id>`. Step h then picks that provider's
   model, sends the same prompt and records the header ("... via <provider>, cloud") and the Outbound line with its
   redaction count. The script only asks `cmdkey /list:PillionProgrammer/<id>` whether the key exists; it never reads
   it. Add a screenshot from the Chat window for the gate review.
2. **IntelliCode requirement for ghost text on Visual Studio 2022: decided 2026-10-08, option (a).** IntelliCode with its automatic code completions is a requirement on 17.x, enforced by a run-time check in phase 1; the adornment fallback stays the contingency. The question as it was put: The rerun with the .NET desktop
   workload passed: on 17.x the proposal broker is IntelliCode's, so ghost text works only when IntelliCode is installed
   and its *Automatically generate code completions in the Editor* option is on (see
   [Visual Studio 2022 17.14](#visual-studio-2022-1714) and PR 2). Choose one: (a) state IntelliCode as a requirement for
   ghost text on 2022, with a run-time check that reports "inline suggestions need IntelliCode's code completions on
   Visual Studio 2022" in the status bar (a VSIX prerequisite would also block core-editor-only installs and 18.x, where
   it is not needed); (b) build the adornment fallback for 17.x; or (c) offer ghost text on 18.x only. Then record the
   decision, which settles whether the Proposals criterion is a Pass.
3. **Offline run.** From an elevated PowerShell 7 prompt in the repository root:
   `pwsh tools/gate0/Invoke-OfflineRun.ps1 -IAmReady`. It lists the adapters it disables (on this machine `Ethernet`
   and `vEthernet (Default Switch)`), takes about five minutes and puts the network back in a `finally` block. Dry run
   first if you like: `pwsh tools/gate0/Invoke-OfflineRun.ps1 -IAmReady -WhatIf`. If the window is closed mid-run,
   re-enable the adapters with `Get-NetAdapter | Where-Object Status -eq Disabled | Enable-NetAdapter`.
4. **Copilot and IntelliCode ordering policy.** Decide whether Pillion's proposals go before or after GitHub Copilot's
   (18.x) and IntelliCode's (17.x) when both have one, and whether that is a setting. On 18.10,
   `[Order(Before = "CompletionsProposalSourceProvider")]` won every time. On 17.14, ours was shown first, but
   IntelliCode's queued proposal took over as soon as ours diverged, and a provider without `[Order]` behaved the same.
5. **Manual host-kill check.** The criterion asks for a manual check beside the scripted one: in the Experimental
   Instance, kill `Pillion.Host.exe` in Task Manager while a reply is streaming and confirm the reply is marked
   Interrupted with Retry, typing stays responsive, and the status bar goes Restarting then Ready.
6. **CI.** Review and merge PR 15 into `phase0/integration` (it fixes the pipe-owner check that made CI red), then decide
   whether a Visual Studio 2022 17.14 CI leg is wanted before the first merge to `main`.
7. **Gate decision.** Record pass or fail in the decision log.

## Open issues known from phase 0

- **Closed in phase 1 (work item 1.16): no VSSDK integration-test project.** At Gate 0 the host-kill evidence was a
  scripted UI Automation run against the Experimental Instance (`Invoke-HostKillCheck`), plus `RealProcessTests`
  (supervisor kill and restart against `Pillion.FakeHost`, outside Visual Studio). `tests/Pillion.VisualStudio.IntegrationTests`
  now runs `[IdeFact]` tests inside the Experimental Instance of 18.x, and the same sources build as
  `tests/Pillion.VisualStudio.IntegrationTests.VS2022` for 17.14 (no single version of the test framework drives both).
  They cover package load on folder open with no Activity Log errors, `IHostConnection` reaching Ready, the host kill
  (UI thread probed every 250 ms for 15 s, longest gap under 2 s, Ready again within 15 s) and **Tools > Pillion: Open
  Chat**; `tools/vs-tests` deploys and runs them, and CI runs them on both versions. The UI Automation scripts stay for
  the gate runs (chat with a model, Local-only, the offline run).
- **The chat transcript is not virtualised.** Every turn stays in the visual tree; long conversations will cost memory
  and layout time. Fine for phase 0; needs a virtualising panel before the chat is used for long sessions.
- **Azure OpenAI is deferred.** The OpenAI-compatible adapter covers OpenAI, OpenRouter, enterprise gateways and local
  OpenAI-compatible servers; Azure OpenAI's deployment-based URLs and `api-key` header follow later.
- **Closed in PR 15: CI was red on every phase 0 pull request.** `Pillion.VisualStudio.Tests` failed five tests on the
  GitHub-hosted runner (PR 8, run 37807700407): `HostProcessLauncher.VerifyServer` threw "Pillion Host's pipe is owned
  by another user". The runner's process is elevated, so the host's pipe is owned by `BUILTIN\Administrators` rather
  than by the user SID the check compared with; Visual Studio started with Run as administrator would have refused its
  own host the same way. `VerifyServer` now accepts the user SID or the token owner SID and still refuses any other.
  The host needed no change: .NET's `CurrentUserOnly` owns and grants the pipe as the token owner on the server and
  compares the remote owner with the token owner on the client, so both ends agree. Not reproduced locally (this
  machine's prompt is not elevated and a UAC prompt could not be answered); the evidence is the unit tests with an
  injected Administrators owner and the green CI run 37815739685 on PR 15.
- **Closed in PR 15: no feature flags existed.** See the feature flag row above. Not yet covered by a flag: the editor
  and workspace services and the stubs for later phases (they have no behaviour in phase 0).
- **`main` has not been built by CI with phase 0 merged.** Nothing from phase 0 has been squash-merged to `main` yet;
  `phase0/integration` (PR 8) carries it. CI targeted 18.x only (`windows-latest`); phase 1 (work item 1.16) adds a
  `windows-2022` job that runs the integration tests on 17.14 against the VSIX the 18.x job built.
- **Closed in PR 15: duplicate picker entries.** Ollama on this machine listed `gemma4:e2b` twice (two digests under
  one name) and the picker showed two identical entries. The Ollama provider now lists each name once, keeping the
  first entry of `/api/tags` (the digest the tag currently points at; `:latest` and no tag are the same name). Fixture
  test `ListModels_DeduplicatesByName_KeepingTheFirstEntryTheCurrentDigest`.
- **Worktree metadata.** The phase 0 worktrees under `D:\Development\PillionProgrammer-wt` were created with MSYS-style
  paths (`/d/Development/...`) in their `.git` files, which Windows Git cannot resolve (`git worktree list` marks them
  prunable). Running `git worktree prune` in the main checkout would drop them. The gate0 worktree's `.git` file was
  rewritten to `D:/Development/...` so Git works there.
