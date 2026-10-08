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
Providers/OpenAICompatible/  The OpenAI-compatible adapter (OpenAI, LM Studio, llama.cpp, vLLM, OpenRouter, gateways)
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

### OpenAI-compatible (`Providers/OpenAICompatible`)

Kinds `openai-compatible` and `openai`: the `/v1` protocol with `HttpClient` (no SDK), identical for OpenAI, OpenRouter,
LM Studio, the llama.cpp server, vLLM and enterprise gateways.

- Endpoint: the base URL including the version segment, for example `https://openrouter.ai/api/v1`. A bare host
  (`http://localhost:1234`) gets `/v1`. `openai` defaults to `https://api.openai.com/v1`; `openai-compatible` has no
  default and is hidden with a message when the endpoint is missing. Redirects are never followed (they could carry the
  key to another host).
- Models: `GET /models`; a 404, 405 or 501 falls back to `providers[].models`, then to the catalog defaults for the kind.
  Capabilities come from the catalog by model id; a `context_length`, `max_model_len` or `context_window` in the listing
  overrides the context window. Duplicate ids are dropped.
- Chat: `POST /chat/completions` with `stream: true` and `stream_options.include_usage`, parsed as server-sent events
  (`data:` lines, comments ignored, `[DONE]` ends the stream). Usage is read from the final chunk. A server that rejects
  `stream_options` is asked again without it (and remembered); a server that ignores `stream` and returns one JSON
  document is handled. Tool calls are not sent or read in phase 0. `max_tokens` is sent for compatible servers and
  `max_completion_tokens` for kind `openai`, which also drops `temperature` and `stop` for reasoning models (`o1`, `o3`,
  `o4`, `gpt-5`, `gpt-6` prefixes).
- Key: `Authorization: Bearer`, read from `ISecretStore` (secret name = provider id) for every request and never kept in
  a field. `RequiresApiKey` is true unless the endpoint host is a loopback address; a key is still sent to a loopback
  server if one is stored. A key containing control characters is refused before anything is sent. A warning is logged
  once when a key goes over plain `http` to a non-loopback host.
- Network options, read from the provider's `providers` entry (kept in the settings model's extension data):

  | Property | Meaning |
  | --- | --- |
  | `"proxy"` | `"system"` (default), `"none"`, or an `http(s)` proxy URL. Proxies get the signed-in user's Windows credentials. |
  | `"caBundle"` | Path of a PEM file with extra root certificates. Applied through `ServerCertificateCustomValidationCallback`: a certificate the platform trusts is accepted; if the only problem is an untrusted chain, the chain is rebuilt with the bundle as the sole trust anchors (revocation is not checked on that pass, because private CAs rarely publish a reachable CRL). A name mismatch, an expired certificate or any other error is still rejected. A missing or empty file hides the provider with a message. |
  | `"supportsFim"` | `true` when a local server implements `POST /v1/completions` with `suffix` (see FIM). |

- FIM: chat-framed only for cloud, so `GetFimClient` returns `null` for any non-loopback endpoint. A loopback server that
  sets `"supportsFim": true` gets a client that posts `prompt`, `suffix`, `max_tokens`, `stop` and `temperature` to
  `/completions` and streams `choices[0].text`; the server applies the model's FIM template. Only models whose catalog
  family has FIM (the Qwen2.5-Coder family today) are listed as FIM-capable. The flag is a declaration; the adapter does
  not probe `/completions`, because a probe would cost a generation.
- Errors: 401 and 403 are `ProviderUnavailable` with a message that the key was rejected (the server's text is never
  repeated, because some servers echo a masked key); a 404 whose body names a model, or code `model_not_found`, is
  `ModelNotInstalled`; any other 404 points at the endpoint path; context-length wording (OpenAI, vLLM, llama.cpp) is
  `ContextOverflow`; 429 and 5xx are `ProviderException`s carrying the status, which the gateway retries; connection and
  TLS failures are `ProviderUnavailable` (a TLS failure suggests `caBundle`). Error events inside a stream are mapped the
  same way.
- Test connection: `GET /models` with the key (15 s limit). `ProbeDetailedAsync` reports reachable, authenticated
  (`true` accepted, `false` rejected or required, `null` none sent and none needed), the `Server` header and the model
  count; `ProbeAsync` folds that into `ProviderProbeResult`, with a rejected key counting as not usable.
- Catalog: `openai` and `openai-compatible` map Fast, Balanced and Frontier to OpenAI's current ids (`gpt-6-luna`,
  `gpt-6.1-sol`, `gpt-6-astra`); override with `routing.profiles` for gateways with other ids. Families cover the GPT,
  o-series, Claude, Gemini, DeepSeek, Llama, Mistral and embedding, audio and image families. Azure OpenAI is not
  implemented (phase 1).

## Extension points for later work items

| Interface | Default | Supplied by |
| --- | --- | --- |
| `IProviderReachability` | `AlwaysReachable` | Connectivity (0.10) |
| `IProviderHealthSink` | `NoProviderHealthSink` | Connectivity (0.10) |
| `IProgressSink` | `NoProgressSink` | Chat window (0.11) |
| `ICostGovernor` | `NoCostCaps` | Cost governance (phase 1) |
| `IOutboundLog` | `InMemoryOutboundLog` | Persistence with the audit trail |
| `IModelProviderFactory` | Ollama, OpenAI-compatible, OpenAI | Anthropic, Gemini, Mistral, Azure OpenAI (phase 1) |

## Tests

`tests/Pillion.ModelGateway.Tests`: routing table, Auto policy, circuit breaker, concurrency, timeouts, retries, cache,
redaction round trip, Outbound log, catalog and templates, the Ollama adapter against recorded fixtures
(`Fixtures/Ollama`) and the OpenAI-compatible adapter against fixtures in the documented wire shape
(`Fixtures/OpenAICompatible`), including real-socket tests for the proxy, streaming and a TLS handshake under a custom CA.
Ollama tests with `Category=Live` talk to `http://localhost:11434` and skip themselves when it does not
answer; the OpenAI-compatible live tests run only when `PILLION_TEST_OPENAI_COMPAT_ENDPOINT` is set (optionally
`PILLION_TEST_OPENAI_COMPAT_KEY` and `PILLION_TEST_OPENAI_COMPAT_MODEL`). Run only live tests with `dotnet test --filter Category=Live`, or exclude them with `--filter Category!=Live`.
