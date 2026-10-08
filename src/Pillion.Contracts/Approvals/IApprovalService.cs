using System.Threading;
using System.Threading.Tasks;

namespace Pillion.Contracts;

/// <summary>User approvals, implemented by the VSIX and called by the host.</summary>
public interface IApprovalService
{
    /// <summary>Asks the user to approve a tool call and waits for the answer.</summary>
    /// <param name="request">The approval request.</param>
    /// <param name="cancellationToken">A token that cancels this call.</param>
    /// <returns>The user's decision.</returns>
    Task<ApprovalDecision> RequestApprovalAsync(ApprovalRequest request, CancellationToken cancellationToken);
}
