using System.Threading;
using System.Threading.Tasks;

namespace Pillion.Contracts;

/// <summary>Agent sessions, implemented by the host and called by the VSIX.</summary>
/// <remarks>
/// Contract 1.4 (work item 3.5) added session control (<see cref="ListSessionsAsync"/>, <see cref="GetSessionAsync"/>,
/// <see cref="SendMessageAsync"/>, <see cref="SetApprovalModeAsync"/>, <see cref="RevertSessionAsync"/>) and
/// <see cref="AnswerApprovalAsync"/>. Approvals are session state: the ledger (<see cref="AgentSessionState.PendingApproval"/>)
/// carries the request, persisted with the session, and the VSIX answers it through <see cref="ApproveAsync"/>,
/// <see cref="DenyAsync"/> or <see cref="AnswerApprovalAsync"/>, which also works after either process restarted. The
/// host-to-VSIX <see cref="IApprovalService.RequestApprovalAsync"/> stays on the contract and is not used by the runtime.
/// The host reports a session's progress with the notifications <c>agentSessionChanged</c> (<see cref="AgentSessionState"/>),
/// <c>agentToolCall</c> (<see cref="AgentToolCallRecord"/>) and <c>agentChangesetReady</c> (<see cref="AgentChangesetReady"/>),
/// none of which is replayed to a client that connects later: call <see cref="GetSessionAsync"/> then. A 1.3 host answers
/// the new methods with StreamJsonRpc's method-not-found error.
/// </remarks>
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

    /// <summary>
    /// Answers a pending approval with the full decision: approve or deny, with
    /// <see cref="ApprovalDecision.RememberForSession"/>, <see cref="ApprovalDecision.AddToAllowList"/> and, for a changeset,
    /// the per-hunk <see cref="ApprovalDecision.Preview"/>. <see cref="ApproveAsync"/> and <see cref="DenyAsync"/> keep working
    /// and mean a plain yes or no. Added in contract 1.4 (work item 3.5), because the two older methods cannot carry the flags.
    /// </summary>
    /// <param name="decision">The decision; <see cref="ApprovalDecision.ApprovalId"/> names the approval.</param>
    /// <param name="cancellationToken">A token that cancels this call.</param>
    /// <returns>A task that completes when the decision has been recorded. An unknown or already answered approval fails with <see cref="PillionErrorCode.PolicyDenied"/>.</returns>
    Task AnswerApprovalAsync(ApprovalDecision decision, CancellationToken cancellationToken);

    /// <summary>Lists sessions, most recently updated first. Added in contract 1.4.</summary>
    /// <param name="request">The filters.</param>
    /// <param name="cancellationToken">A token that cancels this call.</param>
    /// <returns>The sessions.</returns>
    Task<AgentSessionSummary[]> ListSessionsAsync(AgentSessionListRequest request, CancellationToken cancellationToken);

    /// <summary>Gets a session's ledger. Added in contract 1.4.</summary>
    /// <param name="sessionId">The session id.</param>
    /// <param name="cancellationToken">A token that cancels this call.</param>
    /// <returns>The ledger, or <see langword="null"/> for an unknown id.</returns>
    Task<AgentSessionState?> GetSessionAsync(string sessionId, CancellationToken cancellationToken);

    /// <summary>
    /// Sends a follow-up message to a session: it joins the conversation with provenance <see cref="ProvenanceKind.User"/>, and
    /// a <see cref="AgentSessionStatus.Paused"/> session resumes. Added in contract 1.4.
    /// </summary>
    /// <param name="request">The message.</param>
    /// <param name="cancellationToken">A token that cancels this call.</param>
    /// <returns>A task that completes when the message has been queued. A stopped, completed or failed session fails with <see cref="PillionErrorCode.PolicyDenied"/>.</returns>
    Task SendMessageAsync(AgentMessageRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Changes a session's approval mode from the next decision on. <see cref="AgentApprovalMode.Autonomous"/> is refused by a phase 3
    /// host with <see cref="PillionErrorCode.PolicyDenied"/>. Added in contract 1.4.
    /// </summary>
    /// <param name="sessionId">The session id.</param>
    /// <param name="mode">The mode.</param>
    /// <param name="cancellationToken">A token that cancels this call.</param>
    /// <returns>A task that completes when the mode has been recorded.</returns>
    Task SetApprovalModeAsync(string sessionId, AgentApprovalMode mode, CancellationToken cancellationToken);

    /// <summary>
    /// Reverts every applied changeset of a session, newest first, the way <see cref="RevertAsync"/> reverts one: the host builds
    /// a restore changeset from the checkpoint (<see cref="Changeset.RevertsChangesetId"/> names what it undoes) and the VSIX applies
    /// it through Visual Studio's undo while the changeset is still undoable and by writing the files otherwise, with a merge
    /// view for a file that changed after the apply. The Agent window's primary revert (decision of 2026-10-24 in
    /// <c>docs/phase-3-plan.md</c>). A running session is stopped first. Added in contract 1.4.
    /// </summary>
    /// <param name="sessionId">The session id.</param>
    /// <param name="cancellationToken">A token that cancels this call.</param>
    /// <returns>A task that completes when every changeset has been reverted or presented for merging.</returns>
    Task RevertSessionAsync(string sessionId, CancellationToken cancellationToken);
}
