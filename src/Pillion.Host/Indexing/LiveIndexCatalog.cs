using Pillion.Common.Settings;
using Pillion.Host.Context.Retrieval;
using Pillion.Host.WorkspaceQueries;
using Pillion.Indexing.Ownership;

namespace Pillion.Host.Indexing;

/// <summary>
/// The indexes the indexing pipeline has open, for retrieval (work item 2.11's <see cref="IIndexCatalog"/>) and for the
/// workspace queries' fallback (2.14's <see cref="ISymbolGraphProvider"/>). Searches use the same <c>IndexStorage</c> the
/// pipeline writes through (or reads, on a host that does not own the index), so no second read-only opener competes for a
/// root this host owns, and new chunks are searchable as soon as a batch is stored. The symbol graph is the one the pipeline
/// built at its last refresh; a reader has none (retrieval then reads the stored symbols). Indexes on this machine that the
/// pipeline does not have open (another repository, or before the extension reports the workspace) are served by the
/// read-only catalog of work item 2.11, which never opens a root the pipeline has open.
/// </summary>
internal sealed class LiveIndexCatalog : IIndexCatalog, ISymbolGraphProvider, IAsyncDisposable
{
    private readonly IndexingService _indexing;
    private readonly IFeatureFlags _features;
    private readonly IIndexCatalog? _fallback;

    /// <summary>Initializes a new instance of the <see cref="LiveIndexCatalog"/> class.</summary>
    /// <param name="indexing">The pipeline.</param>
    /// <param name="features">The feature flags (nothing is served while <c>indexing</c> or <c>localStore</c> is off).</param>
    /// <param name="fallback">Serves the indexes the pipeline does not have open; owned (disposed with this catalog). Optional.</param>
    public LiveIndexCatalog(IndexingService indexing, IFeatureFlags features, IIndexCatalog? fallback = null)
    {
        ArgumentNullException.ThrowIfNull(indexing);
        ArgumentNullException.ThrowIfNull(features);
        _indexing = indexing;
        _features = features;
        _fallback = fallback;

        // Asked for every index (no roots), the read-only catalog would otherwise open the roots the pipeline has open too
        // (and keep them open), only for this catalog to drop them: a second opener of the same database.
        if (fallback is ReadOnlyIndexCatalog readOnly)
        {
            readOnly.SkipRoot = root => _indexing.OpenRoots.Any(r => string.Equals(r.Store.Identity.NormalizedRoot, root, StringComparison.Ordinal));
        }
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<CatalogIndex>> GetIndexesAsync(IReadOnlyList<string> roots, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(roots);
        if (!_features.IsEnabled(FeatureNames.Indexing) || !_features.IsEnabled(FeatureNames.LocalStore))
        {
            return [];
        }

        var wanted = roots.Where(r => !string.IsNullOrWhiteSpace(r)).Select(IndexIdentity.NormalizeRoot).ToHashSet(StringComparer.Ordinal);
        var open = _indexing.OpenRoots;
        var result = open
            .Where(r => wanted.Count == 0 || wanted.Contains(r.Store.Identity.NormalizedRoot))
            .Select(r => new CatalogIndex(r.Store.Identity.Id, r.Path, r.Storage, r.Graph))
            .ToList();

        // The rest from disk, read-only: only roots the pipeline does not have open, so the same index is never opened twice.
        var live = open.Select(r => r.Store.Identity.NormalizedRoot).ToHashSet(StringComparer.Ordinal);
        var others = wanted.Where(r => !live.Contains(r)).ToList();
        if (_fallback is not null && (wanted.Count == 0 || others.Count > 0))
        {
            foreach (var index in await _fallback.GetIndexesAsync(others, cancellationToken).ConfigureAwait(false))
            {
                if (!live.Contains(IndexIdentity.NormalizeRoot(index.RootPath)))
                {
                    result.Add(index);
                }
            }
        }

        return [.. result.OrderBy(i => IndexIdentity.NormalizeRoot(i.RootPath), StringComparer.Ordinal)];
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_fallback is IAsyncDisposable disposable)
        {
            await disposable.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <inheritdoc/>
    public ValueTask<IReadOnlyList<IndexedSymbolGraph>> GetGraphsAsync(CancellationToken cancellationToken)
    {
        if (!_features.IsEnabled(FeatureNames.Indexing))
        {
            return ValueTask.FromResult<IReadOnlyList<IndexedSymbolGraph>>([]);
        }

        IReadOnlyList<IndexedSymbolGraph> graphs = [.. _indexing.OpenRoots
            .Where(r => r.Graph is not null)
            .Select(r => new IndexedSymbolGraph(r.Path, r.Graph!))];
        return ValueTask.FromResult(graphs);
    }
}
