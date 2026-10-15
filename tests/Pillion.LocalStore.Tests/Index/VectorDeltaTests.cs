using Pillion.LocalStore.Index;

namespace Pillion.LocalStore.Tests.Index;

/// <summary>
/// Incremental updates of the in-memory vector copy (work item 2.10): a write that sets
/// <see cref="IndexWriteSession.UpdateVectorCache"/> brings the copy up to date with its commit instead of leaving it to
/// reload every vector, and every search still ranks exactly as the table scan does.
/// </summary>
public sealed class VectorDeltaTests : IDisposable
{
    private const int Dims = 384;
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task ATrackedWrite_AddsAndRemovesVectorsInTheCopy_WithoutAReload()
    {
        await using var storage = await IndexTestSupport.OpenAsync(_temp);
        var random = new Random(2010);
        var files = await FillAsync(storage, random, files: 20, chunksPerFile: 25);
        Assert.True(await storage.WarmVectorCacheAsync() > 0);

        var added = IndexTestSupport.RandomVector(random, Dims);
        var newFile = await storage.AddFileAsync("src/Saved.cs");
        var gone = files[3];
        var ids = await storage.WriteAsync(
            (session, _) =>
            {
                session.UpdateVectorCache = true;
                session.DeleteFileChunks(gone);
                return session.ReplaceFileChunks(newFile, [IndexTestSupport.Chunk("void Saved() {}", embedding: added), IndexTestSupport.Chunk("void Other() {}", 3, embedding: IndexTestSupport.RandomVector(random, Dims))]);
            });

        Assert.True(storage.LastVectorDeltaApplied);
        var hits = await storage.Vectors.SearchAsync(added, 1);
        Assert.Equal(ids[0], hits[0].ChunkId);
        Assert.Equal(0.0, hits[0].Distance, 6);
        Assert.Equal(20L * 25 - 25 + 2, await storage.CountAsync("SELECT count(*) FROM chunk_embeddings"));
        await AssertSameAsTableScanAsync(storage, random);
    }

    [Fact]
    public async Task ReplacingAFilesChunks_DropsItsOldVectorsFromTheCopy()
    {
        await using var storage = await IndexTestSupport.OpenAsync(_temp);
        var random = new Random(7);
        var files = await FillAsync(storage, random, files: 5, chunksPerFile: 10);
        await storage.WarmVectorCacheAsync();
        var oldIds = (await storage.Chunks.GetChunksByFileAsync(files[0])).Select(c => c.Id).ToHashSet();

        await storage.WriteAsync(
            (session, _) =>
            {
                session.UpdateVectorCache = true;
                return session.ReplaceFileChunks(files[0], [IndexTestSupport.Chunk("void Edited() {}", embedding: IndexTestSupport.RandomVector(random, Dims))]);
            });

        Assert.True(storage.LastVectorDeltaApplied);
        var hits = await storage.Vectors.SearchAsync(IndexTestSupport.RandomVector(random, Dims), 60);
        Assert.DoesNotContain(hits, h => oldIds.Contains(h.ChunkId));
        Assert.Equal(41, hits.Count);
        await AssertSameAsTableScanAsync(storage, random);
    }

    [Fact]
    public async Task VectorsAddedLater_ToChunksStoredWithoutThem_ReachTheCopy()
    {
        await using var storage = await IndexTestSupport.OpenAsync(_temp);
        var random = new Random(11);
        await FillAsync(storage, random, files: 4, chunksPerFile: 10);
        await storage.WarmVectorCacheAsync();
        var file = await storage.AddFileAsync("src/Big.cs");
        var first = IndexTestSupport.RandomVector(random, Dims);
        var late = IndexTestSupport.RandomVector(random, Dims);

        // The save path's budget ran out: the second chunk is stored without a vector.
        var ids = await storage.WriteAsync(
            (session, _) =>
            {
                session.UpdateVectorCache = true;
                return session.ReplaceFileChunks(file, [IndexTestSupport.Chunk("void A() {}", embedding: first), IndexTestSupport.Chunk("void B() {}", 3)]);
            });
        Assert.True(storage.LastVectorDeltaApplied);
        Assert.NotEqual(ids[1], (await storage.Vectors.SearchAsync(late, 1))[0].ChunkId);

        await storage.WriteAsync(
            (session, token) =>
            {
                session.UpdateVectorCache = true;
                return session.UpsertEmbeddings([new ChunkEmbedding(ids[1], late)], token);
            });

        Assert.True(storage.LastVectorDeltaApplied);
        Assert.Equal(ids[1], (await storage.Vectors.SearchAsync(late, 1))[0].ChunkId);
        await AssertSameAsTableScanAsync(storage, random);
    }

    [Fact]
    public async Task AChangeTheWriteDidNotMake_LeavesTheCopyToReload_AndSearchStaysCorrect()
    {
        await using var storage = await IndexTestSupport.OpenAsync(_temp);
        var random = new Random(13);
        var files = await FillAsync(storage, random, files: 4, chunksPerFile: 10);
        await storage.WarmVectorCacheAsync();

        // Outside any tracked write: the copy is now one generation behind.
        await storage.ExecuteAsync($"DELETE FROM chunks WHERE file_id = {files[1]}");
        var vector = IndexTestSupport.RandomVector(random, Dims);
        var ids = await storage.WriteAsync(
            (session, _) =>
            {
                session.UpdateVectorCache = true;
                return session.ReplaceFileChunks(files[2], [IndexTestSupport.Chunk("void C() {}", embedding: vector)]);
            });

        Assert.False(storage.LastVectorDeltaApplied);
        Assert.Equal(ids[0], (await storage.Vectors.SearchAsync(vector, 1))[0].ChunkId);
        await AssertSameAsTableScanAsync(storage, random);
    }

    [Fact]
    public async Task UpsertThroughTheVectorIndex_IsAppliedToTheCopy()
    {
        await using var storage = await IndexTestSupport.OpenAsync(_temp);
        var random = new Random(17);
        await FillAsync(storage, random, files: 3, chunksPerFile: 10);
        await storage.WarmVectorCacheAsync();
        var target = (await storage.Chunks.GetChunksByFileAsync(1))[0].Id;
        var vector = IndexTestSupport.RandomVector(random, Dims);

        await storage.Vectors.UpsertAsync(target, vector, new VectorMetadata("csharp", "src"));

        Assert.Equal(target, (await storage.Vectors.SearchAsync(vector, 1))[0].ChunkId);
        await AssertSameAsTableScanAsync(storage, random);
    }

    private static async Task AssertSameAsTableScanAsync(IndexStorage storage, Random random)
    {
        for (var q = 0; q < 10; q++)
        {
            var query = IndexTestSupport.RandomVector(random, Dims);
            var fromCopy = await storage.Vectors.SearchAsync(query, 10);
            var fromTable = await storage.TableScan.SearchAsync(query, 10);
            Assert.Equal(fromTable.Select(h => h.ChunkId), fromCopy.Select(h => h.ChunkId));
        }
    }

    private static async Task<List<long>> FillAsync(IndexStorage storage, Random random, int files, int chunksPerFile)
    {
        var ids = new List<long>();
        for (var f = 0; f < files; f++)
        {
            var file = await storage.AddFileAsync($"src/File{f}.cs");
            ids.Add(file);
            var chunks = Enumerable.Range(0, chunksPerFile)
                .Select(c => IndexTestSupport.Chunk($"void M{c}() {{}}", c * 3 + 1, embedding: IndexTestSupport.RandomVector(random, Dims)))
                .ToList();
            await storage.Chunks.ReplaceFileChunksAsync(file, chunks);
        }

        return ids;
    }
}
