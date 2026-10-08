# Pillion Programmer

Pillion Programmer is a free, MIT-licensed Visual Studio extension that adds AI pair programming: inline
completion, chat and an agent that can edit your solution with your approval. It works offline-first with local
models and can also use cloud providers you configure. It runs on Visual Studio 2022 (17.14 or later) and Visual
Studio 2026.

The design document is here:
[Pillion Programmer design document](https://claude.ai/code/artifact/02f3ee51-8712-47d2-89af-3c7f6f1559fb).

> Status: early scaffolding (phase 0). The extension loads and shows its version; no AI features yet.

## Architecture in one paragraph

Two processes. **Pillion.VisualStudio** is a classic in-process VSSDK extension (.NET Framework 4.8, built
against the 17.14 VS SDK) that background-loads with a solution or folder. **Pillion Host** is a .NET 10 console
process, published self-contained, that will run the models, indexing and agent and talk to the extension over a
named pipe. **Pillion.Contracts** (`netstandard2.0`) defines the RPC interfaces and DTOs and is compiled into both.

## Repository layout

```
PillionProgrammer.sln            Full solution (needs Visual Studio's MSBuild because of the VSIX project)
PillionProgrammer.NoVsix.slnf    Solution filter without the VSIX, for dotnet build / dotnet test
global.json                      .NET SDK version (10.0.401, roll forward to latest feature band)
Directory.Build.props            Shared build settings (nullable, warnings as errors, analyzers, versioning)
Directory.Packages.props         Central package versions (all pinned)
.github/workflows/ci.yml         CI: build the full solution, run tests, upload the .vsix
docs/                            Project documents
src/
  Pillion.Contracts/             RPC interfaces and DTOs (netstandard2.0); see its README for the versioning rule
  Pillion.Common/                Host-side settings, secrets, redaction, connectivity (net10.0, placeholder)
  Pillion.ModelGateway/          Model providers and routing (net10.0, placeholder)
  Pillion.Host/                  Pillion Host console process (net10.0); see its README for the launch protocol
  Pillion.VisualStudio/          The Visual Studio extension (net48, produces Pillion.VisualStudio.vsix)
tests/
  Pillion.Contracts.Tests/       Contract shape tests and MessagePack round trips for every DTO
  Pillion.Common.Tests/
  Pillion.ModelGateway.Tests/
  Pillion.Host.Tests/
```

## Building and testing locally

Prerequisites: Visual Studio 2026 (or Visual Studio 2022 17.14 or later) with the **Visual Studio extension
development** workload, and the .NET SDK named in `global.json`. The .NET 10 SDK requires MSBuild 18, so the full
solution builds from a Visual Studio 2026 Developer Command Prompt; with only Visual Studio 2022 installed, use the
solution filter for everything except the VSIX.

From a **Developer Command Prompt** or **Developer PowerShell** in the repository root:

```
REM Full solution, including the VSIX (Visual Studio's MSBuild; the VSIX project cannot build with dotnet build)
msbuild PillionProgrammer.sln /restore /p:Configuration=Release /m

REM Everything except the VSIX
dotnet build PillionProgrammer.NoVsix.slnf -c Release

REM Tests
dotnet test PillionProgrammer.NoVsix.slnf -c Release
```

The extension package is written to `src/Pillion.VisualStudio/bin/Release/net48/Pillion.VisualStudio.vsix`.

To debug the extension, open `PillionProgrammer.sln` in Visual Studio, set `Pillion.VisualStudio` as the startup
project and press F5; it deploys to and starts the experimental instance (`/rootsuffix Exp`). Open any solution or
folder, then check **View > Output > Pillion Programmer** and **Tools > Pillion: Show Status**.

To publish Pillion Host as a self-contained win-x64 app:

```
dotnet publish src/Pillion.Host/Pillion.Host.csproj -p:PublishProfile=win-x64
```

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md).

## Licence

[MIT](LICENSE). Copyright (c) 2026 Ryan Kendrick.
