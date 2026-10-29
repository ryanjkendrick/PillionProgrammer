using System.Threading;
using System.Threading.Tasks;

namespace Pillion.Contracts;

/// <summary>
/// Workspace trust (design sections 12 and 14), implemented by the VSIX and called by the host: before the host uses a
/// repository's <c>.pillion/rules.md</c>, <c>prompts\*.md</c> or (phase 4) <c>mcp.json</c>, the user sees the folder's contents
/// and their hash and decides. A decision is pinned to the content hash in the host's <c>trust.json</c> and asked again when
/// the content changes; <c>policy.json</c> is applied whether or not the folder is trusted, since it can only restrict. Added in
/// contract 1.4 (work item 3.5; called by 3.7, shown by 3.14). A 1.3 VSIX answers with StreamJsonRpc's method-not-found error,
/// which the host treats as not trusted.
/// </summary>
public interface ITrustService
{
    /// <summary>Shows the <c>.pillion</c> folder's contents and hash and waits for the user's decision.</summary>
    /// <param name="request">The folder, its files and its hash.</param>
    /// <param name="cancellationToken">A token that cancels this call (the host gives up waiting when the workspace closes).</param>
    /// <returns>The decision.</returns>
    Task<TrustDecision> RequestTrustAsync(TrustRequest request, CancellationToken cancellationToken);
}
