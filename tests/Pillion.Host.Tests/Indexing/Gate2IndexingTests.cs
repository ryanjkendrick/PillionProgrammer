using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
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
/// Skipped unless <c>PILLION_GATE2_INDEX=1</c> when the tests are discovered: <c>tools/gate2/Invoke-Gate2.ps1</c> sets it, with
/// the run's parameters, for each of the generated-repository runs of step d.
/// </summary>
public sealed class Gate2IndexingFactAttribute : FactAttribute
{
    /// <summary>Initializes a new instance of the <see cref="Gate2IndexingFactAttribute"/> class.</summary>
    public Gate2IndexingFactAttribute()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("PILLION_GATE2_INDEX"), "1", StringComparison.Ordinal))
        {
            Skip = "Gate 2 generated-repository run: started by tools/gate2/Invoke-Gate2.ps1 (PILLION_GATE2_INDEX=1).";
        }
    }
}

/// <summary>
/// Gate 2 step d (work item 2.17): a generated repository of 100,000 or 1,000,000 lines indexed by the real indexing pipeline
/// (<see cref="IndexingService"/> composed in-process, as the other indexing tests do, with the real gateway and the bundled
/// <c>bge-small-en-v1.5</c> model), on the CPU or with DirectML, with the real working set governing the memory cap. Writes
/// one JSON result: time, throughput, when the first results were searchable and how much was searchable after the first
/// minute, peak working set, bytes per chunk on disk, tokens per chunk, memory pauses and the execution provider used.
/// </summary>
/// <remarks>
/// Parameters (environment): <c>PILLION_GATE2_LINES</c> (100000 by default), <c>PILLION_GATE2_SOURCE</c> (<c>synthetic</c>:
/// generated C# from <see cref="SyntheticRepository"/>, about 69 tokens a chunk; <c>real</c>: this repository's own <c>src</c>
/// copied as many times as it takes, each copy under its own folder, about 167 tokens a chunk), <c>PILLION_GATE2_EP</c>
/// (<c>cpu</c> or <c>directml</c>), <c>PILLION_GATE2_REPO</c> (the folder to generate into; reused when it already holds the
/// repository), <c>PILLION_GATE2_DATA</c> (the data root of the index; a temporary folder by default, deleted afterwards),
/// <c>PILLION_GATE2_OUT</c> (the JSON file).
/// </remarks>
public sealed class Gate2IndexingTests(ITestOutputHelper output)
{
    [Gate2IndexingFact]
    [Trait("Category", "Model")]
    [Trait("Category", "Benchmark")]
    [Trait("Category", "Gate2")]
    public async Task GeneratedRepository_IsIndexedWithTheBundledModel()
    {
        var lines = int.Parse(Setting("PILLION_GATE2_LINES", "100000"), CultureInfo.InvariantCulture);
        var source = Setting("PILLION_GATE2_SOURCE", "synthetic");
        var ep = Setting("PILLION_GATE2_EP", "cpu");
        using var temp = new TestSupport.TempDirectory();
        var repository = Setting("PILLION_GATE2_REPO", Path.Combine(temp.Path, "repo"));
        var dataRoot = Setting("PILLION_GATE2_DATA", Path.Combine(temp.Path, "data"));
        var outPath = Setting("PILLION_GATE2_OUT", Path.Combine(temp.Path, "gate2-index.json"));
        Directory.CreateDirectory(dataRoot);

        var generation = Stopwatch.StartNew();
        var generated = Generate(repository, source, lines);
        generation.Stop();
        output.WriteLine($"Repository {repository}: {generated.Files:N0} files, {generated.Lines:N0} lines ({source}), ready in {generation.Elapsed.TotalSeconds:0.0} s.");

        await using var gateway = BuildGateway(ep == "directml" ? BundledExecutionProvider.DirectML : BundledExecutionProvider.Cpu);
        var embedder = gateway.GetRequiredService<IIndexEmbedder>();
        var sampler = new PeakSampler();
        await using var harness = new IndexingHarness(dataRoot, embedder, new IndexingOptions(), memory: sampler);
        var clock = Stopwatch.StartNew();
        await harness.StartAsync(repository);

        // While it runs: the time of the first stored chunk and how much was stored (and searchable) at the one-minute mark.
        long? chunksAtOneMinute = null;
        int? filesAtOneMinute = null;
        double? firstChunkSeconds = null;
        var deadline = TimeSpan.FromMinutes(lines >= 500_000 ? 120 : 60);
        IndexStatus status;
        while (true)
        {
            status = harness.Service.GetStatus();
            var stored = status.Roots.Sum(r => r.ChunkCount);
            if (firstChunkSeconds is null && stored > 0)
            {
                firstChunkSeconds = clock.Elapsed.TotalSeconds;
            }

            if (chunksAtOneMinute is null && clock.Elapsed >= TimeSpan.FromMinutes(1))
            {
                chunksAtOneMinute = stored;
                filesAtOneMinute = status.IndexedFileCount;
            }

            if (status.Phase == IndexPhase.Idle && status.Roots.Length > 0 && status.Roots.All(r => r.LastFullSweepUtc is not null) && harness.Service.OpenRoots.Count == status.Roots.Length)
            {
                break;
            }

            Assert.True(clock.Elapsed < deadline, $"Not idle after {deadline.TotalMinutes} minutes: phase {status.Phase}, {status.IndexedFileCount}/{status.TotalFileCount} files.");
            await Task.Delay(250);
        }

        clock.Stop();
        var stats = harness.Service.LastRunStatistics!;
        var root = harness.Root;
        var size = await root.Storage.Chunks.GetSizeAsync();
        var chunks = status.Roots[0].ChunkCount;
        var modelId = embedder.ResolveModel().ModelId;
        var loaded = gateway.GetRequiredService<BundledModelRuntime>().ExecutionProviderOf(modelId.Split('/')[^1]) ?? gateway.GetRequiredService<BundledModelRuntime>().ExecutionProviderOf(modelId);
        var memoryPauses = harness.Sender.Sent.Count(s => s.Status.IsPaused && s.Status.PauseReason == IndexPauseReason.Memory);

        // A lexical and a vector search on the finished index, so the figures describe a working index.
        var probe = await embedder.EmbedAsync(embedder.ResolveModel(), ["where is the item count kept?"], CancellationToken.None);
        var vectorHits = await root.Storage.Vectors.SearchAsync(probe[0].Quantized, 10);

        var seconds = clock.Elapsed.TotalSeconds;
        var result = new JsonObject
        {
            ["test"] = nameof(GeneratedRepository_IsIndexedWithTheBundledModel),
            ["source"] = source,
            ["requestedLines"] = lines,
            ["executionProviderRequested"] = ep,
            ["executionProviderLoaded"] = loaded?.ToString() ?? "unknown",
            ["model"] = modelId,
            ["repository"] = repository,
            ["dataRoot"] = dataRoot,
            ["generationSeconds"] = Math.Round(generation.Elapsed.TotalSeconds, 1),
            ["files"] = stats.FilesIndexed,
            ["lines"] = stats.Lines,
            ["chunks"] = chunks,
            ["seconds"] = Math.Round(seconds, 1),
            ["minutes"] = Math.Round(seconds / 60, 2),
            ["linesPerMinute"] = Math.Round(stats.Lines / seconds * 60),
            ["chunksPerSecond"] = Math.Round(chunks / seconds, 1),
            ["tokensPerChunk"] = Math.Round(stats.Tokens / (double)Math.Max(1, stats.Chunks), 1),
            ["embeddingSeconds"] = Math.Round(stats.EmbeddingTime.TotalSeconds, 1),
            ["embeddingBatches"] = stats.EmbeddingBatches,
            ["sweepSeconds"] = Math.Round(stats.SweepTime.TotalSeconds, 1),
            ["refreshSeconds"] = Math.Round(stats.RefreshTime.TotalSeconds, 1),
            ["refreshes"] = stats.Refreshes,
            ["firstSearchableSeconds"] = stats.FirstSearchable is { } first ? Math.Round(first.TotalSeconds, 1) : null,
            ["firstChunkStoredSeconds"] = firstChunkSeconds is { } fc ? Math.Round(fc, 1) : null,
            ["chunksAtOneMinute"] = chunksAtOneMinute,
            ["filesAtOneMinute"] = filesAtOneMinute,
            ["databaseBytes"] = size.Bytes,
            ["walBytes"] = size.WalBytes,
            ["bytesPerChunk"] = Math.Round(size.Bytes / (double)Math.Max(1, chunks)),
            ["bytesPerChunkWithWal"] = Math.Round((size.Bytes + size.WalBytes) / (double)Math.Max(1, chunks)),
            ["peakWorkingSetMB"] = Math.Round(sampler.PeakBytes / 1048576.0),
            ["processPeakWorkingSetMB"] = Math.Round(Process.GetCurrentProcess().PeakWorkingSet64 / 1048576.0),
            ["memoryCapMB"] = new PillionSettings().Indexing.MaxMemoryMb,
            ["memoryPauses"] = memoryPauses,
            ["batchSizeAtEnd"] = harness.Service.Memory.BatchSize,
            ["pipelineThreads"] = harness.Service.Scheduler.MaximumConcurrencyLevel,
            ["logicalProcessors"] = Environment.ProcessorCount,
            ["vectorHitsForProbe"] = vectorHits.Count,
            ["finishedUtc"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
        };
        var text = result.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        File.WriteAllText(outPath, text);
        output.WriteLine(text);
        Console.WriteLine(text);

        Assert.True(chunks > 0, "Nothing was indexed.");
        Assert.NotEmpty(vectorHits);
    }

    private static string Setting(string name, string fallback)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }

    /// <summary>Writes the repository unless the folder already holds it (a marker file names what it holds).</summary>
    private static (int Files, long Lines) Generate(string repository, string source, int lines)
    {
        var marker = Path.Combine(repository, ".gate2-generated");
        var expected = $"{source} {lines}";
        if (File.Exists(marker) && File.ReadAllText(marker).StartsWith(expected + " ", StringComparison.Ordinal))
        {
            var parts = File.ReadAllText(marker).Split(' ');
            return (int.Parse(parts[2], CultureInfo.InvariantCulture), long.Parse(parts[3], CultureInfo.InvariantCulture));
        }

        if (Directory.Exists(repository))
        {
            Directory.Delete(repository, recursive: true);
        }

        Directory.CreateDirectory(repository);
        var files = 0;
        long written = 0;
        if (source == "real")
        {
            var src = FindRepositoryFolder("src");
            var sources = Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories)
                .Where(f => !Path.GetRelativePath(src, f).Split(Path.DirectorySeparatorChar).Any(p => p is "bin" or "obj"))
                .OrderBy(f => f, StringComparer.Ordinal)
                .ToList();
            for (var copy = 0; written < lines; copy++)
            {
                foreach (var file in sources)
                {
                    var target = Path.Combine(repository, "copy" + copy.ToString("D2", CultureInfo.InvariantCulture), Path.GetRelativePath(src, file));
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(file, target);
                    files++;
                    written += CountLines(file);
                }
            }
        }
        else
        {
            var fileCount = Math.Max(1, lines / 1000);
            foreach (var (path, text) in SyntheticRepository.Files(fileCount, 1000))
            {
                var full = Path.Combine(repository, path.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.WriteAllText(full, text);
                files++;
                written += text.Count(c => c == '\n');
            }
        }

        File.WriteAllText(marker, string.Create(CultureInfo.InvariantCulture, $"{expected} {files} {written}"));
        return (files, written);
    }

    private static long CountLines(string file)
    {
        long count = 0;
        foreach (var b in File.ReadAllBytes(file))
        {
            if (b == (byte)'\n')
            {
                count++;
            }
        }

        return count;
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

    private static ServiceProvider BuildGateway(BundledExecutionProvider executionProvider)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IGatewaySettingsSource>(new StaticGatewaySettingsSource(new PillionSettings()));
        services.AddSingleton<ISecretStore>(new InMemorySecretStore());
        services.AddSingleton(new BundledModelOptions
        {
            InstallModelsDirectory = Path.Combine(AppContext.BaseDirectory, "models"),
            UserModelsDirectory = null,
            ExecutionProvider = executionProvider,
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

    /// <summary>The real working set (it governs the memory cap, as in the host), with its peak.</summary>
    private sealed class PeakSampler : IMemorySampler
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
}
