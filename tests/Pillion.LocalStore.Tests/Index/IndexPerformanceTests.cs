using System.Diagnostics;
using System.Globalization;
using System.Text;
using Pillion.LocalStore.Index;
using Xunit.Abstractions;

namespace Pillion.LocalStore.Tests.Index;

/// <summary>
/// Design section 13 budgets on a 100,000-chunk index of 384-dimension int8 vectors and short texts: insert throughput,
/// vector KNN and FTS5 latency (p50 and p95 over 50 queries) with both vector backends (the default in-memory backend and
/// the table scan), and the bytes on disk per chunk. A benchmark (about a minute), so CI leaves it out
/// (<c>Category!=Benchmark</c>); the Gate 2 script runs it (work item 2.17). The measured figures go to the test output.
/// </summary>
[Collection(IndexPerformanceGroup.Name)]
[Trait("Category", "Benchmark")]
public sealed class IndexPerformanceTests : IDisposable
{
    private const int Chunks = 100_000;
    private const int ChunksPerFile = 10;
    private const int FilesPerBatch = 100;
    private const int Dims = 384;
    private const int Queries = 50;
    private const int K = 50;

    // Section 13: semantic search p50 100 ms and p95 300 ms (hybrid, on the reference machine, which is the development
    // machine). On a shared CI runner (CI set) every limit is 10/3 times looser: a p95 of 1 s.
    private static readonly double Scale = string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CI")) ? 1 : 10.0 / 3;
    private static readonly TimeSpan VectorP95Limit = TimeSpan.FromMilliseconds(300 * Scale);
    private static readonly TimeSpan LexicalP95Limit = TimeSpan.FromMilliseconds(300 * Scale);

    // Section 13: about 1.5 KB per chunk (with these short texts).
    private const double BytesPerChunkLimit = 1536;

    private static readonly string[] Verbs = ["get", "set", "update", "create", "delete", "find", "load", "save", "parse", "render", "build", "apply", "validate", "send", "compute"];
    private static readonly string[] Nouns = ["order", "customer", "invoice", "total", "payment", "address", "item", "cart", "token", "session", "index", "chunk", "vector", "file", "model", "request", "response", "user", "price", "tax"];
    private static readonly string[] Languages = ["csharp", "typescript", "python", "sql"];

    private readonly TempDirectory _temp = new();
    private readonly ITestOutputHelper _output;

    public IndexPerformanceTests(ITestOutputHelper output) => _output = output;

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task A100kChunkIndex_MeetsTheSearchBudgets()
    {
        var total = Stopwatch.StartNew();
        var random = new Random(20261015);
        await using var storage = await IndexTestSupport.OpenAsync(_temp);

        // Files: 20 partitions, 4 languages.
        var fileIds = new long[Chunks / ChunksPerFile];
        await storage.Database.WriteAsync(async (connection, transaction, token) =>
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO files (path, language, partition) VALUES ($path, $language, $partition) RETURNING id";
            var path = command.Parameters.Add("$path", Microsoft.Data.Sqlite.SqliteType.Text);
            var language = command.Parameters.Add("$language", Microsoft.Data.Sqlite.SqliteType.Text);
            var partition = command.Parameters.Add("$partition", Microsoft.Data.Sqlite.SqliteType.Text);
            for (var f = 0; f < fileIds.Length; f++)
            {
                path.Value = string.Create(CultureInfo.InvariantCulture, $"src/Part{f % 20}/File{f}.cs");
                language.Value = Languages[f % Languages.Length];
                partition.Value = string.Create(CultureInfo.InvariantCulture, $"src/Part{f % 20}");
                fileIds[f] = (long)(await command.ExecuteScalarAsync(token))!;
            }
        });

        // Insert: batches of 100 files x 10 chunks, one transaction each.
        var textBytes = 0L;
        var insert = Stopwatch.StartNew();
        for (var start = 0; start < fileIds.Length; start += FilesPerBatch)
        {
            var batch = new List<FileChunks>(FilesPerBatch);
            for (var f = start; f < start + FilesPerBatch; f++)
            {
                var chunks = new List<NewChunk>(ChunksPerFile);
                for (var c = 0; c < ChunksPerFile; c++)
                {
                    var text = RandomCode(random);
                    textBytes += Encoding.UTF8.GetByteCount(text);
                    chunks.Add(new NewChunk(c * 10 + 1, c * 10 + 8, "method", string.Create(CultureInfo.InvariantCulture, $"src/Part{f % 20}/File{f}.cs > Ns > Type{f} > M{c}"), text, text.Length / 4)
                    {
                        Embedding = IndexTestSupport.RandomVector(random, Dims),
                    });
                }

                batch.Add(new FileChunks(fileIds[f], chunks));
            }

            await storage.Chunks.ReplaceFilesChunksAsync(batch);
        }

        insert.Stop();
        Assert.Equal(Chunks, await storage.Chunks.CountChunksAsync());
        Assert.Equal(Chunks, await storage.CountAsync("SELECT count(*) FROM chunk_embeddings"));

        var size = await storage.Chunks.GetSizeAsync();
        var bytesPerChunk = (double)size.Bytes / Chunks;
        Report($"Insert: {Chunks:N0} chunks in {insert.Elapsed.TotalSeconds:F1} s = {Chunks / insert.Elapsed.TotalSeconds:N0} chunks/s (chunks, FTS5 and int8 vectors, batches of {FilesPerBatch * ChunksPerFile})");
        Report($"Disk: {size.Bytes / 1048576.0:F1} MB = {bytesPerChunk:N0} bytes per chunk (average text {textBytes / (double)Chunks:N0} bytes; WAL {size.WalBytes / 1048576.0:F1} MB; no float32 vectors)");

        // Queries.
        var vectorQueries = Enumerable.Range(0, Queries).Select(_ => IndexTestSupport.RandomVector(random, Dims)).ToList();
        var lexicalQueries = Enumerable.Range(0, Queries).Select(i => i % 2 == 0
            ? Pick(random, Verbs) + Capitalise(Pick(random, Nouns)) + Capitalise(Pick(random, Nouns))
            : Pick(random, Nouns) + " " + Pick(random, Nouns) + " " + Pick(random, Verbs)).ToList();
        var filter = new ChunkFilter { Partitions = ["src/Part3", "src/Part7"] };

        Assert.Equal(AutoVectorIndex.Name, storage.Vectors.Backend);
        var cacheLoad = Stopwatch.StartNew();
        await storage.WarmVectorCacheAsync();
        cacheLoad.Stop();
        var inMemory = await MeasureAsync(vectorQueries, q => storage.Vectors.SearchAsync(q, K));
        var inMemoryFiltered = await MeasureAsync(vectorQueries, q => storage.Vectors.SearchAsync(q, K, filter));
        var scan = await MeasureAsync(vectorQueries, q => storage.TableScan.SearchAsync(q, K));
        var scanFiltered = await MeasureAsync(vectorQueries, q => storage.TableScan.SearchAsync(q, K, filter));
        var lexical = await MeasureAsync(lexicalQueries, q => storage.Chunks.SearchLexicalAsync(q, K));
        var lexicalFiltered = await MeasureAsync(lexicalQueries, q => storage.Chunks.SearchLexicalAsync(q, K, filter));

        Report($"Vector KNN k={K}, in-memory (copy of {storage.VectorCacheBytes / 1048576.0:F1} MB, loaded in {cacheLoad.Elapsed.TotalMilliseconds:F0} ms): p50 {inMemory.P50:F1} ms, p95 {inMemory.P95:F1} ms; 2 of 20 partitions: p50 {inMemoryFiltered.P50:F1} ms, p95 {inMemoryFiltered.P95:F1} ms");
        Report($"Vector KNN k={K}, table scan: p50 {scan.P50:F1} ms, p95 {scan.P95:F1} ms; 2 of 20 partitions: p50 {scanFiltered.P50:F1} ms, p95 {scanFiltered.P95:F1} ms");
        Report($"FTS5 BM25 k={K}: p50 {lexical.P50:F1} ms, p95 {lexical.P95:F1} ms; 2 of 20 partitions: p50 {lexicalFiltered.P50:F1} ms, p95 {lexicalFiltered.P95:F1} ms");

        // The backends agree on the full index too (distances to the float; the ids too, unless two distances tie).
        for (var i = 0; i < 5; i++)
        {
            var fromScan = await storage.TableScan.SearchAsync(vectorQueries[i], 10);
            var fromMemory = await storage.Vectors.SearchAsync(vectorQueries[i], 10);
            Assert.Equal(fromScan.Select(h => (float)h.Distance), fromMemory.Select(h => (float)h.Distance));
            if (fromScan.Select(h => h.Distance).Distinct().Count() == fromScan.Count)
            {
                Assert.Equal(fromScan.Select(h => h.ChunkId), fromMemory.Select(h => h.ChunkId));
            }
        }

        Report($"Test body: {total.Elapsed.TotalSeconds:F1} s");

        Assert.True(TimeSpan.FromMilliseconds(inMemory.P95) < VectorP95Limit, $"in-memory p95 {inMemory.P95:F1} ms, limit {VectorP95Limit.TotalMilliseconds} ms");
        Assert.True(TimeSpan.FromMilliseconds(lexical.P95) < LexicalP95Limit, $"FTS5 p95 {lexical.P95:F1} ms, limit {LexicalP95Limit.TotalMilliseconds} ms");
        Assert.True(bytesPerChunk < BytesPerChunkLimit, $"{bytesPerChunk:N0} bytes per chunk, limit {BytesPerChunkLimit}");
    }
    private static async Task<(double P50, double P95)> MeasureAsync<TQuery, TResult>(IReadOnlyList<TQuery> queries, Func<TQuery, Task<TResult>> run)
    {
        // Warm up: the first reads open and key reader connections and fill the page cache.
        for (var i = 0; i < 3; i++)
        {
            await run(queries[i]);
        }

        var times = new List<double>(queries.Count);
        foreach (var query in queries)
        {
            var watch = Stopwatch.StartNew();
            await run(query);
            times.Add(watch.Elapsed.TotalMilliseconds);
        }

        times.Sort();
        return (Percentile(times, 0.50), Percentile(times, 0.95));
    }

    private static double Percentile(List<double> sorted, double p) => sorted[Math.Clamp((int)Math.Ceiling(p * sorted.Count) - 1, 0, sorted.Count - 1)];

    private static string RandomCode(Random random)
    {
        var builder = new StringBuilder(320);
        var method = Pick(random, Verbs) + Capitalise(Pick(random, Nouns)) + Capitalise(Pick(random, Nouns));
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
        return builder.ToString();
    }

    private static string Pick(Random random, string[] words) => words[random.Next(words.Length)];

    private static string Capitalise(string word) => char.ToUpperInvariant(word[0]) + word[1..];

    private void Report(string line)
    {
        _output.WriteLine(line);
        Console.WriteLine("[IndexPerformance] " + line);
    }
}

/// <summary>
/// Runs the index performance test alone in the test process: xUnit runs a collection that disables parallelization after
/// every parallel test has finished.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class IndexPerformanceGroup
{
    /// <summary>The collection's name.</summary>
    public const string Name = "Index performance";
}
