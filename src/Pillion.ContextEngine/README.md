# Pillion.ContextEngine

The Context Engine library (design section 7). Today it holds the **chunkers** (phase 2, work item 2.4) and **symbols
and the repo map** (work item 2.5, [below](#symbols-and-the-repo-map)); storage and retrieval join it in later phase 2
items. It is a .NET 10 class library with no Visual Studio dependency. Nothing references it yet except
`tests/Pillion.ContextEngine.Tests`; the host wires it into the indexing pipeline in work item 2.9, where it ships
behind the `features.indexing` flag. Until then it has no flag of its own, because nothing calls it.

## Versions

`ContextEngineVersions` holds two versions, each a string holding an integer (both now `"1"`). Bump one to the next integer in
the same pull request as the change that needs it.

`ChunkerVersion` is part of the index identity (design section 10: an index is keyed by repository root, embedding
model and chunker version) and is written to the index `meta` table as `chunker_version` by repository discovery (work
item 2.3). Changing it builds a new index, embeddings included. Bump it when the same file would produce different
chunks: boundaries, kinds, breadcrumbs, text, splitting, the window settings, the default token counting, the
extension-to-language map (`LanguageCatalog`) or a chunking grammar description (`TreeSitterGrammars`).

`SymbolsVersion` is written by the host to the index `meta` table as `symbols_version` (index schema version 3,
`IndexSymbolsSchema` in Pillion.LocalStore), separately from `chunker_version`. When the stored value is missing or
differs, only the `symbols` and `symbol_refs` tables are rebuilt: no re-chunking, no re-embedding. Bump it when the
stored symbols would differ: which definitions and references are extracted, their kinds, names, containers, lines or
signatures, a symbol grammar description (`TreeSitterSymbolGrammars`), the symbol id formula, or the resolution rules
(they decide the stored `symbol_refs` edges). Resolution changes bump the symbols version only; chunk changes bump the
chunker version only.

Bump neither for changes that leave the stored output identical (refactoring, performance work, tests, documentation)
or that only affect results computed from it (ranking, the repo map renderer, the query helpers).

## Using it

```csharp
var chunker = new FileChunker();                       // LanguageCatalog.Default
var options = ChunkOptions.Default with
{
    MaxTokens = 512,                                   // the embedding model's input size
    TokenCounter = TokenCounters.FromWordPieceVocabulary(vocabStream), // the model's own tokenizer
    OnFallback = f => log.LogDebug("{Path}: {Reason}", f.Path, f.Reason),
};
IReadOnlyList<Chunk> chunks = chunker.Chunk("src/Orders/OrderService.cs", text, options);
```

Each `Chunk` has the file path, 1-based inclusive `StartLine` and `EndLine`, a `Kind` (`Document`, `TypeHeader`,
`Member`, `Statement`, `Markup`, `Window`), the breadcrumb, the text, and `TokenCount`, the count of
`EmbeddingText` (breadcrumb, line break, text) in the injected counter. `Part`/`PartCount` and `IsTruncated` describe
splitting. All chunkers are thread-safe and never throw because of file content.

## Rules

- **Breadcrumb**: `path > namespace > type > member`, segments joined by `" > "`, with `" (part n of m)"` on the
  parts of a split unit (`Breadcrumbs` formats, sanitises and parses them). Over the breadcrumb budget
  (`MaxBreadcrumbTokens`, at most a quarter of the cap) the path is shortened to the file name, then segments are cut.
- **Cap**: `MaxTokens` (default 512) bounds `TokenCount`, breadcrumb included, so room for the breadcrumb is always
  left. A unit over the cap is split into parts at statement boundaries (preferring the outermost statements), or at
  line boundaries when it has none; a single line over the cap on its own is truncated and marked `IsTruncated`.
- **Coverage**: syntactic chunkers cover every line of the file exactly once. Comments, doc comments and attributes
  before a unit lead into it; blank lines and closing lines (`}`, `End Class`, `</div>`, `#endif`, `GO`) after a unit
  stay with it. Runs of using directives, imports and fields are grouped into one chunk.
- **Fallback window**: 60 lines with 10 lines of overlap, breadcrumb = path only, for any file without a syntactic
  chunker, and for a file whose parse fails badly (Roslyn: more than one syntax error per 20 lines; tree-sitter: more
  than 30% of the text inside error nodes), whose grammar cannot be loaded, or whose chunker fails unexpectedly.
  `ChunkOptions.OnFallback` reports each fallback.
- **Token counting**: the counter is injected. `TokenCounters.FromWordPieceVocabulary` builds Microsoft.ML.Tokenizers'
  `BertTokenizer` from a `vocab.txt` (the bundled bge-small model's format, work item 2.7) and
  `TokenCounters.FromTokenizer` wraps any tokenizer. The default, `TokenCounters.ApproximateWordPiece`, is a
  deterministic approximation that errs high, for tests and until the model lands.
- Discovery (work item 2.3) excludes generated, minified, binary, oversized and secret-bearing files before chunking.

## Languages

| Language | Extensions | Chunker |
| --- | --- | --- |
| C# | `.cs`, `.csx` | Roslyn syntax tree (no compilation) |
| Visual Basic | `.vb` | Roslyn syntax tree (no compilation) |
| Razor | `.razor`, `.cshtml` | `@code`/`@functions` members by the C# chunker, `@{ }` blocks as one unit split at statements, markup as HTML |
| TypeScript, TSX, JavaScript, Python, C, C++, Go, Rust, Java, CSS | see `LanguageCatalog.Default` | tree-sitter, one generic walker driven by `TreeSitterGrammars` |
| HTML | `.html`, `.htm`, `.xhtml` | tree-sitter HTML: elements that fit are units, larger ones are opened, small siblings grouped |
| SQL | `.sql` | Pillion's own statement lexer (no SQL grammar ships with the binding) |
| Anything else | | window |

The map is data: `LanguageCatalog` maps extensions to `LanguageDefinition`s, and each tree-sitter language is a
`TreeSitterGrammar` record listing its namespace, type, member, wrapper and block node types. Adding a grammar means
a `TreeSitterGrammar` record, a catalog entry, and the grammar's native library name in `PillionTreeSitterLibrary` in
the project file.

## tree-sitter binding

**TreeSitter.DotNet 1.3.0** (Marius Greuel, https://github.com/mariusgreuel/tree-sitter-dotnet-bindings), MIT,
chosen on 2026-10-15 after comparing the tree-sitter packages on nuget.org:

- It is the maintained, widely used binding (about 240,000 downloads), targets netstandard2.0 with no dependencies,
  and ships prebuilt native libraries for the tree-sitter runtime and 30 grammars on win-x64 and **win-arm64** (and
  x86, Linux and macOS), so nothing native is built here. Every grammar Pillion needs is included except SQL.
- The `tree-sitter` / `tree-sitter.runtime.*` packages (0.4.x/1.0.0) ship only C, C++, JSON and Lua grammars;
  `TreeSitterSharp` likewise. `TreeSitterLanguagePack` (306 grammars, depends on TreeSitter.DotNet) is a 194 MB
  package with no licence expression, too heavy for a VSIX.
- The tree-sitter runtime and the grammars Pillion loads (C, C++, CSS, Go, HTML, Java, JavaScript, Python, Rust,
  TypeScript and TSX) are MIT; their notices are in `THIRD-PARTY-NOTICES.txt`.

The package is about 260 MB across all runtimes. The project excludes its native assets and copies only the 12
libraries the chunkers load, for win-x64 and win-arm64 (about 13 MB per runtime), to `runtimes\<rid>\native\` in
every project that references it. Because those files are then not in the dependency manifest, `TreeSitterNative`
registers an import resolver so the bindings find `tree-sitter.dll` there; the grammar libraries are found by the
bindings' own loader.

## Measured

On the development machine (Release build): a 100,000-line synthetic C# file (1.8 MB, 11,481 chunks) in about 0.5 s;
1 MB files in 0.01 s (text windows) to 0.4-0.7 s (tree-sitter languages, where the native parse is most of the
time). The benchmark tests in `tests/Pillion.ContextEngine.Tests` log the figures on every run.

## Symbols and the repo map

Design section 7, indexing step 4: syntax-derived definitions and name-based references for every chunked language,
all approximate, feeding the `symbols` and `symbol_refs` tables (section 10), the repo map and the index fallback for
`find_references` and `callers_of`. Everything here is in memory; the indexing pipeline (2.9) persists the records
through the Local Store (2.8).

```csharp
var extractor = new FileSymbolExtractor();                     // LanguageCatalog.Default, every language below
var builder = new SymbolGraphBuilder(new SymbolGraphOptions { PartitionOf = path => partitions[path] });
foreach (var (path, text) in files)
{
    builder.AddFile(extractor.Extract(path, text, new SymbolExtractionOptions { OnFailure = f => log.LogDebug("{Path}: {Reason}", f.Path, f.Reason) }));
}

SymbolGraph graph = builder.Build();                           // definitions + symbol_refs-shaped edges
RepoMap map = RepoMapRenderer.Render(graph, RepoMapOptions.Default with
{
    MaxTokens = 1024, TokenCounter = chatModelCounter, FocusFiles = openFiles,
});
var callers = graph.CallersOf(graph.FindDefinitions("OrderService.Place")[0].Id);
```

### Records

- `SymbolDefinition`: file path, name (without type parameters), `SymbolKind`, container breadcrumb (the chunk
  breadcrumb's segments without the path, for example `Fixtures.Sample > Greeter<T>`), 1-based start and end line
  (the declaration itself, after doc comments and attributes), a one-line signature (at most 200 characters), the
  language, `IsApproximate` (always true here) and `Id`.
- `Id` = the first 63 bits of SHA-256 over `path \0 container \0 name \0 kind \0 ordinal`, where the overload ordinal is the
  0-based position among the file's definitions with the same container, name and kind, in source order. There is no line
  in it, so edits that move a definition keep its id (and the edges from other files to it); only renaming, moving it to
  another container, or inserting an overload before it changes it. Positive and never 0; `SymbolIds.Compute` exposes the
  formula and `SymbolDefinition.Ordinal` the ordinal.
- `SymbolReference`: file path, line, referenced name, `SymbolReferenceKind` (`Call`, `TypeUse`, `Inheritance`,
  `Import`, `Use`), the qualifier written before the name (`Point` in `Point::new()`, `order` in `order.Total`), the
  from-container breadcrumb of the code it is in, and `IsConstruction` for `new X()`.
- `SymbolEdge`: from symbol id (null for file-level code), to symbol id, file path, line, kind, `CandidateCount` and
  `IsAmbiguous`: a `symbol_refs` row except for the file id.
- Kinds and reference kinds have stable numeric values and stable lower-case names (`SymbolKinds.Name`).

### Languages

| Language | Definitions | References |
| --- | --- | --- |
| C#, VB (Roslyn syntax, no semantic model) | namespaces, classes, structs, records, interfaces, enums and members, VB modules, methods, constructors, operators, properties, indexers, events, fields, constants, delegates, record parameters, `Declare` | `using`/`Imports`, base lists and `Inherits`/`Implements`, invocations, `new`, names in type-only positions, attributes, member accesses and other identifiers except locals, parameters and type parameters of the enclosing member |
| Razor | the component (a class named after the file), `@code`/`@functions` members by the C# rules | C# in `@code` and `@{ }`, `@using`, `@inherits`, `@implements`, `@inject`, `@model`, `@layout`, component tags, `@name` expressions, `@onclick="Handler"` |
| TypeScript, TSX, JavaScript, Python, C, C++, Go, Rust, Java (tree-sitter) | the grammar's declaration nodes, top-level and type-level variables and fields (never locals), one module symbol per file for Python, JavaScript, TypeScript, Rust, C and C++ | calls and constructions, type identifiers, `extends`/`implements`/superclasses/base clauses/trait impls, imports, `require`, decorators and annotations |
| SQL (the chunker's statement lexer) | `CREATE [OR REPLACE / OR ALTER] TABLE / VIEW / PROCEDURE / FUNCTION`, schema as container | tables after `FROM` (with its comma list), `JOIN`, `UPDATE`, `INTO`, `INSERT`, `DELETE`, `MERGE`, `USING`, `REFERENCES`, `ALTER TABLE`; procedures after `EXEC` |
| HTML, CSS | HTML `id` attributes (`#top`), CSS class selectors (`.card`) | HTML `class` names (`.card`), CSS id selectors (`#top`) |

The tree-sitter languages are data: `TreeSitterSymbolGrammar` lists, per grammar, the definition node types (with
kind, a required field, a kind-refining field, a container field), container-only nodes (Rust `impl`), declarations
and declarators, reference nodes (with the field that holds the target), type identifiers and qualified types,
inheritance, import and type regions, skipped regions (type parameter declarations), module strings and the file
module naming; `TreeSitterSymbolExtractor` walks any grammar described that way. Extraction never throws: a parse
that fails badly (the chunkers' thresholds), a grammar that cannot load, or an unexpected error gives an empty
extraction with `IsComplete = false` and a call to `SymbolExtractionOptions.OnFailure`.

### Resolution

`SymbolGraphBuilder` holds the latest extraction per file (`AddFile` replaces, `RemoveFile` removes) and resolves every
reference by name, independent of the order files were added in:

1. Candidates are definitions with exactly the name, except that a Visual Basic reference (and its qualifier and
   scope) matches any case, as the language does (constructors are never candidates; a construction resolves to
   its type). A dotted or `::` name with no match is retried as its last segment, qualified by the one before.
2. Candidates whose kind cannot be meant are dropped (`SymbolKinds.IsCompatible`; constructions need a type).
3. With a qualifier, candidates whose container ends with it (generic arguments ignored, last dotted part accepted) or
   whose file stem is it are preferred; without one, candidates declared in the innermost container around the
   reference (its from-container, then each enclosing container) are preferred.
4. A bare identifier in C#, VB or Razor (an unqualified `Use`) resolves only within its file.
5. Then the same file is preferred, then the same partition (`SymbolGraphOptions.PartitionOf`, by default the first
   folder of the path), then anywhere.
6. One candidate gives one edge; up to `MaxCandidates` (5) give an edge each, marked ambiguous with the candidate
   count; more give none. `SymbolGraph.Statistics` counts resolved, ambiguous, too ambiguous and unresolved
   references.

The edge's source is the innermost definition whose lines contain the reference (or the one its from-container names,
when several members share a line); file-level code has none. Query helpers on `SymbolGraph`: `FindDefinitions(name)`
(exact, or `Type.Member` / `ns::Type` qualified), `FindReferences(id)`, `ReferencesFrom(id)`, `CallersOf(id)` (distinct
definitions with a `Call` edge to it), `ParentOf(id)`, `DefinitionsIn(path)`, `GetDefinition(id)`.

### Ranking and the repo map

`SymbolRanker` runs a weighted PageRank (damping 0.85, at most 50 iterations, converged when the ranks move less than
1e-6 in total) over the symbol graph (a node per definition and per file for file-level code; edges weighted 1 for
calls, type uses and inheritance and 0.5 for imports and uses, split between a reference's candidates) and over the
file graph (cross-file edges), then multiplies each score by `1 + ln(1 + n)` where `n` is the number of other files
that refer to the definition or file. `SymbolGraph.Ranking` caches the default ranking.

`RepoMapRenderer` takes symbols in order (every symbol of the focus files first, then the rest by score; ties broken
by path, line, name and id), keeps the longest prefix whose rendering fits `MaxTokens` in the injected counter, and
renders each file (focus files first, then by file rank) as its path followed by its symbols in source order,
indented by nesting, with their enclosing definitions for context:

```
src/Orders/OrderService.cs
  namespace Orders
    class OrderService : IOrderService
      method Place(Order order)
```

Modules and enum members are left out by default; lines are cut at 160 characters. The output is deterministic.

### Persistence (index schema version 3)

- `symbols.id` takes `SymbolDefinition.Id` (`NewSymbol.Id`), so re-indexing a file upserts its symbols in place and
  references from other files to a symbol that is still there stay valid; `symbols.kind` and `symbol_refs.kind` take
  `SymbolKinds.Name(...)`.
- `symbol_refs.from_symbol_id` is null for file-level code; `symbol_refs.ambiguous` and `symbol_refs.candidates` take
  `SymbolEdge.IsAmbiguous` and `SymbolEdge.CandidateCount`.
- After rebuilding symbols, the host writes `ContextEngineVersions.SymbolsVersion` with
  `ISymbolRepository.SetSymbolsVersionAsync`.
### Measured

On the development machine (Release build, single thread): a 100,000-line synthetic C# file extracts in about 0.3-0.6 s
(13,400 definitions, 31,000 references); a 100-file, 101,000-line synthetic repository extracts in about 1 s, and then
builds its graph in about 0.18 s, ranks in about 0.05 s and renders a 1,024-token map in about 5 ms (about 0.23 s
after extraction). `SymbolPerformanceTests` logs the figures on every run.
