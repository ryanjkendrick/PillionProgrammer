using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Pillion.Common.Hardware;
using Pillion.ContextEngine.Retrieval;
using Pillion.Contracts;
using Pillion.Eval.Golden;
using Pillion.Eval.Metrics;
using Pillion.Eval.Models;
using Pillion.ModelGateway;

namespace Pillion.Eval.Retrieval;

/// <summary>
/// The <c>retrieval</c> command: for every golden set and model, builds the corpus, indexes it through the real pipeline,
/// runs every query through <see cref="Host.Context.Retrieval.IRetrievalService"/> in each mode, measures the int8 loss, and
/// writes a JSON result per set and model plus a Markdown summary.
/// </summary>
internal static class RetrievalRunner
{
    /// <summary>The JSON options of the result files.</summary>
    internal static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Runs every set with every model.</summary>
    /// <param name="options">The options.</param>
    /// <param name="log">Receives progress.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    /// <returns>The report.</returns>
    public static async Task<RetrievalRunReport> RunAsync(RetrievalRunOptions options, TextWriter log, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(log);
        var started = DateTimeOffset.UtcNow;
        var output = Path.Combine(options.OutputRoot, started.ToLocalTime().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(output);
        var sets = options.Sets.Select(GoldenSet.Load).ToList();
        foreach (var set in sets)
        {
            var problems = Corpus.CheckExpectedFiles(set, Corpus.List(set));
            if (problems.Count > 0 && !options.AllowMissingFiles)
            {
                throw new GoldenSetException($"{set.Name}: {problems.Count} expected file problem(s), for example: {string.Join(" ", problems.Take(5))} (pass --allow-missing-files to run anyway).");
            }
        }

        var machine = DescribeMachine();
        var results = new List<SetModelResult>();
        foreach (var model in options.Models)
        {
            foreach (var set in sets)
            {
                await log.WriteLineAsync($"== {set.Name} with {model} ==").ConfigureAwait(false);
                var result = await RunOneAsync(set, model, options, machine, log, cancellationToken).ConfigureAwait(false);
                var jsonPath = Path.Combine(output, $"{set.Name}__{model.FileSafeName}.json");
                result = result with { JsonPath = jsonPath };
                await File.WriteAllTextAsync(jsonPath, JsonSerializer.Serialize(result, Json), cancellationToken).ConfigureAwait(false);
                await File.WriteAllTextAsync(Path.ChangeExtension(jsonPath, ".md"), RetrievalReport.Single(result), cancellationToken).ConfigureAwait(false);
                await log.WriteLineAsync(RetrievalReport.ConsoleLines(result)).ConfigureAwait(false);
                results.Add(result);
            }
        }

        var summaryPath = Path.Combine(output, "summary.md");
        await File.WriteAllTextAsync(summaryPath, RetrievalReport.Summary(results, started), cancellationToken).ConfigureAwait(false);
        await log.WriteLineAsync($"Results: {output}").ConfigureAwait(false);
        return new RetrievalRunReport(output, results, summaryPath);
    }

    private static async Task<SetModelResult> RunOneAsync(GoldenSet set, ModelSpec model, RetrievalRunOptions options, MachineInfo machine, TextWriter log, CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.UtcNow;
        var warnings = new List<string>();
        warnings.AddRange(Corpus.CheckExpectedFiles(set, Corpus.List(set)));
        ModelIdentityInfo? identity = null;
        IReadOnlyList<string> residentBefore = [];
        if (model.Provider == ModelSpec.Bundled)
        {
            await ModelIdentity.EnsureBundledAsync(model, options.ModelsDirectory, options.Download, log, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            identity = await ModelIdentity.DescribeOllamaAsync(model, options.OllamaEndpoint, cancellationToken).ConfigureAwait(false);
            residentBefore = await ModelIdentity.OllamaResidentAsync(options.OllamaEndpoint, cancellationToken).ConfigureAwait(false);
            await log.WriteLineAsync($"  Ollama resident before the run: {(residentBefore.Count == 0 ? "nothing" : string.Join(", ", residentBefore))}").ConfigureAwait(false);
        }

        var temp = Path.Combine(Path.GetTempPath(), "pillion-eval", Guid.NewGuid().ToString("N")[..12]);
        var corpusRoot = Path.Combine(temp, "corpus");
        var dataRoot = Path.Combine(temp, "data");
        Directory.CreateDirectory(corpusRoot);
        Directory.CreateDirectory(dataRoot);
        try
        {
            var corpusFiles = Corpus.Copy(set, corpusRoot);
            await log.WriteLineAsync(string.Create(CultureInfo.InvariantCulture, $"  corpus: {corpusFiles.Count} files in {corpusRoot}")).ConfigureAwait(false);

            await using var environment = await EvalEnvironment.CreateAsync(model, options, dataRoot, cancellationToken).ConfigureAwait(false);
            var clock = Stopwatch.StartNew();
            var status = await environment.IndexAsync(corpusRoot, options.IndexTimeout, log, cancellationToken).ConfigureAwait(false);
            clock.Stop();
            var root = environment.Indexing.OpenRoots[0];
            var storage = root.Storage;
            var files = await storage.Chunks.ListFilesAsync(cancellationToken).ConfigureAwait(false);
            var chunks = files.Sum(f => (long)f.Chunks);
            var embedded = files.Sum(f => (long)f.Embedded);
            if (embedded < chunks)
            {
                warnings.Add(string.Create(CultureInfo.InvariantCulture, $"{chunks - embedded} of {chunks} chunks have no vector (last error: {status.LastError ?? "none"})."));
            }

            var stats = environment.Indexing.LastRunStatistics;
            var index = new IndexFigures(corpusFiles.Count, files.Count, chunks, embedded, stats?.Lines ?? 0, clock.Elapsed.TotalSeconds, stats?.EmbeddingTime.TotalSeconds ?? 0);
            await log.WriteLineAsync(string.Create(CultureInfo.InvariantCulture, $"  indexed {index.IndexedFiles} files, {index.Chunks} chunks ({index.EmbeddedChunks} embedded) in {index.Seconds:0.0} s")).ConfigureAwait(false);

            // Every chunk of the index, with its corpus-relative file and breadcrumb (the reachability check and the int8 loss read them).
            var indexed = new List<IndexedChunk>();
            foreach (var file in files)
            {
                var relative = Relative(root.Path, file.Path);
                foreach (var chunk in await storage.Chunks.GetChunksByFileAsync(file.FileId, cancellationToken).ConfigureAwait(false))
                {
                    indexed.Add(new IndexedChunk(relative, chunk.Breadcrumb, chunk.Breadcrumb + "\n" + chunk.Text));
                }
            }

            warnings.AddRange(UnreachableSymbols(set, indexed));

            var modes = new List<ModeSummary>();
            var perQuery = set.Queries.ToDictionary(q => q.Id, _ => new Dictionary<string, QueryModeResult>(StringComparer.Ordinal), StringComparer.Ordinal);
            foreach (var mode in options.Modes)
            {
                var (summary, outcomes) = await RunModeAsync(environment, root.Path, set, mode, options.Budget, cancellationToken).ConfigureAwait(false);
                modes.Add(summary);
                foreach (var (id, outcome) in outcomes)
                {
                    perQuery[id][mode] = outcome;
                }

                await log.WriteLineAsync(string.Create(CultureInfo.InvariantCulture, $"  {mode,-8} recall@10 file {summary.FileRecallAt10:0.000}, symbol {summary.SymbolRecallAt10:0.000}; MRR file {summary.FileMrr:0.000}; p50 {summary.P50Ms:0} ms, p95 {summary.P95Ms:0} ms{(summary.Degraded > 0 ? $", {summary.Degraded} degraded" : string.Empty)}")).ConfigureAwait(false);
            }

            Int8LossSummary? int8 = null;
            Dictionary<string, Int8Ranks>? int8Ranks = null;
            if (options.Int8Loss)
            {
                (int8, int8Ranks) = await MeasureInt8LossAsync(environment, model, set, indexed, cancellationToken).ConfigureAwait(false);
                await log.WriteLineAsync(string.Create(CultureInfo.InvariantCulture, $"  int8 loss: file recall@10 float32 {int8.Float32.FileRecallAt10:0.000} vs int8 {int8.Int8.FileRecallAt10:0.000} = {int8.FileRecallLossPoints:0.0} points; top-10 overlap {int8.TopTenOverlap:0.000}")).ConfigureAwait(false);
            }

            string? described = null;
            try
            {
                var probe = await environment.Gateway.EmbedAsync(new EmbeddingRequest { Inputs = ["describe the model"], Purpose = EmbeddingPurpose.Query, ModelId = model.QualifiedId }, cancellationToken).ConfigureAwait(false);
                if (probe.Model is { } info)
                {
                    described = string.Create(CultureInfo.InvariantCulture, $"{info.QualifiedId}: {info.Dimensions} dimensions, tokenizer {info.TokenizerFamily}, max input {info.MaxInputTokens} tokens");
                }
            }
            catch (PillionRpcException)
            {
                // The description is informative only.
            }

            if (model.Provider == ModelSpec.Bundled)
            {
                identity = ModelIdentity.DescribeBundled(model, environment.BundledRuntime);
            }

            var queries = set.Queries.Select(q => new QueryResult
            {
                Id = q.Id,
                Query = q.Query,
                Kind = q.Kind,
                ExpectedFiles = q.ExpectedFiles,
                ExpectedSymbols = q.ExpectedSymbols,
                Modes = perQuery[q.Id],
                Int8FileRanks = int8Ranks?.GetValueOrDefault(q.Id),
            }).ToList();

            return new SetModelResult
            {
                StartedUtc = started,
                Machine = machine,
                Set = set.Name,
                SetDirectory = set.Directory,
                Model = model.QualifiedId,
                Identity = identity,
                EmbeddingModel = described,
                Index = index,
                Search = new SearchSettings(RetrievalMetrics.K, RetrievalOptions.Default.DefaultMaxPerFile, options.Budget.TotalMilliseconds),
                Modes = modes,
                Int8Loss = int8,
                Warnings = warnings,
                Queries = queries,
            };
        }
        finally
        {
            if (model.Provider == ModelSpec.Ollama && !residentBefore.Any(r => ModelIdentity.SameOllamaModel(r, model.ModelId)))
            {
                await ModelIdentity.UnloadOllamaAsync(model, options.OllamaEndpoint, CancellationToken.None).ConfigureAwait(false);
            }

            if (options.KeepIndex)
            {
                await log.WriteLineAsync($"  kept {temp}").ConfigureAwait(false);
            }
            else
            {
                TryDelete(temp);
            }
        }
    }

    private static async Task<(ModeSummary Summary, List<(string Id, QueryModeResult Outcome)> Outcomes)> RunModeAsync(
        EvalEnvironment environment, string rootPath, GoldenSet set, string mode, TimeSpan budget, CancellationToken cancellationToken)
    {
        var searchMode = ParseMode(mode);
        var service = environment.CreateRetrievalService(budget);

        // The first search loads the model and warms the caches; it is not counted.
        await service.SearchDetailedAsync(new SearchRequest { Query = "warm up the search path before measuring", Mode = searchMode, MaxResults = RetrievalMetrics.K }, cancellationToken).ConfigureAwait(false);

        var outcomes = new List<(string, QueryModeResult)>();
        foreach (var query in set.Queries)
        {
            var started = Stopwatch.GetTimestamp();
            var response = await service.SearchDetailedAsync(new SearchRequest { Query = query.Query, Mode = searchMode, MaxResults = RetrievalMetrics.K }, cancellationToken).ConfigureAwait(false);
            var latency = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            var ranked = response.Results.Select(r => new RankedResult(Relative(rootPath, r.FilePath), r.Breadcrumb ?? string.Empty)).ToList();
            outcomes.Add((query.Id, new QueryModeResult(
                RetrievalMetrics.FirstFileRank(ranked, [.. query.ExpectedFiles]),
                RetrievalMetrics.FirstSymbolRank(ranked, [.. query.ExpectedSymbols]),
                latency,
                response.Degraded,
                ranked)));
        }

        return (Summarize(mode, set.Queries, outcomes.ToDictionary(o => o.Item1, o => o.Item2, StringComparer.Ordinal)), outcomes);
    }

    /// <summary>Summarizes one mode's outcomes.</summary>
    /// <param name="mode">The mode.</param>
    /// <param name="queries">The queries.</param>
    /// <param name="outcomes">The outcome per query id.</param>
    /// <returns>The summary.</returns>
    internal static ModeSummary Summarize(string mode, IReadOnlyList<GoldenQuery> queries, IReadOnlyDictionary<string, QueryModeResult> outcomes)
    {
        var fileRanks = queries.Select(q => outcomes[q.Id].FileRank).ToList();
        var symbolRanks = queries.Where(q => q.ExpectedSymbols.Count > 0).Select(q => outcomes[q.Id].SymbolRank).ToList();
        var latencies = queries.Select(q => outcomes[q.Id].LatencyMs).ToList();
        var byKind = queries
            .GroupBy(q => q.Kind ?? "(none)", StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g =>
                {
                    var ranks = g.Select(q => outcomes[q.Id].FileRank).ToList();
                    var symbols = g.Where(q => q.ExpectedSymbols.Count > 0).Select(q => outcomes[q.Id].SymbolRank).ToList();
                    return new KindSummary(ranks.Count, RetrievalMetrics.RecallAt(ranks), RetrievalMetrics.MeanReciprocalRank(ranks), RetrievalMetrics.RecallAt(symbols));
                },
                StringComparer.Ordinal);

        return new ModeSummary
        {
            Mode = mode,
            Queries = queries.Count,
            FileRecallAt1 = RetrievalMetrics.RecallAt(fileRanks, 1),
            FileRecallAt5 = RetrievalMetrics.RecallAt(fileRanks, 5),
            FileRecallAt10 = RetrievalMetrics.RecallAt(fileRanks),
            FileMrr = RetrievalMetrics.MeanReciprocalRank(fileRanks),
            SymbolQueries = symbolRanks.Count,
            SymbolRecallAt10 = RetrievalMetrics.RecallAt(symbolRanks),
            SymbolMrr = RetrievalMetrics.MeanReciprocalRank(symbolRanks),
            P50Ms = RetrievalMetrics.Percentile(latencies, 50),
            P95Ms = RetrievalMetrics.Percentile(latencies, 95),
            Degraded = queries.Count(q => outcomes[q.Id].Degraded),
            ByKind = byKind,
        };
    }

    private static async Task<(Int8LossSummary Summary, Dictionary<string, Int8Ranks> Ranks)> MeasureInt8LossAsync(
        EvalEnvironment environment, ModelSpec model, GoldenSet set, IReadOnlyList<IndexedChunk> indexed, CancellationToken cancellationToken)
    {
        // The chunk vectors were captured as the pipeline embedded them; anything missed (should be nothing) is embedded again.
        var captured = environment.Gateway.Documents;
        var missing = indexed.Select(c => c.EmbeddingText).Where(t => !captured.ContainsKey(t)).Distinct(StringComparer.Ordinal).ToList();
        for (var start = 0; start < missing.Count; start += 32)
        {
            var batch = missing.Skip(start).Take(32).ToList();
            await environment.Gateway.EmbedAsync(new EmbeddingRequest { Inputs = batch, Purpose = EmbeddingPurpose.Document, ModelId = model.QualifiedId, BatchSize = batch.Count }, cancellationToken).ConfigureAwait(false);
        }

        var vectors = new List<(IndexedChunk Chunk, EmbeddedVector Vector)>(indexed.Count);
        foreach (var chunk in indexed)
        {
            if (captured.TryGetValue(chunk.EmbeddingText, out var vector) && vector.Normalized is not null)
            {
                vectors.Add((chunk, vector));
            }
        }

        var texts = set.Queries.Select(q => q.Query).ToList();
        var queryResult = await environment.Gateway.EmbedAsync(new EmbeddingRequest { Inputs = texts, Purpose = EmbeddingPurpose.Query, ModelId = model.QualifiedId, IncludeFloat32 = true, BatchSize = 16 }, cancellationToken).ConfigureAwait(false);

        var ranks = new Dictionary<string, Int8Ranks>(StringComparer.Ordinal);
        var floatFile = new List<int>();
        var int8File = new List<int>();
        var floatSymbol = new List<int>();
        var int8Symbol = new List<int>();
        double overlap = 0;
        for (var i = 0; i < set.Queries.Count; i++)
        {
            var query = set.Queries[i];
            var queryVector = queryResult.Vectors[i] ?? throw new InvalidOperationException($"The query {query.Id} could not be embedded.");
            var floatTop = TopTen(vectors, v => EmbeddingVectors.Cosine(queryVector.Normalized!, v.Normalized!));
            var int8Top = TopTen(vectors, v => EmbeddingVectors.Cosine(queryVector.Quantized, v.Quantized));
            overlap += floatTop.Intersect(int8Top).Count() / (double)Math.Max(1, floatTop.Count);
            var floatRanked = floatTop.Select(index => new RankedResult(vectors[index].Chunk.File, vectors[index].Chunk.Breadcrumb)).ToList();
            var int8Ranked = int8Top.Select(index => new RankedResult(vectors[index].Chunk.File, vectors[index].Chunk.Breadcrumb)).ToList();
            var f = RetrievalMetrics.FirstFileRank(floatRanked, [.. query.ExpectedFiles]);
            var q = RetrievalMetrics.FirstFileRank(int8Ranked, [.. query.ExpectedFiles]);
            floatFile.Add(f);
            int8File.Add(q);
            ranks[query.Id] = new Int8Ranks(f, q);
            if (query.ExpectedSymbols.Count > 0)
            {
                floatSymbol.Add(RetrievalMetrics.FirstSymbolRank(floatRanked, [.. query.ExpectedSymbols]));
                int8Symbol.Add(RetrievalMetrics.FirstSymbolRank(int8Ranked, [.. query.ExpectedSymbols]));
            }
        }

        var floatLeg = new LegFigures(RetrievalMetrics.RecallAt(floatFile), RetrievalMetrics.MeanReciprocalRank(floatFile), RetrievalMetrics.RecallAt(floatSymbol), RetrievalMetrics.MeanReciprocalRank(floatSymbol));
        var int8Leg = new LegFigures(RetrievalMetrics.RecallAt(int8File), RetrievalMetrics.MeanReciprocalRank(int8File), RetrievalMetrics.RecallAt(int8Symbol), RetrievalMetrics.MeanReciprocalRank(int8Symbol));
        return (new Int8LossSummary(
            vectors.Count,
            missing.Count,
            floatLeg,
            int8Leg,
            (floatLeg.FileRecallAt10 - int8Leg.FileRecallAt10) * 100,
            (floatLeg.SymbolRecallAt10 - int8Leg.SymbolRecallAt10) * 100,
            floatLeg.FileMrr - int8Leg.FileMrr,
            overlap / Math.Max(1, set.Queries.Count)), ranks);
    }

    /// <summary>The indexes of the ten best-scoring entries, best first (ties by index, so the order is deterministic).</summary>
    private static List<int> TopTen(List<(IndexedChunk Chunk, EmbeddedVector Vector)> vectors, Func<EmbeddedVector, float> score)
    {
        var scores = new float[vectors.Count];
        for (var i = 0; i < scores.Length; i++)
        {
            scores[i] = score(vectors[i].Vector);
        }

        return [.. Enumerable.Range(0, scores.Length).OrderByDescending(i => scores[i]).ThenBy(i => i).Take(RetrievalMetrics.K)];
    }

    /// <summary>Lists the expected symbols that no chunk breadcrumb of the query's expected files contains (they cap symbol recall).</summary>
    private static IEnumerable<string> UnreachableSymbols(GoldenSet set, IReadOnlyList<IndexedChunk> indexed)
    {
        var byFile = indexed.ToLookup(c => c.File, StringComparer.OrdinalIgnoreCase);
        foreach (var query in set.Queries)
        {
            foreach (var symbol in query.ExpectedSymbols)
            {
                var parts = BreadcrumbSymbols.SplitSymbol(symbol);
                if (!query.ExpectedFiles.SelectMany(f => byFile[f]).Any(c => BreadcrumbSymbols.Contains(BreadcrumbSymbols.Names(c.Breadcrumb), parts)))
                {
                    yield return $"{query.Id}: no chunk of the expected files has '{symbol}' in its breadcrumb.";
                }
            }
        }
    }

    /// <summary>Maps a mode name to the contract's search mode.</summary>
    /// <param name="mode">The mode name.</param>
    /// <returns>The search mode.</returns>
    internal static SearchMode ParseMode(string mode) => mode.ToLowerInvariant() switch
    {
        "hybrid" => SearchMode.Hybrid,
        "lexical" => SearchMode.Lexical,
        "vector" => SearchMode.Vector,
        "symbol" => SearchMode.Symbol,
        _ => throw new FormatException($"Unknown mode '{mode}': use hybrid, lexical, vector or symbol."),
    };

    private static string Relative(string root, string path) =>
        RetrievalMetrics.NormalizePath(Path.IsPathRooted(path) ? Path.GetRelativePath(root, path) : path);

    private static MachineInfo DescribeMachine()
    {
        var cpu = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? RuntimeInformation.ProcessArchitecture.ToString();
        if (OperatingSystem.IsWindows())
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            if (key?.GetValue("ProcessorNameString") is string name)
            {
                cpu = name.Trim();
            }
        }

        string gpu;
        try
        {
            gpu = GpuTierDetector.Shared.Detect().ToString();
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            gpu = "unknown";
        }

        return new MachineInfo(
            Environment.MachineName,
            RuntimeInformation.OSDescription,
            cpu,
            Environment.ProcessorCount,
            Math.Round(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024.0 * 1024 * 1024), 1),
            gpu,
            RuntimeInformation.FrameworkDescription);
    }

    private static void TryDelete(string folder)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (Directory.Exists(folder))
                {
                    Directory.Delete(folder, recursive: true);
                }

                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(200);
            }
        }
    }

    private sealed record IndexedChunk(string File, string Breadcrumb, string EmbeddingText);
}
