# Pillion.Contracts

The RPC contract between the Visual Studio extension (`Pillion.VisualStudio`, .NET Framework 4.8 inside
`devenv.exe`) and Pillion Host (`Pillion.Host`, .NET 10). This assembly targets `netstandard2.0` and is compiled
into both processes.

## What lives here

- Service interfaces. VSIX to host: `IModelService`, `IContextService`, `IAgentService`, `ISettingsService`,
  `IHostLifecycleService`. Host to VSIX: `IEditorService`, `IApprovalService`, `IWorkspaceQueryService`.
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
commit.

## Changelog

### 1.2 (pending)

Additive only, batched with the other phase 1 contract additions into one minor bump at the wave boundary
(`ContractVersion` still says 1.1 until then). Each bullet below is its own work item's entry.

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
