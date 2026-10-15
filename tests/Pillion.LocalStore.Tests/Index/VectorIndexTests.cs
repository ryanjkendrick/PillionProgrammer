using Pillion.LocalStore.Index;

namespace Pillion.LocalStore.Tests.Index;

public sealed class VectorIndexTests : IDisposable
{
    private const int Dims = 384;
    private static readonly string[] Partitions = ["src/Api", "src/Core", "tests"];
    private static readonly string[] Languages = ["csharp", "typescript"];
    private static readonly string[] SourcePartitions = ["src/Api", "src/Core"];
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task BothBackends_ReturnTheSameTopK()
    {
        await using var storage = await IndexTestSupport.OpenAsync(_temp);
        var random = new Random(20261015);
        await FillAsync(storage, random, files: 60, chunksPerFile: 50);

        for (var q = 0; q < 25; q++)
        {
            var query = IndexTestSupport.RandomVector(random, Dims);
            var vec = await storage.SqliteVec!.SearchAsync(query, 10);
            var brute = await storage.BruteForce.SearchAsync(query, 10);

            Assert.Equal(10, vec.Count);
            Assert.Equal(vec.Select(h => h.ChunkId), brute.Select(h => h.ChunkId));
            for (var i = 0; i < vec.Count; i++)
            {
                Assert.Equal(vec[i].Distance, brute[i].Distance, 6);
            }
        }
    }

    [Fact]
    public async Task TheNearestVector_IsTheVectorItself_AtDistanceZero()
    {
        await using var storage = await IndexTestSupport.OpenAsync(_temp);
        var vectors = await FillAsync(storage, new Random(1), files: 5, chunksPerFile: 20);
        var (id, vector) = vectors[37];

        foreach (var index in new IVectorIndex[] { storage.SqliteVec!, storage.BruteForce })
        {
            var hits = await index.SearchAsync(vector, 3);
            Assert.Equal(id, hits[0].ChunkId);
            Assert.Equal(0.0, hits[0].Distance, 6);
            Assert.True(hits[1].Distance > hits[0].Distance);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Filters_ByPartitionAndLanguage_KeepOnlyMatchingChunks(bool sqliteVec)
    {
        await using var storage = await IndexTestSupport.OpenAsync(_temp);
        var random = new Random(7);
        await FillAsync(storage, random, files: 12, chunksPerFile: 30);
        var index = sqliteVec ? (IVectorIndex)storage.SqliteVec! : storage.BruteForce;
        var metadata = await MetadataByChunkAsync(storage);

        var query = IndexTestSupport.RandomVector(random, Dims);
        var byPartition = await index.SearchAsync(query, 20, new ChunkFilter { Partitions = ["tests"] });
        var byBoth = await index.SearchAsync(query, 20, new ChunkFilter { Partitions = ["src/Api", "src/Core"], Languages = ["typescript"] });

        Assert.Equal(20, byPartition.Count);
        Assert.All(byPartition, hit => Assert.Equal("tests", metadata[hit.ChunkId].Partition));
        Assert.Equal(20, byBoth.Count);
        Assert.All(byBoth, hit =>
        {
            Assert.Contains(metadata[hit.ChunkId].Partition, SourcePartitions);
            Assert.Equal("typescript", metadata[hit.ChunkId].Language);
        });

        // The filtered results are the best of the filtered set, as an unfiltered brute-force scan shows.
        var all = await storage.BruteForce.SearchAsync(query, 360);
        var expected = all.Where(h => metadata[h.ChunkId].Partition == "tests").Take(20).Select(h => h.ChunkId);
        Assert.Equal(expected, byPartition.Select(h => h.ChunkId));
    }

    [Fact]
    public async Task DeletingAFilesChunks_RemovesTheirFtsAndVectorRows()
    {
        await using var storage = await IndexTestSupport.OpenAsync(_temp);
        var random = new Random(3);
        var keep = await storage.AddFileAsync("src/Keep.cs");
        var drop = await storage.AddFileAsync("src/Drop.cs");
        await storage.Chunks.ReplaceFileChunksAsync(keep, [IndexTestSupport.Chunk("void KeepMe() {}", embedding: IndexTestSupport.RandomVector(random, Dims))]);
        var dropIds = await storage.Chunks.ReplaceFileChunksAsync(drop, [
            IndexTestSupport.Chunk("void DropMe() {}", embedding: IndexTestSupport.RandomVector(random, Dims)),
            IndexTestSupport.Chunk("void DropMeToo() {}", 5, embedding: IndexTestSupport.RandomVector(random, Dims)),
        ]);

        Assert.Equal(3L, await storage.CountAsync("SELECT count(*) FROM chunk_vectors"));
        Assert.Equal(2, await storage.Chunks.DeleteFileChunksAsync(drop));

        Assert.Equal(1L, await storage.CountAsync("SELECT count(*) FROM chunks"));
        Assert.Equal(1L, await storage.CountAsync("SELECT count(*) FROM chunk_embeddings"));
        Assert.Equal(1L, await storage.CountAsync("SELECT count(*) FROM chunk_vectors"));
        Assert.Equal(0L, await storage.CountAsync("SELECT count(*) FROM vec_deleted"));
        Assert.Empty(await storage.Chunks.SearchLexicalAsync("DropMe DropMeToo", 10));
        Assert.Single(await storage.Chunks.SearchLexicalAsync("KeepMe", 10));
        foreach (var index in new IVectorIndex[] { storage.SqliteVec!, storage.BruteForce })
        {
            var hits = await index.SearchAsync(IndexTestSupport.RandomVector(random, Dims), 10);
            Assert.Single(hits);
            Assert.DoesNotContain(hits, h => dropIds.Contains(h.ChunkId));
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DeleteByFile_RemovesOnlyTheVectors(bool sqliteVec)
    {
        await using var storage = await IndexTestSupport.OpenAsync(_temp);
        var random = new Random(5);
        var file = await storage.AddFileAsync("src/A.cs");
        var other = await storage.AddFileAsync("src/B.cs");
        await storage.Chunks.ReplaceFileChunksAsync(file, [IndexTestSupport.Chunk("void A() {}", embedding: IndexTestSupport.RandomVector(random, Dims))]);
        await storage.Chunks.ReplaceFileChunksAsync(other, [IndexTestSupport.Chunk("void B() {}", embedding: IndexTestSupport.RandomVector(random, Dims))]);
        var index = sqliteVec ? (IVectorIndex)storage.SqliteVec! : storage.BruteForce;

        Assert.Equal(1, await index.DeleteByFileAsync(file));

        Assert.Equal(2L, await storage.Chunks.CountChunksAsync());
        Assert.Equal(1L, await storage.CountAsync("SELECT count(*) FROM chunk_embeddings"));
        Assert.Equal(1L, await storage.CountAsync("SELECT count(*) FROM chunk_vectors"));
        Assert.Single(await storage.SqliteVec!.SearchAsync(IndexTestSupport.RandomVector(random, Dims), 10));
    }

    [Fact]
    public async Task VectorsWrittenWithoutTheExtension_AreRebuiltIntoSqliteVec_OnTheNextOpen()
    {
        var random = new Random(11);
        await using (var withVec = await IndexTestSupport.OpenAsync(_temp))
        {
            await FillAsync(withVec, random, files: 4, chunksPerFile: 10, firstFile: 0);
        }

        // A host without sqlite-vec adds files, deletes one and re-embeds a chunk: chunk_vectors goes stale.
        await using (var withoutVec = await IndexTestSupport.OpenWithoutExtensionAsync(_temp))
        {
            await FillAsync(withoutVec, random, files: 4, chunksPerFile: 10, firstFile: 4);
            await withoutVec.Chunks.DeleteFileChunksAsync(1);
            await withoutVec.Vectors.UpsertAsync(30, IndexTestSupport.RandomVector(random, Dims), new VectorMetadata("csharp", "tests"));
            Assert.Equal("stale", await withoutVec.Database.ScalarAsync("SELECT value FROM meta WHERE key = 'vector_index_state'"));
            Assert.Equal(70L, await withoutVec.CountAsync("SELECT count(*) FROM chunk_embeddings"));
        }

        await using var reopened = await IndexTestSupport.OpenAsync(_temp);
        Assert.Equal("synced", await reopened.Database.ScalarAsync("SELECT value FROM meta WHERE key = 'vector_index_state'"));
        Assert.Equal(70L, await reopened.CountAsync("SELECT count(*) FROM chunk_vectors"));
        Assert.Equal(0L, await reopened.CountAsync("SELECT count(*) FROM vec_deleted"));
        for (var q = 0; q < 10; q++)
        {
            var query = IndexTestSupport.RandomVector(random, Dims);
            Assert.Equal(
                (await reopened.BruteForce.SearchAsync(query, 10)).Select(h => h.ChunkId),
                (await reopened.SqliteVec!.SearchAsync(query, 10)).Select(h => h.ChunkId));
        }
    }

    [Fact]
    public async Task ChunksDeletedByACascadeFromFiles_LeaveSqliteVec_OnTheNextWrite()
    {
        await using var storage = await IndexTestSupport.OpenAsync(_temp);
        var random = new Random(13);
        await FillAsync(storage, random, files: 3, chunksPerFile: 5);

        await storage.ExecuteAsync("DELETE FROM files WHERE id = 2");
        Assert.Equal(5L, await storage.CountAsync("SELECT count(*) FROM vec_deleted"));

        var file = await storage.AddFileAsync("src/New.cs");
        await storage.Chunks.ReplaceFileChunksAsync(file, [IndexTestSupport.Chunk("void New() {}", embedding: IndexTestSupport.RandomVector(random, Dims))]);

        Assert.Equal(0L, await storage.CountAsync("SELECT count(*) FROM vec_deleted"));
        Assert.Equal(11L, await storage.CountAsync("SELECT count(*) FROM chunk_vectors"));
        Assert.Equal(11L, await storage.CountAsync("SELECT count(*) FROM chunk_embeddings"));
    }

    [Fact]
    public async Task EmbeddingsAddedLater_TakeTheirMetadataFromTheFile()
    {
        await using var storage = await IndexTestSupport.OpenAsync(_temp);
        var random = new Random(17);
        var file = await storage.AddFileAsync("web/app.ts", "typescript", "web");
        var ids = await storage.Chunks.ReplaceFileChunksAsync(file, [IndexTestSupport.Chunk("function a() {}"), IndexTestSupport.Chunk("function b() {}", 3)]);
        var vector = IndexTestSupport.RandomVector(random, Dims);
        var f32 = vector.Select(v => v / 127f).ToArray();

        var stored = await storage.Chunks.UpsertEmbeddingsAsync([
            new ChunkEmbedding(ids[0], vector) { VectorFloat32 = f32 },
            new ChunkEmbedding(999_999, vector),
        ]);

        Assert.Equal(1, stored);
        var hits = await storage.SqliteVec!.SearchAsync(vector, 5, new ChunkFilter { Languages = ["typescript"], Partitions = ["web"] });
        Assert.Equal(ids[0], Assert.Single(hits).ChunkId);
        Assert.Equal(1L, await storage.CountAsync("SELECT count(*) FROM vec_float32"));
        Assert.Equal((long)(Dims * sizeof(float)), await storage.CountAsync("SELECT length(embedding) FROM vec_float32"));
        var listed = Assert.Single(await storage.Chunks.ListFilesAsync());
        Assert.Equal((2, 1), (listed.Chunks, listed.Embedded));
    }

    [Fact]
    public async Task AVectorOfAnotherDimension_IsRejected_AndAnEmptyIndexFindsNothing()
    {
        await using var storage = await IndexTestSupport.OpenAsync(_temp);
        var file = await storage.AddFileAsync("src/A.cs");
        var ids = await storage.Chunks.ReplaceFileChunksAsync(file, [IndexTestSupport.Chunk("void A() {}"), IndexTestSupport.Chunk("void B() {}", 2)]);

        Assert.Empty(await storage.Vectors.SearchAsync(new sbyte[Dims], 5));

        await storage.Vectors.UpsertAsync(ids[0], IndexTestSupport.RandomVector(new Random(1), Dims), new VectorMetadata("csharp", "src"));
        Assert.Equal(Dims.ToString(System.Globalization.CultureInfo.InvariantCulture), await storage.Database.ScalarAsync("SELECT value FROM meta WHERE key = 'vector_dimensions'"));
        await Assert.ThrowsAsync<ArgumentException>(() => storage.Vectors.UpsertAsync(ids[1], new sbyte[Dims / 2], new VectorMetadata("csharp", "src")));
        await Assert.ThrowsAsync<ArgumentException>(() => storage.Vectors.SearchAsync(new sbyte[Dims / 2], 5));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => storage.Vectors.SearchAsync(new sbyte[Dims], 0));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => storage.Vectors.SearchAsync(new sbyte[Dims], IVectorIndex.MaxK + 1));
    }

    [Fact]
    public async Task UpsertingAVectorAgain_ReplacesIt()
    {
        await using var storage = await IndexTestSupport.OpenAsync(_temp);
        var random = new Random(19);
        var file = await storage.AddFileAsync("src/A.cs");
        var ids = await storage.Chunks.ReplaceFileChunksAsync(file, [IndexTestSupport.Chunk("void A() {}", embedding: IndexTestSupport.RandomVector(random, Dims))]);
        var replacement = IndexTestSupport.RandomVector(random, Dims);

        await storage.Vectors.UpsertAsync(ids[0], replacement, new VectorMetadata("csharp", "src"));

        Assert.Equal(1L, await storage.CountAsync("SELECT count(*) FROM chunk_vectors"));
        foreach (var index in new IVectorIndex[] { storage.SqliteVec!, storage.BruteForce })
        {
            Assert.Equal(0.0, (await index.SearchAsync(replacement, 1))[0].Distance, 6);
        }
    }

    private static async Task<List<(long Id, sbyte[] Vector)>> FillAsync(IndexStorage storage, Random random, int files, int chunksPerFile, int firstFile = 0)
    {
        var all = new List<(long, sbyte[])>();
        for (var f = firstFile; f < firstFile + files; f++)
        {
            var fileId = await storage.AddFileAsync($"src/File{f}.cs", Languages[f % Languages.Length], Partitions[f % Partitions.Length]);
            var vectors = Enumerable.Range(0, chunksPerFile).Select(_ => IndexTestSupport.RandomVector(random, Dims)).ToList();
            var ids = await storage.Chunks.ReplaceFileChunksAsync(
                fileId,
                vectors.Select((v, i) => IndexTestSupport.Chunk($"void Member{f}_{i}() {{ }}", i * 3 + 1, embedding: v)).ToList());
            all.AddRange(ids.Select((id, i) => (id, vectors[i])));
        }

        return all;
    }

    private static Task<Dictionary<long, (string Language, string Partition)>> MetadataByChunkAsync(IndexStorage storage) =>
        storage.Database.ReadAsync(async (connection, token) =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT c.id, f.language, f.partition FROM chunks c JOIN files f ON f.id = c.file_id";
            using var reader = await command.ExecuteReaderAsync(token);
            var map = new Dictionary<long, (string, string)>();
            while (await reader.ReadAsync(token))
            {
                map[reader.GetInt64(0)] = (reader.GetString(1), reader.GetString(2));
            }

            return map;
        });
}
