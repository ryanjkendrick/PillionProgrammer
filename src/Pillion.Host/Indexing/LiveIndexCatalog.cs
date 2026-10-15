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
/// built at its last refresh; a reader has none (retrieval then reads the stored symbols).
/// </summary>
/// <remarks>
/// Only the indexes of the open workspace's roots are served (<see cref="IndexingService.WorkspaceRoots"/>; design sections
/// 7 and 12, issue #98): a request without roots means the workspace's roots, a request naming roots gets those of them
/// that belong to the workspace, and with no solution or folder open nothing is served. A workspace root whose index the
/// pipeline has not opened (yet) is served by the read-only catalog of work item 2.11, asked for that root by name; other
/// indexes on this machine are visible only in the Index window's Indexes tab.
/// </remarks>
internal sealed class LiveIndexCatalog : IIndexCatalog, ISymbolGraphProvider, IAsyncDisposable
{
    private readonly IndexingService _indexing;
    private readonly IFeatureFlags _features;
    private readonly IIndexCatalog? _fallback;

    /// <summary>Initializes a new instance of the <see cref="LiveIndexCatalog"/> class.</summary>
    /// <param name="indexing">The pipeline.</param>
    /// <param name="features">The feature flags (nothing is served while <c>indexing</c> or <c>localStore</c> is off).</param>
    /// <param name="fallback">Serves the workspace roots' indexes the pipeline does not have open, asked for by root; owned (disposed with this catalog). Optional.</param>
    public LiveIndexCatalog(IndexingService indexing, IFeatureFlags features, IIndexCatalog? fallback = null)
    {
        ArgumentNullException.ThrowIfNull(indexing);
        ArgumentNullException.ThrowIfNull(features);
        _indexing = indexing;
        _features = features;
        _fallback = fallback;

        // A root the pipeline opens between this catalog's check and the read-only open would otherwise get a second opener
        // of the same database (kept open by the read-only catalog).
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

        // The open workspace's roots bound everything: no workspace, no indexes.
        var workspace = _indexing.WorkspaceRoots.Select(IndexIdentity.NormalizeRoot).ToHashSet(StringComparer.Ordinal);
        if (workspace.Count == 0)
        {
            return [];
        }

        var requested = roots.Where(r => !string.IsNullOrWhiteSpace(r)).Select(IndexIdentity.NormalizeRoot).ToHashSet(StringComparer.Ordinal);
        var wanted = requested.Count == 0 ? workspace : requested.Where(workspace.Contains).ToHashSet(StringComparer.Ordinal);
        if (wanted.Count == 0)
        {
            return [];
        }

        var open = _indexing.OpenRoots;
        var result = open
            .Where(r => wanted.Contains(r.Store.Identity.NormalizedRoot))
            .Select(r => new CatalogIndex(r.Store.Identity.Id, r.Path, r.Storage, r.Graph))
            .ToList();

        // A workspace root the pipeline has not opened (yet) from disk, read-only and by name, so the same index is never
        // opened twice and no other root is ever asked for.
        var live = open.Select(r => r.Store.Identity.NormalizedRoot).ToHashSet(StringComparer.Ordinal);
        var others = wanted.Where(r => !live.Contains(r)).ToList();
        if (_fallback is not null && others.Count > 0)
        {
            foreach (var index in await _fallback.GetIndexesAsync(others, cancellationToken).ConfigureAwait(false))
            {
                var root = IndexIdentity.NormalizeRoot(index.RootPath);
                if (!live.Contains(root) && wanted.Contains(root))
                {
                    result.Add(index);
                }
            }
        }

        return [.. result.OrderBy(i => IndexIdentity.NormalizeRoot(i.RootPath), StringComparer.Ordinal)];
    }

    /// <inheritdoc/>
    public ValueTask<IReadOnlyList<IndexedSymbolGraph>> GetGraphsAsync(CancellationToken cancellationToken)
    {
        if (!_features.IsEnabled(FeatureNames.Indexing))
        {
            return ValueTask.FromResult<IReadOnlyList<IndexedSymbolGraph>>([]);
        }

        var workspace = _indexing.WorkspaceRoots.Select(IndexIdentity.NormalizeRoot).ToHashSet(StringComparer.Ordinal);
        IReadOnlyList<IndexedSymbolGraph> graphs = [.. _indexing.OpenRoots
            .Where(r => r.Graph is not null && workspace.Contains(r.Store.Identity.NormalizedRoot))
            .Select(r => new IndexedSymbolGraph(r.Path, r.Graph!))];
        return ValueTask.FromResult(graphs);
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_fallback is IAsyncDisposable disposable)
        {
            await disposable.DisposeAsync().ConfigureAwait(false);
        }
    }
}
