using System;
using System.Runtime.Serialization;

namespace Pillion.Contracts;

/// <summary>
/// The task ledger of an agent session (design section 8, Tool loop): the goal, the plan with the state of each step, the
/// budgets used, the pending approval if any and the changesets so far. Returned by <see cref="IAgentService.GetSessionAsync"/>
/// and sent as the one argument of the host-to-VSIX notification <c>agentSessionChanged</c> whenever any of it changes. The
/// host persists it, so a session resumes <see cref="AgentSessionStatus.Paused"/> after a restart. Added in contract 1.4
/// (work item 3.5; consumed by 3.6 and 3.14).
/// </summary>
[DataContract]
public sealed class AgentSessionState
{
    /// <summary>Gets or sets the session id (the value <see cref="IAgentService.StartSessionAsync"/> returned).</summary>
    [DataMember(Order = 0)]
    public string SessionId { get; set; } = string.Empty;

    /// <summary>Gets or sets the conversation the session was started from (<see cref="AgentSessionRequest.ConversationId"/>); empty when none.</summary>
    [DataMember(Order = 1)]
    public string ConversationId { get; set; } = string.Empty;

    /// <summary>Gets or sets the goal, as the user stated it.</summary>
    [DataMember(Order = 2)]
    public string Goal { get; set; } = string.Empty;

    /// <summary>Gets or sets where the session is in its life.</summary>
    [DataMember(Order = 3)]
    public AgentSessionStatus Status { get; set; }

    /// <summary>Gets or sets the approval mode in force (<see cref="IAgentService.SetApprovalModeAsync"/> changes it).</summary>
    [DataMember(Order = 4)]
    public AgentApprovalMode ApprovalMode { get; set; }

    /// <summary>Gets or sets the plan: the steps in order, each with its status and the files it touches.</summary>
    [DataMember(Order = 5)]
    public AgentPlanStep[] Steps { get; set; } = Array.Empty<AgentPlanStep>();

    /// <summary>Gets or sets the constraints the ledger holds the agent to (from the user, the rules files and the policy).</summary>
    [DataMember(Order = 6)]
    public string[] Constraints { get; set; } = Array.Empty<string>();

    /// <summary>Gets or sets the budgets used and their limits.</summary>
    [DataMember(Order = 7)]
    public AgentBudgetUsage Budgets { get; set; } = new AgentBudgetUsage();

    /// <summary>
    /// Gets or sets the approval the session is waiting for, or <see langword="null"/>. Answered through
    /// <see cref="IAgentService.ApproveAsync"/>, <see cref="IAgentService.DenyAsync"/> or
    /// <see cref="IAgentService.AnswerApprovalAsync"/>; an answer given after a host restart still counts.
    /// </summary>
    [DataMember(Order = 8)]
    public ApprovalRequest? PendingApproval { get; set; }

    /// <summary>Gets or sets the changesets the session has produced, oldest first (what Revert changeset and Revert session act on).</summary>
    [DataMember(Order = 9)]
    public AgentChangesetSummary[] Changesets { get; set; } = Array.Empty<AgentChangesetSummary>();

    /// <summary>Gets or sets when the session started.</summary>
    [DataMember(Order = 10)]
    public DateTime StartedUtc { get; set; }

    /// <summary>Gets or sets when the ledger last changed.</summary>
    [DataMember(Order = 11)]
    public DateTime UpdatedUtc { get; set; }

    /// <summary>
    /// Gets or sets the agent's latest report to the user: the final answer of a completed session, the report against the
    /// ledger when a budget ran out, or <see langword="null"/> while there is none.
    /// </summary>
    [DataMember(Order = 12)]
    public string? Summary { get; set; }

    /// <summary>Gets or sets the error that ended a <see cref="AgentSessionStatus.Failed"/> session (<see cref="PillionErrorCode.BudgetExhausted"/> for a budget), or <see langword="null"/>.</summary>
    [DataMember(Order = 13)]
    public PillionError? Error { get; set; }

    /// <summary>Gets or sets the model the session's steps are routed to (<c>providerId/modelId</c>), empty until the first step.</summary>
    [DataMember(Order = 14)]
    public string ModelId { get; set; } = string.Empty;
}

/// <summary>One step of an agent's plan (design section 8, Multi-file edit plans: the ledger marks sensitive paths). Added in contract 1.4.</summary>
[DataContract]
public sealed class AgentPlanStep
{
    /// <summary>Gets or sets what the step does, in the agent's words.</summary>
    [DataMember(Order = 0)]
    public string Title { get; set; } = string.Empty;

    /// <summary>Gets or sets the step's state.</summary>
    [DataMember(Order = 1)]
    public PlanStepStatus Status { get; set; }

    /// <summary>Gets or sets the full paths the step intends to touch.</summary>
    [DataMember(Order = 2)]
    public string[] Paths { get; set; } = Array.Empty<string>();

    /// <summary>Gets or sets the members of <see cref="Paths"/> that are sensitive (design section 8): they need explicit approval in every mode.</summary>
    [DataMember(Order = 3)]
    public string[] SensitivePaths { get; set; } = Array.Empty<string>();

    /// <summary>Gets or sets a note on the step's outcome (why it was skipped or failed), or <see langword="null"/>.</summary>
    [DataMember(Order = 4)]
    public string? Note { get; set; }
}

/// <summary>
/// A session's budgets (design section 8: 25 tool steps, a token ceiling, a cost ceiling, 60 minutes of wall time) and how
/// much of each is used. Time spent waiting for an approval does not count towards the wall time. Added in contract 1.4.
/// </summary>
[DataContract]
public sealed class AgentBudgetUsage
{
    /// <summary>Gets or sets the tool steps taken.</summary>
    [DataMember(Order = 0)]
    public int Steps { get; set; }

    /// <summary>Gets or sets the step limit.</summary>
    [DataMember(Order = 1)]
    public int MaxSteps { get; set; }

    /// <summary>Gets or sets the tokens used (prompt and completion, every model request of the session).</summary>
    [DataMember(Order = 2)]
    public long Tokens { get; set; }

    /// <summary>Gets or sets the token limit.</summary>
    [DataMember(Order = 3)]
    public long MaxTokens { get; set; }

    /// <summary>Gets or sets the estimated cost so far, in the currency of the cost caps (US dollars); 0 for local models.</summary>
    [DataMember(Order = 4)]
    public double Cost { get; set; }

    /// <summary>Gets or sets the cost limit.</summary>
    [DataMember(Order = 5)]
    public double MaxCost { get; set; }

    /// <summary>Gets or sets the wall time used, in seconds, excluding time spent waiting for approvals.</summary>
    [DataMember(Order = 6)]
    public double WallSeconds { get; set; }

    /// <summary>Gets or sets the wall-time limit, in seconds.</summary>
    [DataMember(Order = 7)]
    public double MaxWallSeconds { get; set; }
}

/// <summary>A changeset as the ledger lists it; the changeset itself travels in <see cref="AgentChangesetReady"/>. Added in contract 1.4.</summary>
[DataContract]
public sealed class AgentChangesetSummary
{
    /// <summary>Gets or sets the changeset id (<see cref="Changeset.Id"/>).</summary>
    [DataMember(Order = 0)]
    public string ChangesetId { get; set; } = string.Empty;

    /// <summary>Gets or sets the changeset's description (<see cref="Changeset.Description"/>).</summary>
    [DataMember(Order = 1)]
    public string Description { get; set; } = string.Empty;

    /// <summary>Gets or sets the number of files the changeset touches (edits, creates, deletes and renames together).</summary>
    [DataMember(Order = 2)]
    public int FileCount { get; set; }

    /// <summary>Gets or sets a value indicating whether the VSIX applied it (its journal is committed).</summary>
    [DataMember(Order = 3)]
    public bool IsApplied { get; set; }

    /// <summary>Gets or sets a value indicating whether it was reverted (by Revert changeset or Revert session).</summary>
    [DataMember(Order = 4)]
    public bool IsReverted { get; set; }

    /// <summary>Gets or sets the checkpoint the VSIX received when it began the apply (<see cref="ApplyBeginResponse.CheckpointId"/>), empty until then.</summary>
    [DataMember(Order = 5)]
    public string CheckpointId { get; set; } = string.Empty;

    /// <summary>Gets or sets the journal entry of the apply (<see cref="ApplyBeginResponse.JournalId"/>), empty until then.</summary>
    [DataMember(Order = 6)]
    public string JournalId { get; set; } = string.Empty;

    /// <summary>Gets or sets when the apply was committed, or <see langword="null"/>.</summary>
    [DataMember(Order = 7)]
    public DateTime? AppliedUtc { get; set; }
}

/// <summary>
/// One tool call of an agent session: the one argument of the host-to-VSIX notification <c>agentToolCall</c>, sent when the
/// call starts and again when it ends (design section 5, the Agent window's live tool-call log; section 10, the audit
/// record). <see cref="ToolCallId"/> is stable across the two, and matches <see cref="ApprovalRequest.ToolCallId"/> on the
/// approval the call needed. Added in contract 1.4.
/// </summary>
[DataContract]
public sealed class AgentToolCallRecord
{
    /// <summary>Gets or sets the id of the call, unique within the host.</summary>
    [DataMember(Order = 0)]
    public string ToolCallId { get; set; } = string.Empty;

    /// <summary>Gets or sets the session the call belongs to.</summary>
    [DataMember(Order = 1)]
    public string SessionId { get; set; } = string.Empty;

    /// <summary>Gets or sets the tool step the call counts as (one-based; the budget's step counter), or 0 for a call that counts no step.</summary>
    [DataMember(Order = 2)]
    public int Step { get; set; }

    /// <summary>Gets or sets the tool's name (<c>read_file</c>, <c>run_command</c>, ...; design section 8, Tool catalog).</summary>
    [DataMember(Order = 3)]
    public string ToolName { get; set; } = string.Empty;

    /// <summary>Gets or sets the permission tier the call was classified as (after the deny-list and policy promotions).</summary>
    [DataMember(Order = 4)]
    public PermissionTier Tier { get; set; }

    /// <summary>Gets or sets a one-line, redacted summary of the arguments (for a command, the exact command line).</summary>
    [DataMember(Order = 5)]
    public string ArgumentsSummary { get; set; } = string.Empty;

    /// <summary>Gets or sets the kind of source of the instruction that led to the call.</summary>
    [DataMember(Order = 6)]
    public ProvenanceKind Provenance { get; set; }

    /// <summary>Gets or sets the source itself (a file path, a tool name, a step), for display.</summary>
    [DataMember(Order = 7)]
    public string ProvenanceDetail { get; set; } = string.Empty;

    /// <summary>Gets or sets why the call was considered tainted by untrusted content (design section 8, Taint rule), or <see langword="null"/>.</summary>
    [DataMember(Order = 8)]
    public string? TaintReason { get; set; }

    /// <summary>Gets or sets the call's state.</summary>
    [DataMember(Order = 9)]
    public AgentToolCallStatus Status { get; set; }

    /// <summary>Gets or sets a short, redacted summary of the result (or of the failure), or <see langword="null"/> before the call ended.</summary>
    [DataMember(Order = 10)]
    public string? ResultSummary { get; set; }

    /// <summary>Gets or sets how long the call ran, in milliseconds (0 before it ended; waiting for an approval is not counted).</summary>
    [DataMember(Order = 11)]
    public long DurationMs { get; set; }

    /// <summary>Gets or sets when the call started.</summary>
    [DataMember(Order = 12)]
    public DateTime StartedUtc { get; set; }

    /// <summary>Gets or sets the approval that let the call run, or <see langword="null"/> when none was needed.</summary>
    [DataMember(Order = 13)]
    public string? ApprovalId { get; set; }

    /// <summary>Gets or sets a value indicating whether the call ran without a prompt (T0, or an allow-listed command, or the mode allowed it).</summary>
    [DataMember(Order = 14)]
    public bool IsAutomatic { get; set; }
}

/// <summary>A session as <see cref="IAgentService.ListSessionsAsync"/> lists it. Added in contract 1.4.</summary>
[DataContract]
public sealed class AgentSessionSummary
{
    /// <summary>Gets or sets the session id.</summary>
    [DataMember(Order = 0)]
    public string SessionId { get; set; } = string.Empty;

    /// <summary>Gets or sets the conversation the session was started from; empty when none.</summary>
    [DataMember(Order = 1)]
    public string ConversationId { get; set; } = string.Empty;

    /// <summary>Gets or sets the goal.</summary>
    [DataMember(Order = 2)]
    public string Goal { get; set; } = string.Empty;

    /// <summary>Gets or sets where the session is in its life.</summary>
    [DataMember(Order = 3)]
    public AgentSessionStatus Status { get; set; }

    /// <summary>Gets or sets the approval mode in force.</summary>
    [DataMember(Order = 4)]
    public AgentApprovalMode ApprovalMode { get; set; }

    /// <summary>Gets or sets when the session started.</summary>
    [DataMember(Order = 5)]
    public DateTime StartedUtc { get; set; }

    /// <summary>Gets or sets when the ledger last changed.</summary>
    [DataMember(Order = 6)]
    public DateTime UpdatedUtc { get; set; }

    /// <summary>Gets or sets the number of changesets the session has produced.</summary>
    [DataMember(Order = 7)]
    public int ChangesetCount { get; set; }
}

/// <summary>Filters for <see cref="IAgentService.ListSessionsAsync"/>. Added in contract 1.4.</summary>
[DataContract]
public sealed class AgentSessionListRequest
{
    /// <summary>Gets or sets the conversation whose sessions to list; empty lists every conversation's.</summary>
    [DataMember(Order = 0)]
    public string ConversationId { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether finished sessions (stopped, completed, failed) are included.</summary>
    [DataMember(Order = 1)]
    public bool IncludeFinished { get; set; }

    /// <summary>Gets or sets the most sessions to return, most recently updated first; 0 means 50, capped at 500.</summary>
    [DataMember(Order = 2)]
    public int Limit { get; set; }
}

/// <summary>A follow-up message to a running or paused session (<see cref="IAgentService.SendMessageAsync"/>). Added in contract 1.4.</summary>
[DataContract]
public sealed class AgentMessageRequest
{
    /// <summary>Gets or sets the session.</summary>
    [DataMember(Order = 0)]
    public string SessionId { get; set; } = string.Empty;

    /// <summary>Gets or sets the user's message (provenance <see cref="ProvenanceKind.User"/>).</summary>
    [DataMember(Order = 1)]
    public string Message { get; set; } = string.Empty;

    /// <summary>Gets or sets pins to add to the session's context, resolved again by the host.</summary>
    [DataMember(Order = 2)]
    public ConversationPin[] Pins { get; set; } = Array.Empty<ConversationPin>();
}

/// <summary>
/// The one argument of the host-to-VSIX notification <c>agentChangesetReady</c>: the agent has a changeset for the user, after
/// syntax validation and with every redaction placeholder restored. In <see cref="AgentApprovalMode.Ask"/>, and for a changeset that
/// touches a sensitive path in every mode, the ledger holds a pending approval for it (<see cref="ApprovalRequest.ChangesetId"/>
/// names it); the apply follows the approval. Added in contract 1.4 (consumed by 3.13 and 3.14).
/// </summary>
[DataContract]
public sealed class AgentChangesetReady
{
    /// <summary>Gets or sets the session that produced the changeset.</summary>
    [DataMember(Order = 0)]
    public string SessionId { get; set; } = string.Empty;

    /// <summary>Gets or sets the changeset.</summary>
    [DataMember(Order = 1)]
    public Changeset Changeset { get; set; } = new Changeset();

    /// <summary>Gets or sets the approval the apply waits for, or <see langword="null"/> when the mode applies it without asking.</summary>
    [DataMember(Order = 2)]
    public string? ApprovalId { get; set; }

    /// <summary>Gets or sets the plan step the changeset belongs to (an index into <see cref="AgentSessionState.Steps"/>), or -1.</summary>
    [DataMember(Order = 3)]
    public int StepIndex { get; set; } = -1;
}
