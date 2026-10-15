using Pillion.Indexing.Ownership;
using Pillion.Indexing.Storage;
using Pillion.LocalStore.Index;

namespace Pillion.Indexing.Tests;

/// <summary>
/// Work items 2.3 and 2.8 together: an index opened through <see cref="IndexStoreFactory"/> carries schema version 1
/// (<c>files</c>) and version 2 (the index storage), files go in through <see cref="FilesRepository"/> and chunks through
/// <see cref="IndexStorage"/>, both kinds of search work, and a second store opens the same index read-only.
/// </summary>
public sealed class IndexFoundationIntegrationTests : IDisposable
{
    private const int Dims = 64;
    private static readonly IndexStoreOptions QuickReaders = new() { ReaderOpenAttempts = 3, ReaderOpenDelay = TimeSpan.FromMilliseconds(20) };

    private readonly TempDirectory _temp = new();
    private readonly FakeFeatureFlags _features = new();
    private readonly FakeProcessTable _processes = new();

    public IndexFoundationIntegrationTests() => Directory.CreateDirectory(Repo);

    private string Repo => _temp.File("repo");

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task AnIndexFromTheFactory_StoresFilesAndChunks_SearchesBothWays_AndReopensReadOnly()
    {
        var random = new Random(2026);
        var orderVector = RandomVector(random);
        var shipVector = RandomVector(random);
        var testVector = RandomVector(random);

        await using var writer = await Host(100).OpenAsync(Request(100));
        Assert.Equal(IndexRole.Writer, writer.Role);
        Assert.Equal(3, writer.Database.SchemaVersion);
        await using var storage = IndexStorage.Create(writer.Database, ownsDatabase: false);

        // 2.3's files table is what 2.8's chunks reference: integer ids, language and partition.
        var files = await writer.Files.UpsertAsync([
            IndexDatabases.Record("src/Orders/OrderService.cs", language: "csharp", partition: "Orders"),
            IndexDatabases.Record("tools/ship.py", language: "python", partition: "tools"),
            IndexDatabases.Record("tests/OrderTests.cs", language: "csharp", partition: "tests"),
        ]);
        var byPath = files.ToDictionary(f => f.Path, f => f.Id);
        Assert.All(files, f => Assert.True(f.Id > 0));

        var orderChunk = (await storage.Chunks.ReplaceFileChunksAsync(byPath["src/Orders/OrderService.cs"], [
            new NewChunk(10, 14, "method", "src/Orders/OrderService.cs > Shop > OrderService > GetOrderTotal", "public decimal GetOrderTotal(Order order) => order.Lines.Sum(l => l.Price);", 20) { Embedding = orderVector },
        ]))[0];
        var shipChunk = (await storage.Chunks.ReplaceFileChunksAsync(byPath["tools/ship.py"], [
            new NewChunk(1, 2, "function", "tools/ship.py > ship_parcel", "def ship_parcel(parcel):\n    carrier.send(parcel)", 12) { Embedding = shipVector },
        ]))[0];
        var testChunk = (await storage.Chunks.ReplaceFileChunksAsync(byPath["tests/OrderTests.cs"], [
            new NewChunk(5, 9, "method", "tests/OrderTests.cs > OrderTests > Total_IsTheSumOfLines", "public void Total_IsTheSumOfLines() => Assert.Equal(3, service.GetOrderTotal(order));", 22) { Embedding = testVector },
        ]))[0];

        // Lexical: identifier splitting, and the partition and language filters come from 2.3's files rows.
        Assert.Equal(new[] { orderChunk, testChunk }.Order(), (await storage.Chunks.SearchLexicalAsync("order total", 10)).Select(h => h.ChunkId).Order());
        Assert.Equal([orderChunk], (await storage.Chunks.SearchLexicalAsync("getOrderTotal", 10, new ChunkFilter { Partitions = ["Orders"] })).Select(h => h.ChunkId));
        Assert.Equal([shipChunk], (await storage.Chunks.SearchLexicalAsync("ship parcel", 10, new ChunkFilter { Languages = ["python"] })).Select(h => h.ChunkId));

        // Vector: the default in-memory backend and the table scan.
        foreach (var index in new IVectorIndex[] { storage.Vectors, storage.TableScan })
        {
            Assert.Equal(shipChunk, (await index.SearchAsync(shipVector, 1))[0].ChunkId);
            Assert.Equal([testChunk], (await index.SearchAsync(orderVector, 3, new ChunkFilter { Partitions = ["tests"] })).Select(h => h.ChunkId));
        }

        // A second store on the same index (another host) is a reader with the database read-only, and sees everything.
        await using var reader = await Host(200).OpenAsync(Request(200));
        Assert.Equal(IndexRole.Reader, reader.Role);
        Assert.True(reader.Database.IsReadOnly);
        await using var readerStorage = IndexStorage.Create(reader.Database, ownsDatabase: false);
        Assert.Equal(3L, await readerStorage.Chunks.CountChunksAsync());
        Assert.Equal([orderChunk], (await readerStorage.Chunks.SearchLexicalAsync("GetOrderTotal", 10, new ChunkFilter { Languages = ["csharp"], Partitions = ["Orders"] })).Select(h => h.ChunkId));
        Assert.Equal(orderChunk, (await readerStorage.Vectors.SearchAsync(orderVector, 1))[0].ChunkId);
        Assert.Equal("tools/ship.py", Assert.Single(await readerStorage.Chunks.ListFilesAsync(), f => f.FileId == byPath["tools/ship.py"]).Path);
        await Assert.ThrowsAsync<InvalidOperationException>(() => readerStorage.Chunks.ReplaceFileChunksAsync(byPath["tools/ship.py"], []));

        // A file 2.3 deletes takes its chunks, FTS5 rows and vectors with it, and the reader's in-memory copy follows.
        await writer.Files.DeleteAsync(["tools/ship.py"]);
        Assert.Equal(2L, await storage.Chunks.CountChunksAsync());
        Assert.Empty(await storage.Chunks.SearchLexicalAsync("ship parcel carrier", 10));
        Assert.DoesNotContain(shipChunk, (await readerStorage.Vectors.SearchAsync(shipVector, 10)).Select(h => h.ChunkId));
        Assert.Empty(await readerStorage.Chunks.SearchLexicalAsync("carrier", 10));
    }

    private IndexStoreFactory Host(int processId) =>
        new(_temp.NewPaths(), _features, probe: _processes.HostProbe(processId), options: QuickReaders);

    private IndexRequest Request(int processId) => new(Repo, "bge-small-en-v1.5-int8", "1", $"pillion-host-{processId}");

    private static sbyte[] RandomVector(Random random)
    {
        var vector = new sbyte[Dims];
        for (var i = 0; i < Dims; i++)
        {
            vector[i] = (sbyte)random.Next(-127, 128);
        }

        return vector;
    }
}
