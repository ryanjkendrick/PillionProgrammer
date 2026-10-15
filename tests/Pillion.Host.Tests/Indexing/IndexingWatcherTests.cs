using System.Diagnostics;
using System.Globalization;
using Pillion.Contracts;
using Pillion.Host.Indexing;
using Xunit.Abstractions;

namespace Pillion.Host.Tests.Indexing;

/// <summary>
/// The watcher and the incremental updates of work item 2.10, over a real temporary repository with the deterministic fake
/// embedder: saves relayed from Visual Studio, watched edits, deletes and renames, the discovery rules as the event filter,
/// burst coalescing, the git HEAD watch, overflow and failure fallbacks, pauses, and the writer lock.
/// </summary>
public sealed class IndexingWatcherTests(ITestOutputHelper output) : IDisposable
{
    private readonly IndexingWorkspace _workspace = new();

    public void Dispose() => _workspace.Dispose();

    internal static IndexingOptions WatchingOptions(TimeSpan? fallback = null) => new()
    {
        MinimumRefreshInterval = TimeSpan.FromMilliseconds(200),
        PowerPollInterval = TimeSpan.FromMilliseconds(100),
        MemoryPollInterval = TimeSpan.FromMilliseconds(50),
        ReaderPollInterval = TimeSpan.FromMilliseconds(200),
        EmbeddingRetryDelay = TimeSpan.FromMilliseconds(10),
        WatcherFallbackInterval = fallback ?? TimeSpan.FromMinutes(5),
    };

    /// <summary>Waits until a condition holds, polling every few milliseconds.</summary>
    internal static async Task UntilAsync(Func<Task<bool>> condition, string what, TimeSpan? timeout = null)
    {
        var limit = timeout ?? TimeSpan.FromSeconds(30);
        var watch = Stopwatch.StartNew();
        while (!await condition())
        {
            if (watch.Elapsed > limit)
            {
                throw new TimeoutException($"Timed out after {limit.TotalSeconds:0} s waiting for {what}.");
            }

            await Task.Delay(5);
        }
    }

    internal static Task UntilAsync(Func<bool> condition, string what, TimeSpan? timeout = null) =>
        UntilAsync(() => Task.FromResult(condition()), what, timeout);

    [Fact]
    public async Task ASavedFile_IsSearchableLexicallyAndByVector_WithinTheBudget_AndJoinsTheSymbolGraph()
    {
        Fixtures.Copy("shop", _workspace.Repository);
        Fixtures.Age(_workspace.Repository);
        await using var harness = new IndexingHarness(_workspace.Data, options: WatchingOptions());
        await harness.StartAsync(_workspace.Repository);
        await harness.WaitForIdleAsync();
        var storage = harness.Root.Storage;
        await storage.WarmVectorCacheAsync();

        var path = _workspace.File("src/Payments/PaymentGateway.cs");
        const string Method = "    /// <summary>Reconciles a zebracorn ledger entry.</summary>\n    public bool ReconcileZebracorn(string ledgerEntry) => ledgerEntry.Length > 0;\n\n";
        File.WriteAllText(path, File.ReadAllText(path).Replace("    /// <summary>Charges a card", Method + "    /// <summary>Charges a card", StringComparison.Ordinal));
        var query = FakeEmbedder.Vector("Reconciles a zebracorn ledger entry ReconcileZebracorn ledgerEntry").Quantized;

        var watch = Stopwatch.StartNew();
        harness.Service.NotifySaved(path);
        await UntilAsync(async () => (await storage.Chunks.SearchLexicalAsync("ReconcileZebracorn", 3)).Count > 0, "the saved method to be found lexically");
        var lexical = watch.Elapsed;
        await UntilAsync(
            async () =>
            {
                var hits = await storage.Vectors.SearchAsync(query, 1);
                return hits.Count > 0 && (await storage.Chunks.GetChunkAsync(hits[0].ChunkId))?.Text.Contains("ReconcileZebracorn", StringComparison.Ordinal) == true;
            },
            "the saved method to be the nearest vector");
        var vector = watch.Elapsed;
        await UntilAsync(() => harness.Service.LastSaveToSearchable is not null, "the save's figure");

        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Save to searchable (fake embedder): lexical {lexical.TotalMilliseconds:0} ms, vector {vector.TotalMilliseconds:0} ms; the indexer's own figure {harness.Service.LastSaveToSearchable!.Value.TotalMilliseconds:0} ms."));
        Assert.True(vector < TimeSpan.FromMilliseconds(500), $"searchable after {vector.TotalMilliseconds:0} ms");
        Assert.True(harness.Service.LastSaveToSearchable < TimeSpan.FromMilliseconds(500));

        // The in-memory vectors were updated with the commit, not reloaded; unchanged chunks kept their vectors.
        Assert.True(storage.LastVectorDeltaApplied);
        Assert.True(harness.Service.LastSaveStatistics!.ChunksReused > 0);
        Assert.All(await storage.Chunks.ListFilesAsync(), f => Assert.Equal(f.Chunks, f.Embedded));

        // The symbol graph has the new method; OrderService's call to Charge survived the edit.
        await UntilAsync(() => harness.Root.Graph?.Definitions.Any(d => d.Name == "ReconcileZebracorn") == true, "the graph to have the new method");
        var charge = Assert.Single(await storage.Symbols.FindSymbolsByNameAsync("Charge"));
        var orderService = await harness.Root.Store.Files.GetAsync("src/Orders/OrderService.cs");
        Assert.Contains(await storage.Symbols.GetReferencesToAsync(charge.Id), r => r.FileId == orderService!.Id);
    }

    [Fact]
    public async Task AWatchedEdit_IsIndexedAfterTheDebounce_WithoutAFullRun()
    {
        Fixtures.Copy("shop", _workspace.Repository);
        Fixtures.Age(_workspace.Repository);
        await using var harness = new IndexingHarness(_workspace.Data, options: WatchingOptions());
        await harness.StartAsync(_workspace.Repository);
        await harness.WaitForIdleAsync();
        var fullRuns = harness.Service.FullRuns;

        File.AppendAllText(_workspace.File("tools/report.py"), "\n\ndef quarterly_wombat_summary(rows):\n    return len(rows)\n");

        var storage = harness.Root.Storage;
        await UntilAsync(async () => (await storage.Chunks.SearchLexicalAsync("quarterly_wombat_summary", 3)).Count > 0, "the watched edit to be indexed");
        Assert.Equal(fullRuns, harness.Service.FullRuns);
        Assert.True(harness.Service.IncrementalRuns >= 1);
        Assert.Equal(1, harness.Service.LastIncrementalStatistics!.FilesIndexed);
    }

    [Fact]
    public async Task ADelete_RemovesTheFilesChunksVectorsSymbolsAndEdges()
    {
        Fixtures.Copy("shop", _workspace.Repository);
        Fixtures.Age(_workspace.Repository);
        await using var harness = new IndexingHarness(_workspace.Data, options: WatchingOptions());
        await harness.StartAsync(_workspace.Repository);
        await harness.WaitForIdleAsync();
        var storage = harness.Root.Storage;
        var files = harness.Root.Store.Files;
        var gateway = (await files.GetAsync("src/Payments/PaymentGateway.cs"))!;
        var chunkIds = (await storage.Chunks.GetChunksByFileAsync(gateway.Id)).Select(c => c.Id).ToHashSet();
        var charge = Assert.Single(await storage.Symbols.FindSymbolsByNameAsync("Charge"));
        Assert.NotEmpty(await storage.Symbols.GetReferencesToAsync(charge.Id));
        await storage.WarmVectorCacheAsync();

        File.Delete(_workspace.File("src/Payments/PaymentGateway.cs"));

        await UntilAsync(async () => await files.GetAsync("src/Payments/PaymentGateway.cs") is null, "the deleted file's row to go");
        await UntilAsync(() => harness.Service.GetStatus().Phase == IndexPhase.Idle, "the run to finish");
        Assert.Empty(await storage.Symbols.FindSymbolsByNameAsync("Charge"));
        Assert.Empty(await storage.Symbols.GetReferencesToAsync(charge.Id));
        Assert.Empty(await storage.Chunks.GetChunksByFileAsync(gateway.Id));
        Assert.DoesNotContain(await storage.Chunks.SearchLexicalAsync("PaymentGateway Charge cardToken", 50), h => chunkIds.Contains(h.ChunkId));
        var nearest = await storage.Vectors.SearchAsync(FakeEmbedder.Vector("Charges a card and returns the processor's receipt").Quantized, 50);
        Assert.DoesNotContain(nearest, h => chunkIds.Contains(h.ChunkId));
        Assert.True(storage.LastVectorDeltaApplied);
        Assert.Equal(await storage.Chunks.CountChunksAsync(), (await storage.Vectors.SearchAsync(FakeEmbedder.Vector("anything").Quantized, 4096)).Count);
        await UntilAsync(() => harness.Root.Graph?.Definitions.All(d => d.Name != "Charge") == true, "the graph to drop Charge");
        Assert.DoesNotContain(harness.Root.Graph!.Edges, e => e.ToSymbolId == charge.Id);
        Assert.Equal(7, harness.Service.GetStatus().Roots[0].TotalFileCount);
    }

    [Fact]
    public async Task ARename_MovesTheChunksVectorsAndSymbolsToTheNewPath()
    {
        Fixtures.Copy("shop", _workspace.Repository);
        Fixtures.Age(_workspace.Repository);
        await using var harness = new IndexingHarness(_workspace.Data, options: WatchingOptions());
        await harness.StartAsync(_workspace.Repository);
        await harness.WaitForIdleAsync();
        var storage = harness.Root.Storage;
        var files = harness.Root.Store.Files;
        var embedded = ((FakeEmbedder)harness.Embedder).ChunksEmbedded;

        Directory.CreateDirectory(_workspace.File("src/Billing"));
        File.Move(_workspace.File("src/Payments/PaymentGateway.cs"), _workspace.File("src/Billing/CardGateway.cs"));

        await UntilAsync(async () => await files.GetAsync("src/Billing/CardGateway.cs") is not null && await files.GetAsync("src/Payments/PaymentGateway.cs") is null, "the rename to be indexed");
        await UntilAsync(() => harness.Service.GetStatus().Phase == IndexPhase.Idle, "the run to finish");
        var moved = (await files.GetAsync("src/Billing/CardGateway.cs"))!;
        var chunks = await storage.Chunks.GetChunksByFileAsync(moved.Id);
        Assert.NotEmpty(chunks);
        Assert.All(chunks, c => Assert.StartsWith("src/Billing/CardGateway.cs", c.Breadcrumb, StringComparison.Ordinal));
        Assert.All(await storage.Chunks.ListFilesAsync(), f => Assert.Equal(f.Chunks, f.Embedded));
        Assert.Equal("src", moved.Partition);
        var charge = Assert.Single(await storage.Symbols.FindSymbolsByNameAsync("Charge"));
        Assert.Equal(moved.Id, charge.FileId);
        var orderService = (await files.GetAsync("src/Orders/OrderService.cs"))!;
        await UntilAsync(async () => (await storage.Symbols.GetReferencesToAsync(charge.Id)).Any(r => r.FileId == orderService.Id), "OrderService's call to reach the moved Charge");
        Assert.True(((FakeEmbedder)harness.Embedder).ChunksEmbedded > embedded);
    }

    [Fact]
    public async Task ExcludedPaths_GenerateNoWork()
    {
        Fixtures.Copy("shop", _workspace.Repository);
        File.WriteAllText(_workspace.File(".gitignore"), "*.log\nartifacts/\n");
        Fixtures.Age(_workspace.Repository);
        await using var harness = new IndexingHarness(_workspace.Data, options: WatchingOptions());
        await harness.StartAsync(_workspace.Repository);
        await harness.WaitForIdleAsync();
        var watcher = harness.Root.Watcher!;
        var (incremental, full, seen) = (harness.Service.IncrementalRuns, harness.Service.FullRuns, watcher.EventsSeen);

        foreach (var path in new[] { "bin/Debug/App.dll", "obj/project.assets.json", "src/Orders/bin/Release/x.cs", "web/node_modules/pad/index.js", ".vs/state.json", "build.log", "artifacts/out.txt", "src/Orders/Order.g.cs", ".env", "web/app.min.js", ".git/objects/ab/cdef" })
        {
            var full2 = _workspace.File(path);
            Directory.CreateDirectory(Path.GetDirectoryName(full2)!);
            File.WriteAllText(full2, "generated");
        }

        await UntilAsync(() => watcher.EventsSeen > seen, "the watcher to see the writes");
        await Task.Delay(1500);
        Assert.Equal(0, watcher.Pending);
        Assert.Equal(incremental, harness.Service.IncrementalRuns);
        Assert.Equal(full, harness.Service.FullRuns);
        output.WriteLine($"{watcher.EventsSeen - seen} events seen, {watcher.EventsQueued} queued in the whole test.");
        Assert.Equal(0, watcher.EventsQueued);
    }

    [Fact]
    public async Task ABurstOfTwoHundredWrites_CoalescesIntoOneBatch_ReportedAsStoring()
    {
        Fixtures.Copy("shop", _workspace.Repository);
        Directory.CreateDirectory(_workspace.File("src/Burst"));
        Fixtures.Age(_workspace.Repository);
        var embedder = new FakeEmbedder();
        await using var harness = new IndexingHarness(_workspace.Data, embedder, WatchingOptions());
        await harness.StartAsync(_workspace.Repository);
        await harness.WaitForIdleAsync();
        var watcher = harness.Root.Watcher!;
        var (batches, incremental, full, sent) = (watcher.Batches, harness.Service.IncrementalRuns, harness.Service.FullRuns, harness.Sender.Sent.Count);
        embedder.Delay = TimeSpan.FromMilliseconds(40); // long enough for the status to be sent while it runs

        for (var i = 0; i < 200; i++)
        {
            File.WriteAllText(_workspace.File($"src/Burst/Widget{i:D3}.cs"), $"namespace Shop.Burst;\n\npublic static class Widget{i:D3}\n{{\n    public static int Size{i:D3}() => {i};\n}}\n");
        }

        await UntilAsync(() => harness.Service.IncrementalRuns > incremental, "the burst to be indexed", TimeSpan.FromSeconds(60));
        await UntilAsync(() => harness.Service.GetStatus().Phase == IndexPhase.Idle, "the run to finish");
        Assert.Equal(batches + 1, watcher.Batches);
        Assert.Equal(incremental + 1, harness.Service.IncrementalRuns);
        Assert.Equal(full, harness.Service.FullRuns);
        Assert.Equal(200, harness.Service.LastIncrementalStatistics!.FilesIndexed);
        Assert.Equal(208, harness.Service.GetStatus().Roots[0].IndexedFileCount);
        Assert.Equal(208, harness.Service.GetStatus().Roots[0].TotalFileCount);
        var statuses = harness.Sender.Sent.Skip(sent).Select(s => s.Status).ToList();
        Assert.Contains(statuses, s => s.Phase == IndexPhase.Storing && s.Progress is { FilesTotal: 200 } && s.IsIndexing);
        Assert.DoesNotContain(statuses, s => s.Phase is IndexPhase.Discovering or IndexPhase.Hashing);
    }

    [Fact]
    public async Task AMovedHead_SweepsOnlyTheChangedFiles_AndDropsTheCheckoutsFileEvents()
    {
        Fixtures.Copy("shop", _workspace.Repository);
        const string CommitA = "1111111111111111111111111111111111111111";
        const string CommitB = "2222222222222222222222222222222222222222";
        File.WriteAllText(_workspace.File(".git/HEAD").EnsureDirectory(), "ref: refs/heads/main\n");
        File.WriteAllText(_workspace.File(".git/refs/heads/main").EnsureDirectory(), CommitA + "\n");
        Fixtures.Age(_workspace.Repository);
        await using var harness = new IndexingHarness(_workspace.Data, options: WatchingOptions());
        await harness.StartAsync(_workspace.Repository);
        await harness.WaitForIdleAsync();
        var (incremental, full) = (harness.Service.IncrementalRuns, harness.Service.FullRuns);

        // What "git checkout feature" does: take the index lock, rewrite the tree, move HEAD, let go of the lock.
        File.WriteAllText(_workspace.File(".git/index.lock"), "lock");
        await Task.Delay(100);
        File.AppendAllText(_workspace.File("src/Orders/Order.cs"), "\n// feature branch\n");
        File.AppendAllText(_workspace.File("web/layout.ts"), "\nexport const featureFlag = true;\n");
        File.AppendAllText(_workspace.File("tools/inventory.py"), "\n\ndef feature_only():\n    return 1\n");
        File.WriteAllText(_workspace.File(".git/refs/heads/feature"), CommitB + "\n");
        File.WriteAllText(_workspace.File(".git/HEAD"), "ref: refs/heads/feature\n");
        await Task.Delay(700); // longer than the debounce: the file events are held while git works
        Assert.Equal(incremental, harness.Service.IncrementalRuns);
        File.Delete(_workspace.File(".git/index.lock"));

        await UntilAsync(() => harness.Service.FullRuns > full, "the sweep after the branch switch");
        await harness.WaitForIdleAsync();
        await Task.Delay(800);
        Assert.Equal(3, harness.Service.LastRunStatistics!.FilesIndexed);
        Assert.Equal(incremental, harness.Service.IncrementalRuns);
        Assert.NotEmpty(await harness.Root.Storage.Chunks.SearchLexicalAsync("feature_only", 3));
    }

    [Fact]
    public async Task ABufferOverflow_FallsBackToASweep()
    {
        Fixtures.Copy("shop", _workspace.Repository);
        await using var harness = new IndexingHarness(_workspace.Data, options: WatchingOptions());
        await harness.StartAsync(_workspace.Repository);
        await harness.WaitForIdleAsync();
        var full = harness.Service.FullRuns;

        harness.Root.Watcher!.RaiseError(new InternalBufferOverflowException());

        await UntilAsync(() => harness.Service.FullRuns > full, "the sweep after the overflow");
        Assert.False(harness.Root.Watcher!.IsBroken);
    }

    [Fact]
    public async Task ABrokenWatcher_SweepsPeriodically_AndStartsAgain_WithoutTakingTheHostDown()
    {
        Fixtures.Copy("shop", _workspace.Repository);
        Fixtures.Age(_workspace.Repository);
        await using var harness = new IndexingHarness(_workspace.Data, options: WatchingOptions(fallback: TimeSpan.FromMilliseconds(300)));
        await harness.StartAsync(_workspace.Repository);
        await harness.WaitForIdleAsync();
        var watcher = harness.Root.Watcher!;
        var full = harness.Service.FullRuns;

        watcher.RaiseError(new IOException("The watcher's handle failed."));

        Assert.True(watcher.IsBroken);
        await UntilAsync(() => harness.Service.FullRuns > full, "the sweep after the failure");
        await UntilAsync(() => !watcher.IsBroken, "the watcher to start again");
        await harness.WaitForIdleAsync();

        // Watching again: an edit is picked up without a sweep.
        File.AppendAllText(_workspace.File("web/shapes.ts"), "\nexport const restartedWatcherMarker = 1;\n");
        await UntilAsync(async () => (await harness.Root.Storage.Chunks.SearchLexicalAsync("restartedWatcherMarker", 3)).Count > 0, "the edit after the restart");
    }

    [Fact]
    public async Task WhilePaused_ChangesQueue_AndOnResumeTheyAreAppliedTogether()
    {
        Fixtures.Copy("shop", _workspace.Repository);
        Fixtures.Age(_workspace.Repository);
        await using var harness = new IndexingHarness(_workspace.Data, options: WatchingOptions());
        await harness.StartAsync(_workspace.Repository);
        await harness.WaitForIdleAsync();
        var incremental = harness.Service.IncrementalRuns;

        harness.Activity.Set(IdeActivity.Build);
        File.AppendAllText(_workspace.File("web/shapes.ts"), "\nexport const pausedMarkerOne = 1;\n");
        File.AppendAllText(_workspace.File("web/layout.ts"), "\nexport const pausedMarkerTwo = 2;\n");
        harness.Service.NotifySaved(_workspace.File("web/layout.ts"));
        await UntilAsync(() => harness.Root.HasPendingChanges, "the changes to be queued");
        await Task.Delay(1500);
        Assert.Equal(incremental, harness.Service.IncrementalRuns);
        Assert.Equal(IndexPauseReason.Build, harness.Service.GetStatus().PauseReason);

        harness.Activity.Set(IdeActivity.None);

        await UntilAsync(() => harness.Service.IncrementalRuns > incremental, "the queued changes to be applied");
        Assert.Equal(2, harness.Service.LastIncrementalStatistics!.FilesIndexed);
        Assert.NotEmpty(await harness.Root.Storage.Chunks.SearchLexicalAsync("pausedMarkerOne", 3));
        Assert.NotEmpty(await harness.Root.Storage.Chunks.SearchLexicalAsync("pausedMarkerTwo", 3));
    }

    [Fact]
    public async Task LosingTheWriterLock_StopsTheWatcher_AndTheIndexIsOpenedAgain()
    {
        Fixtures.Copy("shop", _workspace.Repository);
        Fixtures.Age(_workspace.Repository);
        await using var harness = new IndexingHarness(_workspace.Data, options: WatchingOptions());
        await harness.StartAsync(_workspace.Repository);
        await harness.WaitForIdleAsync();
        var before = harness.Root;
        Assert.NotNull(before.Watcher);

        File.Delete(before.Store.Folder.LockFile);
        File.AppendAllText(_workspace.File("web/shapes.ts"), "\nexport const afterLostLock = 1;\n");
        harness.Service.NotifySaved(_workspace.File("web/shapes.ts"));

        await UntilAsync(() => harness.Service.OpenRoots.Count == 1 && !ReferenceEquals(harness.Service.OpenRoots[0], before), "the index to be opened again");
        Assert.Null(before.Watcher);
        var after = harness.Root;
        Assert.True(after.IsWriter); // nobody else owns it, so this host takes it again
        Assert.NotNull(after.Watcher);
        await harness.WaitForIdleAsync();
        await UntilAsync(async () => (await after.Storage.Chunks.SearchLexicalAsync("afterLostLock", 3)).Count > 0, "the change to be indexed by the new owner");
    }

    [Fact]
    public async Task ASaveOutsideTheRoots_OrOfAnExcludedFile_IsIgnored()
    {
        Fixtures.Copy("shop", _workspace.Repository);
        await using var harness = new IndexingHarness(_workspace.Data, options: WatchingOptions());
        await harness.StartAsync(_workspace.Repository);
        await harness.WaitForIdleAsync();

        harness.Service.NotifySaved(Path.Combine(Path.GetTempPath(), "elsewhere.cs"));
        harness.Service.NotifySaved(_workspace.File("bin/Debug/App.cs"));
        harness.Service.NotifySaved(_workspace.File(".env"));
        harness.Service.NotifySaved(null);

        Assert.False(harness.Root.HasPendingChanges);
    }

    [Fact]
    public async Task ASaveOfALargeFile_IsSearchableWithinTheBudget_AndGetsTheRestOfItsVectorsRightAfter()
    {
        Fixtures.Copy("shop", _workspace.Repository);
        Fixtures.Age(_workspace.Repository);
        var embedder = new FakeEmbedder();
        await using var harness = new IndexingHarness(_workspace.Data, embedder, WatchingOptions());
        await harness.StartAsync(_workspace.Repository);
        await harness.WaitForIdleAsync();
        var storage = harness.Root.Storage;

        // A slow model: 100 ms a batch of 8, so a file of 80 new chunks cannot be embedded within 500 ms.
        embedder.Delay = TimeSpan.FromMilliseconds(100);
        var path = _workspace.File("src/Orders/Large.cs");
        File.WriteAllText(path, LargeCSharpFile("Shop.Orders", "Large", 80, "HugeMarker"));
        harness.Service.NotifySaved(path);

        await UntilAsync(() => harness.Service.LastSaveToSearchable is not null, "the large save to be searchable", TimeSpan.FromSeconds(60));
        Assert.True(harness.Service.LastSaveToSearchable < TimeSpan.FromMilliseconds(700), $"{harness.Service.LastSaveToSearchable!.Value.TotalMilliseconds:0} ms");
        Assert.NotEmpty(await storage.Chunks.SearchLexicalAsync("HugeMarker79", 3));

        await UntilAsync(() => harness.Service.GetStatus().Phase == IndexPhase.Idle && harness.Service.IncrementalRuns > 0, "the rest of the vectors", TimeSpan.FromSeconds(60));
        Assert.Equal(1, harness.Service.LastSaveStatistics!.PartialFiles);
        var row = (await harness.Root.Store.Files.GetAsync("src/Orders/Large.cs"))!;
        Assert.NotEqual(0, row.ModifiedUnixMilliseconds);
        var summary = Assert.Single(await storage.Chunks.ListFilesAsync(), f => f.Path == "src/Orders/Large.cs");
        Assert.Equal(summary.Chunks, summary.Embedded);
        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{summary.Chunks} chunks; searchable after {harness.Service.LastSaveToSearchable!.Value.TotalMilliseconds:0} ms with part of the vectors."));
    }

    /// <summary>A C# file with <paramref name="methods"/> documented methods, each naming a marker.</summary>
    internal static string LargeCSharpFile(string ns, string type, int methods, string marker)
    {
        var text = new System.Text.StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"namespace {ns};\n\n/// <summary>Generated for the watcher tests.</summary>\npublic sealed class {type}\n{{\n");
        for (var i = 0; i < methods; i++)
        {
            text.Append(CultureInfo.InvariantCulture, $"    /// <summary>Computes the {marker}{i} value from the order total and the discount rate.</summary>\n");
            text.Append(CultureInfo.InvariantCulture, $"    public decimal {marker}{i}(decimal total, decimal rate)\n    {{\n");
            text.Append(CultureInfo.InvariantCulture, $"        var discounted = total - (total * rate / 100m);\n        return discounted > {i} ? discounted : {i};\n    }}\n\n");
        }

        text.Append("}\n");
        return text.ToString();
    }
}

/// <summary>Small helpers for writing test trees.</summary>
internal static class PathTestExtensions
{
    /// <summary>Creates the folder of a file path and returns the path.</summary>
    public static string EnsureDirectory(this string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return path;
    }
}
