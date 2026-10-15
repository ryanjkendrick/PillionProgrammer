using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Pillion.LocalStore.Index;

/// <summary>Which <see cref="IVectorIndex"/> backend an <see cref="IndexStorage"/> uses.</summary>
public enum VectorBackendPreference
{
    /// <summary>
    /// <see cref="AutoVectorIndex"/>, the default: brute force over the in-memory copy of the vectors while they fit
    /// <see cref="IndexStorageOptions.VectorCacheMaxBytes"/>, then the table scan.
    /// </summary>
    InMemory,

    /// <summary><see cref="TableScanVectorIndex"/>: scan <c>chunk_embeddings</c> on every search and keep no copy.</summary>
    TableScan,
}

/// <summary>How an <see cref="IndexStorage"/> searches.</summary>
public sealed class IndexStorageOptions
{
    /// <summary>Gets the vector backend. Defaults to <see cref="VectorBackendPreference.InMemory"/>.</summary>
    public VectorBackendPreference VectorBackend { get; init; } = VectorBackendPreference.InMemory;

    /// <summary>
    /// Gets the largest in-memory copy of the int8 vectors the default backend keeps, in bytes (about the chunk count times
    /// the dimension). Defaults to 64 MiB, enough for 170,000 chunks of 384 dimensions; beyond it, and at 0, searches scan
    /// the table.
    /// </summary>
    public long VectorCacheMaxBytes { get; init; } = 64L * 1024 * 1024;

    /// <summary>Gets the BM25 weight of the <c>text</c> column (breadcrumb and text, whole identifiers). Defaults to 1.0.</summary>
    public double TextWeight { get; init; } = 1.0;

    /// <summary>Gets the BM25 weight of the <c>identifiers</c> column (split identifiers). Defaults to 0.5, so a whole-identifier match ranks above a split one.</summary>
    public double IdentifiersWeight { get; init; } = 0.5;
}

/// <summary>
/// The lexical, vector and symbol storage of one index database (work item 2.8): opens the encrypted database (or takes
/// one already open), chooses the vector backend and logs the choice once per process, and hands out the repositories.
/// </summary>
/// <remarks>
/// Vector search runs in managed code over the int8 vectors in <c>chunk_embeddings</c>. An HNSW or sqlite-vec backend can
/// be added behind <see cref="IVectorIndex"/> for indexes beyond the in-memory cap (see the README).
/// </remarks>
public sealed partial class IndexStorage : IAsyncDisposable
{
    private static int _choiceLogged;
    private readonly bool _ownsDatabase;

    private IndexStorage(EncryptedDatabase database, bool ownsDatabase, IndexStorageOptions options)
    {
        Database = database;
        _ownsDatabase = ownsDatabase;
        Options = options;
        var vectors = new VectorStorage(database, options.VectorCacheMaxBytes);
        Storage = vectors;
        Chunks = new ChunkRepository(vectors, options.TextWeight, options.IdentifiersWeight);
        Symbols = new SymbolRepository(database);
        TableScan = new TableScanVectorIndex(vectors);
        Vectors = options.VectorBackend == VectorBackendPreference.TableScan ? TableScan : new AutoVectorIndex(vectors);
    }

    /// <summary>Gets the database; repository discovery (work item 2.3) shares it for <c>files</c> and <c>meta</c>.</summary>
    public EncryptedDatabase Database { get; }

    /// <summary>Gets the options.</summary>
    public IndexStorageOptions Options { get; }

    /// <summary>Gets the chosen vector backend.</summary>
    public IVectorIndex Vectors { get; }

    /// <summary>Gets the table-scan backend (always available; tests and the Index window can compare it with the default).</summary>
    public TableScanVectorIndex TableScan { get; }

    /// <summary>Gets the chunks, their FTS5 rows and lexical search.</summary>
    public IChunkRepository Chunks { get; }

    /// <summary>Gets the symbols and reference edges.</summary>
    public ISymbolRepository Symbols { get; }

    /// <summary>Gets the memory held by the in-memory copy of the vectors, in bytes (0 before the first search).</summary>
    public long VectorCacheBytes => Storage.CacheBytes;

    internal VectorStorage Storage { get; }

    /// <summary>Opens (or creates and migrates) an index database and its storage.</summary>
    /// <param name="databaseOptions">Where the database and its key live.</param>
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
        var database = await EncryptedDatabase.OpenAsync(databaseOptions, schema, logger, cancellationToken).ConfigureAwait(false);
        try
        {
            return CreateCore(database, ownsDatabase: true, options, logger);
        }
        catch
        {
            await database.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Creates the storage over an index database that is already open.</summary>
    /// <param name="database">The database, at index schema version 2 or later.</param>
    /// <param name="ownsDatabase">Whether disposing the storage disposes the database.</param>
    /// <param name="options">How to search, or <see langword="null"/> for the defaults.</param>
    /// <param name="logger">The logger.</param>
    /// <returns>The storage.</returns>
    /// <exception cref="InvalidOperationException">The database is older than index schema version 2.</exception>
    public static IndexStorage Create(EncryptedDatabase database, bool ownsDatabase, IndexStorageOptions? options = null, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(database);
        return CreateCore(database, ownsDatabase, options, logger);
    }

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

    private static IndexStorage CreateCore(EncryptedDatabase database, bool ownsDatabase, IndexStorageOptions? options, ILogger? logger)
    {
        options ??= new IndexStorageOptions();
        logger ??= NullLogger.Instance;
        if (database.SchemaVersion < IndexStorageSchema.Version)
        {
            throw new InvalidOperationException($"The index database is at schema version {database.SchemaVersion}; index storage needs version {IndexStorageSchema.Version} or later.");
        }

        var storage = new IndexStorage(database, ownsDatabase, options);
        var reason = storage.Vectors is AutoVectorIndex
            ? $"brute force over an in-memory copy of the vectors while they fit {options.VectorCacheMaxBytes / 1048576.0:0.#} MiB, then a table scan"
            : "brute force over the table on every search";
        if (Interlocked.Exchange(ref _choiceLogged, 1) == 0)
        {
            LogBackend(logger, storage.Vectors.Backend, reason);
        }
        else
        {
            LogBackendAgain(logger, storage.Vectors.Backend, reason);
        }

        return storage;
    }

    [LoggerMessage(EventId = 2110, Level = LogLevel.Information, Message = "Index vector search uses the {Backend} backend ({Reason}).")]
    private static partial void LogBackend(ILogger logger, string backend, string reason);

    [LoggerMessage(EventId = 2111, Level = LogLevel.Debug, Message = "Index vector search uses the {Backend} backend ({Reason}).")]
    private static partial void LogBackendAgain(ILogger logger, string backend, string reason);
}
