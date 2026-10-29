using System.Threading;
using System.Threading.Tasks;

namespace Pillion.Contracts;

/// <summary>
/// The apply journal and checkpoints (design sections 5 and 10), implemented by the host and called by the VSIX. The VSIX is
/// the only writer of the working tree; the host owns every record that must survive a restart. So before its first edit the
/// VSIX hands the host the pre-edit text of every file the changeset touches (<see cref="BeginApplyAsync"/>: the host stores a
/// content-addressed checkpoint in <c>checkpoints.db</c> and a journal intent in <c>conversations.db</c>), and after its last it
/// commits with the post-apply hashes (<see cref="CommitApplyAsync"/>) or, having rolled back, aborts
/// (<see cref="AbortApplyAsync"/>). An entry with an intent but neither is an interrupted apply; at the next start the VSIX
/// lists them (<see cref="ListInterruptedAppliesAsync"/>) and offers roll-back or roll-forward. Added in contract 1.4 (work item
/// 3.5; implemented by 3.8, called by 3.13). A 1.3 host answers every method with StreamJsonRpc's method-not-found error, in
/// which case the VSIX does not apply.
/// </summary>
public interface IJournalService
{
    /// <summary>Records the intent to apply a changeset and checkpoints the pre-edit text of every file it touches.</summary>
    /// <param name="request">The changeset id and the files with their pre-edit text.</param>
    /// <param name="cancellationToken">A token that cancels this call.</param>
    /// <returns>The checkpoint and journal ids, which the VSIX reports in the <see cref="ApplyResult"/>.</returns>
    Task<ApplyBeginResponse> BeginApplyAsync(ApplyBeginRequest request, CancellationToken cancellationToken);

    /// <summary>Marks an apply committed, with the hash of every file as the VSIX left it.</summary>
    /// <param name="request">The journal id and the post-apply hashes.</param>
    /// <param name="cancellationToken">A token that cancels this call.</param>
    /// <returns>A task that completes when the commit is durable. An unknown or already closed journal id fails with <see cref="PillionErrorCode.PolicyDenied"/>.</returns>
    Task CommitApplyAsync(ApplyCommitRequest request, CancellationToken cancellationToken);

    /// <summary>Marks an apply aborted: the VSIX rolled every changed file back (or rolled an interrupted apply back at start-up).</summary>
    /// <param name="journalId">The journal id.</param>
    /// <param name="reason">Why, for the audit trail; <see langword="null"/> when not known.</param>
    /// <param name="cancellationToken">A token that cancels this call.</param>
    /// <returns>A task that completes when the abort is durable.</returns>
    Task AbortApplyAsync(string journalId, string? reason, CancellationToken cancellationToken);

    /// <summary>
    /// Lists the journal entries that have an intent but no commit or abort (an apply interrupted by a crash of either
    /// process), oldest first, each with the pre-edit text for roll-back and the changeset for roll-forward.
    /// </summary>
    /// <param name="cancellationToken">A token that cancels this call.</param>
    /// <returns>The interrupted applies; empty when there are none.</returns>
    Task<ApplyJournalEntry[]> ListInterruptedAppliesAsync(CancellationToken cancellationToken);
}
