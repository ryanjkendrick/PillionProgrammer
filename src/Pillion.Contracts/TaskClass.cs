namespace Pillion.Contracts;

/// <summary>The kind of work a model request performs; used to route the request to a suitable model.</summary>
public enum TaskClass
{
    /// <summary>Inline (ghost text) code completion.</summary>
    Autocomplete,

    /// <summary>An edit applied to a selection or the code around the caret.</summary>
    InlineEdit,

    /// <summary>A conversational chat turn.</summary>
    Chat,

    /// <summary>A step in an autonomous agent session.</summary>
    Agent,

    /// <summary>Computing embeddings for indexing or search.</summary>
    Embedding,

    /// <summary>A request that includes images.</summary>
    Vision,
}
