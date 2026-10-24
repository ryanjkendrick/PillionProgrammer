using Pillion.ContextEngine.Chunking.Core;

namespace Pillion.ContextEngine.Chunking;

/// <summary>
/// The entry point for the indexing pipeline: picks the chunker for a file from a <see cref="LanguageCatalog"/> by
/// extension, uses the window chunker for anything else, and guarantees that content never makes it throw (a chunker
/// that fails unexpectedly is replaced by windows and reported through <see cref="ChunkOptions.OnFallback"/>).
/// Generated, minified, binary, oversized and secret-bearing files are excluded before this point, by repository
/// discovery (work item 2.3).
/// </summary>
public sealed class FileChunker : IChunker
{
    private readonly LanguageCatalog _catalog;

    /// <summary>Creates a file chunker over a catalog.</summary>
    /// <param name="catalog">The languages; <see cref="LanguageCatalog.Default"/> when null.</param>
    public FileChunker(LanguageCatalog? catalog = null)
    {
        _catalog = catalog ?? LanguageCatalog.Default;
    }

    /// <summary>The language a path would be chunked as.</summary>
    /// <param name="path">A file path.</param>
    /// <returns>A language identifier; <see cref="LanguageIds.Text"/> for files without a syntactic chunker.</returns>
    public string LanguageOf(string path) => _catalog.Find(path)?.Id ?? LanguageIds.Text;

    /// <inheritdoc />
    public IReadOnlyList<Chunk> Chunk(string path, string text, ChunkOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        var language = _catalog.Find(path);
        if (language is null)
        {
            return new WindowChunker().Chunk(path, text, options);
        }

        try
        {
            return language.Chunker.Chunk(path, text, options);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            SyntaxChunker.ReportFallback(options, new ChunkerFallback(path, language.Id, $"{exception.GetType().Name}: {exception.Message}"));
            var document = new SourceDocument(path, text);
            return WindowChunker.Windows(new TokenBudget(document, options), language.Id);
        }
    }
}
