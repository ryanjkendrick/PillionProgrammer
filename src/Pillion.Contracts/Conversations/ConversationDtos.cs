using System;
using System.Runtime.Serialization;

namespace Pillion.Contracts;

/// <summary>Asks for one page of the conversation list (newest first). Added in contract 1.3.</summary>
[DataContract]
public sealed class ConversationListRequest
{
    /// <summary>Gets or sets how many conversations to skip from the most recently updated one.</summary>
    [DataMember(Order = 0)]
    public int Offset { get; set; }

    /// <summary>Gets or sets the most conversations to return; 0 or less means 50, and the host caps it at 500.</summary>
    [DataMember(Order = 1)]
    public int Limit { get; set; }
}

/// <summary>One page of the conversation list. Added in contract 1.3.</summary>
[DataContract]
public sealed class ConversationPage
{
    /// <summary>Gets or sets the conversations on this page, most recently updated first.</summary>
    [DataMember(Order = 0)]
    public ConversationSummary[] Items { get; set; } = [];

    /// <summary>Gets or sets a value indicating whether more conversations follow this page.</summary>
    [DataMember(Order = 1)]
    public bool HasMore { get; set; }
}

/// <summary>A conversation as the conversation list shows it. Added in contract 1.3.</summary>
[DataContract]
public sealed class ConversationSummary
{
    /// <summary>Gets or sets the conversation id (the <see cref="ChatRequest.ConversationId"/> of its chat requests).</summary>
    [DataMember(Order = 0)]
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets the title shown in the list.</summary>
    [DataMember(Order = 1)]
    public string Title { get; set; } = string.Empty;

    /// <summary>Gets or sets when the conversation was started, in UTC.</summary>
    [DataMember(Order = 2)]
    public DateTime CreatedUtc { get; set; }

    /// <summary>Gets or sets when the conversation last changed, in UTC; a new message moves it forward.</summary>
    [DataMember(Order = 3)]
    public DateTime UpdatedUtc { get; set; }

    /// <summary>Gets or sets the id of the model the conversation last used (<c>providerId/modelId</c>), or <see langword="null"/> when none has answered yet.</summary>
    [DataMember(Order = 4)]
    public string? LastModelId { get; set; }

    /// <summary>Gets or sets the number of messages in the conversation.</summary>
    [DataMember(Order = 5)]
    public int MessageCount { get; set; }
}

/// <summary>A conversation with its messages and pins. Added in contract 1.3.</summary>
[DataContract]
public sealed class ConversationDetail
{
    /// <summary>Gets or sets the conversation.</summary>
    [DataMember(Order = 0)]
    public ConversationSummary Conversation { get; set; } = new();

    /// <summary>Gets or sets the messages, oldest first.</summary>
    [DataMember(Order = 1)]
    public ConversationMessage[] Messages { get; set; } = [];

    /// <summary>Gets or sets the conversation's pins, in the order the Chat window shows them.</summary>
    [DataMember(Order = 2)]
    public ConversationPin[] Pins { get; set; } = [];
}

/// <summary>A stored message of a conversation. Added in contract 1.3.</summary>
[DataContract]
public sealed class ConversationMessage
{
    /// <summary>Gets or sets the message id, increasing in the order messages were added.</summary>
    [DataMember(Order = 0)]
    public long Id { get; set; }

    /// <summary>Gets or sets the author.</summary>
    [DataMember(Order = 1)]
    public ChatRole Role { get; set; }

    /// <summary>Gets or sets the message text.</summary>
    [DataMember(Order = 2)]
    public string Content { get; set; } = string.Empty;

    /// <summary>Gets or sets the provider's id of the model that wrote an assistant message, or <see langword="null"/>.</summary>
    [DataMember(Order = 3)]
    public string? ModelId { get; set; }

    /// <summary>Gets or sets the provider that served an assistant message, or <see langword="null"/>.</summary>
    [DataMember(Order = 4)]
    public string? ProviderId { get; set; }

    /// <summary>Gets or sets the prompt tokens of the request that produced an assistant message (0 when unknown).</summary>
    [DataMember(Order = 5)]
    public int PromptTokens { get; set; }

    /// <summary>Gets or sets the tokens generated for an assistant message (0 when unknown).</summary>
    [DataMember(Order = 6)]
    public int CompletionTokens { get; set; }

    /// <summary>Gets or sets when the message was written, in UTC.</summary>
    [DataMember(Order = 7)]
    public DateTime CreatedUtc { get; set; }

    /// <summary>Gets or sets the citations of an assistant reply that used retrieved code (as its final <see cref="ChatChunk.Citations"/>), so a restored reply keeps its clickable spans.</summary>
    [DataMember(Order = 8)]
    public Citation[] Citations { get; set; } = [];
}

/// <summary>
/// A context pin kept with a conversation. The host stores what to resolve again, not the content: on restore the Chat
/// window resolves <c>@Kind:Target</c> through <see cref="IContextService.ResolveMentionAsync"/>. A pin of client-only text
/// (<c>selection</c>, <c>clipboard</c>, <c>errors</c>) keeps no content and is shown as stale. Added in contract 1.3.
/// </summary>
[DataContract]
public sealed class ConversationPin
{
    /// <summary>Gets or sets what is pinned, one of the <see cref="MentionKinds"/> (as <see cref="MentionResolution.Kind"/>).</summary>
    [DataMember(Order = 0)]
    public string Kind { get; set; } = string.Empty;

    /// <summary>Gets or sets what the pin points at: a full path for <c>file</c> and <c>folder</c>, a symbol name for <c>symbol</c>, otherwise the mention's value.</summary>
    [DataMember(Order = 1)]
    public string Target { get; set; } = string.Empty;

    /// <summary>Gets or sets the short name shown on the pin badge.</summary>
    [DataMember(Order = 2)]
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Gets or sets the pin's size in tokens when it was pinned.</summary>
    [DataMember(Order = 3)]
    public int TokenCount { get; set; }
}

/// <summary>Creates a conversation. Added in contract 1.3.</summary>
[DataContract]
public sealed class ConversationCreateRequest
{
    /// <summary>
    /// Gets or sets the id to give the conversation, normally the <see cref="ChatRequest.ConversationId"/> the Chat window
    /// already uses; empty means the host chooses one.
    /// </summary>
    [DataMember(Order = 0)]
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets the title; empty means the host names it from the first user message.</summary>
    [DataMember(Order = 1)]
    public string Title { get; set; } = string.Empty;

    /// <summary>Gets or sets the model the conversation starts with (<c>providerId/modelId</c>), or <see langword="null"/>.</summary>
    [DataMember(Order = 2)]
    public string? ModelId { get; set; }
}

/// <summary>What <see cref="IConversationService.DeleteAllConversationsAsync"/> deleted. Added in contract 1.3.</summary>
[DataContract]
public sealed class DeleteAllConversationsResult
{
    /// <summary>Gets or sets the number of conversations deleted.</summary>
    [DataMember(Order = 0)]
    public int Conversations { get; set; }

    /// <summary>Gets or sets the number of messages deleted.</summary>
    [DataMember(Order = 1)]
    public int Messages { get; set; }

    /// <summary>Gets or sets the number of pins deleted.</summary>
    [DataMember(Order = 2)]
    public int Pins { get; set; }

    /// <summary>Gets or sets the number of Outbound log entries deleted.</summary>
    [DataMember(Order = 3)]
    public int OutboundEntries { get; set; }
}
