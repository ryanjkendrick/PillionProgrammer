# Phase 0: Foundations

Phase 0 takes the product from an empty repository to a host and extension shell that can chat with a local Ollama model and one cloud model, survive the host being killed, and prove the Proposals API on both supported Visual Studio versions. It closes at Gate 0.

The design document is the specification: https://claude.ai/code/artifact/02f3ee51-8712-47d2-89af-3c7f6f1559fb (a Markdown export lives in [design-document.md](design-document.md)). Section numbers below refer to it.

## Gate 0

| Criterion | Evidence |
| --- | --- |
| Chat reply from an Ollama model and from a cloud model, each naming the model that answered | Screenshots from the Chat window plus the Outbound log entries |
| Killing the host never hangs Visual Studio; the supervisor restarts it with backoff and shows Failed after three crashes in five minutes | Visual Studio integration test plus a manual check |
| Proposals API spike green on VS 2022 17.14 and VS 2026 18.x | `spikes/ProposalsSpike/FINDINGS.md` |
| Secret scanner round trip: no placeholder reaches disk, originals restored in model output | Unit tests |
| Local-only mode routes nothing to a cloud provider and runs no probes | Component test with the network adapter disabled |
| Every work item behind a feature flag, unit and component tests green, main builds on CI on 17.14 and 18.x | CI run on main |

## Work items

| Id | Work item | Component | Depends on | Status |
| --- | --- | --- | --- | --- |
| 0.1 | Repository and solution scaffold: projects, central package versions, build props, CI, README | All | | Done, PR 1 |
| 0.2 | Contracts assembly: process boundary interfaces, DTOs, error taxonomy, contract version (section 4) | Contracts | 0.1 | Done, PR 1 |
| 0.3 | Host skeleton: named-pipe JSON-RPC server, handshake and token check, state machine, structured rolling logs, graceful shutdown (sections 4, 10, 12) | Host | 0.2 | Done, PR 6 |
| 0.4 | VSIX host supervisor and RPC client: signed-binary check, Job Object, anonymous-pipe token, restart backoff, Failed state, status bar (section 4) | VSIX | 0.2 | Done, PR 7 |
| 0.5 | Settings and secrets: versioned `settings.json` with atomic writes and precedence, `ISettingsService`, Windows Credential Manager store (sections 6, 10) | Common, Host | 0.2 | Done, PR 4 |
| 0.6 | Secret scanner and redaction round trip: patterns, structured placeholders, per-request restore, rejection of unresolved placeholders (section 12) | Common | 0.1 | Done, PR 5 |
| 0.7 | Model Gateway core: Microsoft.Extensions.AI clients, FIM interface, capability descriptors, model catalog data, routing order, circuit breaker, concurrency and timeouts, redaction middleware, `IModelService` (section 6) | ModelGateway, Host | 0.3, 0.5, 0.6 | Done, PR 10 |
| 0.8 | Ollama adapter: native API chat and FIM with `suffix`, `num_ctx` and `keep_alive` per profile, minimum version check, model listing (sections 6, 11) | ModelGateway | 0.7 | Done, PR 10 |
| 0.9 | OpenAI-compatible cloud adapter: streaming chat, custom endpoint, proxy and CA bundle, key from Credential Manager, Test connection (section 6) | ModelGateway | 0.7 | Done, PR 12 |
| 0.10 | Connectivity and Local-only mode: per-provider probes, network-change events, four states, policy file `localOnly` (section 11) | Common, Host | 0.7 | Done, PR 9 |
| 0.11 | Chat tool window and Options pages: model picker, streaming replies naming the model, Outbound log entries, Providers and keys page, Local-only switch (section 5) | VSIX | 0.4, 0.7 | Done, PR 11 |
| 0.12 | Proposals API spike on 17.14 and 18.x with findings and the fallback assessment (section 5) | Spike | | Viable on 18.10, PR 2; 17.14 run pending VS 2022 |
| 0.13 | Gate 0 evidence: host-kill integration test, offline run script, gate report | Tests | all | Done, PR 14; Gate 0 failures fixed in PR 15 (report: [gate-0-report.md](gate-0-report.md)) |

Work item 0.13 was added when the plan was laid out; the design doc's count of twelve is indicative.

## Decisions taken while planning phase 0

| Date | Decision | Why |
| --- | --- | --- |
| 2026-10-08 | The one cloud provider in phase 0 is the OpenAI-compatible adapter | It covers OpenAI, Azure OpenAI, OpenRouter and enterprise gateways as well as local OpenAI-compatible servers, so one adapter serves the most configurations. The Anthropic, Gemini and Mistral adapters follow in phase 1 |
| 2026-10-08 | The handshake lives on a fifth interface, `IHostLifecycleService`, beside the four VSIX-to-host services in section 4 | Keeps lifecycle calls out of the feature services; the contract version rule is unchanged |
| 2026-10-08 | Phase 0 has thirteen work items; Gate 0 evidence is its own item | Separates gate evidence from feature work so the gate review has one place to look |
| 2026-10-08 | The host and tests use MessagePack 2.5.307; the VSIX binds to the 2.5.192 that Visual Studio 17.14 ships | Open advisories on 2.5.192; central package versions apply only to direct references, so the VSIX keeps the SDK's transitive version and the wire format is unchanged |
| 2026-10-08 | The wire uses StreamJsonRpc's length-header message handler with the MessagePack formatter; the VSIX cannot pass the current-user-only pipe option on .NET Framework, so it verifies the pipe server's process id and owner before sending the token | The header-delimited handler only supports text formatters in StreamJsonRpc 2.22; found by work items 0.3 and 0.4 |
| 2026-10-08 | Ghost text builds on the Proposals API behind a thin adapter compiled against the 17.14 SDK | The spike (work item 0.12) proved multi-line, right-of-caret and virtual-space proposals on 18.10; 18.x marks several members as changing, so the adapter isolates them. Pending: the 17.14 run and the Copilot ordering policy, both for the product owner |
| 2026-10-08 | Contract 1.1: `ChatChunk` and `CompletionChunk` gain `ModelId`, `ProviderId`, `PromptTokens`, `CompletionTokens` and `Error` (orders 3-7); `ModelDescriptor` gains `ModelId`, `IsAvailable`, `ContextWindow` and `Digest` (orders 5-8) and its `Id` becomes `providerId/modelId`; the `ConnectivityState` enum is added for the `connectivityChanged` notification | Additive only, so a minor bump: a 1.0 peer is still accepted and ignores the new fields. The chat window needs the answering model, partial-stream errors and availability; the changelog is in `src/Pillion.Contracts/README.md` |
| 2026-10-08 | The minimum Ollama version is 0.6.4, checked before the first request and by Test connection rather than at start-up | 0.6.4 is the first release whose `api/show` returns `capabilities`, which the gateway uses to tell chat, FIM, vision and embedding models apart; checking lazily lets Ollama start after Pillion |
| 2026-10-08 | Connectivity reads Online when no cloud provider is configured, and a cloud provider that has not been probed yet counts as reachable | Offline with nothing to reach would grey out or warn about local-only setups for no reason, and treating unprobed providers as down would block the first request after start-up or a settings change; probes and three circuit-breaker failures correct the state within seconds |
| 2026-10-08 | The Model Gateway reads its settings through the connectivity monitor's settings provider, and the circuit breaker reports every counted failure and success to the monitor | One source of truth for Local-only (user setting or repository policy) between the router and the monitor; breaker evidence and probes share one reachability view, as section 11 describes |
| 2026-10-08 | Feature flags: a `features` section in `settings.json` (chat, modelGateway, connectivityProbes, cloudProviders, ollamaProvider, openAICompatibleProvider; all default on), a `disabledFeatures` key in `.pillion/policy.json` (unioned, can only switch off), and a read-only virtual setting `effective.features` on `ISettingsService.GetAsync` | The Gate 0 criterion "every work item behind a feature flag" and the design rule "a feature flag gates every agent capability, so a policy file can switch individual tools off". No version bump: missing sections fill with defaults. The extension reads the effective flags from the host because only the host knows the repository policies |
| 2026-10-08 | The pipe owner check in the extension accepts the user SID or the token owner SID | An elevated token owns the objects it creates as BUILTIN\Administrators; .NET's own `CurrentUserOnly` uses the token owner on both ends, so the extension follows it. Any other owner is still refused |

## Working agreement

- One branch and one pull request per work item, named `phase0/<slug>`; squash-merged to main once CI is green and the lead has reviewed it.
- Each pull request carries its unit and component tests; nothing lands red.
- Sub-agents do not change permissions, policy defaults or contracts without an entry in the decision log above or in the design document.
- The product owner is asked only for decisions and gate reviews.
