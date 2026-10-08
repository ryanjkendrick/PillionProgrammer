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
| 0.3 | Host skeleton: named-pipe JSON-RPC server, handshake and token check, state machine, structured rolling logs, graceful shutdown (sections 4, 10, 12) | Host | 0.2 | In progress |
| 0.4 | VSIX host supervisor and RPC client: signed-binary check, Job Object, anonymous-pipe token, restart backoff, Failed state, status bar (section 4) | VSIX | 0.2 | In progress |
| 0.5 | Settings and secrets: versioned `settings.json` with atomic writes and precedence, `ISettingsService`, Windows Credential Manager store (sections 6, 10) | Common, Host | 0.2 | In progress |
| 0.6 | Secret scanner and redaction round trip: patterns, structured placeholders, per-request restore, rejection of unresolved placeholders (section 12) | Common | 0.1 | In progress |
| 0.7 | Model Gateway core: Microsoft.Extensions.AI clients, FIM interface, capability descriptors, model catalog data, routing order, circuit breaker, concurrency and timeouts, redaction middleware, `IModelService` (section 6) | ModelGateway, Host | 0.3, 0.5, 0.6 | |
| 0.8 | Ollama adapter: native API chat and FIM with `suffix`, `num_ctx` and `keep_alive` per profile, minimum version check, model listing (sections 6, 11) | ModelGateway | 0.7 | |
| 0.9 | OpenAI-compatible cloud adapter: streaming chat, custom endpoint, proxy and CA bundle, key from Credential Manager, Test connection (section 6) | ModelGateway | 0.7 | |
| 0.10 | Connectivity and Local-only mode: per-provider probes, network-change events, four states, policy file `localOnly` (section 11) | Common, Host | 0.7 | |
| 0.11 | Chat tool window and Options pages: model picker, streaming replies naming the model, Outbound log entries, Providers and keys page, Local-only switch (section 5) | VSIX | 0.4, 0.7 | |
| 0.12 | Proposals API spike on 17.14 and 18.x with findings and the fallback assessment (section 5) | Spike | | Viable on 18.10, PR 2; 17.14 run pending VS 2022 |
| 0.13 | Gate 0 evidence: host-kill integration test, offline run script, gate report | Tests | all | |

Work item 0.13 was added when the plan was laid out; the design doc's count of twelve is indicative.

## Decisions taken while planning phase 0

| Date | Decision | Why |
| --- | --- | --- |
| 2026-10-08 | The one cloud provider in phase 0 is the OpenAI-compatible adapter | It covers OpenAI, Azure OpenAI, OpenRouter and enterprise gateways as well as local OpenAI-compatible servers, so one adapter serves the most configurations. The Anthropic, Gemini and Mistral adapters follow in phase 1 |
| 2026-10-08 | The handshake lives on a fifth interface, `IHostLifecycleService`, beside the four VSIX-to-host services in section 4 | Keeps lifecycle calls out of the feature services; the contract version rule is unchanged |
| 2026-10-08 | Phase 0 has thirteen work items; Gate 0 evidence is its own item | Separates gate evidence from feature work so the gate review has one place to look |
| 2026-10-08 | The host and tests use MessagePack 2.5.307; the VSIX binds to the 2.5.192 that Visual Studio 17.14 ships | Open advisories on 2.5.192; central package versions apply only to direct references, so the VSIX keeps the SDK's transitive version and the wire format is unchanged |
| 2026-10-08 | Ghost text builds on the Proposals API behind a thin adapter compiled against the 17.14 SDK | The spike (work item 0.12) proved multi-line, right-of-caret and virtual-space proposals on 18.10; 18.x marks several members as changing, so the adapter isolates them. Pending: the 17.14 run and the Copilot ordering policy, both for the product owner |

## Working agreement

- One branch and one pull request per work item, named `phase0/<slug>`; squash-merged to main once CI is green and the lead has reviewed it.
- Each pull request carries its unit and component tests; nothing lands red.
- Sub-agents do not change permissions, policy defaults or contracts without an entry in the decision log above or in the design document.
- The product owner is asked only for decisions and gate reviews.
