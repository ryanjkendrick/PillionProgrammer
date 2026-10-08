using System;

namespace Pillion.Contracts;

/// <summary>What a model can do.</summary>
[Flags]
public enum ModelCapabilities
{
    /// <summary>No capabilities.</summary>
    None = 0,

    /// <summary>Conversational chat.</summary>
    Chat = 1,

    /// <summary>Fill-in-the-middle completion.</summary>
    Fim = 2,

    /// <summary>Image input.</summary>
    Vision = 4,

    /// <summary>Tool (function) calling.</summary>
    Tools = 8,

    /// <summary>Embedding generation.</summary>
    Embeddings = 16,
}
