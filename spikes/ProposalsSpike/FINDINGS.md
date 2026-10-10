# Gate 0 spike: Proposals API ghost text: findings

Spike date: 2026-10-08. Verified on **Visual Studio Community 2026 18.10.12224.181** (Experimental Instance, `/rootsuffix Exp`).
Rerun on **Visual Studio Community 2022 17.14.41** the same day: see "Results on VS 2022 17.14.41" (core editor only:
third-party proposals are never requested) and then "Results on VS 2022 17.14.41 with the managed desktop workload and
IntelliCode" at the end, which holds the **current verdict for both versions**: viable on 18.10, viable on 17.14 only
with IntelliCode present and its automatic code completions turned on.

## Verdict

**Proposals API viable on 18.10: yes, with two caveats.**

* All four core ghost-text behaviours work through a plain MEF `ProposalSourceProviderBase` export, with no
  internal types and no reflection. That covers multi-line ghost text, insertion with text to the right of the
  caret, proposals starting in virtual space or after indentation, and Tab to accept / Esc to dismiss. VS draws
  the ghost text, the hint bar, the accept and dismiss keys, undo, cycling and reconciliation as you type.
* It coexists with the built-in GitHub Copilot, which was signed in and active during the spike. Proposals come
  from one ordered pipeline, and our `[Order(Before = "CompletionsProposalSourceProvider")]` made our proposal the
  one shown whenever we returned one. When we returned null, Copilot's completions and Next Edit Suggestions
  appeared as usual.
* Caveat 1, IntelliSense-coupled proposals are not proven. These are proposals that continue the selected
  completion item while the completion list is open. The source is only called on some keystrokes during a
  completion session. The one proposal we returned there was not displayed (`DismissedDueToInvalidProposal`).
  Ghost text with the list closed is unaffected. This needs a follow-up (see Open items) but does not block the
  core feature.
* Caveat 2, the API is still moving. Between 17.14 and 18.10 the assembly version went 17.0 to 18.0, the
  `CompletionState` constructors became `[Obsolete("Will be removed in next major release")]`, two `ProposalFlags`
  values became `[Obsolete("This API is not implemented...")]`, four flags were added, and a new type is marked
  `[Obsolete("Class has not been finalized and may change without warning.")]`. Build against the 17.14 SDK
  (the oldest target) and keep the Proposals code behind a thin adapter.

## What was built

```
spikes/ProposalsSpike/
  ProposalsSpike.sln
  src/ProposalsSpike/ProposalsSpike.csproj            SDK-style VSIX, net48, Microsoft.VisualStudio.SDK 17.14.40265, Microsoft.VSSDK.BuildTools 17.14.2142
  src/ProposalsSpike/source.extension.vsixmanifest    InstallationTarget [17.14,) Community/Pro/Enterprise, amd64
  src/ProposalsSpike/SpikeProposalSourceProvider.cs   MEF export + SuggestionServiceBase event logging
  src/ProposalsSpike/SpikeProposalSource.cs           RequestProposalsAsync, logs every call
  src/ProposalsSpike/SpikeScenarios.cs                canned proposals keyed on trigger text
  src/ProposalsSpike/SpikeProposalManagerProvider.cs  optional ProposalManager export ("generate here" on marker lines)
  src/ProposalsSpike/SpikeLog.cs                      %TEMP%\ProposalsSpike.log
  api/                                                reflected API surface (17.14.249) and diff against 18.10
  tools/ApiDump/                                      MetadataLoadContext reflection tool used to produce api/
  tools/vsdrive.ps1, tools/Run-Scenarios.ps1          drive the Exp instance (DTE + SendKeys), screenshots, log per scenario
```

* **SDK-style VSIX works with the 17.14 build tools.** The build tools ship no MSBuild SDK, so the project imports
  `Sdk.props`/`Sdk.targets` from `Microsoft.NET.Sdk` explicitly and then
  `$(VSToolsPath)\VSSDK\Microsoft.VsSDK.targets`. `VSToolsPath` comes from the build tools package props. It needs
  explicit `Reference`s to `System.ComponentModel.Composition` and the WPF assemblies.
  `Microsoft.VisualStudio.SDK` is referenced with `ExcludeAssets="runtime"`, so the VSIX holds only
  `ProposalsSpike.dll`/`.pdb`, the manifest and the licence.
* Build: `"C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe" ProposalsSpike.sln /restore /p:Configuration=Debug`.
  `DeployExtension=true` is the default and deploys to the Exp hive of the VS that owns the MSBuild
  (`%LOCALAPPDATA%\Microsoft\VisualStudio\18.0_<id>Exp\Extensions\...`). `dotnet build` cannot produce a VSIX.
* The 17.14-built DLL loaded in 18.10 without changes. VS redirects `Microsoft.VisualStudio.Language` 17.0.0.0 to 18.0.0.0.

### Triggers

| Type this (C# editor) | Scenario | Proposal |
|---|---|---|
| `//ml` at end of line | multi-line | ` multi-line ghost text` + 3 indented code lines |
| `//noflags` at end of line | single line control (used for the Esc test) | ` single line, ProposalFlags.None` |
| `/*right*/` inside `Console.WriteLine();` | text right of caret | `"spike: inserted before existing text"` |
| `/*rightml*/` inside `Console.WriteLine();` | multi-line with text right of caret | `"first line" +` / `"second line"` |
| `/*replace*/` inside `Console.WriteLine();` | probe: insertion plus a replacement edit further right | `"spike"` and `;` becomes `; // replaced by proposal` |
| line `//vs`, then Enter | virtual space, text padded with the virtual spaces | `int fromVirtualSpace = 1; ...` |
| line `//vs0`, then Enter | virtual space, unpadded text | `int fromVirtualSpaceNoPad = 0;` |
| line `//ind`, next line whitespace only, caret at end | after real indentation | `int afterIndent = 2;` |
| `//two` at end of line | two proposals (cycling) | ` first of two proposals` / ` second of two proposals` |
| line `//isense`, next line `Console.WriteL` (completion list open, WriteLine selected) | IntelliSense-coupled | rest of item + `("spike: proposal after IntelliSense item");` |

The log records every `RequestProposalsAsync` call: scenario, trigger char, caret line and column, position,
virtual space, snapshot version, completion state, and the text before and after the caret. It also records
every `TryGetIsProposalPosition` call and every `SuggestionServiceBase` event (`ProposalDisplayed`,
`ProposalRejected`, `SuggestionAccepted`, `SuggestionDismissed`) with the proposal id. Ids starting `spike:`
are ours.

## API surface actually available (17.14 SDK)

Source: reflection over `Microsoft.VisualStudio.Language.dll` from NuGet `Microsoft.VisualStudio.Language`
17.14.249, which is what `Microsoft.VisualStudio.SDK` 17.14.40265 pulls in. Full dumps: `api/Proposals-17.14.249.txt`
and `api/Suggestions-17.14.249.txt`. Differences from 18.10: `api/diff-17.14-vs-18.10.txt`.

Namespace `Microsoft.VisualStudio.Language.Proposals` (19 types, all public, none `[Obsolete]` in 17.14):

* `ProposalSourceProviderBase`: `abstract Task<ProposalSourceBase> GetProposalSourceAsync(ITextView, CancellationToken)`.
  This is the MEF extension point.
* `ProposalSourceBase : IAsyncDisposable`: `abstract Task<ProposalCollectionBase> RequestProposalsAsync(VirtualSnapshotPoint caret, CompletionState completionState, ProposalScenario scenario, char triggeringCharacter, CancellationToken)`, plus `virtual Task DisposeAsync()`.
* `ProposalCollectionBase` (`SourceName`, `Proposals`). Convenience class `ProposalCollection(string sourceName, IReadOnlyList<ProposalBase>)`. `LazyProposalCollectionBase` adds `GenerateLazyProposalsAsync`.
* `ProposalBase`: `Edits`, `Caret`, `CompletionState`, `Flags` (abstract), and virtual `Description`, `AcceptText`,
  `PreviewText`, `NextText`, `UndoDescription`, `CommitAction` (`Func<bool>`, which replaces the default commit
  when non-null), `Scope`, `ProposalId`.
* `Proposal : ProposalBase`: 3 constructors. Static `TryCreateProposal(...)` returns null when
  `ValidateEdits(edits, caret, completionState)` fails.
* `ProposedEdit` (struct): `(SnapshotSpan span, string replacementText[, IReadOnlyList<Field> fields[, int? finalCaretPosition, int finalCaretVirtualOffset]])`. `Field` is a struct `(Span, Id)`.
* `ProposalFlags` [Flags]: `SingleTabToAccept`, `QueueProposalRequestOnCommit`, `ShowCommitHighlight`, `SimulateBraceCompletion`,
  `MoveCaretToEnd`, `DisableInIntelliSense`, `FormatAfterCommit`, `TriggerIntelliSenseAfterCommit`, `PreserveOnBufferChange`.
* `ProposalScenario`: `Completion`, `TypeChar`, `Return`, `CaretMove`, `DivergedProposal`, `CompletedProposal`,
  `ExplicitInvocation`, `Backspace`, `CompletionAccepted`.
* `ProposalManagerProviderBase` / `ProposalManagerBase`: `bool TryGetIsProposalPosition(VirtualSnapshotPoint, ProposalScenario, char, ref bool value)`.
  The parameter is **`ref`, not `out`**. Returning true means "decided, use `value`". Returning false asks the next manager.
* `ProposalBrokerProviderBase` / `ProposalBrokerBase`: implemented by VS (`ShouldGenerateProposal`, `RequestProposals` returning `AsyncQueue<ProposalCollectionBase>`).
* `CompletionState`: `SelectedItem`, `ApplicableToSpan`, `IsSoftSelection`, `IsBuilderSelected`, `Icon`,
  `UnderlyingCompletionItem`, `IsSnippet`/`IsSuggestion`/`IsPreprocessorDirective`, `TranslateTo`.
* Name constants: `ProposalSourceProviders.IntelliCodeCSharpProposalSource` / `IntelliCodeCPlusPlusProposalSource`,
  and `ProposalManagerProviders.IntelliCodeCSharpProposalManager` / `IntelliCodeCPlusPlusProposalManager`.
  `ICompletionDefaultsSource`.

Namespace `Microsoft.VisualStudio.Language.Suggestions` (the display layer):

* `SuggestionServiceBase` is MEF-importable and was `[Import]`ed successfully. Events: `ProposalDisplayed`,
  `ProposalRejected`, `SuggestionAccepted`, `SuggestionDismissed`. Methods: `GetDisplayedProposal(view)`,
  `DismissProposalAsync`, `TryUpdateProposal`, `TryRegisterProviderAsync`. These events are the only way to learn
  that a proposal was shown, accepted or rejected. `ProposalSourceBase` itself gets no callback.
* `SuggestionManagerBase`, `SuggestionSessionBase`, `SuggestionBase`, `SuggestionProviderBase`, and enums
  `ReasonForAccept`, `ReasonForDismiss`, `ReasonForUpdate`, `EditDisplayStyle`, `TipStyle`.
  `TipStyle`'s own XML doc says "Many of these will disappear once we have locked down the style".

How VS consumes the export (from reflecting `Microsoft.VisualStudio.Editor.Implementation.dll` 18.10):

* `ProposalBrokerProvider` `[ImportMany]`s `Lazy<ProposalSourceProviderBase, IProposalMetadata>` and the same for
  managers. `IProposalMetadata` is internal and is `IOrderable` (`[Name]`, `[Order]`) + `IContentTypeMetadata`
  (`[ContentType]`) + optional `OptionName` (`[DeferCreation]`). **`[TextViewRole]` is not part of the metadata
  and is ignored.** The spike keeps the attribute for documentation and checks `view.Roles` in code.
* The editor host is `InlineCompletionsListener` (`[ContentType("any")]`, roles `DOCUMENT` and `ChatTextViewRole`).
  It reports all inline proposals under the suggestion provider name **`IntelliCodeLineCompletions`**, whoever
  produced them.
* Built-in sources seen: Copilot `CompletionsProposalSourceProvider` (`[ContentType("any")]`,
  `[Order(Before="InlineCSharpProposalSourceProvider")]`, `[Order(Before="Highest Priority")]`,
  `[DeferCreation(OptionName="CreateCopilotCompletionAssetsOption")]`), Copilot `CompletionsProposalManagerProvider`
  with the same ordering, and IntelliCode `InlineCSharpProposalSourceProvider` / `InlineCSharpProposalManagerProvider`
  (`[ContentType("CSharp")]`).

Changes in 18.10 compared with 17.14 (from `api/diff-17.14-vs-18.10.txt`):

* Assembly version 18.0.0.0 (17.14 is 17.0.0.0). A 17.14-compiled extension still binds.
* `CompletionState`: all 3 existing constructors `[Obsolete("Will be removed in next major release")]`. A new
  constructor adds `int sessionId`, and there is a new `SessionId` field. Do not construct `CompletionState`
  ourselves. Pass through the one we receive.
* `ProposalFlags.MoveCaretToEnd` and `PreserveOnBufferChange` are `[Obsolete("This API is not implemented and should not be used.")]`.
  New flags: `SingleEditAccept=512`, `UpdateOnAccept=1024`, `DismissOnAnyEdit=2048`, `HideRemoteEdit=4096`.
  Copilot's NES proposals use `DismissOnAnyEdit`.
* New `Proposal.TryCreateProposal(ProposalBase oldProposal, IReadOnlyList<ProposedEdit> newEdits)`.
* New `ProposalAdjusterProviderBase` `[Obsolete("Class has not been finalized and may change without warning.")]`.
* Suggestions: new reasons (`AcceptedBySelection`, `DismissedAfterLostFocus`, `DismissedBySnippet`,
  `*SingleEdit*`, `UpdatedAfterAccept`, `UpdatedAfterBufferChange`, `DivergedAfterBufferChange`). Several
  `TipStyle`/`EditDisplayStyle` values are obsolete. New `SuggestionServiceBase.DismissAndBlockProposalsAsync`.
  New namespace `Microsoft.VisualStudio.Language.InlinePrompts`.

## Results on VS 2026 18.10

Copilot status: **signed in and active** in the Experimental Instance. The log shows Copilot completions
(`id=cpt...`, `type=InlineCompletionItemAndProposal`) and Next Edit Suggestions (`id=nes...`, `type=NesProposal`).
Screenshots are kept in the session scratchpad, not in the repo. The whole matrix was run twice, by hand and then
with `tools/Run-Scenarios.ps1`, with identical results.

| # | Scenario | Displayed | Tab | Esc | Evidence |
|---|---|---|---|---|---|
| 1 | Multi-line (`//ml`) | yes | accepted, 4 lines inserted, caret at end of last line | - | log `ProposalDisplayed ... spike:ml` then `SuggestionAccepted reason=AcceptedByCommand`; doc text verified via DTE |
| 2 | Text right of caret (`/*right*/`) | yes, inline before `);` | accepted, `);` kept after insertion, caret after inserted text | - | doc: `Console.WriteLine(/*right*/"spike: inserted before existing text");` |
| 2b | Multi-line with text right of caret | yes, existing `);` pushed to the end of the last ghost line | accepted | - | doc: `..."first line" +` / `"second line");` |
| 2c | Replacement edit away from caret | yes, but as a **diff/side-by-side preview**, not grey text | accepted | - | screenshot shows the red original, an arrow, then the green replacement |
| 3 | Virtual space, padded (`//vs`) | yes, drawn from the virtual caret column | accepted, line has exactly 12 leading spaces | - | Return scenario, `virtual=True/12`. VS stripped our 12-space prefix (final edit `[214..214)->"int fromVirtualSpace..."`) and materialised the virtual indentation |
| 3b | Virtual space, unpadded (`//vs0`) | yes, same rendering | accepted, 12 leading spaces | - | identical result: no padding needed |
| 3c | After real indentation (`//ind`) | yes (fired on `CaretMove` to end of a whitespace-only line) | accepted | - | typing a space before Tab kept the proposal and shifted the insertion point (`[227..227)` became `[228..228)`) |
| 4 | Esc | - | - | dismissed, buffer unchanged | `SuggestionDismissed reason=DismissedAfterUserEscape` |
| 4b | Two proposals + Alt+. | first shown, Alt+. shows second | - | dismissed | `ProposalRejected reason=DivergedDueToChangeProposal` (first) then `ProposalDisplayed` (second) |
| 5a | Coexistence with Copilot | ours shown when we return one; Copilot/NES shown when we return null | - | - | e.g. NES `"Hello, World!"` shown at `Console.WriteLine(` when we returned null. Copilot's `ht*/` completion accepted `AcceptedByOvertype` as the user kept typing |
| 5b | IntelliSense-coupled (`//isense` + `Console.WriteL`) | **no** | - | - | see below |

What the screenshots show:

* **Rendering.** Ghost text is drawn **italic and syntax-coloured** by the editor's classifier, the same look as
  Copilot, not flat grey. Multi-line proposals are drawn as extra visual lines under the caret line. The
  following real lines are pushed down and the line-number gutter skips the ghost lines.
* **Hint bar.** It appears above the line as `Pillion spike: ml  [Tab] to accept (spike)  [Esc] Dismiss`, so
  `Description` and `AcceptText` are honoured. It showed on some scenarios but not all at screenshot time,
  probably on a delay or hover.

IntelliSense details (5b):

* While a completion list is open the source receives a `CompletionState`, for example `selected="WriteLine"
  applicableTo=[238..244) soft=False`, with `scenario=Completion` or `TypeChar`.
* Requests stop after a few keystrokes into the member name. In three runs, the manager was still consulted
  (`TryGetIsProposalPosition scenario=Completion ... value=true`) but `RequestProposalsAsync` was not called for
  the later characters. Each keystroke logged `SuggestionDismissed reason=DismissedDueToInvalidProposal proposal=<null>`.
* In the one run where a request arrived with `WriteLine` selected, we returned a continuation-only insertion at
  `ApplicableToSpan.End` with `SingleTabToAccept`. It was not displayed. The second variant
  (item remainder + continuation) was never requested. Copilot showed nothing in this state either.

Other observations:

* **The source is called a lot.** It runs on every `TypeChar`, `CaretMove`, `Return` and `Backspace`, on
  thread-pool threads. One `ProposalScenario` value outside the enum, `16`, was seen once on file open.
  `TryGetIsProposalPosition` runs on the **UI thread**, so it must be trivial.
* **Managers are a chain.** With our manager disabled (`%TEMP%\ProposalsSpike.nomanager`) the `//ml` proposal
  still showed, because a later manager (Copilot's or IntelliCode's) allowed it. If we defer, someone else's
  manager decides whether sources are queried. Shipping our own manager ordered first gives deterministic control.
* **Ordering decides who wins.** With `[Order(Before = "CompletionsProposalSourceProvider")]`, ours displayed and
  Copilot's did not appear at the same time. We did not test what happens without that ordering, or whether
  the broker ever merges sources.
* **Pure insertions survive typing.** VS reconciles a pure insertion with typing: overtyping matching characters
  accepts it (`AcceptedByOvertype`), and a non-matching space was absorbed. Edits with a non-zero-length span are
  treated as diverged on any nearby edit, per the docs.
* **`Proposal.ValidateEdits` is looser than its docs.** It returned `true` for a replacement span that starts at
  the caret, although the docs suggest only zero-length edits may touch the caret. VS also normalises edits
  before display and accept: a replaced `;` with `; // ...` became an insertion of ` // ...` after the `;`.
* **The caret ends after the inserted text** even with `ProposalFlags.None`. `MoveCaretToEnd` is not needed,
  and is obsolete and unimplemented in 18.x anyway.
* **Source lifetime.** `GetProposalSourceAsync` is called per view, including again when a document reloads
  (for example after line-ending normalisation). `DisposeAsync` is called on the old source.

## Limitations and gotchas

* The API is unofficial and moving: no docs beyond XML comments, internal metadata view, obsoletions between
  17.14 and 18.10, and undocumented runtime behaviour such as scenario 16 and internal flags. Pin to the 17.14
  SDK, wrap it in one adapter, and smoke-test every VS minor release.
* Only **pure insertions at the caret** render as ghost text. Any other edit renders as a diff preview, which
  is a different user experience.
* No accept or reject callback on the source. Telemetry and state must come from `SuggestionServiceBase` events,
  filtered by `ProposalId`, because `ProviderName` is always `IntelliCodeLineCompletions`.
* Copilot and NES share the same pipeline and UI. When Copilot is active the two products compete for the
  same slot. Pillion Programmer needs a policy: always first, step aside when Copilot is signed in, or a user
  setting.
* `[TextViewRole]` is ignored by the broker. Filter roles in `GetProposalSourceAsync` (return null).
* `RequestProposalsAsync` must be fast and cancellation-aware: it is called on every keystroke and caret move.
  The real implementation should debounce and return quickly, ideally `LazyProposalCollectionBase` or a cached
  result.
* Automation gotchas met during the spike:
  * On the first launch of a fresh Exp hive, a "What's new" page opened and took focus.
  * The editor needs a real mouse click before SendKeys reaches it.
  * DTE (`!VisualStudio.DTE.<ver>:<pid>` in the ROT) is the reliable way to position the caret and read text.
  * Unexpected: during the first Exp session the Exp instance showed a "Save changes?" (closing) prompt and
    exited. The separate non-Exp VS 2026 instance that was already open on the machine also exited at about the
    same time. The spike sent no keys to that instance. The cause is unknown and did not happen again.

## What an adornment-based fallback would need to cover

If the Proposals API breaks or is withdrawn, a self-drawn implementation needs all of the following, which VS
currently provides for free:

1. Inline ghost text at the caret on the caret line: an `IntraTextAdornment` / space-negotiating adornment, so
   text to the right of the caret moves right.
2. Extra visual lines for multi-line proposals, pushing real lines down (`ILineTransformSource` plus an
   adornment layer). Getting the gutter and line numbers right is the hard part.
3. Classified (syntax-coloured) rendering: run the classifier over a projection or temporary buffer, or accept
   flat grey.
4. Virtual-space handling: render from the virtual caret column and materialise the indentation on commit.
5. Tab and Esc command handlers (`ICommandHandler<TabKeyCommandArgs>` / `EscapeKeyCommandArgs`), ordered ahead of
   IntelliSense, snippets, brace completion and Copilot's own handlers. Fall through when no proposal is shown.
6. Reconciliation on typing (overtype-to-accept, shifting the insertion point, divergence detection), dismissal
   on caret move, focus loss, scroll and outlining.
7. Commit as one undo transaction, with formatting and caret placement after commit.
8. A hint bar or tooltip, next/previous cycling, and partial accept (word or line), if wanted.
9. Coexistence with Copilot's ghost text: two ghost texts at the same caret would collide. Detect Copilot's
   displayed proposal via `SuggestionServiceBase.GetDisplayedProposal` and stand down.

## Open items (need the product owner or VS 2022)

1. **Run the checklist on VS 2022 17.14.** Expected to behave the same (same API), but VS 2022's inline-completion
   host may be IntelliCode-based rather than Copilot-based. Check that `ProposalDisplayed` fires and ghost text
   shows with and without Copilot signed in.
2. **IntelliSense-coupled proposals (5b)**: find out why requests stop during a completion session and what
   shape of proposal is valid with a non-null `CompletionState`. Retest with Copilot disabled or signed out,
   which this spike did not do because changing the Copilot account state was out of scope.
3. **Copilot policy**: decide whether Pillion Programmer orders itself before Copilot (as in the spike), after
   Copilot, or as a user choice. Also test the "no Order attribute" case.
4. Optional: check `ProposalScenario.ExplicitInvocation` (a keyboard shortcut to request a suggestion) and
   `LazyProposalCollectionBase` for streaming model output.

## Checklist: rerun on VS 2022 17.14

1. Install VS 2022 17.14 with the *Visual Studio extension development* workload. Note whether Copilot is
   installed or signed in (Extensions > Manage Extensions, and the Copilot badge at top right).
2. Build and deploy with **VS 2022's** MSBuild, which deploys into the VS 2022 Exp hive (`17.0_<id>Exp`):
   ```
   "C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe" spikes\ProposalsSpike\ProposalsSpike.sln /restore /p:Configuration=Debug
   ```
   Alternatively: `"C:\Program Files\Microsoft Visual Studio\2022\Community\Common7\IDE\VSIXInstaller.exe" /rootSuffix:Exp spikes\ProposalsSpike\src\ProposalsSpike\bin\Debug\net48\ProposalsSpike.vsix`.
   Confirm it appears under Extensions > Manage Extensions > Installed in the Exp instance.
3. Delete `%TEMP%\ProposalsSpike.log`. Launch `devenv.exe /rootsuffix Exp` once by hand, get past any first-run
   or "What's new" pages, then close it.
4. Automated run (hands off the keyboard and mouse for about 3 minutes):
   ```
   pwsh -File spikes\ProposalsSpike\tools\Run-Scenarios.ps1 -DevEnv "C:\Program Files\Microsoft Visual Studio\2022\Community\Common7\IDE\devenv.exe"
   ```
   If keys do not reach the editor, pass `-EditorClick x,y` (window-relative pixels inside the editor, right of
   line 10). Screenshots go to `%TEMP%\ProposalsSpikeWork\shots`.
5. Manual run (instead of step 4). Open any C# file in the Exp instance and, on a new line inside a method:
   1. type `//ml`: grey multi-line text appears. Press Tab: 4 lines inserted.
   2. type `//noflags`, then press Esc: ghost text disappears and the buffer is unchanged.
   3. in `Console.WriteLine();` put the caret between the parentheses and type `/*right*/`: inline ghost text
      appears before `);`. Press Tab.
   4. type `//vs` and press Enter: ghost text appears on the empty, indented line. Press Tab: the line is
      correctly indented.
   5. type `//two`, then press Alt+. : the second proposal shows.
   6. note whether Copilot ghost text appears on ordinary lines (Copilot active) and whether ours replaces it on
      trigger lines.
6. Read `%TEMP%\ProposalsSpike.log` and check, for each scenario:
   * `MEF ... SpikeProposalSourceProvider created` (VS version is in that line).
   * `REQUEST ... scenario=TypeChar/Return` then `RESULT ... ours=True`.
   * `EVENT ProposalDisplayed ... ours=True`.
   * `EVENT SuggestionAccepted reason=AcceptedByCommand` for Tab, and `SuggestionDismissed reason=DismissedAfterUserEscape` for Esc.

   If there are `REQUEST` lines but no `ProposalDisplayed`, the 17.14 host is not displaying third-party
   proposals: record that as a blocker. If there are no `REQUEST` lines at all, check `GetProposalSourceAsync`
   lines and the Order/ContentType metadata.
7. Optional: dump the 17.14 runtime API with
   `dotnet run --project spikes\ProposalsSpike\tools\ApiDump -- "C:\Program Files\Microsoft Visual Studio\2022\Community\Common7\IDE\CommonExtensions\Microsoft\Editor\Microsoft.VisualStudio.Language.dll"`
   and compare it with `api/Proposals-17.14.249.txt`.
8. Close the Experimental Instance. Append a "Results on VS 2022 17.14" section to this file.

## Results on VS 2022 17.14.41

Run date: 2026-10-08. **Visual Studio Community 2022 17.14.41 (September 2026)**, installation version
17.14.37710.0, instance `cb15242f`, Experimental Instance hive `17.0_cb15242fExp`.

### Setup

* **Workloads.** This install has only the **Visual Studio core editor** workload
  (`Microsoft.VisualStudio.Workload.CoreEditor`): no *Visual Studio extension development* workload, no .NET or C++
  workload, so no Roslyn C# language service, no managed project system and **no IntelliCode**. Nothing was installed for
  the run. C# files open with TextMate colouring, which still gives them a content type derived from `text`, so the
  spike's `[ContentType("text")]` exports apply.
* **Copilot.** Not installed. The title-bar badge reads "GitHub Copilot" with the automation help text
  "Install GitHub Copilot components"; vswhere lists only `Microsoft.VisualStudio.Copilot.Contracts` 17.14.1713.
  Signed-in state is therefore moot.
* **Build and deploy.** Built with the **VS 2026 MSBuild** (the project targets the 17.14 SDK) with
  `/p:DeployExtension=false`, from a copy of `spikes/ProposalsSpike` outside the repo. Installed with
  `"C:\Program Files\Microsoft Visual Studio\2022\Community\Common7\IDE\VSIXInstaller.exe" /quiet /rootSuffix:Exp ProposalsSpike.vsix`
  (exit 0; installed to `17.0_cb15242fExp\Extensions\vgwaa5nk.vyt`). The 17.14 VSIXInstaller hands off to the
  Visual Studio Installer's own `VSIXInstaller.exe` (setup engine 4.10.30). `ProposalsSpike.dll` is in the Exp MEF
  catalog (`ComponentModelCache\Microsoft.VisualStudio.Default.err` lists it among the scanned assemblies, with no error
  against it). Removed afterwards with `VSIXInstaller.exe /quiet /rootSuffix:Exp /uninstall:PillionProgrammer.ProposalsSpike.6b1f0c2e-6a3d-4f7e-9a51-2c4e8d7b9f10` (exit 0).
* **Driver.** `tools/Run-Scenarios.ps1` assumes a project system (`Scratch.sln`), which this install lacks, so the same
  steps were run from a session-local wrapper: `vsdrive.ps1` dot-sourced, `Get-ExpProcess` narrowed to a 2022
  `devenv.exe`, and `Start-Exp` opening the scratch **folder** plus `Program.cs` instead of the solution. The ten `Step`
  lines are unchanged. Gotcha: rewriting `Program.cs` on disk while it is open raises Visual Studio's modal "changed
  externally, reload?" prompt, and every DTE call then fails with `RPC_E_CALL_REJECTED` (PowerShell shows it as null
  properties) until the prompt is answered.

### Matrix

`%TEMP%\ProposalsSpike.log` stayed **empty for the whole run**: no `MEF ... SpikeProposalSourceProvider created`, no
`PROVIDER`, `MANAGER`, `REQUEST` or `EVENT` line, in a first launch, a single-scenario run and the full ten-step run.
Screenshots (kept in the session scratchpad) show no ghost text and no hint bar for any trigger.

| # | Scenario | Displayed | Tab | Esc | Evidence |
|---|---|---|---|---|---|
| 1 | Multi-line (`//ml`) | no | inserted indentation (`//ml` followed by spaces) | - | no `REQUEST` lines; doc line `            //ml    ` |
| 2 | Text right of caret (`/*right*/`) | no | inserted spaces before `);` | - | `Console.WriteLine(/*right*/ );` |
| 2b | Multi-line, text right of caret | no | spaces | - | `Console.WriteLine(/*rightml*/   );` |
| 2c | Replacement edit away from caret | no | spaces | - | `Console.WriteLine(/*replace*/   );` |
| 3 / 3b / 3c | Virtual space (`//vs`, `//vs0`) and after indentation (`//ind`) | no | Tab indented the empty line | - | no `Return` or `CaretMove` requests |
| 4 | Esc (`//noflags`) | no | - | nothing to dismiss | no `SuggestionDismissed` |
| 4b | Two proposals + Alt+. | no | - | - | no events |
| 5a | Coexistence | no other inline provider exists (no Copilot, no IntelliCode) | - | - | Copilot badge offers to install components |
| 5b | IntelliSense list open (`//isense` + `Console.WriteL`) | no | - | - | a TextMate word-completion list ("WriteLine") **was** open, and still no request |

### Why: 17.14 has no proposal broker on this install

The **contract** is present and identical to the SDK, but nothing in this install **consumes** it:

* Runtime `Common7\IDE\CommonExtensions\Microsoft\Editor\Microsoft.VisualStudio.Language.dll` is file version
  17.14.305.26414, assembly version 17.0.0.0. Dumped with `tools/ApiDump` (namespaces `...Language.Proposals` and
  `...Language.Suggestions`) and compared with `api/Proposals-17.14.249.txt` / `api/Suggestions-17.14.249.txt`: **no
  difference in types, members, values or attributes**. The only differences are the `///` summaries, because the
  runtime DLL has no XML doc file next to it. So the NuGet 17.14.249 dump is an accurate picture of 17.14.41, and the
  17.14 to 18.10 diff in `api/diff-17.14-vs-18.10.txt` stands.
* No assembly under the 2022 `Common7\IDE` contains `InlineCompletionsListener` or a `ProposalBrokerProvider`
  implementation. The only assemblies that mention `ProposalSourceProviderBase` are the contract itself and
  `Microsoft.VisualStudio.Platform.VSEditor.dll`, which (by its metadata) defines no proposal or suggestion-service type
  and only references `SuggestionServiceBase`, `ProposalDisplayedEventArgs` and `ProposalBase` (as does the 2022
  `Microsoft.VisualStudio.Editor.Implementation.dll`). Nothing `[ImportMany]`s our `ProposalSourceProviderBase`, so MEF
  never constructs `SpikeProposalSourceProvider`, which is exactly what the empty log shows.
* In **18.10** the same pieces are in the platform: `Microsoft.VisualStudio.Editor.Implementation.dll` defines
  `...SuggestionService.ProposalBrokerProvider`, `ProposalBroker`, `SuggestionService`, `SuggestionManager` and
  `...SuggestionService.InlineCompletion.InlineCompletionsListener`.
* In 17.x the broker, the suggestion service and the inline-completions host therefore come from an optional
  component, most likely **IntelliCode** (which is part of the .NET and C++ workloads but not of the core editor). The
  evidence is circumstantial: the provider name VS reports is `IntelliCodeLineCompletions`, the only name constants in
  the API are `IntelliCodeCSharpProposalSource` / `IntelliCodeCPlusPlusProposalSource`, and `Microsoft.VisualStudio.IntelliCode.dll` from the
  18.10 IntelliCode VSIX in the package cache references `ProposalSourceProviderBase` and `SuggestionServiceBase` (its
  C# proposal source). **Not verified**: the 17.14 IntelliCode package is
  not on this machine and nothing was installed.

### API differences observed (17.14.41 runtime vs 18.10 runtime)

As recorded above from the NuGet dump; the runtime check confirms them. 17.14 lacks: the `CompletionState` constructor
with `sessionId` and the `SessionId` field, `Proposal.TryCreateProposal(ProposalBase, IReadOnlyList<ProposedEdit>)`,
`ProposalAdjusterProviderBase`, `ProposalFlags.SingleEditAccept` / `UpdateOnAccept` / `DismissOnAnyEdit` /
`HideRemoteEdit`, `SuggestionServiceBase.DismissAndBlockProposalsAsync`, the newer `ReasonForAccept` /
`ReasonForDismiss` / `ReasonForUpdate` values and the `Microsoft.VisualStudio.Language.InlinePrompts` namespace.
Nothing in 17.14 is `[Obsolete]`. A spike compiled against 17.14 binds on both. The bigger difference is not in the
API but in **who implements it**: platform in 18.10, an optional component (apparently) in 17.14.

### Updated verdict (both versions)

* **VS 2026 18.10: viable**, unchanged: all core behaviours work through a plain MEF export, with the two caveats above
  (IntelliSense-coupled proposals unproven, API drift).
* **VS 2022 17.14.41: not viable on a core-editor-only install.** The source is never requested and nothing is
  displayed, in every scenario. It is **not yet known** whether it works on a 17.14 install that has IntelliCode (or
  Copilot), which is how most C# developers have it. **Gate criterion "Proposals API spike green on VS 2022 17.14 and
  VS 2026 18.x": Fail as tested.**
* Consequences for the product:
  1. Ghost text on 17.14 depends on a component we do not ship. At minimum, detect it at run time: if no proposal
     source is requested within a few seconds of the first document view, or `SuggestionServiceBase` cannot be
     imported, report "inline suggestions need IntelliCode on Visual Studio 2022" instead of failing silently.
  2. Decide between (a) declaring IntelliCode a prerequisite on 17.x (and adding it to the VSIX manifest's
     prerequisites for 17.x if the rerun below confirms it), (b) the adornment fallback described above for 17.x only,
     or (c) ghost text on 18.x only, with chat and other features still on 17.14.
* Next step (needs the product owner, because it installs software): add the **.NET desktop development** workload
  (or just the IntelliCode component) to VS 2022 17.14, then rerun this section's matrix. Check that a `MEF ...
  SpikeProposalSourceProvider created` line appears and that `ProposalDisplayed ... ours=True` fires; also note the
  Copilot badge state. If the source is created then, record which component brought the broker. (Done: see the next
  section.)

## Results on VS 2022 17.14.41 with the managed desktop workload and IntelliCode

Run date: 2026-10-08, evening. Same install as above (Visual Studio Community 2022 17.14.41, installation version
17.14.37710.0, instance `cb15242f`, hive `17.0_cb15242fExp`) after the product owner added the **.NET desktop
development** workload. vswhere now lists `Microsoft.VisualStudio.Workload.ManagedDesktop` 17.14.36518.2,
`Microsoft.VisualStudio.Component.IntelliCode` 17.14.37709.7, `Microsoft.VisualStudio.IntelliCode` 2.2.3078.1097 and
`Microsoft.VisualStudio.IntelliCode.CSharp` 1.1.0.2437901. Nothing else changed.

Copilot status: **not installed** (the title-bar badge still offers to install it). IntelliCode: **installed and active**.
In Tools > Options > IntelliCode > General, *C# suggestions*, *Inline diff views*, *Automatically generate code
completions in the Editor*, *Promote likely items in IntelliSense completion lists* and *Show indicator margin* are on
(screenshot `options-ic-general.png`). The log shows IntelliCode's own whole-line completions (`id=<guid>:<n>`,
`type=ProposalAndPrediction`, `flags=SimulateBraceCompletion`).

**Build, deploy and driver.** As in the first 17.14 run: a copy of `spikes/ProposalsSpike` outside the repo, built with
the VS 2026 MSBuild and `/p:DeployExtension=false`, installed with 2022's `VSIXInstaller.exe /quiet /rootSuffix:Exp`
(exit 0), then `devenv /rootsuffix Exp /updateconfiguration`. This time **`tools/Run-Scenarios.ps1` ran as written**
(`-DevEnv` set to the 2022 `devenv.exe`, `$env:SPIKE_WORK` set to the session scratchpad, `-KeepOpen`). The managed
project system loads the `dotnet new console` scratch project (net8.0, .NET SDK 10.0.401), Roslyn classifies the
file and CodeLens shows. No folder-based wrapper was needed. The spike was uninstalled at the end (`/uninstall:...`,
exit 0, then `/updateconfiguration`); no `devenv.exe` or `VSIXInstaller.exe` remained.

**Provider created.** The first log lines after launch:

```
PROVIDER   GetProposalManagerAsync contentType=CSharp
MEF        SpikeProposalSourceProvider created; SuggestionServiceBase=Microsoft.VisualStudio.IntelliCode.SuggestionService.Implementation.SuggestionService; VS process=17.14.37710.0 built by: d17.14
PROVIDER   GetProposalSourceAsync contentType=CSharp roles=[DEBUGGABLE,PRIMARYDOCUMENT,ANALYZABLE,DOCUMENT,EDITABLE,INTERACTIVE,STRUCTURED,ZOOMABLE,UBIDIFF,UBIRIGHTDIFF] document=True
```

### Who implements the broker on 17.14: IntelliCode (now verified)

`Extensions\Microsoft\IntelliCode\Microsoft.VisualStudio.IntelliCode.dll` (file version 2.2.3078.1097) is the only
assembly under the 2022 `Common7\IDE` that defines the consumers of the Proposals contract (metadata scan). It defines
`...IntelliCode.SuggestionService.Implementation.ProposalBrokerProvider` (exports `ProposalBrokerProviderBase`),
`ProposalBroker`, `SuggestionService` (exports `SuggestionServiceBase`, `[Name("IntelliCodeSuggestionService")]`,
`[ContentType("any")]`), `SuggestionManager` and `...InlineCompletion.InlineCompletionsListener` (`[ContentType("any")]`,
roles `DOCUMENT`, `ChatTextViewRole`, `EMBEDDED_PEEK_TEXT_VIEW`). It also defines the Tab, Return, TypeChar, Backspace
and next-suggestion command handlers. These are the same type names that 18.10 has in the platform
(`Microsoft.VisualStudio.Editor.Implementation.dll`), so the 18.x platform took over IntelliCode's implementation. On
17.14 the `SuggestionServiceBase` our provider imports is IntelliCode's, as the `MEF` line shows.

IntelliCode's own C# source and manager on 17.14 are `InlineCSharpProposalSourceProvider`
`[Name("IntelliCodeCSharpProposalSource")]` and `InlineCSharpProposalManagerProvider`
`[Name("IntelliCodeCSharpProposalManager")]`, both `[ContentType("CSharp")]`, with **no `[Order]`**. The names match the
`ProposalSourceProviders` / `ProposalManagerProviders` constants in the SDK. Every proposal, ours and IntelliCode's, is
reported under the provider name **`IntelliCodeLineCompletions`**, as on 18.10.

### Matrix

All rows come from the `Run-Scenarios.ps1` run unless marked. The log is `log-01-run-scenarios.txt` and the screenshots
are `<scenario>-shown.png` / `-after.png`, all kept in the session scratchpad.

| # | Scenario | Displayed | Tab | Esc | Evidence |
|---|---|---|---|---|---|
| 1 | Multi-line (`//ml`) | yes | accepted, 4 lines inserted | - | `ProposalDisplayed ... ours=True id=spike:ml:...` then `SuggestionAccepted reason=AcceptedByCommand`; doc `//ml multi-line ghost text` / `int first = 1;` / `int second = 2;` / `Console.WriteLine(first + second);` |
| 2 | Text right of caret (`/*right*/`) | yes, inline before `);` | accepted, `);` kept | - | doc `Console.WriteLine(/*right*/"spike: inserted before existing text");` |
| 2b | Multi-line with text right of caret | yes | accepted | - | doc `Console.WriteLine(/*rightml*/"first line" +` / `"second line");` |
| 2c | Replacement edit away from caret | yes, as a **diff preview** (red original, arrow, green replacement), as on 18.10 | accepted | - | accepted edits normalised to `[237..237)->"spike"` + `[239..239)->" // replaced by proposal"`; doc `Console.WriteLine(/*replace*/"spike"); // replaced by proposal` |
| 3 | Virtual space, padded (`//vs`) | yes, from the virtual caret column | accepted, but the line has **24** leading spaces | - | `virtual=True/12`; accepted edit still `[214..214)->"            int fromVirtualSpace..."`. **17.14 does not strip the padding** (18.10 did), so the 12 materialised virtual spaces plus our 12 doubled the indent |
| 3b | Virtual space, unpadded (`//vs0`) | yes | accepted, 12 leading spaces | - | correct on both versions: send unpadded text in virtual space |
| 3c | After real indentation (`//ind`) | yes (`CaretMove`) | accepted after a typed space shifted the insertion point | - | as on 18.10 (`[228..228)`) |
| 4 | Esc (`//noflags`) | yes | - | dismissed, buffer unchanged | `SuggestionDismissed reason=DismissedAfterUserEscape` |
| 4b | Two proposals + Alt+. | first shown; **Alt+. does not show the second** | - | dismissed | no `ProposalRejected` and no second `ProposalDisplayed` after Alt+. Retried by hand with Alt+. and with `DTE.ExecuteCommand("Edit.NextSuggestion")`, which is bound to Alt+. in Text Editor and handled by IntelliCode's `InlineNextSuggestionCommandHandler` (`NextSuggestionCommandArgs`). The first proposal stayed and no event fired (`two2-*.png`) |
| 5a | Coexistence with IntelliCode | ours shown when we return one; IntelliCode's shown when we return null | - | - | see below |
| 5b | IntelliSense-coupled | **yes** (variant build, list open) | first Tab commits the item, second Tab accepts ours | - | see below. The original `//isense` row was again never requested with `WriteLine` selected |

What the screenshots show (`ml-shown.png`, `right-shown.png`, `vs-shown.png`):

* **Rendering.** Ghost text is **flat grey, not italic and not syntax-coloured** (18.10 draws it italic and classified).
  Multi-line proposals push the following lines down, as on 18.10.
* **Hint bar.** `Pillion spike: ml  [Tab] to accept (spike)` appears above the line, so `Description` and `AcceptText`
  are honoured. No Esc hint and no next/previous hint is drawn.

### Coexistence with IntelliCode and the IntelliSense case

A session-local variant (not committed) added a `//compete` marker. On the line after it, our source returns the
insertion `/*pillion*/` at the caret for every request, passing through the `CompletionState` when there is one. The
variant also added a second source provider with **no `[Order]`**, active only while `%TEMP%\ProposalsSpike.noorder`
exists (the ordered one then returns null). Places where IntelliCode reliably proposes were found first with our source
returning null: after typing `Conso` (completion list open on `Console`, IntelliCode proposes `.WriteLine(`), and after
`var s ` (no list, IntelliCode proposes `= Console.ReadLine();`). Logs are `log-02-manual.txt` and `log-03-compete.txt`.

* **IntelliCode shows its own proposals when our source returns nothing.** In the `//isense` run and on plain lines:
  `ProposalDisplayed ... ours=False id=<guid>:0 type=ProposalAndPrediction flags=SimulateBraceCompletion edits=[...".WriteLine("]`.
  Our source is still called whenever any manager says yes, including on lines without our markers (IntelliCode's
  manager says yes there), so it must return null cheaply.
* **Ours takes the slot when we return a proposal.** At `var s `, ours (`/*pillion*/`) was displayed and IntelliCode's
  `= Console.ReadLine();` never appeared (`ordered-vars.png`). At `Conso` with the list open, ours was displayed instead
  of `.WriteLine(` (`compete-conso.png`).
* **But the broker keeps the other sources' proposals as fallbacks.** In a second `Conso` run, IntelliCode had also
  answered the earlier `Return` request with `Console.WriteLine(` at the line start. When our proposal diverged on the
  first typed character (`ProposalRejected reason=DivergedAfterTypeChar ours=True`), the broker displayed IntelliCode's
  queued proposal 8 ms later (`ProposalDisplayed ... ours=False id=...:1`) **without asking our source again**. While
  that proposal stayed valid as the user typed `onso`, neither our manager nor our source was called. Ordering
  decides who is shown first, not who owns the line. This also explains the original `//isense` row: once IntelliCode's
  `.WriteLine(` is displayed and still matches, no source is asked, so no request arrives with `WriteLine` selected.
* **Ordering made no visible difference here.** With the unordered provider active, the same two tests gave the same
  result: ours displayed at `var s ` and first at `Conso`, then IntelliCode's fallback after divergence. Our source
  answers synchronously while IntelliCode's runs a model, so being first to answer may matter as much as `[Order]`. Not
  settled.
* **IntelliSense-coupled proposals work on 17.14.** With the list open on `Console` and the `CompletionState` passed
  through, `ProposalDisplayed ... ours=True id=spike:compete-cs:...` showed `Conso` + `le` (the item remainder, italic) +
  ghost `/*pillion*/` (`compete-conso.png`). The first Tab committed `Console` and left the ghost text in place. The
  second Tab gave `SuggestionAccepted reason=AcceptedByCommand` and the line `Console/*pillion*/`. That was with
  `ProposalFlags.None`; `SingleTabToAccept` (one Tab for both) was not tested. This is the 18.10 Caveat 1 shape that was
  never displayed there, so it should be retested on 18.10 with the same variant.
* **IntelliCode's option switches off our ghost text too.** With *Automatically generate code completions in the
  Editor* unchecked in Tools > Options > IntelliCode > General, typing `//ml` produced **no manager call, no request and
  no ghost text**. IntelliCode's own proposal at `Conso` disappeared as well (`t2off-ml.png`). Turning the option back on
  restored both immediately (`t3on-*`; log `log-04-intellicode-option.txt`). So on 17.14, a user who turns off
  IntelliCode's completions also turns off Pillion's ghost text, and nothing tells our extension.

### Comparison with 18.10

| | 18.10 (platform broker, Copilot active) | 17.14.41 with IntelliCode (IntelliCode broker, no Copilot) |
|---|---|---|
| Broker / suggestion service | platform, `Microsoft.VisualStudio.Editor.Implementation.dll` | IntelliCode 2.2.3078.1097, `Microsoft.VisualStudio.IntelliCode.dll`; **absent** without IntelliCode |
| Provider name in events | `IntelliCodeLineCompletions` | same |
| Multi-line, text right of caret, after indentation, Tab, Esc | work | work |
| Replacement away from caret | diff preview | diff preview |
| Virtual space, padded text | padding stripped, correct indent | padding **kept**, indent doubled |
| Virtual space, unpadded | correct | correct |
| Rendering | italic, syntax-coloured | flat grey |
| Alt+. cycling between two proposals in one collection | works | **does not** |
| IntelliSense-coupled proposal | not displayed (only shape tried: continuation with `SingleTabToAccept`) | displayed (insertion at caret with the `CompletionState`, `ProposalFlags.None`), two Tabs |
| Other provider's proposals when ours returns null | Copilot and NES | IntelliCode whole-line |
| Ours vs the other provider when both have one | ours shown (`[Order(Before = ...)]`) | ours shown first; the other's queued proposal takes over after ours diverges |
| Another product's setting switches ours off | not seen | **yes**: IntelliCode's *Automatically generate code completions in the Editor* |

### Final verdict (both versions)

* **VS 2026 18.10: viable**, unchanged, with the caveats in the first verdict. Those are API drift, and IntelliSense-coupled
  proposals, which still need a retest with the shape that worked on 17.14.
* **VS 2022 17.14.41: viable only with IntelliCode present**, and only while IntelliCode's *Automatically generate
  code completions in the Editor* option is on. IntelliCode comes with the .NET desktop and other .NET workloads. With it,
  every core behaviour works through the same MEF export: multi-line, text to the right of the caret, virtual space
  (unpadded text), after indentation, Tab, Esc, and IntelliSense-coupled proposals. Without it (core editor only), the
  provider is never created. **Gate criterion "Proposals API spike green on VS 2022 17.14 and VS 2026 18.x": Pass, on
  condition that IntelliCode is a 17.x requirement.**
* Differences to design for on 17.14: never pad virtual-space proposals; do not rely on next/previous cycling; expect grey
  rather than classified rendering; expect IntelliCode's proposals to take over when ours diverges.
* Product decision needed. The options are: make IntelliCode (with its automatic completions on) a stated requirement for
  ghost text on Visual Studio 2022, build the adornment fallback for 17.x, or ship ghost text on 18.x only. Whichever is
  chosen, detect the problem at run time on 17.x and say so in the status bar instead of failing silently. The cases are:
  `SuggestionServiceBase` cannot be imported (no IntelliCode), or the source gets no request within a few seconds of a
  document view (IntelliCode's option off). A VSIX prerequisite on `Microsoft.VisualStudio.Component.IntelliCode` would
  block installation on core-editor-only installs, including on 18.x where it is not needed, so a run-time check fits
  better.

### Still unknown

1. Why Alt+. / `Edit.NextSuggestion` does not cycle to the second proposal in our collection on 17.14. It may cycle only
   across sources, or only through IntelliCode's own proposals.
2. Whether `[Order]` or response time decides which source is displayed first. A slow source (a real model) may lose to
   IntelliCode's on 17.14 and to Copilot's on 18.x.
3. Whether a source can stop the broker from falling back to another source's queued proposal after ours diverges.
4. Behaviour with GitHub Copilot installed on 17.14. Copilot for 17.x ships its own completions source; it is not
   installed here.
5. Whether the IntelliSense-coupled shape that worked here also works on 18.10, and whether `SingleTabToAccept` makes it
   a single Tab.
6. Other 17.x minor versions. IntelliCode is updated separately, so its broker can change without a Visual Studio update.

Automation note: the unexplained "Save changes?" prompt in the 18.10 session (see Limitations and gotchas) is probably
self-inflicted. `Focus-Exp` taps Alt to release the foreground lock, which can leave the menu bar in keyboard mode. The
next Enter then opens the File menu, and a typed `t` picks **Close Solu&tion**. It happened once in this session when
`Focus-Exp` was called explicitly before typing `//two`, and was cancelled with Esc. Clicking into the editor after
`Focus-Exp` (as `Run-Scenarios.ps1` does) avoids it.

## Coexistence countermeasure (work item 1.4)

Run date: 2026-10-10. Visual Studio Community 2026 **18.10.12224.181** with GitHub Copilot signed in, and Visual Studio
Community 2022 **17.14.41** with IntelliCode (automatic completions on, no Copilot), both under `/rootsuffix PillionCx`,
with the real Pillion Host and Ollama `qwen2.5-coder:7b` on the GPU. Traces, screenshots and the drivers stayed in the
session scratchpad, not in the repo.

### What the broker does, measured with canned proposals

A session-local probe build (not committed) made Pillion's source return a fixed `/*P*/` at every request, optionally
after a delay, and logged every suggestion-service event and every manager and source call.

* **Queued takeover, both versions.** Pillion's proposal shown, a typed character that does not match it
  (`DivergedAfterTypeChar`), then the competitor's proposal from the *same* broker request displayed 30 ms later (18.10,
  Copilot) or 41 ms later (17.14, IntelliCode). Neither Pillion's manager nor its source is asked while that proposal still
  matches what is typed. This reproduces the Gate 0 finding.
* **The broker waits about 0.8 s for a source ordered ahead.** With Pillion answering after 400 ms, Pillion's proposal was
  shown and Copilot's queued. With 800 ms or 1,500 ms, Copilot's was shown about 790 ms after the request (18.10) and
  IntelliCode's about 850 ms after it (17.14); Pillion's later answer was queued, not shown. So `[Order]` does decide,
  but only inside that window. That is why it looked indecisive in the spike: an answer after the window loses.
* **`SuggestionServiceBase.TryUpdateProposal` cannot replace another source's proposal.** It returns the proposal, but the
  competitor's stays displayed (18.10).
* **A second suggestion provider is not usable.** With `TryRegisterProviderAsync` and `TryDisplaySuggestionAsync`, no
  session is returned while another proposal is displayed, and a second registration in the same view returns no manager.
* **An explicit invocation reaches Pillion's source.** `Edit.NextSuggestion` (command group `160961B3-...`, id 94) with
  nothing displayed makes the broker ask every manager and source with `ProposalScenario.ExplicitInvocation`, on both
  versions. With a proposal displayed it cycles instead. On 18.10 this also works across sources: it brought a late,
  queued Pillion proposal forward (`DivergedDueToChangeProposal`).
* **Only a dismissal with `DismissedAfterUserEscape` clears the broker's queue.** After `DismissProposalAsync` with
  `DismissedForRefinement`, `DismissedBySession` or `DismissedAfterCaretMoved`, an explicit invocation re-showed the queued
  competitor proposal (same id) without asking any source. After `DismissedAfterUserEscape` it asked every source and
  Pillion's answer was shown (18.10; the same on 17.14).
* **Never dismiss and invoke from inside the suggestion service's own event handling.** On 17.14, doing both inside the
  competitor's `ProposalDisplayed` handling (a type-through cache hit made the push run 16 ms after the display) left
  IntelliCode's broker consulting the managers but asking no source again for the rest of the session. This happened in
  two runs. The shipped code waits 150 ms off the handler before a push.
* **`ProposalBrokerProviderBase.GetProposalBroker(view).RequestProposals(...)` is public** and returns every source's
  collection (Copilot's source name is `CompletionsProposalSource`). It makes every source run again, so it is not used to
  inspect the competitor.
* **Detection without private APIs.** The `Name` metadata of the `ProposalSourceProviderBase` exports names the
  competitors: `CompletionsProposalSourceProvider` (Copilot, 18.10), `IntelliCodeCSharpProposalSource` (both versions) and
  `IntelliCodeVCProposalSource` (18.10). On a fresh hive, the settings manager returns `Missing` for
  `Microsoft.VisualStudio.Conversations.AreCopilotCompletionsEnabled`,
  `Microsoft.VisualStudio.IntelliCode.ShowIntellicodeCompletions`, `...ShowAICompletions` and Visual Studio 18's
  `textEditor.codeCompletions.general.codeCompletionsProviders`. Until the user changes them, "on by default" is all that
  can be read. Copilot's sign-in state has no public API. Proposal ids tell the providers apart: Copilot's start with `cpt`
  (completions) or `nes`, and IntelliCode's are a GUID, a colon and a number.
* **Settings links.** On 17.14, `Tools.Options` with IntelliCode's General page GUID (`c6a0ae28-f8eb-330b-8012-35a3022dded0`)
  opens that page. On 18.10, `Tools.Options` opens the unified Settings document; a category moniker does not navigate,
  so the link opens Settings at the top.
* **Shortcut.** Alt+\ is bound to `Edit.GoToMember` (Global) in Visual Studio 2026's default scheme. "Pillion: Suggest" is
  therefore on the prefix chord, then S (`Global::Ctrl+Shift+Alt+P, S`, no conflict).

### Countermeasures evaluated

* **(a) Re-request outside the broker** whenever a competitor's proposal is displayed, and on every keystroke while it is
  typed through (the broker asks nobody then). **(b) Type-through from the cache** is tried first. Both only produce a
  Pillion suggestion; showing it needs the dismiss-and-explicit-invocation lever above.
* **Replace** (chosen) combines (a) and (b) with showing the result:
  * The competitor's proposal stays on screen until Pillion has a suggestion for exactly that caret and text.
  * Then Pillion dismisses it with the user-Escape reason and invokes explicitly. Pillion's source answers at once from
    what it kept.
  * This also applies when Pillion's answer arrives after the broker's 0.8 s wait.
  * At most once per position. Never while a completion list is open, after the user cycled with Alt+., or when Pillion
    has nothing.
  * If the explicit invocation re-shows a queued competitor proposal instead (seen once on 18.10 right after the user
    pressed Esc), Pillion dismisses it and asks once more.
* **(c) Decline**: dismiss the competitor's proposal at once while a Pillion request for that caret is in flight, then
  show Pillion's suggestion, or ask every source again when Pillion has nothing.

### Measurements: steady typing of a known snippet

**Driver.** A session-local script (built on `tools/vsdrive.ps1`) types seven C# statements into `Main` of a scratch
console project. It sends one key every 110 ms through SendKeys, waits 0.9 s at each line end, then presses Esc, End and
Enter. Each row is two runs, and Tab is never pressed. The measurement variable `PILLION_AUTOCOMPLETE_COUNTERMEASURE`
(`off`, `replace`, `decline`) switched the countermeasure, and `PILLION_COEXISTENCE_TRACE` wrote the slot timeline.

**Columns:**

* **Displaced**: a Pillion proposal that a competitor's replaced while it was shown, or within 250 ms of diverging. This is
  the new `displaced` metrics outcome.
* **Not regained**: no Pillion proposal appeared within 2 s after the displacement.
* **Pillion slot share**: the time a Pillion proposal was displayed, over the time any proposal was displayed.

| Version, competitor | Build, countermeasure | Pillion shown | Displaced | Not regained | Competitor shown | Taken back (Pillion then shown) | Pillion slot share |
| --- | --- | --- | --- | --- | --- | --- | --- |
| 18.10, Copilot | off (before) | 34 | 2 (5.9%) | 2 | 13 | 0 | 90.4% (183 s vs 19.5 s) |
| 18.10, Copilot | replace, first build | 35 | 2 (5.7%) | 2 | 11 | 4 (2; Copilot re-proposed the other 2) | 96.6% (196 s vs 7.0 s) |
| 18.10, Copilot | replace, second build | 34 | 2 (5.9%) | 2 | 10 | 0 | 96.3% (211 s vs 8.1 s) |
| 18.10, Copilot | replace, final build | 37 | 4 (10.8%) | 2 | 14 | 3 (3) | 95.2% (213 s vs 10.8 s) |
| 18.10, Copilot | decline | 36 | 2 (5.6%) | 2 | 14 | - | 100% (Copilot 0 s on screen) |
| 17.14, IntelliCode | off (before) | 38 | 5 (13.2%) | 2 | 20 | 0 | 87.5% (206 s vs 29.4 s) |
| 17.14, IntelliCode | off, final build | 39 | 1 (2.6%) | 0 | 19 | 0 | 88.4% (224 s vs 29.6 s) |
| 17.14, IntelliCode | replace, first build | 40 | 5 (12.5%) | 2 | 19 | 7 (7) | 95.0% (214 s vs 11.3 s) |
| 17.14, IntelliCode | replace, second build (push inside the display handler) | 1 | 0 | 0 | 1 | 1 (0) | IntelliCode's broker stopped asking any source after the first push |
| 17.14, IntelliCode | replace, final build (push 150 ms off the handler) | 43 | 2 (4.7%) | 1 | 20 | 4 (4) | 91.9% (235 s vs 20.6 s) |
| 17.14, IntelliCode | decline | 1 | 0 | 0 | 1 | - | the same stop after its first dismiss-then-invoke, in two runs |

**Reading the table:**

* **Displacement is rare during steady typing.** With the real 7B model, the pipeline usually answers inside the broker's
  0.8 s wait, so Pillion's proposal is shown first: 2 to 4 displaced per 34 to 37 shown on 18.10, and 1 to 5 per 38 to 43 on 17.14.
  In the displaced cases that were not regained, the re-request found **no better Pillion suggestion** for the new caret
  (the model answered empty or with the text already typed), so "replace" correctly left the competitor's proposal in
  place; the others were taken back. The
  displacement rate itself cannot drop further without starving the competitor.
* **"Replace" reduces the competitor's other time in the slot.** When the competitor answered first or Pillion answered
  late, Pillion's suggestion took the slot back. Pillion's share rose from 90% to 95 to 97% on 18.10, and from 88% to 92 to 95%
  on 17.14. The competitor was still shown 10 to 20 times when Pillion had nothing, and its proposal was never removed
  without one of Pillion's taking its place.
* **"Decline" was not chosen.** It starved Copilot completely (14 proposals, 0 s on screen). On 17.14, its immediate
  dismiss-then-invoke stopped IntelliCode's broker.
* **Chosen rules:** `pillionFirst` by default, with "replace", as listed in the README ("When Copilot or IntelliCode also
  suggest").
* **Other orderings, checked by hand:**
  * `competitorFirst` gave IntelliCode 93% of the slot on 17.14 and Copilot 74% on 18.10, with Pillion filling in only
    when nothing was displayed.
  * `onDemand` showed no Pillion proposal while typing. "Pillion: Suggest" showed one at the caret on both versions,
    3 times out of 3 on 18.10 straight after Esc.
* **Info bar.** It appeared on the first document on 17.14 (IntelliCode, at start-up) and on 18.10 (at the first Copilot
  proposal). Answering it wrote `autocomplete.ordering` and `autocomplete.coexistencePromptDone`, and it did not come back
  after a restart once the host's settings were read. The first build read the flag before the host answered and showed
  the bar again; that is fixed.

**Side effects.** The competitor records each replaced proposal as dismissed by Escape, because no other reason clears the
queue. Each takeover also makes every source run once more for the explicit invocation, Copilot's included.

**Not verified:**

* The prefix chord itself. Ctrl+Alt+Shift chords could not be injected with SendKeys or `keybd_event`, including the
  existing Open Chat chord. The binding is registered and the command was run through DTE.
* Copilot on Visual Studio 2022.
* A slower model or the CPU tier, where Pillion would miss the 0.8 s window more often and "replace" would matter more.
