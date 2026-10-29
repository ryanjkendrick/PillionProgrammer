using System.Threading;
using System.Threading.Tasks;

namespace Pillion.Contracts;

/// <summary>User approvals, implemented by the VSIX and called by the host.</summary>
/// <remarks>
/// Contract 1.0. From contract 1.4 the Agent Runtime does not call it: approvals are session state
/// (<see cref="AgentSessionState.PendingApproval"/>) answered through <see cref="IAgentService"/>, so an answer survives a
/// restart of either process (decision of 2026-10-24 in <c>docs/phase-3-plan.md</c>). The method stays on the contract for
/// other callers.
/// </remarks>
public interface IApprovalService
{
    /// <summary>Asks the user to approve a tool call and waits for the answer.</summary>
    /// <param name="request">The approval request.</param>
    /// <param name="cancellationToken">A token that cancels this call.</param>
    /// <returns>The user's decision.</returns>
    Task<ApprovalDecision> RequestApprovalAsync(ApprovalRequest request, CancellationToken cancellationToken);
}
