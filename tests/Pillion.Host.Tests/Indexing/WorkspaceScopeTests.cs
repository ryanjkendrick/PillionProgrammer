using Pillion.Common.Settings;
using Pillion.ContextEngine.Retrieval;
using Pillion.Contracts;
using Pillion.Host.Context;
using Pillion.Host.Context.Retrieval;
using Pillion.Host.Indexing;
using Pillion.ModelGateway;

namespace Pillion.Host.Tests.Indexing;

/// <summary>
/// Retrieval, the mentions and the repo map read only the indexes of the open workspace's roots (design sections 7 and 12;
/// issue #98, found by the Gate 2 run): with two indexes on disk and one root open, a search, <c>@file</c>, <c>@symbol</c>,
/// <c>@solution</c> and the chat's context assembly never return or cite anything from the other index; with nothing open,
/// nothing is retrieved and chat assembles without retrieved chunks.
/// </summary>
public sealed class WorkspaceScopeTests : IDisposable
{
    private const string Canary = """
        namespace Elsewhere.Canary;

        /// <summary>Only the repository outside the workspace has this class: it charges the card for the order service.</summary>
        public sealed class LeakCanary
        {
            /// <summary>Charges the card of an order and keeps the receipt.</summary>
            public string ChargeTheCard(string cardToken) => "the leak canary charged the card " + cardToken;
        }
        """;

    private readonly IndexingWorkspace _workspace = new();
    private readonly string _elsewhere;

    public WorkspaceScopeTests()
    {
        _elsewhere = Path.Combine(Path.GetDirectoryName(_workspace.Repository)!, "elsewhere");
        Directory.CreateDirectory(_elsewhere);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        _workspace.Dispose();
    }

    [Fact]
    public async Task WithTwoIndexesOnDisk_AndOneRootOpen_NothingFromTheOtherIndexIsSearchedMentionedOrCited()
    {
        // The other folder's index, built by an earlier session and left on disk: the same code plus a class only it has.
        Fixtures.Copy("shop", _elsewhere);
        Directory.CreateDirectory(Path.Combine(_elsewhere, "src", "Canary"));
        await File.WriteAllTextAsync(Path.Combine(_elsewhere, "src", "Canary", "LeakCanary.cs"), Canary);
        await using (var earlier = new IndexingHarness(_workspace.Data, new FakeEmbedder()))
        {
            await earlier.StartAsync(_elsewhere);
            await earlier.WaitForIdleAsync();
        }

        // This session opens only the repository.
        Fixtures.Copy("shop", _workspace.Repository);
        await using var harness = new IndexingHarness(_workspace.Data, new FakeEmbedder());
        await harness.StartAsync(_workspace.Repository);
        await harness.WaitForIdleAsync();

        // Both indexes are on disk (a tool that asks for every index on purpose sees them).
        await using (var everything = new ReadOnlyIndexCatalog(harness.Paths, harness.Flags, servesEveryIndex: true))
        {
            Assert.Equal(2, (await everything.GetIndexesAsync([], CancellationToken.None)).Count);
        }

        var fallback = new ReadOnlyIndexCatalog(harness.Paths, harness.Flags);
        await using var catalog = new LiveIndexCatalog(harness.Service, harness.Flags, fallback);

        // The catalog: no roots means the workspace's root; the other root, named, gets nothing.
        var served = Assert.Single(await catalog.GetIndexesAsync([], CancellationToken.None));
        Assert.Same(harness.Root.Storage, served.Storage);
        Assert.Empty(await catalog.GetIndexesAsync([_elsewhere], CancellationToken.None));
        Assert.Single(await catalog.GetIndexesAsync([_elsewhere, _workspace.Repository], CancellationToken.None));
        Assert.All(await catalog.GetGraphsAsync(CancellationToken.None), g => AssertInRepository(g.RootPath));
        Assert.Equal(0, fallback.OpenCount);

        // Search: without roots, and naming the other root.
        var retrieval = new RetrievalService(catalog, harness.Flags, new FakeQueryEmbedder(), new RetrievalOptions { TimeBudget = TimeSpan.FromSeconds(30) });
        foreach (var query in new[] { "LeakCanary ChargeTheCard leak canary", "how does the order service charge the card" })
        {
            var response = await retrieval.SearchDetailedAsync(new SearchRequest { Query = query, MaxResults = 20 }, CancellationToken.None);
            Assert.All(response.Results, r => AssertInRepository(r.FilePath));
        }

        Assert.NotEmpty((await retrieval.SearchDetailedAsync(new SearchRequest { Query = "how does the order service charge the card", MaxResults = 5 }, CancellationToken.None)).Results);
        Assert.Empty((await retrieval.SearchDetailedAsync(new SearchRequest { Query = "LeakCanary", Roots = [_elsewhere], MaxResults = 5 }, CancellationToken.None)).Results);

        // The mentions, with the client's roots and without them.
        var mentions = new ContextRpcService(TokenCounter.Default, new StaticGatewaySettingsSource(new PillionSettings()), catalog: catalog, queries: null);
        foreach (var roots in new[] { Array.Empty<string>(), [_workspace.Repository] })
        {
            var file = await mentions.ResolveMentionAsync(new MentionRequest { Mention = "@file:PaymentGateway.cs", WorkspaceRoots = roots }, CancellationToken.None);
            Assert.True(file.IsResolved, file.Reason);
            AssertInRepository(file.FilePath!);
            AssertNothingFromElsewhere(file);

            var canaryFile = await mentions.ResolveMentionAsync(new MentionRequest { Mention = "@file:LeakCanary.cs", WorkspaceRoots = roots }, CancellationToken.None);
            Assert.False(canaryFile.IsResolved);
            AssertNothingFromElsewhere(canaryFile);

            // The class is in both indexes: one answer, from the workspace (with both indexes it would be ambiguous).
            var symbol = await mentions.ResolveMentionAsync(new MentionRequest { Mention = "@symbol:PaymentGateway", WorkspaceRoots = roots }, CancellationToken.None);
            Assert.True(symbol.IsResolved, symbol.Reason);
            AssertInRepository(symbol.FilePath!);
            AssertNothingFromElsewhere(symbol);

            var canarySymbol = await mentions.ResolveMentionAsync(new MentionRequest { Mention = "@symbol:LeakCanary", WorkspaceRoots = roots }, CancellationToken.None);
            Assert.False(canarySymbol.IsResolved);
            AssertNothingFromElsewhere(canarySymbol);

            var solution = await mentions.ResolveMentionAsync(new MentionRequest { Mention = "@solution", WorkspaceRoots = roots }, CancellationToken.None);
            Assert.True(solution.IsResolved, solution.Reason);
            Assert.Contains("Repo map of " + _workspace.Repository, solution.Content, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("LeakCanary", solution.Content, StringComparison.Ordinal);
            AssertNothingFromElsewhere(solution);
        }

        // Chat: the retrieved chunks and the repo map come from the workspace's index only.
        var retriever = new CodeContextRetriever(retrieval, catalog, harness.Flags, catalog);
        var assembled = await AssembleAsync(retriever, "How does the leak canary or the order service charge the card? LeakCanary ChargeTheCard");
        Assert.NotEmpty(assembled.SentChunks);
        Assert.All(assembled.SentChunks, c => AssertInRepository(c.FilePath));
        Assert.All(assembled.SentChunks, c => Assert.StartsWith(harness.Root.Store.Identity.Id + ":", c.Key, StringComparison.Ordinal));
        Assert.Single(assembled.Sections, s => s.Kind == PromptSectionKind.RepoMap);
        Assert.All(assembled.Sections.Where(s => s.Kind != PromptSectionKind.Query), s => Assert.DoesNotContain("LeakCanary", s.Text, StringComparison.Ordinal));
        Assert.All(await retriever.GetGraphsAsync(CancellationToken.None), g => AssertInRepository(g.RootPath));

        // Nothing open: nothing is retrieved, and chat assembles without retrieved chunks or a repo map.
        harness.Service.SetWorkspace(new WorkspaceDescription());
        Assert.Empty(await catalog.GetIndexesAsync([], CancellationToken.None));
        Assert.Empty(await catalog.GetIndexesAsync([_workspace.Repository], CancellationToken.None));
        Assert.Empty(await catalog.GetGraphsAsync(CancellationToken.None));
        Assert.Empty((await retrieval.SearchDetailedAsync(new SearchRequest { Query = "how does the order service charge the card", MaxResults = 5 }, CancellationToken.None)).Results);
        Assert.False(await retriever.HasIndexAsync(CancellationToken.None));
        var unindexed = await AssembleAsync(retriever, "How does the order service charge the card?");
        Assert.Empty(unindexed.SentChunks);
        Assert.DoesNotContain(unindexed.Sections, s => s.Kind is PromptSectionKind.RepoMap);
        var nothing = await mentions.ResolveMentionAsync(new MentionRequest { Mention = "@symbol:PaymentGateway" }, CancellationToken.None);
        Assert.False(nothing.IsResolved);
        Assert.Equal(0, fallback.OpenCount);
    }

    private async Task<AssembledContext> AssembleAsync(CodeContextRetriever retriever, string question)
    {
        var options = new ContextAssemblerOptions();
        var tokens = TokenCounter.Default;
        var assembler = new ContextAssembler(tokens, options, [new RetrievalContextSource(retriever), new RepoMapContextSource(retriever, tokens, options)]);
        var request = new ChatRequest
        {
            RequestId = Guid.NewGuid().ToString("N"),
            Messages = [new ChatMessage { Role = ChatRole.User, Content = question }],
            CurrentFile = _workspace.File("src/Orders/OrderService.cs"),
            CurrentFileRegion = "public void Place(Order order) { }",
        };

        return await assembler.AssembleAsync(request, new AssemblyTarget("qwen2.5-coder:7b", 32768, true, "You help.", []), null, CancellationToken.None);
    }

    private void AssertInRepository(string path)
    {
        Assert.False(path.StartsWith(_elsewhere, StringComparison.OrdinalIgnoreCase), "From the index outside the workspace: " + path);
        Assert.StartsWith(_workspace.Repository, path, StringComparison.OrdinalIgnoreCase);
    }

    private void AssertNothingFromElsewhere(MentionResolution resolution)
    {
        Assert.DoesNotContain(_elsewhere, resolution.Content ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("LeakCanary", resolution.Content ?? string.Empty, StringComparison.Ordinal);
        Assert.All(resolution.Candidates, c => Assert.DoesNotContain(_elsewhere, c, StringComparison.OrdinalIgnoreCase));
        Assert.All(resolution.Citations, c => AssertInRepository(c.FilePath));
    }

    private sealed class FakeQueryEmbedder : IQueryEmbedder
    {
        public Task<QueryEmbedding> EmbedQueryAsync(string text, string? modelId, int? dimensions, CancellationToken cancellationToken) =>
            Task.FromResult(new QueryEmbedding(FakeEmbedder.Vector(text).Quantized, modelId ?? "fake/hashed-words"));
    }
}
