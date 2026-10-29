namespace Pillion.Contracts;

/// <summary>
/// How much an agent session may do without asking (design section 8, Approval modes). An integer on the wire. Added in
/// contract 1.4 (work item 3.5; consumed by 3.6, 3.7 and 3.14).
/// </summary>
/// <remarks>
/// Phase 3 ships <see cref="Ask"/> and <see cref="EditsAuto"/>. <see cref="Autonomous"/> is declared now so its wire value is
/// fixed, but no phase 3 host accepts it: a session started with it is refused with <see cref="PillionErrorCode.PolicyDenied"/>
/// until phase 4 ("Autonomy and browser", decision of 2026-10-24 in <c>docs/phase-3-plan.md</c>). Sensitive-path edits and
/// tainted actions prompt in every mode.
/// </remarks>
public enum AgentApprovalMode
{
    /// <summary>The default: T1 edits and T2 commands prompt, allow-listed commands included; T0 reads run.</summary>
    Ask,

    /// <summary>T1 edits apply automatically with a checkpoint; T2 commands prompt.</summary>
    EditsAuto,

    /// <summary>Reserved for phase 4: T1 and allow-listed T2 automatic, T3 prompts. Refused by a phase 3 host.</summary>
    Autonomous,
}

/// <summary>Where an agent session is in its life (the ledger's state). An integer on the wire. Added in contract 1.4.</summary>
public enum AgentSessionStatus
{
    /// <summary>The tool loop is running (planning, calling tools or waiting for a model).</summary>
    Running,

    /// <summary>Stopped on a pending approval; <see cref="AgentSessionState.PendingApproval"/> says which.</summary>
    WaitingForApproval,

    /// <summary>
    /// Suspended with its ledger intact, for example after a host restart (design section 4, Failure modes); a follow-up
    /// message or an approval resumes it.
    /// </summary>
    Paused,

    /// <summary>Stopped by the user (<see cref="IAgentService.StopAsync"/>) before it finished.</summary>
    Stopped,

    /// <summary>Finished with a final answer.</summary>
    Completed,

    /// <summary>Ended by an error or an exhausted budget; <see cref="AgentSessionState.Error"/> says which.</summary>
    Failed,
}

/// <summary>The state of one step of an agent's plan. An integer on the wire. Added in contract 1.4.</summary>
public enum PlanStepStatus
{
    /// <summary>Not started.</summary>
    Pending,

    /// <summary>The step the agent is working on.</summary>
    InProgress,

    /// <summary>Finished.</summary>
    Done,

    /// <summary>Dropped from the plan (the agent found it unnecessary or the user denied it).</summary>
    Skipped,

    /// <summary>Attempted and failed.</summary>
    Failed,
}

/// <summary>
/// Where an instruction the agent acts on came from (design section 8, Tool loop; section 12). Shown in the ledger and in
/// every approval prompt, and written to the audit trail, so every kind is declared now even where its source arrives in a
/// later phase. An integer on the wire. Added in contract 1.4.
/// </summary>
public enum ProvenanceKind
{
    /// <summary>Not recorded (a peer that does not know provenance, or a record from before it was tracked).</summary>
    Unknown,

    /// <summary>The user's own prompt or follow-up.</summary>
    User,

    /// <summary>A rules file: the user-level <c>rules.md</c> or a trusted repository's <c>.pillion/rules.md</c>.</summary>
    RulesFile,

    /// <summary>Content read from a file in the repository (source, documentation, configuration).</summary>
    RepositoryFile,

    /// <summary>The result of a tool call.</summary>
    ToolOutput,

    /// <summary>The screen buffer of a command run in the Pillion terminal.</summary>
    Terminal,

    /// <summary>Git output: a diff, a log message, a status line, a file name.</summary>
    Git,

    /// <summary>A web page read through browser automation (phase 4). Untrusted: taints the session.</summary>
    WebPage,

    /// <summary>An MCP server's result or tool description (phase 4). Untrusted: taints the session.</summary>
    McpServer,
}

/// <summary>The state of a tool call reported by the <c>agentToolCall</c> notification. An integer on the wire. Added in contract 1.4.</summary>
public enum AgentToolCallStatus
{
    /// <summary>The call started (after any approval); the record has no result or duration yet.</summary>
    Started,

    /// <summary>The call finished and returned a result.</summary>
    Succeeded,

    /// <summary>The call ran and failed; <see cref="AgentToolCallRecord.ResultSummary"/> says why.</summary>
    Failed,

    /// <summary>The user denied the call, or policy refused it; it never ran.</summary>
    Denied,

    /// <summary>The call was cancelled or killed (a stop, a timeout, a host restart).</summary>
    Cancelled,
}
