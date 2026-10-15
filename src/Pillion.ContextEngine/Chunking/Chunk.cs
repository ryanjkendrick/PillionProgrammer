namespace Pillion.ContextEngine.Chunking;

/// <summary>
/// One unit of a file for the index: a syntactic unit (a member with its doc comment, a type header, a statement, a
/// markup element) or a fallback window, with the breadcrumb that is embedded in front of it (design section 7).
/// </summary>
public sealed record Chunk
{
    /// <summary>The file path the chunk came from, exactly as the caller passed it (usually relative to the root).</summary>
    public required string FilePath { get; init; }

    /// <summary>The first line of the chunk, 1-based.</summary>
    public required int StartLine { get; init; }

    /// <summary>The last line of the chunk, 1-based and inclusive.</summary>
    public required int EndLine { get; init; }

    /// <summary>What kind of unit the chunk is.</summary>
    public required ChunkKind Kind { get; init; }

    /// <summary>
    /// The breadcrumb <c>path &gt; namespace &gt; type &gt; member</c>, ending in <c>(part n of m)</c> when a unit over
    /// the token cap was split. See <see cref="Breadcrumbs"/> for the format.
    /// </summary>
    public required string Breadcrumb { get; init; }

    /// <summary>
    /// The source text of the lines <see cref="StartLine"/> to <see cref="EndLine"/> with their original line breaks,
    /// without the line break after the last line. When <see cref="IsTruncated"/> is set, a prefix of that text.
    /// </summary>
    public required string Text { get; init; }

    /// <summary>The number of tokens in <see cref="EmbeddingText"/>, counted with <see cref="ChunkOptions.TokenCounter"/>.</summary>
    public required int TokenCount { get; init; }

    /// <summary>The language the file was chunked as, for example <c>csharp</c>, <c>python</c> or <c>text</c>.</summary>
    public string Language { get; init; } = LanguageIds.Text;

    /// <summary>The part number, from 1, when a unit over the token cap was split; otherwise 1.</summary>
    public int Part { get; init; } = 1;

    /// <summary>The number of parts the unit was split into; 1 when it was not split.</summary>
    public int PartCount { get; init; } = 1;

    /// <summary>
    /// Set when a single line was longer than the token cap and its text was cut to fit (for example a long string
    /// literal or a line of minified code). The line range still names the whole line.
    /// </summary>
    public bool IsTruncated { get; init; }

    /// <summary>The text that is embedded: the breadcrumb, a line break, then <see cref="Text"/>.</summary>
    public string EmbeddingText => Breadcrumb + "\n" + Text;
}
