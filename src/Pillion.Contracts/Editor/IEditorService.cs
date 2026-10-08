using System.Threading;
using System.Threading.Tasks;

namespace Pillion.Contracts;

/// <summary>Editor operations, implemented by the VSIX and called by the host.</summary>
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
}
