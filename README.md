# Pillion Programmer

Pillion Programmer is a free, MIT-licensed Visual Studio extension that adds AI pair programming: inline
completion, chat and an agent that can edit your solution with your approval. It works offline-first with local
models and can also use cloud providers you configure. It runs on Visual Studio 2022 (17.14 or later) and Visual
Studio 2026.

The design document is here:
[Pillion Programmer design document](https://claude.ai/code/artifact/02f3ee51-8712-47d2-89af-3c7f6f1559fb).

> Status: phase 2 (Context) is built and its evidence is collected ([docs/gate-2-report.md](docs/gate-2-report.md)); the gate
> decision is the product owner's. The extension supervises Pillion Host and has a Chat window with saved conversations,
> `@`-mentions and cited answers, inline chat, ghost-text autocomplete, an Index window and Options pages. Chat, inline
> edits and autocomplete work end to end with local Ollama models and with the cloud providers you configure, and the Context
> Engine indexes your repository on your machine. The agent, terminal and browser features come in phases 3 and 4.

## Architecture in one paragraph

Two processes. **Pillion.VisualStudio** is a classic in-process VSSDK extension (.NET Framework 4.8, built
against the 17.14 VS SDK) that background-loads with a solution or folder. **Pillion Host** is a .NET 10 console
process, published self-contained, that runs the models, the indexing and the retrieval (the agent comes in phase 3) and
talks to the extension over a named pipe. **Pillion.Contracts** (`netstandard2.0`) defines the RPC interfaces and DTOs and is compiled into both.

## Repository layout

```
PillionProgrammer.sln            Full solution (needs Visual Studio's MSBuild because of the VSIX project)
PillionProgrammer.NoVsix.slnf    Solution filter without the VSIX, for dotnet build / dotnet test
global.json                      .NET SDK version (10.0.401, roll forward to latest feature band)
Directory.Build.props            Shared build settings (nullable, warnings as errors, analyzers, versioning)
Directory.Packages.props         Central package versions (all pinned, with the reason for each native dependency)
nuget.config                     Package sources: nuget.org, plus Microsoft's vssdk feed for the VS test framework only
THIRD-PARTY-NOTICES.txt          Licences of every third-party component that ships (native libraries and models included)
CONTRIBUTING.md                  Branches, pull requests and the rules that keep the build healthy
.github/workflows/ci.yml         CI: build the full solution, run tests, upload the .vsix, VS integration tests on 18.x and 17.14
docs/                            Design document export, phase plans, gate reports; see "Documentation" below
eval/                            Retrieval golden sets built from this repository's own code; see eval/README.md
spikes/                          Throw-away spikes (the Proposals API spike and its findings)
tools/
  vs-tests/                      Deploy the VSIX to an Experimental Instance and run the VS integration tests
  gate0/, gate1/, gate2/         Gate evidence scripts (gate2 also builds the generated repositories and the Gate 2 report)
  eval/Pillion.Eval/             The evaluation harness runner (retrieval slice); never shipped
src/
  Pillion.Contracts/             RPC interfaces and DTOs (netstandard2.0), contract 1.3; see its README for the versioning rule
  Pillion.Common/                Host-side settings, feature flags, secrets, redaction, connectivity (net10.0)
  Pillion.ModelGateway/          Model providers, routing, embeddings (Ollama, OpenAI-compatible, cloud, bundled ONNX), context assembly
  Pillion.LocalStore/            Encrypted SQLite (conversations.db and the index storage: chunks, FTS5, vectors, symbols); see its README
  Pillion.Indexing/              Repository discovery, ignore rules, hashing, index identity, writer election and retention; see its README
  Pillion.ContextEngine/         Chunkers (Roslyn, tree-sitter, Razor, SQL, window), symbols and repo map, hybrid retrieval; see its README
  Pillion.Host/                  Pillion Host console process (net10.0): RPC, indexing pipeline, watcher, mentions, conversations; see its README
  Pillion.VisualStudio/          The Visual Studio extension (net48, produces Pillion.VisualStudio.vsix)
  Pillion.VisualStudio.Core/     Host supervisor, RPC client, view models and logger for the extension (net48, no VS SDK)
tests/
  Pillion.Contracts.Tests/       Contract shape tests and MessagePack round trips for every DTO
  Pillion.Common.Tests/
  Pillion.ModelGateway.Tests/
  Pillion.Host.Tests/            Includes the generated-repository indexing and watcher benchmarks (Category=Benchmark and Model)
  Pillion.LocalStore.Tests/      Encryption, keys, WAL, writer queue, migrations, conversations repository, index storage
  Pillion.Indexing.Tests/        Exclusion rules, gitignore matching, hashing and sweeps, writer lock election, retention
  Pillion.ContextEngine.Tests/   Chunker, symbol and retrieval tests on fixtures, property-based tests (CsCheck), benchmarks
  Pillion.Eval.Tests/            Harness metrics, query parsing, the checked-in golden sets, a model smoke test
  Pillion.VisualStudio.Tests/    Supervisor, launch protocol, view models and signature policy tests (net48)
  Pillion.VisualStudio.IntegrationTests/         Tests inside the Visual Studio 2026 Experimental Instance; see its README
  Pillion.VisualStudio.IntegrationTests.VS2022/  The same tests built for Visual Studio 2022 17.14
  Pillion.FakeHost/              Stand-in host process used by Pillion.VisualStudio.Tests (not a test project)
```

### Documentation

| Page | What it covers |
| --- | --- |
| [docs/design-document.md](docs/design-document.md) | Export of the design document (the Claude Doc is the source of truth); phase 2 notes mark what changed |
| [docs/phase-2-plan.md](docs/phase-2-plan.md), [docs/gate-2-report.md](docs/gate-2-report.md) | The phase 2 plan, its decision log and the Gate 2 evidence |
| [docs/eval-retrieval-baseline.md](docs/eval-retrieval-baseline.md), [eval/README.md](eval/README.md) | The retrieval evaluation baseline and the golden-set format |
| [docs/index-window.md](docs/index-window.md) | The Index window, the status bar indicator and the Indexing options |
| [docs/pillionignore.md](docs/pillionignore.md) | What is indexed, `.pillionignore` syntax, built-ins, re-inclusion and secret files |
| [docs/mentions.md](docs/mentions.md) | The `@`-mention grammar and every kind |

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
("Pillion: Ready, Online"). Replies render as Markdown (Markdig): headings, lists, tables, quotes, inline code and
emphasis use Visual Studio's theme colours; images are never downloaded (their alt text is shown); a link opens in the
default browser only after a confirmation that shows its address. Each fenced code block is a read-only editor view
coloured for the language in its fence tag (`csharp`, `ts`, `json`, `powershell` and so on), with **Copy** and **Insert at
cursor**, which inserts into the active document at the caret (replacing any selection) as one undo step. Typing `@` in
the message box opens a picker at the caret (the kinds are listed under [Mentions](#mentions)), then up to 20 files of the
solution or folder matching what you type, ranked by file name and then by path (`@kind:value` picks the kind, as in
`@folder:src/Api`; Up and Down move, Enter or Tab pins, Esc closes). The picker's file list comes from the loaded projects of
the solution, or from the open folder, without `bin`, `obj`, `.git`, `node_modules` and `.vs`; it is refreshed when a
solution, project or folder opens or closes, and every two minutes at most when the picker opens (when you pick a name, the
host resolves it against the index). A pinned item becomes a chip above the message box with its token count and a remove button; pins
belong to the conversation and go with every message of it (`@errors` sends the Error List's entries as the developer's
filters show them). An ambiguous name lists its matches to pick from, an excluded or secret-looking file asks "This file
looks like it holds secrets. Pin it anyway?" (an approved one shows a lock and never goes to a cloud model), and a mention
that matches nothing says why under the pins. **Include current file** (on by default) sends the active document's path
and its selection, or the 60 lines around the caret, with each message, except while `@selection` is pinned. **Inspect**,
beside **Send**, shows the **Context Inspector**: the context the message would be sent with, assembled by Pillion Host for
the model it would route to without calling it, item by item with token counts, what would be cut or left out and why,
how many secrets would be replaced by placeholders for a cloud model, and a remove button per pin; **Send** there sends.
The **Outbound log** expander at the bottom lists the last 200 requests Pillion Host sent to any model from any task class,
newest first (time, task, provider and model, local or cloud, tokens, redactions, duration, outcome), refreshed after
each reply and with **Refresh**; **Copy** copies it as tab-separated text. Neither shows any prompt text. The
**Conversation** list at the top lists your saved conversations, newest first (**New**, **Rename**, **Delete**, and switching
in the list); they are saved by Pillion Host and survive a restart (see [Persistence](#persistence)). **Tools > Options >
Pillion Programmer** has the **Providers and keys** page (API keys go to the Windows Credential Manager under
`PillionProgrammer/<provider id>`, never to settings files), the **Privacy** page (Local-only mode, the cloud
switch per task class and **Delete all conversations**), the **Models and routing** page (a profile and a cloud switch per task class, an optional
model id per profile, the "Think harder by default" placeholder and the GPU tier override), the **Autocomplete** page and the
**Indexing** page (see [Context Engine](#context-engine)). The pages are a view
over Pillion Host's `settings.json` and say "Host not ready" until the host is running; the host tells the extension
when settings change (`settingsChanged`), so the chat model picker, open pages and autocomplete follow a save or an edit
of the file without a Refresh press. `tests/Pillion.FakeHost` serves canned models, in-memory settings, canned mention resolutions (a name with `ambiguous`
in it lists two files, `secret` or `.env` asks for approval, `missing` does not resolve) and a canned Context Inspector, so
it can stand in for
`Host\Pillion.Host.exe` in the experimental instance when you want to try the UI without a model; a message containing
`/markdown` gets a reply that exercises the Markdown rendering, and `/long` a reply of about 2,000 tokens.

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

Every capability sits behind a feature flag, so a user or a repository policy can switch it off. All 22 flags default to
on except `reranker`, which waits on a licence review. They are listed below in the order Pillion Host writes them
(`FeatureNames.All`). The user sets them in the `features` section of `%LOCALAPPDATA%\PillionProgrammer\settings.json`:

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
    "inlineChat": true,
    "mentions": true,
    "outboundLogView": true,
    "localStore": true,
    "indexing": true,
    "embeddings": true,
    "bundledEmbeddings": true,
    "workspaceQueries": true,
    "indexWindow": true,
    "retrieval": true,
    "reranker": false
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
| `inlineChat` | `IInlineEditService.EditAsync` (inline chat edits, `/doc`, `/test`, `/fix`, `/explain`) fails with `PolicyDenied` and a message that names the flag, and **Pillion: Inline Chat** is disabled (an open adornment says "Inline chat is disabled by settings or policy" and cannot send). Chat, autocomplete and model listing are unaffected. |
| `mentions` | The Chat window hides the `@` picker, the pins and **Include current file**, and sends no pins and no current file with a message (pins already made stay with their conversation and come back when the flag does). Pillion Host's `ResolveMentionAsync` is not gated; chat itself is unaffected. The twelve kinds are under [Mentions](#mentions). |
| `outboundLogView` | The Chat window hides **Inspect** and the **Outbound log**, and `IDiagnosticsService.GetOutboundRecordsAsync` and `PreviewContextAsync` fail with `PolicyDenied` and a message that names the flag. The Outbound log itself is still kept by Pillion Host. |
| `localStore` | Pillion Host does not create or open its encrypted Local Store (`conversations.db`, see `src/Pillion.LocalStore/README.md`): opening it fails with `PolicyDenied` and a message that names the flag, and nothing is written to disk. The conversation service (`IConversationService`) then fails with `PolicyDenied` naming the flag, and the Chat window keeps conversations in memory until Visual Studio closes with a one-line notice; the Outbound log is kept in memory only. Conversations, pins and the Outbound log are stored through it (see [Persistence](#persistence)). |
| `indexing` | Pillion Host does not discover, hash or index repository files and does not create or open index databases (`index\<id>\index.db`, see `src/Pillion.Indexing/README.md`): opening an index fails with `PolicyDenied` and a message that names the flag, nothing is written to disk and the retention sweep does nothing. An index needs `localStore` as well, because it is a Local Store database. With the flag on, the host indexes the roots of the solution or folder the extension reports (`IContextService.SetWorkspaceAsync`, work item 2.9; see the host README, Indexing pipeline); with it off the index status is Idle with no roots and the index controls are refused with `PolicyDenied`. The same flag covers the file watcher, the git `HEAD` watch and the incremental updates of work item 2.10 (saves relayed from Visual Studio, open documents first): off, nothing is watched and the `documentSaved` and `openDocumentsChanged` notifications have no effect. |
| `embeddings` | The embedding gateway (`IModelGateway.EmbedAsync`, work item 2.6) fails with `PolicyDenied` and a message that names the flag; it also needs `modelGateway`. Chat, autocomplete and model listing are unaffected. Embeddings never leave the machine unless `cloudSwitches.embedding` is turned on as well (it is off by default; Local-only always wins). |
| `bundledEmbeddings` | The bundled embedding provider (`bundled`, work item 2.7) is not built or routed to: the ONNX model shipped with the extension (`bge-small-en-v1.5`) and the models downloaded on request stop being offered, and the Embedding task class uses Ollama or another configured local embedding model instead (or fails with `ModelNotInstalled` when there is none). Nothing else changes. |
| `workspaceQueries` | Visual Studio refuses Pillion Host's `FindReferencesAsync` and `CallersOfAsync` calls with `PolicyDenied` and a message that names the flag, and Pillion Host stops asking: `find_references` and `callers_of` (for `@symbol` and chat) are answered from the index's approximate reference edges only, marked approximate (work item 2.14). Nothing else changes. |
| `indexWindow` | **View > Other Windows > Pillion Index** and **Tools > Pillion: Open Index** are hidden, the status bar shows no indexing indicator, and the **Indexing** Options page (`indexing.exclude`, `maxMemoryMb`, `pauseOnBattery`, `retentionDays`) says it is disabled by settings or policy. Pillion Host indexes either way (that is the `indexing` flag); the setting values stay in `settings.json`. The extension reads the flag when Pillion Host becomes ready and when the Tools menu opens (an absent flag means on). |
| `retrieval` | `IContextService.SearchAsync` and `SearchDetailedAsync` (hybrid retrieval, work item 2.11: lexical, vector and symbol candidates fused with Reciprocal Rank Fusion, see `src/Pillion.ContextEngine/README.md`) fail with `PolicyDenied` and a message that names the flag. Searching also needs `indexing` and `localStore` to open an index, and the vector candidates need `embeddings`; without them the search answers from what it can open (no index: no hits; no embeddings: lexical and symbol hits only, marked `Degraded`). |
| `reranker` | **Off by default.** The cross-encoder reranker of hybrid retrieval waits on the licence review in design section 18, so no reranker ships yet: this version keeps the fused order whether the flag is on or off. A policy can keep it off; nothing turns it on but the user. |

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
{ "autocomplete": { "disabledLanguages": [ "python" ], "announceSuggestions": true, "ordering": "pillionFirst" } }
```

A new suggestion is announced to screen readers with a UI Automation notification; `announceSuggestions: false`
switches that off.

**Visual Studio 2022** draws ghost text through IntelliCode, so it needs IntelliCode (part of every .NET workload) with
**Tools > Options > IntelliCode > General > Automatically generate code completions in the Editor** on. When either is
missing the status bar and the Pillion Output pane say "Inline suggestions need IntelliCode's automatic code completions
on Visual Studio 2022"; the check runs when the extension loads and again whenever the Tools menu opens. Visual Studio
2026 needs nothing extra.

### When Copilot or IntelliCode also suggest

GitHub Copilot (built into Visual Studio 2026) and IntelliCode's whole-line completions draw their proposals in the same
ghost-text slot as Pillion. **Tools > Options > Pillion Programmer > Autocomplete** sets who gets it
(`autocomplete.ordering` in `settings.json`):

| Ordering | What happens |
| --- | --- |
| **Pillion first** (`pillionFirst`, the default) | Pillion asks for a suggestion on every keystroke. When another provider's proposal takes the slot and Pillion has a suggestion for the same caret, Pillion's replaces it (rules below). When Pillion has nothing, the other proposal stays. |
| **Other provider first** (`competitorFirst`) | While Copilot or IntelliCode is present, Pillion's proposal manager defers to theirs and Pillion's source answers the editor with nothing; a Pillion suggestion fills in only when nothing else is displayed a second after the request. |
| **Pillion on demand only** (`onDemand`) | No automatic Pillion suggestions. **Pillion: Suggest** (Ctrl+Alt+Shift+P, then S; Alt+\ is Go To Member in Visual Studio 2026) asks for one at the caret and shows it in place of whatever is displayed. The command works in the other modes too. |

The first time a document opens with another provider active, an info bar asks once per machine: "Copilot (or
IntelliCode) inline suggestions are also on. Choose: Pillion first, Copilot first, Pillion on demand only, or keep both as
they are", with a link to that product's own settings (Pillion never changes them) and "Don't show again". IntelliCode on
Visual Studio 2022 counts as active at start-up (its whole-line completions are local and on whenever Pillion's ghost text
can show); Copilot, and IntelliCode on Visual Studio 2026, count once one of their proposals has been displayed, because
Copilot's sign-in state has no public API. Answering writes `autocomplete.ordering` and
`autocomplete.coexistencePromptDone`; closing the bar shows it again at the next start. The Autocomplete page's "Ask again"
check box clears the flag.

The rules under Pillion first (work item 1.4; measurements in `spikes/ProposalsSpike/FINDINGS.md`, "Coexistence
countermeasure"):

- Visual Studio's broker waits about 0.8 s for a source ordered ahead of another before it shows the later one, and
  keeps the other sources' proposals queued. When Pillion's proposal stops matching what is typed, a competitor's queued
  one takes over 8 to 40 ms later, and while it still matches no source is asked again.
- When a competitor's proposal is displayed (taking over, or because Pillion answered after the broker's wait), Pillion
  asks for its own at that caret outside the broker: the type-through cache first, then the pipeline (with the normal
  debounce, again on every keystroke while the competitor's proposal is typed through).
- Only when Pillion has a suggestion for exactly that caret and text does it take the slot, 150 ms after deciding and never
  inside the suggestion service's own event handling (that stopped IntelliCode's broker on 17.14): it keeps the suggestion,
  dismisses the displayed proposal and makes the editor ask every source again (an explicit invocation), which Pillion's
  source answers at once. The dismissal uses the user-Escape reason because it is the only one that clears the broker's
  queue, so the other product records that proposal as dismissed by Escape.
- At most one takeover per caret position; none while a completion list is open, while the user has cycled to the other
  proposal with Alt+. (until the text changes), or when Pillion has nothing (the competitor is never left without a
  proposal because of Pillion).
- Pillion never takes Tab: Tab accepts whichever proposal is displayed. A Pillion suggestion replaced by another
  provider's is counted as `displaced` in the autocomplete metrics (Show Status and the `metrics-*.jsonl` lines).

## Inline chat

**Tools > Pillion: Inline Chat** (default shortcut Ctrl+Alt+Shift+P, then I; also on the code editor's context menu)
opens a prompt above the code it is about: the selection extended to whole lines; with nothing selected, the member the
caret is in (method, constructor, property, event or field, with its doc comment; Roslyn, for C# and Visual Basic);
otherwise the caret's line. The scope is highlighted while the prompt is open. The editor makes room for the prompt
(a line transform adds space above the scope's first line), so nothing is drawn over the code and the document is not
touched.

Type an instruction ("extract the validation into a method") and press Enter, or type `/` for the shortcuts: `/doc`
(documentation comments only), `/test` (new test code, offered as **Insert at cursor** or **Copy** instead of a
replacement, with where it belongs), `/fix` (the errors and warnings on the scope's lines from the Error List and Roslyn
are sent with the request) and `/explain` (an explanation, nothing to apply). The model picker defaults to Auto (the
host routes by the InlineEdit task class) and remembers your choice; **Recent** and Up and Down in the prompt recall the
last ten prompts of the Visual Studio session. The status line says "Thinking..." with the model once it is known, the
elapsed time, **Stop** and, after a failure, the error and **Retry**.

The proposal streams into an inline difference view (the theme's difference colours plus + and - line markers) while the
document stays as it was, so Roslyn, the file watcher and Live Unit Testing never see partial code. **Enter** (with an
empty prompt) applies it as one edit after the file is checked out through source control, undone with one Undo
("Pillion inline edit"); **Esc** discards it and leaves nothing to undo; typing another instruction refines the proposal
in place. A warning from the host (for example "no change proposed") is shown above the difference view. The prompt
closes when you apply or discard, when the document closes, or when the caret moves further from the scope than the
editor shows lines. Every control is in the tab order with a screen-reader name; while the prompt has focus, keys the
editor would otherwise take (typing, Backspace, arrows, Ctrl+A/C/V/X/Z, Enter, Esc, Tab) go to the prompt.

## Context Engine

The Context Engine answers one question for every feature: which few thousand tokens of this repository should the model see?
It indexes your repository on your machine, searches it, and feeds chat, `@`-mentions, ghost-text autocomplete and inline edits.
It runs in Pillion Host behind the `indexing`, `retrieval` and `localStore` flags (design document section 7).

**What is indexed.** The files of the repository roots of the open solution or folder: the git top-level folder of each project
(a linked worktree or submodule is followed to its real git directory), or the opened folder when there is no git repository. A
solution that spans two repositories has two roots; a nested repository is indexed under its own root. Each file is split into
*chunks*, one syntactic unit each (a member with its doc comment, or a type header) with a breadcrumb such as
`path > namespace > type > member`: C# and Visual Basic through Roslyn (no compilation needed), Razor split into its C# and markup
parts, TypeScript, TSX, JavaScript, Python, C, C++, Go, Rust, Java, HTML and CSS through tree-sitter, SQL through a statement
lexer, and anything else in 60-line windows. Beside the chunk text the index keeps a lexical index (BM25, with camelCase and
snake_case identifiers split so `getOrderTotal` matches "order total"), an embedding of every chunk, and a table of definitions
and name-based references (marked approximate) from which the repo map and the fallback for `find_references` and `callers_of`
are built. Precise answers for C# and VB come from the live Roslyn workspace in Visual Studio. Search fuses the lexical,
vector and symbol results (Reciprocal Rank Fusion) and expands a hit to its whole member.

**Embeddings stay on the machine.** The default model is `bge-small-en-v1.5`, bundled in the extension and run in Pillion Host with
ONNX Runtime on the CPU (or DirectML when a GPU is present), so indexing works offline with nothing installed. Two more models
download on request (`jina-embeddings-v2-base-code`, `multilingual-e5-small`), and Ollama or OpenAI-compatible embedding models
can be routed to. Nothing is sent to a cloud provider for embedding unless you turn `cloudSwitches.embedding` on (off by default;
Local-only always wins). The retrieval baseline is in [docs/eval-retrieval-baseline.md](docs/eval-retrieval-baseline.md).

**Where the index lives.** `%LOCALAPPDATA%\PillionProgrammer\index\<index-id>\`, one folder per repository root, embedding model
and chunker version: `index.db` (encrypted with SQLite3 Multiple Ciphers; the database key is DPAPI-protected for your Windows
user in `key.bin`, user-only ACL), `writer.lock` and `state.json` (paths and versions, never code). Several Visual Studio
instances share one index: the first takes the lock and indexes and watches, the others search it, and one takes over when the
owner goes away. Changing the embedding model builds a new index beside the old one. Nothing is written inside your repository
except a `.pillionignore` you create.

**Staying current.** The first run indexes the repository in the background and partial results are searchable after the first
batch. After that a file watcher (500 ms debounce), the save event Visual Studio relays and a watch on git's `HEAD` keep it up to
date: a saved file is searchable within about 0.5 s, and a branch switch is a hash sweep of what changed, not a rebuild. Indexing
runs at below-normal priority on at most half the cores, pauses by itself during builds and debugging and while Windows battery
saver is on (`indexing.pauseOnBattery`), and stays under `indexing.maxMemoryMb` (1,536 MB by default) by halving its embedding
batch and then pausing.

**What is left out, and `.pillionignore`.** Pillion skips `bin`, `obj`, `node_modules`, `.git` and `.vs`, what `.gitignore` ignores,
generated and minified files, lock files, binary files, files over 1 MB, links, and a root `packages` folder that looks like a
NuGet packages folder. To leave out more, put a `.pillionignore` at the repository root in `.gitignore` syntax:

```
# Pillion does not index paths listed here.
testdata/
*.snap
/third_party/
```

A `!` rule re-includes: it takes back in what `.gitignore` ignored, and any built-in name except `.git`:

```
# index our own packages\ folder and a checked-in tools\bin, and what .gitignore hides
!packages/
!tools/bin/
!src/generated/
```

`indexing.exclude` (Options > Pillion Programmer > Indexing, or a repository policy's `exclude`) adds patterns that a
`.pillionignore` rule cannot undo. Full syntax, the order the rules apply in, and examples are in
[docs/pillionignore.md](docs/pillionignore.md).

**Secret-bearing files** (`.env`, `.env.*`, `*.pfx`, `*.pem`, `*.key`, `*.p12`, `secrets.json`, `appsettings.*.json`,
`*.publishsettings`, SSH private keys, `*.ppk`, `*.jks`, `*.keystore`, `.netrc`, `.pgpass`) are never opened, hashed or stored,
whatever `.gitignore` or `.pillionignore` says. The Index window lists them first with a shield. `@file` on one asks for your
approval, and an approved one is never sent to a cloud model. A secret written inside an ordinary source file is not detected by
name: it sits in the encrypted index and is redacted by the scanner when text goes to a cloud model.

**The Index window** (**View > Other Windows > Pillion Index**, **Tools > Pillion: Open Index**, or a click on the status bar
indicator) shows the status and progress, one row per repository root with **Rebuild** and **Delete**, the excluded files with
the reason for each, every index folder on the machine, and **Pause** and **Resume**. The status bar shows
`↻ Indexing 42%`, `‖ Index paused: build` or `⚠ Index error` while there is something to say. See
[docs/index-window.md](docs/index-window.md). Behind the `indexWindow` flag.

**The Indexing options** (Tools > Options > Pillion Programmer > Indexing, or the `indexing` section of `settings.json`):

| Setting | Default | Meaning |
| --- | --- | --- |
| `indexing.exclude` | none | Patterns in `.gitignore` syntax that are never indexed |
| `indexing.maxMemoryMb` | 1,536 | Indexing pauses at this working set (256 to 65,536) |
| `indexing.pauseOnBattery` | on | Pause while Windows battery saver is on |
| `indexing.retentionDays` | 30 | An index unused for this many days, or whose folder no longer exists, may be deleted; 0 turns the age rule off |

**Retention.** The rule above is implemented, but the host does not run the sweep on a schedule yet: stale indexes are listed in
the Index window (**Indexes** tab, "Root is gone") and deleted there on request, and **Delete** frees the disk at once. Disk use is
about 1.2 to 1.3 KB a chunk for generated code and about 2.2 KB a chunk for real code
([docs/gate-2-report.md](docs/gate-2-report.md)).

## Mentions

Type `@` in the Chat window's input to pin context to the conversation (behind the `mentions` flag). The picker groups its
entries under headings (kinds, files, folders, symbols, docs, and the options of `@git` and `@output`), each with its kind's
icon and a hint line, and is driven from the keyboard: Up and Down move, Enter or Tab pick, Esc closes. Every pin shows its
token count; `@kind:value` names the kind explicitly. Twelve kinds resolve (reference: [docs/mentions.md](docs/mentions.md)):

| Mention | What is pinned | Where it comes from |
| --- | --- | --- |
| `@file` | A file's text (`@Customer.cs`, `@file:src/Program.cs`) | The open index's file list, fuzzy matched; the solution's file list when there is no index |
| `@folder` | A folder outline: each file's first-chunk summary and its top definitions | The index; the folder on disk otherwise |
| `@symbol` | A definition's code and a summary of its references (`@symbol:OrderService`) | The index's symbols; references from Roslyn for C# and VB, the index otherwise |
| `@docs` | A Markdown or text document, chunk by chunk up to the limit; bare `@docs` lists them | The index, plus the folders in `mentions.docsFolders` |
| `@solution` | The repo map, focused on the open documents | The index |
| `@git` | The working-tree diff (`@git`), the staged changes (`@git:staged`) or the last commits (`@git:log:5`), with secrets redacted | The repository of the open folder (LibGit2Sharp) |
| `@output` | An Output pane's tail (`@output:build`, `debug` or `tests`) | Visual Studio |
| `@build` | The last build's outcome and output | Visual Studio |
| `@tests` | The Tests pane's text: failing tests with their stack traces | Visual Studio |
| `@selection` | The editor selection | Visual Studio |
| `@clipboard` | The clipboard text | Visual Studio |
| `@errors` | The Error List, as your filters show it, errors first | Visual Studio |

`@terminal` is part of the design and arrives with the command runner in phase 3. A bare `@name` tries a file, then a symbol,
then a folder. A file the exclusion globs or the secret-file rules refuse needs your approval before it is pinned, and an
approved one never goes to a cloud model; `@git` lists such files without their changes, and `@symbol` shows no reference snippet
from them. To let `@docs` search documentation outside the repository, list the folders in `settings.json` (full paths, or
relative to the first open folder; empty by default):

```json
{ "mentions": { "docsFolders": ["C:\\docs\\handbook", "docs/adr"] } }
```

Replies that used retrieved code **cite** it: a `path:line` the reply names becomes a link that opens the file and selects the
span, and when it names none the chunks sent are listed under a collapsed **Sources** line. **Inspect** shows the retrieved
chunks and the repo map before sending, each with a remove button.

## Persistence

Pillion Host saves what you would be sorry to lose, on your machine, encrypted (behind the `localStore` flag):

- **Conversations** with their messages and **pins** (what to resolve again, never the content) live in
  `%LOCALAPPDATA%\PillionProgrammer\conversations.db`, an encrypted SQLite database (SQLite3 Multiple Ciphers, ChaCha20-Poly1305).
  Its key is DPAPI-protected for your Windows user in `conversations.key.bin` beside it with a user-only ACL. The Chat window's
  conversation list reads them, so they survive a Pillion Host restart and a Visual Studio restart, and two Visual Studio
  instances share the file. A reply is saved when it ends, with the model, provider, tokens, cost and citations. A reply you
  stopped is saved with what had arrived and marked stopped; a reply that failed is not saved (your message is, so Retry does
  not repeat it). Inline edits are not saved. Before an upgrade changes the file's schema, a byte copy of the still encrypted
  file is kept beside it as `conversations.db.v<n>.bak`.
- **The Outbound log**, the record of every request sent to any model from any task class (time, task, provider and model, local
  or cloud, tokens, redactions, duration, outcome; never prompt text), is kept in the same database, so the Chat window's view
  survives a restart. Autocomplete answers served from the host's cache never reached a model and are kept for the session only.
- **Delete all.** **Tools > Options > Pillion Programmer > Privacy > Delete all conversations** (after a confirmation) removes
  every conversation, message, pin and Outbound record, compacts the file so the freed pages go too, and deletes the migration
  backups. Settings and the code index are not touched. A single conversation is deleted from the Chat window's list.

Conversations and the Outbound log are kept until you delete them; the code index has its own retention (above).

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
(`keep_alive` -1). Chat, inline edits and autocomplete share one loaded copy of a model on an 8 GB card, at 4,096 tokens
of context for a 7B model (Ollama cannot hold a second copy at another context size there, and a request for one waits
for ever); a request that needs more makes Pillion unload the model and load it once at the larger size. To keep a model
at another size, set it per model (model ids contain dots, so edit the object in `settings.json`):

```json
{ "models": { "residentContext": { "qwen2.5-coder:7b": 8192 } } }
```

See [src/Pillion.ModelGateway/README.md](src/Pillion.ModelGateway/README.md#residency-and-ollama_max_loaded_models) for what
stays resident per tier and the `OLLAMA_MAX_LOADED_MODELS` setting that matches each.

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md).

## Licence

[MIT](LICENSE). Copyright (c) 2026 Ryan Kendrick.
