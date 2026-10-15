using System.Threading;
using System.Threading.Tasks;

namespace Pillion.Contracts;

/// <summary>Language-service queries against the open solution, implemented by the VSIX and called by the host.</summary>
/// <remarks>
/// The VSIX answers from the live Roslyn workspace, off the UI thread, and enforces the budgets of design section 13: one
/// query in flight, 2 s per query, at most five a second. A query over the concurrency or rate limit fails fast with a
/// <see cref="PillionRpcException"/> (<see cref="PillionErrorCode.Cancelled"/>, with a retry hint) instead of queueing; one
/// that reaches 2 s is stopped (<see cref="CallersOfAsync"/> then returns what it found, marked truncated). The host falls
/// back to the index's approximate edges. Contract 1.3 added <see cref="CallersOfAsync"/>; a 1.2 VSIX answers
/// it with StreamJsonRpc's method-not-found error, which the host treats the same way.
/// </remarks>
public interface IWorkspaceQueryService
{
    /// <summary>Finds references to a symbol.</summary>
    /// <param name="request">The request.</param>
    /// <param name="cancellationToken">A token that cancels this call.</param>
    /// <returns>The references found.</returns>
    Task<FindReferencesResponse> FindReferencesAsync(FindReferencesRequest request, CancellationToken cancellationToken);

    /// <summary>Gets diagnostics for a file or the whole solution.</summary>
    /// <param name="request">The request.</param>
    /// <param name="cancellationToken">A token that cancels this call.</param>
    /// <returns>The diagnostics.</returns>
    Task<DiagnosticsResponse> GetDiagnosticsAsync(DiagnosticsRequest request, CancellationToken cancellationToken);

    /// <summary>Gets information about a symbol.</summary>
    /// <param name="request">The request.</param>
    /// <param name="cancellationToken">A token that cancels this call.</param>
    /// <returns>The symbol information.</returns>
    Task<SymbolResponse> GetSymbolAsync(SymbolRequest request, CancellationToken cancellationToken);

    /// <summary>Renames a symbol across the solution.</summary>
    /// <param name="request">The request.</param>
    /// <param name="cancellationToken">A token that cancels this call.</param>
    /// <returns>The result of the rename.</returns>
    Task<RenameSymbolResponse> RenameSymbolAsync(RenameSymbolRequest request, CancellationToken cancellationToken);

    /// <summary>Runs tests in the solution.</summary>
    /// <param name="request">The request.</param>
    /// <param name="cancellationToken">A token that cancels this call.</param>
    /// <returns>The outcome of the test run.</returns>
    Task<RunTestsResponse> RunTestsAsync(RunTestsRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Finds the callers of a method, constructor, property or other member (the agent and chat tool <c>callers_of</c>, and
    /// <c>@symbol</c>), from the live Roslyn workspace for C# and VB. Added in contract 1.3 (work item 2.14).
    /// </summary>
    /// <remarks>
    /// Budgets, enforced by the VSIX: one workspace query in flight (this and the other methods together), 2 s per query,
    /// at most five queries a second. A call over a limit fails at once with <see cref="PillionErrorCode.Cancelled"/>; a
    /// call that runs out of time returns what it found with <see cref="CallersOfResponse.Truncated"/> set. The host then
    /// falls back to, or completes the answer from, the index's approximate reference edges.
    /// </remarks>
    /// <param name="request">The symbol whose callers to find.</param>
    /// <param name="cancellationToken">A token that cancels this call.</param>
    /// <returns>The call sites found.</returns>
    Task<CallersOfResponse> CallersOfAsync(CallersOfRequest request, CancellationToken cancellationToken);
}
