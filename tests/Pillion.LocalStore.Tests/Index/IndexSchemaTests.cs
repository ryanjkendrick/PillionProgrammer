using Pillion.LocalStore.Index;

namespace Pillion.LocalStore.Tests.Index;

public sealed class IndexSchemaTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void TheMigrations_AreVersions2And3_AndChainAfterVersion1()
    {
        var schema = IndexStorageSchema.After(IndexTestSupport.Version1);

        Assert.Equal("index", schema.Name);
        Assert.Equal(3, schema.CurrentVersion);
        Assert.Equal(2, schema.CurrentMinReaderVersion);
        Assert.Same(IndexStorageSchema.Migration, schema.Migrations[1]);
        Assert.Same(IndexSymbolsSchema.Migration, schema.Migrations[2]);
    }

    [Fact]
    public async Task AVersion1Database_IsBackedUpAndMigratedTo3_KeepingItsFiles()
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
        Assert.Equal(3, storage.Database.SchemaVersion);
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
    public async Task ANewDatabase_GoesStraightToVersion3_WithoutABackup()
    {
        await using var storage = await IndexTestSupport.OpenAsync(_temp);

        Assert.Equal(0, storage.Database.OpenedAtVersion);
        Assert.Equal(3, storage.Database.SchemaVersion);
        Assert.Null(storage.Database.BackupPath);
        Assert.Equal("index", await storage.Database.ScalarAsync("SELECT value FROM meta WHERE key = 'store'"));
    }

    [Fact]
    public async Task AVersion1Build_RefusesAVersion3Database()
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

    [Fact]
    public async Task AVersion2Database_IsMigratedTo3_RecreatingTheSymbolTablesAndForgettingTheSymbolsVersion()
    {
        await using (var v2 = await EncryptedDatabase.OpenAsync(IndexTestSupport.DatabaseOptions(_temp), IndexTestSupport.SchemaV2))
        {
            await v2.WriteAsync(async (connection, transaction, token) =>
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO files (id, path, language, partition) VALUES (7, 'src/A.cs', 'csharp', 'src');
                    INSERT INTO chunks (file_id, start_line, end_line, kind, text) VALUES (7, 1, 1, 'method', 'int A() => 1;');
                    INSERT INTO symbols (file_id, name, kind, start_line, end_line) VALUES (7, 'A', 'method', 1, 1);
                    INSERT INTO meta (key, value) VALUES ('symbols_version', '1');
                    """;
                await command.ExecuteNonQueryAsync(token);
            });
        }

        await using var storage = await IndexTestSupport.OpenAsync(_temp);

        Assert.Equal(2, storage.Database.OpenedAtVersion);
        Assert.Equal(3, storage.Database.SchemaVersion);
        Assert.Equal(2, storage.Database.MinReaderVersion);
        Assert.Equal(_temp.File("index.db.v2.bak"), storage.Database.BackupPath);

        // Files and chunks are kept; the derived symbols are dropped and their version forgotten, so the host rebuilds them.
        Assert.Equal(1L, await storage.CountAsync("SELECT count(*) FROM files WHERE id = 7"));
        Assert.Equal(1L, await storage.Chunks.CountChunksAsync());
        Assert.Equal(0L, await storage.Symbols.CountSymbolsAsync());
        Assert.Null(await storage.Symbols.GetSymbolsVersionAsync());

        // The new columns, and a nullable reference source.
        var columns = await storage.Database.ReadAsync(async (connection, token) =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT name, \"notnull\", dflt_value FROM pragma_table_info('symbol_refs') ORDER BY cid";
            using var reader = await command.ExecuteReaderAsync(token);
            var list = new List<(string Name, bool NotNull, string? Default)>();
            while (await reader.ReadAsync(token))
            {
                list.Add((reader.GetString(0), reader.GetInt64(1) != 0, reader.IsDBNull(2) ? null : reader.GetString(2)));
            }

            return list;
        });
        Assert.Equal(["from_symbol_id", "to_symbol_id", "file_id", "line", "kind", "ambiguous", "candidates"], columns.Select(c => c.Name));
        Assert.False(columns[0].NotNull);
        Assert.Equal(("ambiguous", true, "0"), columns[5]);
        Assert.Equal(("candidates", true, "1"), columns[6]);
        Assert.Equal(0L, await storage.CountAsync("SELECT count(*) FROM sqlite_schema WHERE name = 'symbols' AND sql LIKE '%AUTOINCREMENT%'"));
    }

    [Fact]
    public async Task IndexStorage_RefusesADatabaseStillAtVersion2()
    {
        await using var v2 = await EncryptedDatabase.OpenAsync(IndexTestSupport.DatabaseOptions(_temp), IndexTestSupport.SchemaV2);

        Assert.Throws<InvalidOperationException>(() => IndexStorage.Create(v2, ownsDatabase: false));
    }
}