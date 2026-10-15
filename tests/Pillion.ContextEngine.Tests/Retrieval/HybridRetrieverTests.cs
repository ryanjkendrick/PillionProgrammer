using System.Diagnostics;
using Pillion.ContextEngine.Retrieval;
using Pillion.LocalStore.Index;

namespace Pillion.ContextEngine.Tests.Retrieval;

public sealed class HybridRetrieverTests
{
    private const double Precision = 1e-12;

    // Tests that are not about the budget get a generous one, so a busy machine cannot cut a leg short.
    private static readonly RetrievalOptions Generous = RetrievalOptions.Default with { TimeBudget = TimeSpan.FromSeconds(10) };

    private readonly FakeIndex _index = new();
    private readonly FakeEmbedder _embedder = new();

    [Fact]
    public async Task Fusion_IsReciprocalRankFusion_WithTheRanksOfEachList()
    {
        var a = Chunk("src/A.cs");
        var b = Chunk("src/B.cs");
        var c = Chunk("src/C.cs");
        _index.LexicalReturns(a, b, c);
        _index.VectorReturns(c, a);

        var response = await SearchAsync("alpha beta");

        Assert.False(response.Degraded);
        Assert.Equal([a, c, b], response.Hits.Select(h => h.ChunkId));
        var hitA = response.Hits[0];
        Assert.Equal((1, 2), (hitA.LexicalRank, hitA.VectorRank));
        Assert.Equal((1.0 / 61) + (1.0 / 62), hitA.FusedScore, Precision);
        Assert.Equal(hitA.FusedScore, hitA.Score, Precision);
        Assert.Equal((3, 1), (response.Hits[1].LexicalRank, response.Hits[1].VectorRank));
        Assert.Equal((1.0 / 63) + (1.0 / 61), response.Hits[1].FusedScore, Precision);
        Assert.Equal((2, 0), (response.Hits[2].LexicalRank, response.Hits[2].VectorRank));
        Assert.Equal(1.0 / 62, response.Hits[2].FusedScore, Precision);
    }

    [Fact]
    public async Task AnExactSymbolHit_IsAThirdList_AndFindsTheChunkHoldingTheDefinition()
    {
        var file = _index.AddFile("src/inventory.py", "python", "src");
        var ids = _index.AddChunks(file, 4); // lines 1-5, 6-10, 11-15, 16-20
        _index.SymbolList.Add(new SymbolRecord(1, file, "restock", "method", "Item", 12, 14, "def restock(self, amount)"));
        _index.SymbolList.Add(new SymbolRecord(2, file, "Restock", "class", null, 1, 3, null)); // differs in case: not exact
        var other = Chunk("src/other.py");
        _index.LexicalReturns(other, ids[2]);

        var response = await SearchAsync("how does restock add stock");

        var hit = Assert.Single(response.Hits, h => h.ChunkId == ids[2]);
        Assert.Equal(1, hit.SymbolRank);
        Assert.Equal(2, hit.LexicalRank);
        Assert.Equal((1.0 / 62) + (1.0 / 61), hit.FusedScore, Precision);
        Assert.Equal(ids[2], response.Hits[0].ChunkId);
    }

    [Fact]
    public async Task AQualifiedSymbol_MatchesOnlyDefinitionsInThatContainer()
    {
        var file = _index.AddFile("src/Orders.cs");
        var ids = _index.AddChunks(file, 4);
        _index.SymbolList.Add(new SymbolRecord(1, file, "Place", "method", "Shop > OrderService", 2, 4, null));
        _index.SymbolList.Add(new SymbolRecord(2, file, "Place", "method", "Shop > Garden", 17, 19, null));

        var response = await SearchAsync("OrderService.Place", RetrievalSearchMode.Symbol);

        Assert.Equal([ids[0], ids[3]], response.Hits.Select(h => h.ChunkId)); // the qualified name first, then Place itself
        Assert.Equal(1, response.Hits[0].SymbolRank);
    }

    [Fact]
    public async Task TheFocusBoost_ReordersCandidates_ButLeavesTheFusedScoreAlone_AndAddsNothing()
    {
        var a = Chunk("src/A.cs");
        var b = Chunk("src/B.cs");
        Chunk("src/Focused.cs");
        _index.LexicalReturns(a, b);

        var response = await SearchAsync("alpha", focus: [@"C:\repo\src\B.cs", @"C:\repo\src\Focused.cs", @"D:\elsewhere\A.cs"]);

        Assert.Equal([b, a], response.Hits.Select(h => h.ChunkId));
        Assert.Equal(1.0 / 62, response.Hits[0].FusedScore, Precision);
        Assert.Equal((1.0 / 62) + (1.0 / 61), response.Hits[0].Score, Precision);
        Assert.Equal(1, response.Hits[0].FocusRank);
        Assert.Equal(0, response.Hits[1].FocusRank);
    }

    [Fact]
    public async Task AFileNamedInTheQuery_GetsTheFocusBoost()
    {
        var a = Chunk("src/A.cs");
        var parser = Chunk("src/Text/Parser.cs");
        _index.LexicalReturns(a, parser);

        var response = await SearchAsync("how does Parser.cs read tokens");

        Assert.Equal([parser, a], response.Hits.Select(h => h.ChunkId));
        Assert.Equal(1, response.Hits[0].FocusRank);
    }

    [Fact]
    public async Task HitsAreCappedPerFile_AtThreeByDefault_OrTheRequestedNumber()
    {
        var file = _index.AddFile("src/Big.cs");
        var ids = _index.AddChunks(file, 20);
        var spread = new[] { ids[1], ids[5], ids[9], ids[13], ids[17] }; // far enough apart that expansions do not overlap
        var other = Chunk("src/Other.cs");
        _index.LexicalReturns([.. spread, other]);

        var capped = await SearchAsync("alpha");
        var four = await SearchAsync("alpha", maxPerFile: 4);

        Assert.Equal([ids[1], ids[5], ids[9], other], capped.Hits.Select(h => h.ChunkId));
        Assert.Equal([ids[1], ids[5], ids[9], ids[13], other], four.Hits.Select(h => h.ChunkId));
    }

    [Fact]
    public async Task EachHit_IsExpandedWithItsNeighbours_AndAHitAlreadyShownIsDropped()
    {
        var file = _index.AddFile("src/Big.cs");
        var ids = _index.AddChunks(file, 10); // five lines each
        _index.LexicalReturns(ids[5], ids[6], ids[8]);

        var response = await SearchAsync("alpha", includeText: true);

        // ids[5] (lines 26-30) grows to lines 21-35; ids[6] is inside it; ids[8] (41-45) grows to 36-50, without 31-35.
        Assert.Equal([ids[5], ids[8]], response.Hits.Select(h => h.ChunkId));
        Assert.Equal((21, 35), (response.Hits[0].StartLine, response.Hits[0].EndLine));
        Assert.Equal((36, 50), (response.Hits[1].StartLine, response.Hits[1].EndLine));
        Assert.StartsWith("line 21 of src/Big.cs\n", response.Hits[0].Text, StringComparison.Ordinal);
        Assert.EndsWith("line 35 of src/Big.cs", response.Hits[0].Text, StringComparison.Ordinal);
        Assert.Equal(15, response.Hits[0].Text!.Split('\n').Length);
        Assert.Equal(_index.ChunkList.Single(c => c.Id == ids[5]).Text, response.Hits[0].Snippet);
    }

    [Fact]
    public async Task WithoutIncludeText_HitsCarryTheSnippetOnly_AndTheirFileDetails()
    {
        var a = Chunk("src/Api/A.cs", "csharp", "src/Api");
        _index.LexicalReturns(a);

        var hit = Assert.Single((await SearchAsync("alpha")).Hits);

        Assert.Null(hit.Text);
        Assert.Equal(@"C:\repo\src\Api\A.cs", hit.FilePath);
        Assert.Equal("src/Api/A.cs", hit.RelativePath);
        Assert.Equal("src/Api", hit.Partition);
        Assert.Equal("csharp", hit.Language);
        Assert.Equal(_index.IndexId + ":" + a, hit.QualifiedChunkId);
    }

    [Fact]
    public async Task MaxResults_CutsTheList()
    {
        var ids = Enumerable.Range(0, 12).Select(i => Chunk($"src/F{i}.cs")).ToArray();
        _index.LexicalReturns(ids);

        Assert.Equal(10, (await SearchAsync("alpha")).Hits.Count); // the default
        Assert.Equal(ids.Take(3), (await SearchAsync("alpha", maxResults: 3)).Hits.Select(h => h.ChunkId));
    }

    [Fact]
    public async Task ASlowVectorLeg_IsAbandonedAtTheBudget_AndTheLexicalHitsAreReturnedDegraded()
    {
        var a = Chunk("src/A.cs");
        var b = Chunk("src/B.cs");
        _index.LexicalReturns(a);
        var vectorCancelled = new TaskCompletionSource();
        _index.Vector = async (_, _, ct) =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(10), ct);
            }
            catch (OperationCanceledException)
            {
                vectorCancelled.TrySetResult();
                throw;
            }

            return [new VectorHit(b, 0.1)];
        };

        var watch = Stopwatch.StartNew();
        var response = await SearchAsync("alpha", budget: TimeSpan.FromMilliseconds(250));
        watch.Stop();

        Assert.True(response.Degraded);
        Assert.Contains("vector over budget", response.DegradedReasons);
        Assert.Equal([a], response.Hits.Select(h => h.ChunkId));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"took {watch.Elapsed}");
        Assert.InRange(response.VectorMs, 100, 5000);
        Assert.True(response.TotalMs >= response.VectorMs - 1);
        await vectorCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5)); // the abandoned leg is told to stop
    }

    [Fact]
    public async Task ASlowEmbedder_DegradesTheSameWay()
    {
        var a = Chunk("src/A.cs");
        _index.LexicalReturns(a);
        _embedder.Delay = TimeSpan.FromSeconds(10);

        var response = await SearchAsync("alpha", budget: TimeSpan.FromMilliseconds(200));

        Assert.True(response.Degraded);
        Assert.Equal([a], response.Hits.Select(h => h.ChunkId));
        Assert.Equal(0, _index.VectorCalls);
    }

    [Fact]
    public async Task AFailingLeg_DegradesTheResponse_AndTheOtherLegsStillAnswer()
    {
        var a = Chunk("src/A.cs");
        _index.VectorReturns(a);
        _index.Lexical = (_, _, _) => throw new InvalidOperationException("disk on fire");

        var response = await SearchAsync("alpha");

        Assert.True(response.Degraded);
        Assert.Contains(response.DegradedReasons, r => r.Contains("lexical failed: disk on fire", StringComparison.Ordinal));
        Assert.Equal([a], response.Hits.Select(h => h.ChunkId));
    }

    [Fact]
    public async Task WithoutAnEmbedder_TheVectorLegIsSkipped_AndTheResponseSaysSo()
    {
        var a = Chunk("src/A.cs");
        _index.LexicalReturns(a);
        var retriever = new HybridRetriever(embedder: null, Generous);

        var response = await retriever.SearchAsync(new RetrievalQuery { Text = "alpha" }, [_index], CancellationToken.None);

        Assert.True(response.Degraded);
        Assert.Contains("vector: no embedding model is available", response.DegradedReasons);
        Assert.Equal([a], response.Hits.Select(h => h.ChunkId));
    }

    [Fact]
    public async Task AnIndexWithoutVectors_IsNotADegradation()
    {
        var a = Chunk("src/A.cs");
        _index.LexicalReturns(a);
        _index.VectorDimensions = null;

        var response = await SearchAsync("alpha");

        Assert.False(response.Degraded);
        Assert.Equal(0, _embedder.Calls);
    }

    [Theory]
    [InlineData(RetrievalSearchMode.Lexical, true, false, false)]
    [InlineData(RetrievalSearchMode.Vector, false, true, false)]
    [InlineData(RetrievalSearchMode.Symbol, false, false, true)]
    [InlineData(RetrievalSearchMode.Hybrid, true, true, true)]
    [InlineData(RetrievalSearchMode.LexicalAndSymbol, true, false, true)] // autocomplete snippets: no query embedding
    public async Task TheMode_RunsOnlyItsLegs(RetrievalSearchMode mode, bool lexical, bool vector, bool symbol)
    {
        var a = Chunk("src/A.cs");
        _index.LexicalReturns(a);
        _index.VectorReturns(a);

        var response = await SearchAsync("restock items", mode);

        Assert.Equal(lexical, _index.LexicalCalls > 0);
        Assert.Equal(vector, _index.VectorCalls > 0);
        Assert.Equal(vector, _embedder.Calls > 0);
        Assert.Equal(symbol, _index.SymbolCalls > 0);
        Assert.False(response.Degraded);
        Assert.Equal(lexical || vector ? 1 : 0, response.Hits.Count);
        Assert.Equal(lexical ? 1 : 0, response.Hits.Select(h => h.LexicalRank).SingleOrDefault());
        Assert.Equal(vector ? 1 : 0, response.Hits.Select(h => h.VectorRank).SingleOrDefault());
    }

    [Fact]
    public async Task TheQueryVector_IsEmbeddedOnce_ForRepeatedQueriesWithinTheWindow_WithTheIndexModel()
    {
        var clock = new SteppingClock();
        var retriever = new HybridRetriever(_embedder, Generous, time: clock);

        await retriever.SearchAsync(new RetrievalQuery { Text = "alpha" }, [_index], CancellationToken.None);
        await retriever.SearchAsync(new RetrievalQuery { Text = "alpha" }, [_index], CancellationToken.None);
        Assert.Equal(1, _embedder.Calls);
        Assert.Equal(("alpha", "fake/model", 4), _embedder.Requests[0]);

        await retriever.SearchAsync(new RetrievalQuery { Text = "beta" }, [_index], CancellationToken.None);
        Assert.Equal(2, _embedder.Calls);

        clock.Advance(TimeSpan.FromMinutes(2));
        await retriever.SearchAsync(new RetrievalQuery { Text = "alpha" }, [_index], CancellationToken.None);
        Assert.Equal(3, _embedder.Calls);
    }

    [Fact]
    public async Task AQueryVectorOfTheWrongSize_SkipsTheVectorLeg_WithAReason()
    {
        _index.VectorDimensions = 8;
        _index.LexicalReturns(Chunk("src/A.cs"));

        var response = await SearchAsync("alpha");

        Assert.True(response.Degraded);
        Assert.Contains(response.DegradedReasons, r => r.Contains("4 dimensions, the index 8", StringComparison.Ordinal));
        Assert.Equal(0, _index.VectorCalls);
    }

    [Fact]
    public async Task PartitionAndLanguageFilters_ReachEveryLeg()
    {
        var file = _index.AddFile("src/a.py", "python", "tools");
        var ids = _index.AddChunks(file, 2);
        _index.SymbolList.Add(new SymbolRecord(1, file, "restock", "function", null, 1, 3, null));

        var filtered = await SearchAsync("restock", partitions: ["src"], languages: ["csharp"]);
        var matching = await SearchAsync("restock", partitions: ["tools"]);

        Assert.All(_index.Filters, f => Assert.NotNull(f));
        Assert.Contains(_index.Filters, f => f!.Partitions!.SequenceEqual(["src"]) && f.Languages!.SequenceEqual(["csharp"]));
        Assert.Empty(filtered.Hits); // the symbol leg filters by the file's partition and language itself
        Assert.Equal(ids[0], Assert.Single(matching.Hits).ChunkId);
    }

    [Fact]
    public async Task SeveralIndexes_AreMergedByScore_AndHitsCarryTheirOwnIndexId()
    {
        var second = new FakeIndex("fedcba9876543210fedcba9876543210", @"D:\other");
        var a = Chunk("src/A.cs");
        var f = second.AddFile("lib/B.cs");
        var b = second.AddChunk(f, 1);
        _index.Lexical = (_, _, _) => Task.FromResult<IReadOnlyList<LexicalHit>>([new LexicalHit(a, 5)]);
        second.Lexical = (_, _, _) => Task.FromResult<IReadOnlyList<LexicalHit>>([new LexicalHit(b, 9)]);
        var retriever = new HybridRetriever(_embedder, Generous);

        var response = await retriever.SearchAsync(new RetrievalQuery { Text = "alpha", Mode = RetrievalSearchMode.Lexical }, [_index, second], CancellationToken.None);

        Assert.Equal(["fedcba9876543210fedcba9876543210:" + b, _index.IndexId + ":" + a], response.Hits.Select(h => h.QualifiedChunkId));
        Assert.Equal(@"D:\other\lib\B.cs", response.Hits[0].FilePath);
        Assert.Equal([1, 2], response.Hits.Select(h => h.LexicalRank));
    }

    [Fact]
    public async Task NoIndexesOrABlankQuery_GiveAnEmptyResponse()
    {
        var retriever = new HybridRetriever(_embedder, Generous);

        Assert.Empty((await retriever.SearchAsync(new RetrievalQuery { Text = "alpha" }, [], CancellationToken.None)).Hits);
        Assert.Empty((await retriever.SearchAsync(new RetrievalQuery { Text = "  " }, [_index], CancellationToken.None)).Hits);
        Assert.Equal(0, _index.LexicalCalls);
    }

    [Fact]
    public async Task TheCallersCancellation_StopsTheSearch()
    {
        _index.Lexical = async (_, _, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(10), ct);
            return [];
        };
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        var retriever = new HybridRetriever(_embedder, Generous);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            retriever.SearchAsync(new RetrievalQuery { Text = "alpha", TimeBudget = TimeSpan.FromSeconds(30) }, [_index], cancel.Token));
    }

    [Fact]
    public async Task AReranker_ReordersTheTopCandidates()
    {
        var a = Chunk("src/A.cs");
        var b = Chunk("src/B.cs");
        _index.LexicalReturns(a, b);
        var retriever = new HybridRetriever(_embedder, Generous, reranker: new ReversingReranker());

        var response = await retriever.SearchAsync(new RetrievalQuery { Text = "alpha" }, [_index], CancellationToken.None);

        Assert.Equal([b, a], response.Hits.Select(h => h.ChunkId));
        Assert.Equal(1.0 / 62, response.Hits[0].FusedScore, Precision); // the fused score is kept; the score is the reranker's
        Assert.Equal(2.0, response.Hits[0].Score);
        Assert.Equal("none", NoOpReranker.Instance.Name);
    }

    private long Chunk(string path, string language = "csharp", string partition = "src")
    {
        var file = _index.AddFile(path, language, partition);
        return _index.AddChunk(file, 1);
    }

    private Task<RetrievalResponse> SearchAsync(
        string text,
        RetrievalSearchMode mode = RetrievalSearchMode.Hybrid,
        string[]? focus = null,
        int maxPerFile = 0,
        int maxResults = 0,
        bool includeText = false,
        TimeSpan? budget = null,
        string[]? partitions = null,
        string[]? languages = null) =>
        new HybridRetriever(_embedder, Generous).SearchAsync(
            new RetrievalQuery
            {
                Text = text,
                Mode = mode,
                FocusFiles = focus ?? [],
                MaxPerFile = maxPerFile,
                MaxResults = maxResults,
                IncludeText = includeText,
                TimeBudget = budget ?? TimeSpan.FromSeconds(5),
                Partitions = partitions ?? [],
                Languages = languages ?? [],
            },
            [_index],
            CancellationToken.None);

    private sealed class ReversingReranker : IReranker
    {
        public string Name => "reverse";

        public Task<IReadOnlyList<RerankCandidate>> RerankAsync(string query, IReadOnlyList<RerankCandidate> candidates, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<RerankCandidate>>([.. candidates.Reverse().Select((c, i) => c with { Score = candidates.Count - i })]);
    }
}
