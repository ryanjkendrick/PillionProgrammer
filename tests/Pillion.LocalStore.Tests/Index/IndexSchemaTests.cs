using Pillion.LocalStore.Index;

namespace Pillion.LocalStore.Tests.Index;

public sealed class IndexSchemaTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void TheMigration_IsVersion2_AndChainsAfterVersion1()
    {
        var schema = IndexStorageSchema.After(IndexTestSupport.Version1);

        Assert.Equal("index", schema.Name);
        Assert.Equal(2, schema.CurrentVersion);
        Assert.Equal(2, schema.CurrentMinReaderVersion);
        Assert.Same(IndexStorageSchema.Migration, schema.Migrations[1]);
    }

    [Fact]
    public async Task AVersion1Database_IsBackedUpAndMigratedTo2_KeepingItsFiles()
    {
        await using (var v1 = await EncryptedDatabase.OpenAsync(IndexTestSupport.DatabaseOptions(_temp), IndexTestSupport.SchemaV1))
        {
            await v1.WriteAsync(async (connection, transaction, token) =>
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "INSERT INTO files (id, path, language, partition) VALUES (7, 'src/A.cs', 'csharp', 'src')";
                await command.ExecuteNonQueryAsync(token);
            });
        }

        await using var storage = await IndexTestSupport.OpenAsync(_temp);

        Assert.Equal(1, storage.Database.OpenedAtVersion);
        Assert.Equal(2, storage.Database.SchemaVersion);
        Assert.Equal(2, storage.Database.MinReaderVersion);
        Assert.Equal(_temp.File("index.db.v1.bak"), storage.Database.BackupPath);
        Assert.Equal(1L, await storage.CountAsync("SELECT count(*) FROM files WHERE id = 7"));
        foreach (var table in new[] { "chunks", "chunks_fts", "chunk_embeddings", "vec_float32", "vec_generation", "symbols", "symbol_refs" })
        {
            Assert.Equal(1L, await storage.CountAsync($"SELECT count(*) FROM sqlite_schema WHERE name = '{table}'"));
        }


        var ids = await storage.Chunks.ReplaceFileChunksAsync(7, [IndexTestSupport.Chunk("int Total() => 1;")]);
        Assert.Single(ids);
    }

    [Fact]
    public async Task ANewDatabase_GoesStraightToVersion2_WithoutABackup()
    {
        await using var storage = await IndexTestSupport.OpenAsync(_temp);

        Assert.Equal(0, storage.Database.OpenedAtVersion);
        Assert.Equal(2, storage.Database.SchemaVersion);
        Assert.Null(storage.Database.BackupPath);
        Assert.Equal("index", await storage.Database.ScalarAsync("SELECT value FROM meta WHERE key = 'store'"));
    }

    [Fact]
    public async Task AVersion1Build_RefusesAVersion2Database()
    {
        await using (await IndexTestSupport.OpenAsync(_temp))
        {
        }

        var error = await Assert.ThrowsAsync<LocalStoreException>(() => EncryptedDatabase.OpenAsync(IndexTestSupport.DatabaseOptions(_temp), IndexTestSupport.SchemaV1));
        Assert.Equal(LocalStoreErrorKind.NewerSchema, error.Kind);
    }

    [Fact]
    public async Task IndexStorage_RefusesADatabaseStillAtVersion1()
    {
        await using var v1 = await EncryptedDatabase.OpenAsync(IndexTestSupport.DatabaseOptions(_temp), IndexTestSupport.SchemaV1);

        Assert.Throws<InvalidOperationException>(() => IndexStorage.Create(v1, ownsDatabase: false));
    }

    [Fact]
    public async Task DeletingAFileRow_CascadesToChunks_AndTheTriggerRemovesTheirFtsRows()
    {
        await using var storage = await IndexTestSupport.OpenAsync(_temp);
        var file = await storage.AddFileAsync("src/Orders.cs");
        await storage.Chunks.ReplaceFileChunksAsync(file, [IndexTestSupport.Chunk("decimal GetOrderTotal() => 0;"), IndexTestSupport.Chunk("void Ship() {}")]);
        Assert.Equal(2L, await storage.CountAsync("SELECT count(*) FROM chunks_fts WHERE chunks_fts MATCH 'order OR ship'"));

        // What 2.3 does when a file disappears from disk.
        await storage.ExecuteAsync($"DELETE FROM files WHERE id = {file}");

        Assert.Equal(0L, await storage.Chunks.CountChunksAsync());
        Assert.Equal(0L, await storage.CountAsync("SELECT count(*) FROM chunks_fts WHERE chunks_fts MATCH 'order OR ship'"));
    }
}
