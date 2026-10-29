using System;
using System.Runtime.Serialization;

namespace Pillion.Contracts;

/// <summary>
/// The edits a symbol rename would make, without making them (<see cref="IWorkspaceQueryService.PlanRenameAsync"/>): the agent's
/// <c>rename_symbol</c> adds them to its changeset like any other edit, so they are reviewed and applied with it (decision of
/// 2026-10-24 in <c>docs/phase-3-plan.md</c>). Added in contract 1.4 (work item 3.5; implemented by 3.24, consumed by 3.10).
/// </summary>
[DataContract]
public sealed class RenamePlan
{
    /// <summary>Gets or sets a value indicating whether a symbol was found at the location and can be renamed.</summary>
    [DataMember(Order = 0)]
    public bool IsSuccess { get; set; }

    /// <summary>Gets or sets the symbol's display name, for the ledger.</summary>
    [DataMember(Order = 1)]
    public string SymbolName { get; set; } = string.Empty;

    /// <summary>Gets or sets the edits, one per occurrence, grouped by file in path order; each matches exactly once in its file.</summary>
    [DataMember(Order = 2)]
    public FileEdit[] Edits { get; set; } = Array.Empty<FileEdit>();

    /// <summary>Gets or sets the files among <see cref="Edits"/> that are sensitive paths (design section 8), which need explicit approval in every mode.</summary>
    [DataMember(Order = 3)]
    public string[] SensitivePaths { get; set; } = Array.Empty<string>();

    /// <summary>Gets or sets the conflicts Roslyn reported (a name that already exists in scope, an overload that changes meaning); the agent must resolve or accept them.</summary>
    [DataMember(Order = 4)]
    public RenameConflict[] Conflicts { get; set; } = Array.Empty<RenameConflict>();

    /// <summary>Gets or sets why the plan could not be made, or <see langword="null"/>.</summary>
    [DataMember(Order = 5)]
    public string? Message { get; set; }

    /// <summary>Gets or sets a value indicating whether the 2 s budget stopped the search; the edits may be incomplete.</summary>
    [DataMember(Order = 6)]
    public bool Truncated { get; set; }
}

/// <summary>One conflict a rename would cause. Added in contract 1.4.</summary>
[DataContract]
public sealed class RenameConflict
{
    /// <summary>Gets or sets where the conflict is.</summary>
    [DataMember(Order = 0)]
    public SourceLocation Location { get; set; } = new SourceLocation();

    /// <summary>Gets or sets Roslyn's description of the conflict.</summary>
    [DataMember(Order = 1)]
    public string Message { get; set; } = string.Empty;
}
