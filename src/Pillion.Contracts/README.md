# Pillion.Contracts

The RPC contract between the Visual Studio extension (`Pillion.VisualStudio`, .NET Framework 4.8 inside
`devenv.exe`) and Pillion Host (`Pillion.Host`, .NET 10). This assembly targets `netstandard2.0` and is compiled
into both processes.

## What lives here

- Service interfaces. VSIX to host: `IModelService`, `IInlineEditService`, `IContextService`, `IAgentService`,
  `ISettingsService`, `IHostLifecycleService`, `IDiagnosticsService`, `IConversationService`. Host to VSIX: `IEditorService`,
  `IApprovalService`, `IWorkspaceQueryService`.
- The DTOs those interfaces exchange, the shared enums, `PillionError` / `PillionRpcException`, and
  `ContractVersion`.

Nothing else: no transport, no serializer, no implementation. The only package references allowed are
`Microsoft.Bcl.AsyncInterfaces` and `System.Threading.Tasks.Extensions` (for `IAsyncEnumerable<T>` and
`ValueTask` on `netstandard2.0`). **Do not add MessagePack or StreamJsonRpc references here.**

## Versioning rule

`ContractVersion` holds a major and a minor number, exchanged in the handshake (`HandshakeRequest` /
`HandshakeResponse`). Two builds are compatible when their **major versions are equal**; minor versions may differ.

- **Minor bump**: only adds. Add a new method to an interface, or append a new field to a DTO with the next unused
  `Order`. An older peer never calls the new method and ignores the new field.
- **Major bump**: anything else, including removing or renaming a method, changing a parameter or return type,
  and removing, re-typing, re-ordering or changing the meaning of a DTO field.

`tests/Pillion.Contracts.Tests` asserts the exact method names on each interface, so an accidental contract change
fails the tests. When you change an interface deliberately, update that test and `ContractVersion` in the same
commit. Method names must also be unique across all the services one side exposes (Pillion Host registers every
VSIX-to-host service on one JSON-RPC connection under its CLR method names, and the VSIX does the same for the host-to-VSIX
ones), and must not clash with a notification name (`ContractV13Tests`).

## Changelog

### 1.3 (phase 2)

Additive only, batched at the boundary of phase 2's first wave into one minor bump (`ContractVersion` says 1.3; decision
of 2026-10-15 in `docs/phase-2-plan.md`). A 1.2 peer is still accepted by the handshake: a 1.2 host answers the new
methods with StreamJsonRpc's method-not-found error, a 1.2 client never calls them and registers no handler for
`indexStatusChanged` (nor sends `ideActivityChanged`), and each side reads the new fields' defaults from the other (`ContractV13Tests`, and over the real
pipe `ContractV13RpcTests` and `HandshakeCompatibilityTests` in the host tests). Every new DTO is round-tripped by
`DtoRoundTripTests`. Until the consuming work items land, Pillion Host answers most new methods with
`PillionErrorCode.NotImplemented` (see the host README, Contract 1.3 stubs); the fake host serves them all with canned data.

- **Conversations (work item 2.2).** A new VSIX-to-host service over `conversations.db`, behind the `localStore` flag. There
  is no append method: the host appends the user's message and the reply as `ChatAsync` runs a request whose
  `ConversationId` names a conversation. Pillion Host implements it in work item 2.2 (see the host README, Saved
  conversations).

  | Type | Member | Order | Meaning |
  | --- | --- | --- | --- |
  | `IConversationService` (new, VSIX to host) | `ListConversationsAsync(ConversationListRequest)` | | One page, most recently updated first |
  | `IConversationService` | `GetConversationAsync(string conversationId)` | | Messages and pins; `null` for an unknown id |
  | `IConversationService` | `CreateConversationAsync(ConversationCreateRequest)` | | Creating an existing id returns it unchanged |
  | `IConversationService` | `RenameConversationAsync(string conversationId, string title)` | | `bool`; an empty title leaves it as it was |
  | `IConversationService` | `DeleteConversationAsync(string conversationId)` | | `bool` |
  | `IConversationService` | `DeleteAllConversationsAsync()` | | Delete all on the Privacy page: conversations, messages, pins and the Outbound log, then compaction |
  | `IConversationService` | `SetPinsAsync(string conversationId, ConversationPin[] pins)` | | Replaces the pins; `bool` (false for an unknown id) |
  | `ConversationListRequest` (new) | `Offset`, `Limit` | 0-1 | `Limit` 0 means 50, capped at 500 |
  | `ConversationPage` (new) | `Items`, `HasMore` | 0-1 | `ConversationSummary[]` |
  | `ConversationSummary` (new) | `Id`, `Title`, `CreatedUtc`, `UpdatedUtc`, `LastModelId`, `MessageCount` | 0-5 | `LastModelId` is `providerId/modelId` or `null` |
  | `ConversationDetail` (new) | `Conversation`, `Messages`, `Pins` | 0-2 | |
  | `ConversationMessage` (new) | `Id`, `Role`, `Content`, `ModelId`, `ProviderId`, `PromptTokens`, `CompletionTokens`, `CreatedUtc`, `Citations`, `Cancelled` | 0-9 | `Role` is `ChatRole`; `Citations` keep a restored reply's spans clickable; `Cancelled` marks a reply stopped before it finished (`Content` is what had arrived) |
  | `ConversationPin` (new) | `Kind`, `Target`, `DisplayName`, `TokenCount` | 0-3 | What to resolve again on restore (`@Kind:Target`), never the content |
  | `ConversationCreateRequest` (new) | `Id`, `Title`, `ModelId` | 0-2 | Empty `Id`: the host chooses one; empty `Title`: named from the first user message |
  | `DeleteAllConversationsResult` (new) | `Conversations`, `Messages`, `Pins`, `OutboundEntries` | 0-3 | What was deleted |
  | `PillionErrorCode` | `StorageUnavailable` | enum value 7 | The Local Store cannot be opened or used (key, schema, migration, cipher); `LocalStoreException` uses it for every kind but `FeatureDisabled` (still `PolicyDenied`). A 1.2 peer sees an unknown number |

- **Index status and control (work items 2.9 and 2.15).** `IndexStatus` grows; `IContextService` gains four control
  methods; the host pushes the status in a new notification.

  | Type | Member | Order | Meaning |
  | --- | --- | --- | --- |
  | `IndexStatus` | `Phase` | 5 | `IndexPhase` (new enum): `Idle` 0, `Discovering`, `Hashing`, `Chunking`, `Embedding`, `Storing`, `Paused`, `Failed` |
  | `IndexStatus` | `PauseReason` | 6 | `IndexPauseReason` (new enum): `None` 0, `User`, `Build`, `Test`, `Debug`, `Battery`, `Memory`, `LowDisk` |
  | `IndexStatus` | `Roots` | 7 | `IndexRootStatus[]`, one per repository root of the open solution |
  | `IndexStatus` | `ExcludedSecretFileCount` | 8 | Secret-bearing files excluded, over every root |
  | `IndexStatus` | `LastError` | 9 | `string?` for the Index window |
  | `IndexStatus` | `Progress` | 10 | `IndexProgress?`; `null` when idle. `IndexedFileCount` and `TotalFileCount` stay the totals over every root |
  | `IndexRootStatus` (new) | `RootPath`, `IndexId`, `Role`, `IndexedFileCount`, `TotalFileCount`, `ExcludedFileCount`, `SecretFileCount`, `ChunkCount`, `DiskBytes`, `LastFullSweepUtc`, `EmbeddingModelId`, `ChunkerVersion`, `SchemaVersion` | 0-12 | `Role` is `IndexRole` (new enum): `Unknown` 0 (not opened yet), `Writer`, `Reader` |
  | `IndexProgress` (new) | `FilesDone`, `FilesTotal`, `ChunksDone`, `EstimatedRemainingSeconds` | 0-3 | The run in progress; the estimate is -1 when unknown |
  | `IContextService` | `ListExcludedFilesAsync(ExcludedFilesRequest)` | | One page of a root's excluded files and folders, secret-bearing first |
  | `IContextService` | `RebuildIndexAsync(string rootPath)` | | Starts a rebuild in the background (forwarded to the owner on a reader); progress arrives as notifications |
  | `IContextService` | `DeleteIndexAsync(string indexId)` | | `bool`; refused with `PolicyDenied` while another live host owns the index |
  | `IContextService` | `ListIndexesAsync()` | | `IndexInfo[]`: every index folder on the machine, for the retention view |
  | `ExcludedFilesRequest` (new) | `RootPath`, `Offset`, `Limit`, `SecretOnly` | 0-3 | `Limit` 0 means 200, capped at 1,000 |
  | `ExcludedFilesPage` (new) | `Items`, `TotalCount`, `HasMore` | 0-2 | |
  | `ExcludedFile` (new) | `Path`, `Reason`, `Description`, `IsSecretBearing`, `IsDirectory` | 0-4 | `Reason` is the name of `Pillion.Indexing`'s `ExclusionReason` (a string, so a new rule needs no contract change) |
  | `IndexInfo` (new) | `IndexId`, `RootPath`, `EmbeddingModelId`, `ChunkerVersion`, `DiskBytes`, `LastUsedUtc`, `RootExists`, `IsInUse`, `IsOpen` | 0-8 | `RootExists` is `bool?` (`null`: the drive is offline or there is no `state.json`) |

  Notification `indexStatusChanged`, host to client, one positional argument: the `IndexStatus`. Sent whenever the status
  changes, **at most twice a second** (changes within a window are coalesced; the latest is sent when it ends), and not
  replayed to a client that connects later, which calls `GetIndexStatusAsync`. Like `connectivityChanged` it is not a
  method on a contract interface; a 1.2 client registers no handler and ignores it. In the VSIX it surfaces as
  `IHostConnection.IndexStatusChanged`.

- **Workspace and IDE activity (work item 2.9, added within 1.3).** The host could not learn what is open or when the IDE
  is building, so the pipeline needed two additions; both are additive and the version stays 1.3. A host without them (an
  earlier 1.3 build or 1.2) answers `SetWorkspaceAsync` with method-not-found and ignores the notification; the extension
  logs that and carries on.

  | Type | Member | Order | Meaning |
  | --- | --- | --- | --- |
  | `IContextService` | `SetWorkspaceAsync(WorkspaceDescription)` | | What is open; the host resolves the repository roots and indexes them in the background (behind `features.indexing`). Called after every handshake and when the solution, its projects or the opened folder change; the same roots again restart nothing; an empty description closes the indexes |
  | `WorkspaceDescription` (new) | `SolutionPath`, `ProjectPaths`, `FolderPath` | 0-2 | Full paths: the `.sln`/`.slnx` (or `null`), the loaded projects' files (or folders), the Open Folder folder (or `null`) |
  | `IdeActivity` (new, `[Flags]`) | `None` 0, `Build` 1, `Debug` 2, `Test` 4 | | An integer on the wire |

  Notification `ideActivityChanged`, **client to host**, one positional argument: the `IdeActivity` holding every activity
  in progress (a set, so a lost or repeated notification cannot leave the host out of step). Sent after every handshake and
  whenever the set changes. The host pauses indexing while any flag is set (pause reasons `Build`, `Debug`, `Test`) and
  forgets the set when the client disconnects. It is the first client-to-host notification: the host registers it on every
  connection like an RPC target, and only after the handshake (`ContractV13Tests` keeps it from clashing with method names).
  The extension (`IndexingRelay` in Pillion.VisualStudio.Core) sends `Build` from the solution build events and `Debug`
  from the debugger's mode; it does not set `Test` yet (no supported Test Explorer API in the SDK packages it uses), so a
  test run pauses indexing only through its build.

- **Search (work item 2.11).** `SearchAsync` is unchanged (a 1.2 client keeps calling it); the new
  `SearchDetailedAsync(SearchRequest)` returns the hits with timings.

  | Type | Member | Order | Meaning |
  | --- | --- | --- | --- |
  | `IContextService` | `SearchDetailedAsync(SearchRequest)` | | Returns `SearchResponse` |
  | `SearchRequest` | `Roots`, `Partitions`, `Languages` | 2-4 | `string[]` filters; empty does not restrict |
  | `SearchRequest` | `Mode` | 5 | `SearchMode` (new enum): `Hybrid` 0 (default), `Lexical`, `Vector`, `Symbol` |
  | `SearchRequest` | `MaxPerFile` | 6 | 0 means the host default |
  | `SearchRequest` | `IncludeText` | 7 | Whether hits carry the full chunk text |
  | `SearchRequest` | `FocusFiles` | 8 | `string[]`: open and recently edited files for the retrieval boost |
  | `SearchResult` | `ChunkId` | 5 | Opaque to the client, unique across one host's indexes (index id and chunk row id) |
  | `SearchResult` | `Breadcrumb`, `Kind`, `Partition`, `Language` | 6-9 | The chunk's breadcrumb and chunker kind, its file's partition and language |
  | `SearchResult` | `LexicalRank`, `VectorRank` | 10-11 | One-based rank in each candidate list; 0 when absent from it |
  | `SearchResult` | `FusedScore` | 12 | The Reciprocal Rank Fusion score |
  | `SearchResult` | `Text` | 13 | `string?`: the full chunk text when `IncludeText` was set |
  | `SearchResponse` (new) | `Results`, `LexicalMs`, `VectorMs`, `FusionMs`, `TotalMs`, `Degraded` | 0-5 | `Degraded`: the time budget cut retrieval short |

- **Citations and retrieval in chat (work item 2.12).**

  | Type | Member | Order | Meaning |
  | --- | --- | --- | --- |
  | `Citation` (new) | `FilePath`, `StartLine`, `EndLine`, `ChunkId`, `Breadcrumb`, `ReplyOffset` | 0-5 | A zero-based, inclusive `file:line` span; `ReplyOffset` is the UTF-16 offset into the reply text where it applies, or -1 for the whole reply |
  | `ChatChunk` | `Citations` | 8 | `Citation[]`, on the final chunk only |
  | `ChatRequest` | `RetrievalMode` | 9 | `RetrievalMode` (new enum): `Auto` 0 (the host decides), `On`, `Off` |
  | `ChatRequest` | `MaxRetrievedChunks` | 10 | 0 means the host default |
  | `ChatRequest` | `ExcludedContextKeys` | 11 | `string[]`: the items removed in the Context Inspector before sending, by their `ContextKey`; the host leaves them out. Added with work item 2.12, after the wave-1 batch, still additive under 1.3 |
  | `ContextReportItemDto` | `ContextKey` | 9 | The key that removes the item (a `retrieved` chunk's id, or `repoMap`); empty for pins (removed by `PinIndex`) and items that cannot be removed. Added with work item 2.12 |
  | `AutocompleteHostCounters` | `PromptPrefixChecks`, `PromptPrefixReuses`, `PromptPrefixReuseRate` | 22-24 | Model requests whose document had an earlier model request to the same model; those whose prompt prefix (snippets, header, code before the caret) starts with the previous one's, which a resident local model's prompt cache can reuse; and the rate (issue #47, option 1). Added with work item 2.12 |

  A host's chat reply cites the retrieved chunks it sent: an explicit `path:line` or `path:start-end` reference in the reply
  that matches a sent chunk becomes a `Citation` with `ReplyOffset` just after the reference; when the reply names none, every
  chunk sent is listed with `ReplyOffset` -1 (implicit citations, which the Chat window shows collapsed under "Sources").

- **Mentions v2 (work item 2.13).** The kind stays a string on the wire; the new static class `MentionKinds` names every
  kind, the phase 1 ones (`file`, `folder`, `selection`, `errors`, `clipboard`) and the new `symbol`, `docs`,
  `solution`, `git`, `output`, `build` and `tests`. `MentionResolution.Truncated` already existed (1.2, order 7).

  | Type | Member | Order | Meaning |
  | --- | --- | --- | --- |
  | `MentionRequest` | `Qualifier` | 7 | The kind the user typed in `@kind:value` (an explicit disambiguation: no fuzzy fallback); empty for a bare `@name` |
  | `MentionRequest` | `Arguments` | 8 | `string[]`: for `git`, `diff` (default), `staged`, `log` with a count, or a revision range; for `output`, the pane |
  | `MentionResolution` | `Citations` | 12 | `Citation[]`: the spans the content came from (for example a symbol's definition and references) |

  No member was added for the pane text of `output`, `build` and `tests`: as the `MentionKinds` documentation says, the
  client sends it as `Payload`. For a `symbol` that is ambiguous, `Candidates` holds values that resolve to one definition,
  `Dotted.Name#<full path>:<1-based line>`, rather than paths; the client sends one back as the value after `@symbol:`.

- **Workspace queries (work item 2.14).** A host-to-VSIX method beside `FindReferencesAsync`. The VSIX enforces the
  budgets: one workspace query in flight, 2 s each, at most five a second; over the concurrency or rate limit a call fails
  at once with `Cancelled`, and one that reaches 2 s returns what it found with `Truncated`. The host falls back to the
  index's approximate edges. A 1.2 VSIX answers with method-not-found, which the host treats the same way.

  | Type | Member | Order | Meaning |
  | --- | --- | --- | --- |
  | `IWorkspaceQueryService` | `CallersOfAsync(CallersOfRequest)` | | The callers of a member, from the live Roslyn workspace |
  | `CallersOfRequest` (new) | `SymbolName`, `Container`, `FilePath`, `Line`, `MaxResults` | 0-4 | `Line` is -1 when unknown; `MaxResults` 0 means 100 |
  | `CallersOfResponse` (new) | `IsFound`, `Callers`, `Truncated` | 0-2 | `CallSite[]` ordered by file and line |
  | `CallSite` (new) | `FilePath`, `Line`, `Snippet`, `CallerName`, `IsApproximate` | 0-4 | `IsApproximate`: matched by name, not bound by the compiler |

- **Stubs.** `PillionErrorCode.NotImplemented` (enum value 8): the method is on the contract but this host does not
  implement it yet; the message names the feature and `Details` the work item.

### 1.2 (phase 1)

Additive only, batched from the phase 1 work items into one minor bump (`ContractVersion` says 1.2). A 1.1 peer is still
accepted by the handshake: it never calls the new services and methods, and ignores the new fields; a 1.2 peer reads their
defaults from a 1.1 peer (`ContractV12Tests`, `ContextContractTests`, `ContractV12InlineEditTests` and, over the real pipe,
`HandshakeCompatibilityTests` in the host tests). Each bullet below is its own work item's entry; a later phase 1 addition goes at the end of the list.
The feature flags `mentions` and `outboundLogView` that gate the new calls are settings, not contract members.

- **Autocomplete request context (PR 21, used by work item 1.2).** `CompletionRequest` grows the editor context the request pipeline
  captures; every member is optional and a 1.1 host ignores them.

  | Type | Member | Order | Meaning |
  | --- | --- | --- | --- |
  | `CompletionRequest` | `CaretLine` | 7 | Zero-based line of the caret in the document |
  | `CompletionRequest` | `CaretColumn` | 8 | Zero-based column of the caret |
  | `CompletionRequest` | `EnclosingDeclaration` | 9 | `string?`: the signature or header of the declaration that encloses the caret, when known |
  | `CompletionRequest` | `OpenDocuments` | 10 | `string[]`: full paths of the documents open in the IDE, most recently used first |
  | `CompletionRequest` | `RecentEdits` | 11 | `string[]`: short descriptions of the latest edits (file, line, changed text), newest first |
  | `CompletionRequest` | `AllowMultiLine` | 12 | A multi-line completion is wanted at this caret position |
  | `CompletionRequest` | `StopSequences` | 13 | `string[]`: stop sequences beyond the model family's defaults |

- **Mentions and context assembly (work item 1.10).** `IContextService` is unchanged; its DTOs and `ChatRequest` grow.

  | Type | Member | Order | Meaning |
  | --- | --- | --- | --- |
  | `TaskClass` | `Summarise` | enum value 6 | History compression (Fast online, Local-fast offline); the enum only appends |
  | `MentionRequest` | `Kind` | 1 | `file`, `folder`, `selection`, `errors`, `clipboard`; empty reads it from the mention text, and a bare `@name` is a fuzzy match |
  | `MentionRequest` | `WorkspaceRoots` | 2 | `string[]`: the solution or folder roots; relative paths resolve against them |
  | `MentionRequest` | `CandidateFiles` | 3 | `string[]`: the solution or folder file list for fuzzy `file` and `folder` matching (phase 1 has no index) |
  | `MentionRequest` | `Payload` | 4 | `string?`: the selection, the clipboard text, or the diagnostics as JSON lines for `errors` |
  | `MentionRequest` | `MaxTokens` | 5 | Truncation limit for the content (0 is the host default) |
  | `MentionRequest` | `ApprovedSensitive` | 6 | The user approved pinning an excluded or secret file |
  | `MentionResolution` | `Kind` | 4 | The kind it resolved as |
  | `MentionResolution` | `DisplayName` | 5 | Short name for the pin badge |
  | `MentionResolution` | `TokenCount` | 6 | Tokens of `Content` for the default model, after truncation |
  | `MentionResolution` | `Truncated` | 7 | `Content` was cut on a line boundary to fit `MaxTokens` |
  | `MentionResolution` | `Candidates` | 8 | `string[]`: up to 10 paths when a fuzzy match is ambiguous |
  | `MentionResolution` | `Reason` | 9 | Why it is unresolved |
  | `MentionResolution` | `RequiresApproval` | 10 | Refused only because the file is excluded or looks secret; ask the user, then resolve again with `ApprovedSensitive` |
  | `MentionResolution` | `IsSensitive` | 11 | The content came from such a file with approval; it is never sent to a cloud model |
  | `ChatRequest` | `Pins` | 4 | `MentionResolution[]` already resolved and pinned by the client |
  | `ChatRequest` | `CurrentFile` | 5 | Path of the file open in the editor |
  | `ChatRequest` | `CurrentFileRegion` | 6 | `string?`: the text around the caret, or the selection |
  | `ChatRequest` | `ConversationId` | 7 | Key of the cached rolling history summary |
  | `ChatRequest` | `ProjectRules` | 8 | `string?`: `.pillion/rules.md`, supplied only after workspace trust |

  A request with pins, a current file or project rules is assembled by the gateway (budget, priority, per-item caps,
  history summary); a plain request is sent as before. See `src/Pillion.ModelGateway/README.md`, Context assembly.

- **Inline edits (work item 1.8).** A new VSIX-to-host service and its DTOs. A 1.1 host answers it with
  StreamJsonRpc's method-not-found error.

  | Type | Member | Order | Meaning |
  | --- | --- | --- | --- |
  | `IInlineEditService` (new, VSIX to host) | `EditAsync(InlineEditRequest, CancellationToken)` | | Streams `InlineEditChunk`s: the proposed replacement for the request's scope (work item 1.8) |
  | `IInlineEditService` | `CancelEditAsync(string requestId, CancellationToken)` | | Cancels an in-flight edit. Not `CancelAsync`: the host exposes every target under its CLR method names in one namespace, so that name would reach `IModelService.CancelAsync` |
  | `InlineEditRequest` (new) | `RequestId`, `FilePath`, `LanguageId`, `ScopeText`, `ScopeStartLine`, `ScopeStartColumn`, `TextBefore`, `TextAfter`, `Instruction`, `Command`, `Diagnostics`, `PreviousProposal`, `ModelId`, `Profile` (defaults to `Auto`), `ConversationId` | 0-14 | The scope (selection, else enclosing member, else current line), its capped context, the instruction and slash shortcut, the diagnostics for `/fix`, the proposal being refined, a model override and the inline session id |
  | `InlineEditChunk` (new) | `RequestId`, `Text`, `IsFinal`, `Explanation`, `ModelId`, `ProviderId`, `PromptTokens`, `CompletionTokens`, `Error`, `RejectedBlocks`, `Warning`, `ResetText` | 0-11 | A delta of the proposed replacement (plain code); `ResetText` discards the text received so far (the early preview changed); the final chunk carries the explanation, token totals, bounced-block count and any warning |
  | `InlineEditDiagnostic` (new) | `Line`, `Severity`, `Code`, `Message` | 0-3 | A diagnostic on the scope (zero-based document line) |
  | `InlineEditCommand` (new enum) | `None` 0, `Doc` 1, `Test` 2, `Fix` 3, `Explain` 4 | | The slash shortcut; an integer on the wire |

  Stream rules for `EditAsync`: concatenating every chunk's `Text`, starting again at each chunk with `ResetText`, gives
  the proposal once the final chunk has arrived. Every failure, before or after preview text, fails the call with a
  `PillionRpcException` (the client sees the `PillionError` as the error data); discard any preview then. `Error` is
  there for parity with `ChatChunk` and is not set by a 1.2 host. `/explain` sends no text, only the
  final chunk's `Explanation`; `/test` sends new test code (not a replacement of the scope) and says where it belongs
  in `Explanation`. See `src/Pillion.Host/README.md` (Inline edits) for the host's behaviour.
- **Settings-changed notification and Think harder (work item 1.15).** Notification `settingsChanged`, host to client, one positional argument: a `string[]` of the names of the top-level
  `settings.json` sections that changed. Sent after the handshake, debounced to 250 ms, after a save and after an
  external edit of the file. Like `connectivityChanged` it is not a method on a contract interface, so the interface
  shape tests are unchanged, and a 1.1 client that registers no handler ignores it. See the host README.
- Setting `routing.thinkHarder` (boolean, default `false`) in `settings.json`; it is read through
  `ISettingsService`, so no DTO changes.
- **Context Inspector and Outbound log view (work item 1.13).** Two read methods on `IDiagnosticsService` and three new
  DTOs; no existing member changes. A 1.1 host answers them with StreamJsonRpc's method-not-found error, which the Chat
  window shows in the inspector and the log view.

  | Type | Member | Order | Meaning |
  | --- | --- | --- | --- |
  | `IDiagnosticsService` | `GetOutboundRecordsAsync(int maxCount, CancellationToken)` | | The Outbound log's records, newest first; `maxCount` 0 or less means 200 |
  | `IDiagnosticsService` | `PreviewContextAsync(ChatRequest, CancellationToken)` | | The context the request would be assembled into for the model it would be routed to, without calling a model |
  | `OutboundRecordDto` (new) | `TimestampUtc`, `RequestId`, `ProviderId`, `ModelId`, `TaskClass`, `IsLocal`, `PromptTokens`, `CompletionTokens`, `RedactionCount`, `ContextItems`, `ContextTrimmed`, `DurationMs`, `Outcome`, `ErrorCode`, `CacheHit` | 0-14 | One request sent to a model, counts only; `Outcome` is `Succeeded`, `Failed`, `Cancelled`, `TimedOut` or `Dropped` (a string, so a new outcome needs no contract change); `ErrorCode` is `PillionErrorCode?` |
  | `ContextReportDto` (new) | `ModelId`, `ProviderId`, `IsLocal`, `ContextWindow`, `OutputReserve`, `BudgetTokens`, `TotalTokens`, `Items`, `IsAssembled`, `Redacts`, `RedactionCount` | 0-10 | The preview: the routed model, the window, answer reserve and budget, the items in prompt order, whether sending assembles the request (pins, current file or rules), and the secrets redaction would replace for this model |
  | `ContextReportItemDto` (new) | `Kind`, `Name`, `Tokens`, `OriginalTokens`, `Truncated`, `Dropped`, `Note`, `PinIndex`, `RedactionCount` | 0-8 | One item: `Kind` is `system`, `rules`, `repoMap`, `pin`, `currentFile`, `retrieved`, `summary`, `history` or `query` (a string); `PinIndex` is the pin's index in `ChatRequest.Pins` (-1 for other items) so the client can unpin it |

  Both methods need the `outboundLogView` feature flag (the preview also `modelGateway`); see the host README, Diagnostics.
- **Autocomplete telemetry (work item 1.5).** New service `IDiagnosticsService` (VSIX to host, read-only) with `GetAutocompleteCountersAsync`, returning the new DTO
  `AutocompleteHostCounters`: the gateway's autocomplete counters since the host started (requests, cache hits,
  type-through hits, misses, hit rate) and, over the Outbound log's last five minutes, the p50 and p95 time to first
  token and total duration and the prompt tokens counted versus estimated. Counts and durations only (work item 1.5).

  | Type | Member | Order | Meaning |
  | --- | --- | --- | --- |
  | `AutocompleteHostCounters` | `ResidentModels` | 21 | `ResidentModelInfo[]`: the local models loaded now (PR 35) |
  | `ResidentModelInfo` (new) | `ProviderId`, `ModelId`, `ContextLength`, `VramBytes`, `KeepAlive`, `IsPinned` | 0-5 | The provider and model, the loaded context size in tokens (`int?`, null when unknown), the bytes of GPU memory it uses (0 on the CPU), the keep-alive (`-1` for ever, otherwise the time left such as `29m`) and whether it stays loaded for ever (the autocomplete model) |

### 1.1 (phase 0, wave 3)

Additive only; a 1.0 peer is still accepted by the handshake and ignores the new fields, and a 1.1 peer reads their
defaults from a 1.0 peer (`ContractV11Tests`, `HandshakeCompatibilityTests` in the host tests).

| Type | Member | Order | Meaning |
| --- | --- | --- | --- |
| `ChatChunk` | `ModelId` | 3 | The provider's id of the model that produced the chunk; the reply header names it |
| `ChatChunk` | `ProviderId` | 4 | The provider that produced the chunk |
| `ChatChunk` | `PromptTokens` | 5 | Prompt tokens, on the final chunk (0 when unknown) |
| `ChatChunk` | `CompletionTokens` | 6 | Generated tokens, on the final chunk (0 when unknown) |
| `ChatChunk` | `Error` | 7 | `PillionError?`: the failure that ended the stream after some text, on the final chunk |
| `CompletionChunk` | `ModelId`, `ProviderId`, `PromptTokens`, `CompletionTokens`, `Error` | 3-7 | As on `ChatChunk` |
| `ModelDescriptor` | `ModelId` | 5 | The provider's own model id; `Id` is now `providerId/modelId`, unique across providers |
| `ModelDescriptor` | `IsAvailable` | 6 | Whether the model can be used now (policy allows it, its provider is reachable, it is installed) |
| `ModelDescriptor` | `ContextWindow` | 7 | Context window in tokens (0 when unknown) |
| `ModelDescriptor` | `Digest` | 8 | `string?`: the model's content digest when the provider reports one (Ollama does) |
| `ConnectivityState` (new enum) | `Online` 0, `Degraded` 1, `Offline` 2, `LocalOnly` 3 | | The argument of the host-to-VSIX notification `connectivityChanged` (an integer on the wire) |

`connectivityChanged` is a notification, not a method on a contract interface, so the interface shape tests are
unchanged. The host sends it once after every accepted handshake and on every transition; a 1.0 client registers no
handler for it, and StreamJsonRpc ignores a notification nobody handles.

### 1.0 (phase 0, wave 1)

The initial contract: the five VSIX-to-host and three host-to-VSIX interfaces, their DTOs, `PillionError`, `HostState`
and the `hostStateChanged` notification.

## DataContract convention

- DTOs are `sealed` classes with public get/set properties, annotated with `[DataContract]`. Every property has
  `[DataMember(Order = n)]`, with `n` counting up from 0 in declaration order.
- MessagePack-C#'s `StandardResolver` honours these attributes and serialises each DTO as an array indexed by
  `Order`, so the wire format is stable without a MessagePack dependency in this assembly.
- Never reuse or change an existing `Order`; append new members at the end.
- Non-nullable strings default to `string.Empty` and collections to an empty array; optional members are
  nullable (`T?`).
- Line and column numbers are zero-based.

## Interface convention

- Every method takes a trailing `CancellationToken` (asserted by a test).
- Streams are `IAsyncEnumerable<T>`; everything else returns `Task` or `Task<T>`.
- Every public type and member has an XML doc comment (the build fails otherwise).
