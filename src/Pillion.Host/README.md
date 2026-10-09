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

Event id ranges: 100-199 lifecycle, 200-299 RPC.

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
