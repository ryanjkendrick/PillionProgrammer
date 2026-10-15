using Pillion.Common.Settings;
using Pillion.ContextEngine;
using Pillion.Contracts;
using Pillion.Host.Indexing;
using Pillion.Indexing.Ownership;
using Pillion.LocalStore.Index;
using Xunit.Abstractions;
using IndexRole = Pillion.Contracts.IndexRole;

namespace Pillion.Host.Tests.Indexing;

/// <summary>
/// The indexing pipeline end to end (work item 2.9) over the fixture repositories, with a deterministic fake embedder and a
/// real encrypted index in a temporary folder: discovery, hashing, chunking, symbols, embedding and storage; lexical and
/// vector search through <see cref="IndexStorage"/>; incremental re-indexing; exclusions; rebuild and delete.
/// </summary>
public sealed class IndexingPipelineTests(ITestOutputHelper output) : IDisposable
{
    private readonly IndexingWorkspace _workspace = new();

    public void Dispose() => _workspace.Dispose();

    [Fact]
    public async Task AFixtureRepository_IsIndexed_AndIsSearchableLexicallyAndByVector()
    {
        Fixtures.Copy("shop", _workspace.Repository);
        File.WriteAllText(_workspace.File(".env"), "API_KEY=not-a-real-key\n");
        await using var harness = new IndexingHarness(_workspace.Data);
        await harness.StartAsync(_workspace.Repository);

        var status = await harness.WaitForIdleAsync();

        var root = Assert.Single(status.Roots);
        Assert.Equal(IndexRole.Writer, root.Role);
        Assert.Equal(8, root.IndexedFileCount); // 3 C#, 2 TypeScript, 2 Python, the README
        Assert.Equal(8, root.TotalFileCount);
        Assert.True(root.ChunkCount >= 8, $"{root.ChunkCount} chunks");
        Assert.Equal(1, root.SecretFileCount);
        Assert.Equal(1, status.ExcludedSecretFileCount);
        Assert.Equal("fake/hashed-words", root.EmbeddingModelId);
        Assert.Equal(ContextEngineVersions.ChunkerVersion, root.ChunkerVersion);
        Assert.NotNull(root.LastFullSweepUtc);
        Assert.True(root.DiskBytes > 0);
        Assert.Null(status.Progress);

        var storage = harness.Root.Storage;
        var lexical = await storage.Chunks.SearchLexicalAsync("PaymentGateway Charge", 5);
        Assert.NotEmpty(lexical);
        var top = await storage.Chunks.GetChunkAsync(lexical[0].ChunkId);
        Assert.Contains("Charge", top!.Text, StringComparison.Ordinal);

        var query = FakeEmbedder.Vector("low stock report threshold inventory quantity");
        var nearest = await storage.Vectors.SearchAsync(query.Quantized, 3);
        Assert.NotEmpty(nearest);
        var chunk = await storage.Chunks.GetChunkAsync(nearest[0].ChunkId);
        Assert.Contains("low_stock_report", chunk!.Breadcrumb + chunk.Text, StringComparison.Ordinal);

        // Every chunk has a vector, and the vectors are cached for search (warmed after the last batch).
        var files = await storage.Chunks.ListFilesAsync();
        Assert.All(files, f => Assert.Equal(f.Chunks, f.Embedded));
        Assert.True(storage.VectorCacheBytes > 0);

        // Symbols with their stable ids, the reference from OrderService to PaymentGateway.Charge, and the meta rows.
        var charge = Assert.Single(await storage.Symbols.FindSymbolsByNameAsync("Charge"));
        var orderService = await harness.Root.Store.Files.GetAsync("src/Orders/OrderService.cs");
        var callers = await storage.Symbols.GetReferencesToAsync(charge.Id);
        Assert.Contains(callers, r => r.FileId == orderService!.Id && r.Kind == "call");
        Assert.Equal(ContextEngineVersions.SymbolsVersion, await storage.Symbols.GetSymbolsVersionAsync());
        Assert.Equal(new[] { "fake/hashed-words", ContextEngineVersions.ChunkerVersion, FakeEmbedder.Dimensions.ToString(System.Globalization.CultureInfo.InvariantCulture), "bert-wordpiece" }, await ReadMetaAsync(storage, "embedding_model", "chunker_version", "vector_dimensions", "embedding_tokenizer"));

        // state.json carries the last full sweep for retention and the Index window.
        var state = IndexStateFile.Read(harness.Root.Store.Folder.StateFile);
        Assert.NotNull(state?.LastFullSweepUtc);

        // The excluded files of the last sweep: the secret-bearing ones first.
        var excluded = harness.Service.ListExcludedFiles(new ExcludedFilesRequest { RootPath = _workspace.Repository });
        Assert.Equal(".env", excluded.Items[0].Path);
        Assert.True(excluded.Items[0].IsSecretBearing);
        Assert.Equal("SecretBearing", excluded.Items[0].Reason);
        Assert.Single(harness.Service.ListExcludedFiles(new ExcludedFilesRequest { RootPath = _workspace.Repository, SecretOnly = true }).Items);

        // Batches of 32 (the last one smaller).
        var embedder = (FakeEmbedder)harness.Embedder;
        Assert.All(embedder.BatchSizes, size => Assert.InRange(size, 1, 32));
        Assert.Equal(root.ChunkCount, embedder.ChunksEmbedded);
        output.WriteLine($"{root.ChunkCount} chunks in {embedder.BatchSizes.Count} batches; average {harness.Service.LastRunStatistics!.Tokens / (double)harness.Service.LastRunStatistics.Chunks:0.0} tokens per chunk");
    }

    [Fact]
    public async Task TheContextEngineFixtures_AreIndexedAsASecondTree()
    {
        Fixtures.Copy("engine", _workspace.Repository);
        await using var harness = new IndexingHarness(_workspace.Data);
        await harness.StartAsync(_workspace.Repository);

        var status = await harness.WaitForIdleAsync();

        var root = Assert.Single(status.Roots);
        Assert.Equal(3, root.IndexedFileCount);
        var languages = (await harness.Root.Store.Files.GetAllAsync()).Select(f => f.Language).Order().ToArray();
        Assert.Equal(["csharp", "python", "typescript"], languages);
        Assert.NotEmpty(await harness.Root.Storage.Chunks.SearchLexicalAsync("Greeter", 3));
        output.WriteLine($"{root.ChunkCount} chunks; average {harness.Service.LastRunStatistics!.Tokens / (double)harness.Service.LastRunStatistics.Chunks:0.0} tokens per chunk");
    }

    [Fact]
    public async Task AnIncrementalRun_ReplacesChangedFiles_RemovesDeletedOnes_AndKeepsEdgesFromOtherFiles()
    {
        Fixtures.Copy("shop", _workspace.Repository);
        Fixtures.Age(_workspace.Repository);
        await using var harness = new IndexingHarness(_workspace.Data);
        await harness.StartAsync(_workspace.Repository);
        await harness.WaitForIdleAsync();
        var storage = harness.Root.Storage;
        var files = harness.Root.Store.Files;
        var orderService = (await files.GetAsync("src/Orders/OrderService.cs"))!;
        var orderServiceChunks = (await storage.Chunks.GetChunksByFileAsync(orderService.Id)).Select(c => c.Id).ToArray();
        var charge = Assert.Single(await storage.Symbols.FindSymbolsByNameAsync("Charge"));

        // Change PaymentGateway (a new method, Charge kept), delete the README, add a file.
        File.WriteAllText(_workspace.File("src/Payments/PaymentGateway.cs"), File.ReadAllText(_workspace.File("src/Payments/PaymentGateway.cs")).Replace(
            "    /// <summary>Charges a card",
            "    /// <summary>Refunds a charge.</summary>\n    public Receipt Refund(string reference) => new(true, reference);\n\n    /// <summary>Charges a card",
            StringComparison.Ordinal));
        File.Delete(_workspace.File("README.md"));
        File.WriteAllText(_workspace.File("src/Orders/Discounts.cs"), "namespace Shop.Orders;\n\npublic static class Discounts\n{\n    public static long Apply(long pence) => pence * 9 / 10;\n}\n");
        var runs = harness.Sender.Sent.Count;
        harness.Service.RequestRun();
        await harness.WaitForAsync(s => s.Phase == IndexPhase.Idle && harness.Sender.Sent.Count > runs && s.Roots[0].IndexedFileCount == 8, what: "the incremental run");
        await harness.WaitForIdleAsync();

        var stats = harness.Service.LastRunStatistics!;
        Assert.Equal(2, stats.FilesIndexed); // PaymentGateway.cs and Discounts.cs
        Assert.Equal(1, stats.FilesDeleted);
        Assert.Equal(0, stats.FilesSymbolsOnly); // the session already holds every file's symbols
        Assert.Null(await files.GetAsync("README.md")); // the README is gone, with its chunks
        Assert.DoesNotContain(await storage.Chunks.ListFilesAsync(), f => f.Path == "README.md");
        Assert.NotEmpty(await storage.Chunks.SearchLexicalAsync("Refund", 5));
        Assert.NotEmpty(await storage.Chunks.SearchLexicalAsync("Discounts", 5));

        // OrderService.cs was not touched: same chunks, and its edge to Charge (whose id is stable) survived the re-index of PaymentGateway.cs.
        Assert.Equal(orderServiceChunks, (await storage.Chunks.GetChunksByFileAsync(orderService.Id)).Select(c => c.Id).ToArray());
        Assert.Equal(charge.Id, Assert.Single(await storage.Symbols.FindSymbolsByNameAsync("Charge")).Id);
        Assert.Contains(await storage.Symbols.GetReferencesToAsync(charge.Id), r => r.FileId == orderService.Id && r.Kind == "call");
    }

    [Fact]
    public async Task ANewSession_RebuildsTheSymbolGraphFromUnchangedFiles_AndKeepsEdgesAcrossAnEdit()
    {
        Fixtures.Copy("shop", _workspace.Repository);
        Fixtures.Age(_workspace.Repository);
        await using (var first = new IndexingHarness(_workspace.Data))
        {
            await first.StartAsync(_workspace.Repository);
            await first.WaitForIdleAsync();
        }

        // The callee's file changes while no host runs; the caller's file does not.
        File.WriteAllText(_workspace.File("src/Payments/PaymentGateway.cs"), File.ReadAllText(_workspace.File("src/Payments/PaymentGateway.cs")) + "\n// edited\n");
        await using var second = new IndexingHarness(_workspace.Data);
        await second.StartAsync(_workspace.Repository);
        await second.WaitForIdleAsync();

        var stats = second.Service.LastRunStatistics!;
        Assert.Equal(1, stats.FilesIndexed);
        Assert.Equal(7, stats.FilesSymbolsOnly); // read for the graph, not re-chunked or re-embedded
        var storage = second.Root.Storage;
        var charge = Assert.Single(await storage.Symbols.FindSymbolsByNameAsync("Charge"));
        var orderService = (await second.Root.Store.Files.GetAsync("src/Orders/OrderService.cs"))!;
        Assert.Contains(await storage.Symbols.GetReferencesToAsync(charge.Id), r => r.FileId == orderService.Id && r.Kind == "call");
        Assert.Equal(((FakeEmbedder)second.Embedder).ChunksEmbedded, stats.Chunks);
    }

    [Fact]
    public async Task Rebuild_WipesAndIndexesAgain_AndDelete_RemovesTheFolderUntilTheNextRebuild()
    {
        Fixtures.Copy("shop", _workspace.Repository);
        await using var harness = new IndexingHarness(_workspace.Data);
        await harness.StartAsync(_workspace.Repository);
        var before = await harness.WaitForIdleAsync();
        var embedder = (FakeEmbedder)harness.Embedder;
        var embeddedBefore = embedder.ChunksEmbedded;

        harness.Service.Rebuild(_workspace.Repository);
        await harness.WaitForAsync(s => embedder.ChunksEmbedded >= embeddedBefore * 2 && s.Phase == IndexPhase.Idle, what: "the rebuild");
        var after = await harness.WaitForIdleAsync();
        Assert.Equal(before.Roots[0].ChunkCount, after.Roots[0].ChunkCount);
        Assert.Equal(before.Roots[0].IndexId, after.Roots[0].IndexId);

        var listed = Assert.Single(harness.Service.ListIndexes());
        Assert.True(listed.IsOpen);
        Assert.True(listed.IsInUse);
        Assert.Equal(_workspace.Repository, listed.RootPath);

        var folder = harness.Root.Store.Folder.Directory;
        Assert.True(await harness.Service.DeleteIndexAsync(listed.IndexId, CancellationToken.None));
        Assert.False(Directory.Exists(folder));
        Assert.Empty((await harness.WaitForAsync(s => s.Roots.Length == 0)).Roots);
        Assert.False(await harness.Service.DeleteIndexAsync(listed.IndexId, CancellationToken.None));

        // Not indexed again until asked: a rebuild of the root opens a new index.
        harness.Service.Rebuild(_workspace.Repository);
        var rebuilt = await harness.WaitForIdleAsync();
        Assert.Equal(before.Roots[0].IndexedFileCount, rebuilt.Roots[0].IndexedFileCount);
        Assert.True(Directory.Exists(folder));
    }

    [Fact]
    public async Task AFailingEmbedder_IsRetried_ThenTheRunFailsWithTheError_AndTheNextRunCompletes()
    {
        Fixtures.Copy("shop", _workspace.Repository);
        var embedder = new FakeEmbedder { FailNext = 2 };
        await using var harness = new IndexingHarness(_workspace.Data, embedder);
        await harness.StartAsync(_workspace.Repository);
        await harness.WaitForIdleAsync(); // two failures, then the third attempt succeeds

        embedder.FailNext = 100;
        File.WriteAllText(_workspace.File("src/Orders/New.cs"), "namespace Shop.Orders;\n\npublic sealed class Coupon { }\n");
        harness.Service.RequestRun();
        var failed = await harness.WaitForAsync(s => s.Phase == IndexPhase.Failed, what: "the failed run");
        Assert.Contains("failed on purpose", failed.LastError, StringComparison.Ordinal);

        embedder.FailNext = 0;
        harness.Service.RequestRun();
        await harness.WaitForAsync(s => s.Phase == IndexPhase.Idle && s.Roots[0].IndexedFileCount == 9, what: "the recovered run");
    }

    [Fact]
    public async Task TheChunkCap_IsTheModelsInputSize_AndTheChunksAreCountedInItsTokenizer()
    {
        Fixtures.Copy("shop", _workspace.Repository);
        await using var harness = new IndexingHarness(_workspace.Data);
        await harness.StartAsync(_workspace.Repository);
        await harness.WaitForIdleAsync();

        foreach (var file in await harness.Root.Store.Files.GetAllAsync())
        {
            foreach (var chunk in await harness.Root.Storage.Chunks.GetChunksByFileAsync(file.Id))
            {
                Assert.InRange(chunk.TokenCount, 1, 512);
                Assert.Equal(Pillion.ContextEngine.Chunking.TokenCounters.ApproximateWordPiece(chunk.Breadcrumb + "\n" + chunk.Text), chunk.TokenCount);
            }
        }

        Assert.Equal("512", Assert.Single(await ReadMetaAsync(harness.Root.Storage, "chunk_max_tokens")));
    }

    [Fact]
    public void ChunkKinds_HaveStableStoredNames()
    {
        Assert.Equal(
            ["document", "type-header", "member", "statement", "markup", "window"],
            Enum.GetValues<Pillion.ContextEngine.Chunking.ChunkKind>().Select(IndexPass.KindName));
    }

    [Fact]
    public async Task WithTheIndexingFlagOff_NothingIsOpened_AndTurningItOnStartsIndexing()
    {
        Fixtures.Copy("shop", _workspace.Repository);
        await using var harness = new IndexingHarness(_workspace.Data);
        harness.Flags.Disabled[FeatureNames.Indexing] = true;
        await harness.StartAsync(_workspace.Repository);
        await Task.Delay(300);
        Assert.Empty(harness.Service.OpenRoots);
        Assert.Equal(IndexPhase.Idle, harness.Service.GetStatus().Phase);
        Assert.False(Directory.Exists(harness.Paths.IndexDirectory) && Directory.EnumerateDirectories(harness.Paths.IndexDirectory).Any());

        harness.Flags.Disabled.TryRemove(FeatureNames.Indexing, out _);
        harness.Flags.Raise();
        await harness.WaitForIdleAsync();
    }

    private static async Task<string?[]> ReadMetaAsync(IndexStorage storage, params string[] keys) =>
        await storage.Database.ReadAsync(
            (connection, _) =>
            {
                var values = new string?[keys.Length];
                for (var i = 0; i < keys.Length; i++)
                {
                    using var command = connection.CreateCommand();
                    command.CommandText = "SELECT value FROM meta WHERE key = $key";
                    command.Parameters.AddWithValue("$key", keys[i]);
                    values[i] = command.ExecuteScalar() as string;
                }

                return Task.FromResult(values);
            });
}
