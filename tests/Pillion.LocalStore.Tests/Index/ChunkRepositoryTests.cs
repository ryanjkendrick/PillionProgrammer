using Pillion.LocalStore.Index;

namespace Pillion.LocalStore.Tests.Index;

public sealed class ChunkRepositoryTests : IAsyncLifetime, IDisposable
{
    private readonly TempDirectory _temp = new();
    private IndexStorage _storage = null!;

    public async Task InitializeAsync() => _storage = await IndexTestSupport.OpenAsync(_temp);

    public async Task DisposeAsync()
    {
        await _storage.DisposeAsync();
    }

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task ReplaceFileChunks_StoresTheChunks_AndReadsThemBack()
    {
        var file = await _storage.AddFileAsync("src/Orders.cs");
        var ids = await _storage.Chunks.ReplaceFileChunksAsync(file, [
            new NewChunk(1, 4, "type-header", "src/Orders.cs > Shop > Orders", "public class Orders\n{\n    // header\n}", 12),
            new NewChunk(6, 9, "method", "src/Orders.cs > Shop > Orders > Total", "public decimal Total()\n{\n    return 0;\n}", 15),
        ]);

        Assert.Equal(2, ids.Count);
        Assert.True(ids[1] > ids[0]);
        var first = await _storage.Chunks.GetChunkAsync(ids[0]);
        Assert.Equal(new ChunkRecord(ids[0], file, 1, 4, "type-header", "src/Orders.cs > Shop > Orders", "public class Orders\n{\n    // header\n}", 12), first);
        Assert.Equal(ids, (await _storage.Chunks.GetChunksByFileAsync(file)).Select(c => c.Id));
        Assert.Equal([ids[1], ids[0]], (await _storage.Chunks.GetChunksAsync([ids[1], 424242, ids[0]])).Select(c => c.Id));
        Assert.Null(await _storage.Chunks.GetChunkAsync(424242));
        Assert.Equal(2L, await _storage.Chunks.CountChunksAsync());
        Assert.Equal(2L, await _storage.CountAsync("SELECT count(*) FROM chunks_fts"));
    }

    [Fact]
    public async Task ReplacingAgain_ReplacesEverything_AndNeverReusesIds()
    {
        var file = await _storage.AddFileAsync("src/A.cs");
        var first = await _storage.Chunks.ReplaceFileChunksAsync(file, [IndexTestSupport.Chunk("void Old() {}"), IndexTestSupport.Chunk("void Older() {}", 3)]);

        var second = await _storage.Chunks.ReplaceFileChunksAsync(file, [IndexTestSupport.Chunk("void New() {}")]);

        Assert.True(second[0] > first.Max());
        Assert.Equal(["void New() {}"], (await _storage.Chunks.GetChunksByFileAsync(file)).Select(c => c.Text));
        Assert.Equal(1L, await _storage.CountAsync("SELECT count(*) FROM chunks_fts"));
        Assert.Empty(await _storage.Chunks.ReplaceFileChunksAsync(file, []));
        Assert.Equal(0L, await _storage.Chunks.CountChunksAsync());
    }

    [Fact]
    public async Task ABatch_IsOneTransaction_SoAnUnknownFileWritesNothing()
    {
        var file = await _storage.AddFileAsync("src/A.cs");

        await Assert.ThrowsAsync<KeyNotFoundException>(() => _storage.Chunks.ReplaceFilesChunksAsync([
            new FileChunks(file, [IndexTestSupport.Chunk("void A() {}")]),
            new FileChunks(9999, [IndexTestSupport.Chunk("void Ghost() {}")]),
        ]));

        Assert.Equal(0L, await _storage.Chunks.CountChunksAsync());
        Assert.Equal(0L, await _storage.CountAsync("SELECT count(*) FROM chunks_fts"));
    }

    [Fact]
    public async Task ABatchOfFiles_ReturnsTheIdsPerFile()
    {
        var a = await _storage.AddFileAsync("src/A.cs");
        var b = await _storage.AddFileAsync("src/B.cs");

        var ids = await _storage.Chunks.ReplaceFilesChunksAsync([
            new FileChunks(a, [IndexTestSupport.Chunk("void A1() {}"), IndexTestSupport.Chunk("void A2() {}", 2)]),
            new FileChunks(b, [IndexTestSupport.Chunk("void B1() {}")]),
        ]);

        Assert.Equal([2, 1], ids.Select(i => i.Count));
        Assert.Equal(b, (await _storage.Chunks.GetChunkAsync(ids[1][0]))!.FileId);
    }

    [Fact]
    public async Task TheListing_CountsChunksTokensAndVectorsPerFile()
    {
        var random = new Random(1);
        var a = await _storage.AddFileAsync("src/B.cs");
        var b = await _storage.AddFileAsync("src/A.cs");
        await _storage.AddFileAsync("src/Empty.cs");
        await _storage.Chunks.ReplaceFileChunksAsync(a, [
            new NewChunk(1, 2, "method", string.Empty, "void X() {}", 10) { Embedding = IndexTestSupport.RandomVector(random, 16) },
            new NewChunk(3, 4, "method", string.Empty, "void Y() {}", 5),
        ]);
        await _storage.Chunks.ReplaceFileChunksAsync(b, [new NewChunk(1, 1, "window", string.Empty, "x", 1)]);

        var listing = await _storage.Chunks.ListFilesAsync();

        Assert.Equal(
            [new FileChunkSummary(b, "src/A.cs", 1, 1, 0), new FileChunkSummary(a, "src/B.cs", 2, 15, 1)],
            listing);
    }

    [Fact]
    public async Task TheSize_ComesFromPageAccounting_AndGrowsWithChunks()
    {
        var before = await _storage.Chunks.GetSizeAsync();
        var file = await _storage.AddFileAsync("src/Big.cs");
        await _storage.Chunks.ReplaceFileChunksAsync(file, Enumerable.Range(0, 500).Select(i => IndexTestSupport.Chunk($"public int Member{i}() => {i} * SomeValue{i};", i + 1)).ToList());

        var after = await _storage.Chunks.GetSizeAsync();

        Assert.Equal(4096L, after.PageSize);
        Assert.Equal(after.PageCount * after.PageSize, after.Bytes);
        Assert.True(after.Bytes > before.Bytes);
        Assert.True(after.WalBytes > 0);
    }

    [Fact]
    public async Task UnknownFiles_AreRejected()
    {
        await Assert.ThrowsAsync<KeyNotFoundException>(() => _storage.Chunks.ReplaceFileChunksAsync(77, [IndexTestSupport.Chunk("x")]));
    }
}
