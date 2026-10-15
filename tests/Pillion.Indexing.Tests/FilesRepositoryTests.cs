using Pillion.Indexing.Storage;
using Pillion.LocalStore;

namespace Pillion.Indexing.Tests;

internal static class IndexDatabases
{
    public static Task<EncryptedDatabase> OpenAsync(TempDirectory temp, string folder = "idx", bool readOnly = false) =>
        EncryptedDatabase.OpenAsync(new EncryptedDatabaseOptions { DatabasePath = temp.File($"{folder}/index.db"), ReadOnly = readOnly }, IndexSchema.Schema);

    public static FileRecord Record(string path, ulong hash = 1, long size = 10, long modified = 1000, string language = "csharp", string partition = "src") =>
        new(0, path, language, partition, hash, size, modified, 0);
}

public sealed class FilesRepositoryTests
{
    [Fact]
    public async Task TheSchema_IsVersion3_FilesFromVersion1TheIndexStorageFrom2AndSymbolsFrom3_AndRecordsTheStoreKind()
    {
        using var temp = new TempDirectory();
        await using var database = await IndexDatabases.OpenAsync(temp);

        // Version 1 is files (2.3); version 2 is the index storage (2.8, IndexStorageSchema), which raises the reader version;
        // version 3 recreates the symbol tables (2.5, IndexSymbolsSchema) and keeps the reader version.
        Assert.Equal(3, database.SchemaVersion);
        Assert.Equal(3, IndexSchema.Schema.CurrentVersion);
        Assert.Same(Pillion.LocalStore.Index.IndexStorageSchema.Migration, IndexSchema.Schema.Migrations[1]);
        Assert.Same(Pillion.LocalStore.Index.IndexSymbolsSchema.Migration, IndexSchema.Schema.Migrations[2]);
        Assert.Equal("index", await ScalarAsync(database, "SELECT value FROM meta WHERE key = 'store'"));
        Assert.Equal("3", await ScalarAsync(database, "SELECT value FROM meta WHERE key = 'schema_version'"));
        Assert.Equal("2", await ScalarAsync(database, "SELECT value FROM meta WHERE key = 'min_reader_version'"));
        var columns = await database.ReadAsync(async (connection, token) =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT name FROM pragma_table_info('files') ORDER BY cid";
            using var reader = await command.ExecuteReaderAsync(token);
            var names = new List<string>();
            while (await reader.ReadAsync(token))
            {
                names.Add(reader.GetString(0));
            }

            return names;
        });
        Assert.Equal(["id", "path", "language", "partition", "hash", "size", "mtime", "indexed_at"], columns);
    }

    [Fact]
    public async Task Upsert_InsertsNewFiles_AndAssignsIds()
    {
        using var temp = new TempDirectory();
        await using var database = await IndexDatabases.OpenAsync(temp);
        var now = new DateTimeOffset(2026, 10, 15, 9, 0, 0, TimeSpan.Zero);
        var repository = new FilesRepository(database, new TestTime(now));

        var stored = await repository.UpsertAsync([IndexDatabases.Record("src/a.cs", hash: 0xABC), IndexDatabases.Record("src/b.cs", hash: 0xDEF)]);

        Assert.Equal(2, stored.Count);
        Assert.All(stored, r => Assert.True(r.Id > 0));
        Assert.NotEqual(stored[0].Id, stored[1].Id);
        Assert.All(stored, r => Assert.Equal(now.ToUnixTimeMilliseconds(), r.IndexedAtUnixMilliseconds));
        Assert.Equal(2, await repository.CountAsync());
        var all = await repository.GetAllAsync();
        Assert.Equal(["src/a.cs", "src/b.cs"], all.Select(r => r.Path));
        Assert.Equal(0xABCUL, all[0].Hash);
    }

    [Fact]
    public async Task Upsert_OfAnExistingPath_KeepsTheId_AndReplacesTheRest()
    {
        using var temp = new TempDirectory();
        await using var database = await IndexDatabases.OpenAsync(temp);
        var time = new TestTime();
        var repository = new FilesRepository(database, time);
        var first = (await repository.UpsertAsync([IndexDatabases.Record("src/a.cs", hash: 1, size: 5, modified: 100)]))[0];

        time.Now = time.Now.AddMinutes(5);
        var second = (await repository.UpsertAsync([IndexDatabases.Record("src/a.cs", hash: 2, size: 6, modified: 200, language: "other", partition: "lib")]))[0];

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(2UL, second.Hash);
        Assert.Equal(6, second.Size);
        Assert.Equal(200, second.ModifiedUnixMilliseconds);
        Assert.Equal("other", second.Language);
        Assert.Equal("lib", second.Partition);
        Assert.True(second.IndexedAtUnixMilliseconds > first.IndexedAtUnixMilliseconds);
        Assert.Equal(1, await repository.CountAsync());
    }

    [Fact]
    public async Task Paths_AreUniqueWithoutRegardToCase_AndTheLatestSpellingIsKept()
    {
        using var temp = new TempDirectory();
        await using var database = await IndexDatabases.OpenAsync(temp);
        var repository = new FilesRepository(database);
        var first = (await repository.UpsertAsync([IndexDatabases.Record("src/Thing.cs")]))[0];

        var second = (await repository.UpsertAsync([IndexDatabases.Record("SRC/thing.CS")]))[0];

        Assert.Equal(first.Id, second.Id);
        Assert.Equal("SRC/thing.CS", second.Path);
        Assert.Equal(1, await repository.CountAsync());
        Assert.NotNull(await repository.GetAsync("src/thing.cs"));
    }

    [Theory]
    [InlineData(0UL)]
    [InlineData(1UL)]
    [InlineData(0x7FFFFFFFFFFFFFFFUL)]
    [InlineData(0x8000000000000000UL)]
    [InlineData(0xFFFFFFFFFFFFFFFFUL)]
    [InlineData(0xEF46DB3751D8E999UL)]
    public async Task EveryHashValue_RoundTripsThroughTheSignedColumn(ulong hash)
    {
        using var temp = new TempDirectory();
        await using var database = await IndexDatabases.OpenAsync(temp);
        var repository = new FilesRepository(database);

        await repository.UpsertAsync([IndexDatabases.Record("a.cs", hash: hash)]);

        Assert.Equal(hash, (await repository.GetAsync("a.cs"))!.Hash);
    }

    [Fact]
    public async Task Get_ReturnsNullForAnUnknownPath_AndDeleteRemovesRows()
    {
        using var temp = new TempDirectory();
        await using var database = await IndexDatabases.OpenAsync(temp);
        var repository = new FilesRepository(database);
        await repository.UpsertAsync([IndexDatabases.Record("a.cs"), IndexDatabases.Record("b.cs"), IndexDatabases.Record("c.cs")]);

        Assert.Null(await repository.GetAsync("nope.cs"));
        Assert.Equal(2, await repository.DeleteAsync(["a.cs", "C.CS", "never-there.cs"]));
        Assert.Equal(["b.cs"], (await repository.GetAllAsync()).Select(r => r.Path));
    }

    [Fact]
    public async Task AFailedBatch_IsRolledBackWhole()
    {
        using var temp = new TempDirectory();
        await using var database = await IndexDatabases.OpenAsync(temp);
        var repository = new FilesRepository(database);
        await repository.UpsertAsync([IndexDatabases.Record("keep.cs")]);

        // A null language violates NOT NULL on the second record; nothing from the batch may remain.
        var bad = IndexDatabases.Record("bad.cs") with { Language = null! };
        await Assert.ThrowsAnyAsync<Exception>(() => repository.UpsertAsync([IndexDatabases.Record("new.cs"), bad]));

        Assert.Equal(["keep.cs"], (await repository.GetAllAsync()).Select(r => r.Path));
    }

    [Fact]
    public async Task ADatabaseOpenedReadOnly_ReadsButRefusesWrites()
    {
        using var temp = new TempDirectory();
        await using var writer = await IndexDatabases.OpenAsync(temp);
        await new FilesRepository(writer).UpsertAsync([IndexDatabases.Record("a.cs")]);

        await using var reader = await IndexDatabases.OpenAsync(temp, readOnly: true);
        var readOnly = new FilesRepository(reader);

        Assert.Equal(["a.cs"], (await readOnly.GetAllAsync()).Select(r => r.Path));
        await Assert.ThrowsAsync<InvalidOperationException>(() => readOnly.UpsertAsync([IndexDatabases.Record("b.cs")]));
        await Assert.ThrowsAsync<InvalidOperationException>(() => readOnly.DeleteAsync(["a.cs"]));
    }

    [Fact]
    public async Task TheIndexFile_IsEncrypted()
    {
        using var temp = new TempDirectory();
        await using (var database = await IndexDatabases.OpenAsync(temp))
        {
            await new FilesRepository(database).UpsertAsync([IndexDatabases.Record("src/VerySecretCustomerName.cs")]);
            await database.VacuumAsync();
        }

        var bytes = await File.ReadAllBytesAsync(temp.File("idx/index.db"));
        Assert.DoesNotContain("VerySecretCustomerName", System.Text.Encoding.Latin1.GetString(bytes), StringComparison.Ordinal);
        Assert.False(bytes.AsSpan().StartsWith("SQLite format 3\0"u8));
        Assert.True(File.Exists(temp.File("idx/key.bin")));
    }

    private static async Task<string?> ScalarAsync(EncryptedDatabase database, string sql) =>
        (string?)await database.ReadAsync(async (connection, token) =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            return await command.ExecuteScalarAsync(token);
        });
}
