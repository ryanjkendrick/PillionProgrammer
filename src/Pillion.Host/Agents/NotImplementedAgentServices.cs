using Pillion.Contracts;
using Pillion.Host.Rpc;

namespace Pillion.Host.Agents;

/// <summary>
/// Contract 1.4 stubs (work item 3.5): the agent, terminal and journal services are on the contract before the work items that
/// implement them (3.6, 3.11 and 3.8) land, so a 1.4 extension gets a typed <see cref="PillionErrorCode.NotImplemented"/>
/// naming the work item rather than method-not-found. Each work item replaces its stub (see the host README, Contract 1.4 stubs).
/// </summary>
internal sealed class NotImplementedAgentService : IAgentService
{
    private const string WorkItem = "3.6";

    /// <inheritdoc/>
    public Task<string> StartSessionAsync(AgentSessionRequest request, CancellationToken cancellationToken) =>
        Task.FromException<string>(RpcErrors.NotImplemented("Agent sessions", WorkItem));

    /// <inheritdoc/>
    public Task ApproveAsync(string approvalId, CancellationToken cancellationToken) =>
        Task.FromException(RpcErrors.NotImplemented("Agent approvals", WorkItem));

    /// <inheritdoc/>
    public Task DenyAsync(string approvalId, string? reason, CancellationToken cancellationToken) =>
        Task.FromException(RpcErrors.NotImplemented("Agent approvals", WorkItem));

    /// <inheritdoc/>
    public Task StopAsync(string sessionId, CancellationToken cancellationToken) =>
        Task.FromException(RpcErrors.NotImplemented("Agent sessions", WorkItem));

    /// <inheritdoc/>
    public Task RevertAsync(string sessionId, string changesetId, CancellationToken cancellationToken) =>
        Task.FromException(RpcErrors.NotImplemented("Reverting an agent changeset", WorkItem));

    /// <inheritdoc/>
    public Task AnswerApprovalAsync(ApprovalDecision decision, CancellationToken cancellationToken) =>
        Task.FromException(RpcErrors.NotImplemented("Agent approvals", WorkItem));

    /// <inheritdoc/>
    public Task<AgentSessionSummary[]> ListSessionsAsync(AgentSessionListRequest request, CancellationToken cancellationToken) =>
        Task.FromException<AgentSessionSummary[]>(RpcErrors.NotImplemented("Agent sessions", WorkItem));

    /// <inheritdoc/>
    public Task<AgentSessionState?> GetSessionAsync(string sessionId, CancellationToken cancellationToken) =>
        Task.FromException<AgentSessionState?>(RpcErrors.NotImplemented("Agent sessions", WorkItem));

    /// <inheritdoc/>
    public Task SendMessageAsync(AgentMessageRequest request, CancellationToken cancellationToken) =>
        Task.FromException(RpcErrors.NotImplemented("Agent sessions", WorkItem));

    /// <inheritdoc/>
    public Task SetApprovalModeAsync(string sessionId, AgentApprovalMode mode, CancellationToken cancellationToken) =>
        Task.FromException(RpcErrors.NotImplemented("Agent sessions", WorkItem));

    /// <inheritdoc/>
    public Task RevertSessionAsync(string sessionId, CancellationToken cancellationToken) =>
        Task.FromException(RpcErrors.NotImplemented("Reverting an agent session", WorkItem));
}

/// <summary>The <see cref="ITerminalService"/> stub until the Command Runner (work item 3.11) lands.</summary>
internal sealed class NotImplementedTerminalService : ITerminalService
{
    private const string Feature = "The Pillion terminal";
    private const string WorkItem = "3.11";

    /// <inheritdoc/>
    public Task SendInputAsync(string terminalId, string text, CancellationToken cancellationToken) =>
        Task.FromException(RpcErrors.NotImplemented(Feature, WorkItem));

    /// <inheritdoc/>
    public Task ResizeAsync(string terminalId, int columns, int rows, CancellationToken cancellationToken) =>
        Task.FromException(RpcErrors.NotImplemented(Feature, WorkItem));

    /// <inheritdoc/>
    public Task<TerminalInfo> OpenDeveloperShellAsync(OpenDeveloperShellRequest request, CancellationToken cancellationToken) =>
        Task.FromException<TerminalInfo>(RpcErrors.NotImplemented(Feature, WorkItem));

    /// <inheritdoc/>
    public Task<TerminalScreen> GetScreenAsync(string terminalId, CancellationToken cancellationToken) =>
        Task.FromException<TerminalScreen>(RpcErrors.NotImplemented(Feature, WorkItem));

    /// <inheritdoc/>
    public Task<TerminalInfo[]> ListTerminalsAsync(CancellationToken cancellationToken) =>
        Task.FromException<TerminalInfo[]>(RpcErrors.NotImplemented(Feature, WorkItem));

    /// <inheritdoc/>
    public Task CloseTerminalAsync(string terminalId, CancellationToken cancellationToken) =>
        Task.FromException(RpcErrors.NotImplemented(Feature, WorkItem));
}

/// <summary>The <see cref="IJournalService"/> stub until the checkpoints and the apply journal (work item 3.8) land.</summary>
internal sealed class NotImplementedJournalService : IJournalService
{
    private const string Feature = "The apply journal";
    private const string WorkItem = "3.8";

    /// <inheritdoc/>
    public Task<ApplyBeginResponse> BeginApplyAsync(ApplyBeginRequest request, CancellationToken cancellationToken) =>
        Task.FromException<ApplyBeginResponse>(RpcErrors.NotImplemented(Feature, WorkItem));

    /// <inheritdoc/>
    public Task CommitApplyAsync(ApplyCommitRequest request, CancellationToken cancellationToken) =>
        Task.FromException(RpcErrors.NotImplemented(Feature, WorkItem));

    /// <inheritdoc/>
    public Task AbortApplyAsync(string journalId, string? reason, CancellationToken cancellationToken) =>
        Task.FromException(RpcErrors.NotImplemented(Feature, WorkItem));

    /// <inheritdoc/>
    public Task<ApplyJournalEntry[]> ListInterruptedAppliesAsync(CancellationToken cancellationToken) =>
        Task.FromException<ApplyJournalEntry[]>(RpcErrors.NotImplemented(Feature, WorkItem));
}
