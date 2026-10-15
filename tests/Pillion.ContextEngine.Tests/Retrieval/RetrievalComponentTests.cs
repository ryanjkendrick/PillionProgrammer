using Pillion.ContextEngine.Retrieval;
using Pillion.ContextEngine.Symbols;
using Pillion.LocalStore.Index;
using Xunit.Abstractions;

namespace Pillion.ContextEngine.Tests.Retrieval;

/// <summary>
/// Hybrid retrieval over a real encrypted index built from the chunker fixtures (fifteen files in fifteen languages): the
/// chunkers, FTS5 with identifier splitting, the in-memory int8 vector search, the stored symbols and the in-memory symbol
/// graph, with a deterministic hashing embedder in place of the model.
/// </summary>
public sealed class RetrievalComponentTests : IAsyncLifetime, IDisposable
{
    private readonly RetrievalTempDirectory _temp = new();
    private readonly HashingEmbedder _embedder = new();
    private readonly ITestOutputHelper _output;
    private IndexStorage _storage = null!;
    private SymbolGraph _graph = null!;

    public RetrievalComponentTests(ITestOutputHelper output) => _output = output;

    public async Task InitializeAsync() => (_storage, _graph) = await FixtureIndex.BuildAsync(_temp, _embedder);

    public async Task DisposeAsync() => await _storage.DisposeAsync();

    public void Dispose() => _temp.Dispose();

    [Theory]
    [InlineData("where are orders placed", "src/OrderService.java", "place")]
    [InlineData("how is stock added to an item", "src/inventory.py", "restock")]
    [InlineData("which function starts listening for http requests", "src/server.go", "Start")]
    public async Task ANaturalLanguageQuestion_FindsTheExpectedFileAndSymbol(string question, string file, string symbol)
    {
        var response = await SearchAsync(question, graph: null);

        Report(question, response);
        Assert.False(response.Degraded, string.Join("; ", response.DegradedReasons));
        Assert.Equal(file, response.Hits[0].RelativePath);

        // The symbol's definition is shown in one of the top three hits: as the hit itself or within its expanded span.
        var definition = _graph.FindDefinitions(symbol).Single(d => d.FilePath == file);
        Assert.Contains(response.Hits.Take(3), h => h.RelativePath == file && h.StartLine <= definition.StartLine && h.EndLine >= definition.EndLine);
    }

    [Fact]
    public async Task ASymbolNamedInTheQuestion_IsFoundExactly_ThroughTheGraphOrTheStoredSymbols()
    {
        foreach (var graph in new[] { _graph, null })
        {
            var response = await SearchAsync("what does restock return", graph);

            Report("restock (graph: " + (graph is not null) + ")", response);
            var top = response.Hits[0];
            Assert.Equal("src/inventory.py", top.RelativePath);
            Assert.True(EndsWithSegment(top.Breadcrumb, "restock"), top.Breadcrumb);
            Assert.Equal(1, top.SymbolRank);
        }
    }

    [Fact]
    public async Task Hits_CarryLinesTextAndRanksThatMatchTheIndex()
    {
        var response = await SearchAsync("what does restock return", graph: null, includeText: true);

        var hit = response.Hits.First(h => EndsWithSegment(h.Breadcrumb, "restock"));
        var lines = Fixture.Read("python/inventory.py").Split('\n');
        var expected = string.Join('\n', lines[(hit.StartLine - 1)..hit.EndLine]).Replace("\r", string.Empty, StringComparison.Ordinal);
        Assert.Equal(expected.TrimEnd('\n'), hit.Text!.TrimEnd('\n'));
        Assert.Contains("def restock", hit.Text, StringComparison.Ordinal);
        Assert.True(hit.LexicalRank > 0 || hit.VectorRank > 0);
        Assert.Equal(FixtureIndex.IndexId + ":" + hit.ChunkId, hit.QualifiedChunkId);
        Assert.Equal("python", hit.Language);
        Assert.Equal("src", hit.Partition);
        Assert.Equal(Path.Combine(_temp.Path, "src", "inventory.py"), hit.FilePath);
        Assert.True(response.Hits.Count(h => h.RelativePath == "src/inventory.py") <= 3);
    }

    [Fact]
    public async Task TheLanguageFilter_KeepsOtherLanguagesOut()
    {
        var response = await SearchAsync("greet a person by name", graph: null, languages: ["vb"]);

        Assert.NotEmpty(response.Hits);
        Assert.All(response.Hits, h => Assert.Equal("vb", h.Language));
    }

    [Fact]
    public async Task TheIndexMeta_GivesTheModelAndVectorSize_AndTheQueryIsEmbeddedWithThem()
    {
        var index = await StorageRetrievalIndex.CreateAsync(FixtureIndex.IndexId, _temp.Path, _storage, null, CancellationToken.None);

        Assert.Equal(FixtureIndex.ModelId, index.EmbeddingModelId);
        Assert.Equal(_embedder.Dimensions, index.VectorDimensions);
        var named = await index.FindFilesByNameAsync(["inventory.py", "src/shapes.ts", "nothing.cs"], CancellationToken.None);
        Assert.Equal(["inventory.py", "src/shapes.ts"], named.Keys.Order());
        var exact = await index.FindFilesAsync(["SRC/INVENTORY.PY"], CancellationToken.None);
        Assert.Equal("src/inventory.py", exact["SRC/INVENTORY.PY"].Path);
    }

    private async Task<RetrievalResponse> SearchAsync(string question, SymbolGraph? graph, bool includeText = false, string[]? languages = null)
    {
        var index = await StorageRetrievalIndex.CreateAsync(FixtureIndex.IndexId, _temp.Path, _storage, graph, CancellationToken.None);
        var retriever = new HybridRetriever(_embedder);
        return await retriever.SearchAsync(
            new RetrievalQuery { Text = question, IncludeText = includeText, Languages = languages ?? [], TimeBudget = TimeSpan.FromSeconds(10) },
            [index],
            CancellationToken.None);
    }

    private static bool EndsWithSegment(string breadcrumb, string name)
    {
        var last = breadcrumb.Split(" > ")[^1];
        var paren = last.IndexOf(" (part", StringComparison.Ordinal);
        last = paren > 0 ? last[..paren] : last;
        return last == name || last.StartsWith(name + "(", StringComparison.Ordinal) || last.StartsWith(name + "<", StringComparison.Ordinal);
    }

    private void Report(string question, RetrievalResponse response)
    {
        _output.WriteLine($"{question}: lexical {response.LexicalMs:F1} ms, vector {response.VectorMs:F1} ms, symbol {response.SymbolMs:F1} ms, fusion {response.FusionMs:F1} ms, total {response.TotalMs:F1} ms");
        foreach (var hit in response.Hits)
        {
            _output.WriteLine($"  {hit.Score:F5} L{hit.LexicalRank} V{hit.VectorRank} S{hit.SymbolRank} F{hit.FocusRank} {hit.RelativePath}:{hit.StartLine}-{hit.EndLine} {hit.Breadcrumb}");
        }
    }
}
