namespace Pillion.ContextEngine.Symbols;

/// <summary>
/// A reference resolved by name to one candidate definition: the shape of a <c>symbol_refs</c> row (design section 10)
/// except for the file id, which the indexing pipeline maps from <see cref="FilePath"/>. A reference with several
/// candidates becomes one edge per candidate, each marked <see cref="IsAmbiguous"/>. Like the definitions, every
/// edge is approximate.
/// </summary>
/// <param name="FromSymbolId">
/// The innermost definition in <see cref="FilePath"/> whose lines contain the reference (the calling method, the class
/// whose base list it is); null for file-level code outside every definition (imports before a namespace, top-level
/// statements).
/// </param>
/// <param name="ToSymbolId">The candidate definition.</param>
/// <param name="FilePath">The file the reference is in.</param>
/// <param name="Line">The reference's line, 1-based.</param>
/// <param name="Kind">How the name is used.</param>
public sealed record SymbolEdge(long? FromSymbolId, long ToSymbolId, string FilePath, int Line, SymbolReferenceKind Kind)
{
    /// <summary>The number of candidate definitions the reference resolved to (1 when it was unambiguous).</summary>
    public int CandidateCount { get; init; } = 1;

    /// <summary>True when the reference resolved to more than one candidate (overloads, partial types, the same name in two files).</summary>
    public bool IsAmbiguous => CandidateCount > 1;
}

/// <summary>Settings for <see cref="SymbolGraphBuilder"/>.</summary>
public sealed record SymbolGraphOptions
{
    /// <summary>The default options.</summary>
    public static SymbolGraphOptions Default { get; } = new();

    /// <summary>
    /// The partition of a file (design section 10: the project or top-level folder used for path scoping), used to
    /// prefer candidates near the reference. Pass the indexing pipeline's own <c>files.partition</c> lookup; the
    /// default is the path's first folder (the empty string for files at the root).
    /// </summary>
    public Func<string, string>? PartitionOf { get; init; }

    /// <summary>
    /// A reference with more candidates than this (after the qualifier, same-file and same-partition preferences) is
    /// counted as too ambiguous and produces no edges. Default 5.
    /// </summary>
    public int MaxCandidates { get; init; } = 5;

    /// <summary>The default partition: the first folder of the path.</summary>
    /// <param name="path">A repository-relative path.</param>
    /// <returns>The first folder, or the empty string.</returns>
    public static string TopLevelFolder(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return string.Empty;
        }

        var trimmed = path.TrimStart('/', '\\');
        var cut = trimmed.AsSpan().IndexOfAny('/', '\\');
        return cut < 0 ? string.Empty : trimmed[..cut];
    }
}
