# @-mentions

Type `@` in the Chat window's message box to pin context to the conversation: a file, a folder, a symbol, a document, the build
output, the working-tree diff. A pin is sent with every message of the conversation and shows its token cost. This page is the
reference for the grammar, every kind with where it comes from and how much of it fits, what needs approval, and how replies
cite what they used. The design is section 7 (Context pinning with @-mentions) of the
[design document](design-document.md); the work is items 1.9 (first five kinds), 2.2 (pins saved with the conversation) and 2.13
(the rest). Mentions are behind the `mentions` feature flag (see the [README](../README.md#feature-flags)).

## The grammar

- `@` followed by a **fuzzy name** resolves to a file, a symbol or a folder: `@Customer.cs` pins the file.
- `@kind:value` names the kind and has **no fuzzy fallback**: `@symbol:OrderService`, `@folder:src/Api`, `@file:src/Program.cs`,
  `@git:staged`, `@git:log:5`, `@output:debug`.
- A bare kind with no value works for the kinds that need none: `@selection`, `@errors`, `@clipboard`, `@build`, `@tests`,
  `@solution`, `@git`, `@output`, `@docs` (which lists the documents).
- A colon starts a value only after a **known kind**, so `@C:\src\Foo.cs` is one fuzzy name, not the kind `C`.
- Values may be quoted: `@file:"src/My File.cs"`.

A bare `@name` tries a **file** first, then a **symbol** whose name matches exactly, then a **folder**. A file wins unless it only
matches loosely. When several files match you get up to ten candidates to pick from; when only a loose letter-order guess exists
it is offered as a candidate and never pinned on its own.

Fuzzy file matching goes by file name first, then by path, and the best tier with any match decides; a tier with exactly one match
resolves. For a name without a slash the tiers are: the whole file name, the name without its extension, a name that starts with
the text, a name that contains it, and last the letters in order (loose, candidates only). With a slash the path is compared: ends
with it, ends with it ignoring the extension, contains it, letters in order. Case and slash style are ignored.

## The picker

`@` opens a picker at the caret. It groups its entries under headings (kinds, files, folders, symbols, docs, and the options of
`@git` and `@output`), each with its kind's icon and a hint line. Up and Down move, Enter or Tab pick, Esc closes. As you type,
file names are matched against the solution's file list (the loaded projects, or the open folder, without `bin`, `obj`, `.git`,
`node_modules` and `.vs`); when you pick, the host resolves the name against the index first. The pin becomes a chip above the
message box with its token count and a remove button. Pins belong to the conversation and are saved with it as the kind, the
target and the display name: when you reopen the conversation they are resolved again, so a pinned file shows its current
content. The content itself is never stored.

**Include current file** (on by default) is separate from pins: it sends the active document's path and its selection, or the 60
lines around the caret, with each message, except while `@selection` is pinned.

## The twelve kinds

Token limits are the defaults when a request sets none; a larger pin is cut on a line boundary and marked truncated. Counts use
the tokenizer of the model the chat would route to.

| Mention | What is pinned | Where it comes from | Limit |
| --- | --- | --- | --- |
| `@file`, `@file:path` | The file's text. An ambiguous name lists up to 10 candidates | The open index's file list, fuzzy matched; the solution's file list when there is no index or no strong match (so secret files that the index leaves out can still resolve for approval) | 12,000 tokens; the file is read up to 1 MiB (binary refused) |
| `@folder`, `@folder:path` | An outline: each file's first-chunk summary and its top definitions | The index (files table and symbol graph); the folder on disk otherwise | 12,000 tokens; up to 200 files, 12 definitions under each |
| `@symbol:Name` | The definition's code and a summary of its references. Exact name first, then the same name in any case, then a prefix or substring | The index's symbol table for the definition; references from the live Roslyn workspace through Visual Studio for C# and VB (answered off the UI thread within 2 s), the index's approximate edges otherwise or when Roslyn cannot answer. `Name#path:line` picks one namesake (the picker offers it for an ambiguous name) | 6,000 tokens: the definition may take 70%, up to 400 lines read from disk when the index has no chunk; 50 reference sites are asked for and 30 listed |
| `@docs`, `@docs:name` | A Markdown or text document (`.md`, `.markdown`, `.mdx`, `.txt`, `.rst`, `.adoc`, `.asciidoc`), chunk by chunk in order; bare `@docs` lists the documents | The index, plus the folders in `mentions.docsFolders` | 8,000 tokens; the listing holds up to 2,000 documents |
| `@solution` | The repo map: files and their top symbols ranked by how much of the code refers to them, focused on the documents open in Visual Studio | The index (the symbol graph of each open root); Visual Studio is asked for the open documents and waited for up to 1.5 s | 4,000 tokens |
| `@git` | The working-tree diff (untracked files included). `@git:staged` the staged changes; `@git:log` the last commits with their patches (`@git:log:5` or `@git:log 5` for a count; 10 by default, 50 at most); `@git:A..B` the commits of a revision range | LibGit2Sharp, on the repository of the first workspace root. Every patch goes through the secret scanner (secrets become `<redacted:kind>`) and files the exclusion rules refuse are listed without their changes | 8,000 tokens; at most 400,000 characters and 300 files gathered first |
| `@output`, `@output:pane` | The end of an Output window pane: `build` (the default), `debug` or `tests` | Visual Studio; the extension sends the last 65,536 characters of the pane | 4,000 tokens, tail kept (the cut says how many earlier lines were omitted) |
| `@build` | The last build's outcome ("Last build (failed)" or "(succeeded)") and its output | Visual Studio; the Build pane as it was when the build finished. Unresolved if no build has finished since Visual Studio started | 4,000 tokens, tail kept |
| `@tests` | The Tests pane's text: failing tests with their stack traces | Visual Studio's Tests pane. Test Explorer has no documented event API, so run the tests first, then pin | 4,000 tokens, tail kept |
| `@selection` | The editor selection | Visual Studio | 12,000 tokens |
| `@clipboard` | The clipboard text | Visual Studio | 12,000 tokens |
| `@errors` | The diagnostics, errors first, one line each (severity, id, message, file, line) | The Error List as your filters show it | 12,000 tokens; at most 200 diagnostics |

`@terminal` (the last Pillion terminal command's screen) is part of the design but **not available yet**: it waits for the command
runner in phase 3, and answers "not available in this version".

A kind this version does not know is not an error in the host: the mention comes back unresolved with a reason, and the picker
under it says why.

## Secret files and approval

A file is refused until you approve it when it matches the `indexing.exclude` globs (or a repository policy's `exclude`), or the
host's secret-file rules: `.env`, `.env.*` (except examples such as `.env.example`), `*.pfx`, `*.p12`, `*.pem`, `*.key`, `*.jks`,
`*.keystore`, `secrets.json`, anything under `.ssh`, `.aws` or `.gnupg`, and an `appsettings.*.json` that mentions
`ConnectionStrings` or `Password`. (The indexer's own list of secret-bearing names is wider; see
[pillionignore.md](pillionignore.md).)

1. The mention comes back unresolved with `RequiresApproval`, and the Chat window asks: "This file looks like it holds secrets.
   Pin it anyway?"
2. If you agree, the window asks again with the approval, and the file is pinned with a **lock** on its chip.
3. An approved file is marked sensitive: **context assembly never sends it to a cloud model**, whatever the routing.

`@folder` leaves excluded and secret files out of its outline and counts them. `@git` lists such files without their changes.
`@symbol` shows no reference snippet from them. Nothing from a secret-bearing file is ever in the index, so retrieval cannot
return it either.

## Citations

Where a pin comes from specific lines, the resolution carries **citations** (up to 50 `file:line` spans): a symbol's definition and
references, the changed lines of a `@git` diff, and the files and lines that build diagnostics and stack traces name in
`@output`, `@build` and `@tests`. In the same way, a chat reply that used retrieved chunks cites them:

- When the reply names a retrieved chunk's location, with `path:line`, `path:start-end` or `#L12-L14` (an en dash, a bare file
  name or a full path also work), that becomes an **explicit** citation: a link at that point in the text that opens the file and
  selects the span.
- When the reply names none, every chunk that was sent is listed as an **implicit** citation under a collapsed **Sources** line.
- The Context Inspector (**Inspect**, beside Send) lists the retrieved chunks and the repo map before sending, each with a remove
  button, so you decide what the model sees.

A chat asks for 8 retrieved chunks by default (at most 20), found from your latest message plus the identifiers in pinned
selections, focused on the current and pinned files. Chunks of a pinned file are not repeated. Retrieval is asked only when an
index is open.

## Where it comes from, and when there is no index

Without an open index (indexing off, still discovering, or no repository) `@file` and `@folder` fall back to the solution's file
list as in phase 1, and `@symbol`, `@docs` and `@solution` say that they read the index and that none is open for this solution
or folder; open [the Index window](index-window.md) to see why. `@docs` can still read the folders in `mentions.docsFolders`:

```json
{ "mentions": { "docsFolders": ["C:\\docs\\handbook", "docs/adr"] } }
```

(full paths, or relative to the first open folder; empty by default).
