namespace Pillion.Contracts;

/// <summary>Whether a chat request gets code retrieved from the index (design section 7, Context assembly). An integer on the wire. Added in contract 1.3.</summary>
public enum RetrievalMode
{
    /// <summary>The host decides: it retrieves when <c>retrieval</c> is on and an index is available, and the question is about the code.</summary>
    Auto,

    /// <summary>Always retrieve when an index is available.</summary>
    On,

    /// <summary>Never retrieve chunks for this request; pins, the current file and project rules still apply.</summary>
    Off,
}
