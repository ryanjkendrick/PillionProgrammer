using Pillion.ContextEngine.Retrieval;
using Pillion.Contracts;
using Pillion.Host.Context.Retrieval;
using Pillion.Host.Indexing;
using Pillion.ModelGateway;

namespace Pillion.Host.Tests.Indexing;

/// <summary>
/// Context assembly v2 (work item 2.12) on the indexing pipeline's own index (2.9): once the pipeline has indexed the fixture
/// repository, the chat's repo map comes from the symbol graph the live catalog hands out, and retrieved chunks are read
/// through the storage the pipeline writes, with no second, read-only opener of the same database.
/// </summary>
public sealed class ContextAssemblyLiveCatalogTests : IDisposable
{
    private readonly IndexingWorkspace _workspace = new();

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        _workspace.Dispose();
    }

    [Fact]
    public async Task TheRepoMapAndTheRetrievedChunks_ComeFromThePipelinesIndex_WithoutASecondOpener()
    {
        Fixtures.Copy("shop", _workspace.Repository);
        await using var harness = new IndexingHarness(_workspace.Data, new FakeEmbedder());
        await harness.StartAsync(_workspace.Repository);
        await harness.WaitForIdleAsync();

        var fallback = new ReadOnlyIndexCatalog(harness.Paths, harness.Flags);
        await using var catalog = new LiveIndexCatalog(harness.Service, harness.Flags, fallback);
        var retrieval = new RetrievalService(catalog, harness.Flags, new FakeQueryEmbedder(), new RetrievalOptions { TimeBudget = TimeSpan.FromSeconds(30) });
        var retriever = new CodeContextRetriever(retrieval, catalog, harness.Flags, catalog);
        var options = new ContextAssemblerOptions();
        var tokens = TokenCounter.Default;
        var assembler = new ContextAssembler(tokens, options, [new RetrievalContextSource(retriever), new RepoMapContextSource(retriever, tokens, options)]);
        var request = new ChatRequest
        {
            RequestId = "live-1",
            Messages = [new ChatMessage { Role = ChatRole.User, Content = "How does the order service charge the card?" }],
            CurrentFile = _workspace.File("src/Orders/OrderService.cs"),
            CurrentFileRegion = "public void Place(Order order) { }",
        };

        var assembled = await assembler.AssembleAsync(request, new AssemblyTarget("qwen2.5-coder:7b", 32768, true, "You help.", []), null, CancellationToken.None);

        // The repo map: from the pipeline's graph, the current file first (its focus), the other files' symbols after it.
        var map = Assert.Single(assembled.Sections, s => s.Kind == PromptSectionKind.RepoMap);
        Assert.StartsWith("## Repository map\nsrc/Orders/OrderService.cs\n", map.Text, StringComparison.Ordinal);
        Assert.Contains("src/Payments/PaymentGateway.cs", map.Text, StringComparison.Ordinal);
        Assert.Contains("Charge", map.Text, StringComparison.Ordinal);
        var mapItem = Assert.Single(assembled.Report.Items, i => i.Kind == ContextItemKind.RepoMap);
        Assert.Equal("repoMap", mapItem.Key);
        Assert.False(mapItem.Dropped);

        // Retrieved chunks: from the same index, through the pipeline's storage.
        Assert.NotEmpty(assembled.SentChunks);
        Assert.All(assembled.SentChunks, c => Assert.StartsWith(_workspace.Repository, c.FilePath, StringComparison.OrdinalIgnoreCase));
        Assert.All(assembled.SentChunks, c => Assert.StartsWith(harness.Root.Store.Identity.Id + ":", c.Key, StringComparison.Ordinal));
        Assert.Contains(assembled.SentChunks, c => c.Path is "src/Payments/PaymentGateway.cs" or "src/Orders/OrderService.cs");
        var served = Assert.Single(await catalog.GetIndexesAsync([], CancellationToken.None));
        Assert.Same(harness.Root.Storage, served.Storage);
        Assert.Same(harness.Root.Graph, served.Graph);
        Assert.Equal(0, fallback.OpenCount); // the read-only catalog never opened the root the pipeline owns
    }

    private sealed class FakeQueryEmbedder : IQueryEmbedder
    {
        public Task<QueryEmbedding> EmbedQueryAsync(string text, string? modelId, int? dimensions, CancellationToken cancellationToken) =>
            Task.FromResult(new QueryEmbedding(FakeEmbedder.Vector(text).Quantized, modelId ?? "fake/hashed-words"));
    }
}
