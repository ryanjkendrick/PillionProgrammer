using System;
using System.Runtime.Serialization;

namespace Pillion.Contracts;

/// <summary>The state of the workspace index.</summary>
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
}
