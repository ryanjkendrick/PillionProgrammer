using System.Threading;
using System.Threading.Tasks;

namespace Pillion.Contracts;

/// <summary>Agent sessions, implemented by the host and called by the VSIX.</summary>
public interface IAgentService
{
    /// <summary>Starts an agent session.</summary>
    /// <param name="request">The session request.</param>
    /// <param name="cancellationToken">A token that cancels this call.</param>
    /// <returns>The new session id.</returns>
    Task<string> StartSessionAsync(AgentSessionRequest request, CancellationToken cancellationToken);

    /// <summary>Approves a pending tool call.</summary>
    /// <param name="approvalId">The approval id.</param>
    /// <param name="cancellationToken">A token that cancels this call.</param>
    /// <returns>A task that completes when the approval has been recorded.</returns>
    Task ApproveAsync(string approvalId, CancellationToken cancellationToken);

    /// <summary>Denies a pending tool call.</summary>
    /// <param name="approvalId">The approval id.</param>
    /// <param name="reason">The user's reason, if given.</param>
    /// <param name="cancellationToken">A token that cancels this call.</param>
    /// <returns>A task that completes when the denial has been recorded.</returns>
    Task DenyAsync(string approvalId, string? reason, CancellationToken cancellationToken);

    /// <summary>Stops an agent session.</summary>
    /// <param name="sessionId">The session id.</param>
    /// <param name="cancellationToken">A token that cancels this call.</param>
    /// <returns>A task that completes when the session has stopped.</returns>
    Task StopAsync(string sessionId, CancellationToken cancellationToken);

    /// <summary>Reverts a changeset applied by an agent session.</summary>
    /// <param name="sessionId">The session id.</param>
    /// <param name="changesetId">The changeset to revert.</param>
    /// <param name="cancellationToken">A token that cancels this call.</param>
    /// <returns>A task that completes when the changeset has been reverted.</returns>
    Task RevertAsync(string sessionId, string changesetId, CancellationToken cancellationToken);
}
