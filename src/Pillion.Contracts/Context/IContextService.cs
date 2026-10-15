using System.Threading;
using System.Threading.Tasks;

namespace Pillion.Contracts;

/// <summary>Workspace context and indexing, implemented by the host and called by the VSIX.</summary>
/// <remarks>
/// <para>
/// Contract 1.3 added <see cref="SearchDetailedAsync"/>, <see cref="ListExcludedFilesAsync"/>, <see cref="RebuildIndexAsync"/>,
/// <see cref="DeleteIndexAsync"/>, <see cref="ListIndexesAsync"/> and <see cref="SetWorkspaceAsync"/>; a 1.2 host answers them with StreamJsonRpc's
/// method-not-found error.
/// </para>
/// <para>
/// The host also pushes the index state to the VSIX: the notification <c>indexStatusChanged</c> (host to client, one
/// positional argument, an <see cref="IndexStatus"/>) is sent whenever the status changes, at most twice a second (changes
/// in between are coalesced and the latest status is sent at the end of the window). It is a notification, not a method on
/// this interface; a 1.2 client registers no handler and ignores it. A client that connects later calls
/// <see cref="GetIndexStatusAsync"/> for the current status.
/// </para>
/// <para>
/// The VSIX tells the host what the IDE is busy with in the notification <c>ideActivityChanged</c> (client to host, one
/// positional argument, an <see cref="IdeActivity"/> holding every activity in progress): sent after every handshake and
/// whenever the set changes, so the host can pause indexing during builds, test runs and debugging. A 1.2 host has no
/// handler and ignores it.
/// </para>
/// </remarks>
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

    /// <summary>
    /// Searches the workspace index like <see cref="SearchAsync"/>, and also reports where the time went and whether the
    /// budget cut retrieval short. Added in contract 1.3 (work item 2.11).
    /// </summary>
    /// <param name="request">The search request.</param>
    /// <param name="cancellationToken">A token that cancels this call.</param>
    /// <returns>The hits and timings.</returns>
    Task<SearchResponse> SearchDetailedAsync(SearchRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Lists, one page at a time, the files and folders a root's index excluded, with the rule that excluded each and the
    /// secret-bearing ones marked (the Index window). Added in contract 1.3 (work item 2.15).
    /// </summary>
    /// <param name="request">The root and the page.</param>
    /// <param name="cancellationToken">A token that cancels this call.</param>
    /// <returns>The page.</returns>
    Task<ExcludedFilesPage> ListExcludedFilesAsync(ExcludedFilesRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Rebuilds a root's index from scratch in the background. Completes once the rebuild has started; progress arrives as
    /// <c>indexStatusChanged</c> notifications. On a host that only reads the index, the request is forwarded to its owner.
    /// Added in contract 1.3 (work item 2.15).
    /// </summary>
    /// <param name="rootPath">The repository root (as <see cref="IndexRootStatus.RootPath"/>).</param>
    /// <param name="cancellationToken">A token that cancels this call (not the rebuild once started).</param>
    /// <returns>A task that completes when the rebuild has started.</returns>
    Task RebuildIndexAsync(string rootPath, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes an index folder (the Index window's disk use and retention view). An index that another live host owns is
    /// refused with <see cref="PillionErrorCode.PolicyDenied"/>; one this host has open is closed first, and its root is
    /// not indexed again until <see cref="RebuildIndexAsync"/> or the next time the solution opens. Added in contract 1.3
    /// (work item 2.15).
    /// </summary>
    /// <param name="indexId">The index id, from <see cref="IndexInfo.IndexId"/> or <see cref="IndexRootStatus.IndexId"/>.</param>
    /// <param name="cancellationToken">A token that cancels this call.</param>
    /// <returns><see langword="true"/> when the folder was deleted; <see langword="false"/> when there is no index with that id.</returns>
    Task<bool> DeleteIndexAsync(string indexId, CancellationToken cancellationToken);

    /// <summary>
    /// Lists every index folder on this machine with its disk use and whether its root still exists (the Index window's
    /// retention view). Added in contract 1.3 (work item 2.15).
    /// </summary>
    /// <param name="cancellationToken">A token that cancels this call.</param>
    /// <returns>One entry per index folder, ordered by root path.</returns>
    Task<IndexInfo[]> ListIndexesAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Tells the host what is open in Visual Studio: the solution and its projects, or an opened folder. The host resolves
    /// the repository roots and indexes them in the background (behind <c>features.indexing</c>); progress arrives as
    /// <c>indexStatusChanged</c> notifications. Call it after every handshake and whenever the solution, its projects or
    /// the opened folder change; an empty description closes the indexes. Added in contract 1.3 (work item 2.9).
    /// </summary>
    /// <param name="workspace">What is open.</param>
    /// <param name="cancellationToken">A token that cancels this call (not the indexing it starts).</param>
    /// <returns>A task that completes when the host has taken the workspace (indexing continues in the background).</returns>
    Task SetWorkspaceAsync(WorkspaceDescription workspace, CancellationToken cancellationToken);
}
