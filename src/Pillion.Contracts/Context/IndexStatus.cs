using System;
using System.Runtime.Serialization;

namespace Pillion.Contracts;

/// <summary>
/// The state of the workspace index: returned by <see cref="IContextService.GetIndexStatusAsync"/> and sent by the host in
/// the <c>indexStatusChanged</c> notification. Members from <see cref="Phase"/> on were added in contract 1.3 (work items
/// 2.9 and 2.15); a 1.2 host leaves them at their defaults.
/// </summary>
/// <remarks>
/// <see cref="IndexedFileCount"/> and <see cref="TotalFileCount"/> are the totals over every root of the open solution, as in
/// 1.2; <see cref="Progress"/> describes the run in progress and <see cref="Roots"/> each root's index.
/// </remarks>
[DataContract]
public sealed class IndexStatus
{
    /// <summary>Gets or sets a value indicating whether indexing is in progress.</summary>
    [DataMember(Order = 0)]
    public bool IsIndexing { get; set; }

    /// <summary>Gets or sets a value indicating whether indexing is paused.</summary>
    [DataMember(Order = 1)]
    public bool IsPaused { get; set; }

    /// <summary>Gets or sets the number of files indexed so far.</summary>
    [DataMember(Order = 2)]
    public int IndexedFileCount { get; set; }

    /// <summary>Gets or sets the number of files to index.</summary>
    [DataMember(Order = 3)]
    public int TotalFileCount { get; set; }

    /// <summary>Gets or sets when the index was last updated, in UTC.</summary>
    [DataMember(Order = 4)]
    public DateTime? LastUpdatedUtc { get; set; }

    /// <summary>Gets or sets the step the indexer is in; <see cref="IndexPhase.Idle"/> when it has nothing to do (or there is no index).</summary>
    [DataMember(Order = 5)]
    public IndexPhase Phase { get; set; }

    /// <summary>Gets or sets why indexing is paused, when <see cref="Phase"/> is <see cref="IndexPhase.Paused"/>; otherwise <see cref="IndexPauseReason.None"/>.</summary>
    [DataMember(Order = 6)]
    public IndexPauseReason PauseReason { get; set; }

    /// <summary>Gets or sets one entry per repository root of the open solution or folder; empty when nothing is open or indexing is off.</summary>
    [DataMember(Order = 7)]
    public IndexRootStatus[] Roots { get; set; } = [];

    /// <summary>Gets or sets the number of secret-bearing files that were excluded, over every root (the Index window lists them).</summary>
    [DataMember(Order = 8)]
    public int ExcludedSecretFileCount { get; set; }

    /// <summary>Gets or sets the last error the indexer met, for the Index window, or <see langword="null"/>; set while <see cref="Phase"/> is <see cref="IndexPhase.Failed"/>.</summary>
    [DataMember(Order = 9)]
    public string? LastError { get; set; }

    /// <summary>Gets or sets the progress of the run in progress, or <see langword="null"/> when the indexer is idle.</summary>
    [DataMember(Order = 10)]
    public IndexProgress? Progress { get; set; }
}
