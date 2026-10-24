using Microsoft.Data.Sqlite;

namespace Pillion.LocalStore.Tests;

public sealed class DatabaseTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task OpenCreateAndReopen_RoundTripsTheData()
    {
        await using (var database = await TestDatabases.OpenAsync(_temp))
        {
            Assert.Equal(0, database.OpenedAtVersion);
            await database.AddNoteAsync("first");
            await database.AddNoteAsync("second");
        }

        await using (var reopened = await TestDatabases.OpenAsync(_temp))
        {
            Assert.Equal(1, reopened.OpenedAtVersion);
            Assert.Equal(["first", "second"], await reopened.GetNotesAsync());
        }
    }

    [Fact]
    public async Task TheJournalMode_IsWal_WithABusyTimeout()
    {
        await using var database = await EncryptedDatabase.OpenAsync(
            new EncryptedDatabaseOptions { DatabasePath = _temp.File("notes.db"), BusyTimeout = TimeSpan.FromSeconds(7) },
            TestDatabases.NotesV1);
        await database.AddNoteAsync("x");

        Assert.Equal("wal", await database.ScalarAsync("PRAGMA journal_mode"));
        Assert.Equal(7000L, await database.ScalarAsync("PRAGMA busy_timeout"));
        Assert.Equal(1L, await database.ScalarAsync("PRAGMA foreign_keys"));
        Assert.Equal(2L, await database.ScalarAsync("PRAGMA temp_store")); // MEMORY: no unencrypted temporary files.
        Assert.True(File.Exists(_temp.File("notes.db-wal")));
    }

    [Fact]
    public async Task ConcurrentWritersFromTwoTasks_RunOneAtATime()
    {
        await using var database = await TestDatabases.OpenAsync(_temp);
        var inside = 0;
        var maxInside = 0;

        async Task WriteManyAsync(string prefix)
        {
            for (var i = 0; i < 10; i++)
            {
                await database.WriteAsync(async (connection, transaction, token) =>
                {
                    var now = Interlocked.Increment(ref inside);
                    InterlockedMax(ref maxInside, now);
                    using var command = connection.CreateCommand();
                    command.Transaction = transaction;
                    command.CommandText = "INSERT INTO notes (text) VALUES ($text)";
                    command.Parameters.AddWithValue("$text", $"{prefix}{i}");
                    await command.ExecuteNonQueryAsync(token);
                    await Task.Delay(5, token); // Hold the writer so an overlap would show.
                    Interlocked.Decrement(ref inside);
                });
            }
        }

        await Task.WhenAll(Task.Run(() => WriteManyAsync("a")), Task.Run(() => WriteManyAsync("b")));

        Assert.Equal(1, maxInside);
        Assert.Equal(20, (await database.GetNotesAsync()).Count);
    }

    [Fact]
    public async Task TwoOpenInstancesOfOneFile_LikeTwoHosts_WriteConcurrentlyWithoutBusyErrors()
    {
        await using var first = await TestDatabases.OpenAsync(_temp);
        await using var second = await TestDatabases.OpenAsync(_temp);

        await Task.WhenAll(
            Task.Run(async () => { for (var i = 0; i < 25; i++) { await first.AddNoteAsync($"first {i}"); } }),
            Task.Run(async () => { for (var i = 0; i < 25; i++) { await second.AddNoteAsync($"second {i}"); } }));

        Assert.Equal(50, (await first.GetNotesAsync()).Count);
        Assert.Equal(50, (await second.GetNotesAsync()).Count);
    }

    [Fact]
    public async Task Readers_RunConcurrently_AlongsideAnOpenWrite()
    {
        await using var database = await TestDatabases.OpenAsync(_temp);
        await database.AddNoteAsync("committed");
        using var bothReading = new Barrier(2);
        var writeHeld = new TaskCompletionSource();
        var releaseWrite = new TaskCompletionSource();

        var write = database.WriteAsync(async (connection, transaction, token) =>
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO notes (text) VALUES ('uncommitted')";
            await command.ExecuteNonQueryAsync(token);
            writeHeld.SetResult();
            await releaseWrite.Task;
        });
        await writeHeld.Task;

        // Two reads meet at the barrier: neither can finish unless both run at once, while the write is still open.
        var reads = Enumerable.Range(0, 2).Select(_ => Task.Run(() => database.ReadAsync(async (connection, token) =>
        {
            Assert.True(bothReading.SignalAndWait(TimeSpan.FromSeconds(10), token), "the reads did not run concurrently");
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT count(*) FROM notes";
            return (long)(await command.ExecuteScalarAsync(token))!;
        }))).ToList();

        var counts = await Task.WhenAll(reads).WaitAsync(TimeSpan.FromSeconds(20));
        releaseWrite.SetResult();
        await write;

        Assert.All(counts, c => Assert.Equal(1L, c)); // The uncommitted row is not visible to readers.
        Assert.Equal(2, (await database.GetNotesAsync()).Count);
    }

    [Fact]
    public async Task ReaderConnections_CannotWrite()
    {
        await using var database = await TestDatabases.OpenAsync(_temp);

        var error = await Assert.ThrowsAsync<SqliteException>(() => database.ReadAsync(async (connection, token) =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO notes (text) VALUES ('nope')";
            return await command.ExecuteNonQueryAsync(token);
        }));

        Assert.Contains("readonly", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AFailedWrite_IsRolledBack()
    {
        await using var database = await TestDatabases.OpenAsync(_temp);

        await Assert.ThrowsAsync<InvalidOperationException>(() => database.WriteAsync(async (connection, transaction, token) =>
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO notes (text) VALUES ('rolled back')";
            await command.ExecuteNonQueryAsync(token);
            throw new InvalidOperationException("boom");
        }));

        Assert.Empty(await database.GetNotesAsync());
        await database.AddNoteAsync("after"); // The writer is free again.
        Assert.Equal(["after"], await database.GetNotesAsync());
    }

    [Fact]
    public async Task AfterDispose_CallsFail_AndTheFilesAreReleased()
    {
        var database = await TestDatabases.OpenAsync(_temp);
        await database.AddNoteAsync("x");
        await database.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => database.AddNoteAsync("y"));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => database.GetNotesAsync());
        File.Move(_temp.File("notes.db"), _temp.File("moved.db")); // Nothing holds the file open.
        Assert.False(File.Exists(_temp.File("notes.db-wal")));
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while ((current = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, current) != current)
        {
        }
    }
}
