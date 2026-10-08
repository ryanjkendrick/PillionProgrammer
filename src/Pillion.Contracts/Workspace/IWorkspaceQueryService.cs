using System.Threading;
using System.Threading.Tasks;

namespace Pillion.Contracts;

/// <summary>Language-service queries against the open solution, implemented by the VSIX and called by the host.</summary>
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
}
