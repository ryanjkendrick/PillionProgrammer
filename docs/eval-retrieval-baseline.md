# Retrieval evaluation baseline

This is the first baseline from the retrieval evaluation slice (work item 2.16, design section 15). It gives the product
owner the figures for the open decision in the phase 2 plan, the **default embedding model**. This document does not
change the default; the catalog's `embeddingProviderOrder` still puts the bundled `bge-small-en-v1.5` first.

## Where the golden sets live

The product owner's lead decided this for this item. The harness runner (`tools/eval/Pillion.Eval`) and the first
retrieval golden sets live in this repository under `eval/`. Every sample in them comes from code that is already public
here under MIT: this repository's own `src` folder and its test fixtures. Externally sourced samples, such as the
design's three C#, TypeScript and Python fixture repositories and the completion set, will move to the private
golden-set repository once issue #50 is answered. Nothing from a non-permissive source may be added. See
[eval/README.md](../eval/README.md) for the format and the rules.

## Run

- **Date:** 2026-10-15, harness at the head of `feature/eval-retrieval`.
- **Machine:** RYAN-DESKTOP, the development machine and the reference machine's class.
  - CPU: AMD Ryzen 7 3700X, 8 cores and 16 logical processors.
  - Memory: 32 GB.
  - GPU: NVIDIA GeForce RTX 2080 SUPER, 8 GB.
  - Software: Windows 11 (10.0.26300), .NET 10.0.13, Ollama on `localhost:11434`.
- **Pipeline:** the host's real indexing pipeline and retrieval service, composed in process with Local-only on.
  - Each set and model gets a fresh encrypted index.
  - Search uses `MaxResults` 10 and at most 3 results per file (the retriever's default), with a 60 s budget per search.
  - The bundled models run on the CPU (ONNX Runtime 1.24.4, int8 graphs).
  - `qwen3-embedding:0.6b` ran on the GPU as the residency rules placed it. Nothing else was resident in Ollama, so the
    "beside the resident 7B" case was not exercised. A placement check after indexing reported `num_ctx` 2,048 and
    2,053 of 2,053 MiB in GPU memory.
- **Sets:**
  - `pillion-src`: 60 queries over `src`, which is 605 files, 601 indexed and 107,695 lines.
  - `fixtures`: 40 queries over 24 fixture files.

The two sets are not the Gate 2 evidence set. Gate 2 asks for recall at 10 on three fixture repositories in C#,
TypeScript and Python, which wait on issue #50. They are the best evidence available now. Each `pillion-src` query is
1.7 points of recall, so differences of one or two queries are within noise.

## Results: `pillion-src` (60 queries)

Recall@10 file is the share of queries with a result from an expected file in the top 10. This is the gate metric.

- **Recall@10 symbol:** the share of queries with a top-10 result whose breadcrumb holds an expected symbol.
- **MRR:** the mean reciprocal rank, cut at 10.
- **Latency:** end to end through the retrieval service, query embedding included, after a warm-up search.

| Model | Mode | Recall@10 file | MRR file | Recall@1 / @5 file | Recall@10 symbol | MRR symbol | p50 ms | p95 ms |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| bundled/bge-small-en-v1.5 | hybrid | **0.967** | **0.831** | 0.77 / 0.92 | 0.750 | 0.428 | 12 | 16 |
| | lexical | 0.933 | 0.798 | 0.72 / 0.90 | 0.683 | 0.382 | 7 | 11 |
| | vector | 0.950 | 0.753 | 0.62 / 0.93 | 0.783 | 0.466 | 11 | 14 |
| ollama/qwen3-embedding:0.6b | hybrid | **1.000** | **0.814** | 0.75 / 0.92 | 0.700 | 0.367 | 28 | 33 |
| | lexical | 0.933 | 0.791 | 0.70 / 0.90 | 0.683 | 0.372 | 6 | 11 |
| | vector | 0.950 | 0.755 | 0.65 / 0.90 | 0.633 | 0.380 | 29 | 34 |
| bundled/jina-embeddings-v2-base-code | hybrid | **0.967** | **0.826** | 0.75 / 0.92 | 0.783 | 0.489 | 23 | 31 |
| | lexical | 0.933 | 0.797 | 0.72 / 0.90 | 0.650 | 0.374 | 6 | 11 |
| | vector | 0.933 | 0.774 | 0.67 / 0.93 | 0.817 | 0.537 | 17 | 25 |
| bundled/multilingual-e5-small | hybrid | **0.950** | **0.764** | 0.65 / 0.93 | 0.767 | 0.417 | 11 | 14 |
| | lexical | 0.933 | 0.793 | 0.70 / 0.92 | 0.683 | 0.381 | 6 | 10 |
| | vector | 0.933 | 0.662 | 0.52 / 0.87 | 0.750 | 0.431 | 11 | 13 |

The lexical rows differ slightly between models because each model chunks with its own token counts.

- The bundled models count with their own tokenizers.
- `qwen3-embedding` counts with the deterministic WordPiece approximation, which gives 10,479 chunks against 10,132 for
  bge-small.

No response was degraded under the 60 s budget. No golden-set warnings remain: every expected file is indexed and every
expected symbol is held by a chunk breadcrumb of its file.

### By query kind, hybrid mode

Each cell is recall@10 file / MRR file / recall@10 symbol.

| Model | concept (16) | symbol (14) | where (16) | which (14) |
| --- | --- | --- | --- | --- |
| bge-small-en-v1.5 | 0.88 / 0.69 / 0.56 | 1.00 / 0.96 / 0.86 | 1.00 / 0.94 / 0.69 | 1.00 / 0.74 / 0.93 |
| qwen3-embedding:0.6b | 1.00 / 0.63 / 0.56 | 1.00 / 1.00 / 0.71 | 1.00 / 0.95 / 0.69 | 1.00 / 0.69 / 0.86 |

The per-kind figures for jina and e5 are in their JSON and Markdown files.

## Results: `fixtures` (40 queries, 24 files)

| Model | Mode | Recall@10 file | MRR file | Recall@10 symbol | MRR symbol | p50 ms | p95 ms |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: |
| bundled/bge-small-en-v1.5 | hybrid / lexical / vector | 1.000 / 1.000 / 1.000 | 0.833 / 0.895 / 0.938 | 0.921 / 0.947 / 1.000 | 0.761 / 0.816 / 0.904 | 8 / 1 / 8 | 10 / 1 / 10 |
| ollama/qwen3-embedding:0.6b | hybrid / lexical / vector | 1.000 / 1.000 / 1.000 | 0.892 / 0.896 / 0.956 | 0.895 / 0.947 / 1.000 | 0.727 / 0.816 / 0.920 | 25 / 1 / 23 | 38 / 1 / 35 |
| bundled/jina-embeddings-v2-base-code | hybrid / lexical / vector | 1.000 / 1.000 / 1.000 | 0.873 / 0.896 / 0.904 | 0.921 / 0.947 / 1.000 | 0.773 / 0.816 / 0.886 | 21 / 1 / 21 | 29 / 2 / 26 |
| bundled/multilingual-e5-small | hybrid / lexical / vector | 1.000 / 1.000 / 1.000 | 0.877 / 0.896 / 0.900 | 0.895 / 0.947 / 1.000 | 0.743 / 0.816 / 0.898 | 9 / 1 / 8 | 11 / 1 / 10 |

With 24 files and at most 3 results per file, every top 10 holds most of the corpus, so recall by file is 1.0 for every
model. This set is a sanity check for the harness; the smoke test runs it. Only MRR and the symbol figures say anything.

## Int8 loss against float32

The vector leg is scored alone: the top 10 chunks by exact cosine, with no per-file cap or expansion. The float32 and
int8 vectors come from the same gateway calls (`IncludeFloat32`), and were captured while the pipeline embedded the
corpus.

| Set | Model | Chunks | Recall@10 file float32 / int8 | Loss | Recall@10 symbol float32 / int8 | Loss | Top-10 overlap |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: |
| pillion-src | bge-small-en-v1.5 | 10,132 | 0.933 / 0.933 | 0.0 points | 0.767 / 0.767 | 0.0 points | 0.982 |
| pillion-src | qwen3-embedding:0.6b | 10,479 | 0.950 / 0.950 | 0.0 | 0.717 / 0.717 | 0.0 | 0.997 |
| pillion-src | jina-embeddings-v2-base-code | 9,953 | 0.917 / 0.917 | 0.0 | 0.817 / 0.817 | 0.0 | 0.997 |
| pillion-src | multilingual-e5-small | 10,240 | 0.933 / 0.933 | 0.0 | 0.767 / 0.767 | 0.0 | 0.987 |
| fixtures | all four | 132 | 1.000 / 1.000 | 0.0 | 1.000 / 1.000 | 0.0 | 0.990 to 1.000 |

The file MRR moved by at most 0.008 on `pillion-src`, in both directions:

| Model | File MRR float32 / int8 |
| --- | ---: |
| bge-small | 0.748 / 0.748 |
| qwen3-embedding | 0.759 / 0.751 |
| jina | 0.743 / 0.751 |
| e5 | 0.713 / 0.709 |

The int8 index loses nothing measurable at 10 on these sets, well within the design's 2 points.

## Models and indexing cost

| Model | Pinned by | Dimensions, tokenizer, chunk cap | Chunks | Index time (embedding) |
| --- | --- | --- | ---: | ---: |
| bundled/bge-small-en-v1.5 (MIT, bundled) | sha256 of `model_quantized.onnx` `6c9c6101a956d62dfb5e7190c538226c0c5bb9cb27b651234b6df063ee7dbfe4` (manifest commit ea104dac) | 384, bert-wordpiece, 512 | 10,132 | 259 s (247 s), CPU |
| ollama/qwen3-embedding:0.6b (Apache-2.0) | Ollama digest `ac6da0dfba84a81fdbfbaf330198c33cd77c4cdfc53e8bc50eb581914a15621d` (595.78M parameters, Q8_0, 639 MB) | 1024, qwen3-bpe (chunks counted with the WordPiece approximation), 512 | 10,479 | 356 s (332 s), GPU |
| bundled/jina-embeddings-v2-base-code (Apache-2.0, on request, 157 MB download) | sha256 of `model_quantized.onnx` `ed45870251c9f0cf656e78aab0d37a23489066df8a222bb1c8caf8a45f2cb16d` (manifest commit 516f4baf) | 768, roberta-bpe, 512 | 9,953 | 852 s (831 s), CPU |
| bundled/multilingual-e5-small (MIT, on request, 118 MB download) | sha256 of `model_qint8_avx512_vnni.onnx` `dd476dd0c2514e9b9be83aeb3853fac0763e0bdf4a71645407587d77c48a2d88` (manifest commit 614241f6) | 384, xlmr-sentencepiece, 512 | 10,240 | 328 s (316 s), CPU |

The chunk cap is 512 tokens for every model: the pipeline caps chunks at its own maximum, whatever the model's window.
The index time is wall time from setting the workspace to an idle, swept index of 107,695 lines. bge-small indexes at
about 25,000 lines a minute on the CPU, which is under the Gate 2 target of 100,000 lines in 3 minutes. That gate row
belongs to the performance suite (2.17); this is an observation.

`nomic-embed-text` was not run because it is not pulled on this machine.

## What the figures say for the default-model decision

These are facts for the product owner, not a recommendation from this item.

- **Hybrid recall at 10 by file:** every model clears the proposed gate of 0.85 on `pillion-src`. qwen3-embedding 1.000
  (60 of 60), bge-small and jina 0.967 (58), e5 0.950 (57). The gap is one or two queries.
- **Hybrid MRR by file:** bge-small 0.831 and jina 0.826 lead, then qwen3-embedding 0.814 and e5 0.764.
- **Symbol recall:** the code model jina leads in both hybrid (0.783) and vector (0.817). In the vector leg alone,
  qwen3-embedding is the weakest by symbol (0.633).
- **Cost:**
  - bge-small needs no download and no Ollama. It is the cheapest to index on the CPU and the fastest to query, at a
    hybrid p50 of 12 ms.
  - jina takes 3.3 times as long to index on the CPU and is a 157 MB download.
  - qwen3-embedding needs Ollama, about 2 GB of GPU memory at `num_ctx` 2,048, and a hybrid p50 of 28 ms with the model
    on the GPU.
- **Int8:** quantisation costs nothing measurable for any of the four models.
- **Not yet known:** the design's 0.90 target for the code model cannot be judged until the code model is run on the
  external fixture repositories (issue #50).

## Notes

- **Not indexed: `ContentInspector.cs`.** `src/Pillion.Indexing/Discovery/ContentInspector.cs` is excluded by discovery as
  generated code. Its first 2 KB hold the `<auto-generated` marker text it searches for. A golden query that expected
  the file was replaced, and the harness now warns when an expected file is in the corpus but not in the index. This is
  a small false positive in discovery, not fixed here.
- **First attempt: process killed.** The first `pillion-src` run with bge-small stopped silently at about 500 of 601
  files. It was the tool's background mechanism killing the process, not a fault in the provider. The same run completed
  when started directly. The figures above come from later runs.

## Truncated embeddings, work item 3.4

Issue #90 asked whether embedding only the first part of a long chunk, with the full text still stored, would buy back
indexing time on the CPU tier without costing retrieval. The product owner's decision of 2026-10-24 made it a phase 3
side item with this harness as its judge. This section gives the figures; **the default is not changed**:
`indexing.embeddingTokenCap` ships at 0 (off).

### What the option does

- `indexing.embeddingTokenCap` (settings file only, 0 by default) caps the text handed to the embedding model at N
  tokens of the chunk's own tokenizer count. The chunk's stored text, its lexical (FTS) entry and its `token_count` stay
  complete; only the vector is made from less.
- **The breadcrumb counts inside the cap and comes first.** It carries the file, namespace, type and member names, which
  is what the symbol and "where" queries match on, so it is never cut; the leading text gets what remains, cut at a
  white-space boundary. A cap the breadcrumb alone fills embeds the breadcrumb and nothing else. A cap of zero or less is
  off; a cap at or above the chunk cap (512) is off too, since nothing would be cut; a small positive cap is raised to 16.
- **The cap is part of what identifies the vectors.** The cap in force is written to the index `meta`
  (`embedding_token_cap`) before the first vector of a run; a full run over an index whose recorded cap differs from the
  setting wipes the index and embeds everything again, as a model change does, and a settings change to another cap
  queues that run. An index built before the option has no row and counts as 0, so existing indexes are left alone.
- The harness takes `--embedding-token-cap <n>`, records the cap and the number of chunks cut, and scores the int8 loss
  on the vectors of the capped inputs. It now also reports the embedding time, chunks per second and the index size.

### Runs

- **Date:** 2026-10-29. **Machine:** RYAN-DESKTOP as above (.NET 10.0.14, ONNX Runtime on the CPU). Another agent's
  build ran during the first baseline run, which is the likely reason it is 48 s slower than its repeat; the two 256 runs
  agree to 0.3 s.
- **Set:** `pillion-src` at this branch's `src`: 619 files, 615 indexed, 113,412 lines, 10,560 chunks. The baseline here
  is run on the same day with the same harness, not the 2026-10-15 figures above (the corpus has grown since).
- **Model:** bundled `bge-small-en-v1.5` int8 on the CPU, hybrid, lexical and vector modes, `MaxResults` 10, 60 s budget.
- The baseline and the 256 run were each run twice to show the run-to-run noise. The repeats embed identical inputs; the
  small differences between them (one symbol query, 0.001 to 0.015 of MRR) come from the batch composition the int8
  model sees, since the pipeline sorts chunks by length within a window and batches are padded to their longest text.

| Cap | Chunks cut (of 10,560) | Index time | Embedding time | Chunks/s | Hybrid recall@1 / @5 / @10 file | Hybrid MRR file | Hybrid recall@10 / MRR symbol | Vector recall@10 / MRR file | Vector recall@10 symbol | Hybrid p50 / p95 ms | Database bytes a chunk |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| off | 0 | 280.1 s | 266.3 s | 37.7 | 0.77 / 0.90 / **0.967** | **0.825** | 0.717 / 0.395 | 0.950 / 0.752 | 0.783 | 13 / 17 | 2,179 |
| off (repeat) | 0 | 232.0 s | 219.9 s | 45.5 | 0.77 / 0.90 / **0.967** | **0.826** | 0.733 / 0.399 | 0.950 / 0.755 | 0.783 | 12 / 16 | 2,266 |
| 256 | 2,211 (21%) | 177.2 s | 166.5 s | 59.6 | 0.70 / 0.92 / **0.967** | **0.788** | 0.783 / 0.417 | 0.933 / 0.744 | 0.733 | 13 / 16 | 2,171 |
| 256 (repeat) | 2,211 (21%) | 176.9 s | 166.0 s | 59.7 | 0.72 / 0.90 / **0.967** | **0.803** | 0.783 / 0.404 | 0.950 / 0.751 | 0.750 | 13 / 17 | 2,174 |
| 128 | 4,914 (47%) | 135.7 s | 124.7 s | 77.8 | 0.67 / 0.90 / **0.983** | **0.763** | 0.750 / 0.370 | 0.917 / 0.673 | 0.717 | 12 / 17 | 2,167 |

The lexical rows are identical in every run (recall@10 file 0.933, MRR 0.789, recall@10 symbol 0.683), as they must be:
the cap never touches the lexical index. The int8 loss stayed at 0.0 points of file recall for the baseline and 256 runs
(top-10 overlap 0.982 to 0.988); at 128 the int8 vectors scored 1.7 points above float32 by file and 1.7 below by
symbol, which is one query each way.

By query kind, hybrid mode (recall@10 file / MRR file / recall@10 symbol):

| Cap | concept (16) | symbol (14) | where (16) | which (14) |
| --- | --- | --- | --- | --- |
| off | 0.88 / 0.68 / 0.50 | 1.00 / 0.96 / 0.79 | 1.00 / 0.94 / 0.69 | 1.00 / 0.72 / 0.93 |
| off (repeat) | 0.88 / 0.68 / 0.56 | 1.00 / 0.96 / 0.86 | 1.00 / 0.94 / 0.69 | 1.00 / 0.72 / 0.86 |
| 256 | 0.88 / 0.64 / 0.50 | 1.00 / 0.96 / 0.86 | 1.00 / 0.89 / 0.88 | 1.00 / 0.66 / 0.93 |
| 256 (repeat) | 0.88 / 0.66 / 0.50 | 1.00 / 0.96 / 0.86 | 1.00 / 0.91 / 0.88 | 1.00 / 0.69 / 0.93 |
| 128 | 0.94 / 0.58 / 0.56 | 1.00 / 0.96 / 0.79 | 1.00 / 0.91 / 0.81 | 1.00 / 0.60 / 0.86 |

The two file misses in hybrid mode are the same in every run but one (`gw-17`, a concept query about the autocomplete
suggestion cache, and `core-12`, about stop words in the full-text match expression; at 128 `gw-17` happened to land in
the top 10). The cap does not change which files are found; it changes their rank.

### What the figures say

- **Recall at 10 by file, the gate metric, is unchanged at 256** (0.967, 58 of 60, in all four runs) and within one query
  at 128. Every expected file is still found, because the lexical leg sees the whole text and the fusion keeps it.
- **The cost of 256 is rank, not recall.** Hybrid MRR by file falls from 0.825 / 0.826 to 0.788 / 0.803 (0.02 to 0.04, against
  0.001 between the baseline repeats and 0.015 between the 256 repeats), and recall at 1 from 0.77 to 0.70 / 0.72: three or
  four of the 60 queries lose their first-place hit. The loss sits in the `concept` and `which` kinds, whose answers are
  often long members and type headers (21% of this corpus's chunks are over 256 tokens) whose tails the vector leg no
  longer sees. In the vector leg alone, recall at 10 moved by at most one query and MRR by 0.001 to 0.011, so the fused
  rank is where the difference shows.
- **Symbol recall improves at 256** in hybrid mode (0.717 / 0.733 to 0.783, the `where` kind from 0.69 to 0.88): with the
  breadcrumb first and less text behind it, a long member's vector carries more of its own name. The vector-leg symbol
  recall falls (0.783 to 0.733 / 0.750), so the gain comes through the fusion.
- **128 is too far.** Hybrid MRR falls to 0.763 (0.06), recall at 1 to 0.67, and the vector leg loses a tenth of its MRR
  (0.752 to 0.673) and 3 points of file recall.
- **Time: 256 saves a quarter to a third of the indexing time on real code**: 177 s against 232 to 280 s (embedding
  166 s against 220 to 266 s), 59.7 chunks a second against 37.7 to 45.5. Per 100,000 lines of this code that is 1.6
  minutes against 2.0 to 2.5 here, well inside both the restated CPU-tier target of 4 minutes and the original 3. 128
  saves about half (136 s).
- **Index size does not change.** The vector has the same 384 int8 dimensions whatever the model saw, and the text is
  stored whole, so the database holds 2,167 to 2,266 bytes a chunk in every run; the spread is SQLite page layout, not
  the cap. The issue's 2.3 KB figure is untouched by this experiment.
- **Query latency does not change** (hybrid p50 12 to 13 ms, p95 16 to 17 ms): the query is embedded whole and the
  vector search is the same size.

### Recommendation

**Keep the option off by default.** The 256 cap is a real indexing-time saving on the CPU tier (about 30%), but it
pays for it with first-rank quality on exactly the queries the design cares about for chat context: three or four of 60
answers slip from rank 1, and hybrid MRR falls by 0.02 to 0.04, outside the run-to-run noise. The metric the cap leaves
intact, recall at 10, is the gate metric, but the product shows a handful of chunks and the first is the one that
matters most. The pressure that motivated the experiment has also eased: the product owner restated the CPU-tier targets
on 2026-10-24 to figures the uncapped pipeline meets, the index size, the other half of issue #90, is not affected by the
cap at all, and the DirectML decision (issue #78) addresses the GPU tier without any quality cost. 128 should not be
offered as a default under any tier.

What the option is good for: an opt-in for very large repositories on the CPU tier (at 1,000,000 lines the saving is
of the order of 10 minutes), and a documented, measured knob should the targets be tightened again. It is settable in
`settings.json` today; it has no options-page control, which is right for an experiment. If the product owner prefers
the time over the rank, 256 is the value to take; the index rebuilds itself when the setting changes.

### Commands

```powershell
dotnet build PillionProgrammer.NoVsix.slnf -c Release
# baseline (run twice)
dotnet tools\eval\Pillion.Eval\bin\Release\net10.0\Pillion.Eval.dll retrieval --set eval\retrieval\pillion-src --model bundled/bge-small-en-v1.5
# cap 256 (run twice) and cap 128
dotnet tools\eval\Pillion.Eval\bin\Release\net10.0\Pillion.Eval.dll retrieval --set eval\retrieval\pillion-src --model bundled/bge-small-en-v1.5 --embedding-token-cap 256
dotnet tools\eval\Pillion.Eval\bin\Release\net10.0\Pillion.Eval.dll retrieval --set eval\retrieval\pillion-src --model bundled/bge-small-en-v1.5 --embedding-token-cap 128
```

Each run takes 3 to 6 minutes on the reference machine and leaves `summary.md`, `pillion-src__bundled_bge-small-en-v1.5[__cap<n>].json`
and `.md` under `tools/eval/out/<timestamp>/`. The runs above are `20261029-081323` and `20261029-082400` (baseline),
`20261029-081820` and `20261029-082817` (256) and `20261029-082131` (128) on the development machine.

## Raw results

Results are under `tools/eval/out/` on the development machine (git-ignored). Each run folder has `summary.md`, and a
`.json` and `.md` per set and model.

| Model | Run folder |
| --- | --- |
| bge-small-en-v1.5 (both sets) | `tools/eval/out/20261015-122402/` |
| qwen3-embedding:0.6b (both sets) | `tools/eval/out/20261015-122845/`; the placement check is `20261015-125056/` (fixtures, vector mode only) |
| jina-embeddings-v2-base-code | `tools/eval/out/20261015-123528/` (fixtures, with the download), `20261015-123609/` (pillion-src) |
| multilingual-e5-small (both sets) | `tools/eval/out/20261015-125115/` |

To reproduce: `dotnet tools/eval/Pillion.Eval/bin/Release/net10.0/Pillion.Eval.dll retrieval --set eval/retrieval/pillion-src
--set eval/retrieval/fixtures --model <spec>` (see [eval/README.md](../eval/README.md)).
