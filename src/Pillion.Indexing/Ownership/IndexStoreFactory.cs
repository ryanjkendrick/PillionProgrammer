using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Pillion.Common;
using Pillion.Common.Settings;
using Pillion.Indexing.Storage;
using Pillion.LocalStore;

namespace Pillion.Indexing.Ownership;

/// <summary>Which index to open: the three parts of its identity, and this host's pipe name for <c>writer.lock</c>.</summary>
/// <param name="RootPath">The repository root folder.</param>
/// <param name="EmbeddingModelId">The embedding model id.</param>
/// <param name="ChunkerVersion">The chunker version.</param>
/// <param name="HostPipeName">The pipe name of this host's RPC server; recorded in <c>writer.lock</c> if this host becomes the owner.</param>
public sealed record IndexRequest(string RootPath, string EmbeddingModelId, string ChunkerVersion, string HostPipeName);

/// <summary>Tuning for opening an index as a reader; tests shorten it.</summary>
public sealed class IndexStoreOptions
{
    /// <summary>Gets how many times a reader retries while the owner has not finished creating or migrating the database. Defaults to 20.</summary>
    public int ReaderOpenAttempts { get; init; } = 20;

    /// <summary>Gets the pause between those retries. Defaults to 250 ms (about 5 seconds in all).</summary>
    public TimeSpan ReaderOpenDelay { get; init; } = TimeSpan.FromMilliseconds(250);
}

/// <summary>
/// Opens an index (design section 10, Index identity and ownership). The first host to reach an index takes
/// <c>writer.lock</c> and opens the database read-write, creating or migrating it; every other host opens it read-only
/// and learns the owner's pipe name. A lock whose process is gone, or whose process id belongs to a different process
/// now, is reclaimed, so a crashed host never blocks the index. Both <c>features.indexing</c> and
/// <c>features.localStore</c> must be on.
/// </summary>
public sealed partial class IndexStoreFactory
{
    /// <summary>The message of the error returned while the <see cref="FeatureNames.Indexing"/> feature is off.</summary>
    public const string IndexingDisabledMessage =
        "Pillion's indexing is turned off by settings or policy (feature indexing), so no index is opened. "
        + "Set features.indexing to true in settings.json, or remove it from disabledFeatures in the repository policy.";

    private const string LocalStoreDisabledMessage =
        "Pillion's Local Store is turned off by settings or policy (feature localStore), so no index is opened. "
        + "Set features.localStore to true in settings.json, or remove it from disabledFeatures in the repository policy.";

    private readonly PillionPaths _paths;
    private readonly IFeatureFlags _features;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly IProcessProbe _probe;
    private readonly IndexStoreOptions _options;

    /// <summary>Initializes a new instance of the <see cref="IndexStoreFactory"/> class.</summary>
    /// <param name="paths">The data layout (indexes live under <see cref="PillionPaths.IndexDirectory"/>).</param>
    /// <param name="features">The feature flags.</param>
    /// <param name="loggerFactory">The logger factory.</param>
    /// <param name="time">The clock.</param>
    /// <param name="probe">Decides whether a lock's process is alive.</param>
    /// <param name="options">Reader retry tuning.</param>
    public IndexStoreFactory(
        PillionPaths paths,
        IFeatureFlags features,
        ILoggerFactory? loggerFactory = null,
        TimeProvider? time = null,
        IProcessProbe? probe = null,
        IndexStoreOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(features);
        _paths = paths;
        _features = features;
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        _logger = _loggerFactory.CreateLogger<IndexStoreFactory>();
        _time = time ?? TimeProvider.System;
        _probe = probe ?? ProcessProbe.Instance;
        _options = options ?? new IndexStoreOptions();
    }

    /// <summary>
    /// Opens the index for a request: as its owner if no live host holds it, otherwise read-only. The folder, key and
    /// database are created by the owner only; a reader never creates anything.
    /// </summary>
    /// <param name="request">The index to open.</param>
    /// <param name="cancellationToken">Cancels the open.</param>
    /// <returns>The open index; dispose it when done.</returns>
    /// <exception cref="LocalStoreException">A feature is off, or the database cannot be opened (see <see cref="LocalStoreException.Kind"/>).</exception>
    /// <exception cref="DirectoryNotFoundException">The repository root does not exist.</exception>
    public async Task<IndexStore> OpenAsync(IndexRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!_features.IsEnabled(FeatureNames.Indexing))
        {
            throw new LocalStoreException(LocalStoreErrorKind.FeatureDisabled, IndexingDisabledMessage, "Turn the indexing feature back on to index repositories.");
        }

        if (!_features.IsEnabled(FeatureNames.LocalStore))
        {
            throw new LocalStoreException(LocalStoreErrorKind.FeatureDisabled, LocalStoreDisabledMessage, "Turn the localStore feature back on to index repositories.");
        }

        var identity = new IndexIdentity(request.RootPath, request.EmbeddingModelId, request.ChunkerVersion);
        if (!Directory.Exists(identity.RootPath))
        {
            throw new DirectoryNotFoundException($"The repository root '{identity.RootPath}' does not exist.");
        }

        var folder = IndexFolder.For(_paths, identity);
        _paths.EnsureCreated();
        Directory.CreateDirectory(folder.Directory);

        Exception? last = null;
        for (var attempt = 0; attempt < Math.Max(1, _options.ReaderOpenAttempts); attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Each round tries the election again, so a writer that died while a reader waited is replaced.
            var election = WriterLock.TryAcquire(folder.LockFile, request.HostPipeName, _probe, _time);
            if (election.Lock is { } held)
            {
                return await OpenAsWriterAsync(identity, folder, held, request.HostPipeName, cancellationToken).ConfigureAwait(false);
            }

            var owner = election.Owner!;
            try
            {
                return await OpenAsReaderAsync(identity, folder, owner, cancellationToken).ConfigureAwait(false);
            }
            catch (LocalStoreException ex) when (ex.Kind is LocalStoreErrorKind.DatabaseMissing or LocalStoreErrorKind.NeedsMigration or LocalStoreErrorKind.KeyMissing)
            {
                last = ex;
            }
            catch (SqliteException ex)
            {
                last = ex;
            }

            LogWaitingForWriter(_logger, folder.Id, owner.ProcessId);
            await Task.Delay(_options.ReaderOpenDelay, _time, cancellationToken).ConfigureAwait(false);
        }

        throw last ?? new IOException("The index could not be opened.");
    }

    private async Task<IndexStore> OpenAsWriterAsync(IndexIdentity identity, IndexFolder folder, WriterLock held, string pipeName, CancellationToken cancellationToken)
    {
        EncryptedDatabase? database = null;
        try
        {
            database = await EncryptedDatabase.OpenAsync(
                new EncryptedDatabaseOptions { DatabasePath = folder.DatabaseFile, KeyFilePath = folder.KeyFile },
                IndexSchema.Schema,
                _loggerFactory.CreateLogger<EncryptedDatabase>(),
                cancellationToken).ConfigureAwait(false);
            await WriteIdentityAsync(database, identity, cancellationToken).ConfigureAwait(false);

            var now = _time.GetUtcNow();
            var previous = IndexStateFile.Read(folder.StateFile);
            var state = new IndexState(
                IndexState.CurrentVersion,
                identity.RootPath,
                identity.EmbeddingModelId,
                identity.ChunkerVersion,
                database.SchemaVersion,
                previous?.CreatedUtc ?? now,
                now,
                previous?.LastFullSweepUtc);
            IndexStateFile.Write(folder.StateFile, state);

            var self = new IndexWriterEndpoint(pipeName, held.Info.ProcessId, held.Info.ProcessStartUtc, IsSelf: true);
            LogOpenedAsWriter(_logger, folder.Id, identity.RootPath);
            return new IndexStore(identity, folder, database, new FilesRepository(database, _time), held, self, state, _probe, _time);
        }
        catch
        {
            if (database is not null)
            {
                await database.DisposeAsync().ConfigureAwait(false);
            }

            held.Dispose();
            throw;
        }
    }

    private async Task<IndexStore> OpenAsReaderAsync(IndexIdentity identity, IndexFolder folder, WriterLockInfo owner, CancellationToken cancellationToken)
    {
        var database = await EncryptedDatabase.OpenAsync(
            new EncryptedDatabaseOptions { DatabasePath = folder.DatabaseFile, KeyFilePath = folder.KeyFile, ReadOnly = true },
            IndexSchema.Schema,
            _loggerFactory.CreateLogger<EncryptedDatabase>(),
            cancellationToken).ConfigureAwait(false);

        var state = IndexStateFile.Read(folder.StateFile)
            ?? new IndexState(IndexState.CurrentVersion, identity.RootPath, identity.EmbeddingModelId, identity.ChunkerVersion, database.SchemaVersion, _time.GetUtcNow(), _time.GetUtcNow(), null);
        var endpoint = new IndexWriterEndpoint(owner.PipeName, owner.ProcessId, owner.ProcessStartUtc, owner.ProcessId == _probe.CurrentProcessId);
        LogOpenedAsReader(_logger, folder.Id, owner.ProcessId, owner.PipeName);
        return new IndexStore(identity, folder, database, new FilesRepository(database, _time), null, endpoint, state, _probe, _time);
    }

    private static Task WriteIdentityAsync(EncryptedDatabase database, IndexIdentity identity, CancellationToken cancellationToken) =>
        database.WriteAsync(
            (connection, transaction, token) =>
            {
                foreach (var (key, value) in new[]
                {
                    (IndexMetaKeys.EmbeddingModel, identity.EmbeddingModelId),
                    (IndexMetaKeys.ChunkerVersion, identity.ChunkerVersion),
                    (IndexMetaKeys.RootPath, identity.RootPath),
                })
                {
                    using var command = connection.CreateCommand();
                    command.Transaction = transaction;
                    command.CommandText = "INSERT INTO meta (key, value) VALUES ($key, $value) ON CONFLICT (key) DO UPDATE SET value = excluded.value";
                    command.Parameters.AddWithValue("$key", key);
                    command.Parameters.AddWithValue("$value", value);
                    command.ExecuteNonQuery();
                }

                return Task.CompletedTask;
            },
            cancellationToken);

    [LoggerMessage(EventId = 2300, Level = LogLevel.Information, Message = "Opened index {IndexId} for {Root} as the writer.")]
    private static partial void LogOpenedAsWriter(ILogger logger, string indexId, string root);

    [LoggerMessage(EventId = 2301, Level = LogLevel.Information, Message = "Opened index {IndexId} read-only; process {OwnerProcessId} owns it (pipe {PipeName}).")]
    private static partial void LogOpenedAsReader(ILogger logger, string indexId, int ownerProcessId, string pipeName);

    [LoggerMessage(EventId = 2302, Level = LogLevel.Debug, Message = "Index {IndexId} is not ready yet; waiting for its owner, process {OwnerProcessId}.")]
    private static partial void LogWaitingForWriter(ILogger logger, string indexId, int ownerProcessId);
}
