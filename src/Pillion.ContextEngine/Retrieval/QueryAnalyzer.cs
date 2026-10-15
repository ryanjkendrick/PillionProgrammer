using System.Collections.Frozen;
using System.Text;
using Pillion.ContextEngine.Chunking;
using Pillion.LocalStore.Index;

namespace Pillion.ContextEngine.Retrieval;

/// <summary>What <see cref="QueryAnalyzer"/> understood of a query (design section 7, Hybrid retrieval step 1).</summary>
public sealed record QueryAnalysis
{
    /// <summary>Gets the query as given.</summary>
    public required string Text { get; init; }

    /// <summary>Gets the identifier tokens as written, in order, without duplicates, including qualified names (<c>OrderService.Place</c>, <c>ns::Type</c>).</summary>
    public required IReadOnlyList<string> Identifiers { get; init; }

    /// <summary>Gets the lower-cased search terms: the identifiers split on camelCase, snake_case, acronyms and digits, without stop words or duplicates.</summary>
    public required IReadOnlyList<string> Terms { get; init; }

    /// <summary>Gets the file names and paths the query mentions (a known extension, or a path separator), with <c>/</c> separators.</summary>
    public required IReadOnlyList<string> FileNames { get; init; }

    /// <summary>
    /// Gets the identifiers worth looking up as symbol names (no stop words, no very short words); the symbol leg keeps
    /// those that match a definition exactly.
    /// </summary>
    public required IReadOnlyList<string> SymbolCandidates { get; init; }

    /// <summary>Gets the text handed to lexical search: the tokens without stop words (FTS5 splits identifiers itself), or empty.</summary>
    public required string LexicalQuery { get; init; }
}

/// <summary>
/// Query understanding for hybrid retrieval: identifier splitting (with the index's own <see cref="IdentifierSplitter"/>, so
/// the query is split exactly as the chunks were), file-name detection and the symbol-name candidates. Pure and
/// deterministic; embedding the question is the vector leg's job.
/// </summary>
public static class QueryAnalyzer
{
    /// <summary>The most symbol candidates looked up per query.</summary>
    public const int MaxSymbolCandidates = 16;

    /// <summary>The most file names kept per query.</summary>
    public const int MaxFileNames = 8;

    // Words that carry no meaning for code search; kept short on purpose, so words that are also code (get, set, list,
    // file, find) stay. Compared lower-case.
    private static readonly FrozenSet<string> StopWords = new[]
    {
        "a", "about", "after", "all", "also", "am", "an", "and", "any", "are", "as", "at", "be", "been", "before", "being",
        "but", "by", "can", "could", "did", "do", "does", "doing", "done", "each", "either", "else", "etc", "every", "for",
        "from", "had", "has", "have", "having", "he", "her", "here", "him", "his", "how", "i", "if", "in", "into", "is",
        "it", "its", "just", "me", "might", "more", "most", "much", "must", "my", "no", "nor", "not", "of", "on", "once",
        "only", "or", "other", "our", "out", "over", "own", "please", "same", "shall", "she", "should", "show", "so",
        "some", "such", "than", "that", "the", "their", "them", "then", "there", "these", "they", "this", "those",
        "through", "to", "too", "under", "until", "up", "us", "very", "was", "we", "were", "what", "when", "where",
        "whether", "which", "while", "who", "whom", "whose", "why", "will", "with", "within", "without", "would", "you",
        "your", "code", "codebase", "implemented", "implement", "implementation", "happens", "handled", "handle", "work",
        "works", "working", "used", "uses", "use", "using", "called", "call", "calls", "defined", "define", "explain",
        "tell", "see", "look", "need", "want", "like", "way", "ways", "thing", "things", "something", "somewhere",
    }.ToFrozenSet(StringComparer.Ordinal);

    // Extensions that make a dotted token a file name: every language the chunkers know plus common project files.
    private static readonly FrozenSet<string> FileExtensions = LanguageCatalog.Default.Languages
        .SelectMany(l => l.Extensions)
        .Select(e => e.TrimStart('.'))
        .Concat(
        [
            "md", "txt", "json", "jsonc", "xml", "yml", "yaml", "toml", "ini", "config", "props", "targets", "csproj",
            "vbproj", "fsproj", "sln", "slnx", "slnf", "ps1", "psm1", "sh", "bat", "cmd", "xaml", "resx", "fs", "kt", "swift",
            "rb", "php", "scss", "less", "vue", "svelte", "proto", "graphql", "gradle", "dockerfile", "editorconfig",
        ])
        .ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Analyses a query.</summary>
    /// <param name="text">The query.</param>
    /// <returns>The analysis; empty lists for a query without tokens.</returns>
    public static QueryAnalysis Analyze(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var identifiers = new List<string>();
        var fileNames = new List<string>();
        var lexicalTokens = new List<string>();
        var seenIdentifiers = new HashSet<string>(StringComparer.Ordinal);
        var seenFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenLexical = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var word in Words(text))
        {
            if (IsFileName(word, out var file))
            {
                if (fileNames.Count < MaxFileNames && seenFiles.Add(file))
                {
                    fileNames.Add(file);
                }

                // The file's name still searches lexically (breadcrumbs start with the path): its stem and folder names.
                foreach (var segment in file.Split('/', StringSplitOptions.RemoveEmptyEntries))
                {
                    var stem = segment;
                    var dot = segment.LastIndexOf('.');
                    if (dot > 0 && FileExtensions.Contains(segment[(dot + 1)..]))
                    {
                        stem = segment[..dot];
                    }

                    foreach (var token in IdentifierTokens(stem))
                    {
                        AddLexical(token);
                    }
                }

                continue;
            }

            var qualified = word.Contains('.', StringComparison.Ordinal) || word.Contains("::", StringComparison.Ordinal);
            var tokens = IdentifierTokens(word);
            if (qualified && tokens.Count > 1 && tokens.All(t => IsIdentifier(t) && t.Length > 1) && !word.Contains('-', StringComparison.Ordinal))
            {
                AddIdentifier(word.Trim('.', ':'));
            }

            foreach (var token in tokens)
            {
                if (IsIdentifier(token))
                {
                    AddIdentifier(token);
                }

                AddLexical(token);
            }
        }

        var terms = new List<string>();
        var seenTerms = new HashSet<string>(StringComparer.Ordinal);
        foreach (var token in lexicalTokens)
        {
            foreach (var part in IdentifierSplitter.Split(token))
            {
                if (!StopWords.Contains(part) && seenTerms.Add(part))
                {
                    terms.Add(part);
                }
            }
        }

        var symbolCandidates = identifiers
            .Where(IsSymbolCandidate)
            .Take(MaxSymbolCandidates)
            .ToList();

        return new QueryAnalysis
        {
            Text = text,
            Identifiers = identifiers,
            Terms = terms,
            FileNames = fileNames,
            SymbolCandidates = symbolCandidates,
            LexicalQuery = string.Join(' ', lexicalTokens.Take(IdentifierSplitter.MaxQueryTokens)),
        };

        void AddIdentifier(string identifier)
        {
            if (identifier.Length > 0 && seenIdentifiers.Add(identifier))
            {
                identifiers.Add(identifier);
            }
        }

        void AddLexical(string token)
        {
            if (token.Length > 0 && !StopWords.Contains(token.ToLowerInvariant()) && seenLexical.Add(token))
            {
                lexicalTokens.Add(token);
            }
        }
    }

    /// <summary>Whether a dotted or slashed word names a file, and the name with <c>/</c> separators.</summary>
    /// <param name="word">A word of the query.</param>
    /// <param name="file">Receives the file name or path.</param>
    /// <returns><see langword="true"/> for a file name or path.</returns>
    internal static bool IsFileName(string word, out string file)
    {
        file = word.Replace('\\', '/').Trim('/');
        if (file.Length == 0 || word.Contains("://", StringComparison.Ordinal))
        {
            return false;
        }

        var name = file[(file.LastIndexOf('/') + 1)..];
        var dot = name.LastIndexOf('.');
        var hasExtension = dot >= 0 && dot < name.Length - 1 && FileExtensions.Contains(name[(dot + 1)..]) && (dot > 0 || name.Length > 1);
        var hasSeparator = file.Contains('/', StringComparison.Ordinal) && file.Split('/').All(s => s.Length > 0 && s.Any(char.IsLetterOrDigit));
        return hasExtension || hasSeparator;
    }

    /// <summary>The words of a query: runs of token characters joined by the characters that occur inside paths and qualified names.</summary>
    private static IEnumerable<string> Words(string text)
    {
        var builder = new StringBuilder();
        foreach (var c in text)
        {
            if (IsTokenChar(c) || c is '.' or '/' or '\\' or ':' or '-')
            {
                builder.Append(c);
                continue;
            }

            if (Flush(builder) is { } word)
            {
                yield return word;
            }
        }

        if (Flush(builder) is { } last)
        {
            yield return last;
        }

        static string? Flush(StringBuilder builder)
        {
            // Punctuation at the edges is sentence punctuation (a full stop, a colon, a dash), not part of the word.
            var word = builder.ToString().Trim('.', ':', '-', '/', '\\');
            builder.Clear();
            return word.Any(IsTokenChar) ? word : null;
        }
    }

    /// <summary>The identifier-like tokens of a word (runs of letters, digits and underscores).</summary>
    private static List<string> IdentifierTokens(string word)
    {
        var tokens = new List<string>();
        var start = -1;
        for (var i = 0; i <= word.Length; i++)
        {
            if (i < word.Length && IsTokenChar(word[i]))
            {
                if (start < 0)
                {
                    start = i;
                }
            }
            else if (start >= 0)
            {
                tokens.Add(word[start..i]);
                start = -1;
            }
        }

        return tokens;
    }

    private static bool IsTokenChar(char c) => c == '_' || char.IsLetterOrDigit(c);

    private static bool IsIdentifier(string token) => token.Length > 0 && (char.IsLetter(token[0]) || token[0] == '_');

    private static bool IsSymbolCandidate(string identifier)
    {
        var last = identifier[(Math.Max(identifier.LastIndexOf('.'), identifier.LastIndexOf(':')) + 1)..];
        if (last.Length == 0 || StopWords.Contains(last.ToLowerInvariant()) && last.All(c => !char.IsUpper(c) || c == last[0]))
        {
            return false;
        }

        // Short plain words ("id", "io") are too common to be names on their own; code-shaped tokens of any length are kept.
        var codeShaped = last.Skip(1).Any(char.IsUpper) || last.Contains('_', StringComparison.Ordinal) || last.Any(char.IsDigit) || last != identifier;
        return codeShaped || last.Length >= 3;
    }
}
