using Pillion.ContextEngine.Chunking.Core;

namespace Pillion.ContextEngine.Chunking;

/// <summary>
/// The fallback chunker (design section 7): windows of <see cref="ChunkOptions.WindowLines"/> lines (60) that share
/// <see cref="ChunkOptions.WindowOverlap"/> lines (10) with the next, breadcrumb = path only. Used for any text file
/// without a syntactic chunker and whenever a syntactic chunker gives up. A window over the token cap is split into
/// parts at line boundaries.
/// </summary>
public sealed class WindowChunker : IChunker
{
    private readonly string _language;

    /// <summary>Creates a window chunker that labels its chunks with a language.</summary>
    /// <param name="language">The language recorded on the chunks; <see cref="LanguageIds.Text"/> by default.</param>
    public WindowChunker(string language = LanguageIds.Text)
    {
        _language = language;
    }

    /// <inheritdoc />
    public IReadOnlyList<Chunk> Chunk(string path, string text, ChunkOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        var document = new SourceDocument(path, text);
        if (document.IsBlank)
        {
            return [];
        }

        return Windows(new TokenBudget(document, options), _language);
    }

    internal static List<Chunk> Windows(TokenBudget budget, string language)
    {
        var chunks = new List<Chunk>();
        var lineCount = budget.Document.LineCount;
        if (budget.Document.IsBlank)
        {
            return chunks;
        }

        var height = budget.Options.WindowLines;
        var step = height - budget.Options.WindowOverlap;
        for (var start = 1; ; start += step)
        {
            var end = Math.Min(start + height - 1, lineCount);
            ChunkAssembler.Fit(budget, new ChunkUnit(start, end, ChunkKind.Window, []), language, chunks);
            if (end >= lineCount)
            {
                break;
            }
        }

        return chunks;
    }
}
