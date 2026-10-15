using System;
using System.Runtime.Serialization;

namespace Pillion.Contracts;

/// <summary>The step the indexing pipeline is in (design section 7, Indexing pipeline). An integer on the wire. Added in contract 1.3.</summary>
public enum IndexPhase
{
    /// <summary>Nothing to do: the index is up to date, or there is no index.</summary>
    Idle,

    /// <summary>Walking the repository roots and applying the exclusions.</summary>
    Discovering,

    /// <summary>Hashing files to find the ones that changed.</summary>
    Hashing,

    /// <summary>Parsing changed files into chunks and extracting their symbols.</summary>
    Chunking,

    /// <summary>Computing embeddings for new chunks.</summary>
    Embedding,

    /// <summary>Writing chunks, vectors and symbols to the index.</summary>
    Storing,

    /// <summary>Paused; <see cref="IndexStatus.PauseReason"/> says why.</summary>
    Paused,

    /// <summary>Stopped by an error; <see cref="IndexStatus.LastError"/> says which.</summary>
    Failed,
}

/// <summary>Why indexing is paused (design sections 7 and 13). An integer on the wire. Added in contract 1.3.</summary>
public enum IndexPauseReason
{
    /// <summary>Not paused.</summary>
    None,

    /// <summary>The user paused it (<see cref="IContextService.PauseIndexingAsync"/>).</summary>
    User,

    /// <summary>A build is running.</summary>
    Build,

    /// <summary>A test run is in progress.</summary>
    Test,

    /// <summary>The debugger is attached.</summary>
    Debug,

    /// <summary>Windows battery saver is on (and <c>indexing.pauseOnBattery</c> is set).</summary>
    Battery,

    /// <summary>The host reached its memory cap even after halving the embedding batch.</summary>
    Memory,

    /// <summary>Less than 500 MB of disk is free on the index's drive (design section 4, Failure modes).</summary>
    LowDisk,
}

/// <summary>Whether this host owns an index or reads one another host owns (design section 10, Index identity and ownership). An integer on the wire. Added in contract 1.3.</summary>
public enum IndexRole
{
    /// <summary>The index is not open yet (the root was discovered but its index has not been opened).</summary>
    Unknown,

    /// <summary>This host holds <c>writer.lock</c> and runs the indexer and the watcher.</summary>
    Writer,

    /// <summary>Another host owns the index; this one opens it read-only and forwards update requests to the owner.</summary>
    Reader,
}

/// <summary>The index of one repository root. Added in contract 1.3.</summary>
[DataContract]
public sealed class IndexRootStatus
{
    /// <summary>Gets or sets the full path of the repository root (the git top-level directory, or the opened folder).</summary>
    [DataMember(Order = 0)]
    public string RootPath { get; set; } = string.Empty;

    /// <summary>Gets or sets the index id (the folder name under <c>index\</c>); empty until the index is opened.</summary>
    [DataMember(Order = 1)]
    public string IndexId { get; set; } = string.Empty;

    /// <summary>Gets or sets whether this host owns the index or reads it.</summary>
    [DataMember(Order = 2)]
    public IndexRole Role { get; set; }

    /// <summary>Gets or sets the number of files indexed under this root.</summary>
    [DataMember(Order = 3)]
    public int IndexedFileCount { get; set; }

    /// <summary>Gets or sets the number of files found under this root that pass the exclusions (indexed or still to index).</summary>
    [DataMember(Order = 4)]
    public int TotalFileCount { get; set; }

    /// <summary>Gets or sets the number of files and folders excluded under this root (see <see cref="IContextService.ListExcludedFilesAsync"/>).</summary>
    [DataMember(Order = 5)]
    public int ExcludedFileCount { get; set; }

    /// <summary>Gets or sets how many of the excluded files are secret-bearing.</summary>
    [DataMember(Order = 6)]
    public int SecretFileCount { get; set; }

    /// <summary>Gets or sets the number of chunks in the index.</summary>
    [DataMember(Order = 7)]
    public long ChunkCount { get; set; }

    /// <summary>Gets or sets the disk use of the index folder, in bytes.</summary>
    [DataMember(Order = 8)]
    public long DiskBytes { get; set; }

    /// <summary>Gets or sets when the last full hash sweep finished, in UTC, or <see langword="null"/> when none has.</summary>
    [DataMember(Order = 9)]
    public DateTime? LastFullSweepUtc { get; set; }

    /// <summary>Gets or sets the id of the embedding model the index was built with.</summary>
    [DataMember(Order = 10)]
    public string EmbeddingModelId { get; set; } = string.Empty;

    /// <summary>Gets or sets the chunker version the index was built with.</summary>
    [DataMember(Order = 11)]
    public string ChunkerVersion { get; set; } = string.Empty;

    /// <summary>Gets or sets the schema version of the index database.</summary>
    [DataMember(Order = 12)]
    public int SchemaVersion { get; set; }
}

/// <summary>The progress of an indexing run. Added in contract 1.3.</summary>
[DataContract]
public sealed class IndexProgress
{
    /// <summary>Gets or sets the files this run has finished.</summary>
    [DataMember(Order = 0)]
    public int FilesDone { get; set; }

    /// <summary>Gets or sets the files this run has to process (it can grow while discovery is still running).</summary>
    [DataMember(Order = 1)]
    public int FilesTotal { get; set; }

    /// <summary>Gets or sets the chunks this run has stored.</summary>
    [DataMember(Order = 2)]
    public long ChunksDone { get; set; }

    /// <summary>Gets or sets the estimated seconds until the run finishes, or -1 when there is no estimate yet.</summary>
    [DataMember(Order = 3)]
    public int EstimatedRemainingSeconds { get; set; } = -1;
}

/// <summary>Asks for one page of the files a root's index excluded. Added in contract 1.3.</summary>
[DataContract]
public sealed class ExcludedFilesRequest
{
    /// <summary>Gets or sets the repository root (as <see cref="IndexRootStatus.RootPath"/>).</summary>
    [DataMember(Order = 0)]
    public string RootPath { get; set; } = string.Empty;

    /// <summary>Gets or sets how many entries to skip. Entries are ordered secret-bearing first, then by path.</summary>
    [DataMember(Order = 1)]
    public int Offset { get; set; }

    /// <summary>Gets or sets the most entries to return; 0 or less means 200, and the host caps it at 1,000.</summary>
    [DataMember(Order = 2)]
    public int Limit { get; set; }

    /// <summary>Gets or sets a value indicating whether to return only the secret-bearing files.</summary>
    [DataMember(Order = 3)]
    public bool SecretOnly { get; set; }
}

/// <summary>One page of excluded files. Added in contract 1.3.</summary>
[DataContract]
public sealed class ExcludedFilesPage
{
    /// <summary>Gets or sets the entries on this page.</summary>
    [DataMember(Order = 0)]
    public ExcludedFile[] Items { get; set; } = [];

    /// <summary>Gets or sets the number of entries that match the request, over every page.</summary>
    [DataMember(Order = 1)]
    public int TotalCount { get; set; }

    /// <summary>Gets or sets a value indicating whether more entries follow this page.</summary>
    [DataMember(Order = 2)]
    public bool HasMore { get; set; }
}

/// <summary>A file or folder that is not indexed, and why. Added in contract 1.3.</summary>
[DataContract]
public sealed class ExcludedFile
{
    /// <summary>Gets or sets the path relative to the root, with <c>/</c> separators.</summary>
    [DataMember(Order = 0)]
    public string Path { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the rule that excluded it, by name: <c>SecretBearing</c>, <c>GitIgnore</c>, <c>PillionIgnore</c>,
    /// <c>BuiltIn</c>, <c>UserExclude</c>, <c>GeneratedFile</c>, <c>MinifiedFile</c>, <c>LockFile</c>,
    /// <c>GeneratedHeader</c>, <c>Binary</c>, <c>TooLarge</c>, <c>ReparsePoint</c>, <c>NestedRepository</c> or
    /// <c>Unreadable</c> (a string, so a new rule needs no contract change).
    /// </summary>
    [DataMember(Order = 1)]
    public string Reason { get; set; } = string.Empty;

    /// <summary>Gets or sets a short sentence for the Index window naming the exact rule (the gitignore line, the pattern, the size).</summary>
    [DataMember(Order = 2)]
    public string Description { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether the file is secret-bearing (marked in the Index window; pinning it needs approval).</summary>
    [DataMember(Order = 3)]
    public bool IsSecretBearing { get; set; }

    /// <summary>Gets or sets a value indicating whether the entry is a folder excluded as a whole (its contents are not listed).</summary>
    [DataMember(Order = 4)]
    public bool IsDirectory { get; set; }
}

/// <summary>One index folder on disk, for the Index window's retention view. Added in contract 1.3.</summary>
[DataContract]
public sealed class IndexInfo
{
    /// <summary>Gets or sets the index id (the folder name).</summary>
    [DataMember(Order = 0)]
    public string IndexId { get; set; } = string.Empty;

    /// <summary>Gets or sets the repository root the index covers, or <see langword="null"/> when its <c>state.json</c> is missing.</summary>
    [DataMember(Order = 1)]
    public string? RootPath { get; set; }

    /// <summary>Gets or sets the embedding model id, or <see langword="null"/> when unknown.</summary>
    [DataMember(Order = 2)]
    public string? EmbeddingModelId { get; set; }

    /// <summary>Gets or sets the chunker version, or <see langword="null"/> when unknown.</summary>
    [DataMember(Order = 3)]
    public string? ChunkerVersion { get; set; }

    /// <summary>Gets or sets the disk use of the folder, in bytes.</summary>
    [DataMember(Order = 4)]
    public long DiskBytes { get; set; }

    /// <summary>Gets or sets when the index was last used, in UTC.</summary>
    [DataMember(Order = 5)]
    public DateTime LastUsedUtc { get; set; }

    /// <summary>Gets or sets whether the repository root still exists; <see langword="null"/> when that cannot be told (no state, or its drive is offline).</summary>
    [DataMember(Order = 6)]
    public bool? RootExists { get; set; }

    /// <summary>Gets or sets a value indicating whether a live host (this one or another) holds the index's <c>writer.lock</c>.</summary>
    [DataMember(Order = 7)]
    public bool IsInUse { get; set; }

    /// <summary>Gets or sets a value indicating whether this host has the index open for the current solution or folder.</summary>
    [DataMember(Order = 8)]
    public bool IsOpen { get; set; }
}
