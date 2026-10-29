using System;
using Pillion.Contracts;

namespace Pillion.VisualStudio.Core;

/// <summary>
/// Describes an <c>agentSessionChanged</c> notification from Pillion Host (contract 1.4): a session's ledger after a change.
/// Not replayed to a late subscriber; call <see cref="IAgentService.GetSessionAsync"/> when a window opens.
/// </summary>
public sealed class HostAgentSessionChangedEventArgs : EventArgs
{
    /// <summary>Initializes a new instance of the <see cref="HostAgentSessionChangedEventArgs"/> class.</summary>
    /// <param name="session">The ledger the host reported; <see langword="null"/> is read as an empty ledger.</param>
    public HostAgentSessionChangedEventArgs(AgentSessionState? session)
    {
        Session = session ?? new AgentSessionState();
    }

    /// <summary>Gets the ledger.</summary>
    public AgentSessionState Session { get; }
}

/// <summary>Describes an <c>agentToolCall</c> notification from Pillion Host (contract 1.4): a tool call that started or ended.</summary>
public sealed class HostAgentToolCallEventArgs : EventArgs
{
    /// <summary>Initializes a new instance of the <see cref="HostAgentToolCallEventArgs"/> class.</summary>
    /// <param name="toolCall">The record the host reported; <see langword="null"/> is read as an empty record.</param>
    public HostAgentToolCallEventArgs(AgentToolCallRecord? toolCall)
    {
        ToolCall = toolCall ?? new AgentToolCallRecord();
    }

    /// <summary>Gets the tool call.</summary>
    public AgentToolCallRecord ToolCall { get; }
}

/// <summary>Describes an <c>agentChangesetReady</c> notification from Pillion Host (contract 1.4): a changeset for the user to review.</summary>
public sealed class HostAgentChangesetReadyEventArgs : EventArgs
{
    /// <summary>Initializes a new instance of the <see cref="HostAgentChangesetReadyEventArgs"/> class.</summary>
    /// <param name="changeset">What the host reported; <see langword="null"/> is read as an empty notification.</param>
    public HostAgentChangesetReadyEventArgs(AgentChangesetReady? changeset)
    {
        Changeset = changeset ?? new AgentChangesetReady();
    }

    /// <summary>Gets the notification: the session and its changeset.</summary>
    public AgentChangesetReady Changeset { get; }
}

/// <summary>Describes a <c>terminalScreenChanged</c> notification from Pillion Host (contract 1.4): a terminal's rendered screen and scrollback delta.</summary>
public sealed class HostTerminalScreenChangedEventArgs : EventArgs
{
    /// <summary>Initializes a new instance of the <see cref="HostTerminalScreenChangedEventArgs"/> class.</summary>
    /// <param name="screen">The screen the host reported; <see langword="null"/> is read as an empty screen.</param>
    public HostTerminalScreenChangedEventArgs(TerminalScreen? screen)
    {
        Screen = screen ?? new TerminalScreen();
    }

    /// <summary>Gets the screen.</summary>
    public TerminalScreen Screen { get; }
}

/// <summary>Describes a <c>backgroundProcessChanged</c> notification from Pillion Host (contract 1.4): a background process whose state changed.</summary>
public sealed class HostBackgroundProcessChangedEventArgs : EventArgs
{
    /// <summary>Initializes a new instance of the <see cref="HostBackgroundProcessChangedEventArgs"/> class.</summary>
    /// <param name="process">The process the host reported; <see langword="null"/> is read as an empty record.</param>
    public HostBackgroundProcessChangedEventArgs(BackgroundProcessInfo? process)
    {
        Process = process ?? new BackgroundProcessInfo();
    }

    /// <summary>Gets the process.</summary>
    public BackgroundProcessInfo Process { get; }
}
