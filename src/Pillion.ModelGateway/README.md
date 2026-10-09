# Pillion.ModelGateway

The only component that talks to a language model (design document section 6). Pillion Host registers it with
`services.AddPillionModelGateway()` and serves it to the VSIX as `IModelService` (`src/Pillion.Host/Models/ModelRpcService.cs`).

```
Abstractions/   IModelProvider, IModelProviderFactory, IFimClient + FimRequest, ModelCapabilityDescriptor, ProviderException
Catalog/        catalog.json (data) and ModelCatalog: default model per profile per provider kind, capabilities per family
Routing/        IModelRouter / ModelRouter, RoutingRequest, RoutedModel, IProviderReachability, ICostGovernor
Resilience/     CircuitBreakerRegistry + IProviderHealthSink, ConcurrencyLimiter, IProgressSink
Redaction/      RedactingChatClient, RedactingFimClient, StreamingPlaceholderRestorer
Caching/        AutocompleteCache (LRU, 256 entries, exact and type-through hits)
Autocomplete/   AutocompletePromptBuilder + AutocompleteBudget, CompletionShaper, AutocompleteCounters, PromptEvaluationRates
Context/        ContextAssembler (budget, priority, caps, rolling summary), ContextReport, FileAccessPolicy, TextTruncation, GatewayHistorySummariser
Tokens/         token-families.json (data) and ITokenCounter / TokenCounter (Microsoft.ML.Tokenizers or a calibrated estimate)
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
   Embedding: always local; Summarise: Fast online, Local-fast offline, following the Chat cloud switch). "Online" means a cloud provider is allowed for the task class and reachable. The profile
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
   `WaitingForLocalModel` after 2 s and `SlowerThanExpected` to `IProgressSink` after 120 s scaled by prompt size (chat)
   or after a budget from the measured prompt-evaluation rate with a 10 s floor (autocomplete).
   A failure before the first token moves to the next candidate; a failure after it ends the stream with
   `Error` on the final chunk.
3. **Stream.** Every chunk carries `ModelId` and `ProviderId`; the final chunk has `IsFinal`, token counts and any
   error. Placeholders are restored as they stream: text from a `<` that could start `<redacted:` is held until the
   closing `>`.
4. **Record.** One `OutboundRecord` per attempt (time, provider, model, task class, locality, prompt tokens and whether
   they were counted or estimated, completion tokens, cache hit, time to first token, redaction count, duration,
   outcome, and for an assembled chat request the number of context items and how many were truncated or dropped)
   plus one structured log line; never prompt text. Autocomplete cache hits are recorded too.

Autocomplete has its own steps (budget, header, cache, stops); see [Autocomplete](#autocomplete).

## Autocomplete

`CompleteAsync` is the host's autocomplete service (design sections 6, 7 and 9). Pillion Host serves it through
`IModelService.CompleteAsync` while `features.autocomplete` and `features.modelGateway` are on (`PolicyDenied`
otherwise).

1. **Route.** The Autocomplete task class is routed as above; the router only checks that a budgeted prompt
   (2,048 tokens) fits the model, because the prompt is built per candidate.
2. **Cache.** An LRU of 256 entries keyed by provider/model, single- or multi-line, SHA-256 of the last 2,048 prefix
   characters and SHA-256 of the first 512 suffix characters. A repeated keystroke or a backspace is an exact hit. When
   the prefix ends with up to 64 characters that start a cached completion for the shorter prefix (the user is typing
   the suggestion), the rest of that completion is served (a type-through hit). A hit is one final chunk with no
   request, and it is recorded in the Outbound log with `CacheHit` set and 0 prompt tokens.
3. **Budget.** `AutocompleteBudget.PromptTokensFor`: 1,024 prompt tokens when the completion model is local and the GPU
   tier is CPU only, 2,048 on a GPU or in the cloud, and never more than the model's context minus the completion. The
   budget covers the header, prefix, suffix and FIM framing (counted for an explicit template, 8 tokens reserved for a
   template the server applies). After the framing:

   | Share | Rule |
   | --- | --- |
   | Suffix minimum | 15%, when the suffix needs it |
   | Prefix minimum | 60%, when the prefix needs it |
   | Header | at most 15%, and only out of what the two minimums leave |
   | Remainder | to the prefix first, then the suffix, so the suffix is trimmed first |

   The prefix is cut on line boundaries from the top and always keeps its tail (the caret line; a caret line longer
   than its allowance keeps its last characters). The suffix is cut on line boundaries from the bottom. No project or
   user rules are added.
4. **Header.** Comment lines in the document's language (`//`, `#`, `--`, `<!-- -->`, none for JSON or Markdown), most
   stable first: `File: <name>`, `Open files: <up to 8 names, sorted>`, `In: <enclosing declaration>` (left out when the
   kept prefix already shows it), `Recent edit: ...` (up to 3, edits in the current file left out). Items are added in the
   order file, declaration, open files, edits while they fit the header's allowance.
5. **Stable prefix.** The header comes first and changes only when the declaration, the open documents or the edits
   elsewhere change. The prefix starts on a line whose absolute number (from `CaretLine`) is a multiple of 16, so the
   start moves only in 16-line steps as the file grows. Consecutive keystrokes therefore change only the tail and a
   resident model reuses its cached prompt: on the development machine, the second request with one more character took
   78 ms of prompt evaluation against 237 ms for the first (qwen2.5-coder:1.5b, 1,828 prompt tokens).
6. **FIM templates.** Ollama applies the model's own template when `suffix` is passed, so the Ollama path sends the
   header plus prefix as `prompt` and the suffix as `suffix`. An OpenAI-compatible local server whose `providers` entry
   sets `"supportsFim": true, "fimTemplate": "explicit"` (a raw `/v1/completions` server) gets the prompt rendered from
   the family's template in `Templates/fim-formats.json` (Qwen2.5-Coder, CodeLlama, StarCoder2, DeepSeek-Coder,
   Codestral) and an empty suffix. The family is matched by model id.
7. **Stops.** A single-line request (`AllowMultiLine` false) stops at the first newline; then the request's
   `StopSequences`, the family's stop tokens and the defaults (`\n\n\n`). The gateway applies them again to the stream,
   whatever the provider honoured, and closes the stream when one matches. A multi-line request may run to 30 lines;
   the client applies its block rules. Without `MaxTokens`, a single-line request generates up to 64 tokens and a
   multi-line one up to 256.
8. **Timeouts.** Cloud autocomplete has a 3 s hard limit and is dropped silently (an empty stream, Outbound outcome
   `Dropped`). A local request reports `WaitingForLocalModel` after 2 s and `SlowerThanExpected` after three times the
   prompt size divided by the model's measured prompt-evaluation rate (`PromptEvaluationRates`, from the time to first
   token of earlier completions; 200 tokens/s until measured), never less than 10 s
   (`LocalAutocompleteFirstTokenFloor`); it is never failed for being slow, so a cold load shows progress. Autocomplete
   has one request in flight per provider and is never retried.
9. **Record.** Every completion writes an `OutboundRecord`: provider, model, locality, prompt tokens (counted when the
   family has a tokenizer, `PromptTokensCounted`), completion tokens, `CacheHit`, `TimeToFirstToken`, duration,
   redaction count and outcome. `AutocompleteCounters.Snapshot()` (a singleton, also `ModelGateway.Counters`) reports
   requests, exact and type-through hits, misses, model requests, drops, failures, cancellations and the hit rate.

### Token counting (`Tokens/`)

`ITokenCounter.ForModel(modelId)` returns the counter for the model's family from `Tokens/token-families.json`; context
assembly (work item 1.10) uses the same interface.

| Family | Models | Counting |
| --- | --- | --- |
| `openai-o200k` | gpt-4o, gpt-4.1, gpt-5, gpt-6, o1, o3, o4 | Microsoft.ML.Tokenizers, `o200k_base` |
| `openai-cl100k` | gpt-4, gpt-3.5, text-embedding-3 | Microsoft.ML.Tokenizers, `cl100k_base` |
| `llama3` | llama3*, Llama 3.x | `cl100k_base`: Llama 3 keeps its 100,000 ranks and pre-tokenizer and adds 28,000, so the count is an upper bound in practice |
| `qwen2.5-coder` | qwen2.5*, qwen2*, qwen3* | estimate, 4.33 non-digit characters per token, 1 token per digit, +10% |
| `gemma` | gemma*, codegemma* | estimate, 3.57 characters per token, 1 per digit, +10% |
| `codellama`, `starcoder2`, `deepseek-coder`, `codestral`, default | | estimate at the lowest measured ratio (3.57), not measured |

Tokenizers are created on first use and shared, so a host that only completes with Qwen never loads one. The estimate
ratios were measured on 2026-10-09 with Ollama 0.40.1 on the corpus in
`tests/Pillion.ModelGateway.Tests/Fixtures/Tokens/corpus` (four C#, TypeScript and Python files, 17,506 characters, 231
of them digits): qwen2.5-coder:1.5b took 4,218 tokens (17,275 non-digit characters over 3,987 tokens = 4.33), gemma4:e2b
5,064 (3.57). Against the measured counts the estimate is 95% to 127% per file; the digit-heavy test file is the one it
undercounts (by under 5%). `cl100k_base` came within 2% of Qwen's counts on every file.

### Residency and `OLLAMA_MAX_LOADED_MODELS`

The Ollama adapter sends `keep_alive` -1 for the completion model and a fixed `num_ctx` per profile (Local-fast 4,096),
so the model is loaded once and never reloaded by a context change. Ollama still unloads a model when another request
needs its memory and the server already holds `OLLAMA_MAX_LOADED_MODELS` models, so set it (a system environment
variable for the Ollama service, then restart Ollama) to the number of models the tier keeps resident:

| GPU tier (`models.gpuTier`) | Resident | `OLLAMA_MAX_LOADED_MODELS` |
| --- | --- | --- |
| CPU only | the 1.5B FIM coder, plus a chat model in RAM | 2 |
| 8 GB | one 7B FIM coder; chat shares it or runs on the CPU | 2 |
| 12 GB | a 1.5B coder plus a 14B chat model, or a 7B coder plus an 8B chat model | 2 |
| 16 GB | a 7B coder plus a 14B chat model | 2 |
| 24 GB or more | a 7B coder plus a 32B chat model | 2 |

Keep `OLLAMA_NUM_PARALLEL` at 1 on 8 and 12 GB cards: every parallel slot multiplies the context memory of each loaded
model. On the development machine (RTX 2080 SUPER, 8 GB) qwen2.5-coder:7b at `num_ctx` 4,096 took 4.7 GB of VRAM and
stayed fully on the GPU next to the 1.5B model (1.2 GB).

## Context assembly

`Context/ContextAssembler.cs` builds the prompt of a chat request (design section 7, Context assembly), independent of
any provider. `ModelGateway.ChatAsync` uses it for a request that carries `Pins`, a `CurrentFile` or
`CurrentFileRegion`, or `ProjectRules` (`ContextAssembler.ShouldAssemble`); any other request is sent exactly as before.
Assembly runs once per candidate model, because the model's window decides what fits; routing for such a request checks
only that the user's own message fits, so history that assembly is about to compress cannot rule a small-window model out.

**Budget.** `window - output reserve - system prompt - the user's message`. The window is the model's context window
(inventory, else the catalog), and for a local Ollama model the fixed `num_ctx` of the routed profile when that is smaller
(Local-fast 4,096, Local-strong 8,192 by default). The output reserve is 4,000 tokens
(`ContextAssemblerOptions.OutputReserveTokens`) but never more than a quarter of the window. A message that does not fit
at all fails with `ContextOverflow` for that candidate, and the next candidate (a larger window) is tried.

**Priority and caps.** Items are placed in this order, each taking what it needs up to its cap from what is left:

| Order | Item | Cap | When over the cap |
| --- | --- | --- | --- |
| 1 | Project rules (`ProjectRules`, supplied by the VSIX after workspace trust) | 10% of the budget | Head kept, elision marker |
| 2 | Pins, in the order the client sent them | 25% of the budget each | First 60% and last 40%, elision marker between |
| 3 | Current file region | 15% | Lines around the middle kept, markers at the cut ends |
| 4 | Retrieved chunks (phase 2 hook, `IContextSource`) | 20% | Head kept |
| 5 | Repository map (phase 2 hook) | 10% | Head kept |
| 6 | History, newest turns first | what is left | Compressed into a rolling summary |

An item that does not fit the room left is cut to it when at least 64 tokens remain, otherwise dropped. Cuts happen on
line boundaries; the budget is checked with the real count, not the estimate (`TextTruncation`). A pin that is an
approved excluded or secret file (`IsSensitive`) is dropped for a cloud model, and a pin or current file that matches
`indexing.exclude` or the secret-file rules without approval is dropped (`FileAccessPolicy`, shared with mention
resolution in the host).

**History and the rolling summary.** If the history fits, it is kept verbatim. Otherwise the newest turns that fit in half
of the room (starting at a user turn) stay, and the older turns are summarised through `IHistorySummariser`, which the gateway
implements as a plain chat request of task class `Summarise` (Fast online, Local-fast offline; if the chat itself goes to
a local model the summary is kept local too). A long history is folded in batches of about 2,500 tokens, each batch
building on the summary so far. The summary is cached per `ConversationId` with a fingerprint of the turns it covers: it
is reused while those turns are unchanged and the turns after it still fit, and when the tail outgrows the room only the
new turns are folded into the cached summary. A changed earlier turn drops the cache. If summarising fails, the older turns
are left out and the report says so; the chat still goes ahead. Without a conversation id nothing is cached.

**Layout.** Sections go from most to least stable so provider-side prompt caching hits on the long common prefix: one
system message with the system prompt, rules, repository map, pins, current file, retrieved chunks and summary in that
order, then the recent turns, then the user's message. Pins keep the order the client sent them, so pinning one more
item only appends. Two requests with the same rules and pins render byte-identical leading sections (a test asserts it).
Pinned text is fenced with a fence longer than any backtick run inside it and introduced as reference material that must
not be followed as instructions.

**Report.** `AssembledContext.Report` (`ContextReport`) lists every item with its token count, its unshortened count,
and whether it was truncated or dropped, with a reason; the Context Inspector (work item 1.13) will show it. The Outbound
record keeps the item count and the number truncated or dropped (`ContextItems`, `ContextTrimmed`).

**Measured.** On a 128k window with three pins of 8, 60 and 400 KB, a current file region, rules and 30 turns of history,
assembly takes a median of about 45 ms and p95 of about 215 ms with the exact o200k tokenizer, and 12 ms and 17 ms with
the calibrated estimate (`Context/ContextAssemblyBenchmarkTests`). The Gate 1 row is p50 150 ms, p95 400 ms.

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
| `IContextSource` | none | The index (phase 2): retrieved chunks (`ContextSlot.RetrievedChunks`) and the repository map (`ContextSlot.RepoMap`); register with `AddSingleton<IContextSource, ...>` |
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
`PILLION_TEST_OPENAI_COMPAT_KEY` and `PILLION_TEST_OPENAI_COMPAT_MODEL`). Autocomplete: `Autocomplete/` covers the budget table, token counting and its calibration, FIM layouts, stop handling,
cache keys and counters, GPU-tier budgets and timeouts; `Ollama/AutocompleteLiveTests` (Live) checks a real completion,
multi-line output, the prompt-cache hit (`prompt_eval_duration`), the recorded calibration, and writes first-token and
total latency percentiles for qwen2.5-coder:1.5b and :7b to the test output (`--logger "console;verbosity=detailed"`).
Context assembly: `Context/` covers truncation, the exclusion and secret-file rules, the assembler (budget maths, priority, caps, summary trigger and cache, stable leading sections, the phase 2 hooks), chat through the gateway with fake providers, a timing benchmark, and `ContextLiveTests` (Live: a pinned file answered by gemma4:e2b, the summariser on a long fake history).
Run only live tests with `dotnet test --filter Category=Live`, or exclude them with `--filter Category!=Live`.
