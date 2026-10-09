using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.Language.Proposals;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;

namespace Pillion.VisualStudio.Autocomplete;

/// <summary>
/// Builds Pillion's proposals. Part of the Proposals adapter: every use of
/// <c>Microsoft.VisualStudio.Language.Proposals</c> lives in this folder, so an API change in a later Visual Studio
/// touches one place. Only pure insertions at the caret are made, because only those render as ghost text (other
/// edits render as a difference preview); virtual-space indentation is never repeated in the text (17.14 does not
/// strip it, 18.x does).
/// </summary>
internal static class ProposalFactory
{
    /// <summary>The source name reported in proposal collections and shown in the hint bar.</summary>
    public const string SourceName = "Pillion Programmer";

    /// <summary>
    /// The flags on every proposal: accepting one immediately queues a request for the next (design section 9,
    /// "an acceptance immediately requests the next suggestion").
    /// </summary>
    public const ProposalFlags Flags = ProposalFlags.QueueProposalRequestOnCommit;

    /// <summary>Creates an insertion proposal at the caret.</summary>
    /// <param name="caret">The caret the request was made for (its snapshot is the one the edit applies to).</param>
    /// <param name="text">The text to insert, with the document's line breaks.</param>
    /// <param name="proposalId">The Pillion proposal id.</param>
    /// <returns>The proposal, or <see langword="null"/> when the editor considers it invalid.</returns>
    public static ProposalBase? CreateInsertion(VirtualSnapshotPoint caret, string text, string proposalId)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        var edits = new List<ProposedEdit> { new(new SnapshotSpan(caret.Position, 0), text) };
        return Proposal.TryCreateProposal(
            SourceName,
            edits,
            caret,
            completionState: null,
            flags: Flags,
            commitAction: null,
            proposalId: proposalId,
            acceptText: null,
            nextText: null);
    }

    /// <summary>Wraps proposals in a collection for the broker.</summary>
    /// <param name="proposals">The proposals.</param>
    /// <returns>The collection.</returns>
    public static ProposalCollectionBase Collection(params ProposalBase[] proposals) => new ProposalCollection(SourceName, proposals);

    /// <summary>Gets the text a displayed proposal would still insert at the caret, when it is a pure insertion there.</summary>
    /// <param name="proposal">The displayed proposal.</param>
    /// <param name="caret">The view's caret position.</param>
    /// <param name="text">The text.</param>
    /// <returns><see langword="true"/> for a single zero-length edit at the caret.</returns>
    public static bool TryGetInsertion(ProposalBase? proposal, SnapshotPoint caret, out string text)
    {
        text = string.Empty;
        if (proposal?.Edits is not { Count: 1 } edits)
        {
            return false;
        }

        var edit = edits[0];
        var span = edit.Span.Snapshot == caret.Snapshot ? edit.Span : edit.Span.TranslateTo(caret.Snapshot, SpanTrackingMode.EdgeExclusive);
        if (span.Length != 0 || span.Start != caret || string.IsNullOrEmpty(edit.ReplacementText))
        {
            return false;
        }

        text = edit.ReplacementText;
        return true;
    }

    /// <summary>Gets the line break to use in a view's document at a position.</summary>
    /// <param name="view">The view.</param>
    /// <param name="position">A position in the view's buffer.</param>
    /// <returns>The caret line's line break, or the editor's default.</returns>
    public static string NewLine(ITextView view, SnapshotPoint position)
    {
        var line = position.GetContainingLine();
        if (line.LineBreakLength > 0)
        {
            return line.GetLineBreakText();
        }

        if (line.LineNumber > 0)
        {
            var previous = position.Snapshot.GetLineFromLineNumber(line.LineNumber - 1);
            if (previous.LineBreakLength > 0)
            {
                return previous.GetLineBreakText();
            }
        }

        return view.Options.GetOptionValue(DefaultOptions.NewLineCharacterOptionId) ?? Environment.NewLine;
    }

    /// <summary>Converts <c>\n</c> line breaks to the document's.</summary>
    /// <param name="text">The text with <c>\n</c> line breaks.</param>
    /// <param name="newLine">The document's line break.</param>
    /// <returns>The converted text.</returns>
    public static string ToDocumentLineBreaks(string text, string newLine) =>
        newLine == "\n" ? text : text.Replace("\n", newLine);
}
