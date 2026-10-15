namespace Pillion.LocalStore.Conversations;

/// <summary>
/// Typed access to <c>conversations.db</c>: conversations, their messages and pins, and the Outbound log. Every call is
/// asynchronous and cancellable; writes are serialised through the database's single writer.
/// </summary>
public interface IConversationRepository
{
    /// <summary>Creates a conversation, or updates its title, model and updated time when it exists (its created time is kept).</summary>
    /// <param name="conversation">The conversation.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task that completes when it is stored.</returns>
    Task SaveConversationAsync(ConversationRecord conversation, CancellationToken cancellationToken = default);

    /// <summary>Gets a conversation.</summary>
    /// <param name="id">The conversation id.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The conversation, or <see langword="null"/> when there is none with that id.</returns>
    Task<ConversationRecord?> GetConversationAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>Lists conversations, most recently updated first.</summary>
    /// <param name="limit">The most to return.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The conversations.</returns>
    Task<IReadOnlyList<ConversationRecord>> ListConversationsAsync(int limit = 100, CancellationToken cancellationToken = default);

    /// <summary>Deletes a conversation with its messages and pins.</summary>
    /// <param name="id">The conversation id.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns><see langword="true"/> when a conversation was deleted.</returns>
    Task<bool> DeleteConversationAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>Adds a message and moves the conversation's updated time forward to the message's.</summary>
    /// <param name="message">The message.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The stored message with its id.</returns>
    /// <exception cref="KeyNotFoundException">The conversation does not exist.</exception>
    Task<MessageRecord> AddMessageAsync(NewMessage message, CancellationToken cancellationToken = default);

    /// <summary>Gets the messages of a conversation in the order they were added.</summary>
    /// <param name="conversationId">The conversation id.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The messages; empty for an unknown conversation.</returns>
    Task<IReadOnlyList<MessageRecord>> GetMessagesAsync(string conversationId, CancellationToken cancellationToken = default);

    /// <summary>Replaces the pins of a conversation.</summary>
    /// <param name="conversationId">The conversation id; it must exist.</param>
    /// <param name="pins">The pins; a repeated kind and target keeps the last.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task that completes when they are stored.</returns>
    /// <exception cref="KeyNotFoundException">The conversation does not exist.</exception>
    Task ReplacePinsAsync(string conversationId, IReadOnlyList<PinRecord> pins, CancellationToken cancellationToken = default);

    /// <summary>Gets the pins of a conversation, ordered by kind and target.</summary>
    /// <param name="conversationId">The conversation id.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The pins; empty for an unknown conversation.</returns>
    Task<IReadOnlyList<PinRecord>> GetPinsAsync(string conversationId, CancellationToken cancellationToken = default);

    /// <summary>Appends an entry to the Outbound log.</summary>
    /// <param name="entry">The entry; its <see cref="OutboundLogEntry.Id"/> is ignored.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The stored entry's id.</returns>
    Task<long> AppendOutboundAsync(OutboundLogEntry entry, CancellationToken cancellationToken = default);

    /// <summary>Gets the most recent Outbound log entries, oldest first.</summary>
    /// <param name="limit">The most to return.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The entries.</returns>
    Task<IReadOnlyList<OutboundLogEntry>> GetRecentOutboundAsync(int limit = 500, CancellationToken cancellationToken = default);

    /// <summary>Deletes conversations last updated before a time, with their messages and pins.</summary>
    /// <param name="cutoff">Conversations updated before this are deleted.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The number of conversations deleted.</returns>
    Task<int> DeleteConversationsUpdatedBeforeAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default);

    /// <summary>Deletes Outbound log entries recorded before a time.</summary>
    /// <param name="cutoff">Entries before this are deleted.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The number of entries deleted.</returns>
    Task<int> DeleteOutboundBeforeAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes everything the store holds (Delete all in Options): every conversation, message and pin, the whole
    /// Outbound log, and the backups that migrations made; then compacts the file so the freed pages are gone too.
    /// </summary>
    /// <param name="cancellationToken">Cancels the call before the delete is committed.</param>
    /// <returns>What was deleted.</returns>
    Task<DeleteAllResult> DeleteAllAsync(CancellationToken cancellationToken = default);
}

/// <summary>What <see cref="IConversationRepository.DeleteAllAsync"/> deleted.</summary>
/// <param name="Conversations">Conversations deleted.</param>
/// <param name="Messages">Messages deleted.</param>
/// <param name="Pins">Pins deleted.</param>
/// <param name="OutboundEntries">Outbound log entries deleted.</param>
/// <param name="BackupFiles">Migration backup files deleted.</param>
public sealed record DeleteAllResult(int Conversations, int Messages, int Pins, int OutboundEntries, int BackupFiles);
