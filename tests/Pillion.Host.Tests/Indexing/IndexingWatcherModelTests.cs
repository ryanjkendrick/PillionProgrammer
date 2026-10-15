using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Pillion.Common.Settings;
using Pillion.Contracts;
using Pillion.Host.Indexing;
using Xunit.Abstractions;

namespace Pillion.Host.Tests.Indexing;

/// <summary>
/// The Gate 2 figures of work item 2.10, measured: one saved file searchable within 500 ms (the bundled model on the CPU,
/// a 200-line C# file), and a branch switch touching 500 files swept within 30 s (a generated git repository with two
/// branches; the fake embedder for the pipeline's own part, the bundled model for the real figure). The model and
/// benchmark tests carry <c>Category=Model</c> or <c>Category=Benchmark</c>, so CI skips them; the gate script runs them.
/// </summary>
public sealed class IndexingWatcherModelTests(ITestOutputHelper output) : IDisposable
{
    private readonly IndexingWorkspace _workspace = new();

    public void Dispose() => _workspace.Dispose();

    [Fact]
    [Trait("Category", "Model")]
    public async Task SaveToSearchable_ATwoHundredLineCSharpFile_WithTheBundledModelOnTheCpu()
    {
        Fixtures.Copy("shop", _workspace.Repository);
        var pricing = _workspace.File("src/Orders/Pricing.cs");
        File.WriteAllText(pricing, IndexingWatcherTests.LargeCSharpFile("Shop.Orders", "Pricing", 28, "Price"));
        Fixtures.Age(_workspace.Repository);
        var lines = File.ReadAllText(pricing).Count(c => c == '\n');
        await using var gateway = IndexingModelTests.BuildGateway(new PillionSettings());
        var embedder = gateway.GetRequiredService<IIndexEmbedder>();
        // PILLION_SAVE_BATCH compares save batch sizes; the default otherwise.
        var batch = int.TryParse(Environment.GetEnvironmentVariable("PILLION_SAVE_BATCH"), CultureInfo.InvariantCulture, out var b) ? b : new IndexingOptions().SaveEmbeddingBatchSize;
        var watching = IndexingWatcherTests.WatchingOptions();
        var options = new IndexingOptions
        {
            MinimumRefreshInterval = watching.MinimumRefreshInterval,
            PowerPollInterval = watching.PowerPollInterval,
            MemoryPollInterval = watching.MemoryPollInterval,
            ReaderPollInterval = watching.ReaderPollInterval,
            SaveEmbeddingBatchSize = batch,
        };
        await using var harness = new IndexingHarness(_workspace.Data, embedder, options);
        await harness.StartAsync(_workspace.Repository);
        await harness.WaitForIdleAsync(TimeSpan.FromMinutes(5));
        var chunks = (await harness.Root.Storage.Chunks.ListFilesAsync()).Single(f => f.Path == "src/Orders/Pricing.cs").Chunks;

        // A typical save: one method edited (the other chunks keep their vectors).
        var edits = new List<double>();
        for (var i = 0; i < 6; i++)
        {
            var text = File.ReadAllText(pricing).Replace($"return discounted > {i} ? discounted : {i};", $"return discounted > {i} ? discounted - {i + 1} : {i}; // edit {i}", StringComparison.Ordinal);
            edits.Add(await SaveAsync(harness, pricing, text));
        }

        // A save where every chunk is new: the whole 200-line file rewritten (or a new file of that size).
        var rewrites = new List<double>();
        var partial = 0;
        var withVectors = new List<string>();
        for (var i = 0; i < 4; i++)
        {
            var path = _workspace.File($"src/Orders/Rewritten{i}.cs");
            rewrites.Add(await SaveAsync(harness, path, IndexingWatcherTests.LargeCSharpFile("Shop.Orders", $"Rewritten{i}", 28, $"Rewrite{i}x")));
            var stats = harness.Service.LastSaveStatistics!;
            partial += stats.PartialFiles;
            withVectors.Add(string.Create(CultureInfo.InvariantCulture, $"{stats.SavedChunksWithVectors}/{stats.SavedChunks}, all after {stats.SavedAllVectorsAfter?.TotalMilliseconds:0} ms"));
        }

        var text2 = string.Create(
            CultureInfo.InvariantCulture,
            $"Save to searchable, bundled bge-small int8 on the CPU, {lines}-line C# file ({chunks} chunks), save batches of {batch}: "
            + $"one method edited: {Describe(edits)}; every chunk new: {Describe(rewrites)}, {partial} of {rewrites.Count} stored with part of their vectors within the budget (chunks with vectors when searchable: {string.Join(", ", withVectors)}); "
            + $"{Environment.ProcessorCount} logical processors.");
        output.WriteLine(text2);
        Console.WriteLine(text2);
        Assert.All(edits.Concat(rewrites), ms => Assert.True(ms < 5000, text2));
    }

    [Fact]
    [Trait("Category", "Benchmark")]
    public async Task BranchSwitch_FiveHundredFiles_WithTheFakeEmbedder_IsThePipelinesOwnCost()
    {
        if (await GitRepository.TryCreateAsync(_workspace.Repository, totalFiles: 2000, changedFiles: 500, output) is not { } git)
        {
            return;
        }

        await using var harness = new IndexingHarness(_workspace.Data, new FakeEmbedder(), new IndexingOptions());
        await harness.StartAsync(_workspace.Repository);
        await harness.WaitForIdleAsync(TimeSpan.FromMinutes(10));

        var results = new List<string>();
        foreach (var (branch, expected) in new[] { ("feature-small", 500), ("main", 500), ("feature-rewrite", 500) })
        {
            var (elapsed, text) = await SwitchAsync(harness, git, branch, expected);
            results.Add(text);
            Assert.True(elapsed < TimeSpan.FromSeconds(30), text);
        }

        var summary = "Branch switch, 2,000-file generated repository, fake embedder: " + string.Join(" | ", results);
        output.WriteLine(summary);
        Console.WriteLine(summary);
    }

    [Fact]
    [Trait("Category", "Model")]
    [Trait("Category", "Benchmark")]
    public async Task BranchSwitch_FiveHundredFiles_WithTheBundledModelOnTheCpu()
    {
        if (await GitRepository.TryCreateAsync(_workspace.Repository, totalFiles: 600, changedFiles: 500, output) is not { } git)
        {
            return;
        }

        await using var gateway = IndexingModelTests.BuildGateway(new PillionSettings());
        var embedder = gateway.GetRequiredService<IIndexEmbedder>();
        await using var harness = new IndexingHarness(_workspace.Data, embedder, new IndexingOptions());
        var initial = Stopwatch.StartNew();
        await harness.StartAsync(_workspace.Repository);
        await harness.WaitForIdleAsync(TimeSpan.FromMinutes(30));
        output.WriteLine($"Initial index of {harness.Service.LastRunStatistics!.FilesIndexed} files, {harness.Service.LastRunStatistics.Chunks} chunks: {initial.Elapsed.TotalSeconds:0.0} s.");

        var results = new List<string>();
        foreach (var (branch, expected) in new[] { ("feature-small", 500), ("main", 500), ("feature-rewrite", 500) })
        {
            var (_, text) = await SwitchAsync(harness, git, branch, expected);
            results.Add(text);
            output.WriteLine(text);
        }

        var summary = "Branch switch, 600-file generated repository, bundled bge-small int8 on the CPU: " + string.Join(" | ", results);
        output.WriteLine(summary);
        Console.WriteLine(summary);
    }

    private static string Describe(List<double> milliseconds)
    {
        var sorted = milliseconds.Order().ToList();
        return string.Create(CultureInfo.InvariantCulture, $"median {sorted[sorted.Count / 2]:0} ms, max {sorted[^1]:0} ms (each: {string.Join(", ", milliseconds.Select(m => m.ToString("0", CultureInfo.InvariantCulture)))} ms)");
    }

    /// <summary>Writes a file, relays the save, and returns the indexer's save-to-searchable time once the run is done.</summary>
    private static async Task<double> SaveAsync(IndexingHarness harness, string path, string text)
    {
        var before = harness.Service.LastSaveStatistics;
        File.WriteAllText(path, text);
        harness.Service.NotifySaved(path);
        await IndexingWatcherTests.UntilAsync(() => !ReferenceEquals(harness.Service.LastSaveStatistics, before), "the save to be indexed", TimeSpan.FromMinutes(2));
        var elapsed = harness.Service.LastSaveToSearchable!.Value.TotalMilliseconds;

        // Let the watcher's own (no-op) event for the file pass before the next save.
        await Task.Delay(800);
        await IndexingWatcherTests.UntilAsync(() => harness.Service.GetStatus().Phase == IndexPhase.Idle, "the run to finish", TimeSpan.FromMinutes(2));
        return elapsed;
    }

    /// <summary>Checks out a branch with git and waits until the sweep it causes has finished.</summary>
    private async Task<(TimeSpan Elapsed, string Text)> SwitchAsync(IndexingHarness harness, GitRepository git, string branch, int expectedFiles)
    {
        // Five of the changed files are open in Visual Studio: they are indexed first.
        harness.Service.SetOpenDocuments([.. Enumerable.Range(0, 5).Select(i => Path.Combine(_workspace.Repository, "src", "Module00", $"Type{i:D4}.cs"))]);
        var full = harness.Service.FullRuns;
        var started = Stopwatch.GetTimestamp();
        var watch = Stopwatch.StartNew();
        await git.RunAsync("checkout", "-q", branch);
        var checkout = watch.Elapsed;
        await IndexingWatcherTests.UntilAsync(() => harness.Service.FullRuns > full, $"the sweep after checking out {branch}", TimeSpan.FromMinutes(30));
        await IndexingWatcherTests.UntilAsync(() => harness.Service.GetStatus().Phase == IndexPhase.Idle, "the index to be idle", TimeSpan.FromMinutes(30));
        var elapsed = watch.Elapsed;
        var stats = harness.Service.LastRunStatistics!;
        var embedded = stats.Chunks - stats.ChunksReused;
        var open = stats.PrioritySearchableAt is { } at ? Stopwatch.GetElapsedTime(started, at).TotalSeconds : double.NaN;
        var text = string.Create(
            CultureInfo.InvariantCulture,
            $"{branch}: {stats.FilesIndexed} files changed (expected {expectedFiles}), {elapsed.TotalSeconds:0.0} s from the checkout's start to idle "
            + $"(git checkout {checkout.TotalSeconds:0.0} s, sweep {stats.SweepTime.TotalSeconds:0.0} s, embedding {stats.EmbeddingTime.TotalSeconds:0.0} s in {stats.EmbeddingBatches} batches, run {stats.Duration.TotalSeconds:0.0} s); "
            + $"{stats.Chunks} chunks stored, {embedded} embedded, {stats.ChunksReused} reused; the {stats.PriorityFiles} open documents searchable {open:0.0} s after the checkout's start");
        output.WriteLine(text);
        Assert.Equal(expectedFiles, stats.FilesIndexed);
        return (elapsed, text);
    }

    /// <summary>A generated git repository with a main branch and two branches that differ from it in the same files.</summary>
    private sealed class GitRepository(string path)
    {
        /// <summary>Creates the repository: <paramref name="totalFiles"/> C# files of about 100 lines on <c>main</c>; <c>feature-small</c> edits two methods in each of <paramref name="changedFiles"/> of them, <c>feature-rewrite</c> rewrites those files whole. Returns <see langword="null"/> when git is not installed.</summary>
        public static async Task<GitRepository?> TryCreateAsync(string path, int totalFiles, int changedFiles, ITestOutputHelper output)
        {
            var git = new GitRepository(path);
            try
            {
                await git.RunAsync("init", "-q", "-b", "main");
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                output.WriteLine("git is not available; the branch-switch measurement did not run: " + ex.Message);
                return null;
            }

            await git.RunAsync("config", "user.name", "Pillion Tests");
            await git.RunAsync("config", "user.email", "tests@example.invalid");
            await git.RunAsync("config", "core.autocrlf", "false");
            for (var i = 0; i < totalFiles; i++)
            {
                git.Write(i, IndexingWatcherTests.LargeCSharpFile($"Generated.Module{i / 50}", $"Type{i:D4}", 12, $"Value{i:D4}m"));
            }

            await git.RunAsync("add", "-A");
            await git.RunAsync("commit", "-q", "-m", "main");

            await git.RunAsync("checkout", "-q", "-b", "feature-small");
            for (var i = 0; i < changedFiles; i++)
            {
                var text = IndexingWatcherTests.LargeCSharpFile($"Generated.Module{i / 50}", $"Type{i:D4}", 12, $"Value{i:D4}m")
                    .Replace("return discounted > 0 ? discounted : 0;", "return discounted > 0 ? discounted : -1; // feature", StringComparison.Ordinal)
                    .Replace("return discounted > 1 ? discounted : 1;", "return discounted > 1 ? Math.Round(discounted, 2) : 1;", StringComparison.Ordinal);
                git.Write(i, text);
            }

            await git.RunAsync("commit", "-q", "-a", "-m", "feature-small");

            await git.RunAsync("checkout", "-q", "main");
            await git.RunAsync("checkout", "-q", "-b", "feature-rewrite");
            for (var i = 0; i < changedFiles; i++)
            {
                git.Write(i, IndexingWatcherTests.LargeCSharpFile($"Generated.Module{i / 50}", $"Rewritten{i:D4}", 12, $"Other{i:D4}r"));
            }

            await git.RunAsync("commit", "-q", "-a", "-m", "feature-rewrite");
            await git.RunAsync("checkout", "-q", "main");
            return git;
        }

        public async Task RunAsync(params string[] arguments)
        {
            var start = new ProcessStartInfo("git")
            {
                WorkingDirectory = path,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
            };
            foreach (var argument in arguments)
            {
                start.ArgumentList.Add(argument);
            }

            using var process = Process.Start(start) ?? throw new InvalidOperationException("git did not start.");
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed ({process.ExitCode}): {await stderr}{await stdout}");
            }
        }

        private void Write(int index, string text)
        {
            var file = Path.Combine(path, "src", $"Module{index / 50:D2}", $"Type{index:D4}.cs");
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, text);
        }
    }
}
