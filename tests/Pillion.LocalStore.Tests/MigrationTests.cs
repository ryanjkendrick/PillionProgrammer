using System.Text;
using Pillion.Contracts;

namespace Pillion.LocalStore.Tests;

public sealed class MigrationTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task ANewDatabase_RecordsItsVersions_WithoutABackup()
    {
        await using var database = await TestDatabases.OpenAsync(_temp, TestDatabases.NotesV2);

        Assert.Equal(2, database.SchemaVersion);
        Assert.Equal(1, database.MinReaderVersion);
        Assert.Null(database.BackupPath);
        Assert.Equal("2", await database.ScalarAsync("SELECT value FROM meta WHERE key = 'schema_version'"));
        Assert.Equal("1", await database.ScalarAsync("SELECT value FROM meta WHERE key = 'min_reader_version'"));
        Assert.Equal("notes", await database.ScalarAsync("SELECT value FROM meta WHERE key = 'store'"));
        Assert.Empty(Directory.GetFiles(_temp.Path, "*.bak*"));
    }

    [Fact]
    public async Task AnOlderDatabase_IsBackedUpEncrypted_ThenMigrated()
    {
        await using (var v1 = await TestDatabases.OpenAsync(_temp, TestDatabases.NotesV1))
        {
            await v1.AddNoteAsync("from version 1");
        }

        await using (var v2 = await TestDatabases.OpenAsync(_temp, TestDatabases.NotesV2))
        {
            Assert.Equal(1, v2.OpenedAtVersion);
            Assert.Equal(2, v2.SchemaVersion);
            Assert.Equal(_temp.File("notes.db.v1.bak"), v2.BackupPath);
            Assert.Equal(["from version 1"], await v2.GetNotesAsync());
            Assert.Equal(0L, await v2.ScalarAsync("SELECT count(*) FROM tags")); // The migration ran.
        }

        // The backup is a byte copy of the encrypted file: no SQLite header, and it opens with the same key at version 1.
        var header = new byte[16];
        using (var stream = File.OpenRead(_temp.File("notes.db.v1.bak")))
        {
            stream.ReadExactly(header);
        }

        Assert.NotEqual(Encoding.ASCII.GetBytes("SQLite format 3\0"), header);
        await using var backup = await EncryptedDatabase.OpenAsync(
            new EncryptedDatabaseOptions { DatabasePath = _temp.File("notes.db.v1.bak"), KeyFilePath = _temp.File("key.bin") },
            TestDatabases.NotesV1);
        Assert.Equal(1, backup.SchemaVersion);
        Assert.Equal(["from version 1"], await backup.GetNotesAsync());
    }

    [Fact]
    public async Task AFailingMigration_IsRolledBack_AndTheDatabaseStaysAtItsVersion()
    {
        await using (var v1 = await TestDatabases.OpenAsync(_temp, TestDatabases.NotesV1))
        {
            await v1.AddNoteAsync("kept");
        }

        var broken = new StoreSchema(
            "notes",
            [
                TestDatabases.NotesV1.Migrations[0],
                StoreMigration.Sql(2, 1, "half done", "CREATE TABLE half (x INTEGER); INSERT INTO no_such_table VALUES (1)"),
            ]);

        var error = await Assert.ThrowsAsync<LocalStoreException>(() => TestDatabases.OpenAsync(_temp, broken));
        Assert.Equal(LocalStoreErrorKind.MigrationFailed, error.Kind);
        Assert.Contains("left unchanged at version 1", error.Message, StringComparison.Ordinal);

        await using var reopened = await TestDatabases.OpenAsync(_temp, TestDatabases.NotesV1);
        Assert.Equal(1, reopened.SchemaVersion);
        Assert.Equal(["kept"], await reopened.GetNotesAsync());
        Assert.Equal(0L, await reopened.ScalarAsync("SELECT count(*) FROM sqlite_schema WHERE name = 'half'"));
    }

    [Fact]
    public async Task ADatabaseFromANewerMajorSchema_IsRefused_AndLeftUntouched()
    {
        await using (var database = await TestDatabases.OpenAsync(_temp, TestDatabases.NotesV1))
        {
            await database.AddNoteAsync("written by the future");
            await database.SetMetaAsync("schema_version", "5");
            await database.SetMetaAsync("min_reader_version", "4");
        }

        var error = await Assert.ThrowsAsync<LocalStoreException>(() => TestDatabases.OpenAsync(_temp, TestDatabases.NotesV2));

        Assert.Equal(LocalStoreErrorKind.NewerSchema, error.Kind);
        var rpc = Assert.IsAssignableFrom<PillionRpcException>(error);
        Assert.Equal(PillionErrorCode.PolicyDenied, rpc.Error.Code);
        Assert.Contains("newer Pillion Programmer", error.Message, StringComparison.Ordinal);
        Assert.Contains("schema version 5", error.Message, StringComparison.Ordinal);
        Assert.Contains("reader of version 4", error.Message, StringComparison.Ordinal);
        Assert.Contains("reads up to 2", error.Message, StringComparison.Ordinal);
        Assert.NotNull(rpc.Error.RetryHint);
        Assert.Empty(Directory.GetFiles(_temp.Path, "*.bak*"));
    }

    [Fact]
    public async Task ANewerSchemaThatKeepsOldReaders_OpensWithoutMigrating()
    {
        await using (var v2 = await TestDatabases.OpenAsync(_temp, TestDatabases.NotesV2))
        {
            await v2.AddNoteAsync("v2 note");
        }

        await using var v1 = await TestDatabases.OpenAsync(_temp, TestDatabases.NotesV1);

        Assert.Equal(2, v1.SchemaVersion);
        Assert.Equal(1, v1.MinReaderVersion);
        Assert.Null(v1.BackupPath);
        Assert.Equal(["v2 note"], await v1.GetNotesAsync());
        Assert.Equal("2", await v1.ScalarAsync("SELECT value FROM meta WHERE key = 'schema_version'"));
    }

    [Theory]
    [InlineData(new[] { 2 }, new[] { 1 })]
    [InlineData(new[] { 1, 3 }, new[] { 1, 1 })]
    [InlineData(new[] { 1, 2 }, new[] { 1, 3 })]
    [InlineData(new[] { 1, 2, 3 }, new[] { 1, 2, 1 })]
    public void ASchemaWithBadVersions_IsRejected(int[] versions, int[] readers)
    {
        var migrations = versions.Select((v, i) => StoreMigration.Sql(v, readers[i], "m", "SELECT 1")).ToList();

        Assert.Throws<ArgumentException>(() => new StoreSchema("bad", migrations));
    }
}
