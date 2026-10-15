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
