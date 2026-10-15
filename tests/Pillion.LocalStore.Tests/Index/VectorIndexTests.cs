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
    public async Task BothBackends_ReturnTheTopKOfAScalarReference()
    {
        await using var storage = await IndexTestSupport.OpenAsync(_temp);
        var random = new Random(20261015);
        var vectors = await FillAsync(storage, random, files: 60, chunksPerFile: 50);

        for (var q = 0; q < 25; q++)
        {
            var query = IndexTestSupport.RandomVector(random, Dims);
            var expected = Reference(vectors, query, 10);
            var inMemory = await storage.Vectors.SearchAsync(query, 10);
            var scan = await storage.TableScan.SearchAsync(query, 10);

            Assert.Equal(expected.Select(h => h.ChunkId), inMemory.Select(h => h.ChunkId));
            Assert.Equal(expected.Select(h => h.ChunkId), scan.Select(h => h.ChunkId));
            for (var i = 0; i < expected.Count; i++)
            {
                Assert.Equal(expected[i].Distance, inMemory[i].Distance, 6);
                Assert.Equal(expected[i].Distance, scan[i].Distance, 6);
            }
        }

        Assert.True(storage.VectorCacheBytes > 0);
    }

    [Fact]
    public async Task TheNearestVector_IsTheVectorItself_AtDistanceZero()
    {
        await using var storage = await IndexTestSupport.OpenAsync(_temp);
        var vectors = await FillAsync(storage, new Random(1), files: 5, chunksPerFile: 20);
        var (id, vector, _, _) = vectors[37];

        foreach (var index in new IVectorIndex[] { storage.Vectors, storage.TableScan })
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
    public async Task Filters_ByPartitionAndLanguage_KeepTheBestMatchingChunks(bool inMemory)
    {
        await using var storage = await IndexTestSupport.OpenAsync(_temp);
        var random = new Random(7);
        var vectors = await FillAsync(storage, random, files: 12, chunksPerFile: 30);
        var index = inMemory ? storage.Vectors : storage.TableScan;

        var query = IndexTestSupport.RandomVector(random, Dims);
        var byPartition = await index.SearchAsync(query, 20, new ChunkFilter { Partitions = ["tests"] });
        var byBoth = await index.SearchAsync(query, 20, new ChunkFilter { Partitions = SourcePartitions, Languages = ["typescript"] });

        Assert.Equal(
            Reference(vectors.Where(v => v.Partition == "tests").ToList(), query, 20).Select(h => h.ChunkId),
            byPartition.Select(h => h.ChunkId));
        Assert.Equal(
            Reference(vectors.Where(v => SourcePartitions.Contains(v.Partition) && v.Language == "typescript").ToList(), query, 20).Select(h => h.ChunkId),
            byBoth.Select(h => h.ChunkId));
        Assert.Empty(await index.SearchAsync(query, 20, new ChunkFilter { Partitions = ["nowhere"] }));
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
        Assert.Equal(3, (await storage.Vectors.SearchAsync(IndexTestSupport.RandomVector(random, Dims), 10)).Count);

        Assert.Equal(2, await storage.Chunks.DeleteFileChunksAsync(drop));

        Assert.Equal(1L, await storage.CountAsync("SELECT count(*) FROM chunks"));
        Assert.Equal(1L, await storage.CountAsync("SELECT count(*) FROM chunk_embeddings"));
        Assert.Empty(await storage.Chunks.SearchLexicalAsync("DropMe DropMeToo", 10));
        Assert.Single(await storage.Chunks.SearchLexicalAsync("KeepMe", 10));
        foreach (var index in new IVectorIndex[] { storage.Vectors, storage.TableScan })
        {
            var hits = await index.SearchAsync(IndexTestSupport.RandomVector(random, Dims), 10);
            Assert.Single(hits);
            Assert.DoesNotContain(hits, h => dropIds.Contains(h.ChunkId));
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DeleteByFile_RemovesOnlyTheVectors(bool inMemory)
    {
        await using var storage = await IndexTestSupport.OpenAsync(_temp);
        var random = new Random(5);
        var file = await storage.AddFileAsync("src/A.cs");
        var other = await storage.AddFileAsync("src/B.cs");
        await storage.Chunks.ReplaceFileChunksAsync(file, [new NewChunk(1, 1, "method", string.Empty, "void A() {}", 3) { Embedding = IndexTestSupport.RandomVector(random, Dims), EmbeddingFloat32 = new float[Dims] }]);
        await storage.Chunks.ReplaceFileChunksAsync(other, [IndexTestSupport.Chunk("void B() {}", embedding: IndexTestSupport.RandomVector(random, Dims))]);
        var index = inMemory ? storage.Vectors : storage.TableScan;

        Assert.Equal(1, await index.DeleteByFileAsync(file));

        Assert.Equal(2L, await storage.Chunks.CountChunksAsync());
        Assert.Equal(1L, await storage.CountAsync("SELECT count(*) FROM chunk_embeddings"));
        Assert.Equal(0L, await storage.CountAsync("SELECT count(*) FROM vec_float32"));
        Assert.Single(await index.SearchAsync(IndexTestSupport.RandomVector(random, Dims), 10));
    }

    [Fact]
    public async Task OverTheCacheLimit_TheDefaultBackendScansTheTable_AndRanksAlike()
    {
        await using var storage = await IndexTestSupport.OpenAsync(_temp);
        var random = new Random(23);
        await FillAsync(storage, random, files: 20, chunksPerFile: 40);
        await using var overLimit = IndexStorage.Create(storage.Database, ownsDatabase: false, new IndexStorageOptions { VectorCacheMaxBytes = 1024 });
        await using var noCache = IndexStorage.Create(storage.Database, ownsDatabase: false, new IndexStorageOptions { VectorCacheMaxBytes = 0 });
        var filter = new ChunkFilter { Partitions = ["src/Core"], Languages = ["csharp", "typescript"] };

        for (var q = 0; q < 10; q++)
        {
            var query = IndexTestSupport.RandomVector(random, Dims);
            foreach (var f in new ChunkFilter?[] { null, filter })
            {
                var expected = (await storage.Vectors.SearchAsync(query, 15, f)).Select(h => h.ChunkId).ToList();
                Assert.Equal(15, expected.Count);
                Assert.Equal(expected, (await overLimit.Vectors.SearchAsync(query, 15, f)).Select(h => h.ChunkId));
                Assert.Equal(expected, (await noCache.Vectors.SearchAsync(query, 15, f)).Select(h => h.ChunkId));
            }
        }

        Assert.InRange(await storage.WarmVectorCacheAsync(), 800L * Dims, 800L * (Dims + 32));
        Assert.Equal(0L, await overLimit.WarmVectorCacheAsync());
        Assert.Equal(0L, noCache.VectorCacheBytes);
    }

    [Fact]
    public async Task TheInMemoryCopy_FollowsEveryChange_IncludingCascades()
    {
        await using var storage = await IndexTestSupport.OpenAsync(_temp);
        var random = new Random(29);
        var vectors = await FillAsync(storage, random, files: 3, chunksPerFile: 10);
        var (target, targetVector, _, _) = vectors[4];
        Assert.Equal(target, (await storage.Vectors.SearchAsync(targetVector, 1))[0].ChunkId);
        Assert.True(storage.VectorCacheBytes > 0);

        // A new vector, as close as can be, through the repository.
        var file = await storage.AddFileAsync("src/New.cs");
        var newId = (await storage.Chunks.ReplaceFileChunksAsync(file, [IndexTestSupport.Chunk("void New() {}", embedding: targetVector)]))[0];
        var hits = await storage.Vectors.SearchAsync(targetVector, 2);
        Assert.Equal(new[] { target, newId }.Order(), hits.Select(h => h.ChunkId).Order());

        // Removed by a cascade from files, outside the repository (what 2.3 does).
        await storage.ExecuteAsync($"DELETE FROM files WHERE id = {file}");
        Assert.DoesNotContain(newId, (await storage.Vectors.SearchAsync(targetVector, 5)).Select(h => h.ChunkId));

        // Re-embedded through the vector index.
        await storage.Vectors.UpsertAsync(target, IndexTestSupport.RandomVector(random, Dims), new VectorMetadata("csharp", "src/Api"));
        Assert.NotEqual(0.0, (await storage.Vectors.SearchAsync(targetVector, 1))[0].Distance, 3);
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
        var hits = await storage.Vectors.SearchAsync(vector, 5, new ChunkFilter { Languages = ["typescript"], Partitions = ["web"] });
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

        Assert.Equal(1L, await storage.CountAsync("SELECT count(*) FROM chunk_embeddings"));
        foreach (var index in new IVectorIndex[] { storage.Vectors, storage.TableScan })
        {
            Assert.Equal(0.0, (await index.SearchAsync(replacement, 1))[0].Distance, 6);
        }
    }

    [Fact]
    public async Task TheBackendCanBeChosen()
    {
        await using (var defaults = await IndexTestSupport.OpenAsync(_temp))
        {
            Assert.Equal(AutoVectorIndex.Name, defaults.Vectors.Backend);
        }

        await using var scan = await IndexTestSupport.OpenAsync(_temp, new IndexStorageOptions { VectorBackend = VectorBackendPreference.TableScan });
        Assert.Same(scan.TableScan, scan.Vectors);
        Assert.Equal(TableScanVectorIndex.Name, scan.Vectors.Backend);
    }

    /// <summary>A scalar reference: cosine distance in doubles, rounded to float like the backends, ordered by distance then id.</summary>
    private static List<VectorHit> Reference(IReadOnlyList<(long Id, sbyte[] Vector, string Language, string Partition)> vectors, sbyte[] query, int k)
    {
        static double Norm(sbyte[] v) => Math.Sqrt(v.Sum(x => (double)x * x));
        var queryNorm = Norm(query);
        return vectors
            .Select(v => new VectorHit(v.Id, (float)(1.0 - (v.Vector.Zip(query, (a, b) => (double)a * b).Sum() / (queryNorm * Norm(v.Vector))))))
            .OrderBy(h => h.Distance)
            .ThenBy(h => h.ChunkId)
            .Take(k)
            .ToList();
    }

    private static async Task<List<(long Id, sbyte[] Vector, string Language, string Partition)>> FillAsync(IndexStorage storage, Random random, int files, int chunksPerFile)
    {
        var all = new List<(long, sbyte[], string, string)>();
        for (var f = 0; f < files; f++)
        {
            var language = Languages[f % Languages.Length];
            var partition = Partitions[f % Partitions.Length];
            var fileId = await storage.AddFileAsync($"src/File{f}.cs", language, partition);
            var vectors = Enumerable.Range(0, chunksPerFile).Select(_ => IndexTestSupport.RandomVector(random, Dims)).ToList();
            var ids = await storage.Chunks.ReplaceFileChunksAsync(
                fileId,
                vectors.Select((v, i) => IndexTestSupport.Chunk($"void Member{f}_{i}() {{ }}", i * 3 + 1, embedding: v)).ToList());
            all.AddRange(ids.Select((id, i) => (id, vectors[i], language, partition)));
        }

        return all;
    }
}
