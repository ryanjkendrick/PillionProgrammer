using Pillion.ContextEngine.Symbols;
using Pillion.LocalStore.Index;

namespace Pillion.Host.Context.Retrieval;

/// <summary>An open index the catalog hands to retrieval: its id, its repository root, its storage and, when the host keeps one, its symbol graph.</summary>
/// <param name="IndexId">The index id (32 hex digits).</param>
/// <param name="RootPath">The repository root (a full path).</param>
/// <param name="Storage">The open storage; owned by the catalog, which disposes it.</param>
/// <param name="Graph">The in-memory symbol graph, or <see langword="null"/> (retrieval then reads the stored symbols).</param>
internal sealed record CatalogIndex(string IndexId, string RootPath, IndexStorage Storage, SymbolGraph? Graph = null);

/// <summary>
/// Gives retrieval the open indexes of the current workspace: the <see cref="IndexStorage"/> (and, when there is one, the
/// <see cref="SymbolGraph"/>) per repository root. <see cref="ReadOnlyIndexCatalog"/> is the simple implementation that
/// opens index folders read-only; the indexing pipeline (work item 2.9) plugs in its live catalog of the indexes it owns
/// or reads, bounded by the open workspace's roots (issue #98): retrieval, the mentions and the repo map never read an
/// index of another folder.
/// </summary>
internal interface IIndexCatalog
{
    /// <summary>Gets the open indexes for some repository roots.</summary>
    /// <param name="roots">
    /// The roots (as the contract's <c>SearchRequest.Roots</c>); empty means the open workspace's roots, never every index on
    /// disk. A root outside the workspace gets nothing.
    /// </param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>One open index per root that has one; roots without an index are left out.</returns>
    Task<IReadOnlyList<CatalogIndex>> GetIndexesAsync(IReadOnlyList<string> roots, CancellationToken cancellationToken);
}
