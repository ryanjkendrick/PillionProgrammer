using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Pillion.Contracts;

/// <summary>
/// Inline chat edits (design sections 5 and 8), implemented by the host and called by the VSIX. The host turns an
/// instruction about a scope of code (the selection, else the enclosing member, else the current line) into a proposed
/// replacement for that scope. The VSIX shows it as a difference view and applies it as one edit; the host never
/// touches the buffer.
/// </summary>
public interface IInlineEditService
{
    /// <summary>
    /// Streams a proposed replacement for the request's scope. Concatenating the <see cref="InlineEditChunk.Text"/> of
    /// every chunk (starting again whenever a chunk has <see cref="InlineEditChunk.ResetText"/> set) gives the full
    /// replacement; the last chunk has <see cref="InlineEditChunk.IsFinal"/> set. Every failure, even after preview
    /// text, fails the call with a <see cref="PillionRpcException"/>.
    /// </summary>
    /// <param name="request">The edit request.</param>
    /// <param name="cancellationToken">A token that cancels the stream.</param>
    /// <returns>The chunks, in order.</returns>
    IAsyncEnumerable<InlineEditChunk> EditAsync(InlineEditRequest request, CancellationToken cancellationToken);

    /// <summary>Cancels an in-flight edit request.</summary>
    /// <param name="requestId">The id of the request to cancel.</param>
    /// <param name="cancellationToken">A token that cancels this call.</param>
    /// <returns>A task that completes when the cancellation has been requested.</returns>
    Task CancelEditAsync(string requestId, CancellationToken cancellationToken);
}
