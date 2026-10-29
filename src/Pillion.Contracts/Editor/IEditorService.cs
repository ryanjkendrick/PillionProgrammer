using System.Threading;
using System.Threading.Tasks;

namespace Pillion.Contracts;

/// <summary>Editor operations, implemented by the VSIX and called by the host.</summary>
/// <remarks>
/// The only file-writing path (design section 4). Contract 1.4 (work item 3.5) added <see cref="ReadDocumentAsync"/>; a 1.3 VSIX
/// answers it with StreamJsonRpc's method-not-found error, and the host reads the disk instead.
/// </remarks>
public interface IEditorService
{
    /// <summary>Applies a changeset to the open solution.</summary>
    /// <param name="changeset">The changeset to apply.</param>
    /// <param name="cancellationToken">A token that cancels this call.</param>
    /// <returns>The result of applying the changeset.</returns>
    Task<ApplyResult> ApplyChangesetAsync(Changeset changeset, CancellationToken cancellationToken);

    /// <summary>Shows a changeset as a diff preview and waits for the user's decision.</summary>
    /// <param name="changeset">The changeset to preview.</param>
    /// <param name="cancellationToken">A token that cancels this call.</param>
    /// <returns>The user's decision.</returns>
    Task<PreviewDecision> ShowPreviewAsync(Changeset changeset, CancellationToken cancellationToken);

    /// <summary>Lists the documents open in the editor.</summary>
    /// <param name="cancellationToken">A token that cancels this call.</param>
    /// <returns>The open documents.</returns>
    Task<OpenDocument[]> GetOpenDocumentsAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Reads an open document's buffer text, unsaved changes included, so the agent's <c>read_file</c> and its edits see the text
    /// the VSIX will change (decision of 2026-10-24 in <c>docs/phase-3-plan.md</c>). Added in contract 1.4.
    /// </summary>
    /// <param name="path">The document's full path.</param>
    /// <param name="cancellationToken">A token that cancels this call.</param>
    /// <returns>The text, or a result with <see cref="DocumentText.IsOpen"/> false when the document is not open (the caller then reads the disk).</returns>
    Task<DocumentText> ReadDocumentAsync(string path, CancellationToken cancellationToken);
}
