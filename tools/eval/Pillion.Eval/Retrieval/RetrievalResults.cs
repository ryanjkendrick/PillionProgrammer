using Pillion.Eval.Metrics;
using Pillion.Eval.Models;

namespace Pillion.Eval.Retrieval;

/// <summary>The options of a <c>retrieval</c> run.</summary>
internal sealed record RetrievalRunOptions
{
    /// <summary>Gets the golden set folders.</summary>
    public required IReadOnlyList<string> Sets { get; init; }

    /// <summary>Gets the models.</summary>
    public required IReadOnlyList<ModelSpec> Models { get; init; }

    /// <summary>Gets the folder that receives this run's timestamped output folder.</summary>
    public required string OutputRoot { get; init; }

    /// <summary>Gets the search modes to score (<c>hybrid</c>, <c>lexical</c>, <c>vector</c>, <c>symbol</c>).</summary>
    public IReadOnlyList<string> Modes { get; init; } = ["hybrid", "lexical", "vector"];

    /// <summary>Gets a value indicating whether to measure the int8 recall loss against float32.</summary>
    public bool Int8Loss { get; init; } = true;

    /// <summary>Gets the search time budget (generous: quality is measured, latency is reported apart).</summary>
    public TimeSpan Budget { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Gets the folder on-request bundled models are downloaded to.</summary>
    public required string ModelsDirectory { get; init; }

    /// <summary>Gets a value indicating whether a missing bundled model may be downloaded.</summary>
    public bool Download { get; init; } = true;

    /// <summary>Gets the Ollama endpoint.</summary>
    public string OllamaEndpoint { get; init; } = "http://localhost:11434/";

    /// <summary>Gets a value indicating whether bundled models may run on DirectML (the baseline is the CPU).</summary>
    public bool DirectML { get; init; }

    /// <summary>Gets a value indicating whether the temporary corpus and index are kept.</summary>
    public bool KeepIndex { get; init; }

    /// <summary>Gets a folder that keeps each set and model's corpus and index between runs (a stopped run resumes), or <see langword="null"/> for a fresh temporary folder that is deleted.</summary>
    public string? WorkDirectory { get; init; }

    /// <summary>Gets the longest indexing may take.</summary>
    public TimeSpan IndexTimeout { get; init; } = TimeSpan.FromHours(3);

    /// <summary>Gets a value indicating whether host logs at Information level are shown.</summary>
    public bool Verbose { get; init; }

    /// <summary>Gets a value indicating whether a set whose expected files are not all in the corpus still runs.</summary>
    public bool AllowMissingFiles { get; init; }
}

/// <summary>The output of a run: one result per set and model, and where the files went.</summary>
/// <param name="OutputDirectory">The timestamped output folder.</param>
/// <param name="Results">The results.</param>
/// <param name="SummaryPath">The Markdown summary.</param>
internal sealed record RetrievalRunReport(string OutputDirectory, IReadOnlyList<SetModelResult> Results, string SummaryPath);

/// <summary>The result of one golden set with one model.</summary>
internal sealed record SetModelResult
{
    /// <summary>Gets the harness result format version.</summary>
    public int FormatVersion { get; init; } = 1;

    /// <summary>Gets when the run started (UTC).</summary>
    public required DateTimeOffset StartedUtc { get; init; }

    /// <summary>Gets the machine.</summary>
    public required MachineInfo Machine { get; init; }

    /// <summary>Gets the set name.</summary>
    public required string Set { get; init; }

    /// <summary>Gets the set folder.</summary>
    public required string SetDirectory { get; init; }

    /// <summary>Gets the model spec.</summary>
    public required string Model { get; init; }

    /// <summary>Gets how the model is pinned.</summary>
    public ModelIdentityInfo? Identity { get; init; }

    /// <summary>Gets the embedding model as the gateway described it (dimensions, tokenizer, input limit).</summary>
    public string? EmbeddingModel { get; init; }

    /// <summary>Gets the index figures.</summary>
    public required IndexFigures Index { get; init; }

    /// <summary>Gets the search settings.</summary>
    public required SearchSettings Search { get; init; }

    /// <summary>Gets one summary per mode.</summary>
    public required IReadOnlyList<ModeSummary> Modes { get; init; }

    /// <summary>Gets the int8 loss measurement, when run.</summary>
    public Int8LossSummary? Int8Loss { get; init; }

    /// <summary>Gets problems that do not stop the run (unreachable symbols, missing vectors).</summary>
    public required IReadOnlyList<string> Warnings { get; init; }

    /// <summary>Gets the per-query outcomes.</summary>
    public required IReadOnlyList<QueryResult> Queries { get; init; }

    /// <summary>Gets the path of the JSON file this result was written to.</summary>
    public string? JsonPath { get; init; }
}

/// <summary>The machine a run used.</summary>
/// <param name="Name">The computer name.</param>
/// <param name="Os">The operating system.</param>
/// <param name="Cpu">The processor name.</param>
/// <param name="LogicalProcessors">The logical processor count.</param>
/// <param name="MemoryGb">The physical memory.</param>
/// <param name="Gpu">The GPU as the tier detector sees it.</param>
/// <param name="Runtime">The .NET runtime.</param>
internal sealed record MachineInfo(string Name, string Os, string Cpu, int LogicalProcessors, double MemoryGb, string Gpu, string Runtime);

/// <summary>What was indexed and how long it took.</summary>
/// <param name="CorpusFiles">Files copied into the corpus.</param>
/// <param name="IndexedFiles">Files the pipeline indexed (the rest are excluded by discovery).</param>
/// <param name="Chunks">Chunks in the index.</param>
/// <param name="EmbeddedChunks">Chunks with a vector.</param>
/// <param name="Lines">Lines indexed.</param>
/// <param name="Seconds">Wall time from setting the workspace to an idle, swept index.</param>
/// <param name="EmbeddingSeconds">Time spent embedding (pipeline statistics).</param>
internal sealed record IndexFigures(int CorpusFiles, long IndexedFiles, long Chunks, long EmbeddedChunks, long Lines, double Seconds, double EmbeddingSeconds);

/// <summary>The search request settings.</summary>
/// <param name="K">The cut-off and <c>MaxResults</c>.</param>
/// <param name="MaxPerFile">The per-file cap (the retriever's default).</param>
/// <param name="BudgetMs">The time budget per search.</param>
internal sealed record SearchSettings(int K, int MaxPerFile, double BudgetMs);

/// <summary>The figures of one search mode.</summary>
internal sealed record ModeSummary
{
    /// <summary>Gets the mode.</summary>
    public required string Mode { get; init; }

    /// <summary>Gets the number of queries.</summary>
    public required int Queries { get; init; }

    /// <summary>Gets recall at 1 by file.</summary>
    public required double FileRecallAt1 { get; init; }

    /// <summary>Gets recall at 5 by file.</summary>
    public required double FileRecallAt5 { get; init; }

    /// <summary>Gets recall at 10 by file (the gate metric).</summary>
    public required double FileRecallAt10 { get; init; }

    /// <summary>Gets the mean reciprocal rank by file (cut at 10).</summary>
    public required double FileMrr { get; init; }

    /// <summary>Gets the number of queries with expected symbols.</summary>
    public required int SymbolQueries { get; init; }

    /// <summary>Gets recall at 10 by symbol (over the queries with symbols).</summary>
    public required double SymbolRecallAt10 { get; init; }

    /// <summary>Gets the mean reciprocal rank by symbol (cut at 10).</summary>
    public required double SymbolMrr { get; init; }

    /// <summary>Gets the median search latency, end to end through the retrieval service, query embedding included.</summary>
    public required double P50Ms { get; init; }

    /// <summary>Gets the 95th percentile search latency.</summary>
    public required double P95Ms { get; init; }

    /// <summary>Gets the number of degraded responses (a leg failed or ran out of time).</summary>
    public required int Degraded { get; init; }

    /// <summary>Gets the figures per query kind.</summary>
    public required IReadOnlyDictionary<string, KindSummary> ByKind { get; init; }
}

/// <summary>The figures of one query kind in one mode.</summary>
/// <param name="Queries">The number of queries.</param>
/// <param name="FileRecallAt10">Recall at 10 by file.</param>
/// <param name="FileMrr">MRR by file.</param>
/// <param name="SymbolRecallAt10">Recall at 10 by symbol.</param>
internal sealed record KindSummary(int Queries, double FileRecallAt10, double FileMrr, double SymbolRecallAt10);

/// <summary>The int8 loss: the vector leg scored with float32 and with int8 vectors from the same embedding calls.</summary>
/// <param name="Chunks">Chunks scored.</param>
/// <param name="ReEmbedded">Chunks whose vectors were not captured while indexing and were embedded again.</param>
/// <param name="Float32">The float32 figures.</param>
/// <param name="Int8">The int8 figures.</param>
/// <param name="FileRecallLossPoints">Float32 minus int8 file recall at 10, in percentage points.</param>
/// <param name="SymbolRecallLossPoints">Float32 minus int8 symbol recall at 10, in percentage points.</param>
/// <param name="FileMrrLoss">Float32 minus int8 file MRR.</param>
/// <param name="TopTenOverlap">The mean share of the top 10 chunks the two agree on.</param>
internal sealed record Int8LossSummary(int Chunks, int ReEmbedded, LegFigures Float32, LegFigures Int8, double FileRecallLossPoints, double SymbolRecallLossPoints, double FileMrrLoss, double TopTenOverlap);

/// <summary>Recall and MRR of one leg.</summary>
/// <param name="FileRecallAt10">Recall at 10 by file.</param>
/// <param name="FileMrr">MRR by file.</param>
/// <param name="SymbolRecallAt10">Recall at 10 by symbol.</param>
/// <param name="SymbolMrr">MRR by symbol.</param>
internal sealed record LegFigures(double FileRecallAt10, double FileMrr, double SymbolRecallAt10, double SymbolMrr);

/// <summary>One query's outcome.</summary>
internal sealed record QueryResult
{
    /// <summary>Gets the query id.</summary>
    public required string Id { get; init; }

    /// <summary>Gets the query text.</summary>
    public required string Query { get; init; }

    /// <summary>Gets the kind.</summary>
    public string? Kind { get; init; }

    /// <summary>Gets the expected files.</summary>
    public required IReadOnlyList<string> ExpectedFiles { get; init; }

    /// <summary>Gets the expected symbols.</summary>
    public required IReadOnlyList<string> ExpectedSymbols { get; init; }

    /// <summary>Gets the outcome per mode.</summary>
    public required IReadOnlyDictionary<string, QueryModeResult> Modes { get; init; }

    /// <summary>Gets the float32 and int8 vector-leg ranks (file), when measured.</summary>
    public Int8Ranks? Int8FileRanks { get; init; }
}

/// <summary>A query's file rank in the vector leg with float32 and with int8 vectors.</summary>
/// <param name="Float32">The float32 rank (0 = none in the top 10).</param>
/// <param name="Int8">The int8 rank.</param>
internal sealed record Int8Ranks(int Float32, int Int8);

/// <summary>One query in one mode.</summary>
/// <param name="FileRank">The rank of the first result in an expected file (0 = none in the top 10).</param>
/// <param name="SymbolRank">The rank of the first result whose breadcrumb contains an expected symbol (0 = none).</param>
/// <param name="LatencyMs">The search's wall time.</param>
/// <param name="Degraded">Whether the response was degraded.</param>
/// <param name="Top">The top results.</param>
internal sealed record QueryModeResult(int FileRank, int SymbolRank, double LatencyMs, bool Degraded, IReadOnlyList<RankedResult> Top);
