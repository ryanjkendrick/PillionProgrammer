using System.Runtime.Serialization;

namespace Pillion.Contracts;

/// <summary>Names the symbol whose callers <see cref="IWorkspaceQueryService.CallersOfAsync"/> finds. Added in contract 1.3 (work item 2.14).</summary>
[DataContract]
public sealed class CallersOfRequest
{
    /// <summary>Gets or sets the symbol's simple name, for example <c>PlaceOrderAsync</c>.</summary>
    [DataMember(Order = 0)]
    public string SymbolName { get; set; } = string.Empty;

    /// <summary>Gets or sets the containing type or namespace, qualified as far as known (for example <c>Shop.Orders.OrderService</c>), or empty.</summary>
    [DataMember(Order = 1)]
    public string Container { get; set; } = string.Empty;

    /// <summary>Gets or sets the full path of a file that declares the symbol, or empty when unknown (the name and container are then searched).</summary>
    [DataMember(Order = 2)]
    public string FilePath { get; set; } = string.Empty;

    /// <summary>Gets or sets the zero-based line of the declaration in <see cref="FilePath"/>, or -1 when unknown.</summary>
    [DataMember(Order = 3)]
    public int Line { get; set; } = -1;

    /// <summary>Gets or sets the most call sites to return; 0 means 100.</summary>
    [DataMember(Order = 4)]
    public int MaxResults { get; set; }
}

/// <summary>The call sites <see cref="IWorkspaceQueryService.CallersOfAsync"/> found. Added in contract 1.3 (work item 2.14).</summary>
[DataContract]
public sealed class CallersOfResponse
{
    /// <summary>Gets or sets a value indicating whether the symbol was found.</summary>
    [DataMember(Order = 0)]
    public bool IsFound { get; set; }

    /// <summary>Gets or sets the call sites, ordered by file and line.</summary>
    [DataMember(Order = 1)]
    public CallSite[] Callers { get; set; } = [];

    /// <summary>Gets or sets a value indicating whether more call sites exist than were returned (the result cap or the 2 s budget cut the search short).</summary>
    [DataMember(Order = 2)]
    public bool Truncated { get; set; }
}

/// <summary>One call of a symbol. Added in contract 1.3 (work item 2.14).</summary>
[DataContract]
public sealed class CallSite
{
    /// <summary>Gets or sets the full path of the file with the call.</summary>
    [DataMember(Order = 0)]
    public string FilePath { get; set; } = string.Empty;

    /// <summary>Gets or sets the zero-based line of the call.</summary>
    [DataMember(Order = 1)]
    public int Line { get; set; }

    /// <summary>Gets or sets the text of the line with the call, trimmed.</summary>
    [DataMember(Order = 2)]
    public string Snippet { get; set; } = string.Empty;

    /// <summary>Gets or sets the qualified name of the member that makes the call, or empty at file scope.</summary>
    [DataMember(Order = 3)]
    public string CallerName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether the call site is approximate: matched by name (the index's edges, or a
    /// document Roslyn could not bind) rather than bound to the symbol by the compiler.
    /// </summary>
    [DataMember(Order = 4)]
    public bool IsApproximate { get; set; }
}
