using System.Collections.Frozen;
using Pillion.ContextEngine.Chunking.Razor;
using Pillion.ContextEngine.Chunking.Roslyn;
using Pillion.ContextEngine.Chunking.Sql;
using Pillion.ContextEngine.Chunking.TreeSitter;

namespace Pillion.ContextEngine.Chunking;

/// <summary>A language: its identifier, the file extensions that select it, and the chunker that handles it.</summary>
/// <param name="Id">The language identifier recorded on chunks.</param>
/// <param name="Extensions">File extensions with the leading dot, compared without regard to case.</param>
/// <param name="Chunker">The chunker.</param>
public sealed record LanguageDefinition(string Id, IReadOnlyList<string> Extensions, IChunker Chunker);

/// <summary>
/// The map from file extension to language and chunker, as data. <see cref="Default"/> holds the languages of design
/// section 7; a catalog with more (or different) languages is built by passing definitions to the constructor, for
/// example <c>new LanguageCatalog([.. LanguageCatalog.Default.Languages, new("lua", [".lua"], new TreeSitterChunker(lua))])</c>.
/// Later definitions win for an extension that appears twice. Files with no matching extension use the window chunker.
/// </summary>
public sealed class LanguageCatalog
{
    private readonly FrozenDictionary<string, LanguageDefinition> _byExtension;

    /// <summary>Creates a catalog.</summary>
    /// <param name="languages">The languages, in increasing precedence.</param>
    public LanguageCatalog(IEnumerable<LanguageDefinition> languages)
    {
        ArgumentNullException.ThrowIfNull(languages);
        Languages = [.. languages];
        var map = new Dictionary<string, LanguageDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var language in Languages)
        {
            foreach (var extension in language.Extensions)
            {
                map[extension] = language;
            }
        }

        _byExtension = map.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The languages Pillion chunks syntactically (design section 7).</summary>
    public static LanguageCatalog Default { get; } = new(
    [
        new(LanguageIds.CSharp, [".cs", ".csx"], new CSharpChunker()),
        new(LanguageIds.VisualBasic, [".vb"], new VisualBasicChunker()),
        new(LanguageIds.Razor, [".razor", ".cshtml"], new RazorChunker()),
        new(LanguageIds.TypeScript, [".ts", ".mts", ".cts"], new TreeSitterChunker(TreeSitterGrammars.TypeScript)),
        new(LanguageIds.Tsx, [".tsx"], new TreeSitterChunker(TreeSitterGrammars.Tsx)),
        new(LanguageIds.JavaScript, [".js", ".mjs", ".cjs", ".jsx"], new TreeSitterChunker(TreeSitterGrammars.JavaScript)),
        new(LanguageIds.Python, [".py", ".pyw", ".pyi"], new TreeSitterChunker(TreeSitterGrammars.Python)),
        new(LanguageIds.C, [".c"], new TreeSitterChunker(TreeSitterGrammars.C)),
        new(LanguageIds.Cpp, [".cpp", ".cc", ".cxx", ".c++", ".h", ".hh", ".hpp", ".hxx", ".h++", ".ipp", ".inl", ".tpp"],
            new TreeSitterChunker(TreeSitterGrammars.Cpp)),
        new(LanguageIds.Go, [".go"], new TreeSitterChunker(TreeSitterGrammars.Go)),
        new(LanguageIds.Rust, [".rs"], new TreeSitterChunker(TreeSitterGrammars.Rust)),
        new(LanguageIds.Java, [".java"], new TreeSitterChunker(TreeSitterGrammars.Java)),
        new(LanguageIds.Sql, [".sql"], new SqlChunker()),
        new(LanguageIds.Html, [".html", ".htm", ".xhtml"], new HtmlChunker()),
        new(LanguageIds.Css, [".css"], new TreeSitterChunker(TreeSitterGrammars.Css)),
    ]);

    /// <summary>The languages in this catalog.</summary>
    public IReadOnlyList<LanguageDefinition> Languages { get; }

    /// <summary>The language for a path, by extension; null when none matches.</summary>
    /// <param name="path">A file path.</param>
    /// <returns>The definition, or null.</returns>
    public LanguageDefinition? Find(string path)
    {
        var extension = Extension(path);
        return extension.Length > 0 && _byExtension.TryGetValue(extension, out var language) ? language : null;
    }

    private static string Extension(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return string.Empty;
        }

        var name = path.AsSpan(path.AsSpan().LastIndexOfAny('/', '\\') + 1);
        var dot = name.LastIndexOf('.');
        return dot <= 0 ? string.Empty : name[dot..].ToString();
    }
}
