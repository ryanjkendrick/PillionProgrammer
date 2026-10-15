# Pillion.Host

Pillion Host is the out-of-process .NET 10 server that the Visual Studio extension launches. It serves the
host-side contracts in `Pillion.Contracts` over StreamJsonRpc on a named pipe and calls back into the VSIX for
editor, approval and workspace operations.

```
Program.cs                          Parses the command line, composes the host, returns the exit code
PillionHostBuilderExtensions.cs     AddPillionHost: logging + lifecycle + RPC server (used by Program and tests)
Lifecycle/                          HostStateMachine, HostShutdown (exit code), ParentProcessWatcher
Context/                            ContextRpcService (IContextService): @file, @folder, @selection, @clipboard, @errors;
                                    IndexStatusNotifier (indexStatusChanged, contract 1.3)
Conversations/                      ConversationRpcService (IConversationService, contract 1.3), ConversationRecorder (saves chat turns),
                                    StoreOutboundSink (Outbound log to the store), ConversationRetentionService (see Saved conversations)
Indexing/                           IndexingService (the indexing pipeline, work item 2.9), IndexPass, the pause and memory rules
Rpc/                                RpcServer, RpcConnection, PillionJsonRpc, handshake, IRpcTarget, IVsixConnection
WorkspaceQueries/                   IWorkspaceQueries (work item 2.14): find_references and callers_of for @symbol and chat,
                                    from the VSIX's Roslyn workspace, falling back to the index's approximate edges
                                    (ISymbolGraphProvider, empty until the index supplies graphs)
Logging/                            Rolling JSON-lines file sink and retention
```

## Launch protocol

```
Pillion.Host.exe --pipe <name> --parent-pid <pid> --token-handle <handle> [--log-dir <dir>]
Pillion.Host.exe --version
```

| Exit code | Meaning |
|---|---|
| 0 | Normal stop: `ShutdownAsync`, the parent process exited, Ctrl+C. |
| 1 | Unrecoverable start-up error (state `Failed`): no valid token, the pipe could not be created. |
| 2 | Unknown, duplicate or missing arguments (message and usage on stderr). |

1. **Token.** `<handle>` is the decimal value of an inherited anonymous pipe read handle
   (`AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.Inheritable)`, then
   `GetClientHandleAsString()`, then `DisposeLocalCopyOfClientHandle()` after starting the process). The host
   reads exactly one UTF-8 line within 10 seconds: 32 random bytes, base64url without padding (43 characters),
   then `\n` (a preceding `\r` is tolerated). It then closes the pipe. Anything else is a start-up failure (exit 1).
   The token never appears on the command line, in logs or in error messages.
2. **Pipe.** The host creates
   `NamedPipeServerStream(name, InOut, 1, Byte, Asynchronous | CurrentUserOnly)` and serves one client at a time;
   after a client disconnects it listens again. The first instance also uses `FirstPipeInstance`, so the host
   fails instead of sharing the name if another process created that pipe first.
3. **Wire.** StreamJsonRpc `JsonRpc` over **`LengthHeaderMessageHandler`** (4-byte big-endian length prefix,
   then the message) with `MessagePackFormatter` and its default resolver (the contracts are DataContract-annotated
   for `StandardResolver`). `HeaderDelimitedMessageHandler` cannot carry MessagePack: it requires a text
   formatter and throws `NotSupportedException` on the first write. Method names are the CLR names
   (`HandshakeAsync`, `GetStateAsync`, ...) with StreamJsonRpc defaults; `IAsyncEnumerable<T>` results stream with
   no extra configuration. The host deserialises with `MessagePackSecurity.UntrustedData`, which does not change
   the wire format.
4. **Handshake.** The first call must be `IHostLifecycleService.HandshakeAsync`. It is accepted when, in this
   order: the pipe client's process id (`GetNamedPipeClientProcessId`) equals `--parent-pid`; the protocol token
   matches (constant-time compare); `ClientProcessId` in the request equals the pipe client's process id;
   `ContractVersion.IsCompatible(major, minor)`. On failure the response has `Accepted = false` and a `Reason`
   naming the check (never the token), the rejection is logged, and the host closes the connection as soon as the
   response has been written to the pipe. A connection that has not completed a handshake within 10 seconds is closed. A
   repeated handshake on an accepted connection returns the same accepted response.
5. **Before the handshake** every call to a registered method other than `HandshakeAsync` fails with
   `PillionError { Code = PolicyDenied, Message = "Handshake required" }` (unknown method names still get
   StreamJsonRpc's method-not-found error).
6. **State.** `GetStateAsync` returns the current `HostState`. Every change is sent to the authenticated client as
   the notification `hostStateChanged` with the `HostState` as its single positional argument (an integer on the
   wire). States: `Starting` (setting up), `Ready` (listening), `Degraded` and `Restarting` (for later components),
   `Failed` (start-up error, then exit 1). Allowed transitions are documented on `HostStateMachine`.
7. **Stopping.** `ShutdownAsync` returns at once, then the host stops listening, lets in-flight calls finish for up
   to 1 second, closes the connection (cancelling what is left) and exits 0. The host also exits 0 within about
   half a second of the `--parent-pid` process exiting (it waits on the process handle and polls), as a second
   line of defence beside the VSIX's Job Object.

### Errors on the wire

| Thrown by a target | JSON-RPC error | `data` |
|---|---|---|
| `PillionRpcException` | code -32000, message = `PillionError.Message` | the `PillionError` (code, message, retry hint, details) |
| `OperationCanceledException` | code -32800 (RequestCanceled) | `PillionError { Code = Cancelled }` |
| anything else | code -32000, generic message with a reference id that appears in the host log | none |

A StreamJsonRpc client sees -32000 as `RemoteInvocationException`. To get the `PillionError` back, override
`JsonRpc.GetErrorDetailsDataType` to return `typeof(PillionError)` for code -32000 (and -32800) and read
`RemoteInvocationException.DeserializedErrorData`; `tests/Pillion.Host.Tests/TestSupport/TestRpcClient.cs` shows
this. StreamJsonRpc clients surface -32800 as an `OperationCanceledException` without the data.

## Adding an RPC target

Implement a host-side contract interface and register it in `AddPillionHost` (or anywhere that builds the
service collection) with `AddRpcTarget`. No change to the RPC server is needed:

```csharp
// In PillionHostBuilderExtensions.AddPillionHost, or an AddPillionModels() extension it calls:
builder.Services.AddRpcTarget<IModelService, ModelService>();           // ModelService registered as a singleton
builder.Services.AddRpcTarget<ISettingsService>(sp => new SettingsService(/* ... */));   // or a factory
```

- The target is resolved once per connection (the type-based overload resolves the same singleton every time) and
  exposed with the contract interface's CLR method names. Calls only reach it after the handshake is accepted.
- Throw `PillionRpcException` for expected failures; let `OperationCanceledException` propagate. Anything else is
  logged with its stack trace and reported to the client as a generic error.
- Streaming methods return `IAsyncEnumerable<T>`; mark the token `[EnumeratorCancellation]` in async iterators.
- Registering the same contract twice, a non-interface, or `IHostLifecycleService` throws at start-up.

A notification from the VSIX (client to host, no response) is registered the same way, with
`AddRpcNotification(name, sp => handlerDelegate)`: the delegate's parameters are the notification's positional arguments,
and like a request it is only dispatched after the handshake (`ideActivityChanged` is the first, see Indexing pipeline).

To call the VSIX, inject `IVsixConnection` and ask for a proxy per operation:
`vsix.GetEditorService()`, `GetApprovalService()`, `GetWorkspaceQueryService()`. They throw
`PillionRpcException` with `HostRestarting` while no VSIX is connected. Never dispose a proxy (that closes the
connection), and do not cache one across reconnects.

To report health, inject `HostStateMachine` and call `TryTransition(HostState.Degraded, "reason")` (and back to
`Ready`); the client is notified automatically.

## Mentions (`Context/`)

`ContextRpcService` serves `IContextService`. `ResolveMentionAsync` turns a mention into pinned content with a token count
for the default chat model, truncated to `MaxTokens` (default 12,000) on a line boundary with `Truncated` set:

| Mention | Source | Result |
| --- | --- | --- |
| `@file:path`, `@Name` | Exact path (absolute, or relative to `WorkspaceRoots`), else fuzzy over `CandidateFiles` by file name, then path; with no list the host walks the roots | The file's text (read from disk, at most 1 MiB, binary refused); an ambiguous name returns up to 10 `Candidates` and `IsResolved` false |
| `@folder:path` | Folder from `WorkspaceRoots` or fuzzy over the folders of `CandidateFiles` | An outline: up to 200 files with size and first line; excluded and secret files are left out and counted |
| `@selection`, `@clipboard` | `Payload` | The text as sent |
| `@errors` | `Payload`: JSON lines `{severity,id,message,file,line}` | One line per diagnostic, errors first, at most 200 |
| `@symbol`, `@docs`, `@git`, ... | | Unresolved: "not available in this version" |

A file must be under a workspace root or in `CandidateFiles`. Files matched by the effective `indexing.exclude` globs or by
the built-in secret rules (`.env`, `.env.*` except examples, `*.pfx`, `*.p12`, `*.pem`, `*.key`, `*.jks`, `*.keystore`,
`secrets.json`, files under `.ssh`, `.aws`, `.gnupg`, and `appsettings.*.json` that mentions `ConnectionStrings` or
`Password`) resolve as unresolved with `RequiresApproval`; the client asks the user and asks again with `ApprovedSensitive`.
An approved file comes back with `IsSensitive`, and the gateway never sends such a pin to a cloud model.

`SearchAsync` and `SearchDetailedAsync` answer with hybrid retrieval ([below](#retrieval-contextretrieval)).
`GetIndexStatusAsync`, `PauseIndexingAsync`, `SetWorkspaceAsync`, `ListExcludedFilesAsync`, `RebuildIndexAsync`,
`DeleteIndexAsync` and `ListIndexesAsync` are served by the indexing pipeline (below). The chat side of context (pins, current file, rules, history summary) is in the Model
Gateway README, Context assembly.

### Saved conversations (work item 2.2)

`IConversationService` (contract 1.3) is served over the Local Store's `IConversationRepository` (the encrypted
`conversations.db`; see the Local Store README). Every method needs the `localStore` feature: while it is off the call fails
with `PolicyDenied` naming the flag. A store that cannot be opened or used fails with `StorageUnavailable` (a
`LocalStoreException` is passed on as it is; any other storage exception is logged and reported with a generic message and its
type name in `Details`).

- **Saving as chat runs.** There is no append method. `ModelRpcService.ChatAsync` asks `ConversationRecorder` to begin a request
  with a `ConversationId`: an unknown id creates the conversation, titled from the first line of the user's message cut to 60
  characters (a conversation the Chat window created empty is named by its first message), and the user's message is
  appended unless it is already the newest message (a failed reply that is retried sends the same history again). The reply is
  written once, when the stream ends: model, provider, prompt and completion tokens (0 is stored as unknown), the cost (0 for a
  local provider, the catalog's prices times the tokens for a cloud one, unknown when the catalog has no price) and the final
  chunk's citations. A reply stopped by Stop, a lost connection or a cancelled call is written with what had arrived and a
  `cancelled` flag (`ConversationMessage.Cancelled` on the wire), and is not written at all when nothing had arrived. A reply that failed (an exception, or a final chunk with
  an `Error`) is not saved: the user's message stays, so the Retry that follows does not repeat it. A storage failure never
  fails the chat; it is logged and the turn goes unsaved. Inline edits do not go through `ChatAsync` and are not saved.
- **Lists and edits.** `ListConversationsAsync` pages newest first (`Limit` 0 is 50, at most 500; `HasMore` from one extra
  row). `RenameConversationAsync` changes the title only (the conversation keeps its place in the list; an empty title leaves it as
  it was). `CreateConversationAsync` returns an existing id unchanged. `SetPinsAsync` replaces the pins in the order given.
  `DeleteAllConversationsAsync` first drops the Outbound records still queued for the store, then empties every table, compacts
  the file and deletes the migration backups, then clears the in-memory Outbound log.
- **The Outbound log.** `StoreOutboundSink` is an `IOutboundSink` that `InMemoryOutboundLog` hands every record to as it is
  produced. It only queues the record (5,000 at most, the oldest dropped); one background task writes them, so a request never
  waits for the disk, and it drains its queue when the host stops (up to 1.5 s). Autocomplete cache hits are not stored: they
  never reached a model, so they stay in the in-memory log only. `GetOutboundRecordsAsync` reads from the store (after waiting
  up to 2 s for the queue to catch up) while `localStore` is on, so the Outbound log view survives a restart, and adds this
  session's cache hits from the in-memory log (after a restart the view has no cache hits); if the store is off or cannot be
  read it answers from the in-memory ring buffer of this session.
- **Retention.** Conversations and the Outbound log are kept until the user deletes them (design section 10), so
  `ConversationRetentionService` opens nothing and removes nothing with the default `ConversationRetentionPolicy`; when a policy
  with an age limit is registered it applies it once, 30 s after start-up, in the background.

### Contract 1.3 stubs

Every contract 1.3 method is implemented now (conversations by 2.2, search by 2.11, the index methods by 2.9). A later
method that is not yet implemented answers honestly with a stub:

`RpcErrors.NotImplemented(feature, workItem)` builds that error; a work item that implements a method removes its stub and
its row here.

## Retrieval (`Context/Retrieval/`)

Work item 2.11, behind `features.retrieval` (off: both search methods fail with `PolicyDenied` naming the flag).
`ContextRpcService.SearchDetailedAsync` and `SearchAsync` delegate to `IRetrievalService` (`RetrievalService`), which takes
the open indexes from an `IIndexCatalog`, adapts each with `StorageRetrievalIndex` (reading the embedding model and vector
size from the index `meta` per search) and runs `HybridRetriever` (Pillion.ContextEngine, see its README, Hybrid
retrieval). Results use the contract's zero-based lines and the opaque chunk id `<index-id>:<row-id>`; `SearchAsync`
returns the same hits as `SearchDetailedAsync` without the timings. `SearchRequest` has no budget field, so the budget is
`RetrievalOptions.TimeBudget` (300 ms; register a `RetrievalOptions` singleton to change it).

- `IIndexCatalog` gives the open `IndexStorage` (and `SymbolGraph`, when the host keeps one) per repository root, for the
  request's `Roots` or every index when there are none. `ReadOnlyIndexCatalog`, registered with `TryAdd`, finds index
  folders by `state.json` under `index\`, picks the most recently opened one per root, opens it read-only (the reader path
  of `IndexStoreFactory`: nothing created, migrated or locked), keeps it open until the host stops and warms its vector
  cache in the background; it serves nothing while `indexing` or `localStore` is off. The indexing pipeline (work item 2.9)
  registers its live catalog before `AddPillionRetrieval` to take its place.
- `GatewayQueryEmbedder` embeds the question with `IModelGateway.EmbedAsync` (one input, purpose `Query`, the index's model
  id and vector size), so the `embeddings` flag, routing, Local-only and the Outbound log apply as for any embedding. When
  embedding fails or overruns the budget, the search answers from the lexical and symbol legs with `Degraded` set.
- `features.reranker` (off by default) is read, but no cross-encoder ships yet (licence review, design section 18): the
  fused order is kept and the host logs that once.

## Index status notification (`indexStatusChanged`)

Host to client, a JSON-RPC notification with one positional argument, an `IndexStatus` (contract 1.3). `IndexStatusNotifier`
(a singleton) sends it: the first status after a quiet spell at once, later ones at most once per 500 ms, coalesced so the
latest status of the window is the one sent. It goes over the same path as `connectivityChanged` and `settingsChanged`
(`INotificationSender`), only to a client whose handshake was accepted, and is not replayed to a client that connects later
(the client calls `GetIndexStatusAsync`). The indexing pipeline publishes to it whenever its phase, progress or roots change
(below). In the VSIX it surfaces as `IHostConnection.IndexStatusChanged`.

## Indexing pipeline (`Indexing/`)

Work item 2.9, design sections 7, 10 and 13, behind `features.indexing` (an index also needs `features.localStore`).
`IndexingService` is a hosted singleton; `ContextRpcService` delegates the index methods of `IContextService` to it.

1. **Workspace.** The extension calls `SetWorkspaceAsync` (contract 1.3) after every handshake and when the solution, its
   projects or the opened folder change. The service resolves the repository roots (`RepositoryRoots.Resolve`: the git
   top-level of each path, or the folder) and opens each root's index through `IndexStoreFactory` with the embedding model
   the `Embedding` task class routes to (`GatewayIndexEmbedder`: the bundled `bge-small-en-v1.5` by default, its own WordPiece
   tokenizer for the chunk cap) and `ContextEngineVersions.ChunkerVersion`. Roots that are no longer wanted are closed (the
   writer releases `writer.lock`); the same roots sent again restart nothing. A changed embedding model opens another index.
2. **Run.** For each root this host owns, one at a time, an `IndexPass`:
   - *Discovering, Hashing*: `RepositorySweeper` with the effective `indexing.exclude` (one walk; unchanged files are neither
     opened nor hashed). Rows of deleted files go in one transaction (chunks, vectors and symbols by cascade); touched files
     get their new metadata. The sweep's excluded and secret-bearing entries are kept for `ListExcludedFilesAsync`.
   - *Chunking*: added and changed files are read, chunked (`FileChunker`, cap `min(model input, 512)` tokens in the
     model's tokenizer, breadcrumb included) and their symbols extracted (`FileSymbolExtractor`) on the pipeline threads.
     On the first run of a session the unchanged files are read for their symbols only (the symbol graph needs every
     file); when the stored `symbols_version` differs, their symbols are stored again (no re-chunking, no re-embedding).
   - *Embedding*: chunks go to `IModelGateway.EmbedAsync` in batches of 32 (`EmbeddingPurpose.Document`, the model pinned by
     id so an index never mixes models; Local-only and the cloud switch are the gateway's rules). A failed batch is
     retried three times (1, 2, 4 s); then the run stops as `Failed` with the error, keeping every file already stored.
   - *Storing*: each file's `files` row, chunks (with FTS5 rows and int8 vectors) and symbols (with `SymbolDefinition.Id`
     as the stored id, so edges from other files survive edits) in **one transaction per file**
     (`IndexStorage.WriteAsync`). After the first stored batch, and then at most every
     `max(10 s, 10 x the last refresh)`, the symbol graph is rebuilt (`SymbolGraphBuilder`, which holds every file's
     latest extraction), the reference edges of the files whose edges changed are written in one transaction, and the
     in-memory vector copy is reloaded (`WarmVectorCacheAsync`), so partial results are searchable while the run goes on.
   - At the end: a last refresh, `meta` rows `symbols_version`, `embedding_tokenizer`, `embedding_max_tokens` and
     `chunk_max_tokens` (beside `embedding_model`, `chunker_version` and `vector_dimensions`, which the factory and the
     storage write), and `state.json`'s last full sweep (`IndexStore.RecordFullSweep`).
3. **Status.** `GetIndexStatusAsync` and `indexStatusChanged`: the phase only moves forward within a run (Discovering,
   Hashing, Chunking, Embedding, Storing, then Idle; `Paused` with its reason on top, `Failed` with `LastError`), files
   done of total, chunks stored, and an estimate from the bytes done since embedding started. Each root reports its id,
   role, files indexed and found, excluded and secret-bearing counts, chunks, disk use (from `GetIndexStatusAsync`; the
   notification leaves it 0 to stay cheap), last full sweep, model, chunker and schema versions.
4. **Controls.** `PauseIndexingAsync` (pause reason `User`); `RebuildIndexAsync(root)` cancels a run on that root, deletes
   every `files` row (cascading to everything else), clears `symbols_version` and `vector_dimensions`, vacuums and runs
   again (for a workspace root whose index was deleted it opens a new one); `DeleteIndexAsync(id)` closes the index if
   this host has it open, deletes the folder through `IndexRetention.Delete` (refused with `PolicyDenied` while another
   live host owns it) and does not reopen that root until a rebuild or the next workspace change; `ListIndexesAsync` maps
   `IndexRetention.GetUsage()`; `ListExcludedFilesAsync` pages the last sweep's list, secret-bearing first. With the flag
   off the status is `Idle` with no roots, nothing is opened, and these controls are refused with `PolicyDenied`.

### Serving search and the workspace queries

`LiveIndexCatalog` is the `IIndexCatalog` of retrieval (work item 2.11) and the `ISymbolGraphProvider` of the workspace
queries (2.14), registered before `AddPillionRetrieval` and the empty provider. Searches use the very `IndexStorage` the
pipeline writes through (or reads, on a reader host), with the symbol graph of the last refresh, so a root this host has
open is never opened a second time and new chunks are searchable as soon as a batch is stored. Indexes on the machine the
pipeline does not have open (another repository, or before the extension reports the workspace) still come from 2.11's
read-only catalog. When an index opens, two reader connections are opened and the in-memory vector copy loaded in the
background, so the first search pays for neither.

### Resource rules (design section 13)

- **Threads.** `PipelineScheduler`: `Environment.ProcessorCount / 2` dedicated threads at `ThreadPriority.BelowNormal`.
  The sweep's hashing, reading, chunking, symbol extraction, the graph rebuild and the database writes run there. The
  bundled model's ONNX Runtime intra-op threads are ONNX Runtime's own (normal priority, capped at half the logical
  processors by `BundledModelOptions.IntraOpThreads`); the gateway runs one embedding batch at a time per provider.
- **Memory.** `MemoryGovernor` samples `Environment.WorkingSet` before every batch. Over `indexing.maxMemoryMb` (1,536 by
  default): a forced, compacting garbage collection first (once per episode), then the batch is halved (32, 16, 8, 4),
  then indexing pauses with reason `Memory`, sampling every second and resuming below 90% of the cap; below 75% the batch
  doubles back to 32.
- **The bundled model's memory.** Measured with the pipeline on this repository's own source: with ONNX Runtime's memory
  pattern on and 32 x 512 x 512 attention cells per run the host passed 2 GB and the memory pause never lifted (the CPU
  arena does not shrink). The bundled runtime now runs with the memory pattern off on the CPU and 512 x 512 cells per run
  (`BundledModelOptions.CpuMemoryPattern`, `MaxAttentionCells`): about 315 MB at peak and about 50% faster. The governor's
  first step also releases the loaded model (`BundledModelRuntime.ReleaseUnused`, its arena with it), and it releases it
  again when it pauses, so a pause can end.
- **IDE activity.** The notification `ideActivityChanged` (client to host, contract 1.3, an `IdeActivity` set) feeds
  `IdeActivityMonitor`: pause reasons `Build`, `Test`, `Debug` while the flags are set; the set is forgotten when the
  extension disconnects, so a crashed Visual Studio cannot leave indexing paused.
- **Battery.** `Win32PowerStatus` reads `GetSystemPowerStatus` (`SystemStatusFlag` 1 = battery saver) every 15 s and on a
  settings change; with `indexing.pauseOnBattery` (default on) it pauses with reason `Battery`.
- Pauses take effect at the next checkpoint: before each file is read and before each embedding batch. Reported in the
  order User, Memory, Build, Test, Debug, Battery when several hold.

### Readers

A host that opened an index read-only (another Visual Studio owns it) does not index it, and **forwards nothing**: forwarding
"file changed" or "rebuild" requests to the owner's pipe needs a host-to-host handshake (the pipe and its token are bound
to the owner's own extension), which is not cheap to add here. Instead the reader checks every 30 s whether the owner is
still alive (`IndexStore.GetLiveWriter`); when it is gone the reader opens the index again, reclaiming the stale lock, and
as the new writer runs a full sweep, which picks up whatever changed meanwhile. A reader still searches the index. A
rebuild requested on a reader is refused with `PolicyDenied` naming the other instance. The watcher (work item 2.10) can
add forwarding later through the same `IndexWriterEndpoint`.

Event ids 2900-2999 (the indexing libraries use 2300-2399).

## Diagnostics (`Models/DiagnosticsRpcService.cs`)

`DiagnosticsRpcService` serves `IDiagnosticsService`, read-only and never with prompt or completion text:

- `GetAutocompleteCountersAsync` (work item 1.5): the gateway's autocomplete counters and the last five minutes of
  autocomplete records in the Outbound log.
- `GetOutboundRecordsAsync(maxCount)` (work item 1.13): the Outbound log's records, newest first, at most `maxCount` (0 or
  less means 200; the ring buffer keeps `ModelGatewayOptions.OutboundLogCapacity`). Each is time, request id, provider,
  model, task class, locality, prompt and completion tokens, redactions, context items and how many were trimmed,
  duration, outcome with its error code, and whether autocomplete served it from the cache. The Chat window's Outbound log
  view reads it. With `localStore` on the records come from the Local Store (they survive a restart; work item 2.2), except
  that autocomplete cache hits, which never reached a model, are kept in memory only: the view shows this session's and
  none from before a restart (see Saved conversations).
- `PreviewContextAsync(ChatRequest)` (work item 1.13): the Context Inspector. `ContextPreview` routes the request as the
  gateway would (the router sees only the size of the user's message), builds the same assembly target (the inventory's
  window, capped at the routed profile's `num_ctx` for Ollama, the chat system prompt, the effective exclusions) and runs a
  fresh `ContextAssembler`, so no model is called and the summary cache that sending uses is untouched. History that sending
  would summarise is reported as a summary at its cap. The report lists every item in prompt order with its tokens, whether
  it was cut or dropped and why, and, for a pin, its index in `Pins`. When the target is a cloud model (or
  `privacy.redactForLocalProviders` is on) each item that would be sent is run through the secret scanner and its matches
  are counted (`Redacts`, `RedactionCount`). No model routable, or a message that alone overflows the window, fails the call
  with the router's or the assembler's `PillionRpcException`.

Both 1.13 methods need the `outboundLogView` feature (the preview also `modelGateway`); otherwise they fail with
`PolicyDenied` and a message that names the flag.

## Inline edits (`InlineEdit/`)

`AddPillionInlineEdit()` registers `InlineEditRpcService` as the `IInlineEditService` target (work item 1.8, design
sections 5, 8 and 12). `EditAsync` and `CancelEditAsync` need the `modelGateway` and `inlineChat` flags; otherwise
`PolicyDenied` with a message that names the flag.

1. **Prompt.** `InlineEditPromptBuilder` (in the gateway, `Editing/`) builds a system prompt per command from
   `src/Pillion.ModelGateway/Templates/inline-edit.json` and one user message: language and file name (never the folder),
   up to 6,000 characters of context before and after the scope (whole lines nearest it), the scope with `N| ` line
   numbers from its document line, the diagnostics, earlier instructions of the same `ConversationId`, the task for the
   slash command and the instruction. A refinement (`PreviousProposal` set) shows the proposal as the code to edit.
2. **Model call.** `IModelGateway.ChatAsync` with `TaskClass.InlineEdit`: the gateway routes it (Balanced online,
   Local-strong offline, or the request's `ModelId`/`Profile`), redacts the prompt for a cloud provider (or for a local
   one when `privacy.redactForLocalProviders` is on), restores placeholders in the reply and writes one Outbound log
   record per call (a request that is sent back to the model makes one record per attempt, all with its request id).
   Inline edits are sampled at `ModelGatewayOptions.InlineEditTemperature` (0.2); other chat keeps the provider default.
3. **Mode and edit format.** Two ways of answering, chosen per request:

   - **Whole-scope mode** when the code to edit (the scope, or the proposal being refined) has at most
     `inlineEdit.wholeScopeMaxLines` lines (default 80) and the command is not `/test` or `/explain`. The model is
     asked for the complete rewritten code as plain code: no fences, no line numbers, no prose
     (`WholeScopeParser`, `system.editWhole` in the template). It reads a reply tolerantly: a Markdown fence around the
     code (prose around the fence becomes the explanation), `N| ` line numbers on every line (stripped), CRLF or LF, and
     blank lines at the edges; the trailing newline follows the scope. Its complete lines stream out as they arrive, so
     the first preview text comes after the first line, not after the old scope has been copied. A refinement works the
     same way: the prompt shows the previous proposal and asks for the revised whole code.
   - **Block mode** for a larger scope, and as the fallback when a whole-scope reply is still unusable after one retry
     (empty, in block format, `/doc` changing code, an invented placeholder, or a retry that Roslyn rejects without a
     better result): the conversation starts again with the block prompt and gets one retry. The model answers with
     search-and-replace blocks:

     ```
     <<<<<<< SEARCH
     lines copied exactly from the code to edit
     =======
     the lines that replace them
     >>>>>>> REPLACE
     ```

     Chosen for multi-file agent edits (design section 8) and large scopes because it is the format code models are most
     practised in (Aider, Cline and others use it), it carries code without JSON escaping, its whole-line markers cannot
     be confused with code or Markdown fences, and an exact copy of the old lines makes every edit checkable before
     anything is shown. For a small scope the copy is what costs time: with `qwen2.5-coder:7b` the first preview text
     of "extract the validation into a method" took about 2.8 s in blocks and about 0.2 s as a whole-scope rewrite.

   `/test` answers with one fenced code block (new code, placed elsewhere) and `/explain` with prose in both cases.
   `inlineEdit.wholeScopeMaxLines` is read from `settings.json` for every request (`InlineEditOptions`); 0 always uses
   blocks, and a value that is not a whole number of 0 or more is ignored. The mode is logged (event 603, and 604 for a
   fallback; the finish message, event 601, names the mode that produced the result). The contract has no field for it,
   so it is not on the final chunk (`RejectedBlocks` is 0 in whole-scope mode) and the Outbound record, which the
   gateway writes, has no details field to carry it.
4. **Apply and check.** In block mode `EditBlockApplier` applies the blocks in order to an in-memory copy of the scope:
   exact match, then
   ignoring trailing whitespace, then ignoring indentation (the replacement is re-indented to the code); blank edge
   lines are dropped if the block does not match with them. Malformed blocks, blocks that match zero or several places,
   code without blocks, and placeholder-shaped tokens (`<redacted:...>`) that are not already in the code are sent back
   with a corrective message, at most twice (`InlineEditEngine.MaxRetries`); then the call fails. So is a `/doc` edit
   that changes anything but comment lines (C#, VB, F#, C, C++, JavaScript, TypeScript). A reply with neither
   blocks nor code means no change: the scope comes back unchanged with a warning and the model's reason. In
   whole-scope mode the same checks apply to the rewritten code: it must not be empty, a `/doc` rewrite may only change
   comment lines, placeholders are checked as above, and a rewrite identical to the code means no change (with the
   warning). An unusable whole-scope reply is sent back once (`InlineEditEngine.WholeScopeMaxRetries`) before the
   fallback to blocks (`FallbackMaxRetries`: one retry there).
5. **Validate.** For `csharp` and `vb`, Roslyn parses `TextBefore + proposal + TextAfter` (parse only, no compilation),
   in both modes. If the proposal has more syntax errors than the original, the errors and the proposal go back once;
   if the answer is no better, the proposal with the fewest errors is returned with a `Warning`. Other languages are
   not validated.
6. **Stream.** While the model writes, the engine re-applies the blocks so far and sends the complete lines that can no
   longer change (the code before the block being written, then each finished replacement line), so the preview starts
   before the model finishes. In whole-scope mode it sends each complete line of the code as it arrives (without a
   fence or line numbers, holding back trailing blank lines). The final text is the validated proposal; if it does not
   start with what was sent, it is sent again with `ResetText`. Every failure fails the call with a `PillionRpcException`, even after preview text:
   `PolicyDenied` for an unresolvable placeholder, `ProviderUnavailable` (with a retry hint and `Details`
   `inline-edit: unusable reply`) when the model's blocks could not be used, or the gateway's own error.

Scope text is converted to LF for every step and the proposal is returned in the scope's own line endings.
Conversations (`InlineEditConversations`) are in memory only: 64 sessions, 8 instructions each.

## Logging

Console (single line) plus a file sink writing one JSON object per line to `host-YYYYMMDD.log` (local date) in
`--log-dir`, default `%LOCALAPPDATA%\PillionProgrammer\logs`:

```json
{"ts":"2026-10-08T14:42:13.338+01:00","level":"Information","category":"Pillion.Host.Rpc.RpcConnection","eventId":211,"eventName":"LogAccepted","message":"Connection 1 handshake accepted (client contract 1.1).","state":{"ConnectionId":"1","Major":"1","Minor":"1"}}
```

`exception` (with stack trace) is added when there is one. If another host process already has today's file
open, the second process writes `host-YYYYMMDD-<pid>.log`. At start-up, `host-*.log` files older than 7 days are
deleted, then the oldest are deleted until the remaining ones total at most 100 MB; other files in the folder are
never touched. The default minimum level is Information (Microsoft.* at Warning); set the environment variable
`Logging__LogLevel__Default=Debug` for more. Never log tokens or request payloads at Information or above.

Event id ranges: 100-199 lifecycle, 200-299 RPC, 600-609 inline edits, 2900-2999 the indexing pipeline.

## Connectivity

`AddPillionConnectivity()` (in `Connectivity/`, namespace `Pillion.Host.Connectivity`) registers the
`Pillion.Common.Connectivity.ConnectivityMonitor` (also as `IConnectivityMonitor`), an HTTP prober, the system
network-change source and a hosted service that runs the monitor and tells the extension. `AddPillionHost` calls it
and connects the Model Gateway to the monitor through three adapters in `Models/`:

- `ConnectivityGatewaySettingsSource`: the gateway reads the same `IConnectivitySettingsProvider` as the monitor
  (user settings plus repository policies), so the router's Local-only check and the monitor's `LocalOnly` state come
  from one source of truth.
- `ConnectivityProviderReachability` (`IProviderReachability`): routing skips a cloud provider the monitor has found
  unreachable, and every cloud provider in Local-only. Providers the monitor does not track count as reachable.
- `ConnectivityProviderHealthSink` (`IProviderHealthSink`): every failure the circuit breaker counts is
  `ReportFailure`, every success `ReportSuccess`, so three failures in a row mark a cloud provider unreachable.

Feature flags (`IFeatureFlags`, registered by `AddPillionHost` over the same effective settings): `ModelRpcService`
fails every call but cancel with `PolicyDenied` while `modelGateway` is off; the monitor and the provider registry read the flags from
the effective settings they already have; `ISettingsService.GetAsync("effective.features")` returns the effective flags
(read-only, writes to `effective.*` are refused). See the main README for the flag list.

Every service is registered with `TryAdd`, so tests substitute `IConnectivitySettingsProvider`, `IProviderProber`
and `INetworkChangeSource`.

States (`Pillion.Contracts.ConnectivityState`, an integer on the wire): `Online` (0), `Degraded` (1), `Offline` (2),
`LocalOnly` (3).

- `LocalOnly`: the user's `privacy.localOnly` or any repository policy with `localOnly` (resolved through
  `EffectiveSettings`). No probe is sent, the timers are stopped and probes in flight are cancelled.
- Otherwise, over the enabled cloud providers: `Online` when all are reachable, `Degraded` when some are, `Offline`
  when none is. **With no cloud provider configured the state is `Online`**: there is nothing to reach, and Offline
  would wrongly grey out local models. A provider that has not been probed yet counts as reachable.
- Local providers are never probed: Ollama, and an `openai-compatible` provider whose endpoint host is `localhost`,
  a loopback address (`127.0.0.0/8`, `::1`) or a name ending in `.local` or `.localhost`. Disabled providers and
  providers that are not in the settings are not probed either. A cloud provider with no usable endpoint (for example
  Azure OpenAI before the user types one) is not probed and is not reported as down.
- A probe is `GET /` on the endpoint's origin (scheme, host, port) with a 2 s timeout, through the system proxy. Any
  HTTP answer, including 401, 403 and 404, means reachable. DNS, connection, TLS and proxy failures and timeouts mean
  unreachable, with a category. No credentials or user data are sent.
- Probe rounds run at start-up, every 60 s, 1 s after the last network-availability or address-change event, when
  the monitored providers or the local-only flag change in the settings, and on `ProbeNowAsync`. Rounds never overlap.
- Circuit-breaker evidence: three consecutive `ReportFailure(providerId)` calls mark the provider unreachable until
  the next successful probe or `ReportSuccess(providerId)`. A success report in between resets the count.
- `StateChanged` is raised once per transition with a reason such as `openai unreachable: timeout`. The monitor's
  `Snapshot` holds, per monitored provider, reachable, last probe time, last error category and latency.

Notification `connectivityChanged`, host to client, one positional argument: the `ConnectivityState` as an integer.
It is sent once after every accepted handshake (with the state at that moment) and again on every transition. It may
reach the client before the handshake response, so the client registers the handler before it handshakes, as it
does for `hostStateChanged`. Nothing is sent before the handshake is accepted. The sender is
`Connectivity/INotificationSender` over the connection `VsixConnection` holds (`TryNotifyAsync`).

Repository policies: the monitor reads policies through `IRepositoryPolicyProvider`. `RepositoryPolicyRegistry` is
the default; whatever knows the open repositories calls `SetPolicies(...)` on it when the set changes.

## Settings-changed notification (`settingsChanged`)

Host to client, a JSON-RPC notification with one positional argument: a string array of the names of the top-level
sections of `settings.json` that changed (for example `["routing", "cloudSwitches"]`), in document order. It carries
no values; the client reads the settings it shows again through `ISettingsService`.

- Sent after any `SettingsStore.Changed`: a save through `ISettingsService.SetAsync` and an external edit of the file
  (the store's file watcher reloads it first). The sections are found by comparing the settings document before and
  after, so unknown top-level sections (such as `autocomplete`) are named too, and a save that changes nothing sends
  nothing.
- Sent once per burst: every change restarts a 250 ms window, so the several `SetAsync` calls of one Options page save
  produce one notification naming the union of the sections. An external edit therefore arrives about half a second
  after the file is saved (the store's own 250 ms debounce plus this one).
- Sent only to a client whose handshake was accepted, over the same path as `connectivityChanged`
  (`Connectivity/INotificationSender`, `VsixConnection.TryNotifyAsync`). With no client connected nothing is sent, and a
  change made before a client connected is not replayed: a client reads the settings when its host is ready anyway.
- The client registers the handler before it handshakes, as for `hostStateChanged` and `connectivityChanged`. A client
  that registers none is unaffected (StreamJsonRpc ignores a notification nobody handles).
- Implemented by `Settings/SettingsChangedNotifier` (a hosted service registered with
  `AddPillionSettingsNotifications`). In the VSIX it surfaces as `IHostConnection.SettingsChanged`; the chat model
  picker, the open Options pages and autocomplete availability read their settings again when a section they use
  is named.
