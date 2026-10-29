namespace Pillion.Contracts;

/// <summary>
/// The version of the RPC contract defined in this assembly. Exchanged during the handshake so that a
/// VSIX and a host built from different commits can tell whether they can talk to each other.
/// </summary>
/// <remarks>
/// Increment <see cref="Major"/> for any breaking change (removing or changing a method, or changing a DTO field's
/// meaning, type or order). Increment <see cref="Minor"/> when only adding methods or appending DTO fields.
/// </remarks>
public static class ContractVersion
{
    /// <summary>The contract major version. Both sides must use the same major version.</summary>
    public const int Major = 1;

    /// <summary>
    /// The contract minor version. Minor versions may differ between the two sides. 1.1 added the model, provider,
    /// token and error fields on <see cref="ChatChunk"/> and <see cref="CompletionChunk"/>, the model id, availability,
    /// context window and digest on <see cref="ModelDescriptor"/>, and <see cref="ConnectivityState"/>. 1.2 added the
    /// caret, declaration, open-document and edit context on <see cref="CompletionRequest"/>, <see cref="IInlineEditService"/>
    /// and its DTOs, mentions, pins and project rules for context assembly, <see cref="TaskClass.Summarise"/>,
    /// <see cref="IDiagnosticsService"/> with <see cref="AutocompleteHostCounters"/> and the resident models, the Outbound log read and context preview
    /// methods (<c>GetOutboundRecordsAsync</c>, <c>PreviewContextAsync</c>) and the <c>settingsChanged</c> notification. 1.3
    /// added <see cref="IConversationService"/>, the index phase, roots and progress on <see cref="IndexStatus"/> with the
    /// index control methods and the <c>indexStatusChanged</c> notification, the search filters and
    /// <see cref="SearchResponse"/>, <see cref="Citation"/>s on chat replies and mentions, the retrieval switch on
    /// <see cref="ChatRequest"/>, the 1.3 <see cref="MentionKinds"/>, <c>CallersOfAsync</c> on
    /// <see cref="IWorkspaceQueryService"/>, and <see cref="PillionErrorCode.StorageUnavailable"/> and
    /// <see cref="PillionErrorCode.NotImplemented"/> (see the README). 1.4 added agent session control and
    /// <c>AnswerApprovalAsync</c> on <see cref="IAgentService"/> with the ledger (<see cref="AgentSessionState"/>) and the
    /// notifications <c>agentSessionChanged</c>, <c>agentToolCall</c>, <c>agentChangesetReady</c>, <c>terminalScreenChanged</c>
    /// and <c>backgroundProcessChanged</c>, <see cref="ITerminalService"/>, <see cref="IJournalService"/>, <see cref="ITrustService"/>,
    /// <c>ReadDocumentAsync</c> on <see cref="IEditorService"/>, <c>PlanRenameAsync</c> on <see cref="IWorkspaceQueryService"/>, the
    /// creates, deletes, renames and project-item changes on <see cref="Changeset"/>, the per-hunk <see cref="PreviewDecision"/>,
    /// the per-file <see cref="ApplyResult"/>, the Visual Studio installation on <see cref="HandshakeRequest"/>,
    /// <see cref="MentionKinds.Terminal"/>, <see cref="TaskClass.AgentPlanning"/> and <see cref="PillionErrorCode.BudgetExhausted"/>.
    /// </summary>
    public const int Minor = 4;

    /// <summary>Gets the contract version formatted as <c>major.minor</c>.</summary>
    public static string Text => Major.ToString(System.Globalization.CultureInfo.InvariantCulture)
        + "." + Minor.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// Determines whether a peer that reports the given contract version can talk to this build.
    /// </summary>
    /// <param name="otherMajor">The peer's contract major version.</param>
    /// <param name="otherMinor">The peer's contract minor version. Any value is accepted; minors only add methods.</param>
    /// <returns><see langword="true"/> when the major versions are equal; otherwise <see langword="false"/>.</returns>
    public static bool IsCompatible(int otherMajor, int otherMinor)
    {
        _ = otherMinor;
        return otherMajor == Major;
    }
}
