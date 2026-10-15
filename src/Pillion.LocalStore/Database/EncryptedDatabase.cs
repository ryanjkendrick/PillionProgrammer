using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Pillion.LocalStore.Database;
using Pillion.LocalStore.Keys;

namespace Pillion.LocalStore;

/// <summary>
/// One encrypted SQLite database (design section 10): SQLite3 Multiple Ciphers with a DPAPI-protected 256-bit key
/// applied before any other statement, WAL journal mode, a busy timeout, one serialised writer per process and a small
/// pool of concurrent readers, and a <c>meta</c> table whose <c>schema_version</c> and <c>min_reader_version</c> decide
/// whether this build may open the file and which migrations to run.
/// </summary>
/// <remarks>
/// Writes go through <see cref="WriteAsync{T}(Func{SqliteConnection, SqliteTransaction, CancellationToken, Task{T}}, CancellationToken)"/>,
/// one at a time, each in its own <c>BEGIN IMMEDIATE</c> transaction. Reads go through
/// <see cref="ReadAsync{T}(Func{SqliteConnection, CancellationToken, Task{T}}, CancellationToken)"/> on reader connections
/// that are <c>query_only</c> and run alongside the writer (WAL). Other processes (another Visual Studio) open the same
/// file with their own writer; SQLite's locks and the busy timeout serialise them. Temporary tables and sorts are kept
/// in memory, so no unencrypted temporary file is written.
/// </remarks>
public sealed partial class EncryptedDatabase : IAsyncDisposable
{
    private readonly string _readerConnectionString;
    private readonly DatabaseKey _key;
    private readonly SqliteConnection _writer;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly SemaphoreSlim _readerSlots;
    private readonly ConcurrentBag<SqliteConnection> _idleReaders = [];
    private readonly int _maxReaders;
    private readonly int _busyTimeoutMs;
    private readonly ILogger _logger;
    private int _disposed;

    private EncryptedDatabase(
        string path,
        string keyPath,
        string readerConnectionString,
        DatabaseKey key,
        SqliteConnection writer,
        EncryptedDatabaseOptions options,
        MigrationOutcome migration,
        ILogger logger)
    {
        DatabasePath = path;
        KeyFilePath = keyPath;
        _readerConnectionString = readerConnectionString;
        _key = key;
        _writer = writer;
        _maxReaders = Math.Max(1, options.MaxReaders);
        _readerSlots = new SemaphoreSlim(_maxReaders, _maxReaders);
        _busyTimeoutMs = BusyTimeoutMilliseconds(options.BusyTimeout);
        _logger = logger;
        SchemaVersion = migration.Version;
        MinReaderVersion = migration.MinReaderVersion;
        OpenedAtVersion = migration.FromVersion;
        BackupPath = migration.BackupPath;
    }

    /// <summary>Gets the database file path.</summary>
    public string DatabasePath { get; }

    /// <summary>Gets the key file path.</summary>
    public string KeyFilePath { get; }

    /// <summary>Gets the schema version of the file after opening (it can be newer than this build when that version kept old readers compatible).</summary>
    public int SchemaVersion { get; }

    /// <summary>Gets the oldest schema version that can read the file.</summary>
    public int MinReaderVersion { get; }

    /// <summary>Gets the schema version the file had before this open migrated it; 0 for a new database.</summary>
    public int OpenedAtVersion { get; }

    /// <summary>Gets the backup made before migrating, or <see langword="null"/> when this open did not migrate an existing database.</summary>
    public string? BackupPath { get; }

    /// <summary>
    /// Opens (or creates) an encrypted database: loads or creates its key, applies the key, switches to WAL, refuses a
    /// file whose <c>min_reader_version</c> is newer than <paramref name="schema"/>, and migrates an older file after
    /// backing it up.
    /// </summary>
    /// <param name="options">Where the database and its key live.</param>
    /// <param name="schema">The schema this build reads and writes.</param>
    /// <param name="logger">The logger; key material is never logged.</param>
    /// <param name="cancellationToken">Cancels the open; a migration in progress is rolled back.</param>
    /// <returns>The open database; dispose it to close every connection.</returns>
    /// <exception cref="LocalStoreException">The cipher library, the key, the schema version or a migration stopped the open.</exception>
    /// <exception cref="PlatformNotSupportedException">The operating system is not Windows (the key is protected with DPAPI).</exception>
    public static async Task<EncryptedDatabase> OpenAsync(
        EncryptedDatabaseOptions options,
        StoreSchema schema,
        ILogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.DatabasePath);
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Pillion's encrypted databases protect their keys with Windows DPAPI.");
        }

        logger ??= NullLogger.Instance;
        cancellationToken.ThrowIfCancellationRequested();

        var library = SqliteCipher.Library;
        var path = Path.GetFullPath(options.DatabasePath);
        var keyPath = options.ResolvedKeyFilePath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var databaseExists = File.Exists(path) && new FileInfo(path).Length > 0;
        var key = DatabaseKeyFile.LoadOrCreate(keyPath, databaseExists);
        SqliteConnection? writer = null;
        try
        {
            var busyTimeoutMs = BusyTimeoutMilliseconds(options.BusyTimeout);
            var connectionString = BuildConnectionString(path, SqliteOpenMode.ReadWriteCreate);
            var readerConnectionString = BuildConnectionString(path, SqliteOpenMode.ReadWrite);
            writer = OpenConnection(connectionString, path, key, busyTimeoutMs, reader: false);
            EnableWal(writer);

            var migration = await MigrateAsync(writer, schema, path, databaseExists, logger, cancellationToken).ConfigureAwait(false);
            LogOpened(logger, path, migration.Version, migration.MinReaderVersion, library.CipherVersion, library.SqliteVersion);
            return new EncryptedDatabase(path, keyPath, readerConnectionString, key, writer, options, migration, logger);
        }
        catch
        {
            writer?.Dispose();
            key.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Runs a write on the process's single writer connection, in its own <c>BEGIN IMMEDIATE</c> transaction that is
    /// committed when <paramref name="work"/> completes and rolled back when it throws. Writes queue up and run one at a time.
    /// </summary>
    /// <typeparam name="T">The result type.</typeparam>
    /// <param name="work">The write. Commands it creates must use the transaction it is given.</param>
    /// <param name="cancellationToken">Cancels waiting for the writer, and is passed to <paramref name="work"/>.</param>
    /// <returns>The result of <paramref name="work"/>.</returns>
    public async Task<T> WriteAsync<T>(Func<SqliteConnection, SqliteTransaction, CancellationToken, Task<T>> work, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            using var transaction = _writer.BeginTransaction(deferred: false);
            var result = await work(_writer, transaction, cancellationToken).ConfigureAwait(false);
            transaction.Commit();
            return result;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>Runs a write that returns nothing; see <see cref="WriteAsync{T}(Func{SqliteConnection, SqliteTransaction, CancellationToken, Task{T}}, CancellationToken)"/>.</summary>
    /// <param name="work">The write.</param>
    /// <param name="cancellationToken">Cancels waiting for the writer, and is passed to <paramref name="work"/>.</param>
    /// <returns>A task that completes when the write is committed.</returns>
    public Task WriteAsync(Func<SqliteConnection, SqliteTransaction, CancellationToken, Task> work, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        return WriteAsync<bool>(
            async (connection, transaction, token) =>
            {
                await work(connection, transaction, token).ConfigureAwait(false);
                return true;
            },
            cancellationToken);
    }

    /// <summary>
    /// Runs a read on a pooled, <c>query_only</c> reader connection. Up to <see cref="EncryptedDatabaseOptions.MaxReaders"/>
    /// reads run at once, alongside the writer. A read that needs a consistent view across several statements opens
    /// its own transaction on the connection.
    /// </summary>
    /// <typeparam name="T">The result type.</typeparam>
    /// <param name="work">The read.</param>
    /// <param name="cancellationToken">Cancels waiting for a reader, and is passed to <paramref name="work"/>.</param>
    /// <returns>The result of <paramref name="work"/>.</returns>
    public async Task<T> ReadAsync<T>(Func<SqliteConnection, CancellationToken, Task<T>> work, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        await _readerSlots.WaitAsync(cancellationToken).ConfigureAwait(false);
        SqliteConnection? connection = null;
        try
        {
            ThrowIfDisposed();
            if (!_idleReaders.TryTake(out connection))
            {
                connection = OpenConnection(_readerConnectionString, DatabasePath, _key, _busyTimeoutMs, reader: true);
            }

            return await work(connection, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (connection is not null)
            {
                if (Volatile.Read(ref _disposed) != 0)
                {
                    connection.Dispose();
                }
                else
                {
                    _idleReaders.Add(connection);
                }
            }

            _readerSlots.Release();
        }
    }

    /// <summary>
    /// Rebuilds the file to drop freed pages (<c>VACUUM</c>, which keeps the encryption) and folds the WAL back into it.
    /// Used after a large delete, such as Delete all.
    /// </summary>
    /// <param name="cancellationToken">Cancels waiting for the writer.</param>
    /// <returns>A task that completes when the file has been compacted.</returns>
    public async Task VacuumAsync(CancellationToken cancellationToken = default)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            Execute(_writer, "VACUUM");
            TryCheckpoint(_writer);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>Deletes the backups that migrations made of this database (<c>&lt;file&gt;.v&lt;n&gt;.bak</c> and its WAL).</summary>
    /// <returns>The number of files deleted.</returns>
    public int DeleteBackups()
    {
        var directory = Path.GetDirectoryName(DatabasePath)!;
        var count = 0;
        foreach (var file in Directory.EnumerateFiles(directory, Path.GetFileName(DatabasePath) + ".v*.bak*"))
        {
            File.Delete(file);
            count++;
        }

        return count;
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        // Wait for the write and the reads in progress, then close every connection. Waiters released afterwards see
        // the disposed flag and throw.
        await _writeGate.WaitAsync().ConfigureAwait(false);
        for (var i = 0; i < _maxReaders; i++)
        {
            await _readerSlots.WaitAsync().ConfigureAwait(false);
        }

        try
        {
            while (_idleReaders.TryTake(out var reader))
            {
                reader.Dispose();
            }

            _writer.Dispose();
            _key.Dispose();
        }
        finally
        {
            _readerSlots.Release(_maxReaders);
            _writeGate.Release();
        }
    }

    private static string BuildConnectionString(string path, SqliteOpenMode mode) => new SqliteConnectionStringBuilder
    {
        DataSource = path,
        Mode = mode,
        Cache = SqliteCacheMode.Private,

        // Connections are kept open by this class; pooling would hide when the key is applied.
        Pooling = false,
    }.ToString();

    private static int BusyTimeoutMilliseconds(TimeSpan timeout) =>
        (int)Math.Clamp(timeout.TotalMilliseconds, 0, int.MaxValue);

    /// <summary>
    /// Opens a connection and applies the key before any other statement, then checks the key and sets the per-connection
    /// pragmas. Closes the connection on failure.
    /// </summary>
    private static SqliteConnection OpenConnection(string connectionString, string path, DatabaseKey key, int busyTimeoutMs, bool reader)
    {
        var connection = new SqliteConnection(connectionString);
        try
        {
            connection.Open();
            ApplyKey(connection, key);
            VerifyKey(connection, path);

            Execute(connection, string.Create(CultureInfo.InvariantCulture, $"PRAGMA busy_timeout = {busyTimeoutMs}"));
            Execute(connection, "PRAGMA foreign_keys = ON");
            Execute(connection, "PRAGMA temp_store = MEMORY");
            Execute(connection, "PRAGMA secure_delete = ON");
            Execute(connection, "PRAGMA synchronous = NORMAL");
            if (reader)
            {
                Execute(connection, "PRAGMA query_only = ON");
            }

            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private static void ApplyKey(SqliteConnection connection, DatabaseKey key)
    {
        using var command = connection.CreateCommand();
        command.CommandText = key.ToHexKeyPragma();
        var result = command.ExecuteScalar() as string;
        command.CommandText = string.Empty;

        // SQLite3 Multiple Ciphers answers "ok"; a plain SQLite ignores the unknown pragma and returns no row, which would
        // leave the file unencrypted.
        if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
        {
            throw new LocalStoreException(
                LocalStoreErrorKind.CipherUnavailable,
                "Pillion's encrypted databases are unavailable: the SQLite library did not accept the database key, so nothing is stored.",
                "Reinstall Pillion Programmer; the e_sqlite3mc library beside Pillion Host may be missing or replaced.");
        }
    }

    private static void VerifyKey(SqliteConnection connection, string path)
    {
        try
        {
            ExecuteScalar(connection, null, "SELECT count(*) FROM sqlite_schema");
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode is 26 /* SQLITE_NOTADB */ or 11 /* SQLITE_CORRUPT */)
        {
            throw new LocalStoreException(
                LocalStoreErrorKind.KeyMismatch,
                $"Pillion's encrypted database ({Path.GetFileName(path)}) cannot be opened: its key does not match, or the file is damaged or not a Pillion database.",
                "Put back the key file that belongs to this database, or delete the database and its key file to start again (the contents cannot be recovered without the key).",
                path,
                ex);
        }
    }

    private static void EnableWal(SqliteConnection connection)
    {
        var mode = ExecuteScalar(connection, null, "PRAGMA journal_mode = WAL") as string;
        if (!string.Equals(mode, "wal", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"The database could not be switched to WAL journal mode (it reports '{mode}').");
        }
    }

    private static async Task<MigrationOutcome> MigrateAsync(
        SqliteConnection writer,
        StoreSchema schema,
        string path,
        bool databaseExisted,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        if (databaseExisted)
        {
            // Fold the WAL into the main file first, so a backup is usually that one file.
            TryCheckpoint(writer);
        }

        // BEGIN IMMEDIATE: holds the write lock, so another process cannot migrate or write while this one decides.
        using var transaction = writer.BeginTransaction(deferred: false);
        ExecuteNonQuery(writer, transaction, "CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY NOT NULL, value TEXT NOT NULL) WITHOUT ROWID");
        var version = ReadMetaInt(writer, transaction, MetaKeys.SchemaVersion);
        var minReader = ReadMetaInt(writer, transaction, MetaKeys.MinReaderVersion);

        if (minReader > schema.CurrentVersion)
        {
            throw new LocalStoreException(
                LocalStoreErrorKind.NewerSchema,
                $"Pillion's {schema.Name} database was written by a newer Pillion Programmer: it has schema version {version} and needs a reader of version {minReader} or later, and this version reads up to {schema.CurrentVersion}. It was not opened or changed.",
                "Update Pillion Programmer in this Visual Studio to the version that wrote the database.",
                path);
        }

        if (version >= schema.CurrentVersion)
        {
            if (version > schema.CurrentVersion)
            {
                LogNewerCompatible(logger, path, version, minReader, schema.CurrentVersion);
            }

            transaction.Commit();
            return new MigrationOutcome(version, version, minReader, BackupPath: null);
        }

        string? backup = null;
        if (version > 0)
        {
            backup = BackupFiles(path, version);
        }

        var applied = version;
        var appliedMinReader = minReader;
        try
        {
            foreach (var migration in schema.Migrations.Where(m => m.Version > version))
            {
                cancellationToken.ThrowIfCancellationRequested();
                LogMigrating(logger, path, migration.Version, migration.Description);
                await migration.ApplyAsync(writer, transaction, cancellationToken).ConfigureAwait(false);
                applied = migration.Version;
                appliedMinReader = migration.MinReaderVersion;
            }

            WriteMeta(writer, transaction, MetaKeys.SchemaVersion, applied.ToString(CultureInfo.InvariantCulture));
            WriteMeta(writer, transaction, MetaKeys.MinReaderVersion, appliedMinReader.ToString(CultureInfo.InvariantCulture));
            WriteMeta(writer, transaction, MetaKeys.Store, schema.Name);
            WriteMeta(writer, transaction, MetaKeys.WrittenBy, WrittenBy);
            if (version == 0)
            {
                WriteMeta(writer, transaction, MetaKeys.CreatedUtc, DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            }

            transaction.Commit();
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not LocalStoreException)
        {
            LogMigrationFailed(logger, ex, path, version, applied + 1);
            throw new LocalStoreException(
                LocalStoreErrorKind.MigrationFailed,
                $"Pillion's {schema.Name} database could not be upgraded from schema version {version} to {schema.CurrentVersion}; it was left unchanged at version {version}.",
                backup is null ? "Report the problem with the Pillion Host log." : $"Report the problem with the Pillion Host log. A backup was made before the upgrade: {Path.GetFileName(backup)}.",
                path,
                ex);
        }

        if (backup is not null)
        {
            LogMigrated(logger, path, version, applied, backup);
        }

        return new MigrationOutcome(version, applied, appliedMinReader, backup);
    }

    /// <summary>
    /// Copies the database file (still encrypted) and its WAL, while the caller holds the write lock, to
    /// <c>&lt;file&gt;.v&lt;version&gt;.bak</c> and <c>&lt;file&gt;.v&lt;version&gt;.bak-wal</c>: SQLite opens the pair as one database.
    /// </summary>
    private static string BackupFiles(string path, int version)
    {
        var backup = string.Create(CultureInfo.InvariantCulture, $"{path}.v{version}.bak");
        CopyShared(path, backup);

        var wal = path + "-wal";
        var backupWal = backup + "-wal";
        if (File.Exists(wal) && new FileInfo(wal).Length > 0)
        {
            CopyShared(wal, backupWal);
        }
        else if (File.Exists(backupWal))
        {
            File.Delete(backupWal);
        }

        return backup;
    }

    private static void CopyShared(string source, string destination)
    {
        // SQLite holds the file open for writing, so it is read with sharing rather than File.Copy.
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None);
        input.CopyTo(output);
        output.Flush(flushToDisk: true);
    }

    private static void TryCheckpoint(SqliteConnection connection)
    {
        try
        {
            Execute(connection, "PRAGMA wal_checkpoint(TRUNCATE)");
        }
        catch (SqliteException)
        {
            // Best effort: another process's readers can keep the WAL in use.
        }
    }

    private static int ReadMetaInt(SqliteConnection connection, SqliteTransaction transaction, string key)
    {
        var value = ExecuteScalar(connection, transaction, "SELECT value FROM meta WHERE key = $key", ("$key", key)) as string;
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? number : 0;
    }

    private static void WriteMeta(SqliteConnection connection, SqliteTransaction transaction, string key, string value) =>
        ExecuteNonQuery(
            connection,
            transaction,
            "INSERT INTO meta (key, value) VALUES ($key, $value) ON CONFLICT (key) DO UPDATE SET value = excluded.value",
            ("$key", key),
            ("$value", value));

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            // Pragmas such as wal_checkpoint return a row; drain it.
        }
    }

    private static void ExecuteNonQuery(SqliteConnection connection, SqliteTransaction? transaction, string sql, params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        command.ExecuteNonQuery();
    }

    private static object? ExecuteScalar(SqliteConnection connection, SqliteTransaction? transaction, string sql, params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return command.ExecuteScalar();
    }

    private static string WrittenBy =>
        "Pillion.LocalStore " + (typeof(EncryptedDatabase).Assembly.GetName().Version?.ToString() ?? "unknown");

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    [LoggerMessage(EventId = 2100, Level = LogLevel.Information, Message = "Opened encrypted database {Path} (schema {SchemaVersion}, readers from {MinReaderVersion}) with {CipherVersion} on SQLite {SqliteVersion}.")]
    private static partial void LogOpened(ILogger logger, string path, int schemaVersion, int minReaderVersion, string cipherVersion, string sqliteVersion);

    [LoggerMessage(EventId = 2101, Level = LogLevel.Information, Message = "Migrating {Path} to schema {Version}: {Description}.")]
    private static partial void LogMigrating(ILogger logger, string path, int version, string description);

    [LoggerMessage(EventId = 2102, Level = LogLevel.Information, Message = "Migrated {Path} from schema {FromVersion} to {ToVersion}; the previous file was backed up to {BackupPath}.")]
    private static partial void LogMigrated(ILogger logger, string path, int fromVersion, int toVersion, string backupPath);

    [LoggerMessage(EventId = 2103, Level = LogLevel.Error, Message = "Migrating {Path} from schema {FromVersion} failed at version {FailedVersion}; the transaction was rolled back.")]
    private static partial void LogMigrationFailed(ILogger logger, Exception exception, string path, int fromVersion, int failedVersion);

    [LoggerMessage(EventId = 2104, Level = LogLevel.Information, Message = "{Path} has schema {SchemaVersion}, newer than this build's {CurrentVersion}, and stays readable from version {MinReaderVersion}; opened without migrating.")]
    private static partial void LogNewerCompatible(ILogger logger, string path, int schemaVersion, int minReaderVersion, int currentVersion);

    /// <summary>The well-known <c>meta</c> keys.</summary>
    internal static class MetaKeys
    {
        /// <summary>The schema version of the file.</summary>
        public const string SchemaVersion = "schema_version";

        /// <summary>The oldest schema version that can read the file.</summary>
        public const string MinReaderVersion = "min_reader_version";

        /// <summary>The database kind (<see cref="StoreSchema.Name"/>).</summary>
        public const string Store = "store";

        /// <summary>The library version that last migrated the file.</summary>
        public const string WrittenBy = "written_by";

        /// <summary>When the file was created (ISO 8601, UTC).</summary>
        public const string CreatedUtc = "created_utc";
    }

    private sealed record MigrationOutcome(int FromVersion, int Version, int MinReaderVersion, string? BackupPath);
}
