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
| `tools/gate0/Invoke-Gate0.ps1` | a. builds the full solution (Release, Visual Studio 2026 MSBuild) and deploys the VSIX to the Experimental Instance, recording the VSIX manifest version, `Pillion.Host.exe --version` and the Ollama version; b. `dotnet test PillionProgrammer.NoVsix.slnf` with TRX files and per-project counts; c. backs up `settings.json`; d. launches `devenv /rootsuffix Exp` on a scratch folder; e. local chat check; f. host-kill check; g. Local-only check; h. cloud chat check (only with `-CloudProviderId` and a stored key); i. closes the instance, checks that no `Pillion.Host.exe` survives and restores `settings.json` byte for byte | Anyone; the agents ran it |
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
| Proposals API spike green on VS 2022 17.14 and VS 2026 18.x | **Pending product owner** (18.10: viable, PR 2) | `spikes/ProposalsSpike/FINDINGS.md` on `phase0/proposals-spike`: viable on 18.10 with two caveats (IntelliSense-coupled proposals unproven, API drift) | 17.14 is now installed; a separate agent runs the 17.14 checklist |
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

## What the product owner must do

1. **Cloud chat.** Store a key for one OpenAI-compatible provider on **Tools > Options > Pillion Programmer > Providers
   and keys** (it goes to the Windows Credential Manager as `PillionProgrammer/<id>`), then run
   `pwsh tools/gate0/Invoke-Gate0.ps1 -SkipBuild -SkipTests -CloudProviderId <id>`. Step h then picks that provider's
   model, sends the same prompt and records the header ("... via <provider>, cloud") and the Outbound line with its
   redaction count. The script only asks `cmdkey /list:PillionProgrammer/<id>` whether the key exists; it never reads
   it. Add a screenshot from the Chat window for the gate review.
2. **Proposals spike on Visual Studio 2022 17.14.** Visual Studio 2022 Community 17.14.41 is now installed; a separate
   agent reruns the 17.14 checklist in `spikes/ProposalsSpike/FINDINGS.md` (PR 2). Review its findings.
3. **Offline run.** From an elevated PowerShell 7 prompt in the repository root:
   `pwsh tools/gate0/Invoke-OfflineRun.ps1 -IAmReady`. It lists the adapters it disables (on this machine `Ethernet`
   and `vEthernet (Default Switch)`), takes about five minutes and puts the network back in a `finally` block. Dry run
   first if you like: `pwsh tools/gate0/Invoke-OfflineRun.ps1 -IAmReady -WhatIf`. If the window is closed mid-run,
   re-enable the adapters with `Get-NetAdapter | Where-Object Status -eq Disabled | Enable-NetAdapter`.
4. **Copilot ordering policy.** Decide whether Pillion's proposals go before or after GitHub Copilot's when both have
   one (the spike shows `[Order(Before = "CompletionsProposalSourceProvider")]` wins every time), and whether that is a
   setting.
5. **Manual host-kill check.** The criterion asks for a manual check beside the scripted one: in the Experimental
   Instance, kill `Pillion.Host.exe` in Task Manager while a reply is streaming and confirm the reply is marked
   Interrupted with Retry, typing stays responsive, and the status bar goes Restarting then Ready.
6. **CI.** Review and merge PR 15 into `phase0/integration` (it fixes the pipe-owner check that made CI red), then decide
   whether a Visual Studio 2022 17.14 CI leg is wanted before the first merge to `main`.
7. **Gate decision.** Record pass or fail in the decision log.

## Open issues known from phase 0

- **No VSSDK integration-test project yet.** The host-kill evidence is a scripted UI Automation run against the
  Experimental Instance (`Invoke-HostKillCheck`), plus `RealProcessTests` (supervisor kill and restart against
  `Pillion.FakeHost`, outside Visual Studio). A VS SDK test project in the Experimental Instance is a phase 1 item.
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
  `phase0/integration` (PR 8) carries it. CI targets 18.x only (`windows-latest`); a 17.14 CI leg does not exist.
- **Closed in PR 15: duplicate picker entries.** Ollama on this machine listed `gemma4:e2b` twice (two digests under
  one name) and the picker showed two identical entries. The Ollama provider now lists each name once, keeping the
  first entry of `/api/tags` (the digest the tag currently points at; `:latest` and no tag are the same name). Fixture
  test `ListModels_DeduplicatesByName_KeepingTheFirstEntryTheCurrentDigest`.
- **Worktree metadata.** The phase 0 worktrees under `D:\Development\PillionProgrammer-wt` were created with MSYS-style
  paths (`/d/Development/...`) in their `.git` files, which Windows Git cannot resolve (`git worktree list` marks them
  prunable). Running `git worktree prune` in the main checkout would drop them. The gate0 worktree's `.git` file was
  rewritten to `D:/Development/...` so Git works there.
