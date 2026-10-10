# Phase 1: Assist

Phase 1 turns the phase 0 foundations into the first build worth putting in front of users: ghost-text autocomplete, inline chat as a difference view, a Chat window with mentions and an Outbound log, and the measurements that prove the budgets hold online and offline. It closes at Gate 1.

The design document is the specification: https://claude.ai/code/artifact/02f3ee51-8712-47d2-89af-3c7f6f1559fb (export in [design-document.md](design-document.md)). Section numbers below refer to it. Phase 0's outcome is in [phase-0-plan.md](phase-0-plan.md) and [gate-0-report.md](gate-0-report.md).

## What phase 0 left us

- A VSIX shell with a supervised .NET 10 host, contract 1.1, settings and Credential Manager secrets, the secret scanner, the Model Gateway with Ollama and OpenAI-compatible adapters, connectivity and Local-only mode, feature flags, a Chat window with Options pages, and the Gate 0 scripts under `tools/gate0`.
- The Proposals API works on VS 2026 and, with IntelliCode present and its automatic completions on, on VS 2022 17.14 (decision: option (a), IntelliCode is a requirement on 17.x with a run-time check).
- Two findings from the spike that phase 1 must design for: a competitor's queued proposal (IntelliCode on 17.x, Copilot on 18.x) takes over the moment ours stops matching, and the `[Order]` attribute was not decisive; answering first may matter as much as ordering.
- Not yet present: a Visual Studio integration-test project (the host-kill check is a scripted UI Automation run), persistence for conversations (phase 2's Local Store), retrieval (phase 2), the Anthropic, Gemini, Mistral and Azure OpenAI adapters.

## Gate 1

Gate 1 is "autocomplete without retrieval and chat within budget, online and offline" (section 17). Budgets come from section 13 and are measured on the reference machine (8-core CPU, 32 GB, 8 GB GPU). Local figures assume the model is resident.

| Criterion | Target | Evidence |
| --- | --- | --- |
| Ghost text works on VS 2022 17.14 (IntelliCode on) and VS 2026 18.x for C#, TypeScript and Python | Multi-line and single-line proposals, Tab, Esc, Ctrl+Right word accept, next and previous suggestion; no proposal while a completion list has a selected item; Tab never taken while another provider's proposal is visible | Visual Studio integration tests on both versions with IntelliCode (17.14) and Copilot (18.x) enabled; gate script run |
| IntelliCode run-time check on 17.x | Status bar and Output pane report when IntelliCode is missing or its automatic completions are off; nothing crashes | Integration test with the option off |
| Autocomplete latency, local 1.5B FIM model on CPU | p50 600 ms, p95 1,500 ms, prompt within 1,024 tokens | Performance suite |
| Autocomplete latency, local 7B FIM model on the GPU | p50 300 ms, p95 800 ms, prompt within 2,048 tokens with a cached prefix | Performance suite, when the 7B model is pulled |
| Autocomplete latency, cloud Fast profile | p50 400 ms, p95 1,000 ms; 3 s hard timeout dropped silently | Performance suite, when a cloud key is stored |
| Keystroke latency with autocomplete on | +0 ms p50, +2 ms p95; all work off the UI thread | Performance suite |
| Prefix cache hit rate during steady typing | 70% or more | Gateway counters in the performance suite |
| VSIX cost in devenv.exe | 0 ms blocking at start-up, under 50 ms total UI-thread CPU; added working set 60 MB or less | Performance suite |
| Chat first token, cloud | p50 1.5 s, p95 3 s, including context assembly | Performance suite |
| Context assembly without retrieval | p50 150 ms, p95 400 ms | Performance suite |
| Inline chat preview, first line | p50 2 s, p95 5 s; Enter applies as one edit with one undo step; Esc leaves nothing to undo | Integration test plus gate script |
| Mentions and inspection | `@file`, `@selection` and `@errors` resolve; the Context Inspector shows the assembled context before sending; every request from every task class has an Outbound log entry | Integration tests; gate script |
| Degradation | Autocomplete p95 over 1.5 s for five minutes raises the debounce to 350 ms and drops to single-line; recovers | Component test with a slow fake provider |
| Completion evaluation baseline | The completion golden set runs against the recommended local model and records exact match, edit similarity and latency; no score gate yet (section 15: gates are revised after the first baseline) | Harness report |
| Offline run | Every check above with the network adapter disabled, local models only | `tools/gate0/Invoke-OfflineRun.ps1` extended for phase 1 |
| CI | Green on develop with the 18.x job and a 17.14 integration-test job | CI run |
| Feature flags | Every work item behind a flag; tests green; nothing lands red | CI run; flag list in the README |

## Work items

| Id | Work item | Component | Depends on | Status |
| --- | --- | --- | --- | --- |
| 1.1 | Proposals adapter: a thin layer over `Microsoft.VisualStudio.Language.Proposals` compiled against the 17.14 SDK; MEF proposal source and provider; shown, accepted and rejected events from the suggestion service matched by proposal id; command filter for word accept and next and previous; suppression in generated and designer files, files over 1 MB and switched-off languages; IntelliCode run-time check on 17.x; UI Automation announcement (sections 5, 13) | VSIX | | |
| 1.2 | Autocomplete request pipeline in the VSIX: 200 ms debounce, cancel on every keystroke, stale results discarded by request id, context capture (prefix, suffix, caret, enclosing declaration through Roslyn where available, open documents, recent edits), multi-line decision and output stop rules from Completion shaping, accept-triggers-next, degradation to 350 ms and single-line (sections 5, 9, 13) | VSIX | 1.1 | |
| 1.3 | Host autocomplete service: `CompleteAsync` through the gateway's FIM path; prompt budget of 1,024 tokens on CPU and 2,048 on GPU or cloud with the suffix trimmed first; FIM templates per model family; token counting with Microsoft.ML.Tokenizers and a calibrated estimate; GPU tier detection; stable prompt prefix for the local cache; Outbound records (sections 6, 7) | Host, ModelGateway | | |
| 1.4 | Coexistence strategy: start-up detection of Copilot and IntelliCode with the offer to disable one side or run on demand; the ordering setting (`autocomplete.ordering`: Pillion first, competitor first, on demand) with the default the product owner chooses; a design and implementation that keeps a competitor's queued proposal from displacing ours, verified against the spike's findings on both versions (section 5, Suggestion coexistence) | VSIX, Spike | 1.1 | |
| 1.5 | Autocomplete telemetry (local only): p50 and p95 windows, cache hit rate, acceptance rate, status bar indicator; feeds the degradation rule and the performance suite (sections 12, 13) | VSIX, Host | 1.2, 1.3 | |
| 1.6 | Inline chat adornment: the chord (prefix, then I), a WPF adornment above the selection with a prompt line, model picker and recent prompts; slash shortcuts `/doc`, `/test`, `/fix`, `/explain`; scope is the selection, else the enclosing member through Roslyn for C# and VB, else the current line (section 5) | VSIX | | |
| 1.7 | Inline difference view and apply: `IDifferenceBufferFactoryService` and `IWpfDifferenceViewer` in inline mode with +/- markers; Enter applies as one `ITextEdit` after source-control checkout; Esc discards with nothing to undo; follow-up prompt refines in place (section 5) | VSIX | 1.6 | |
| 1.8 | Host inline-edit service: the InlineEdit task class, search-and-replace edit format validated before preview, diagnostics attached for `/fix`, redaction round trip on edits with unresolvable placeholders rejected, a new contract method set for inline edits (contract 1.2, minor) (sections 6, 8, 12) | Host, Contracts | | |
| 1.9 | Mentions in the VSIX: `@file` (fuzzy picker over the solution or folder file list, no index yet), `@selection`, `@errors` (Error List and Roslyn diagnostics), `@clipboard`; picker in the Chat window; pins persist for the conversation with token badges (section 7) | VSIX | 1.11 | |
| 1.10 | Mentions and context assembly in the host: `ResolveMentionAsync` for file, selection and errors; context assembly v1 (budget = window minus output reserve minus system prompt; order rules, pins, current file region, history; per-item caps; history compression by a new `Summarise` task class, contract minor); shared token counting with 1.3 (section 7) | Host, Contracts | 1.3 | |
| 1.11 | IDE signals v1: Error List and Roslyn diagnostics, build started and finished, running-document-table events; feeds `@errors`, the inline `/fix`, and the pause-during-build hook phase 2 needs (section 5) | VSIX | | |
| 1.12 | Chat window polish: Markdig rendering with remote images disabled and links opened after confirmation, code blocks in embedded editor views with Insert at cursor, transcript virtualisation, conversation list kept in memory until phase 2's store (section 5) | VSIX | | |
| 1.13 | Context Inspector and Outbound log view: the assembled context shown before a chat request with a remove button per item and redaction notices; the Outbound log view fed by a new read method over the host's `IOutboundLog` (contract minor) (sections 7, 12) | VSIX, Host, Contracts | 1.10 | |
| 1.14 | Cloud adapters: Anthropic, Google Gemini, Mistral with Codestral FIM, Azure OpenAI with key or Entra ID; catalog entries and Test connection (section 6) | ModelGateway | | |
| 1.15 | Models and routing Options page (profile per task class, cloud switch per task class, Think harder control placeholder) and a settings-changed notification over RPC so pickers refresh without a button (sections 5, 6) | VSIX, Host, Contracts | | |
| 1.16 | Visual Studio integration-test project on the VS SDK test framework, run in the Experimental Instance on 17.14 and 18.x: package load, proposal source with IntelliCode and Copilot enabled, inline difference view and single-edit apply, host kill; CI gains a 17.14 job that installs the VSIX built by the 18.x job (section 15) | Tests, CI | 1.1, 1.7 | |
| 1.17 | Evaluation harness, completion slice: the first 100 of the 300 fill-in-the-middle cases across C#, TypeScript and Python from permissively licensed code in a private repository; exact match, edit similarity and latency; models pinned by digest; baseline run recorded (section 15) | Harness | 1.3 | |
| 1.18 | Performance suite v1 and Gate 1 evidence: scripted scenarios for every budget in the table above, the offline run extended for phase 1, the gate report (sections 13, 15) | Tests | all | |
| 1.19 | Design document and docs: decision log (IntelliCode requirement, Gitflow, contract 1.1), section 5 coexistence text updated with the spike findings, the shortcut audit that confirms the prefix is unbound in every Visual Studio scheme, Copilot and ReSharper (section 5) | Docs | | |

Nineteen items against the design's indicative eighteen. Work items 1.1 to 1.5 are the autocomplete wave, 1.6 to 1.11 the inline chat and mentions wave, 1.12 to 1.17 the polish and quality wave, and 1.18 closes the gate. Items without a dependency can start at once; 1.19 is done first by the lead so every brief points at a current design document.

## Decisions taken while planning phase 1

| Date | Decision | Why |
| --- | --- | --- |
| 2026-10-08 | Conversations stay in memory in phase 1; persistence arrives with the encrypted Local Store in phase 2 | Avoids building a throwaway store before SQLite3MultipleCiphers lands |
| 2026-10-08 | `@file` resolves against the solution or folder file list in phase 1 and switches to the index in phase 2 | Retrieval is a phase 2 deliverable; the mention grammar and picker do not change |
| 2026-10-08 | Two contract minor bumps are expected (inline edits, Outbound log read, `Summarise` task class); they are batched into one bump at the wave boundary | Keeps the version rule simple: majors must match, minors only add |
| 2026-10-08 | The 17.14 CI job installs the VSIX built by the 18.x job rather than building itself | The .NET 10 SDK needs MSBuild 18, so the 2022 runner cannot build the solution |
| 2026-10-09 | Inline edits of a small scope (at most `inlineEdit.wholeScopeMaxLines`, default 80) ask the model to rewrite the whole scope as plain code and stream it; search-and-replace blocks stay for larger scopes and as the fallback after one unusable retry. `/test` and `/explain` are unchanged | The block format makes the model copy the whole old scope into SEARCH before any new line can be shown: with `qwen2.5-coder:7b` the first preview text arrived at about 2.8 s median for "extract the validation into a method" against the Gate 1 target of 2 s p50. Rewriting the whole scope puts it at about 0.2 s. The design's edit format (section 8) still governs multi-file agent edits, where the old text is not already known to both sides |
| 2026-10-10 | **Lead's default, pending the product owner's confirmation:** `autocomplete.ordering` defaults to `pillionFirst` (also `competitorFirst` and `onDemand`, chosen in a once-per-machine info bar or on the new Autocomplete Options page). Against a competitor's queued proposal Pillion uses the "replace" countermeasure: re-request outside the broker while a competitor's proposal is displayed (type-through cache first), and only when Pillion has a suggestion for the same caret dismiss the competitor's with the user-Escape reason and trigger an explicit invocation that Pillion's source answers from what it kept; at most once per position, never when Pillion has nothing | The user installed Pillion to use it. Measured on 18.10 with Copilot and 17.14 with IntelliCode (spike FINDINGS, "Coexistence countermeasure"): Pillion's share of the slot rose from 90% to 96% (18.10) and from 88% to 95% (17.14) with the competitor still shown whenever Pillion had nothing; the "decline" alternative starved Copilot completely and left IntelliCode's broker answering nothing on 17.14; `TryUpdateProposal` cannot replace another source's proposal, and only the user-Escape dismissal clears the broker's queue, so the competitor records those proposals as dismissed by Escape |

## Decisions needed from the product owner during phase 1

| Decision | Needed by | Context |
| --- | --- | --- |
| Default of the ordering setting against Copilot and IntelliCode (Pillion first, competitor first, on demand) | 1.4 | The lead's suggestion is a user setting defaulting to Pillion first, because the user installed Pillion to use it |
| Which cloud provider and key to measure the cloud Fast and Balanced budgets with | 1.18 | Any OpenAI-compatible provider works today; 1.14 adds Anthropic, Gemini and Mistral |
| Whether to pull a 7B fill-in-the-middle model (`qwen2.5-coder:7b`, about 4.7 GB) for the GPU budget on the 8 GB reference machine | 1.18 | The 1.5B model covers the CPU budget; the 7B figure is a gate row only when the model is present |
| Where the private golden-set repository for the evaluation harness lives | 1.17 | Section 15 keeps golden sets out of the public repository |
| Confirmation of the shortcut prefix once the audit in 1.19 reports | 1.6 | The provisional prefix is Ctrl+Alt+Shift+P; phase 0 registered the chat chord under it on 18.10 without conflict |

## Carried over from Gate 0

- Cloud chat through the gate script with a stored key, and the elevated offline run; both fold into 1.18.
- A manual host-kill check while a reply is streaming.
- Set `develop` as the repository's default branch and delete the merged `phase0/*` branches and worktrees.

## Working agreement

- Gitflow: one `feature/<slug>` branch and one pull request per work item, branched from `develop`, merged with a merge commit once CI is green and the lead has reviewed it; `main` holds tagged releases only.
- Each pull request carries its unit and component tests and, for any in-process path, a Visual Studio integration test once 1.16 exists; nothing lands red.
- Every work item lands behind a feature flag (`features.autocomplete`, `features.inlineChat`, `features.mentions`, `features.outboundLogView`, and one per new adapter).
- Sub-agents do not change permissions, policy defaults or contracts without an entry in the decision log above or in the design document.
- The product owner is asked only for decisions and gate reviews; the gate review for phase 1 adds the feel of ghost text and the shortcut audit to the usual evidence.
