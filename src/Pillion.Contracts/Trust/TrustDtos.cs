using System;
using System.Runtime.Serialization;

namespace Pillion.Contracts;

/// <summary>Asks the user to trust a repository's <c>.pillion</c> folder (<see cref="ITrustService.RequestTrustAsync"/>). Added in contract 1.4.</summary>
[DataContract]
public sealed class TrustRequest
{
    /// <summary>Gets or sets the repository root (the git top-level directory or the opened folder).</summary>
    [DataMember(Order = 0)]
    public string RepositoryRoot { get; set; } = string.Empty;

    /// <summary>Gets or sets the full path of the <c>.pillion</c> folder.</summary>
    [DataMember(Order = 1)]
    public string PillionFolder { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the content hash the decision is pinned to: the lower-case hexadecimal SHA-256 over the folder's files, each
    /// as its relative path (lower-cased, <c>/</c> separators), U+001F, its bytes, U+001E, in path order.
    /// </summary>
    [DataMember(Order = 2)]
    public string ContentHash { get; set; } = string.Empty;

    /// <summary>Gets or sets the files, for the prompt to show.</summary>
    [DataMember(Order = 3)]
    public TrustFile[] Files { get; set; } = Array.Empty<TrustFile>();

    /// <summary>Gets or sets the hash the folder was trusted at before it changed, or <see langword="null"/> when it was never trusted.</summary>
    [DataMember(Order = 4)]
    public string? PreviouslyTrustedHash { get; set; }

    /// <summary>Gets or sets what wants the folder (an agent session, a slash command, the rules for chat), for the prompt.</summary>
    [DataMember(Order = 5)]
    public string Reason { get; set; } = string.Empty;
}

/// <summary>One file of a <c>.pillion</c> folder as the trust prompt shows it. Added in contract 1.4.</summary>
[DataContract]
public sealed class TrustFile
{
    /// <summary>Gets or sets the path relative to the <c>.pillion</c> folder, with <c>/</c> separators.</summary>
    [DataMember(Order = 0)]
    public string RelativePath { get; set; } = string.Empty;

    /// <summary>Gets or sets the file's text, cut at 64 KB (<see cref="IsTruncated"/>); binary files are sent empty.</summary>
    [DataMember(Order = 1)]
    public string Content { get; set; } = string.Empty;

    /// <summary>Gets or sets the file's size in bytes.</summary>
    [DataMember(Order = 2)]
    public long SizeBytes { get; set; }

    /// <summary>Gets or sets a value indicating whether <see cref="Content"/> was cut.</summary>
    [DataMember(Order = 3)]
    public bool IsTruncated { get; set; }
}

/// <summary>The user's trust decision. Added in contract 1.4.</summary>
[DataContract]
public sealed class TrustDecision
{
    /// <summary>Gets or sets the repository root the decision is for.</summary>
    [DataMember(Order = 0)]
    public string RepositoryRoot { get; set; } = string.Empty;

    /// <summary>Gets or sets the content hash the decision is for (<see cref="TrustRequest.ContentHash"/>).</summary>
    [DataMember(Order = 1)]
    public string ContentHash { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether the folder is trusted at this hash.</summary>
    [DataMember(Order = 2)]
    public bool IsTrusted { get; set; }

    /// <summary>Gets or sets a value indicating whether the host records the decision in <c>trust.json</c>; false asks again next time.</summary>
    [DataMember(Order = 3)]
    public bool Remember { get; set; }
}
