using Pillion.ContextEngine.Chunking.Core;

namespace Pillion.ContextEngine.Symbols;

/// <summary>
/// The shared shape of the extractors: parse, walk, collect. Any failure (a parse that fails badly, a grammar that
/// will not load, an unexpected exception) yields an empty extraction marked incomplete and a call to
/// <see cref="SymbolExtractionOptions.OnFailure"/>; malformed input never makes <see cref="Extract"/> throw.
/// </summary>
public abstract class SyntaxSymbolExtractor : ISymbolExtractor
{
    /// <summary>The language recorded on the symbols.</summary>
    public abstract string Language { get; }

    /// <inheritdoc />
    public SymbolExtraction Extract(string path, string text, SymbolExtractionOptions? options = null)
    {
        path ??= string.Empty;
        var document = new SourceDocument(path, text);
        if (document.IsBlank)
        {
            return SymbolExtraction.Empty(path, Language);
        }

        string? failure;
        try
        {
            var sink = new SymbolSink(document, Language) { ParseTimeout = (options ?? SymbolExtractionOptions.Default).ParseTimeout };
            if (Collect(sink, out failure))
            {
                return sink.ToExtraction();
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            failure = $"{exception.GetType().Name}: {exception.Message}";
        }

        SymbolSink.ReportFailure(options, new SymbolExtractionFailure(path, Language, failure ?? "Extraction failed."));
        return SymbolExtraction.Empty(path, Language, isComplete: false);
    }

    /// <summary>Collects the symbols of the sink's document; false with a reason when the file cannot be read.</summary>
    private protected abstract bool Collect(SymbolSink sink, out string? failure);
}
