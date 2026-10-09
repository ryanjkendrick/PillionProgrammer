# Pillion.Contracts

The RPC contract between the Visual Studio extension (`Pillion.VisualStudio`, .NET Framework 4.8 inside
`devenv.exe`) and Pillion Host (`Pillion.Host`, .NET 10). This assembly targets `netstandard2.0` and is compiled
into both processes.

## What lives here

- Service interfaces. VSIX to host: `IModelService`, `IInlineEditService`, `IContextService`, `IAgentService`,
  `ISettingsService`, `IHostLifecycleService`. Host to VSIX: `IEditorService`, `IApprovalService`, `IWorkspaceQueryService`.
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

### 1.2 (pending, phase 1 wave 2)

Additive only; `ContractVersion` stays 1.1 until the lead bumps it at the wave boundary. A 1.1 VSIX never calls the new
service, and a 1.1 host answers it with StreamJsonRpc's method-not-found error.

| Type | Member | Order | Meaning |
| --- | --- | --- | --- |
| `IInlineEditService` (new, VSIX to host) | `EditAsync(InlineEditRequest, CancellationToken)` | | Streams `InlineEditChunk`s: the proposed replacement for the request's scope (work item 1.8) |
| `IInlineEditService` | `CancelAsync(string requestId, CancellationToken)` | | Cancels an in-flight edit |
| `InlineEditRequest` (new) | `RequestId`, `FilePath`, `LanguageId`, `ScopeText`, `ScopeStartLine`, `ScopeStartColumn`, `TextBefore`, `TextAfter`, `Instruction`, `Command`, `Diagnostics`, `PreviousProposal`, `ModelId`, `Profile` (defaults to `Auto`), `ConversationId` | 0-14 | The scope (selection, else enclosing member, else current line), its capped context, the instruction and slash shortcut, the diagnostics for `/fix`, the proposal being refined, a model override and the inline session id |
| `InlineEditChunk` (new) | `RequestId`, `Text`, `IsFinal`, `Explanation`, `ModelId`, `ProviderId`, `PromptTokens`, `CompletionTokens`, `Error`, `RejectedBlocks`, `Warning`, `ResetText` | 0-11 | A delta of the proposed replacement (plain code); `ResetText` discards the text received so far (the early preview changed); the final chunk carries the explanation, totals, bounced-block count and any warning |
| `InlineEditDiagnostic` (new) | `Line`, `Severity`, `Code`, `Message` | 0-3 | A diagnostic on the scope (zero-based document line) |
| `InlineEditCommand` (new enum) | `None` 0, `Doc` 1, `Test` 2, `Fix` 3, `Explain` 4 | | The slash shortcut; an integer on the wire |

Stream rules for `EditAsync`: concatenating every chunk's `Text`, starting again at each chunk with `ResetText`, gives
the proposal once the final chunk has arrived. A failure before any text fails the call with a `PillionRpcException`;
a failure after text is a final chunk with `Error` and `ResetText` set and no text. `/explain` sends no text, only the
final chunk's `Explanation`; `/test` sends new test code (not a replacement of the scope) and says where it belongs
in `Explanation`. See `src/Pillion.Host/README.md` (Inline edits) for the host's behaviour.

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
