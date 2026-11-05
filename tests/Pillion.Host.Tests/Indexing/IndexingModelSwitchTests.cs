using Pillion.Common;
using Pillion.Common.Settings;
using Pillion.ContextEngine;
using Pillion.ContextEngine.Retrieval;
using Pillion.Contracts;
using Pillion.Host.Context.Retrieval;
using Pillion.Host.Indexing;
using Pillion.Indexing.Ownership;
using Pillion.LocalStore.Index;
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
    public async Task SwitchingAgainBeforeTheNewIndexCompletes_KeepsTheOpenPreviousIndex_RatherThanAdoptingAnOlderOneFromDisk()
    {
        // PR 121 review (second round): with a still older complete index of the root on disk, discarding the incomplete index
        // took the "no previous index" path and adopted the older index from disk over the complete one that was serving,
        // which stayed open, detached, holding its lock. X complete, A complete (X closed, on disk), pause, A -> B -> C.
        const string ThirdModel = "fake/third-model";
        const string FourthModel = "fake/fourth-model";
        Fixtures.Copy("shop", _workspace.Repository);
        var embedder = new FakeEmbedder();
        await using var harness = new IndexingHarness(_workspace.Data, embedder, Options());
        await harness.StartAsync(_workspace.Repository);
        await harness.WaitForIdleAsync();
        var x = harness.Root;
        var xId = x.Store.Identity.Id;

        // X -> A with the pipeline running: A completes, X closes and stays on disk, complete and unused.
        embedder.ModelId = SecondModel;
        harness.Settings.Update(new PillionSettings());
        await harness.WaitForAsync(s => s.Phase == IndexPhase.Idle && s.Roots.Length == 1 && s.Roots[0].EmbeddingModelId == SecondModel && s.Roots[0].LastFullSweepUtc is not null, what: "the second model's index complete and alone");
        await harness.WaitForAsync(_ => !x.Store.HoldsWriterLock(), what: "the first index to be closed");
        var a = harness.Root;
        var aId = a.Store.Identity.Id;
        Assert.NotEqual(xId, aId);
        Assert.Empty(harness.Service.RetiringRoots);
        Assert.Contains(harness.Service.ListIndexes(), i => i.IndexId == xId && !i.IsOpen && !i.IsInUse);

        // A -> B -> C before B completes.
        harness.Service.SetUserPause(true);
        embedder.ModelId = ThirdModel;
        harness.Settings.Update(new PillionSettings());
        await harness.WaitForAsync(s => s.Roots.Length == 2 && s.Roots.Any(r => r.EmbeddingModelId == ThirdModel), what: "the third index beside the second");
        var b = Assert.Single(harness.Service.OpenRoots);
        var bId = b.Store.Identity.Id;
        embedder.ModelId = FourthModel;
        harness.Settings.Update(new PillionSettings());
        var status = await harness.WaitForAsync(s => s.Roots.Length == 2 && s.Roots.Any(r => r.EmbeddingModelId == FourthModel), what: "the fourth index beside the complete second");

        // A, complete and open, is still the previous index and serves; it keeps its lock; X is not opened; B is closed.
        Assert.Same(a, Assert.Single(harness.Service.RetiringRoots));
        Assert.Same(a, Assert.Single(harness.Service.ServingRoots));
        Assert.True(a.Store.HoldsWriterLock(), "the serving previous index holds its lock until the new index completes");
        Assert.Contains(status.Roots, r => r.IndexId == aId && r.Role == IndexRole.Reader && r.ChunkCount > 0);
        Assert.DoesNotContain(status.Roots, r => r.IndexId == xId || r.IndexId == bId);
        Assert.False(b.Store.HoldsWriterLock(), "the incomplete index was closed");
        var indexes = harness.Service.ListIndexes();
        Assert.Contains(indexes, i => i.IndexId == xId && !i.IsOpen && !i.IsInUse);
        Assert.Contains(indexes, i => i.IndexId == bId && !i.IsOpen && !i.IsInUse);
        Assert.Contains(indexes, i => i.IndexId == aId && i.IsOpen);
        var catalog = new LiveIndexCatalog(harness.Service, harness.Flags);
        var retrieval = new RetrievalService(catalog, harness.Flags, new FakeQueryEmbedder(), new RetrievalOptions { TimeBudget = TimeSpan.FromSeconds(30) });
        var served = Assert.Single(await catalog.GetIndexesAsync([], CancellationToken.None));
        Assert.Equal(aId, served.IndexId);
        Assert.Same(a.Storage, served.Storage);
        var response = await retrieval.SearchDetailedAsync(new SearchRequest { Query = "charge the card payment gateway", MaxResults = 5 }, CancellationToken.None);
        Assert.Contains(response.Results, r => r.FilePath.EndsWith("PaymentGateway.cs", StringComparison.Ordinal));

        // C completes: A closes and releases its lock; C serves alone; X, A and B are closed folders on disk.
        harness.Service.SetUserPause(false);
        status = await harness.WaitForIdleAsync();
        var cId = Assert.Single(status.Roots).IndexId;
        Assert.Equal(FourthModel, status.Roots[0].EmbeddingModelId);
        Assert.Empty(harness.Service.RetiringRoots);
        await harness.WaitForAsync(_ => !a.Store.HoldsWriterLock(), what: "the previous index's lock to be released");
        Assert.Equal(cId, Assert.Single(await catalog.GetIndexesAsync([], CancellationToken.None)).IndexId);
        indexes = harness.Service.ListIndexes();
        Assert.All(new[] { xId, aId, bId }, id => Assert.Contains(indexes, i => i.IndexId == id && !i.IsOpen && !i.IsInUse));
        Assert.Equal(cId, Assert.Single(indexes, i => i.IsOpen).IndexId);
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

    [Fact]
    public async Task AnIndexBuiltBeforeIndexTimeRedaction_IsNeitherAdoptedNorServedReadOnly_AndIsDeletedWhenTheNewIndexCompletes()
    {
        // PR 121 review (third round): on the first start after this build every root opens a new, incomplete chunker-"2" index
        // with no previous one open, so the complete chunker-"1" index of the same model (built with no redaction) was adopted
        // and served until the rebuild completed, and the read-only fallback could open it as well.
        Fixtures.Copy("shop", _workspace.Repository);
        var paths = new PillionPaths(_workspace.Data);
        var flags = new AllFlags();
        string unscannedId;
        string unscannedFolder;
        await using (var unscanned = await new IndexStoreFactory(paths, flags).OpenAsync(new IndexRequest(_workspace.Repository, FirstModel, "1", "pillion-earlier-build")))
        {
            unscanned.RecordFullSweep(); // complete, as a build before work item 3.2 left it
            unscannedId = unscanned.Identity.Id;
            unscannedFolder = unscanned.Folder.Directory;
        }

        Assert.False(IndexRedactionSchema.IsScannedChunkerVersion("1"));

        // The read-only catalog does not open it for the root, even with nothing else there.
        await using (var fallback = new ReadOnlyIndexCatalog(paths, flags))
        {
            Assert.Empty(await fallback.GetIndexesAsync([_workspace.Repository], CancellationToken.None));
            Assert.Equal(0, fallback.OpenCount);
        }

        var embedder = new FakeEmbedder();
        await using var harness = new IndexingHarness(_workspace.Data, embedder, Options());
        harness.Service.SetUserPause(true);
        await harness.StartAsync(_workspace.Repository);

        // The run of the new index has started (so the reconcile that opened it, adoption included, is over) and waits paused.
        var status = await harness.WaitForAsync(s => s.Roots.Length >= 1 && s.Progress is not null, what: "the new index's run to start");
        var only = Assert.Single(status.Roots);
        Assert.Equal(IndexRole.Writer, only.Role);
        Assert.Equal(ContextEngineVersions.ChunkerVersion, only.ChunkerVersion);
        Assert.Null(only.LastFullSweepUtc);
        Assert.NotEqual(unscannedId, only.IndexId);
        Assert.Empty(harness.Service.RetiringRoots);
        Assert.Same(harness.Root, Assert.Single(harness.Service.ServingRoots));
        await using var catalog = new LiveIndexCatalog(harness.Service, harness.Flags, new ReadOnlyIndexCatalog(harness.Paths, harness.Flags));
        var served = Assert.Single(await catalog.GetIndexesAsync([], CancellationToken.None));
        Assert.Equal(only.IndexId, served.IndexId);
        Assert.True(Directory.Exists(unscannedFolder));
        Assert.Contains(harness.Service.ListIndexes(), i => i.IndexId == unscannedId && !i.IsOpen && !i.IsInUse && i.ChunkerVersion == "1");

        // The new index completes: the unscanned one is deleted at once, not left to the age rule.
        harness.Service.SetUserPause(false);
        status = await harness.WaitForIdleAsync();
        Assert.NotNull(Assert.Single(status.Roots).LastFullSweepUtc);
        await harness.WaitForAsync(_ => !Directory.Exists(unscannedFolder), what: "the unscanned index to be deleted");
        Assert.DoesNotContain(harness.Service.ListIndexes(), i => i.IndexId == unscannedId);
        Assert.Equal(only.IndexId, Assert.Single(harness.Service.ListIndexes()).IndexId);
    }

    [Fact]
    public async Task SwitchingBackToAnAdoptedPreviousIndex_ReinstatesItWithItsOwnModelDescriptor()
    {
        // PR 121 review (third round): an index adopted from disk was built with the current model's descriptor under its own
        // model id (token counter, input size and tokenizer family of the wrong model); reinstating it made that the root's
        // model, and the next run chunked with it.
        Fixtures.Copy("shop", _workspace.Repository);
        string aId;
        await using (var first = new IndexingHarness(_workspace.Data, new FakeEmbedder(), Options()))
        {
            await first.StartAsync(_workspace.Repository);
            await first.WaitForIdleAsync();
            aId = first.Root.Store.Identity.Id;
        }

        // A new session under model B, whose descriptor differs from A's in more than the id; A is adopted from disk.
        var embedder = new FakeEmbedder { ModelId = SecondModel, MaxTokens = 256, TokenizerFamily = "fake/second-tokenizer" };
        await using var harness = new IndexingHarness(_workspace.Data, embedder, Options());
        harness.Service.SetUserPause(true);
        await harness.StartAsync(_workspace.Repository);
        await harness.WaitForAsync(s => s.Roots.Length == 2, what: "the adopted previous index beside the new one");
        var adopted = Assert.Single(harness.Service.RetiringRoots);
        Assert.Equal(aId, adopted.Store.Identity.Id);
        Assert.Equal(FirstModel, adopted.Model.ModelId);

        // Back to A before B completes: the adopted index is reinstated with A's descriptor, and its sweep records A's input size.
        embedder.ModelId = FirstModel;
        embedder.MaxTokens = 512;
        embedder.TokenizerFamily = "bert-wordpiece";
        harness.Settings.Update(new PillionSettings());
        await harness.WaitForAsync(s => s.Roots.Length == 1 && s.Roots[0].EmbeddingModelId == FirstModel, what: "the first model's index alone");
        Assert.Same(adopted, harness.Root);
        Assert.False(adopted.IsRetiring);
        Assert.Equal(FirstModel, adopted.Model.ModelId);
        Assert.Equal(512, adopted.Model.MaxTokens);
        Assert.Equal("bert-wordpiece", adopted.Model.TokenizerFamily);

        harness.Service.SetUserPause(false);
        await harness.WaitForAsync(s => s.Phase == IndexPhase.Idle && harness.Service.FullRuns >= 1 && s.Roots.Length == 1, what: "the reinstated index's sweep");
        Assert.Equal("512", await ReadMetaAsync(adopted, "embedding_max_tokens"));
        Assert.Equal("bert-wordpiece", await ReadMetaAsync(adopted, "embedding_tokenizer"));
        Assert.Equal("512", await ReadMetaAsync(adopted, "chunk_max_tokens"));
        Assert.Empty(harness.Service.RetiringRoots);
    }

    [Fact]
    public async Task AHostThatOnlyReadsThePreviousIndex_CannotDeleteIt_AndKeepsServingIt()
    {
        // PR 121 review (third round): the previous-index branch of DeleteIndexAsync had no owner check, so a reader removed
        // and closed the previous index it was serving from, then retention refused the delete, and the root was left without
        // its serving fallback.
        Fixtures.Copy("shop", _workspace.Repository);
        var ownerEmbedder = new FakeEmbedder();
        var readerEmbedder = new FakeEmbedder();
        await using var owner = new IndexingHarness(_workspace.Data, ownerEmbedder, Options(), pipeName: "pillion-owner");
        await owner.StartAsync(_workspace.Repository);
        await owner.WaitForIdleAsync();
        var ownerPrevious = owner.Root;
        await using var reader = new IndexingHarness(_workspace.Data, readerEmbedder, Options(), pipeName: "pillion-other");
        await reader.StartAsync(_workspace.Repository);
        await reader.WaitForAsync(s => s.Roots.Length == 1 && s.Roots[0].Role == IndexRole.Reader, what: "the other host to read the first index");
        var readerPrevious = reader.Root;

        owner.Service.SetUserPause(true);
        ownerEmbedder.ModelId = SecondModel;
        owner.Settings.Update(new PillionSettings());
        await owner.WaitForAsync(s => s.Roots.Length == 2 && s.Roots.Any(r => r.EmbeddingModelId == SecondModel && r.Role == IndexRole.Writer), what: "the owner to open the second index");
        readerEmbedder.ModelId = SecondModel;
        reader.Settings.Update(new PillionSettings());
        await reader.WaitForAsync(s => s.Roots.Length == 2 && s.Roots.Any(r => r.EmbeddingModelId == SecondModel), what: "the other host to read the second index");
        Assert.Same(readerPrevious, Assert.Single(reader.Service.RetiringRoots));

        var denied = await Assert.ThrowsAsync<PillionRpcException>(() => reader.Service.DeleteIndexAsync(readerPrevious.Store.Identity.Id, CancellationToken.None));
        Assert.Equal(PillionErrorCode.PolicyDenied, denied.Error.Code);

        // Nothing was removed or closed: the other host still serves the root from its previous index.
        Assert.Same(readerPrevious, Assert.Single(reader.Service.RetiringRoots));
        Assert.Same(readerPrevious, Assert.Single(reader.Service.ServingRoots));
        var catalog = new LiveIndexCatalog(reader.Service, reader.Flags);
        var served = Assert.Single(await catalog.GetIndexesAsync([], CancellationToken.None));
        Assert.Equal(readerPrevious.Store.Identity.Id, served.IndexId);
        Assert.Same(readerPrevious.Storage, served.Storage);
        Assert.Contains(reader.Service.GetStatus().Roots, r => r.IndexId == readerPrevious.Store.Identity.Id && r.Role == IndexRole.Reader);
        Assert.True(Directory.Exists(readerPrevious.Store.Folder.Directory));
        Assert.True(ownerPrevious.Store.HoldsWriterLock(), "the owner still holds the previous index");
        Assert.Same(ownerPrevious, Assert.Single(owner.Service.RetiringRoots));

        owner.Service.SetUserPause(false);
        await owner.WaitForIdleAsync();
    }

    private static Task<string?> ReadMetaAsync(RootIndex root, string key) =>
        root.Storage.Database.ReadAsync(
            (connection, _) =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT value FROM meta WHERE key = $key";
                command.Parameters.AddWithValue("$key", key);
                return Task.FromResult(command.ExecuteScalar() as string);
            },
            CancellationToken.None);

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
