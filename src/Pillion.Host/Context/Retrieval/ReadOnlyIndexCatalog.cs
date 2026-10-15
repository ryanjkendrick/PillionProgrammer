using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Pillion.Common;
using Pillion.Common.Settings;
using Pillion.Indexing.Ownership;
using Pillion.Indexing.Storage;
using Pillion.LocalStore;
using Pillion.LocalStore.Index;

namespace Pillion.Host.Context.Retrieval;

/// <summary>
/// The simple <see cref="IIndexCatalog"/>: finds index folders under <see cref="PillionPaths.IndexDirectory"/> by their
/// <c>state.json</c> and opens their databases read-only (the reader path of <see cref="IndexStoreFactory"/>: nothing is
/// created, migrated or locked, so it never competes with the index's owner). For a root with several indexes (another
/// embedding model or chunker version) the most recently opened one is used. Databases stay open until the catalog is
/// disposed, and each one's vector cache is warmed in the background when it opens. Serves nothing while
/// <c>features.indexing</c> or <c>features.localStore</c> is off.
/// </summary>
internal sealed partial class ReadOnlyIndexCatalog : IIndexCatalog, IAsyncDisposable
{
    private readonly PillionPaths _paths;
    private readonly IFeatureFlags _features;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, CatalogIndex> _open = new(StringComparer.Ordinal);
    private bool _disposed;

    /// <summary>Initializes a new instance of the <see cref="ReadOnlyIndexCatalog"/> class.</summary>
    /// <param name="paths">Where the index folders live.</param>
    /// <param name="features">The feature flags.</param>
    /// <param name="loggerFactory">The logger factory.</param>
    public ReadOnlyIndexCatalog(PillionPaths paths, IFeatureFlags features, ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(features);
        _paths = paths;
        _features = features;
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        _logger = _loggerFactory.CreateLogger<ReadOnlyIndexCatalog>();
    }

    /// <summary>Gets the number of index databases this catalog has open (tests check that a root the pipeline owns is never opened twice).</summary>
    internal int OpenCount
    {
        get
        {
            _gate.Wait();
            try
            {
                return _open.Count;
            }
            finally
            {
                _gate.Release();
            }
        }
    }

    /// <summary>
    /// Gets or sets a filter on normalised roots this catalog must not open: the roots the indexing pipeline has open, so the
    /// same database never gets a second, read-only opener (set by <see cref="Indexing.LiveIndexCatalog"/>). <see langword="null"/> skips none.
    /// </summary>
    internal Func<string, bool>? SkipRoot { get; set; }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<CatalogIndex>> GetIndexesAsync(IReadOnlyList<string> roots, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(roots);
        if (!_features.IsEnabled(FeatureNames.Indexing) || !_features.IsEnabled(FeatureNames.LocalStore) || !Directory.Exists(_paths.IndexDirectory))
        {
            return [];
        }

        var wanted = roots.Where(r => !string.IsNullOrWhiteSpace(r)).Select(IndexIdentity.NormalizeRoot).ToHashSet(StringComparer.Ordinal);
        var folders = FindFolders()
            .Where(f => wanted.Count == 0 || wanted.Contains(f.NormalizedRoot))
            .Where(f => SkipRoot?.Invoke(f.NormalizedRoot) != true)
            .GroupBy(f => f.NormalizedRoot, StringComparer.Ordinal)
            .Select(g => g.OrderByDescending(f => f.State.LastOpenedUtc).First())
            .OrderBy(f => f.NormalizedRoot, StringComparer.Ordinal)
            .ToList();

        var result = new List<CatalogIndex>(folders.Count);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            foreach (var folder in folders)
            {
                if (!_open.TryGetValue(folder.Folder.Id, out var index))
                {
                    index = await TryOpenAsync(folder, cancellationToken).ConfigureAwait(false);
                    if (index is null)
                    {
                        continue;
                    }

                    _open[folder.Folder.Id] = index;
                }

                result.Add(index);
            }
        }
        finally
        {
            _gate.Release();
        }

        return result;
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            foreach (var index in _open.Values)
            {
                await index.Storage.DisposeAsync().ConfigureAwait(false);
            }

            _open.Clear();
        }
        finally
        {
            _gate.Release();
        }
    }

    private IEnumerable<(IndexFolder Folder, IndexState State, string NormalizedRoot)> FindFolders()
    {
        IEnumerable<string> directories;
        try
        {
            directories = Directory.EnumerateDirectories(_paths.IndexDirectory).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogListFailed(_logger, ex.GetType().Name);
            yield break;
        }

        foreach (var directory in directories)
        {
            var folder = new IndexFolder(directory);
            if (!IndexIdPattern().IsMatch(folder.Id) || !File.Exists(folder.DatabaseFile) || !File.Exists(folder.KeyFile))
            {
                continue;
            }

            var state = IndexStateFile.Read(folder.StateFile);
            if (state is null || string.IsNullOrWhiteSpace(state.RootPath))
            {
                continue;
            }

            yield return (folder, state, IndexIdentity.NormalizeRoot(state.RootPath));
        }
    }

    private async Task<CatalogIndex?> TryOpenAsync((IndexFolder Folder, IndexState State, string NormalizedRoot) entry, CancellationToken cancellationToken)
    {
        EncryptedDatabase? database = null;
        try
        {
            database = await EncryptedDatabase.OpenAsync(
                new EncryptedDatabaseOptions { DatabasePath = entry.Folder.DatabaseFile, KeyFilePath = entry.Folder.KeyFile, ReadOnly = true },
                IndexSchema.Schema,
                _loggerFactory.CreateLogger<EncryptedDatabase>(),
                cancellationToken).ConfigureAwait(false);
            var storage = IndexStorage.Create(database, ownsDatabase: true, logger: _loggerFactory.CreateLogger<IndexStorage>());
            database = null;
            LogOpened(_logger, entry.Folder.Id, entry.State.RootPath);

            // Load the vectors into memory now, so the first search does not pay for it (about 0.8 s per 100,000 chunks).
            _ = Task.Run(() => WarmAsync(storage, entry.Folder.Id), CancellationToken.None);
            return new CatalogIndex(entry.Folder.Id, entry.State.RootPath, storage);
        }
        catch (Exception ex) when (ex is LocalStoreException or IOException or UnauthorizedAccessException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
        {
            LogOpenFailed(_logger, entry.Folder.Id, ex.GetType().Name, ex.Message);
            return null;
        }
        finally
        {
            if (database is not null)
            {
                await database.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task WarmAsync(IndexStorage storage, string indexId)
    {
        try
        {
            var bytes = await storage.WarmVectorCacheAsync().ConfigureAwait(false);
            LogWarmed(_logger, indexId, bytes / 1048576.0);
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException or LocalStoreException)
        {
            LogWarmFailed(_logger, indexId, ex);
        }
    }

    [GeneratedRegex("^[0-9a-f]{32}$", RegexOptions.CultureInvariant)]
    private static partial Regex IndexIdPattern();

    [LoggerMessage(EventId = 2920, Level = LogLevel.Information, Message = "Retrieval opened index {IndexId} for {Root} read-only.")]
    private static partial void LogOpened(ILogger logger, string indexId, string root);

    [LoggerMessage(EventId = 2921, Level = LogLevel.Warning, Message = "Retrieval could not open index {IndexId} ({ExceptionType}: {Reason}); it is skipped.")]
    private static partial void LogOpenFailed(ILogger logger, string indexId, string exceptionType, string reason);

    [LoggerMessage(EventId = 2922, Level = LogLevel.Warning, Message = "The index folders could not be listed ({ExceptionType}).")]
    private static partial void LogListFailed(ILogger logger, string exceptionType);

    [LoggerMessage(EventId = 2923, Level = LogLevel.Debug, Message = "Index {IndexId}: vector cache warmed ({Megabytes:F1} MB).")]
    private static partial void LogWarmed(ILogger logger, string indexId, double megabytes);

    [LoggerMessage(EventId = 2924, Level = LogLevel.Debug, Message = "Index {IndexId}: the vector cache could not be warmed.")]
    private static partial void LogWarmFailed(ILogger logger, string indexId, Exception exception);
}
