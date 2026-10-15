using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Pillion.Common.Secrets;
using Pillion.Common.Settings;
using Pillion.ContextEngine.Tests.Symbols;
using Pillion.Contracts;
using Pillion.Host.Indexing;
using Pillion.ModelGateway;
using Pillion.ModelGateway.Providers.Bundled;
using Xunit.Abstractions;

namespace Pillion.Host.Tests.Indexing;

/// <summary>
/// The indexing pipeline with the real gateway and the bundled ONNX model on the CPU (DirectML is not forced in tests):
/// Local-only indexing with nothing but the bundled model, and the throughput figures against the Gate 2 targets (100,000
/// lines in 3 minutes; 1,000,000 in 30 minutes), with the fake embedder beside them to isolate the pipeline's own cost.
/// </summary>
public sealed class IndexingModelTests(ITestOutputHelper output) : IDisposable
{
    private const int ThroughputFiles = 100;
    private const int ThroughputLinesPerFile = 1000;

    private readonly IndexingWorkspace _workspace = new();

    public void Dispose() => _workspace.Dispose();

    [Fact]
    [Trait("Category", "Model")]
    public async Task LocalOnly_WithTheBundledModel_IndexesWithoutAnyRequestLeavingTheMachine()
    {
        Fixtures.Copy("shop", _workspace.Repository);
        var settings = new PillionSettings();
        settings.Privacy.LocalOnly = true;
        await using var gateway = BuildGateway(settings);
        var embedder = gateway.GetRequiredService<IIndexEmbedder>();
        var model = embedder.ResolveModel();
        Assert.Equal("bundled/bge-small-en-v1.5", model.ModelId);
        Assert.Equal(512, model.MaxTokens);
        Assert.Equal("bert-wordpiece", model.TokenizerFamily);

        await using var harness = new IndexingHarness(_workspace.Data, embedder, settings: settings);
        await harness.StartAsync(_workspace.Repository);
        var status = await harness.WaitForIdleAsync(TimeSpan.FromMinutes(3));

        var root = Assert.Single(status.Roots);
        Assert.Equal(8, root.IndexedFileCount);
        Assert.Equal("bundled/bge-small-en-v1.5", root.EmbeddingModelId);
        var storage = harness.Root.Storage;
        Assert.All(await storage.Chunks.ListFilesAsync(), f => Assert.Equal(f.Chunks, f.Embedded));

        // Every request went to the bundled model, in process, as the Embedding task class.
        var records = gateway.GetRequiredService<IOutboundLog>().GetRecent();
        Assert.NotEmpty(records);
        Assert.All(records, r =>
        {
            Assert.Equal(BundledEmbeddingProvider.DefaultId, r.ProviderId);
            Assert.True(r.IsLocal);
            Assert.Equal(TaskClass.Embedding, r.TaskClass);
            Assert.Equal(OutboundOutcome.Succeeded, r.Outcome);
        });

        // The real model finds the payment code for a natural-language question.
        var query = await embedder.EmbedAsync(model, ["how is the customer's card charged?"], CancellationToken.None);
        var hits = await storage.Vectors.SearchAsync(query[0].Quantized, 3);
        var files = new List<string>();
        foreach (var hit in hits)
        {
            var chunk = await storage.Chunks.GetChunkAsync(hit.ChunkId);
            files.Add(chunk!.Breadcrumb);
        }

        output.WriteLine("Top hits: " + string.Join(" | ", files));
        Assert.Contains(files, f => f.Contains("Payment", StringComparison.Ordinal) || f.Contains("OrderService", StringComparison.Ordinal));
        output.WriteLine(await DescribeAsync("shop fixture, bundled model", harness, root.ChunkCount));
    }

    [Fact]
    [Trait("Category", "Model")]
    public async Task TheFixtureRepositories_AverageTokensPerChunk_InTheBundledTokenizer()
    {
        Fixtures.Copy("shop", Path.Combine(_workspace.Repository, "shop"));
        Fixtures.Copy("engine", Path.Combine(_workspace.Repository, "engine"));
        await using var gateway = BuildGateway(new PillionSettings());
        var embedder = gateway.GetRequiredService<IIndexEmbedder>();
        await using var harness = new IndexingHarness(_workspace.Data, embedder);
        await harness.StartAsync(_workspace.Repository);
        await harness.WaitForIdleAsync(TimeSpan.FromMinutes(3));

        var stats = harness.Service.LastRunStatistics!;
        var average = stats.Tokens / (double)stats.Chunks;
        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Fixture repositories (shop + Context Engine fixtures): {stats.FilesIndexed} files, {stats.Chunks} chunks, {average:0.0} bge-small WordPiece tokens per chunk on average (cap 512)."));
        Assert.InRange(average, 5, 512);
    }

    [Fact]
    [Trait("Category", "Model")]
    [Trait("Category", "Benchmark")]
    public async Task Throughput_OneHundredThousandLines_WithTheBundledModelOnTheCpu()
    {
        var lines = GenerateRepository();
        await using var gateway = BuildGateway(new PillionSettings());
        var embedder = gateway.GetRequiredService<IIndexEmbedder>();
        await using var harness = new IndexingHarness(_workspace.Data, embedder, new IndexingOptions());
        var started = Stopwatch.StartNew();
        await harness.StartAsync(_workspace.Repository);
        var status = await harness.WaitForIdleAsync(TimeSpan.FromMinutes(60));
        started.Stop();

        var text = await DescribeAsync($"{lines:N0} generated C# lines, bundled bge-small int8 on the CPU", harness, status.Roots[0].ChunkCount, started.Elapsed);
        output.WriteLine(text);
        Console.WriteLine(text);

        // A generous bound only: 100,000 lines within 30 minutes (the Gate 2 target is 3).
        Assert.True(started.Elapsed < TimeSpan.FromMinutes(30), text);
    }

    [Fact]
    public async Task Throughput_OneHundredThousandLines_WithTheFakeEmbedder_IsThePipelinesOwnCost()
    {
        var lines = GenerateRepository();
        await using var harness = new IndexingHarness(_workspace.Data, new FakeEmbedder(), new IndexingOptions());
        var started = Stopwatch.StartNew();
        await harness.StartAsync(_workspace.Repository);
        var status = await harness.WaitForIdleAsync(TimeSpan.FromMinutes(10));
        started.Stop();

        var text = await DescribeAsync($"{lines:N0} generated C# lines, fake embedder", harness, status.Roots[0].ChunkCount, started.Elapsed);
        output.WriteLine(text);
        Console.WriteLine(text);
        Assert.True(started.Elapsed < TimeSpan.FromMinutes(5), text);
    }

    [Fact]
    [Trait("Category", "Model")]
    [Trait("Category", "Benchmark")]
    public async Task Throughput_ThisRepositorysOwnSource_WithTheBundledModelOnTheCpu()
    {
        // Real code rather than generated code: a copy of this repository's src folder (C#, JSON, XAML, Markdown and the rest).
        var source = FindRepositoryFolder("src");
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            if (relative.Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj"))
            {
                continue;
            }

            var target = _workspace.File(relative.Replace(Path.DirectorySeparatorChar, '/'));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }

        await using var gateway = BuildGateway(new PillionSettings());
        var embedder = gateway.GetRequiredService<IIndexEmbedder>();
        var sampler = new PeakMemorySampler();
        await using var harness = new IndexingHarness(_workspace.Data, embedder, new IndexingOptions(), memory: sampler);
        var started = Stopwatch.StartNew();
        await harness.StartAsync(_workspace.Repository);
        var status = await harness.WaitForIdleAsync(TimeSpan.FromMinutes(60));
        started.Stop();
        output.WriteLine($"Working set sampled before each batch: peak {sampler.PeakBytes / 1048576:N0} MB; batch size at the end {harness.Service.Memory.BatchSize}.");

        var text = await DescribeAsync("this repository's src folder, bundled bge-small int8 on the CPU", harness, status.Roots[0].ChunkCount, started.Elapsed);
        output.WriteLine(text);
        Console.WriteLine(text);
        Assert.True(started.Elapsed < TimeSpan.FromMinutes(30), text);
    }

    private sealed class PeakMemorySampler : IMemorySampler
    {
        private long _peak;

        public long PeakBytes => Interlocked.Read(ref _peak);

        public long WorkingSetBytes()
        {
            var bytes = Environment.WorkingSet;
            long seen;
            while (bytes > (seen = Interlocked.Read(ref _peak)) && Interlocked.CompareExchange(ref _peak, bytes, seen) != seen)
            {
            }

            return bytes;
        }
    }

    private static string FindRepositoryFolder(string name)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "PillionProgrammer.NoVsix.slnf")))
            {
                return Path.Combine(directory.FullName, name);
            }
        }

        throw new DirectoryNotFoundException("The repository root was not found above the test assembly.");
    }

    private static ServiceProvider BuildGateway(PillionSettings settings)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IGatewaySettingsSource>(new StaticGatewaySettingsSource(settings));
        services.AddSingleton<ISecretStore>(new InMemorySecretStore());
        services.AddSingleton(new BundledModelOptions
        {
            InstallModelsDirectory = Path.Combine(AppContext.BaseDirectory, "models"),
            UserModelsDirectory = null,
            ExecutionProvider = BundledExecutionProvider.Cpu,
            IdleUnloadAfter = TimeSpan.Zero,
        });
        services.AddPillionModelGateway();
        services.AddSingleton<IIndexEmbedder>(sp => new GatewayIndexEmbedder(
            sp.GetRequiredService<IModelRouter>(),
            sp.GetRequiredService<IModelGateway>(),
            sp.GetRequiredService<ModelCatalog>(),
            sp.GetRequiredService<BundledModelRuntime>()));
        return services.BuildServiceProvider();
    }

    private static async Task<string> DescribeAsync(string what, IndexingHarness harness, long chunks, TimeSpan? wall = null)
    {
        var stats = harness.Service.LastRunStatistics!;
        var size = await harness.Root.Storage.Chunks.GetSizeAsync();
        var seconds = (wall ?? stats.Duration).TotalSeconds;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{what}: {stats.FilesIndexed} files, {stats.Lines:N0} lines, {chunks:N0} chunks in {seconds:0.0} s "
            + $"= {stats.Lines / seconds * 60:N0} lines/min, {chunks / seconds:0.0} chunks/s; "
            + $"{stats.Tokens / (double)Math.Max(1, stats.Chunks):0.0} tokens/chunk; "
            + $"{(size.Bytes + size.WalBytes) / (double)Math.Max(1, chunks):N0} bytes/chunk on disk (database {size.Bytes:N0} + WAL {size.WalBytes:N0}); "
            + $"embedding {stats.EmbeddingTime.TotalSeconds:0.0} s in {stats.EmbeddingBatches} batches, sweep {stats.SweepTime.TotalSeconds:0.0} s, "
            + $"refresh {stats.RefreshTime.TotalSeconds:0.0} s ({stats.Refreshes}x), first searchable after {stats.FirstSearchable?.TotalSeconds:0.0} s; "
            + $"{Environment.ProcessorCount} logical processors, pipeline threads {harness.Service.Scheduler.MaximumConcurrencyLevel}; "
            + $"test process peak working set {Process.GetCurrentProcess().PeakWorkingSet64 / 1048576:N0} MB.");
    }

    private int GenerateRepository()
    {
        var lines = 0;
        foreach (var (path, text) in SyntheticRepository.Files(ThroughputFiles, ThroughputLinesPerFile))
        {
            var full = _workspace.File(path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, text);
            lines += text.Count(c => c == '\n');
        }

        return lines;
    }
}
