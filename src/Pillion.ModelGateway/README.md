# Pillion.ModelGateway

The only component that talks to a language model (design document section 6). Pillion Host registers it with
`services.AddPillionModelGateway()` and serves it to the VSIX as `IModelService` (`src/Pillion.Host/Models/ModelRpcService.cs`).

```
Abstractions/   IModelProvider, IModelProviderFactory, IFimClient + FimRequest, ModelCapabilityDescriptor, ProviderException
Catalog/        catalog.json (data) and ModelCatalog: default model per profile per provider kind, capabilities per family
Routing/        IModelRouter / ModelRouter, RoutingRequest, RoutedModel, IProviderReachability, ICostGovernor
Resilience/     CircuitBreakerRegistry + IProviderHealthSink, ConcurrencyLimiter, IProgressSink
Redaction/      RedactingChatClient, RedactingFimClient, StreamingPlaceholderRestorer
Caching/        AutocompleteCache (LRU, 256 entries)
Outbound/       IOutboundLog / InMemoryOutboundLog (ring buffer of 500), TokenEstimator
Templates/      system-prompts.json, fim-formats.json (data) and PromptTemplates
Providers/      ProviderRegistry (builds providers from settings), IGatewaySettingsSource, ModelInventory
Providers/Ollama/  The native Ollama adapter
ModelGateway.cs    IModelGateway: ChatAsync, CompleteAsync, ListModelsAsync, TestProviderAsync
StreamAttempt.cs   One request against one model: breaker, concurrency slot, retries, timeouts, Outbound record
```

## A request, end to end

1. **Route.** `ModelRouter` picks a profile: the request's, else `routing.tasks`, else the Auto policy (Autocomplete:
   Local-fast when a local FIM model is installed, else cloud Fast only with the autocomplete cloud switch on; Chat and
   Inline edit: Balanced online, Local-strong offline; Agent and Vision: Frontier online, Local-strong offline;
   Embedding: always local). "Online" means a cloud provider is allowed for the task class and reachable. The profile
   becomes a fallback chain (the next profile in the same locality, then local) and each profile becomes concrete
   candidates: `routing.profiles` (bare or `provider/model`), else the catalog default for the provider kind, else the
   first allowed (`providers[].models`) or installed model that can do the job. Candidates are filtered in the design's
   order: policy (Local-only), cloud switches, connectivity (`IProviderReachability` plus open circuits), capability
   (FIM, vision, embeddings, context size, installed), cost caps. An explicit model id on the request wins over all of
   that except policy and the cloud switches; the Auto chain stays behind it as fallbacks.
2. **Run.** For each candidate until one starts answering: take a circuit-breaker permit, wait for a concurrency slot
   (autocomplete 1, chat 2, agent 1 per provider), redact (cloud providers always; local only with
   `privacy.redactForLocalProviders`), open the stream and wait for the first token. Retries with jittered exponential
   backoff on 429 and 5xx (never for autocomplete). Cloud chat must start within 60 s; cloud autocomplete must finish
   within 3 s or is dropped silently (an empty stream); local requests never time out, they report
   `WaitingForLocalModel` after 2 s and `SlowerThanExpected` after 120 s scaled by prompt size to `IProgressSink`.
   A failure before the first token moves to the next candidate; a failure after it ends the stream with
   `Error` on the final chunk.
3. **Stream.** Every chunk carries `ModelId` and `ProviderId`; the final chunk has `IsFinal`, token counts and any
   error. Placeholders are restored as they stream: text from a `<` that could start `<redacted:` is held until the
   closing `>`.
4. **Record.** One `OutboundRecord` per attempt (time, provider, model, task class, locality, estimated prompt tokens,
   completion tokens, redaction count, duration, outcome) plus one structured log line; never prompt text.

Autocomplete checks the cache first (key: provider/model, SHA-256 of the last 2,048 prefix characters and of the
first 512 suffix characters); a hit is one final chunk and no outbound request.

## Providers

`ProviderRegistry` builds one `IModelProvider` per enabled `providers` entry with the `IModelProviderFactory` for its
kind (the last registered factory for a kind wins). Unknown kinds are skipped with a warning; a provider whose factory
needs an API key and finds none in `ISecretStore` under the provider id is hidden from routing. Providers are rebuilt
when settings change and reused when their entry did not.

### Ollama (`Providers/Ollama`)

- Native API with `HttpClient`: `api/version`, `api/tags` + `api/show` (capabilities and `model_info.*.context_length`,
  cached per digest), `api/chat` (NDJSON), `api/generate` with `suffix` and `raw: false` for FIM.
- Minimum version 0.6.4, the first release whose `api/show` returns `capabilities`. It is checked before the first
  request and by Test connection, not in the constructor, so Ollama may start after Pillion.
- `num_ctx` is fixed per profile: Local-fast 4,096, Local-strong 8,192 (others 8,192). Override per provider with
  `"numCtx": { "localFast": 4096, "localStrong": 16384 }` in its `providers` entry.
- `keep_alive`: `-1` for FIM requests and for chat on a model used for autocomplete (the routed Local-fast model), `30m`
  for other chat.
- Errors: 404 or "model ... not found" is `ModelNotInstalled`; connection failures are `ProviderUnavailable`; context
  length errors are `ContextOverflow`; "does not support insert" is `ProviderUnavailable` without counting against the
  circuit.
- A loopback endpoint is local; any other host is treated as a cloud boundary (redacted, blocked by Local-only).

## Extension points for later work items

| Interface | Default | Supplied by |
| --- | --- | --- |
| `IProviderReachability` | `AlwaysReachable` | Connectivity (0.10) |
| `IProviderHealthSink` | `NoProviderHealthSink` | Connectivity (0.10) |
| `IProgressSink` | `NoProgressSink` | Chat window (0.11) |
| `ICostGovernor` | `NoCostCaps` | Cost governance (phase 1) |
| `IOutboundLog` | `InMemoryOutboundLog` | Persistence with the audit trail |
| `IModelProviderFactory` | Ollama | OpenAI-compatible adapter (0.9) |

## Tests

`tests/Pillion.ModelGateway.Tests`: routing table, Auto policy, circuit breaker, concurrency, timeouts, retries, cache,
redaction round trip, Outbound log, catalog and templates, and the Ollama adapter against recorded fixtures
(`Fixtures/Ollama`). Tests with `Category=Live` talk to `http://localhost:11434` and skip themselves when it does not
answer; run only them with `dotnet test --filter Category=Live`, or exclude them with `--filter Category!=Live`.
