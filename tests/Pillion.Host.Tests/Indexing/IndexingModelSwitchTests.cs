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

        // The reinstated index is swept once (it was complete already, so the sweep finds nothing to do) and is idle again.
        var status = await harness.WaitForAsync(s => s.Phase == IndexPhase.Idle && harness.Service.FullRuns >= runs + 1 && s.Roots.Length == 1, what: "the reinstated index's sweep");
        Assert.Same(original, harness.Root);
        Assert.False(original.IsRetiring);
        Assert.NotNull(original.Watcher);
        Assert.Empty(harness.Service.RetiringRoots);
        Assert.Equal(original.Store.Identity.Id, Assert.Single(status.Roots).IndexId);
        Assert.Equal(IndexRole.Writer, status.Roots[0].Role);
        Assert.NotNull(status.Roots[0].LastFullSweepUtc);
        Assert.Equal(embedded, embedder.ChunksEmbedded); // nothing was embedded again: the reinstated index was complete
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

    [Fact]
    public async Task SwitchingAgainBeforeTheNewIndexCompletes_KeepsTheLastCompleteIndexServing_AndClosesTheIncompleteOne()
    {
        // PR 121 review: A -> B -> C before B completed closed the complete A and kept the empty B as the previous index.
        const string ThirdModel = "fake/third-model";
        Fixtures.Copy("shop", _workspace.Repository);
        var embedder = new FakeEmbedder();
        await using var harness = new IndexingHarness(_workspace.Data, embedder, Options());
        await harness.StartAsync(_workspace.Repository);
        await harness.WaitForIdleAsync();
        var a = harness.Root;
        var catalog = new LiveIndexCatalog(harness.Service, harness.Flags);
        var retrieval = new RetrievalService(catalog, harness.Flags, new FakeQueryEmbedder(), new RetrievalOptions { TimeBudget = TimeSpan.FromSeconds(30) });

        harness.Service.SetUserPause(true);
        embedder.ModelId = SecondModel;
        harness.Settings.Update(new PillionSettings());
        await harness.WaitForAsync(s => s.Roots.Length == 2 && s.Roots.Any(r => r.EmbeddingModelId == SecondModel), what: "the second index beside the first");
        var b = Assert.Single(harness.Service.OpenRoots);

        embedder.ModelId = ThirdModel;
        harness.Settings.Update(new PillionSettings());
        var status = await harness.WaitForAsync(s => s.Roots.Length == 2 && s.Roots.Any(r => r.EmbeddingModelId == ThirdModel), what: "the third index beside the complete first");

        // A (complete) is still the previous index; B (never completed) is closed; C is the writer being built.
        Assert.Same(a, Assert.Single(harness.Service.RetiringRoots));
        Assert.Same(a, Assert.Single(harness.Service.ServingRoots));
        Assert.Equal(ThirdModel, Assert.Single(harness.Service.OpenRoots).Model.ModelId);
        Assert.Contains(status.Roots, r => r.EmbeddingModelId == FirstModel && r.Role == IndexRole.Reader && r.ChunkCount > 0);
        Assert.DoesNotContain(status.Roots, r => r.EmbeddingModelId == SecondModel);
        Assert.False(b.Store.HoldsWriterLock(), "the incomplete index was closed");
        Assert.Contains(harness.Service.ListIndexes(), i => i.EmbeddingModelId == SecondModel && !i.IsOpen && !i.IsInUse);
        var served = Assert.Single(await catalog.GetIndexesAsync([], CancellationToken.None));
        Assert.Equal(a.Store.Identity.Id, served.IndexId);
        var response = await retrieval.SearchDetailedAsync(new SearchRequest { Query = "charge the card payment gateway", MaxResults = 5 }, CancellationToken.None);
        Assert.Contains(response.Results, r => r.FilePath.EndsWith("PaymentGateway.cs", StringComparison.Ordinal));

        // C completes: A closes, C serves.
        harness.Service.SetUserPause(false);
        status = await harness.WaitForIdleAsync();
        Assert.Equal(ThirdModel, Assert.Single(status.Roots).EmbeddingModelId);
        Assert.Empty(harness.Service.RetiringRoots);
        Assert.Equal(harness.Root.Store.Identity.Id, Assert.Single(await catalog.GetIndexesAsync([], CancellationToken.None)).IndexId);
    }

    [Fact]
    public async Task AHostThatOnlyReadsTheNewIndex_ClosesItsPreviousIndex_WhenTheOwnerCompletesTheFirstSweep()
    {
        // PR 121 review: a reader's in-memory state is the snapshot taken at open; the owner's completed sweep must be seen at
        // the reader poll, or the reader serves the previous index for ever.
        Fixtures.Copy("shop", _workspace.Repository);
        var ownerEmbedder = new FakeEmbedder();
        var readerEmbedder = new FakeEmbedder();
        await using var owner = new IndexingHarness(_workspace.Data, ownerEmbedder, Options(), pipeName: "pillion-owner");
        await owner.StartAsync(_workspace.Repository);
        await owner.WaitForIdleAsync();
        await using var reader = new IndexingHarness(_workspace.Data, readerEmbedder, Options(), pipeName: "pillion-other");
        await reader.StartAsync(_workspace.Repository);
        var opened = await reader.WaitForAsync(s => s.Roots.Length == 1 && s.Roots[0].Role == IndexRole.Reader, what: "the other host to read the first index");
        Assert.Equal(FirstModel, opened.Roots[0].EmbeddingModelId);
        var readerPrevious = reader.Root;

        // Both hosts switch; the owner's new index is paused so it does not complete yet.
        owner.Service.SetUserPause(true);
        ownerEmbedder.ModelId = SecondModel;
        owner.Settings.Update(new PillionSettings());
        await owner.WaitForAsync(s => s.Roots.Length == 2 && s.Roots.Any(r => r.EmbeddingModelId == SecondModel && r.Role == IndexRole.Writer), what: "the owner to open the second index");
        readerEmbedder.ModelId = SecondModel;
        reader.Settings.Update(new PillionSettings());
        var during = await reader.WaitForAsync(s => s.Roots.Length == 2 && s.Roots.Any(r => r.EmbeddingModelId == SecondModel), what: "the other host to read the second index");
        Assert.All(during.Roots, r => Assert.Equal(IndexRole.Reader, r.Role));
        Assert.Same(readerPrevious, Assert.Single(reader.Service.RetiringRoots));
        Assert.Same(readerPrevious, Assert.Single(reader.Service.ServingRoots));
        var catalog = new LiveIndexCatalog(reader.Service, reader.Flags);
        Assert.Equal(readerPrevious.Store.Identity.Id, Assert.Single(await catalog.GetIndexesAsync([], CancellationToken.None)).IndexId);

        // The owner completes the first full sweep of the new index: within its poll interval the other host closes its
        // previous index and serves the new one.
        owner.Service.SetUserPause(false);
        await owner.WaitForIdleAsync();
        var after = await reader.WaitForAsync(s => s.Roots.Length == 1 && s.Roots[0].EmbeddingModelId == SecondModel, TimeSpan.FromSeconds(10), "the other host to stop serving its previous index");
        Assert.Equal(IndexRole.Reader, after.Roots[0].Role);
        Assert.NotNull(after.Roots[0].LastFullSweepUtc);
        Assert.Empty(reader.Service.RetiringRoots);
        Assert.Equal(reader.Root.Store.Identity.Id, Assert.Single(await catalog.GetIndexesAsync([], CancellationToken.None)).IndexId);
        Assert.Equal(0, readerEmbedder.ChunksEmbedded);
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
