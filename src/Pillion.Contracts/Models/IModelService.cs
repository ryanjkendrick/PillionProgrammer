using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Pillion.Contracts;

/// <summary>Model access, implemented by the host and called by the VSIX.</summary>
public interface IModelService
{
    /// <summary>Streams a fill-in-the-middle completion.</summary>
    /// <param name="request">The completion request.</param>
    /// <param name="cancellationToken">A token that cancels the stream.</param>
    /// <returns>The completion chunks, in order.</returns>
    IAsyncEnumerable<CompletionChunk> CompleteAsync(CompletionRequest request, CancellationToken cancellationToken);

    /// <summary>Streams a chat response.</summary>
    /// <param name="request">The chat request.</param>
    /// <param name="cancellationToken">A token that cancels the stream.</param>
    /// <returns>The response chunks, in order.</returns>
    IAsyncEnumerable<ChatChunk> ChatAsync(ChatRequest request, CancellationToken cancellationToken);

    /// <summary>Cancels an in-flight completion or chat request.</summary>
    /// <param name="requestId">The id of the request to cancel.</param>
    /// <param name="cancellationToken">A token that cancels this call.</param>
    /// <returns>A task that completes when the cancellation has been requested.</returns>
    Task CancelAsync(string requestId, CancellationToken cancellationToken);

    /// <summary>Lists the models the host can use.</summary>
    /// <param name="cancellationToken">A token that cancels this call.</param>
    /// <returns>The available models.</returns>
    Task<ModelDescriptor[]> ListModelsAsync(CancellationToken cancellationToken);
}
