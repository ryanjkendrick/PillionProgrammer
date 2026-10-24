using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Pillion.Common;
using Pillion.Common.Secrets;
using Pillion.ContextEngine.Chunking;
using Pillion.ContextEngine.Retrieval;
using Pillion.ContextEngine.Symbols;
using Pillion.Contracts;
using Pillion.Host.Context.Retrieval;
using Pillion.Host.Tests.TestSupport;
using Pillion.Indexing.Storage;
using Pillion.LocalStore;
using Pillion.LocalStore.Index;
using Pillion.ModelGateway;
using Xunit.Abstractions;

namespace Pillion.Host.Tests;

/// <summary>
/// Design section 13: context assembly with retrieval, p50 150 ms and p95 400 ms on a 100,000-chunk index (work item 2.12).
/// Builds a generated encrypted index (10,000 files of 10 chunks, 384-dimension int8 vectors, one symbol per chunk and the
/// symbol graph for the repo map), then assembles 40 chat requests through the host's own sources (hybrid retrieval of 8
/// chunks, the repo map focused on the current file) and the real <see cref="ContextAssembler"/> with the default token
/// counter, timing each whole assembly. The query is embedded by a hashing embedder for the budget assertion and its time is
/// reported separately; <see cref="ContextAssemblyModelBenchmarkTests"/> reports one run with the bundled model. A benchmark
/// (about a minute), so CI leaves it out (<c>Category!=Benchmark</c>); the Gate 2 script runs it (work item 2.17).
/// </summary>
[Collection(ContextAssemblyBenchmarkGroup.Name)]
[Trait("Category", "Benchmark")]
public sealed class ContextAssemblyBenchmarkTests(ITestOutputHelper output) : IDisposable
{
    // Section 13 on the reference machine; on a shared CI runner (CI set) every limit is 10/3 times looser, the convention
    // of tests/Pillion.LocalStore.Tests/Index/IndexPerformanceTests.cs.
    private static readonly double Scale = string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CI")) ? 1 : 10.0 / 3;
    private static readonly TimeSpan P50Limit = TimeSpan.FromMilliseconds(150 * Scale);
    private static readonly TimeSpan P95Limit = TimeSpan.FromMilliseconds(400 * Scale);

    private readonly TempDirectory _temp = new();

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        _temp.Dispose();
    }

    [Fact]
    public async Task ContextAssemblyWithRetrieval_OnA100kChunkIndex_MeetsTheBudget()
    {
        var embedder = new TimedEmbedder(new BenchmarkIndex.Hashing());
        await using var index = await BenchmarkIndex.BuildAsync(Path.Combine(_temp.Path, "index.db"), modelId: null, Report);

        var result = await BenchmarkIndex.MeasureAsync(index, embedder, Report);

        Report($"Context assembly with retrieval, {BenchmarkIndex.Requests} requests (8 chunks, repo map, 32k window): p50 {result.P50:F1} ms, p95 {result.P95:F1} ms, max {result.Max:F1} ms; "
            + $"query embedding (hashing) mean {result.EmbedMean:F2} ms, p95 {result.EmbedP95:F2} ms; mean chunks sent {result.Chunks:F1}, repo map in {result.WithMap} of {BenchmarkIndex.Requests}, prompt {result.PromptTokens:F0} tokens; degraded searches {result.Degraded}");
        Assert.True(result.Chunks >= 6, $"mean chunks {result.Chunks}");
        Assert.Equal(BenchmarkIndex.Requests, result.WithMap);
        Assert.True(TimeSpan.FromMilliseconds(result.P50) < P50Limit, $"assembly p50 {result.P50:F1} ms, limit {P50Limit.TotalMilliseconds} ms");
        Assert.True(TimeSpan.FromMilliseconds(result.P95) < P95Limit, $"assembly p95 {result.P95:F1} ms, limit {P95Limit.TotalMilliseconds} ms");
    }

    private void Report(string line)
    {
        output.WriteLine(line);
        Console.WriteLine("[ContextAssemblyBenchmark] " + line);
    }
}

/// <summary>
/// The same measurement with the bundled embedding model (bge-small-en-v1.5 on the CPU) embedding each query through the
/// host's Model Gateway; reported, not asserted (the model's own cost is the variable). Needs the model files
/// (<c>Category=Model</c>) and takes a minute (<c>Category=Benchmark</c>, so CI leaves it out).
/// </summary>
[Collection(ContextAssemblyBenchmarkGroup.Name)]
[Trait("Category", "Benchmark")]
[Trait("Category", "Model")]
public sealed class ContextAssemblyModelBenchmarkTests(ITestOutputHelper output) : IAsyncDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly TempDirectory _data = new();

    public ValueTask DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        _temp.Dispose();
        _data.Dispose();
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task ContextAssemblyWithRetrieval_WithTheBundledModel_IsReported()
    {
        await using var host = await InProcessHost.StartAsync(services =>
        {
            services.Replace(ServiceDescriptor.Singleton(new PillionPaths(_data.Path)));
            services.Replace(ServiceDescriptor.Singleton<ISecretStore>(new InMemorySecretStore()));
        });
        var gateway = host.Services.GetRequiredService<IModelGateway>();
        var probe = await gateway.EmbedAsync(new EmbeddingRequest { Inputs = ["probe"], Purpose = EmbeddingPurpose.Query }, CancellationToken.None);
        var model = probe.Model!.QualifiedId;
        var embedder = new TimedEmbedder(new GatewayQueryEmbedder(gateway));
        await using var index = await BenchmarkIndex.BuildAsync(Path.Combine(_temp.Path, "index.db"), model, Report);

        var result = await BenchmarkIndex.MeasureAsync(index, embedder, Report);

        Report($"With {model}: assembly p50 {result.P50:F1} ms, p95 {result.P95:F1} ms, max {result.Max:F1} ms; query embedding mean {result.EmbedMean:F1} ms, p95 {result.EmbedP95:F1} ms; "
            + $"degraded searches {result.Degraded} of {BenchmarkIndex.Requests} (a degraded search answers without the vector leg)");
        Assert.True(result.Chunks > 0);
    }

    private void Report(string line)
    {
        output.WriteLine(line);
        Console.WriteLine("[ContextAssemblyBenchmark] " + line);
    }
}

/// <summary>Runs the context assembly benchmarks alone in the test process.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ContextAssemblyBenchmarkGroup
{
    /// <summary>The collection's name.</summary>
    public const string Name = "Context assembly benchmark";
}

/// <summary>Times every query embedding of a wrapped embedder.</summary>
internal sealed class TimedEmbedder(IQueryEmbedder inner) : IQueryEmbedder
{
    private readonly object _gate = new();
    private readonly List<double> _times = [];

    public async Task<QueryEmbedding> EmbedQueryAsync(string text, string? modelId, int? dimensions, CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        var result = await inner.EmbedQueryAsync(text, modelId, dimensions, cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            _times.Add(watch.Elapsed.TotalMilliseconds);
        }

        return result;
    }

    public List<double> TakeTimes()
    {
        lock (_gate)
        {
            var copy = _times.ToList();
            _times.Clear();
            return copy;
        }
    }
}

/// <summary>The generated 100,000-chunk index and the measurement both benchmarks share.</summary>
internal sealed class BenchmarkIndex : IAsyncDisposable
{
    public const int Chunks = 100_000;
    public const int ChunksPerFile = 10;
    public const int Requests = 40;
    public const string Root = @"C:\bench";
    public const string IndexId = "00000000000000000000000000000000";

    private const int FilesPerBatch = 100;
    private const int WarmUp = 3;

    private static readonly string[] Verbs = ["get", "set", "update", "create", "delete", "find", "load", "save", "parse", "render", "build", "apply", "validate", "send", "compute"];
    private static readonly string[] Nouns = ["order", "customer", "invoice", "total", "payment", "address", "item", "cart", "token", "session", "index", "chunk", "vector", "file", "model", "request", "response", "user", "price", "tax"];
    private static readonly string[] Openers = ["how does", "where do we", "which code is used to", "explain how we", "what happens when we"];

    private BenchmarkIndex(IndexStorage storage, SymbolGraph graph)
    {
        Storage = storage;
        Graph = graph;
    }

    public IndexStorage Storage { get; }

    public SymbolGraph Graph { get; }

    public static async Task<BenchmarkIndex> BuildAsync(string path, string? modelId, Action<string> report)
    {
        var random = new Random(20261015);
        var build = Stopwatch.StartNew();
        var storage = await IndexStorage.OpenAsync(new EncryptedDatabaseOptions { DatabasePath = path }, IndexSchema.Schema);
        var fileIds = await AddFilesAsync(storage);
        var symbols = new List<(long FileId, string Name, int Start, int End)>(Chunks);
        var definitions = new List<SymbolDefinition>(Chunks + (Chunks / ChunksPerFile));
        for (var start = 0; start < fileIds.Length; start += FilesPerBatch)
        {
            var batch = new List<FileChunks>(FilesPerBatch);
            for (var f = start; f < start + FilesPerBatch; f++)
            {
                var file = FilePath(f);
                definitions.Add(new SymbolDefinition(file, "Type" + f, SymbolKind.Class, "Ns", 1, ChunksPerFile * 10, "class Type" + f, LanguageIds.CSharp));
                var chunks = new List<NewChunk>(ChunksPerFile);
                for (var c = 0; c < ChunksPerFile; c++)
                {
                    var (method, text) = RandomMethod(random);
                    var line = (c * 10) + 1;
                    var breadcrumb = string.Create(CultureInfo.InvariantCulture, $"{file} > Ns > Type{f} > {method}");
                    chunks.Add(new NewChunk(line, line + 8, "member", breadcrumb, text, text.Length / 4) { Embedding = Hashing.Embed(breadcrumb + "\n" + text) });
                    symbols.Add((fileIds[f], method, line + 1, line + 8));
                    definitions.Add(new SymbolDefinition(file, method, SymbolKind.Method, "Ns.Type" + f, line + 1, line + 8, method + "(int id)", LanguageIds.CSharp, ordinal: c));
                }

                batch.Add(new FileChunks(fileIds[f], chunks));
            }

            await storage.Chunks.ReplaceFilesChunksAsync(batch);
        }

        await AddSymbolsAsync(storage, symbols);
        if (modelId is not null)
        {
            await storage.Database.WriteAsync(async (connection, transaction, token) =>
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "INSERT OR REPLACE INTO meta (key, value) VALUES ($key, $value)";
                command.Parameters.AddWithValue("$key", StorageRetrievalIndex.EmbeddingModelMetaKey);
                command.Parameters.AddWithValue("$value", modelId);
                await command.ExecuteNonQueryAsync(token);
            });
        }

        var graph = SymbolGraphBuilder.Build(definitions, []);
        var cache = Stopwatch.StartNew();
        await storage.WarmVectorCacheAsync();
        report($"Index: {Chunks:N0} chunks, {symbols.Count:N0} symbols, graph of {graph.Definitions.Count:N0} definitions, built in {build.Elapsed.TotalSeconds:F1} s; vector cache loaded in {cache.Elapsed.TotalMilliseconds:F0} ms");
        return new BenchmarkIndex(storage, graph);
    }

    public static async Task<Measurement> MeasureAsync(BenchmarkIndex index, TimedEmbedder embedder, Action<string> report)
    {
        var flags = new TestIndex.AllOn();
        var catalog = new FixedCatalog(new CatalogIndex(IndexId, Root, index.Storage, index.Graph));
        var retrieval = new RetrievalService(catalog, flags, embedder);
        var retriever = new CodeContextRetriever(retrieval, catalog, flags);
        var options = new ContextAssemblerOptions();
        var tokens = Pillion.ModelGateway.TokenCounter.Default;
        var assembler = new ContextAssembler(tokens, options, [new RetrievalContextSource(retriever), new RepoMapContextSource(retriever, tokens, options)]);
        var target = new AssemblyTarget("qwen2.5-coder:7b", 32768, true, PromptTemplates.Default.ChatSystemPrompt, []);
        var random = new Random(7);

        var times = new List<double>(Requests);
        var embeds = new List<double>(Requests);
        double chunks = 0, prompt = 0;
        var withMap = 0;
        var degraded = 0;
        for (var i = 0; i < Requests + WarmUp; i++)
        {
            var file = FilePath(random.Next(Chunks / ChunksPerFile));
            var request = new ChatRequest
            {
                RequestId = "bench-" + i.ToString(CultureInfo.InvariantCulture),
                Messages = [new ChatMessage { Role = ChatRole.User, Content = Question(random, withSymbol: i % 2 == 0) }],
                CurrentFile = Path.Combine(Root, file.Replace('/', '\\')),
                CurrentFileRegion = "public void Run()\n{\n    var total = Compute();\n}\n",
            };

            embedder.TakeTimes();
            var watch = Stopwatch.StartNew();
            var assembled = await assembler.AssembleAsync(request, target, null, CancellationToken.None);
            var elapsed = watch.Elapsed.TotalMilliseconds;
            var embedTime = embedder.TakeTimes().Sum();
            if (i < WarmUp)
            {
                continue;
            }

            times.Add(elapsed);
            embeds.Add(embedTime);
            chunks += assembled.SentChunks.Count;
            prompt += assembled.PromptTokens;
            withMap += assembled.Sections.Any(s => s.Kind == PromptSectionKind.RepoMap) ? 1 : 0;
            degraded += assembled.SentChunks.Count == 0 ? 1 : 0;
        }

        // Degraded searches, counted again by a direct search per question (the assembler does not report them).
        var probe = await retrieval.RetrieveAsync(new RetrievalQuery { Text = Question(random, true), MaxResults = 8 }, [], CancellationToken.None);
        report($"A direct search after the run: lexical {probe.LexicalMs:F1} ms, vector {probe.VectorMs:F1} ms, symbol {probe.SymbolMs:F1} ms, fusion {probe.FusionMs:F1} ms, total {probe.TotalMs:F1} ms, degraded {probe.Degraded}");
        times.Sort();
        embeds.Sort();
        return new Measurement(Percentile(times, 0.50), Percentile(times, 0.95), times[^1], embeds.Average(), Percentile(embeds, 0.95), chunks / Requests, withMap, prompt / Requests, degraded + (probe.Degraded ? 1 : 0));
    }

    public ValueTask DisposeAsync() => Storage.DisposeAsync();

    private static string FilePath(int f) => string.Create(CultureInfo.InvariantCulture, $"src/Part{f % 20}/File{f}.cs");

    private static double Percentile(List<double> sorted, double p) => sorted[Math.Clamp((int)Math.Ceiling(p * sorted.Count) - 1, 0, sorted.Count - 1)];

    private static async Task<long[]> AddFilesAsync(IndexStorage storage)
    {
        var fileIds = new long[Chunks / ChunksPerFile];
        await storage.Database.WriteAsync(async (connection, transaction, token) =>
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO files (path, language, partition, hash, size, mtime, indexed_at) VALUES ($path, 'csharp', $partition, 0, 0, 0, 0) RETURNING id";
            var path = command.Parameters.Add("$path", SqliteType.Text);
            var partition = command.Parameters.Add("$partition", SqliteType.Text);
            for (var f = 0; f < fileIds.Length; f++)
            {
                path.Value = FilePath(f);
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
        return builder.Append(" for a ").Append(Pick(random, Nouns)).Append('?').ToString();
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

        return (method, builder.Append("    return ").Append(Pick(random, Nouns)).Append(";\n}").ToString());
    }

    private static string MethodName(Random random) => Pick(random, Verbs) + Capitalise(Pick(random, Nouns)) + Capitalise(Pick(random, Nouns));

    private static string Pick(Random random, string[] words) => words[random.Next(words.Length)];

    private static string Capitalise(string word) => char.ToUpperInvariant(word[0]) + word[1..];

    public sealed record Measurement(double P50, double P95, double Max, double EmbedMean, double EmbedP95, double Chunks, int WithMap, double PromptTokens, int Degraded);

    /// <summary>A feature-hashing embedder (bag of identifier parts into 384 int8 dimensions), microseconds a query.</summary>
    internal sealed class Hashing : IQueryEmbedder
    {
        private const int Dimensions = 384;

        public Task<QueryEmbedding> EmbedQueryAsync(string text, string? modelId, int? dimensions, CancellationToken cancellationToken) =>
            Task.FromResult(new QueryEmbedding(Embed(text), modelId ?? "test/hashing"));

        public static sbyte[] Embed(string text)
        {
            var values = new double[Dimensions];
            foreach (var term in IdentifierSplitter.Split(text))
            {
                var hash = 2166136261u;
                foreach (var c in term.ToLowerInvariant())
                {
                    hash = (hash ^ c) * 16777619u;
                }

                values[(int)(hash % Dimensions)] += (hash & 0x8000_0000) == 0 ? 1 : -1;
            }

            var max = values.Max(Math.Abs);
            var vector = new sbyte[Dimensions];
            if (max == 0)
            {
                vector[0] = 1;
                return vector;
            }

            for (var i = 0; i < Dimensions; i++)
            {
                vector[i] = (sbyte)Math.Clamp(Math.Round(values[i] / max * 127), -127, 127);
            }

            return vector;
        }
    }

    private sealed class FixedCatalog(CatalogIndex index) : IIndexCatalog
    {
        public Task<IReadOnlyList<CatalogIndex>> GetIndexesAsync(IReadOnlyList<string> roots, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<CatalogIndex>>([index]);
    }
}
