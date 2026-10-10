# Pillion Programmer

Pillion Programmer is a free, MIT-licensed Visual Studio extension that adds AI pair programming: inline
completion, chat and an agent that can edit your solution with your approval. It works offline-first with local
models and can also use cloud providers you configure. It runs on Visual Studio 2022 (17.14 or later) and Visual
Studio 2026.

The design document is here:
[Pillion Programmer design document](https://claude.ai/code/artifact/02f3ee51-8712-47d2-89af-3c7f6f1559fb).

> Status: early scaffolding (phase 0). The extension supervises Pillion Host and has a Chat window and Options pages;
> chat works end to end with a local Ollama model. The OpenAI-compatible cloud adapter is still being built.

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
nuget.config                     Package sources: nuget.org, plus Microsoft's vssdk feed for the VS test framework only
.github/workflows/ci.yml         CI: build the full solution, run tests, upload the .vsix, VS integration tests on 18.x and 17.14
tools/vs-tests/                  Deploy the VSIX to an Experimental Instance and run the VS integration tests
docs/                            Project documents
src/
  Pillion.Contracts/             RPC interfaces and DTOs (netstandard2.0); see its README for the versioning rule
  Pillion.Common/                Host-side settings, secrets, redaction, connectivity (net10.0, placeholder)
  Pillion.ModelGateway/          Model providers and routing (net10.0, placeholder)
  Pillion.Host/                  Pillion Host console process (net10.0); see its README for the launch protocol
  Pillion.VisualStudio/          The Visual Studio extension (net48, produces Pillion.VisualStudio.vsix)
  Pillion.VisualStudio.Core/     Host supervisor, RPC client and logger for the extension (net48, no VS SDK)
tests/
  Pillion.Contracts.Tests/       Contract shape tests and MessagePack round trips for every DTO
  Pillion.Common.Tests/
  Pillion.ModelGateway.Tests/
  Pillion.Host.Tests/
  Pillion.VisualStudio.Tests/    Supervisor, launch protocol and signature policy tests (net48)
  Pillion.VisualStudio.IntegrationTests/         Tests inside the Visual Studio 2026 Experimental Instance; see its README
  Pillion.VisualStudio.IntegrationTests.VS2022/  The same tests built for Visual Studio 2022 17.14
  Pillion.FakeHost/              Stand-in host process used by Pillion.VisualStudio.Tests (not a test project)
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

### Visual Studio integration tests

`dotnet test` above does not start Visual Studio. The integration tests run inside the Experimental Instance of
Visual Studio 2026 or Visual Studio 2022 17.14, with the built VSIX installed there first, and need no model:

```
pwsh tools/vs-tests/Install-ToExp.ps1 -DevEnvPath "C:\Program Files\Microsoft Visual Studio\18\Community\Common7\IDE\devenv.exe"
pwsh tools/vs-tests/Invoke-IdeTests.ps1 -DevEnvPath "C:\Program Files\Microsoft Visual Studio\18\Community\Common7\IDE\devenv.exe"
```

Use the 2022 `devenv.exe` for 17.14. Results go to `TestResults/ide-VS18` or `TestResults/ide-VS2022`; CI runs both
versions. See [tests/Pillion.VisualStudio.IntegrationTests/README.md](tests/Pillion.VisualStudio.IntegrationTests/README.md).

To debug the extension, open `PillionProgrammer.sln` in Visual Studio, set `Pillion.VisualStudio` as the startup
project and press F5; it deploys to and starts the experimental instance (`/rootsuffix Exp`). Open any solution or
folder, then check **View > Output > Pillion Programmer** and **Tools > Pillion: Show Status**.

The extension launches Pillion Host when it loads and shows its state in the status bar ("Pillion: Ready",
"Pillion: Restarting (2/3)", "Pillion: Failed, ..."). **Tools > Pillion: Retry** restarts it after a failure and
**Tools > Pillion: Open Logs** opens `%LOCALAPPDATA%\PillionProgrammer\logs`, where the extension writes
`vsix-YYYYMMDD.log`. Building the VSIX publishes the host self-contained (win-x64) with `dotnet publish` and packages
it under `Host\` in the VSIX; that step is skipped when no host source has changed since the last publish.

**Tools > Pillion: Open Chat** (default shortcut Ctrl+Alt+Shift+P, then C; also **View > Other Windows > Pillion
Chat**) opens the Chat window: pick a model (or **Auto**, which lets Pillion Host choose), type a message, Enter
sends and Shift+Enter starts a new line. Each reply is headed with the model and provider that answered, and the
status line shows the connectivity state (Online, Degraded, Offline or Local-only), as does the status bar
("Pillion: Ready, Online"). **Tools >
Options > Pillion Programmer** has the **Providers and keys** page (API keys go to the Windows Credential Manager under
`PillionProgrammer/<provider id>`, never to settings files) and the **Privacy** page (Local-only mode and the cloud
switch per task class) and the **Models and routing** page (a profile and a cloud switch per task class, an optional
model id per profile, the "Think harder by default" placeholder and the GPU tier override). The pages are a view
over Pillion Host's `settings.json` and say "Host not ready" until the host is running; the host tells the extension
when settings change (`settingsChanged`), so the chat model picker, open pages and autocomplete follow a save or an edit
of the file without a Refresh press. `tests/Pillion.FakeHost` serves canned models and in-memory settings, so it can stand in for
`Host\Pillion.Host.exe` in the experimental instance when you want to try the UI without a model.

To publish Pillion Host as a self-contained win-x64 app:

```
dotnet publish src/Pillion.Host/Pillion.Host.csproj -p:PublishProfile=win-x64
```

## How to run it today

You need [Ollama](https://ollama.com) 0.6.4 or later running on this machine with at least one chat model pulled (for
example `ollama pull gemma4:e2b`).

1. **Build** the full solution from a Visual Studio 2026 Developer Command Prompt (see above):
   `msbuild PillionProgrammer.sln /restore /p:Configuration=Release /m`. This also publishes Pillion Host into the VSIX.
2. **Deploy to the Experimental Instance.** Either open `PillionProgrammer.sln` in Visual Studio, set
   `Pillion.VisualStudio` as the startup project and press F5, or from the command line build the extension project
   with deployment switched on and start the instance yourself:

   ```
   msbuild src\Pillion.VisualStudio\Pillion.VisualStudio.csproj /p:Configuration=Release /p:DeployExtension=true
   devenv /rootsuffix Exp
   ```

   Open any solution or folder; the status bar shows "Pillion: Ready, Online" once Pillion Host is up.

   **Visual Studio 2022 17.14.** The build uses Visual Studio 2026's MSBuild, so its deployment reaches only the
   Visual Studio 2026 Experimental Instance. For Visual Studio 2022, install the built VSIX with 2022's installer and
   then rebuild that instance's configuration (without the second command, 17.14 kept its cached configuration and
   did not load the package):

   ```
   "C:\Program Files\Microsoft Visual Studio\2022\Community\Common7\IDE\VSIXInstaller.exe" /quiet /rootSuffix:Exp src\Pillion.VisualStudio\bin\Release\net48\Pillion.VisualStudio.vsix
   "C:\Program Files\Microsoft Visual Studio\2022\Community\Common7\IDE\devenv.exe" /rootsuffix Exp /updateconfiguration
   ```

   Remove it again with `VSIXInstaller.exe /quiet /rootSuffix:Exp /uninstall:PillionProgrammer.e4d0d8fe-694c-4b1f-bcb1-31b346cda51e`.
   The Gate 0 script does all of this when given the 2022 `devenv.exe`:
   `pwsh tools/gate0/Invoke-Gate0.ps1 -DevEnvPath "C:\Program Files\Microsoft Visual Studio\2022\Community\Common7\IDE\devenv.exe"`
   (add `-UninstallAfter` to remove the extension at the end; see [docs/gate-0-report.md](docs/gate-0-report.md)).
3. **Add a provider.** **Tools > Options > Pillion Programmer > Providers and keys** (in Visual Studio 2026 the
   Options window shows a **Providers and keys** link under Pillion Programmer that opens the page): **Add**, then set the kind to
   `ollama`, the endpoint to `http://localhost:11434` and the models to the model you pulled (for example
   `gemma4:e2b`), and press **Save**. **Test connection** checks the Ollama version and lists what is installed.
   The settings live in `%LOCALAPPDATA%\PillionProgrammer\settings.json`, shared by every Visual Studio instance.
4. **Open Pillion Chat** with **Tools > Pillion: Open Chat** (Ctrl+Alt+Shift+P, then C). Press **Refresh** if the
   picker was opened before the provider was saved, pick the model (or Auto), and send a message. The reply streams in
   under a header such as "gemma4:e2b via Ollama, local".

Logs for both processes are in `%LOCALAPPDATA%\PillionProgrammer\logs` (**Tools > Pillion: Open Logs**).

## Feature flags

Every capability sits behind a feature flag, so a user or a repository policy can switch it off. All flags default to
on. The user sets them in the `features` section of `%LOCALAPPDATA%\PillionProgrammer\settings.json`:

```json
{
  "features": {
    "chat": true,
    "modelGateway": true,
    "connectivityProbes": true,
    "cloudProviders": true,
    "ollamaProvider": true,
    "openAICompatibleProvider": true,
    "anthropicProvider": true,
    "geminiProvider": true,
    "mistralProvider": true,
    "azureOpenAIProvider": true,
    "autocomplete": true,
    "inlineChat": true
  }
}
```

| Flag | When it is off |
| --- | --- |
| `chat` | **Tools > Pillion: Open Chat** and **View > Other Windows > Pillion Chat** are disabled, and an open chat window shows "Chat is disabled by settings or policy" and cannot send. Pillion Host keeps running. |
| `modelGateway` | Every `IModelService` call (chat, completion, listing models) fails with `PolicyDenied` and a message that names the flag. Cancelling stays harmless. |
| `connectivityProbes` | Cloud providers are not probed; the connectivity state stays as it is with no cloud provider (Online) and the cloud providers stay routable. |
| `cloudProviders` | Every provider that does not run on this machine is hidden from routing (listed as unavailable with the reason) and nothing is probed. An OpenAI-compatible server on `localhost` or another loopback or `.local` address is local and stays. |
| `ollamaProvider` | Providers of kind `ollama` are not built or routed to. |
| `openAICompatibleProvider` | Providers of kind `openai-compatible` and `openai` (one adapter) are not built or routed to. |
| `anthropicProvider` | Providers of kind `anthropic` are not built or routed to. |
| `geminiProvider` | Providers of kind `gemini` are not built or routed to. |
| `mistralProvider` | Providers of kind `mistral` (chat and Codestral fill-in-the-middle) are not built or routed to. |
| `azureOpenAIProvider` | Providers of kind `azure-openai` are not built or routed to. |
| `autocomplete` | `IModelService.CompleteAsync` (ghost-text autocomplete) fails with `PolicyDenied` and a message that names the flag; chat and model listing are unaffected. The extension stops asking for suggestions (it reads the flag when Pillion Host becomes ready and when the Tools menu opens; an absent flag means on). |
| `inlineChat` | `IInlineEditService.EditAsync` (inline chat edits, `/doc`, `/test`, `/fix`, `/explain`) fails with `PolicyDenied` and a message that names the flag. Chat, autocomplete and model listing are unaffected. |

A repository can switch flags off for everyone who opens it with `disabledFeatures` in `.pillion/policy.json`, next to
`localOnly`, `disabledTools`, `exclude` and `denyCommands`:

```json
{ "disabledFeatures": ["cloudProviders", "chat"] }
```

The lists of all policies in scope are unioned, and a policy can only turn a flag off, never on; names this version does
not know are kept and ignored. Flags take effect on the next request: Pillion Host watches the settings file and the
policies. The extension reads the result, with policies applied, through the read-only virtual setting
`effective.features` of `ISettingsService.GetAsync`; it is re-read when Pillion Host becomes ready, on **Refresh** in the
chat window and when the Tools menu opens. `settings.json` stays at version 1: a file without a `features` section gets
the defaults when it is loaded, and unknown flags in the section are preserved.

## Ghost-text autocomplete

With a fill-in-the-middle model available (for example `ollama pull qwen2.5-coder:1.5b`, added to an Ollama provider),
Pillion suggests code as grey text at the caret in any text editor: single-line by default, multi-line after a line that
opens a block, on an empty line inside a body, after a declaration signature, and after accepting a suggestion at the end
of a line. **Tab** accepts, **Esc** dismisses, **Ctrl+Right** accepts the next word, and **Alt+]** and **Alt+[** ("Pillion:
Next Suggestion" and "Pillion: Previous Suggestion", rebindable under Tools > Options > Keyboard) switch between the
suggestion and its first line only (Visual Studio 2026; Visual Studio 2022 does not cycle between proposals). Accepting
a suggestion asks for the next one at once. Requests wait 200 ms after the last keystroke; when the p95 latency over
five minutes is above 1.5 s the wait rises to 350 ms and suggestions are single-line until it recovers.

Autocomplete measures itself, on your machine only; nothing is sent anywhere. **Tools > Pillion: Show Status** has an
"Autocomplete" section with the last five minutes (requests sent, suggestions shown and the show rate, accepted and the
acceptance rate, cancelled by typing, errors, p50 and p95 time to the first chunk and to the suggestion, and the
degradation state with its reason and since when) and the host's numbers beside them (cache hit rate, p50 and p95 time to
first token, prompts counted versus estimated). The status bar adds ", suggestions slow" while autocomplete is degraded
and ", waiting for suggestion" while a request has waited more than a second for the model. While autocomplete is in
use, every five minutes the Output pane gets one summary line and
`%LOCALAPPDATA%\PillionProgrammer\logs\metrics-YYYYMMDD.jsonl` gets one JSON line: counts, durations, language
identifiers and the degraded flag, never file names, prompts, code or repository names. Metrics files are kept for seven
days or 100 MB, whichever is less. The opt-in telemetry upload of the design document is not part of this.

No suggestions are requested in generated and designer files (`.g.cs`, `.g.i.cs`, `.Designer.cs`, `*.min.js` and files
whose first lines say they are auto-generated), in files over 1 MB, while a completion list has an item selected, or in
languages switched off in `settings.json` (identifiers as in `src/Pillion.VisualStudio.Core/Rpc/DocumentLanguage.cs`):

```json
{ "autocomplete": { "disabledLanguages": [ "python" ], "announceSuggestions": true } }
```

A new suggestion is announced to screen readers with a UI Automation notification; `announceSuggestions: false`
switches that off.

**Visual Studio 2022** draws ghost text through IntelliCode, so it needs IntelliCode (part of every .NET workload) with
**Tools > Options > IntelliCode > General > Automatically generate code completions in the Editor** on. When either is
missing the status bar and the Pillion Output pane say "Inline suggestions need IntelliCode's automatic code completions
on Visual Studio 2022"; the check runs when the extension loads and again whenever the Tools menu opens. Visual Studio
2026 needs nothing extra.

## IDE signals

The extension watches what happens in the IDE and offers it to later features (`@errors`, the inline `/fix`, and the phase 2
indexer) as the `IIdeSignals` service (`src/Pillion.VisualStudio.Core/Signals`; ask the global service provider for
`typeof(IIdeSignals)`). Events, all raised on thread-pool threads in the order Visual Studio reported them: `BuildStarted`,
`BuildFinished(success, project count, duration)`, `BuildStateChanged` (with `BuildActivity.IsBuilding`, the hook the indexer
pauses on), `DocumentOpened`, `DocumentSaved`, `DocumentClosed`, `ActiveDocumentChanged` and `DiagnosticsChanged` (debounced:
500 ms of quiet, never held back more than 3 s). Queries: `GetDiagnosticsAsync(Solution | ActiveDocument)` and
`GetOutputPaneTextAsync("Build" | "Debug" | "Tests", maxChars)` (the end of the pane).

Solution diagnostics are read from the Error List, so they are only the entries the developer's Error List filters show
(its scope, Build + IntelliSense and the severity buttons); `DiagnosticsNote` says so. Active-document diagnostics add
Roslyn's compiler diagnostics for the open text, unsaved edits included; analyzer diagnostics come through the Error List.
If Visual Studio's workspace cannot be reached, only the Error List is used and the note says why. **Tools > Pillion: Show
Status** lists the build state and the last five signals (the service keeps the last 200 with time stamps).

## Local models and the GPU tier

Pillion Host detects the GPU tier once per run (CPU only, 8 GB, 12 GB, 16 GB, 24 GB or more) from `nvidia-smi`, else
DXGI, else the display adapter's registry entry, and picks the resident local models and the autocomplete prompt budget
from it (1,024 tokens when the completion model runs on the CPU, 2,048 on a GPU). Override the detection in
`settings.json`:

```json
{ "models": { "gpuTier": "auto" } }
```

`auto` (the default) detects; `cpu`, `8gb`, `12gb`, `16gb` and `24gb` force a tier. The completion model is kept loaded
(`keep_alive` -1) with a fixed context size per profile; see
[src/Pillion.ModelGateway/README.md](src/Pillion.ModelGateway/README.md#autocomplete) for the `OLLAMA_MAX_LOADED_MODELS`
setting that matches each tier.

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md).

## Licence

[MIT](LICENSE). Copyright (c) 2026 Ryan Kendrick.
