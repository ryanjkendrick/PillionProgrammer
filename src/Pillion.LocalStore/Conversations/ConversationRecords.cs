using Pillion.Contracts;

namespace Pillion.LocalStore.Conversations;

/// <summary>A conversation in the Chat window.</summary>
/// <param name="Id">The conversation id (the <c>ConversationId</c> the VSIX sends with each chat request).</param>
/// <param name="Title">The title shown in the conversation list.</param>
/// <param name="Created">When the conversation was started.</param>
/// <param name="Updated">When it last changed; adding a message moves it forward.</param>
/// <param name="Model">The model the conversation last used, if any.</param>
public sealed record ConversationRecord(string Id, string Title, DateTimeOffset Created, DateTimeOffset Updated, string? Model = null);

/// <summary>A message to add to a conversation.</summary>
/// <param name="ConversationId">The conversation; it must exist.</param>
/// <param name="Role">The author: <c>user</c>, <c>assistant</c>, <c>system</c> or <c>tool</c>.</param>
/// <param name="Content">The message text.</param>
/// <param name="Created">When the message was written.</param>
/// <param name="Model">The model that wrote an assistant message.</param>
/// <param name="Provider">The provider that served it.</param>
/// <param name="TokensIn">The prompt tokens of the request that produced it.</param>
/// <param name="TokensOut">The tokens generated.</param>
/// <param name="Cost">The estimated cost in US dollars, when known.</param>
/// <param name="Citations">The citations of an assistant reply as JSON, or <see langword="null"/> when there are none.</param>
/// <param name="Cancelled">Whether the reply was stopped before it finished (<paramref name="Content"/> is what had arrived).</param>
public sealed record NewMessage(
    string ConversationId,
    string Role,
    string Content,
    DateTimeOffset Created,
    string? Model = null,
    string? Provider = null,
    int? TokensIn = null,
    int? TokensOut = null,
    double? Cost = null,
    string? Citations = null,
    bool Cancelled = false);

/// <summary>A stored message.</summary>
/// <param name="Id">The message id, increasing in the order messages were added.</param>
/// <param name="ConversationId">The conversation.</param>
/// <param name="Role">The author: <c>user</c>, <c>assistant</c>, <c>system</c> or <c>tool</c>.</param>
/// <param name="Content">The message text.</param>
/// <param name="Created">When the message was written.</param>
/// <param name="Model">The model that wrote an assistant message.</param>
/// <param name="Provider">The provider that served it.</param>
/// <param name="TokensIn">The prompt tokens of the request that produced it.</param>
/// <param name="TokensOut">The tokens generated.</param>
/// <param name="Cost">The estimated cost in US dollars, when known.</param>
/// <param name="Citations">The citations of an assistant reply as JSON, or <see langword="null"/> when there are none.</param>
/// <param name="Cancelled">Whether the reply was stopped before it finished.</param>
public sealed record MessageRecord(
    long Id,
    string ConversationId,
    string Role,
    string Content,
    DateTimeOffset Created,
    string? Model,
    string? Provider,
    int? TokensIn,
    int? TokensOut,
    double? Cost,
    string? Citations = null,
    bool Cancelled = false);

/// <summary>A context pin of a conversation (an <c>@</c>-mention kept with it).</summary>
/// <param name="Kind">What is pinned: <c>file</c>, <c>folder</c>, <c>selection</c>, <c>errors</c> or <c>clipboard</c> (as <c>MentionResolution.Kind</c>).</param>
/// <param name="Target">What it points at, for example a repository-relative path.</param>
/// <param name="TokenCount">Its size in tokens when it was pinned.</param>
/// <param name="DisplayName">The short name on the pin's badge; empty when none was stored.</param>
public sealed record PinRecord(string Kind, string Target, int TokenCount, string DisplayName = "");

/// <summary>A conversation as the list shows it: the conversation and how many messages it has.</summary>
/// <param name="Conversation">The conversation.</param>
/// <param name="MessageCount">The number of messages.</param>
public sealed record ConversationListItem(ConversationRecord Conversation, int MessageCount);

/// <summary>
/// One request sent to a model provider, as the Outbound log keeps it on disk (design section 12, Data flows). The fields
/// mirror the host's in-memory <c>OutboundRecord</c>, so it can be persisted as is. Counts and durations only: it never
/// holds prompt or completion text.
/// </summary>
/// <param name="Timestamp">When the request was sent.</param>
/// <param name="RequestId">The caller's request id.</param>
/// <param name="ProviderId">The provider.</param>
/// <param name="ModelId">The model.</param>
/// <param name="TaskClass">The task class.</param>
/// <param name="IsLocal">Whether the provider runs on this machine (stored as the locality <c>local</c> or <c>cloud</c>).</param>
/// <param name="PromptTokens">The prompt size in tokens (0 for a cache hit).</param>
/// <param name="CompletionTokens">The tokens generated.</param>
/// <param name="RedactionCount">The number of secrets replaced by placeholders.</param>
/// <param name="Duration">How long the request took.</param>
/// <param name="Outcome">How it ended: <c>Succeeded</c>, <c>Failed</c>, <c>Cancelled</c>, <c>TimedOut</c> or <c>Dropped</c> (the gateway's <c>OutboundOutcome</c> names).</param>
/// <param name="ErrorCode">The error code when it failed.</param>
/// <param name="PromptTokensCounted">Whether <paramref name="PromptTokens"/> was counted with the model's tokenizer rather than estimated.</param>
/// <param name="CacheHit">Whether an autocomplete request was served from the cache (nothing was sent).</param>
/// <param name="TimeToFirstToken">How long the first token took, when one arrived.</param>
/// <param name="ContextItems">The number of items in the assembled context; 0 when the request was not assembled.</param>
/// <param name="ContextTrimmed">How many of those items were truncated or dropped.</param>
/// <param name="Spans">The files and line spans included (design section 10), as JSON, when recorded.</param>
public sealed record OutboundLogEntry(
    DateTimeOffset Timestamp,
    string RequestId,
    string ProviderId,
    string ModelId,
    TaskClass TaskClass,
    bool IsLocal,
    int PromptTokens,
    int CompletionTokens,
    int RedactionCount,
    TimeSpan Duration,
    string Outcome,
    PillionErrorCode? ErrorCode = null,
    bool PromptTokensCounted = false,
    bool CacheHit = false,
    TimeSpan? TimeToFirstToken = null,
    int ContextItems = 0,
    int ContextTrimmed = 0,
    string? Spans = null)
{
    /// <summary>Gets the row id, increasing in the order entries were appended; 0 for an entry not read from the store.</summary>
    public long Id { get; init; }
}
