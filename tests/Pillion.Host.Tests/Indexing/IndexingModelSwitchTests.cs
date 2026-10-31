using Pillion.Common.Settings;
using Pillion.ContextEngine.Retrieval;
using Pillion.Contracts;
using Pillion.Host.Context.Retrieval;
using Pillion.Host.Indexing;
using IndexRole = Pillion.Contracts.IndexRole;

namespace Pillion.Host.Tests.Indexing;

/// <summary>
/// Switching the embedding model (issue #104, work item 3.2, design section 7): the root's previous index stays open
/// read-only and serves searches until the new index's first full sweep completes, both are in the status meanwhile, then
/// the previous one closes and retention retires it. With the fake embedder, whose model id can change between runs.
/// </summary>
public sealed class IndexingModelSwitchTests : IDisposable
{
    private const string FirstModel = "fake/hashed-words";
    private const string SecondModel = "fake/second-model";

    private readonly IndexingWorkspace _workspace = new();

    public void Dispose() => _workspace.Dispose();

    [Fact]
    public async Task SwitchingTheModel_KeepsThePreviousIndexServing_UntilTheNewOnesFirstSweepCompletes_ThenClosesIt()
    {
        Fixtures.Copy("shop", _workspace.Repository);
        var embedder = new FakeEmbedder();
        await using var harness = new IndexingHarness(_workspace.Data, embedder, Options());
        await harness.StartAsync(_workspace.Repository);
        await harness.WaitForIdleAsync();
        var previous = harness.Root;
        var previousId = previous.Store.Identity.Id;
        var catalog = new LiveIndexCatalog(harness.Service, harness.Flags);
        var retrieval = new RetrievalService(catalog, harness.Flags, new FakeQueryEmbedder(), new RetrievalOptions { TimeBudget = TimeSpan.FromSeconds(30) });

        // Pause the pipeline so the new index cannot complete, then switch the model.
        harness.Service.SetUserPause(true);
        embedder.ModelId = SecondModel;
        harness.Settings.Update(new PillionSettings());
        var status = await harness.WaitForAsync(s => s.Roots.Length == 2, what: "both indexes in the status");

        // The status lists the new index as the writer and the previous one as a read-only reader, with its counts.
        var fresh = Assert.Single(status.Roots, r => r.EmbeddingModelId == SecondModel);
        var old = Assert.Single(status.Roots, r => r.EmbeddingModelId == FirstModel);
        Assert.Equal(IndexRole.Writer, fresh.Role);
        Assert.Null(fresh.LastFullSweepUtc);
        Assert.Equal(IndexRole.Reader, old.Role);
        Assert.Equal(previousId, old.IndexId);
        Assert.NotNull(old.LastFullSweepUtc);
        Assert.True(old.ChunkCount > 0);
        Assert.Equal(_workspace.Repository, old.RootPath);
        Assert.Equal(old.RootPath, fresh.RootPath);
        Assert.Equal(fresh.IndexedFileCount, status.IndexedFileCount); // the totals count the root once, by its current index
        Assert.NotEqual(previousId, fresh.IndexId);

        // The pipeline holds the previous instance open, not indexed, and it is the one that serves.
        var current = Assert.Single(harness.Service.OpenRoots);
        Assert.Equal(SecondModel, current.Model.ModelId);
        Assert.Same(previous, Assert.Single(harness.Service.RetiringRoots));
        Assert.True(previous.IsRetiring);
        Assert.Null(previous.Watcher);
        Assert.Same(previous, Assert.Single(harness.Service.ServingRoots));
        var served = Assert.Single(await catalog.GetIndexesAsync([], CancellationToken.None));
        Assert.Equal(previousId, served.IndexId);
        Assert.Same(previous.Storage, served.Storage);
        Assert.NotNull(served.Graph);
        var response = await retrieval.SearchDetailedAsync(new SearchRequest { Query = "charge the card payment gateway", MaxResults = 5 }, CancellationToken.None);
        Assert.Contains(response.Results, r => r.FilePath.EndsWith("PaymentGateway.cs", StringComparison.Ordinal));
        var graph = Assert.Single(await catalog.GetGraphsAsync(CancellationToken.None));
        Assert.NotEmpty(graph.Graph.FindDefinitions("PaymentGateway.Charge"));

        // The new index completes its first full sweep: the previous one closes and the new one serves.
        var embeddedBefore = embedder.ChunksEmbedded;
        harness.Service.SetUserPause(false);
        status = await harness.WaitForIdleAsync();
        var only = Assert.Single(status.Roots);
        Assert.Equal(SecondModel, only.EmbeddingModelId);
        Assert.NotEqual(previousId, only.IndexId);
        Assert.NotNull(only.LastFullSweepUtc);
        Assert.True(only.ChunkCount > 0);
        Assert.Empty(harness.Service.RetiringRoots);
        Assert.Same(harness.Root, Assert.Single(harness.Service.ServingRoots));
        Assert.Equal(only.IndexId, Assert.Single(await catalog.GetIndexesAsync([], CancellationToken.None)).IndexId);
        Assert.True(embedder.ChunksEmbedded > embeddedBefore, "the new index was embedded with the new model");
        Assert.Equal(SecondModel, harness.Root.Model.ModelId);
        response = await retrieval.SearchDetailedAsync(new SearchRequest { Query = "charge the card payment gateway", MaxResults = 5 }, CancellationToken.None);
        Assert.Contains(response.Results, r => r.FilePath.EndsWith("PaymentGateway.cs", StringComparison.Ordinal));

        // The previous index's folder is still on disk, closed, for retention's age rule (or the Index window's Delete).
        Assert.True(Directory.Exists(previous.Store.Folder.Directory));
        Assert.Contains(harness.Service.ListIndexes(), i => i.IndexId == previousId && !i.IsOpen && !i.IsInUse && i.EmbeddingModelId == FirstModel);
        Assert.Contains(harness.Service.ListIndexes(), i => i.IndexId == only.IndexId && i.IsOpen);
    }

    [Fact]
    public async Task SwitchingBack_WhileThePreviousIndexIsStillOpen_ReinstatesIt_WithoutRebuilding()
    {
        Fixtures.Copy("shop", _workspace.Repository);
        var embedder = new FakeEmbedder();
        await using var harness = new IndexingHarness(_workspace.Data, embedder, Options());
        await harness.StartAsync(_workspace.Repository);
        await harness.WaitForIdleAsync();
        var original = harness.Root;
        var runs = harness.Service.FullRuns;
        var embedded = embedder.ChunksEmbedded;

        harness.Service.SetUserPause(true);
        embedder.ModelId = SecondModel;
        harness.Settings.Update(new PillionSettings());
        await harness.WaitForAsync(s => s.Roots.Length == 2, what: "both indexes in the status");
        var incomplete = Assert.Single(harness.Service.OpenRoots);

        // Back to the first model: the previous index is the current one again, and the half-built one closes at once
        // (the current one is complete, so there is nothing to wait for).
        embedder.ModelId = FirstModel;
        harness.Settings.Update(new PillionSettings());
        await harness.WaitForAsync(s => s.Roots.Length == 1 && s.Roots[0].EmbeddingModelId == FirstModel, what: "the first model's index alone");
        harness.Service.SetUserPause(false);
        var status = await harness.WaitForIdleAsync();

        Assert.Same(original, harness.Root);
        Assert.False(original.IsRetiring);
        Assert.NotNull(original.Watcher);
        Assert.Empty(harness.Service.RetiringRoots);
        Assert.Equal(original.Store.Identity.Id, Assert.Single(status.Roots).IndexId);
        Assert.Equal(IndexRole.Writer, status.Roots[0].Role);
        Assert.Equal(embedded, embedder.ChunksEmbedded); // nothing was embedded again: the reinstated index was complete
        Assert.True(harness.Service.FullRuns >= runs + 1, "the reinstated index is swept once, which finds nothing to do");
        Assert.False(incomplete.Store.HoldsWriterLock(), "the half-built index was closed");
    }

    [Fact]
    public async Task AfterAHostRestartMidSwitch_TheCompletePreviousIndexOnDisk_IsAdopted_AndServesUntilTheNewOneCompletes()
    {
        Fixtures.Copy("shop", _workspace.Repository);
        string previousId;
        await using (var first = new IndexingHarness(_workspace.Data, new FakeEmbedder(), Options()))
        {
            await first.StartAsync(_workspace.Repository);
            await first.WaitForIdleAsync();
            previousId = first.Root.Store.Identity.Id;
        }

        // A new session with another model: the previous index is complete and unused on disk.
        var embedder = new FakeEmbedder { ModelId = SecondModel };
        await using var harness = new IndexingHarness(_workspace.Data, embedder, Options());
        harness.Service.SetUserPause(true);
        await harness.StartAsync(_workspace.Repository);
        var status = await harness.WaitForAsync(s => s.Roots.Length == 2, what: "the adopted previous index beside the new one");

        var old = Assert.Single(status.Roots, r => r.IndexId == previousId);
        Assert.Equal(IndexRole.Reader, old.Role);
        Assert.Equal(FirstModel, old.EmbeddingModelId);
        Assert.True(old.ChunkCount > 0);
        var previous = Assert.Single(harness.Service.RetiringRoots);
        Assert.Same(previous, Assert.Single(harness.Service.ServingRoots));
        var catalog = new LiveIndexCatalog(harness.Service, harness.Flags);
        var served = Assert.Single(await catalog.GetIndexesAsync([], CancellationToken.None));
        Assert.Equal(previousId, served.IndexId);
        Assert.Null(served.Graph); // adopted from disk: no symbol graph of this session; retrieval reads the stored symbols
        var retrieval = new RetrievalService(catalog, harness.Flags, new FakeQueryEmbedder(), new RetrievalOptions { TimeBudget = TimeSpan.FromSeconds(30) });
        var response = await retrieval.SearchDetailedAsync(new SearchRequest { Query = "charge the card payment gateway", MaxResults = 5 }, CancellationToken.None);
        Assert.Contains(response.Results, r => r.FilePath.EndsWith("PaymentGateway.cs", StringComparison.Ordinal));

        harness.Service.SetUserPause(false);
        status = await harness.WaitForIdleAsync();
        Assert.Equal(SecondModel, Assert.Single(status.Roots).EmbeddingModelId);
        Assert.Empty(harness.Service.RetiringRoots);
        Assert.True(Directory.Exists(previous.Store.Folder.Directory));
    }

    [Fact]
    public async Task DeletingThePreviousIndex_DuringTheSwitch_LeavesTheNewOneServing()
    {
        Fixtures.Copy("shop", _workspace.Repository);
        var embedder = new FakeEmbedder();
        await using var harness = new IndexingHarness(_workspace.Data, embedder, Options());
        await harness.StartAsync(_workspace.Repository);
        await harness.WaitForIdleAsync();
        var previous = harness.Root;

        harness.Service.SetUserPause(true);
        embedder.ModelId = SecondModel;
        harness.Settings.Update(new PillionSettings());
        await harness.WaitForAsync(s => s.Roots.Length == 2, what: "both indexes in the status");

        Assert.True(await harness.Service.DeleteIndexAsync(previous.Store.Identity.Id, CancellationToken.None));

        Assert.Empty(harness.Service.RetiringRoots);
        Assert.False(Directory.Exists(previous.Store.Folder.Directory));
        var status = harness.Service.GetStatus();
        Assert.Equal(SecondModel, Assert.Single(status.Roots).EmbeddingModelId);
        Assert.Same(harness.Root, Assert.Single(harness.Service.ServingRoots));
        harness.Service.SetUserPause(false);
        await harness.WaitForIdleAsync();
    }

    private static IndexingOptions Options() => new()
    {
        MinimumRefreshInterval = TimeSpan.FromMilliseconds(200),
        PowerPollInterval = TimeSpan.FromHours(1),
        MemoryPollInterval = TimeSpan.FromMilliseconds(50),
        ReaderPollInterval = TimeSpan.FromMilliseconds(200),
        EmbeddingRetryDelay = TimeSpan.FromMilliseconds(10),
        RetentionSweepInterval = TimeSpan.Zero,
    };

    private sealed class FakeQueryEmbedder : IQueryEmbedder
    {
        public Task<QueryEmbedding> EmbedQueryAsync(string text, string? modelId, int? dimensions, CancellationToken cancellationToken) =>
            Task.FromResult(new QueryEmbedding(FakeEmbedder.Vector(text).Quantized, modelId ?? FirstModel));
    }
}
