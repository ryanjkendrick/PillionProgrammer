using System.Collections.Frozen;
using Pillion.ContextEngine.Chunking;
using Pillion.ContextEngine.Symbols.Markup;
using Pillion.ContextEngine.Symbols.Razor;
using Pillion.ContextEngine.Symbols.Roslyn;
using Pillion.ContextEngine.Symbols.Sql;
using Pillion.ContextEngine.Symbols.TreeSitter;

namespace Pillion.ContextEngine.Symbols;

/// <summary>
/// The entry point for the indexing pipeline: picks the extractor for a file by its language (the same
/// extension-to-language map the chunkers use, <see cref="LanguageCatalog"/>), returns an empty extraction for files
/// with no extractor (plain text, Markdown), and guarantees that content never makes it throw.
/// </summary>
public sealed class FileSymbolExtractor : ISymbolExtractor
{
    private readonly LanguageCatalog _catalog;
    private readonly IReadOnlyDictionary<string, ISymbolExtractor> _extractors;

    /// <summary>Creates a file extractor.</summary>
    /// <param name="catalog">The extension-to-language map; <see cref="LanguageCatalog.Default"/> when null.</param>
    /// <param name="extractors">Extractors by language id; <see cref="DefaultExtractors"/> when null.</param>
    public FileSymbolExtractor(LanguageCatalog? catalog = null, IReadOnlyDictionary<string, ISymbolExtractor>? extractors = null)
    {
        _catalog = catalog ?? LanguageCatalog.Default;
        _extractors = extractors ?? DefaultExtractors;
    }

    /// <summary>The extractor for every language in <see cref="LanguageCatalog.Default"/>, by language id.</summary>
    public static IReadOnlyDictionary<string, ISymbolExtractor> DefaultExtractors { get; } = new Dictionary<string, ISymbolExtractor>
    {
        [LanguageIds.CSharp] = new CSharpSymbolExtractor(),
        [LanguageIds.VisualBasic] = new VisualBasicSymbolExtractor(),
        [LanguageIds.Razor] = new RazorSymbolExtractor(),
        [LanguageIds.TypeScript] = new TreeSitterSymbolExtractor(TreeSitterSymbolGrammars.TypeScript),
        [LanguageIds.Tsx] = new TreeSitterSymbolExtractor(TreeSitterSymbolGrammars.Tsx),
        [LanguageIds.JavaScript] = new TreeSitterSymbolExtractor(TreeSitterSymbolGrammars.JavaScript),
        [LanguageIds.Python] = new TreeSitterSymbolExtractor(TreeSitterSymbolGrammars.Python),
        [LanguageIds.C] = new TreeSitterSymbolExtractor(TreeSitterSymbolGrammars.C),
        [LanguageIds.Cpp] = new TreeSitterSymbolExtractor(TreeSitterSymbolGrammars.Cpp),
        [LanguageIds.Go] = new TreeSitterSymbolExtractor(TreeSitterSymbolGrammars.Go),
        [LanguageIds.Rust] = new TreeSitterSymbolExtractor(TreeSitterSymbolGrammars.Rust),
        [LanguageIds.Java] = new TreeSitterSymbolExtractor(TreeSitterSymbolGrammars.Java),
        [LanguageIds.Sql] = new SqlSymbolExtractor(),
        [LanguageIds.Html] = new HtmlSymbolExtractor(),
        [LanguageIds.Css] = new CssSymbolExtractor(),
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>The language a path would be read as.</summary>
    /// <param name="path">A file path.</param>
    /// <returns>A language identifier; <see cref="LanguageIds.Text"/> for files the catalog does not know.</returns>
    public string LanguageOf(string path) => _catalog.Find(path)?.Id ?? LanguageIds.Text;

    /// <inheritdoc />
    public SymbolExtraction Extract(string path, string text, SymbolExtractionOptions? options = null)
    {
        path ??= string.Empty;
        var language = LanguageOf(path);
        if (!_extractors.TryGetValue(language, out var extractor))
        {
            return SymbolExtraction.Empty(path, language);
        }

        try
        {
            return extractor.Extract(path, text, options);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            SymbolSink.ReportFailure(options, new SymbolExtractionFailure(path, language, $"{exception.GetType().Name}: {exception.Message}"));
            return SymbolExtraction.Empty(path, language, isComplete: false);
        }
    }
}
