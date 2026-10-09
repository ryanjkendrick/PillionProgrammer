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
Providers/Anthropic/  Providers/Gemini/  Providers/Mistral/  Providers/AzureOpenAI/  The cloud adapters (no SDKs)
Providers/Cloud/   HTTP, SSE, error mapping and endpoint/proxy/CA plumbing shared by the four cloud adapters
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
  o-series, Claude, Gemini, DeepSeek, Llama, Mistral and embedding, audio and image families. Azure OpenAI is a separate
  adapter (below).

### Cloud adapters (`Providers/Anthropic`, `Gemini`, `Mistral`, `AzureOpenAI`)

Work item 1.14. Four adapters over `HttpClient` with no SDK, each in its own folder with its own feature flag (all on by
default, see the root README), sharing `Providers/Cloud` for the HTTP request loop, server-sent-event reader, error
mapping, endpoint resolution and the proxy and CA bundle handling (the same code as the OpenAI-compatible adapter's
`OpenAITransport`). Chat streams from every one of them and every chunk names the model and provider. Tool calls are not
sent or read in phase 1.

| Kind | Chat | FIM | Embeddings | Default endpoint | Credential | Flag |
| --- | --- | --- | --- | --- | --- | --- |
| `anthropic` | `POST /v1/messages` (SSE) | No (chat-framed only) | No | `https://api.anthropic.com` | `x-api-key` | `anthropicProvider` |
| `gemini` | `POST /v1beta/models/{model}:streamGenerateContent?alt=sse` | No (chat-framed only) | Not implemented (TODO below) | `https://generativelanguage.googleapis.com` | `x-goog-api-key` | `geminiProvider` |
| `mistral` | `POST /v1/chat/completions` (OpenAI-shaped SSE) | `POST /v1/fim/completions` for Codestral | Not implemented | `https://api.mistral.ai/v1` | `Authorization: Bearer` | `mistralProvider` |
| `azure-openai` | `POST /openai/deployments/{deployment}/chat/completions?api-version=` (SSE) | No (chat-framed only) | Not implemented | none: `https://<resource>.openai.azure.com` | `api-key`, or an Entra ID bearer token | `azureOpenAIProvider` |

Common to all four:

- `RequiresApiKey` is true, so a provider without a key in `ISecretStore` (secret name = provider id) is hidden from
  routing with "No API key is stored for this provider". Azure OpenAI is hidden only when neither a key nor an
  `ITokenCredentialProvider` exists. The key is read for every request and never kept; a key with control characters is
  refused before anything is sent; redirects are never followed.
- Network options come from the provider entry exactly as for the OpenAI-compatible adapter: `"proxy"` (`"system"`,
  `"none"` or a URL) and `"caBundle"` (a PEM file; a missing or empty file hides the provider with a message). A custom
  `endpoint` works for enterprise gateways and keeps its path (`https://gw.example/anthropic` sends to
  `https://gw.example/anthropic/v1/messages`; a bare Mistral host gets `/v1`).
- Errors: 401 and 403 (and Gemini sending 400 for an invalid key) are `ProviderUnavailable` with a message that the
  credential was rejected, never repeating the server text; a 404 on a request that names a model is `ModelNotInstalled`;
  context-length wording (`prompt is too long`, `input token count ... exceeds`, `maximum context length`, HTTP 413) is
  `ContextOverflow`; 429, 503, 529 and other 5xx are retryable `ProviderException`s carrying the status, so the gateway
  retry policy and circuit breaker apply; billing problems (HTTP 402, "credit balance is too low") are not retried;
  connection and TLS failures are `ProviderUnavailable`. Error events inside a stream (Anthropic `event: error`, Google
  and OpenAI-style `error` payloads, Mistral `"object":"error"`) are mapped the same way, with the status the error has
  outside a stream (`overloaded_error` is 529, `rate_limit_error` 429, `UNAVAILABLE` 503).
- Test connection (`ProbeAsync`; `ProbeDetailedAsync` returns a `CloudProbeResult` with reachable, authenticated and the
  model count) calls the model list with the credential under a 15 second limit. A rejected credential is reachable but
  not usable.
- Catalog: each kind has Fast, Balanced and Frontier defaults and family descriptors (context window, maximum output,
  vision, tools, prompt caching, reasoning effort, cost per million tokens) in `Catalog/catalog.json`, each with a `note`
  naming the page it came from (October 2026). They are defaults: the Anthropic, Gemini and Mistral adapters replace the
  window, output limit and capabilities with what the provider model list reports, and `routing.profiles` or
  `providers[].models` override the model choice.

#### Anthropic

- `anthropic-version` is `2023-06-01`, the only version on the versioning page; `"anthropicVersion"` overrides it.
- System messages go in the top-level `system` field. `max_tokens` is required by the API: the request limit, else 8,192
  (capped by the model maximum output); thinking counts toward it. Sampling parameters are never sent, because Claude
  Opus 4.7 and later reject non-default `temperature`, `top_p` and `top_k`.
- Reasoning effort: `ChatOptions.Reasoning.Effort` maps to `output_config.effort` (`Low` and `None` to `low`, `Medium`,
  `High`, `ExtraHigh` to `xhigh`) when the catalog family, or the model list (`capabilities.effort.supported`), says the
  model supports it. Adaptive thinking stays on; thinking blocks are read and dropped, only text is streamed. The manual
  `thinking.budget_tokens` mode is not sent, because current models reject it.
- Usage: input tokens are `input_tokens` plus cache creation and cache read tokens from `message_start`; output tokens
  come from `message_delta`. Stop reasons map to finish reasons (`max_tokens` and `model_context_window_exceeded` to
  Length, `refusal` to ContentFilter).
- Models: `GET /v1/models?limit=1000`, following `has_more` and `last_id`, reading `max_input_tokens`, `max_tokens`,
  `capabilities.image_input` and `capabilities.effort`.
- Catalog: Fast `claude-haiku-5-5`, Balanced `claude-sonnet-5-5`, Frontier `claude-opus-5-5` (the models overview names
  Opus 5.5 as the starting point and Fable 5.1, `claude-fable-5-1`, for the most demanding work; pin it with
  `routing.profiles` to use it).

#### Google Gemini

- Requests go to `/{apiVersion}/models/{model}:streamGenerateContent?alt=sse`, `apiVersion` defaulting to `v1beta`
  (`"apiVersion"` overrides it). The key travels in the `x-goog-api-key` header, never in the URL. `contents` use the roles
  `user` and `model`; system messages become `systemInstruction`; `generationConfig` carries `maxOutputTokens`,
  `temperature` and up to five `stopSequences`. Model names with the `models/` prefix are accepted.
- Reasoning effort maps to `generationConfig.thinkingConfig.thinkingLevel` (`low`, `medium`, `high`; `None` sends `low`,
  `minimal` is never sent because not every model accepts it) for the Gemini 3 families the catalog marks. Gemini 2.5 uses
  a thinking budget instead and is left at its default. Thought parts are dropped; thinking tokens count as output tokens
  in the usage.
- Models: `GET /{apiVersion}/models?pageSize=1000`, following `nextPageToken`, reading `inputTokenLimit`,
  `outputTokenLimit` and `supportedGenerationMethods` (`generateContent` is chat, `embedContent` is an embedding model).
- A blocked prompt (`promptFeedback.blockReason`) ends the stream with a ContentFilter finish and no text.
- TODO: embeddings through `embedContent` (and `batchEmbedContents`) are not implemented. The gateway has no embedding
  path on `IModelProvider` yet; embedding models are listed with the Embeddings capability so the router and the context
  engine can find them once that path exists.
- Catalog: Fast `gemini-3.5-flash-lite`, Balanced `gemini-3.8-flash`, Frontier `gemini-3.1-pro-preview` (a preview
  model: the Gemini models page lists no stable 3.x Pro model).

#### Mistral

- Chat reuses the OpenAI-compatible request body and chunk parser as internal helpers, but not its transport, errors or
  endpoint rules. `stream_options` is not sent (the reference does not document it; the final chunk carries `usage`).
  Models: `GET /v1/models`, accepting `{"data":[...]}` or a bare array, reading `max_context_length` and `capabilities`
  (`completion_chat`, `completion_fim`, `vision`, `function_calling`).
- FIM: `GetFimClient` returns a client for models whose capabilities say FIM (the catalog marks the `codestral` family;
  the model list `completion_fim` wins), and `null` for other models. It posts `prompt` (the prefix), `suffix`,
  `max_tokens`, `temperature`, `stop` and `stream: true` to `/v1/fim/completions` and streams `choices[0].delta.content`
  (also accepting `message.content` and `text`). Mistral applies the Codestral template itself, so the prefix and suffix
  are sent raw.
- A Codestral-specific host (`codestral.mistral.ai`) needs no special support: set `endpoint` to it.
- Catalog: Fast `codestral-latest` (the cloud autocomplete model; it also chats), Balanced `mistral-small-latest`,
  Frontier `mistral-medium-3-5`.

#### Azure OpenAI

- `endpoint` is required (`https://<resource>.openai.azure.com`) and the entry `models` are deployment names. Listing
  returns them without calling the network, with capabilities looked up by deployment name; a deployment with a custom
  name takes its capabilities from `"modelFamilies": { "prod-chat": "gpt-4o" }` in the entry. There are no catalog
  defaults for Fast, Balanced and Frontier: pin them with `routing.profiles` (`azure/prod-chat`) or let the router take
  the first listed deployment.
- `api-version` is `2024-10-21`, the latest generally available data plane inference version on Microsoft Learn;
  `"apiVersion"` overrides it. (The newer `/openai/v1/` surface needs no `api-version` and is not used yet.) The body is
  the official OpenAI body: `max_completion_tokens`, and no `temperature` or `stop` for reasoning-named deployments. The
  deployment is in the URL, so the body has no `model`.
- Authentication, `"authentication"` in the entry: `"auto"` (default) sends the stored key as `api-key`, else an Entra ID
  bearer token when the host registered an `ITokenCredentialProvider`; `"apiKey"` and `"entra"` force one. The token scope
  is `https://cognitiveservices.azure.com/.default` (`"tokenScope"` overrides it). The adapter has no `Azure.Identity`
  dependency: the host implements `ITokenCredentialProvider` over a `TokenCredential` and registers it, and
  `StaticTokenCredentialProvider` serves tests. A failing provider is reported as "could not get a Microsoft Entra ID
  token" without its message.
- Test connection calls `GET /openai/models?api-version=`; a 404 or 405 means reachable but unverified.

## Extension points for later work items

| Interface | Default | Supplied by |
| --- | --- | --- |
| `IProviderReachability` | `AlwaysReachable` | Pillion Host: `ConnectivityProviderReachability` over the connectivity monitor (0.10) |
| `IProviderHealthSink` | `NoProviderHealthSink` | Pillion Host: `ConnectivityProviderHealthSink`; each counted failure and success goes to the monitor's `ReportFailure` / `ReportSuccess` (0.10) |
| `IGatewaySettingsSource` | `SettingsStoreGatewaySettingsSource` (no policies) | Pillion Host: `ConnectivityGatewaySettingsSource`, the monitor's effective settings, so Local-only has one source |
| `IProgressSink` | `NoProgressSink` | Chat window (0.11) |
| `ICostGovernor` | `NoCostCaps` | Cost governance (phase 1) |
| `IOutboundLog` | `InMemoryOutboundLog` | Persistence with the audit trail |
| `IModelProviderFactory` | Ollama, OpenAI-compatible, OpenAI, Anthropic, Gemini, Mistral, Azure OpenAI | In-process plug-ins are out of version 1 |
| `ITokenCredentialProvider` | none (Azure OpenAI then uses keys) | Pillion Host: an `Azure.Identity` `TokenCredential` for Entra ID (a later item) |

## Tests

`tests/Pillion.ModelGateway.Tests`: routing table, Auto policy, circuit breaker, concurrency, timeouts, retries, cache,
redaction round trip, Outbound log, catalog and templates, the Ollama adapter against recorded fixtures
(`Fixtures/Ollama`) and the OpenAI-compatible adapter against fixtures in the documented wire shape
(`Fixtures/OpenAICompatible`), including real-socket tests for the proxy, streaming and a TLS handshake under a custom CA.
The Anthropic, Gemini, Mistral and Azure OpenAI adapters have the same kind of tests (`Providers/<Name>`, fixtures in
`Fixtures/<Name>`, written by hand in each API documented wire shape): model listing, a streamed chat with usage,
cancellation mid-stream, 401, a missing model, a context-length error, 429 and 529 or 503, connection refused, an error
event inside a stream, Test connection, Mistral FIM, Azure key and Entra ID paths and the deployment URL, and a
gateway-level test per provider (routed with the cloud switch on and a key, hidden without one, skipped with the
feature flag off). Their `Category=Live` classes run only when `PILLION_TEST_ANTHROPIC_KEY`, `PILLION_TEST_GEMINI_KEY`,
`PILLION_TEST_MISTRAL_KEY`, or `PILLION_TEST_AZURE_OPENAI_ENDPOINT` with `PILLION_TEST_AZURE_OPENAI_KEY` (and
`PILLION_TEST_AZURE_OPENAI_DEPLOYMENT` for the chat test) are set; no test reads a key from anywhere else.
Ollama tests with `Category=Live` talk to `http://localhost:11434` and skip themselves when it does not
answer; the OpenAI-compatible live tests run only when `PILLION_TEST_OPENAI_COMPAT_ENDPOINT` is set (optionally
`PILLION_TEST_OPENAI_COMPAT_KEY` and `PILLION_TEST_OPENAI_COMPAT_MODEL`). Run only live tests with `dotnet test --filter Category=Live`, or exclude them with `--filter Category!=Live`.
