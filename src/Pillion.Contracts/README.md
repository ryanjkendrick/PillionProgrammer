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
