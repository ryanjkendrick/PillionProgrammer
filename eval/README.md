# Evaluation golden sets

The AI evaluation harness of design section 15 scores Pillion's model-facing behaviour against golden sets. The harness
runner is `tools/eval/Pillion.Eval` (a console tool, never shipped); this folder holds the golden sets whose every sample
comes from code that is already public in this repository under MIT. This is the retrieval slice (work item 2.16).

## The rule about sources

Only code from this repository goes into a golden set here: its own source (`src/`) and its test fixtures. Nothing from
any other project may be added, whatever its licence, and nothing from a non-permissive source may be added anywhere.
Sets built from externally sourced, permissively licensed code (the design's three fixture repositories in C#,
TypeScript and Python, and the completion set) belong to the private golden-set repository once issue #50 decides where
it lives; the harness reads a set folder from any path, so those sets need no change to the runner.

## Retrieval sets

| Set | Corpus | Queries | Purpose |
| --- | --- | ---: | --- |
| `retrieval/pillion-src` | this repository's `src` folder (about 600 files, 86,000 lines of C#) | 60 | The main set: questions a developer asks of this code base |
| `retrieval/fixtures` | the Context Engine chunker fixtures (every language) and the indexing pipeline's `shop` fixture, 24 files | 40 | A sanity set: queries over the C#, TypeScript and Python files; the corpus is tiny, so file recall is easy and the symbol figures and MRR carry the signal. The smoke test runs it |

The `pillion-src` queries are a mix of four kinds, 14 to 16 each: `where` ("where are old log files deleted"), `which`
("which class shuts the host down when Visual Studio exits"), `symbol` (questions that name a symbol, "HandshakeValidator
order of checks") and `concept` ("how does the host avoid flooding the extension with settings notifications"). Each
expected file and symbol was checked against the code when the query was written.

### Format

A set is a folder with two files.

`set.json` names the corpus: folders copied into a fresh corpus root before indexing. `path` is relative to the set
folder; `as` is the folder inside the corpus root. `bin`, `obj`, `.vs`, `.git`, `node_modules` and `TestResults` are
always left out; `exclude` adds folder names.

```json
{
  "name": "pillion-src",
  "description": "What the set covers.",
  "corpus": [ { "path": "../../../src", "as": "src" } ],
  "exclude": []
}
```

`queries.jsonl` holds one query per line:

```json
{"id":"host-07","query":"which class shuts the host down when the Visual Studio process that launched it goes away","expectedFiles":["src/Pillion.Host/Lifecycle/ParentProcessWatcher.cs"],"expectedSymbols":["ParentProcessWatcher"],"kind":"which","notes":"Why these are the answer."}
```

- `id` (required, unique in the set), `query` (required).
- `expectedFiles` (required, at least one): corpus-relative paths with forward slashes and the exact case on disk. A
  query scores a file hit at rank n when the n-th result is in any of them.
- `expectedSymbols` (optional): `Type` or `Type.Member` as declared in the code, no namespace, parameters or generic
  arguments (`Outer.Inner` for a nested type). A result is a symbol hit when its chunk breadcrumb
  (`path > namespace > type > member`) holds the dotted name as consecutive names; `Greeter` matches every member chunk of
  the type and `Greeter.Greet` only that member. Names compare case-sensitively. The symbol is matched in any file, so
  prefer a qualified name where a short one is common.
- `kind` (optional): `where`, `which`, `symbol` or `concept`, for the breakdown in the report. `notes`: free text.

Unknown fields are refused, so a misspelt field name fails loudly.

### Adding a set or a query

1. Write the queries by reading the code: paraphrase non-symbol questions the way a user who does not know the names
   would ask them, and pick the granularity where the answer lives (a member for "where", a type for "which").
2. `dotnet run --project tools/eval/Pillion.Eval -c Release -- validate --set eval/retrieval/<set>` checks that every
   line parses and every expected file is in the corpus.
3. Run the set with the bundled model (below). The report lists any expected symbol that no chunk of the expected files
   holds in its breadcrumb (such a symbol can never score) and every file miss with what came back instead.
4. A new set under `eval/` gets a line in `tests/Pillion.Eval.Tests/GoldenSetTests.cs`, which checks on every build that
   the checked-in sets still parse and point at files that exist: moving or renaming a file a query expects fails that
   test, and the query is updated in the same pull request.

## Running the harness

```powershell
dotnet build tools/eval/Pillion.Eval -c Release
dotnet tools/eval/Pillion.Eval/bin/Release/net10.0/Pillion.Eval.dll retrieval `
    --set eval/retrieval/pillion-src --set eval/retrieval/fixtures `
    --model bundled/bge-small-en-v1.5 --model ollama/qwen3-embedding:0.6b
```

For each set and model the harness copies the corpus to a temporary folder, composes the host's services in process
(Model Gateway, indexing pipeline, retrieval service; Local-only on; the Embedding task class pinned to the model through
`routing.profiles`), indexes the corpus into a temporary encrypted index through the real pipeline, and runs every query
through `IRetrievalService` in the Hybrid, Lexical and Vector modes with `MaxResults` 10 and the retriever's defaults
(at most 3 results per file). It reports:

- recall at 10 by file and by symbol (the share of queries with a hit in the top 10), recall at 1 and 5 by file, and the
  mean reciprocal rank (cut at 10) by file and by symbol, overall and per query kind;
- p50 and p95 search latency per mode, end to end through the retrieval service with the query embedding included, after
  one unmeasured warm-up search. Each mode has its own retrieval service, so no mode reuses another's cached query
  vectors. The time budget per search is 60 s by default (`--budget-ms`), so quality is measured without legs being cut
  short; the latency figures show what the product's 300 ms budget would face;
- the int8 loss: while indexing, the gateway's `IncludeFloat32` path keeps the unit-normalised float32 vector of every
  chunk beside its int8 vector (the same call; the pipeline stores the int8 one as always). The vector leg is then scored
  both ways, top 10 chunks by exact cosine with no per-file cap or expansion, and the recall at 10 difference is
  reported in points with the top-10 overlap;
- the model's id and hash: the SHA-256 of the bundled model's graph (verified against `models.manifest.json` when the
  runtime locates it) or the digest Ollama reports in `/api/tags`; the machine; the index size and time.

Output goes to `tools/eval/out/<timestamp>/` (git-ignored): `summary.md`, and a `.json` (every figure and every query's
ranks and top 10) and `.md` (breakdown, warnings, misses) per set and model. Bundled models other than the build's own
are downloaded once from the manifest's pinned URLs into `%LOCALAPPDATA%\PillionProgrammer-eval\models\` (`--models-dir`,
`--no-download`), apart from the product's models folder. An Ollama embedding model is unloaded after the run unless it
was resident before; other resident models are left alone. A long run (a code model on the CPU indexes `src` in about
15 minutes) can be made resumable with `--work-dir <folder>`: each set and model's corpus and index are kept there, and
running the same command again picks up where a stopped run left off (chunks embedded by the earlier process are embedded
again for the int8 measurement). `--help` lists every option.

The figures of the first baseline are in [docs/eval-retrieval-baseline.md](../docs/eval-retrieval-baseline.md).
