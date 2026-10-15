using System.Threading;
using System.Threading.Tasks;

namespace Pillion.Contracts;

/// <summary>
/// The Chat window's conversations, kept by the host in the encrypted <c>conversations.db</c> (design sections 5 and 10),
/// implemented by the host and called by the VSIX (work item 2.2). Added in contract 1.3; a 1.2 host answers it with
/// StreamJsonRpc's method-not-found error.
/// </summary>
/// <remarks>
/// <para>
/// There is no method to append a message: the host appends the user's message and the reply itself as
/// <see cref="IModelService.ChatAsync"/> runs a request whose <see cref="ChatRequest.ConversationId"/> names a conversation.
/// </para>
/// <para>
/// Every method needs the <c>localStore</c> feature flag; while it is off they fail with
/// <see cref="PillionErrorCode.PolicyDenied"/> and a message that names the flag. A store that cannot be opened or used
/// fails them with <see cref="PillionErrorCode.StorageUnavailable"/>.
/// </para>
/// </remarks>
public interface IConversationService
{
    /// <summary>Lists conversations, most recently updated first, one page at a time.</summary>
    /// <param name="request">The page to return.</param>
    /// <param name="cancellationToken">A token that cancels this call.</param>
    /// <returns>The page.</returns>
    Task<ConversationPage> ListConversationsAsync(ConversationListRequest request, CancellationToken cancellationToken);

    /// <summary>Gets a conversation with its messages and pins.</summary>
    /// <param name="conversationId">The conversation id.</param>
    /// <param name="cancellationToken">A token that cancels this call.</param>
    /// <returns>The conversation, or <see langword="null"/> when there is none with that id.</returns>
    Task<ConversationDetail?> GetConversationAsync(string conversationId, CancellationToken cancellationToken);

    /// <summary>Creates a conversation. Creating one with the id of an existing conversation returns that conversation unchanged.</summary>
    /// <param name="request">The conversation to create.</param>
    /// <param name="cancellationToken">A token that cancels this call.</param>
    /// <returns>The conversation as stored.</returns>
    Task<ConversationSummary> CreateConversationAsync(ConversationCreateRequest request, CancellationToken cancellationToken);

    /// <summary>Renames a conversation.</summary>
    /// <param name="conversationId">The conversation id.</param>
    /// <param name="title">The new title; it is trimmed, and an empty one leaves the title as it was.</param>
    /// <param name="cancellationToken">A token that cancels this call.</param>
    /// <returns><see langword="true"/> when the conversation was renamed; <see langword="false"/> when there is none with that id.</returns>
    Task<bool> RenameConversationAsync(string conversationId, string title, CancellationToken cancellationToken);

    /// <summary>Deletes a conversation with its messages and pins.</summary>
    /// <param name="conversationId">The conversation id.</param>
    /// <param name="cancellationToken">A token that cancels this call.</param>
    /// <returns><see langword="true"/> when a conversation was deleted; <see langword="false"/> when there is none with that id.</returns>
    Task<bool> DeleteConversationAsync(string conversationId, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes every conversation, message and pin and the whole Outbound log (Delete all on the Privacy page), then
    /// compacts the database so the freed pages are gone too.
    /// </summary>
    /// <param name="cancellationToken">A token that cancels this call before the delete is committed.</param>
    /// <returns>What was deleted.</returns>
    Task<DeleteAllConversationsResult> DeleteAllConversationsAsync(CancellationToken cancellationToken);

    /// <summary>Replaces the pins of a conversation (an empty array removes them all).</summary>
    /// <param name="conversationId">The conversation id.</param>
    /// <param name="pins">The pins, in the order the Chat window shows them; a repeated kind and target keeps the last.</param>
    /// <param name="cancellationToken">A token that cancels this call.</param>
    /// <returns><see langword="true"/> when the pins were stored; <see langword="false"/> when there is no conversation with that id.</returns>
    Task<bool> SetPinsAsync(string conversationId, ConversationPin[] pins, CancellationToken cancellationToken);
}
