namespace Pillion.ContextEngine.Chunking;

/// <summary>Settings for chunking: the token cap, how tokens are counted, and the fallback window.</summary>
public sealed record ChunkOptions
{
    /// <summary>The default cap: 512 WordPiece tokens, the bundled embedding model's input size (design section 7).</summary>
    public const int DefaultMaxTokens = 512;

    /// <summary>The smallest cap accepted; below it a breadcrumb and a line of code no longer fit.</summary>
    public const int MinimumMaxTokens = 32;

    /// <summary>The default options: a 512-token cap counted with <see cref="TokenCounters.ApproximateWordPiece"/>.</summary>
    public static ChunkOptions Default { get; } = new();

    /// <summary>
    /// The cap on <see cref="Chunk.TokenCount"/>, in the embedding model's own tokenizer and including the breadcrumb,
    /// so room for the breadcrumb is always left. Units over it are split; a single line over it is truncated.
    /// </summary>
    public int MaxTokens { get; init; } = DefaultMaxTokens;

    /// <summary>
    /// Counts tokens the way the embedding model does. Inject the model's tokenizer through
    /// <see cref="TokenCounters.FromTokenizer"/> or <see cref="TokenCounters.FromWordPieceVocabulary"/>; the default is
    /// a deterministic approximation that errs on the high side.
    /// </summary>
    public TokenCounter TokenCounter { get; init; } = TokenCounters.ApproximateWordPiece;

    /// <summary>
    /// The most tokens a breadcrumb may take (and never more than a quarter of <see cref="MaxTokens"/>); longer ones
    /// are shortened, first by dropping the folders from the path.
    /// </summary>
    public int MaxBreadcrumbTokens { get; init; } = 64;

    /// <summary>The fallback window's height in lines (design section 7: 60).</summary>
    public int WindowLines { get; init; } = 60;

    /// <summary>How many lines consecutive fallback windows share (design section 7: 10).</summary>
    public int WindowOverlap { get; init; } = 10;

    /// <summary>
    /// Called when a syntactic chunker gives up on a file and the window chunker is used instead (a parse that fails
    /// badly, a grammar that cannot be loaded, an unexpected error). Optional; for logging and the Index window.
    /// </summary>
    public Action<ChunkerFallback>? OnFallback { get; init; }

    /// <summary>Checks the settings.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A setting is out of range.</exception>
    /// <exception cref="ArgumentNullException">The token counter is missing.</exception>
    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(TokenCounter);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxTokens, MinimumMaxTokens);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxBreadcrumbTokens, 4);
        ArgumentOutOfRangeException.ThrowIfLessThan(WindowLines, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(WindowOverlap);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(WindowOverlap, WindowLines);
    }
}

/// <summary>Why a syntactic chunker fell back to the window chunker for one file.</summary>
/// <param name="Path">The file.</param>
/// <param name="Language">The language that was attempted.</param>
/// <param name="Reason">A short description (for logs; not shown verbatim to users).</param>
public sealed record ChunkerFallback(string Path, string Language, string Reason);
