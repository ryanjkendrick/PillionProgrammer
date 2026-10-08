namespace Pillion.Contracts;

/// <summary>The author of a <see cref="ChatMessage"/>.</summary>
public enum ChatRole
{
    /// <summary>Instructions that frame the conversation.</summary>
    System,

    /// <summary>A message written by the user.</summary>
    User,

    /// <summary>A message written by the model.</summary>
    Assistant,

    /// <summary>The output of a tool call.</summary>
    Tool,
}
