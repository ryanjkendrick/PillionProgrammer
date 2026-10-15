using System.Text;
using Microsoft.Data.Sqlite;
using Pillion.LocalStore.Conversations;
using Pillion.LocalStore.Database;

namespace Pillion.LocalStore.Tests;

/// <summary>
/// Proves that SQLite3 Multiple Ciphers loads under .NET 10 on this machine and that <c>PRAGMA hexkey</c> really encrypts:
/// the file has no SQLite header, a keyless open fails, and plain text never reaches the database, WAL or backup files.
/// </summary>
public sealed class EncryptionTests : IDisposable
{
    private static readonly byte[] SqliteHeader = Encoding.ASCII.GetBytes("SQLite format 3\0");
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void TheLoadedLibrary_IsSqlite3MultipleCiphers()
    {
        var library = SqliteCipher.Library;

        Assert.StartsWith("SQLite3 Multiple Ciphers", library.CipherVersion, StringComparison.Ordinal);
        Assert.StartsWith("3.", library.SqliteVersion, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ANewDatabase_HasNoSqliteHeader_AndCannotBeOpenedWithoutTheKey()
    {
        await using (var database = await TestDatabases.OpenAsync(_temp))
        {
            await database.AddNoteAsync("hello");
        }

        var path = _temp.File("notes.db");
        var header = new byte[16];
        using (var stream = File.OpenRead(path))
        {
            stream.ReadExactly(header);
        }

        Assert.NotEqual(SqliteHeader, header);

        using var plain = TestDatabases.OpenWithoutKey(path);
        using var command = plain.CreateCommand();
        command.CommandText = "SELECT count(*) FROM sqlite_schema";
        var error = Assert.Throws<SqliteException>(() => command.ExecuteScalar());
        Assert.Equal(26, error.SqliteErrorCode); // SQLITE_NOTADB: "file is not a database"
    }

    [Fact]
    public async Task TheCipher_IsChaCha20()
    {
        await using var database = await TestDatabases.OpenAsync(_temp);

        Assert.Equal("chacha20", await database.ScalarAsync("PRAGMA cipher"));
    }

    [Fact]
    public async Task PlainText_NeverReachesTheDatabaseOrItsWal()
    {
        const string canary = "PILLION-CANARY-7d1f0c9e-plaintext-must-not-hit-the-disk";
        var databasePath = _temp.File("conversations.db");

        await using (var database = await TestDatabases.OpenConversationsAsync(_temp))
        {
            var repository = new ConversationRepository(database);
            await repository.SaveConversationAsync(new ConversationRecord("c1", canary, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
            await repository.AddMessageAsync(new NewMessage("c1", "user", canary + " in a message", DateTimeOffset.UtcNow));

            // While open, the latest writes sit in the WAL.
            Assert.True(new FileInfo(databasePath + "-wal").Length > 0);
            AssertNoCanary(databasePath + "-wal", canary);
            AssertNoCanary(databasePath, canary);
        }

        AssertNoCanary(databasePath, canary);
    }

    private static void AssertNoCanary(string path, string canary)
    {
        byte[] bytes;
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var memory = new MemoryStream())
        {
            stream.CopyTo(memory);
            bytes = memory.ToArray();
        }

        Assert.True(bytes.Length > 0, $"{path} is empty");
        Assert.Equal(-1, bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(canary)));
        Assert.Equal(-1, bytes.AsSpan().IndexOf(Encoding.Unicode.GetBytes(canary)));
        Assert.Equal(-1, bytes.AsSpan().IndexOf("CREATE TABLE"u8));
    }
}
