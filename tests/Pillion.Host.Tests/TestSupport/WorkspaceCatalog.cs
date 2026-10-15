using Pillion.Host.Context.Retrieval;
using Pillion.Indexing.Ownership;

namespace Pillion.Host.Tests.TestSupport;

/// <summary>
/// The live catalog's workspace rule (issue #98) over a standalone catalog, for tests that read an index folder without the
/// indexing pipeline: a request without roots means the given workspace roots, and a root outside them gets nothing.
/// </summary>
/// <param name="inner">The catalog that opens the indexes (asked only for workspace roots, by name).</param>
/// <param name="workspaceRoots">The roots of the open workspace.</param>
internal sealed class WorkspaceCatalog(IIndexCatalog inner, params string[] workspaceRoots) : IIndexCatalog
{
    public Task<IReadOnlyList<CatalogIndex>> GetIndexesAsync(IReadOnlyList<string> roots, CancellationToken cancellationToken)
    {
        var workspace = workspaceRoots.Select(IndexIdentity.NormalizeRoot).ToHashSet(StringComparer.Ordinal);
        var requested = roots.Where(r => !string.IsNullOrWhiteSpace(r)).Select(IndexIdentity.NormalizeRoot).ToList();
        List<string> wanted = requested.Count == 0 ? [.. workspace] : [.. requested.Where(workspace.Contains)];
        return wanted.Count == 0 ? Task.FromResult<IReadOnlyList<CatalogIndex>>([]) : inner.GetIndexesAsync(wanted, cancellationToken);
    }
}
