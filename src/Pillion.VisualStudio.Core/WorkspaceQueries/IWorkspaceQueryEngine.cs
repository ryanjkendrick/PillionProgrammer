using System.Threading;
using System.Threading.Tasks;
using Pillion.Contracts;

namespace Pillion.VisualStudio.Core.WorkspaceQueries;

/// <summary>
/// The language-service half of the workspace queries: resolves a symbol in the live Roslyn workspace and finds its
/// references or callers. The VSIX implements it over Visual Studio's <c>VisualStudioWorkspace</c>; tests use fakes.
/// <see cref="WorkspaceQueryService"/> owns the budgets (one in flight, 2 s, five a second) and calls the engine on the
/// thread pool, never on the UI thread.
/// </summary>
/// <remarks>
/// An engine adds each result to the collector as soon as it has it, so a query the budget stops still returns what it
/// found; it calls <see cref="WorkspaceQueryResults{T}.MarkFound"/> once the symbol is resolved and stops when
/// <see cref="WorkspaceQueryResults{T}.TryAdd"/> returns <see langword="false"/> or the token is cancelled. It never
/// switches to the UI thread. A workspace that is not available yet is reported by throwing a
/// <see cref="PillionRpcException"/> with <see cref="PillionErrorCode.Cancelled"/> and a retry hint.
/// </remarks>
public interface IWorkspaceQueryEngine
{
    /// <summary>Finds the references to the symbol at a location.</summary>
    /// <param name="location">The file, zero-based line and zero-based column of the symbol (a use or its declaration).</param>
    /// <param name="results">Receives the references (file, zero-based line and column).</param>
    /// <param name="cancellationToken">Cancelled when the budget runs out or the caller gives up.</param>
    /// <returns>A task that completes when the search is over.</returns>
    Task FindReferencesAsync(SourceLocation location, WorkspaceQueryResults<SourceLocation> results, CancellationToken cancellationToken);

    /// <summary>Finds the call sites of a member.</summary>
    /// <param name="request">The member: name, container, and the file and line of a declaration when known.</param>
    /// <param name="results">Receives the call sites (not approximate).</param>
    /// <param name="cancellationToken">Cancelled when the budget runs out or the caller gives up.</param>
    /// <returns>A task that completes when the search is over.</returns>
    Task CallersOfAsync(CallersOfRequest request, WorkspaceQueryResults<CallSite> results, CancellationToken cancellationToken);
}
