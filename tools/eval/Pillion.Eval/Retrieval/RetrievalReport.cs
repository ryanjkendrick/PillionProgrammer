using System.Globalization;
using System.Text;

namespace Pillion.Eval.Retrieval;

/// <summary>Renders retrieval results as Markdown.</summary>
internal static class RetrievalReport
{
    /// <summary>Renders the summary of a run: one row per set, model and mode, then the int8 loss.</summary>
    /// <param name="results">The results.</param>
    /// <param name="started">When the run started.</param>
    /// <returns>The Markdown.</returns>
    public static string Summary(IReadOnlyList<SetModelResult> results, DateTimeOffset started)
    {
        ArgumentNullException.ThrowIfNull(results);
        var b = new StringBuilder();
        b.AppendLine("# Retrieval evaluation");
        b.AppendLine();
        Line(b, $"Run started {started:yyyy-MM-dd HH:mm} UTC.");
        if (results.Count > 0)
        {
            var m = results[0].Machine;
            Line(b, $"Machine: {m.Name}, {m.Cpu} ({m.LogicalProcessors} logical processors), {m.MemoryGb:0.#} GB, GPU {m.Gpu}, {m.Os}, {m.Runtime}.");
            Line(b, $"Search: top {results[0].Search.K}, at most {results[0].Search.MaxPerFile} results per file, time budget {results[0].Search.BudgetMs:0} ms per search.");
        }

        b.AppendLine();
        b.AppendLine("| Set | Model | Mode | Queries | Recall@10 file | MRR file | Recall@1 / @5 file | Recall@10 symbol | MRR symbol | p50 ms | p95 ms | Degraded |");
        b.AppendLine("| --- | --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |");
        foreach (var r in results)
        {
            foreach (var mode in r.Modes)
            {
                Line(b, $"| {r.Set} | {r.Model} | {mode.Mode} | {mode.Queries} | {mode.FileRecallAt10:0.000} | {mode.FileMrr:0.000} | {mode.FileRecallAt1:0.00} / {mode.FileRecallAt5:0.00} | {mode.SymbolRecallAt10:0.000} ({mode.SymbolQueries}) | {mode.SymbolMrr:0.000} | {mode.P50Ms:0} | {mode.P95Ms:0} | {mode.Degraded} |");
            }
        }

        if (results.Any(r => r.Int8Loss is not null))
        {
            b.AppendLine();
            b.AppendLine("Int8 loss (vector leg alone, top 10 chunks by exact cosine, no per-file cap or expansion; float32 and int8 vectors from the same embedding calls):");
            b.AppendLine();
            b.AppendLine("| Set | Model | Chunks | Recall@10 file float32 | int8 | Loss (points) | Recall@10 symbol float32 | int8 | Loss (points) | MRR file float32 / int8 | Top-10 overlap |");
            b.AppendLine("| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |");
            foreach (var r in results.Where(r => r.Int8Loss is not null))
            {
                var l = r.Int8Loss!;
                Line(b, $"| {r.Set} | {r.Model} | {l.Chunks} | {l.Float32.FileRecallAt10:0.000} | {l.Int8.FileRecallAt10:0.000} | {l.FileRecallLossPoints:0.0} | {l.Float32.SymbolRecallAt10:0.000} | {l.Int8.SymbolRecallAt10:0.000} | {l.SymbolRecallLossPoints:0.0} | {l.Float32.FileMrr:0.000} / {l.Int8.FileMrr:0.000} | {l.TopTenOverlap:0.000} |");
            }
        }

        b.AppendLine();
        b.AppendLine("| Set | Model | Pinned by | Embedding cap | Corpus files | Indexed files | Chunks | Cut to the cap | Index time s | Embedding s | Chunks/s | Index MB | Bytes a chunk |");
        b.AppendLine("| --- | --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |");
        foreach (var r in results)
        {
            Line(b, $"| {r.Set} | {r.Model} | {(r.Identity is null ? "?" : r.Identity.HashKind + " `" + r.Identity.Hash + "`")} | {(r.EmbeddingTokenCap > 0 ? r.EmbeddingTokenCap.ToString(CultureInfo.InvariantCulture) : "off")} | {r.Index.CorpusFiles} | {r.Index.IndexedFiles} | {r.Index.Chunks} | {r.Index.TruncatedChunks} | {r.Index.Seconds:0.0} | {r.Index.EmbeddingSeconds:0.0} | {r.Index.ChunksPerSecond:0.0} | {r.Index.DatabaseBytes / (1024.0 * 1024):0.0} | {r.Index.BytesPerChunk:0} |");
        }

        return b.ToString();
    }

    /// <summary>Renders one set and model in detail: the modes, the breakdown by kind, the int8 loss, the warnings and the misses.</summary>
    /// <param name="r">The result.</param>
    /// <returns>The Markdown.</returns>
    public static string Single(SetModelResult r)
    {
        ArgumentNullException.ThrowIfNull(r);
        var b = new StringBuilder(Summary([r], r.StartedUtc));
        if (r.Identity is { } id)
        {
            b.AppendLine();
            Line(b, $"Model: {id.QualifiedId}, {id.HashKind} `{id.Hash}`; source {id.Source}; {id.Details}{(id.License is null ? string.Empty : "; licence " + id.License)}.");
        }

        if (r.EmbeddingModel is not null)
        {
            Line(b, $"Gateway description: {r.EmbeddingModel}.");
        }

        b.AppendLine();
        b.AppendLine("By query kind (recall@10 file / MRR file / recall@10 symbol):");
        b.AppendLine();
        var kinds = r.Modes.SelectMany(m => m.ByKind.Keys).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        b.AppendLine("| Mode | " + string.Join(" | ", kinds.Select(k => string.Create(CultureInfo.InvariantCulture, $"{k} ({r.Modes[0].ByKind.GetValueOrDefault(k)?.Queries ?? 0})"))) + " |");
        b.AppendLine("| --- | " + string.Join(" | ", kinds.Select(_ => "---")) + " |");
        foreach (var mode in r.Modes)
        {
            b.AppendLine($"| {mode.Mode} | " + string.Join(" | ", kinds.Select(k => mode.ByKind.TryGetValue(k, out var s) ? string.Create(CultureInfo.InvariantCulture, $"{s.FileRecallAt10:0.00} / {s.FileMrr:0.00} / {s.SymbolRecallAt10:0.00}") : "-")) + " |");
        }

        if (r.Warnings.Count > 0)
        {
            b.AppendLine();
            b.AppendLine("Warnings:");
            b.AppendLine();
            foreach (var warning in r.Warnings)
            {
                b.AppendLine("- " + warning);
            }
        }

        var hybrid = r.Modes.Count > 0 ? r.Modes[0].Mode : null;
        if (hybrid is not null)
        {
            var misses = r.Queries.Where(q => q.Modes.TryGetValue(hybrid, out var o) && o.FileRank == 0).ToList();
            b.AppendLine();
            Line(b, $"File misses in {hybrid} mode ({misses.Count}):");
            b.AppendLine();
            foreach (var miss in misses)
            {
                var top = miss.Modes[hybrid].Top.Take(3).Select(t => t.File);
                Line(b, $"- {miss.Id} \"{miss.Query}\": expected {string.Join(", ", miss.ExpectedFiles)}; got {string.Join(", ", top)}");
            }
        }

        return b.ToString();
    }

    /// <summary>Renders the short console summary of one result.</summary>
    /// <param name="r">The result.</param>
    /// <returns>The lines.</returns>
    public static string ConsoleLines(SetModelResult r)
    {
        ArgumentNullException.ThrowIfNull(r);
        var b = new StringBuilder();
        foreach (var warning in r.Warnings.Take(10))
        {
            Line(b, $"  warning: {warning}");
        }

        if (r.Warnings.Count > 10)
        {
            Line(b, $"  ... and {r.Warnings.Count - 10} more warnings (see the JSON).");
        }

        Line(b, $"  wrote {r.JsonPath}");
        return b.ToString().TrimEnd();
    }

    private static void Line(StringBuilder builder, FormattableString text) => builder.AppendLine(text.ToString(CultureInfo.InvariantCulture));
}
