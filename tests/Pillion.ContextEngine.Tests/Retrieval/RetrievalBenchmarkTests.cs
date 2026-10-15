using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using Pillion.ContextEngine.Retrieval;
using Pillion.Indexing.Storage;
using Pillion.LocalStore;
using Pillion.LocalStore.Index;
using Xunit.Abstractions;

namespace Pillion.ContextEngine.Tests.Retrieval;

/// <summary>
/// Design section 13: semantic search p50 100 ms and p95 300 ms on a 100,000-chunk index, hybrid without reranker. Builds
/// a generated encrypted index (10,000 files of 10 chunks, 384-dimension int8 vectors, one symbol per chunk), then runs 50
/// hybrid searches (lexical, vector and symbol legs, fusion, expansion) through <see cref="StorageRetrievalIndex"/> as the
/// host does, and logs the figures. The query is embedded by the hashing embedder (microseconds); the bundled model's own
/// cost for one query is logged by the host's <c>Category=Model</c> retrieval test. A benchmark (about a minute), so CI
/// leaves it out (<c>Category!=Benchmark</c>); the Gate 2 script runs it (work item 2.17).
/// </summary>
[Collection(RetrievalBenchmarkGroup.Name)]
[Trait("Category", "Benchmark")]
public sealed class RetrievalBenchmarkTests : IDisposable
{
    private const int Chunks = 100_000;
    private const int ChunksPerFile = 10;
    private const int FilesPerBatch = 100;
    private const int Queries = 50;

    // Section 13 on the reference machine (the development machine). On a shared CI runner (CI set) every limit is 10/3
    // times looser, the convention of IndexPerformanceTests.
    private static readonly double Scale = string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CI")) ? 1 : 10.0 / 3;
    private static readonly TimeSpan P50Limit = TimeSpan.FromMilliseconds(100 * Scale);
    private static readonly TimeSpan P95Limit = TimeSpan.FromMilliseconds(300 * Scale);

    private static readonly string[] Verbs = ["get", "set", "update", "create", "delete", "find", "load", "save", "parse", "render", "build", "apply", "validate", "send", "compute"];
    private static readonly string[] Nouns = ["order", "customer", "invoice", "total", "payment", "address", "item", "cart", "token", "session", "index", "chunk", "vector", "file", "model", "request", "response", "user", "price", "tax"];
    private static readonly string[] Languages = ["csharp", "typescript", "python", "sql"];
    private static readonly string[] Openers = ["how does", "where do we", "which code is used to", "explain how we", "what happens when we"];

    private readonly RetrievalTempDirectory _temp = new();
    private readonly ITestOutputHelper _output;

    public RetrievalBenchmarkTests(ITestOutputHelper output) => _output = output;

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task HybridSearch_OnA100kChunkIndex_MeetsTheSemanticSearchBudget()
    {
        var total = Stopwatch.StartNew();
        var random = new Random(20261015);
        var embedder = new HashingEmbedder();
        await using var storage = await IndexStorage.OpenAsync(new EncryptedDatabaseOptions { DatabasePath = _temp.File("index.db") }, IndexSchema.Schema);

        var build = Stopwatch.StartNew();
        var fileIds = await AddFilesAsync(storage);
        var symbols = new List<(long FileId, string Name, int Start, int End)>(Chunks);
        for (var start = 0; start < fileIds.Length; start += FilesPerBatch)
        {
            var batch = new List<FileChunks>(FilesPerBatch);
            for (var f = start; f < start + FilesPerBatch; f++)
            {
                var chunks = new List<NewChunk>(ChunksPerFile);
                for (var c = 0; c < ChunksPerFile; c++)
                {
                    var (method, text) = RandomMethod(random);
                    var line = (c * 10) + 1;
                    var breadcrumb = string.Create(CultureInfo.InvariantCulture, $"src/Part{f % 20}/File{f}.cs > Ns > Type{f} > {method}");
                    chunks.Add(new NewChunk(line, line + 8, "member", breadcrumb, text, text.Length / 4) { Embedding = embedder.Embed(breadcrumb + "\n" + text) });
                    symbols.Add((fileIds[f], method, line + 1, line + 8));
                }

                batch.Add(new FileChunks(fileIds[f], chunks));
            }

            await storage.Chunks.ReplaceFilesChunksAsync(batch);
        }

        await AddSymbolsAsync(storage, symbols);
        build.Stop();
        Assert.Equal(Chunks, await storage.Chunks.CountChunksAsync());
        var cacheLoad = Stopwatch.StartNew();
        await storage.WarmVectorCacheAsync();
        cacheLoad.Stop();
        Report($"Index: {Chunks:N0} chunks, {symbols.Count:N0} symbols in {build.Elapsed.TotalSeconds:F1} s; vector cache loaded in {cacheLoad.Elapsed.TotalMilliseconds:F0} ms");

        // 50 questions in natural language, half naming a method (an exact symbol hit), all with a few code words.
        var queries = Enumerable.Range(0, Queries + 3).Select(i => Question(random, withSymbol: i % 2 == 0)).ToList();
        var retriever = new HybridRetriever(embedder);
        var generous = TimeSpan.FromSeconds(10);
        var measured = await MeasureAsync(storage, retriever, queries, generous);
        var budgeted = await MeasureAsync(storage, new HybridRetriever(embedder), [.. queries.Select(q => q + " now")], budget: null);

        Report($"Hybrid search, {Queries} queries (k = 50 + 50 + symbols, 10 results, expansion), no time budget: p50 {measured.P50:F1} ms, p95 {measured.P95:F1} ms, max {measured.Max:F1} ms; mean legs: lexical {measured.Lexical:F1} ms, vector {measured.Vector:F1} ms, symbol {measured.Symbol:F1} ms, fusion and expansion {measured.Fusion:F1} ms; mean hits {measured.Hits:F1}");
        Report($"Same queries with the default 300 ms budget: p50 {budgeted.P50:F1} ms, p95 {budgeted.P95:F1} ms, max {budgeted.Max:F1} ms; degraded {budgeted.Degraded} of {Queries}");
        Report($"Test body: {total.Elapsed.TotalSeconds:F1} s");

        Assert.True(measured.Hits >= 9, $"mean hits {measured.Hits}");
        Assert.True(TimeSpan.FromMilliseconds(measured.P50) < P50Limit, $"hybrid p50 {measured.P50:F1} ms, limit {P50Limit.TotalMilliseconds} ms");
        Assert.True(TimeSpan.FromMilliseconds(measured.P95) < P95Limit, $"hybrid p95 {measured.P95:F1} ms, limit {P95Limit.TotalMilliseconds} ms");
    }

    private static async Task<Measurement> MeasureAsync(IndexStorage storage, HybridRetriever retriever, List<string> queries, TimeSpan? budget)
    {
        async Task<RetrievalResponse> RunAsync(string query)
        {
            // As the host does per search: the adapter reads the index meta, then the retriever runs.
            var index = await StorageRetrievalIndex.CreateAsync("00000000000000000000000000000000", @"C:\bench", storage, null, CancellationToken.None);
            return await retriever.SearchAsync(new RetrievalQuery { Text = query, TimeBudget = budget }, [index], CancellationToken.None);
        }

        // Warm up: the first reads open and key reader connections and fill the page cache.
        for (var i = 0; i < 3; i++)
        {
            await RunAsync(queries[i]);
        }

        var times = new List<double>(Queries);
        var responses = new List<RetrievalResponse>(Queries);
        foreach (var query in queries.Skip(3))
        {
            var watch = Stopwatch.StartNew();
            var response = await RunAsync(query);
            times.Add(watch.Elapsed.TotalMilliseconds);
            responses.Add(response);
        }

        times.Sort();
        return new Measurement(
            Percentile(times, 0.50),
            Percentile(times, 0.95),
            times[^1],
            responses.Average(r => r.LexicalMs),
            responses.Average(r => r.VectorMs),
            responses.Average(r => r.SymbolMs),
            responses.Average(r => r.FusionMs),
            responses.Average(r => r.Hits.Count),
            responses.Count(r => r.Degraded));
    }

    private static async Task<long[]> AddFilesAsync(IndexStorage storage)
    {
        var fileIds = new long[Chunks / ChunksPerFile];
        await storage.Database.WriteAsync(async (connection, transaction, token) =>
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO files (path, language, partition, hash, size, mtime, indexed_at) VALUES ($path, $language, $partition, 0, 0, 0, 0) RETURNING id";
            var path = command.Parameters.Add("$path", SqliteType.Text);
            var language = command.Parameters.Add("$language", SqliteType.Text);
            var partition = command.Parameters.Add("$partition", SqliteType.Text);
            for (var f = 0; f < fileIds.Length; f++)
            {
                path.Value = string.Create(CultureInfo.InvariantCulture, $"src/Part{f % 20}/File{f}.cs");
                language.Value = Languages[f % Languages.Length];
                partition.Value = string.Create(CultureInfo.InvariantCulture, $"src/Part{f % 20}");
                fileIds[f] = (long)(await command.ExecuteScalarAsync(token))!;
            }
        });
        return fileIds;
    }

    private static Task AddSymbolsAsync(IndexStorage storage, List<(long FileId, string Name, int Start, int End)> symbols) =>
        storage.Database.WriteAsync(async (connection, transaction, token) =>
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO symbols (file_id, name, kind, container, start_line, end_line) VALUES ($file, $name, 'method', 'Ns > Type', $start, $end)";
            var file = command.Parameters.Add("$file", SqliteType.Integer);
            var name = command.Parameters.Add("$name", SqliteType.Text);
            var start = command.Parameters.Add("$start", SqliteType.Integer);
            var end = command.Parameters.Add("$end", SqliteType.Integer);
            foreach (var symbol in symbols)
            {
                file.Value = symbol.FileId;
                name.Value = symbol.Name;
                start.Value = symbol.Start;
                end.Value = symbol.End;
                await command.ExecuteNonQueryAsync(token);
            }
        });

    private static string Question(Random random, bool withSymbol)
    {
        var builder = new StringBuilder(Pick(random, Openers)).Append(' ');
        builder.Append(withSymbol ? MethodName(random) : Pick(random, Verbs)).Append(" the ").Append(Pick(random, Nouns)).Append(' ').Append(Pick(random, Nouns));
        builder.Append(" for a ").Append(Pick(random, Nouns)).Append('?');
        return builder.ToString();
    }

    private static (string Method, string Text) RandomMethod(Random random)
    {
        var method = MethodName(random);
        var builder = new StringBuilder(320);
        builder.Append("public async Task<").Append(Capitalise(Pick(random, Nouns))).Append("> ").Append(method).Append('(')
            .Append(Capitalise(Pick(random, Nouns))).Append(' ').Append(Pick(random, Nouns)).Append("_id)\n{\n");
        var lines = random.Next(2, 6);
        for (var i = 0; i < lines; i++)
        {
            builder.Append("    var ").Append(Pick(random, Nouns)).Append(Capitalise(Pick(random, Nouns))).Append(" = await _")
                .Append(Pick(random, Nouns)).Append("Service.").Append(Capitalise(Pick(random, Verbs))).Append(Capitalise(Pick(random, Nouns)))
                .Append("Async(").Append(Pick(random, Nouns)).Append("_id);\n");
        }

        builder.Append("    return ").Append(Pick(random, Nouns)).Append(";\n}");
        return (method, builder.ToString());
    }

    private static string MethodName(Random random) => Pick(random, Verbs) + Capitalise(Pick(random, Nouns)) + Capitalise(Pick(random, Nouns));

    private static double Percentile(List<double> sorted, double p) => sorted[Math.Clamp((int)Math.Ceiling(p * sorted.Count) - 1, 0, sorted.Count - 1)];

    private static string Pick(Random random, string[] words) => words[random.Next(words.Length)];

    private static string Capitalise(string word) => char.ToUpperInvariant(word[0]) + word[1..];

    private void Report(string line)
    {
        _output.WriteLine(line);
        Console.WriteLine("[RetrievalBenchmark] " + line);
    }

    private sealed record Measurement(double P50, double P95, double Max, double Lexical, double Vector, double Symbol, double Fusion, double Hits, int Degraded);
}

/// <summary>Runs the retrieval benchmark alone in the test process.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class RetrievalBenchmarkGroup
{
    /// <summary>The collection's name.</summary>
    public const string Name = "Retrieval benchmark";
}
