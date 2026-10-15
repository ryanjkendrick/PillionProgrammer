# Pillion.ContextEngine

The Context Engine library (design section 7). Today it holds the **chunkers** (phase 2, work item 2.4); symbol
extraction, storage and retrieval join it in later phase 2 items. It is a .NET 10 class library with no Visual Studio
dependency. Nothing references it yet except `tests/Pillion.ContextEngine.Tests`; the host wires it into the indexing
pipeline in work item 2.9, where it ships behind the `features.indexing` flag. Until then it has no flag of its own,
because nothing calls it.

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
