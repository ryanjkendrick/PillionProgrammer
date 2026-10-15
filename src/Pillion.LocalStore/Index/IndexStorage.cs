using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Pillion.LocalStore.Index.SqliteVec;

namespace Pillion.LocalStore.Index;

/// <summary>Which <see cref="IVectorIndex"/> backend an <see cref="IndexStorage"/> uses.</summary>
public enum VectorBackendPreference
{
    /// <summary>
    /// <see cref="AutoVectorIndex"/>: the in-memory brute-force search while the vectors fit
    /// <see cref="IndexStorageOptions.VectorCacheMaxBytes"/>, then sqlite-vec when the extension loaded, then the
    /// brute-force table scan.
    /// </summary>
    Auto,

    /// <summary><see cref="SqliteVecIndex"/>; opening fails when the extension did not load.</summary>
    SqliteVec,

    /// <summary><see cref="BruteForceVectorIndex"/>, even when the extension loaded (writes still keep the sqlite-vec table in step).</summary>
    BruteForce,
}

/// <summary>How an <see cref="IndexStorage"/> searches.</summary>
public sealed class IndexStorageOptions
{
    /// <summary>Gets the vector backend. Defaults to <see cref="VectorBackendPreference.Auto"/>.</summary>
    public VectorBackendPreference VectorBackend { get; init; } = VectorBackendPreference.Auto;

    /// <summary>
    /// Gets a value indicating whether to load sqlite-vec on the database's connections (when it is available). Defaults
    /// to <see langword="true"/>; tests turn it off to act as a host without the extension.
    /// </summary>
    public bool LoadSqliteVec { get; init; } = true;

    /// <summary>Gets the BM25 weight of the <c>text</c> column (breadcrumb and text, whole identifiers). Defaults to 1.0.</summary>
    public double TextWeight { get; init; } = 1.0;

    /// <summary>
    /// Gets the largest in-memory copy of the int8 vectors the brute-force backend keeps, in bytes (about the chunk count
    /// times the dimension). Defaults to 64 MiB, enough for 170,000 chunks of 384 dimensions; 0 turns the copy off, so every
    /// brute-force search scans the table.
    /// </summary>
    public long VectorCacheMaxBytes { get; init; } = 64L * 1024 * 1024;

    /// <summary>
    /// Gets the page cache of each connection in KiB (SQLite's <c>cache_size</c>), or 0 for SQLite's default (2 MiB). A
    /// larger cache keeps decrypted pages of the sqlite-vec table in memory between searches, per connection.
    /// </summary>
    public int PageCacheKiB { get; init; }

    /// <summary>Gets the BM25 weight of the <c>identifiers</c> column (split identifiers). Defaults to 0.5, so a whole-identifier match ranks above a split one.</summary>
    public double IdentifiersWeight { get; init; } = 0.5;
}

/// <summary>
/// The lexical, vector and symbol storage of one index database (work item 2.8): opens the encrypted database with the
/// sqlite-vec extension loaded on every connection right after the key (when the pinned, hash-checked library is
/// present), chooses the vector backend and logs the choice once per process, brings the sqlite-vec table up to date,
/// and hands out the repositories.
/// </summary>
public sealed partial class IndexStorage : IAsyncDisposable
{
    private static int _choiceLogged;
    private readonly bool _ownsDatabase;

    private IndexStorage(EncryptedDatabase database, bool ownsDatabase, bool sqliteVecLoaded, IndexStorageOptions options)
    {
        Database = database;
        _ownsDatabase = ownsDatabase;
        SqliteVecLoaded = sqliteVecLoaded;
        Options = options;
        var vectors = new VectorStorage(database, sqliteVecLoaded, options.VectorCacheMaxBytes);
        Storage = vectors;
        Chunks = new ChunkRepository(vectors, options.TextWeight, options.IdentifiersWeight);
        Symbols = new SymbolRepository(database);
        SqliteVec = sqliteVecLoaded ? new SqliteVecIndex(vectors) : null;
        BruteForce = new BruteForceVectorIndex(vectors);
        Vectors = options.VectorBackend switch
        {
            VectorBackendPreference.BruteForce => BruteForce,
            VectorBackendPreference.SqliteVec when SqliteVec is not null => SqliteVec,
            _ => new AutoVectorIndex(vectors),
        };
    }

    /// <summary>Gets the database; repository discovery (work item 2.3) shares it for <c>files</c> and <c>meta</c>.</summary>
    public EncryptedDatabase Database { get; }

    /// <summary>Gets a value indicating whether sqlite-vec is loaded on the database's connections.</summary>
    public bool SqliteVecLoaded { get; }

    /// <summary>Gets the options.</summary>
    public IndexStorageOptions Options { get; }

    /// <summary>Gets the chosen vector backend.</summary>
    public IVectorIndex Vectors { get; }

    /// <summary>Gets the sqlite-vec backend, or <see langword="null"/> when the extension is not loaded.</summary>
    public SqliteVecIndex? SqliteVec { get; }

    /// <summary>Gets the brute-force backend (always available; the Index window and tests compare the two).</summary>
    public BruteForceVectorIndex BruteForce { get; }

    /// <summary>Gets the chunks, their FTS5 rows and lexical search.</summary>
    public IChunkRepository Chunks { get; }

    /// <summary>Gets the symbols and reference edges.</summary>
    public ISymbolRepository Symbols { get; }

    internal VectorStorage Storage { get; }

    /// <summary>
    /// Gets the per-connection hook that loads sqlite-vec when it is available; set it as
    /// <see cref="EncryptedDatabaseOptions.ConnectionOpened"/> when opening an index database elsewhere, then pass the
    /// database to <see cref="CreateAsync"/>.
    /// </summary>
    public static Action<SqliteConnection> LoadSqliteVecHook { get; } = connection => SqliteVecExtension.TryLoad(connection);

    /// <summary>Opens (or creates and migrates) an index database with sqlite-vec loaded, and its storage.</summary>
    /// <param name="databaseOptions">Where the database and its key live; its <see cref="EncryptedDatabaseOptions.ConnectionOpened"/> runs after the extension loads.</param>
    /// <param name="schema">The index schema: version 1 (<c>files</c>) followed by <see cref="IndexStorageSchema.Migration"/>.</param>
    /// <param name="options">How to search, or <see langword="null"/> for the defaults.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="cancellationToken">Cancels the open.</param>
    /// <returns>The storage; disposing it closes the database.</returns>
    public static async Task<IndexStorage> OpenAsync(
        EncryptedDatabaseOptions databaseOptions,
        StoreSchema schema,
        IndexStorageOptions? options = null,
        ILogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(databaseOptions);
        ArgumentNullException.ThrowIfNull(schema);
        options ??= new IndexStorageOptions();
        var inner = databaseOptions.ConnectionOpened;
        var loadSqliteVec = options.LoadSqliteVec;
        var pageCacheKiB = options.PageCacheKiB;
        void Hook(SqliteConnection connection)
        {
            if (loadSqliteVec)
            {
                SqliteVecExtension.TryLoad(connection);
            }

            if (pageCacheKiB > 0)
            {
                using var command = connection.CreateCommand();
                command.CommandText = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"PRAGMA cache_size = -{pageCacheKiB}");
                command.ExecuteNonQuery();
            }

            inner?.Invoke(connection);
        }

        var database = await EncryptedDatabase.OpenAsync(
            new EncryptedDatabaseOptions
            {
                DatabasePath = databaseOptions.DatabasePath,
                KeyFilePath = databaseOptions.KeyFilePath,
                BusyTimeout = databaseOptions.BusyTimeout,
                MaxReaders = databaseOptions.MaxReaders,
                ConnectionOpened = Hook,
            },
            schema,
            logger,
            cancellationToken).ConfigureAwait(false);
        try
        {
            return await CreateAsync(database, ownsDatabase: true, options, logger, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await database.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Creates the storage over an index database that is already open (with <see cref="LoadSqliteVecHook"/>, for sqlite-vec).</summary>
    /// <param name="database">The database, at index schema version 2 or later.</param>
    /// <param name="ownsDatabase">Whether disposing the storage disposes the database.</param>
    /// <param name="options">How to search, or <see langword="null"/> for the defaults.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The storage.</returns>
    /// <exception cref="InvalidOperationException">The database is older than index schema version 2, or sqlite-vec was required and is not loaded.</exception>
    public static async Task<IndexStorage> CreateAsync(
        EncryptedDatabase database,
        bool ownsDatabase,
        IndexStorageOptions? options = null,
        ILogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(database);
        options ??= new IndexStorageOptions();
        logger ??= NullLogger.Instance;
        if (database.SchemaVersion < IndexStorageSchema.Version)
        {
            throw new InvalidOperationException($"The index database is at schema version {database.SchemaVersion}; index storage needs version {IndexStorageSchema.Version} or later.");
        }

        var loaded = await database.ReadAsync(
            (connection, _) =>
            {
                try
                {
                    using var command = connection.CreateCommand();
                    command.CommandText = "SELECT vec_version()";
                    command.ExecuteScalar();
                    return Task.FromResult(true);
                }
                catch (SqliteException)
                {
                    return Task.FromResult(false);
                }
            },
            cancellationToken).ConfigureAwait(false);

        if (options.VectorBackend == VectorBackendPreference.SqliteVec && !loaded)
        {
            throw new InvalidOperationException($"The sqlite-vec backend was required, but the extension is not loaded: {SqliteVecExtension.Status.Reason ?? "it was not requested for this database"}.");
        }

        var storage = new IndexStorage(database, ownsDatabase, loaded, options);
        LogChoice(logger, storage, options);
        var maintenance = await storage.Storage.MaintainAsync(cancellationToken).ConfigureAwait(false);
        if (maintenance == VectorMaintenance.Rebuilt)
        {
            LogRebuilt(logger, database.DatabasePath);
        }

        return storage;
    }

    /// <summary>Gets the memory held by the brute-force backend's in-memory copy of the vectors, in bytes (0 before the first search).</summary>
    public long VectorCacheBytes => Storage.CacheBytes;

    /// <summary>
    /// Loads (or reloads, after vectors changed) the in-memory copy of the vectors now, so the next search does not pay
    /// for it: about 0.8 s for 100,000 chunks of 384 dimensions. The indexing pipeline can call it after each batch.
    /// </summary>
    /// <param name="cancellationToken">Cancels waiting for a reader connection.</param>
    /// <returns>The bytes the copy holds; 0 when there are no vectors or they do not fit <see cref="IndexStorageOptions.VectorCacheMaxBytes"/>.</returns>
    public Task<long> WarmVectorCacheAsync(CancellationToken cancellationToken = default) =>
        Database.ReadAsync(
            (connection, _) =>
            {
                var dims = VectorStorage.ReadDimensions(connection, null);
                return Task.FromResult(dims is null ? 0 : Storage.GetCache(connection, dims.Value)?.Bytes ?? 0);
            },
            cancellationToken);

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_ownsDatabase)
        {
            await Database.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static void LogChoice(ILogger logger, IndexStorage storage, IndexStorageOptions options)
    {
        var status = SqliteVecExtension.Status;
        var sqliteVec = storage.SqliteVecLoaded
            ? $"sqlite-vec {status.LoadedVersion} loaded from {status.LibraryPath}"
            : options.LoadSqliteVec ? $"sqlite-vec unavailable: {status.Reason}" : "sqlite-vec not requested";
        var reason = storage.Vectors is AutoVectorIndex
            ? (storage.SqliteVecLoaded
                ? $"in-memory brute force while the vectors fit {options.VectorCacheMaxBytes / 1048576.0:0.#} MiB, then sqlite-vec; {sqliteVec}"
                : $"in-memory brute force while the vectors fit {options.VectorCacheMaxBytes / 1048576.0:0.#} MiB, then a table scan; {sqliteVec}")
            : sqliteVec;
        if (Interlocked.Exchange(ref _choiceLogged, 1) == 0)
        {
            LogBackend(logger, storage.Vectors.Backend, reason);
        }
        else
        {
            LogBackendAgain(logger, storage.Vectors.Backend, reason);
        }
    }

    [LoggerMessage(EventId = 2110, Level = LogLevel.Information, Message = "Index vector search uses the {Backend} backend ({Reason}).")]
    private static partial void LogBackend(ILogger logger, string backend, string reason);

    [LoggerMessage(EventId = 2111, Level = LogLevel.Debug, Message = "Index vector search uses the {Backend} backend ({Reason}).")]
    private static partial void LogBackendAgain(ILogger logger, string backend, string reason);

    [LoggerMessage(EventId = 2112, Level = LogLevel.Information, Message = "Rebuilt the sqlite-vec table of {Path} from its stored vectors.")]
    private static partial void LogRebuilt(ILogger logger, string path);
}
