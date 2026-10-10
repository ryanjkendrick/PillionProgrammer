# Pillion.Host

Pillion Host is the out-of-process .NET 10 server that the Visual Studio extension launches. It serves the
host-side contracts in `Pillion.Contracts` over StreamJsonRpc on a named pipe and calls back into the VSIX for
editor, approval and workspace operations.

```
Program.cs                          Parses the command line, composes the host, returns the exit code
PillionHostBuilderExtensions.cs     AddPillionHost: logging + lifecycle + RPC server (used by Program and tests)
Lifecycle/                          HostStateMachine, HostShutdown (exit code), ParentProcessWatcher
Context/                            ContextRpcService (IContextService): @file, @folder, @selection, @clipboard, @errors
Rpc/                                RpcServer, RpcConnection, PillionJsonRpc, handshake, IRpcTarget, IVsixConnection
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

`SearchAsync` returns an empty array and `GetIndexStatusAsync` reports nothing indexed (the index arrives in phase 2);
`PauseIndexingAsync` does nothing. The chat side of context (pins, current file, rules, history summary) is in the Model
Gateway README, Context assembly.

## Diagnostics (`Models/DiagnosticsRpcService.cs`)

`DiagnosticsRpcService` serves `IDiagnosticsService`, read-only and never with prompt or completion text:

- `GetAutocompleteCountersAsync` (work item 1.5): the gateway's autocomplete counters and the last five minutes of
  autocomplete records in the Outbound log.
- `GetOutboundRecordsAsync(maxCount)` (work item 1.13): the Outbound log's records, newest first, at most `maxCount` (0 or
  less means 200; the ring buffer keeps `ModelGatewayOptions.OutboundLogCapacity`). Each is time, request id, provider,
  model, task class, locality, prompt and completion tokens, redactions, context items and how many were trimmed,
  duration, outcome with its error code, and whether autocomplete served it from the cache. The Chat window's Outbound log
  view reads it.
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

Event id ranges: 100-199 lifecycle, 200-299 RPC, 600-609 inline edits.

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
