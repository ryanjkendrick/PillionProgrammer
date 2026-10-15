using Microsoft.Data.Sqlite;
using Pillion.Indexing.Sweep;
using Pillion.LocalStore;

namespace Pillion.Indexing.Storage;

/// <summary>
/// The typed API over the <c>files</c> table of <c>index.db</c>. Reads work on any open index; writes need the index's
/// owner (a read-only opener's database refuses them). Paths are relative to the repository root with <c>/</c>
/// separators and compare without regard to case. Hashes are stored as the signed 64-bit integer with the same bits.
/// </summary>
public sealed class FilesRepository
{
    private const string SelectColumns = "id, path, language, partition, hash, size, mtime, indexed_at";

    private readonly EncryptedDatabase _database;
    private readonly TimeProvider _time;

    /// <summary>Initializes a new instance of the <see cref="FilesRepository"/> class.</summary>
    /// <param name="database">An open <c>index.db</c> with <see cref="IndexSchema"/>.</param>
    /// <param name="time">The clock for <c>indexed_at</c>; the system clock when <see langword="null"/>.</param>
    public FilesRepository(EncryptedDatabase database, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(database);
        _database = database;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Gets every row, ordered by path.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The rows.</returns>
    public Task<IReadOnlyList<FileRecord>> GetAllAsync(CancellationToken cancellationToken = default) =>
        _database.ReadAsync<IReadOnlyList<FileRecord>>(
            async (connection, token) =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = $"SELECT {SelectColumns} FROM files ORDER BY path";
                return await ReadRecordsAsync(command, token).ConfigureAwait(false);
            },
            cancellationToken);

    /// <summary>Gets one row by path.</summary>
    /// <param name="path">The path relative to the root.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The row, or <see langword="null"/> when the file is not stored.</returns>
    public Task<FileRecord?> GetAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        return _database.ReadAsync<FileRecord?>(
            async (connection, token) =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = $"SELECT {SelectColumns} FROM files WHERE path = $path";
                command.Parameters.AddWithValue("$path", path);
                return (await ReadRecordsAsync(command, token).ConfigureAwait(false)).FirstOrDefault();
            },
            cancellationToken);
    }

    /// <summary>Counts the rows.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The number of stored files.</returns>
    public Task<long> CountAsync(CancellationToken cancellationToken = default) =>
        _database.ReadAsync(
            async (connection, token) =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT count(*) FROM files";
                return (long)(await command.ExecuteScalarAsync(token).ConfigureAwait(false) ?? 0L);
            },
            cancellationToken);

    /// <summary>
    /// Inserts new files and updates existing ones by path, keeping the id of an existing row. <c>indexed_at</c> is set to
    /// now for every record, whatever the record says.
    /// </summary>
    /// <param name="records">The records to store.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>The stored rows, with their ids.</returns>
    public Task<IReadOnlyList<FileRecord>> UpsertAsync(IEnumerable<FileRecord> records, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(records);
        var batch = records.ToList();
        return _database.WriteAsync<IReadOnlyList<FileRecord>>(
            (connection, transaction, token) => Task.FromResult<IReadOnlyList<FileRecord>>(Upsert(connection, transaction, batch, _time.GetUtcNow().ToUnixTimeMilliseconds(), token)),
            cancellationToken);
    }

    /// <summary>Deletes rows by path.</summary>
    /// <param name="paths">The paths relative to the root.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>The number of rows deleted.</returns>
    public Task<int> DeleteAsync(IEnumerable<string> paths, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var batch = paths.ToList();
        return _database.WriteAsync(
            (connection, transaction, token) => Task.FromResult(Delete(connection, transaction, batch, token)),
            cancellationToken);
    }

    /// <summary>
    /// Applies a whole sweep in one transaction: stores the added, changed and touched files and deletes the deleted
    /// ones. The pipeline normally stores added and changed files itself, after chunking them; this is for callers that
    /// only track files.
    /// </summary>
    /// <param name="sweep">The sweep to apply.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>The rows stored and the number deleted.</returns>
    public Task<(IReadOnlyList<FileRecord> Stored, int Deleted)> CommitAsync(SweepResult sweep, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sweep);
        return _database.WriteAsync<(IReadOnlyList<FileRecord>, int)>(
            (connection, transaction, token) =>
            {
                var now = _time.GetUtcNow().ToUnixTimeMilliseconds();
                var deleted = Delete(connection, transaction, sweep.Deleted.Select(r => r.Path).ToList(), token);
                var stored = Upsert(connection, transaction, [.. sweep.Added, .. sweep.Changed, .. sweep.Touched], now, token);
                return Task.FromResult<(IReadOnlyList<FileRecord>, int)>((stored, deleted));
            },
            cancellationToken);
    }

    private static List<FileRecord> Upsert(SqliteConnection connection, SqliteTransaction transaction, List<FileRecord> records, long now, CancellationToken cancellationToken)
    {
        var stored = new List<FileRecord>(records.Count);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            INSERT INTO files (path, language, partition, hash, size, mtime, indexed_at)
            VALUES ($path, $language, $partition, $hash, $size, $mtime, $indexedAt)
            ON CONFLICT (path) DO UPDATE SET
                path = excluded.path,
                language = excluded.language,
                partition = excluded.partition,
                hash = excluded.hash,
                size = excluded.size,
                mtime = excluded.mtime,
                indexed_at = excluded.indexed_at
            RETURNING {SelectColumns}
            """;
        var path = command.Parameters.Add("$path", SqliteType.Text);
        var language = command.Parameters.Add("$language", SqliteType.Text);
        var partition = command.Parameters.Add("$partition", SqliteType.Text);
        var hash = command.Parameters.Add("$hash", SqliteType.Integer);
        var size = command.Parameters.Add("$size", SqliteType.Integer);
        var mtime = command.Parameters.Add("$mtime", SqliteType.Integer);
        var indexedAt = command.Parameters.Add("$indexedAt", SqliteType.Integer);
        foreach (var record in records)
        {
            cancellationToken.ThrowIfCancellationRequested();
            path.Value = record.Path;
            language.Value = record.Language;
            partition.Value = record.Partition;
            hash.Value = unchecked((long)record.Hash);
            size.Value = record.Size;
            mtime.Value = record.ModifiedUnixMilliseconds;
            indexedAt.Value = now;
            using var reader = command.ExecuteReader();
            if (reader.Read())
            {
                stored.Add(ReadRecord(reader));
            }
        }

        return stored;
    }

    private static int Delete(SqliteConnection connection, SqliteTransaction transaction, List<string> paths, CancellationToken cancellationToken)
    {
        var deleted = 0;
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM files WHERE path = $path";
        var path = command.Parameters.Add("$path", SqliteType.Text);
        foreach (var value in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            path.Value = value;
            deleted += command.ExecuteNonQuery();
        }

        return deleted;
    }

    private static async Task<List<FileRecord>> ReadRecordsAsync(SqliteCommand command, CancellationToken cancellationToken)
    {
        var records = new List<FileRecord>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            records.Add(ReadRecord(reader));
        }

        return records;
    }

    private static FileRecord ReadRecord(SqliteDataReader reader) => new(
        reader.GetInt64(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetString(3),
        unchecked((ulong)reader.GetInt64(4)),
        reader.GetInt64(5),
        reader.GetInt64(6),
        reader.GetInt64(7));
}
