using System.Threading;
using System.Threading.Tasks;

namespace Pillion.Contracts;

/// <summary>Workspace context and indexing, implemented by the host and called by the VSIX.</summary>
public interface IContextService
{
    /// <summary>Searches the workspace index.</summary>
    /// <param name="request">The search request.</param>
    /// <param name="cancellationToken">A token that cancels this call.</param>
    /// <returns>The search hits, most relevant first.</returns>
    Task<SearchResult[]> SearchAsync(SearchRequest request, CancellationToken cancellationToken);

    /// <summary>Resolves an <c>@</c> mention typed in chat.</summary>
    /// <param name="request">The mention to resolve.</param>
    /// <param name="cancellationToken">A token that cancels this call.</param>
    /// <returns>The resolution.</returns>
    Task<MentionResolution> ResolveMentionAsync(MentionRequest request, CancellationToken cancellationToken);

    /// <summary>Gets the state of the workspace index.</summary>
    /// <param name="cancellationToken">A token that cancels this call.</param>
    /// <returns>The index status.</returns>
    Task<IndexStatus> GetIndexStatusAsync(CancellationToken cancellationToken);

    /// <summary>Pauses or resumes background indexing.</summary>
    /// <param name="pause"><see langword="true"/> to pause; <see langword="false"/> to resume.</param>
    /// <param name="cancellationToken">A token that cancels this call.</param>
    /// <returns>A task that completes when the change has been applied.</returns>
    Task PauseIndexingAsync(bool pause, CancellationToken cancellationToken);
}
