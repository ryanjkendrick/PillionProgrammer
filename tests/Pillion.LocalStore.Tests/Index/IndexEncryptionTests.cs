using System.Text;
using Microsoft.Data.Sqlite;
using Pillion.LocalStore.Index;

namespace Pillion.LocalStore.Tests.Index;

/// <summary>
/// The index database stays encrypted: no SQLite header, a keyless open fails, and chunk text, split identifiers and the
/// schema never reach the file or its WAL in plain text.
/// </summary>
public sealed class IndexEncryptionTests : IDisposable
{
    private const string Canary = "PillionCanary7d1fPlaintextMustNotHitTheDisk";
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task TheIndexIsEncrypted_AndNoChunkTextReachesTheDisk()
    {
        var path = _temp.File("index.db");
        var vector = IndexTestSupport.RandomVector(new Random(1), 384);
        await using (var storage = await IndexTestSupport.OpenAsync(_temp))
        {

            var file = await storage.AddFileAsync("src/Secret.cs");
            await storage.Chunks.ReplaceFileChunksAsync(file, [
                new NewChunk(1, 3, "method", "src/Secret.cs > " + Canary, "string " + Canary + "() => \"" + Canary + "\";", 20) { Embedding = vector },
            ]);
            Assert.Equal(1L, await storage.CountAsync("SELECT count(*) FROM chunk_embeddings"));

            Assert.True(new FileInfo(path + "-wal").Length > 0);
            AssertNoPlainText(path + "-wal");
            AssertNoPlainText(path);
        }

        AssertNoPlainText(path);
        var header = new byte[16];
        using (var stream = File.OpenRead(path))
        {
            stream.ReadExactly(header);
        }

        Assert.NotEqual(Encoding.ASCII.GetBytes("SQLite format 3\0"), header);
        using var plain = IndexTestSupport.OpenWithoutKey(path);
        using var command = plain.CreateCommand();
        command.CommandText = "SELECT count(*) FROM sqlite_schema";
        Assert.Equal(26, Assert.Throws<SqliteException>(() => command.ExecuteScalar()).SqliteErrorCode);

        // And it reopens with its key and its data.
        await using var reopened = await IndexTestSupport.OpenAsync(_temp);
        Assert.Equal("chacha20", await reopened.Database.ScalarAsync("PRAGMA cipher"));
        Assert.Single(await reopened.Chunks.SearchLexicalAsync(Canary, 5));
        Assert.Single(await reopened.Vectors.SearchAsync(vector, 5));
    }

    private static void AssertNoPlainText(string path)
    {
        byte[] bytes;
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var memory = new MemoryStream())
        {
            stream.CopyTo(memory);
            bytes = memory.ToArray();
        }

        Assert.True(bytes.Length > 0, $"{path} is empty");
        Assert.Equal(-1, bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(Canary)));
        Assert.Equal(-1, bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(Canary.ToLowerInvariant())));
        Assert.Equal(-1, bytes.AsSpan().IndexOf("pillion canary 7"u8));
        Assert.Equal(-1, bytes.AsSpan().IndexOf("CREATE VIRTUAL TABLE"u8));
        Assert.Equal(-1, bytes.AsSpan().IndexOf("chunk_embeddings"u8));
    }
}
