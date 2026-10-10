using System.Runtime.Serialization;

namespace Pillion.Contracts;

/// <summary>
/// The context a chat request would be assembled into, without calling a model (the Context Inspector, design section 7):
/// every item in prompt order with its token count and whether it was truncated or dropped, and the totals against the
/// budget. Names and notes only; never the content.
/// </summary>
[DataContract]
public sealed class ContextReportDto
{
    /// <summary>Gets or sets the model the request would be routed to (the provider's model id).</summary>
    [DataMember(Order = 0)]
    public string ModelId { get; set; } = string.Empty;

    /// <summary>Gets or sets the provider of that model.</summary>
    [DataMember(Order = 1)]
    public string ProviderId { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether that model runs on this machine.</summary>
    [DataMember(Order = 2)]
    public bool IsLocal { get; set; }

    /// <summary>Gets or sets the model's usable context window in tokens.</summary>
    [DataMember(Order = 3)]
    public int ContextWindow { get; set; }

    /// <summary>Gets or sets the tokens kept free for the answer.</summary>
    [DataMember(Order = 4)]
    public int OutputReserve { get; set; }

    /// <summary>Gets or sets the tokens available for context: the window minus the reserve minus the system prompt.</summary>
    [DataMember(Order = 5)]
    public int BudgetTokens { get; set; }

    /// <summary>Gets or sets the tokens of everything that would go into the prompt.</summary>
    [DataMember(Order = 6)]
    public int TotalTokens { get; set; }

    /// <summary>Gets or sets the items, in prompt order; dropped items follow the section they would have joined.</summary>
    [DataMember(Order = 7)]
    public ContextReportItemDto[] Items { get; set; } = [];

    /// <summary>
    /// Gets or sets a value indicating whether the request goes through context assembly when sent (it carries pins, a
    /// current file or project rules). A plain request is sent as it is; its preview is still assembled to show the sizes.
    /// </summary>
    [DataMember(Order = 8)]
    public bool IsAssembled { get; set; }

    /// <summary>Gets or sets a value indicating whether secrets would be replaced by placeholders for this model (a cloud model, or redaction for local models switched on).</summary>
    [DataMember(Order = 9)]
    public bool Redacts { get; set; }

    /// <summary>Gets or sets the number of secrets the scanner finds in the items that would be sent, when <see cref="Redacts"/> is set; else 0.</summary>
    [DataMember(Order = 10)]
    public int RedactionCount { get; set; }
}

/// <summary>One line of a <see cref="ContextReportDto"/>.</summary>
[DataContract]
public sealed class ContextReportItemDto
{
    /// <summary>Gets or sets what the item is: <c>system</c>, <c>rules</c>, <c>repoMap</c>, <c>pin</c>, <c>currentFile</c>, <c>retrieved</c>, <c>summary</c>, <c>history</c> or <c>query</c>.</summary>
    [DataMember(Order = 0)]
    public string Kind { get; set; } = string.Empty;

    /// <summary>Gets or sets the name to show (a file name, <c>Project rules</c>, <c>Your message</c>, ...).</summary>
    [DataMember(Order = 1)]
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the tokens the item takes in the prompt (0 when dropped).</summary>
    [DataMember(Order = 2)]
    public int Tokens { get; set; }

    /// <summary>Gets or sets the tokens it would take unshortened.</summary>
    [DataMember(Order = 3)]
    public int OriginalTokens { get; set; }

    /// <summary>Gets or sets a value indicating whether part of it was cut.</summary>
    [DataMember(Order = 4)]
    public bool Truncated { get; set; }

    /// <summary>Gets or sets a value indicating whether all of it was left out.</summary>
    [DataMember(Order = 5)]
    public bool Dropped { get; set; }

    /// <summary>Gets or sets why it was truncated or dropped; empty otherwise.</summary>
    [DataMember(Order = 6)]
    public string Note { get; set; } = string.Empty;

    /// <summary>Gets or sets, for a <c>pin</c> item, the index of the pin in <see cref="ChatRequest.Pins"/>; -1 for every other item.</summary>
    [DataMember(Order = 7)]
    public int PinIndex { get; set; } = -1;

    /// <summary>Gets or sets the number of secrets the scanner finds in the item when <see cref="ContextReportDto.Redacts"/> is set; else 0.</summary>
    [DataMember(Order = 8)]
    public int RedactionCount { get; set; }
}
