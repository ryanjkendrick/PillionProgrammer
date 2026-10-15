namespace Pillion.LocalStore.Tests;

/// <summary>A database opened read-only by a host that does not own it (work item 2.3: the index's non-writers).</summary>
public sealed class ReadOnlyTests
{
    private static EncryptedDatabaseOptions ReadOnlyOptions(TempDirectory temp) =>
        new() { DatabasePath = temp.File("notes.db"), ReadOnly = true };

    [Fact]
    public async Task ReadOnly_ReadsWhatTheWriterWrote_AndSeesLaterWrites_ButCannotWrite()
    {
        using var temp = new TempDirectory();
        await using var writer = await TestDatabases.OpenAsync(temp);
        await writer.AddNoteAsync("first");

        await using var reader = await EncryptedDatabase.OpenAsync(ReadOnlyOptions(temp), TestDatabases.NotesV1);

        Assert.True(reader.IsReadOnly);
        Assert.False(writer.IsReadOnly);
        Assert.Equal(["first"], await reader.GetNotesAsync());

        await writer.AddNoteAsync("second");
        Assert.Equal(["first", "second"], await reader.GetNotesAsync());

        await Assert.ThrowsAsync<InvalidOperationException>(() => reader.AddNoteAsync("nope"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => reader.VacuumAsync());
    }

    [Fact]
    public async Task ReadOnly_ReadsAfterTheWriterHasClosed()
    {
        using var temp = new TempDirectory();
        await using (var writer = await TestDatabases.OpenAsync(temp))
        {
            await writer.AddNoteAsync("kept");
        }

        await using var reader = await EncryptedDatabase.OpenAsync(ReadOnlyOptions(temp), TestDatabases.NotesV1);

        Assert.Equal(["kept"], await reader.GetNotesAsync());
    }

    [Fact]
    public async Task ReadOnly_WithoutADatabase_FailsAndCreatesNothing()
    {
        using var temp = new TempDirectory();

        var error = await Assert.ThrowsAsync<LocalStoreException>(() => EncryptedDatabase.OpenAsync(ReadOnlyOptions(temp), TestDatabases.NotesV1));

        Assert.Equal(LocalStoreErrorKind.DatabaseMissing, error.Kind);
        Assert.Empty(Directory.GetFileSystemEntries(temp.Path));
    }

    [Fact]
    public async Task ReadOnly_WithoutTheKey_FailsAsKeyMissing_AndNeverMakesANewKey()
    {
        using var temp = new TempDirectory();
        await using (var writer = await TestDatabases.OpenAsync(temp))
        {
            await writer.AddNoteAsync("x");
        }

        File.Delete(temp.File("key.bin"));

        var error = await Assert.ThrowsAsync<LocalStoreException>(() => EncryptedDatabase.OpenAsync(ReadOnlyOptions(temp), TestDatabases.NotesV1));

        Assert.Equal(LocalStoreErrorKind.KeyMissing, error.Kind);
        Assert.False(File.Exists(temp.File("key.bin")));
    }

    [Fact]
    public async Task ReadOnly_OfAnOlderSchema_IsRefused_AndLeavesTheFileAlone()
    {
        using var temp = new TempDirectory();
        await using (var writer = await TestDatabases.OpenAsync(temp, TestDatabases.NotesV1))
        {
            await writer.AddNoteAsync("old");
        }

        var before = File.ReadAllBytes(temp.File("notes.db"));

        var error = await Assert.ThrowsAsync<LocalStoreException>(() => EncryptedDatabase.OpenAsync(ReadOnlyOptions(temp), TestDatabases.NotesV2));

        Assert.Equal(LocalStoreErrorKind.NeedsMigration, error.Kind);
        Assert.Equal(before, File.ReadAllBytes(temp.File("notes.db")));
        Assert.Empty(Directory.GetFiles(temp.Path, "*.bak*"));
    }

    [Fact]
    public async Task ReadOnly_OfANewerCompatibleSchema_OpensAsIs()
    {
        using var temp = new TempDirectory();
        await using (var writer = await TestDatabases.OpenAsync(temp, TestDatabases.NotesV2))
        {
            await writer.AddNoteAsync("new");
        }

        await using var reader = await EncryptedDatabase.OpenAsync(ReadOnlyOptions(temp), TestDatabases.NotesV1);

        Assert.Equal(2, reader.SchemaVersion);
        Assert.Equal(["new"], await reader.GetNotesAsync());
    }
}
