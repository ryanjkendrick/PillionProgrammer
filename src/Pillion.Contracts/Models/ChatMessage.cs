using System.Runtime.Serialization;

namespace Pillion.Contracts;

/// <summary>A single message in a chat conversation.</summary>
[DataContract]
public sealed class ChatMessage
{
    /// <summary>Gets or sets the author of the message.</summary>
    [DataMember(Order = 0)]
    public ChatRole Role { get; set; }

    /// <summary>Gets or sets the message text.</summary>
    [DataMember(Order = 1)]
    public string Content { get; set; } = string.Empty;
}
