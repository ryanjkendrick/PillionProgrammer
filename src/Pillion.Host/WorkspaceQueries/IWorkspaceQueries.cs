using Pillion.ContextEngine.Symbols;

namespace Pillion.Host.WorkspaceQueries;

/// <summary>
/// <c>find_references</c> and <c>callers_of</c> for the host's callers: <c>@symbol</c> (work item 2.13) and chat's
/// architecture questions (design section 7). C# and Visual Basic are answered by the live Roslyn workspace in Visual
/// Studio through <see cref="Contracts.IWorkspaceQueryService"/>; when Visual Studio refuses (busy, over its rate, the flag
/// off), runs out of its 2 s budget, is not connected, or the language is another one, the index's approximate reference
/// edges answer instead, and every such site is marked approximate.
/// </summary>
internal interface IWorkspaceQueries
{
    /// <summary>Finds the references to a symbol.</summary>
    /// <param name="query">The symbol.</param>
    /// <param name="cancellationToken">A token that cancels the query.</param>
    /// <returns>The references; never throws for a refusal or a timeout (only for cancellation).</returns>
    Task<WorkspaceQueryResult> FindReferencesAsync(WorkspaceSymbolQuery query, CancellationToken cancellationToken);

    /// <summary>Finds the call sites of a member (or the constructions of a type).</summary>
    /// <param name="query">The member.</param>
    /// <param name="cancellationToken">A token that cancels the query.</param>
    /// <returns>The call sites; never throws for a refusal or a timeout (only for cancellation).</returns>
    Task<WorkspaceQueryResult> CallersOfAsync(WorkspaceSymbolQuery query, CancellationToken cancellationToken);
}

/// <summary>Names a symbol for <see cref="IWorkspaceQueries"/>.</summary>
internal sealed record WorkspaceSymbolQuery
{
    /// <summary>Gets the symbol's simple name, for example <c>PlaceOrderAsync</c>.</summary>
    public required string Name { get; init; }

    /// <summary>
    /// Gets the containing type or namespace as far as known: dotted (<c>Shop.Orders.OrderService</c>), an index breadcrumb
    /// (<c>Shop.Orders &gt; OrderService</c>) or just the type (<c>OrderService</c>); empty when unknown.
    /// </summary>
    public string Container { get; init; } = string.Empty;

    /// <summary>Gets the full path of a file that declares (or uses) the symbol, or empty when unknown.</summary>
    public string FilePath { get; init; } = string.Empty;

    /// <summary>Gets the zero-based line of the symbol in <see cref="FilePath"/>, or -1.</summary>
    public int Line { get; init; } = -1;

    /// <summary>Gets the zero-based column of the symbol on <see cref="Line"/>, or -1 to find the name on the line.</summary>
    public int Column { get; init; } = -1;

    /// <summary>Gets the most sites to return; 0 means 100.</summary>
    public int MaxResults { get; init; }
}

/// <summary>Where a <see cref="WorkspaceQueryResult"/> came from.</summary>
internal enum WorkspaceQuerySource
{
    /// <summary>Nothing answered: the symbol is unknown to both Visual Studio and the index.</summary>
    None,

    /// <summary>The live Roslyn workspace in Visual Studio (possibly completed from the index; see each site).</summary>
    Roslyn,

    /// <summary>The index's approximate, name-resolved reference edges.</summary>
    Index,
}

/// <summary>One reference or call site.</summary>
/// <param name="FilePath">The full path of the file.</param>
/// <param name="Line">The zero-based line.</param>
/// <param name="Snippet">The line's text, trimmed (empty when the file could not be read).</param>
/// <param name="CallerName">The qualified name of the member the site is in, or empty (file scope, or not known).</param>
/// <param name="IsApproximate">True when the site was matched by name (the index) rather than bound by the compiler.</param>
internal sealed record WorkspaceSite(string FilePath, int Line, string Snippet, string CallerName, bool IsApproximate);

/// <summary>The answer of <see cref="IWorkspaceQueries"/>.</summary>
/// <param name="IsFound">True when the symbol was found (by Roslyn or in the index).</param>
/// <param name="Sites">The sites, ordered by file and line.</param>
/// <param name="Truncated">True when more sites may exist: the result cap, or Roslyn's 2 s budget, cut the answer short.</param>
/// <param name="Source">Who answered.</param>
/// <param name="FallbackReason">Why Roslyn did not answer (alone), or <see langword="null"/> when it did.</param>
/// <param name="Elapsed">How long the query took.</param>
internal sealed record WorkspaceQueryResult(
    bool IsFound,
    IReadOnlyList<WorkspaceSite> Sites,
    bool Truncated,
    WorkspaceQuerySource Source,
    string? FallbackReason,
    TimeSpan Elapsed)
{
    /// <summary>Gets a value indicating whether any site is approximate (the answer, or part of it, came from the index).</summary>
    public bool IsApproximate => Source == WorkspaceQuerySource.Index || Sites.Any(s => s.IsApproximate);
}

/// <summary>
/// The index's symbol graphs, one per indexed root, for the workspace queries' fallback. The indexing pipeline (work item
/// 2.9) and the retrieval catalog (2.11) supply them; until then <see cref="EmptySymbolGraphProvider"/> answers with none,
/// so the fallback finds nothing and says so.
/// </summary>
internal interface ISymbolGraphProvider
{
    /// <summary>Returns the graphs of the indexed roots that are ready; empty when nothing is indexed.</summary>
    /// <param name="cancellationToken">A token that cancels the call.</param>
    /// <returns>The graphs.</returns>
    ValueTask<IReadOnlyList<IndexedSymbolGraph>> GetGraphsAsync(CancellationToken cancellationToken);
}

/// <summary>One indexed root's symbol graph.</summary>
/// <param name="RootPath">The root's full path; relative paths in the graph are relative to it.</param>
/// <param name="Graph">The graph.</param>
internal sealed record IndexedSymbolGraph(string RootPath, SymbolGraph Graph);

/// <summary>The <see cref="ISymbolGraphProvider"/> until the index supplies graphs: none.</summary>
internal sealed class EmptySymbolGraphProvider : ISymbolGraphProvider
{
    /// <inheritdoc/>
    public ValueTask<IReadOnlyList<IndexedSymbolGraph>> GetGraphsAsync(CancellationToken cancellationToken) => ValueTask.FromResult<IReadOnlyList<IndexedSymbolGraph>>([]);
}
