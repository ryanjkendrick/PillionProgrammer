using Pillion.LocalStore.Index;

namespace Pillion.LocalStore.Tests.Index;

public sealed class LexicalSearchTests : IAsyncLifetime, IDisposable
{
    private readonly TempDirectory _temp = new();
    private IndexStorage _storage = null!;
    private long _orders;
    private long _shipping;
    private long _docs;
    private long _orderTotalId;
    private long _snakeId;
    private long _proseId;
    private long _shippingId;
    private long _breadcrumbId;

    public async Task InitializeAsync()
    {
        _storage = await IndexTestSupport.OpenAsync(_temp);
        _orders = await _storage.AddFileAsync("src/Orders/OrderService.cs", "csharp", "src/Orders");
        _shipping = await _storage.AddFileAsync("tools/ship.py", "python", "tools");
        _docs = await _storage.AddFileAsync("docs/notes.md", "markdown", "docs");

        var orderIds = await _storage.Chunks.ReplaceFileChunksAsync(_orders, [
            IndexTestSupport.Chunk("public decimal GetOrderTotal(Order order) => order.Lines.Sum(l => l.Price);", 10),
            IndexTestSupport.Chunk("void Log() { Console.WriteLine(\"started\"); }", 20, breadcrumb: "src/Orders/OrderService.cs > Pillion.Orders > OrderService > Log"),
        ]);
        _orderTotalId = orderIds[0];
        _breadcrumbId = orderIds[1];

        var shippingIds = await _storage.Chunks.ReplaceFileChunksAsync(_shipping, [
            IndexTestSupport.Chunk("def get_order_total(order_id):\n    return totals[order_id]", 1),
            IndexTestSupport.Chunk("def ship_parcel(parcel):\n    carrier.send(parcel)", 5),
        ]);
        _snakeId = shippingIds[0];
        _shippingId = shippingIds[1];

        _proseId = (await _storage.Chunks.ReplaceFileChunksAsync(_docs, [
            IndexTestSupport.Chunk("The order total includes tax. The order total is shown on the invoice.", 1, kind: "window"),
        ]))[0];
    }

    public async Task DisposeAsync()
    {
        await _storage.DisposeAsync();
    }

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task ACamelCaseQuery_MatchesTheSameIdentifierInSnakeCase()
    {
        var hits = await Ids("getOrderTotal");

        Assert.Contains(_orderTotalId, hits);
        Assert.Contains(_snakeId, hits);
    }

    [Fact]
    public async Task PlainWords_MatchTheIdentifiersTheySplitFrom()
    {
        var hits = await Ids("order total");

        Assert.Contains(_orderTotalId, hits);
        Assert.Contains(_snakeId, hits);
        Assert.Contains(_proseId, hits);
        Assert.DoesNotContain(_shippingId, hits);
    }

    [Fact]
    public async Task AWholeSnakeCaseIdentifier_RanksTheChunkThatHasItExactlyFirst()
    {
        var hits = await _storage.Chunks.SearchLexicalAsync("get_order_total", 10);

        Assert.Equal(_snakeId, hits[0].ChunkId);
    }

    [Fact]
    public async Task TheLastToken_MatchesAsAPrefix()
    {
        Assert.Contains(_shippingId, await Ids("ship_par"));
        Assert.Contains(_shippingId, await Ids("carr"));

        // Only the last token is a prefix.
        Assert.DoesNotContain(_shippingId, await Ids("carr invoice"));
    }

    [Fact]
    public async Task TheBreadcrumb_IsSearchable()
    {
        Assert.Contains(_breadcrumbId, await Ids("OrderService Log"));
        Assert.Equal(_breadcrumbId, (await _storage.Chunks.SearchLexicalAsync("Pillion.Orders", 10))[0].ChunkId);
    }

    [Fact]
    public async Task Bm25_RanksMoreAndRarerMatchesHigher_AndScoresDescend()
    {
        var hits = await _storage.Chunks.SearchLexicalAsync("order total invoice", 10);

        // The prose chunk has every word, twice, and the only "invoice".
        Assert.Equal(_proseId, hits[0].ChunkId);
        Assert.True(hits.Zip(hits.Skip(1)).All(pair => pair.First.Score >= pair.Second.Score));
        Assert.All(hits, hit => Assert.True(hit.Score > 0));
    }

    [Fact]
    public async Task ColumnWeights_FavourTheWholeIdentifierColumn()
    {
        // "getordertotal" (text column) and "get order total" (identifiers column) both match chunk 1; with the text column
        // weighted to zero only the split identifiers count, so the score drops.
        await using var weighted = await IndexStorage.CreateAsync(_storage.Database, ownsDatabase: false, new IndexStorageOptions { TextWeight = 0.0, IdentifiersWeight = 1.0 });
        var normal = (await _storage.Chunks.SearchLexicalAsync("GetOrderTotal", 10)).Single(h => h.ChunkId == _orderTotalId).Score;
        var identifiersOnly = (await weighted.Chunks.SearchLexicalAsync("GetOrderTotal", 10)).Single(h => h.ChunkId == _orderTotalId).Score;

        Assert.True(normal > identifiersOnly, $"{normal} should exceed {identifiersOnly}");
    }

    [Fact]
    public async Task Filters_KeepOnlyThePartitionsAndLanguagesAsked()
    {
        Assert.Equal([_orderTotalId], await Ids("getOrderTotal", new ChunkFilter { Partitions = ["src/Orders"] }));
        Assert.Equal([_snakeId], await Ids("getOrderTotal", new ChunkFilter { Languages = ["python"] }));
        Assert.Empty(await Ids("getOrderTotal", new ChunkFilter { Partitions = ["src/Orders"], Languages = ["python"] }));
        Assert.Equal(2, (await Ids("getOrderTotal", new ChunkFilter { Partitions = ["src/Orders", "tools"] })).Count);
    }

    [Fact]
    public async Task K_LimitsTheResults()
    {
        Assert.Single(await _storage.Chunks.SearchLexicalAsync("order", 1));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("(*) -- \"\" ^")]
    public async Task AQueryWithoutTokens_ReturnsNothing(string query)
    {
        Assert.Empty(await _storage.Chunks.SearchLexicalAsync(query, 10));
    }

    [Theory]
    [InlineData("order\" OR \"x")]
    [InlineData("NEAR(order total)")]
    [InlineData("order AND NOT total")]
    [InlineData("text:order")]
    [InlineData("{identifiers}: order")]
    public async Task Fts5Syntax_InTheQuery_IsTreatedAsWords(string query)
    {
        var hits = await _storage.Chunks.SearchLexicalAsync(query, 10);

        Assert.NotEmpty(hits);
    }

    [Fact]
    public async Task ReplacingAFile_RemovesItsOldChunksFromTheIndex()
    {
        await _storage.Chunks.ReplaceFileChunksAsync(_shipping, [IndexTestSupport.Chunk("def unrelated(): pass")]);

        Assert.DoesNotContain(_snakeId, await Ids("getOrderTotal"));
        Assert.Empty(await Ids("carrier"));
    }

    private async Task<List<long>> Ids(string query, ChunkFilter? filter = null) =>
        (await _storage.Chunks.SearchLexicalAsync(query, 10, filter)).Select(h => h.ChunkId).ToList();
}
