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

    /// <summary>Compressing conversation history into a rolling summary (context assembly); routed to Fast online and Local-fast offline.</summary>
    Summarise,

    /// <summary>
    /// An agent session's planning turn (the plan, re-planning after a failure), routed to Frontier online and Local-strong
    /// offline; <see cref="Agent"/> keeps its meaning of an agent step (Balanced). Appended in contract 1.4 (work item 3.5) so every
    /// earlier wire value is kept.
    /// </summary>
    AgentPlanning,
}
