using System.Text.RegularExpressions;
using Pillion.ContextEngine.Symbols;

namespace Pillion.Host.WorkspaceQueries;

/// <summary>
/// Answers the workspace queries from the index's symbol graphs (design section 7: "fall back to the index's approximate
/// edges"): resolves the symbol to its definitions by name, container, file and line, then reads the edges that point at
/// them. Every site is approximate.
/// </summary>
internal static partial class SymbolGraphFallback
{
    /// <summary>A definition found in one of the graphs.</summary>
    /// <param name="Root">The graph and its root.</param>
    /// <param name="Definition">The definition.</param>
    internal sealed record Match(IndexedSymbolGraph Root, SymbolDefinition Definition)
    {
        /// <summary>Gets the definition's full path.</summary>
        public string FullPath => SymbolGraphFallback.FullPath(Root.RootPath, Definition.FilePath);
    }

    /// <summary>The definitions a query names, best first: in the requested file and around the requested line first.</summary>
    /// <param name="graphs">The index's graphs.</param>
    /// <param name="query">The query.</param>
    /// <returns>The definitions; empty when none match.</returns>
    public static IReadOnlyList<Match> Resolve(IReadOnlyList<IndexedSymbolGraph> graphs, WorkspaceSymbolQuery query)
    {
        var container = NormalizeContainer(query.Container);
        var matches = new List<Match>();
        foreach (var root in graphs)
        {
            foreach (var definition in root.Graph.FindDefinitions(query.Name))
            {
                if (ContainerMatches(NormalizeContainer(definition.Container), container))
                {
                    matches.Add(new Match(root, definition));
                }
            }
        }

        if (matches.Count == 0 || string.IsNullOrEmpty(query.FilePath))
        {
            return matches;
        }

        // A file (and line) narrows the namesakes down to the one declared there, when there is one.
        var inFile = matches.Where(m => SamePath(m.FullPath, query.FilePath)).ToList();
        if (inFile.Count == 0)
        {
            return matches;
        }

        if (query.Line >= 0)
        {
            var line = query.Line + 1;
            var around = inFile.Where(m => m.Definition.StartLine <= line && line <= m.Definition.EndLine)
                .OrderBy(m => m.Definition.EndLine - m.Definition.StartLine)
                .ToList();
            if (around.Count > 0)
            {
                return [around[0]];
            }
        }

        return inFile;
    }

    /// <summary>The approximate sites for the definitions: every edge (references) or the call edges (callers).</summary>
    /// <param name="matches">The resolved definitions.</param>
    /// <param name="callsOnly">True for callers-of.</param>
    /// <param name="lines">Reads the snippets.</param>
    /// <returns>The sites, ordered by file and line, one per line.</returns>
    public static IReadOnlyList<WorkspaceSite> Sites(IReadOnlyList<Match> matches, bool callsOnly, SourceLineCache lines)
    {
        var sites = new List<WorkspaceSite>();
        var seen = new HashSet<(string, int)>(PathLineComparer.Instance);
        foreach (var match in matches)
        {
            var graph = match.Root.Graph;
            foreach (var edge in graph.FindReferences(match.Definition.Id))
            {
                if (callsOnly && edge.Kind != SymbolReferenceKind.Call)
                {
                    continue;
                }

                var path = FullPath(match.Root.RootPath, edge.FilePath);
                var line = edge.Line - 1;
                if (!seen.Add((path, line)))
                {
                    continue;
                }

                var caller = edge.FromSymbolId is { } from ? graph.GetDefinition(from) : null;
                sites.Add(new WorkspaceSite(path, line, lines.Snippet(path, line), caller is null ? string.Empty : Dotted(caller.QualifiedName), IsApproximate: true));
            }
        }

        return Order(sites);
    }

    /// <summary>Orders sites by file (ignoring case) and line.</summary>
    /// <param name="sites">The sites.</param>
    /// <returns>The ordered list.</returns>
    public static List<WorkspaceSite> Order(IEnumerable<WorkspaceSite> sites) =>
        [.. sites.OrderBy(s => s.FilePath, StringComparer.OrdinalIgnoreCase).ThenBy(s => s.Line)];

    /// <summary>Normalizes a container: an index breadcrumb, C++ or path separators and type arguments become a dotted name.</summary>
    /// <param name="container">The container.</param>
    /// <returns>The dotted name, or empty.</returns>
    public static string NormalizeContainer(string? container)
    {
        if (string.IsNullOrWhiteSpace(container))
        {
            return string.Empty;
        }

        var text = container.Trim();
        if (text.StartsWith("global::", StringComparison.Ordinal))
        {
            text = text["global::".Length..];
        }

        text = text.Replace(" > ", ".", StringComparison.Ordinal).Replace("::", ".", StringComparison.Ordinal).Replace('/', '.').Replace('\\', '.');
        string previous;
        do
        {
            previous = text;
            text = GenericArguments().Replace(text, string.Empty);
        }
        while (!string.Equals(previous, text, StringComparison.Ordinal));

        return text.Trim('.', ' ');
    }

    /// <summary>Whether a definition's container is the requested one or ends with it (or the request ends with it).</summary>
    /// <param name="definitionContainer">The definition's normalized container.</param>
    /// <param name="container">The normalized requested container.</param>
    /// <returns>True when they match or nothing was requested.</returns>
    public static bool ContainerMatches(string definitionContainer, string container) =>
        container.Length == 0
        || string.Equals(definitionContainer, container, StringComparison.OrdinalIgnoreCase)
        || definitionContainer.EndsWith("." + container, StringComparison.OrdinalIgnoreCase)
        || (definitionContainer.Length > 0 && container.EndsWith("." + definitionContainer, StringComparison.OrdinalIgnoreCase));

    /// <summary>A graph path as a full path.</summary>
    /// <param name="root">The root.</param>
    /// <param name="path">A path in the graph (relative to the root, or full).</param>
    /// <returns>The full path.</returns>
    public static string FullPath(string root, string path) =>
        Path.IsPathRooted(path) || string.IsNullOrEmpty(root) ? path : Path.GetFullPath(Path.Combine(root, path));

    /// <summary>Whether two full paths name the same file (Windows: case is ignored).</summary>
    /// <param name="a">One path.</param>
    /// <param name="b">The other.</param>
    /// <returns>True when they are the same.</returns>
    public static bool SamePath(string a, string b)
    {
        try
        {
            return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static string Dotted(string qualifiedName) => NormalizeContainer(qualifiedName);

    [GeneratedRegex(@"<[^<>]*>|\(Of [^()]*\)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex GenericArguments();

    private sealed class PathLineComparer : IEqualityComparer<(string Path, int Line)>
    {
        public static PathLineComparer Instance { get; } = new();

        public bool Equals((string Path, int Line) x, (string Path, int Line) y) =>
            x.Line == y.Line && string.Equals(x.Path, y.Path, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string Path, int Line) obj) => HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Path), obj.Line);
    }
}

/// <summary>
/// Reads lines of source files for snippets and columns, each file at most once per query. Files that cannot be read, or
/// that are larger than <see cref="MaxFileBytes"/>, give empty snippets.
/// </summary>
internal sealed class SourceLineCache
{
    /// <summary>The largest file read for a snippet.</summary>
    public const long MaxFileBytes = 4 * 1024 * 1024;

    /// <summary>The longest snippet.</summary>
    public const int MaxSnippetLength = 300;

    private readonly Dictionary<string, string[]?> _files = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The text of a line, or <see langword="null"/>.</summary>
    /// <param name="path">The full path.</param>
    /// <param name="line">The zero-based line.</param>
    /// <returns>The line, untrimmed.</returns>
    public string? Line(string path, int line)
    {
        var lines = Load(path);
        return lines is not null && line >= 0 && line < lines.Length ? lines[line] : null;
    }

    /// <summary>The trimmed text of a line, cut to <see cref="MaxSnippetLength"/>; empty when unknown.</summary>
    /// <param name="path">The full path.</param>
    /// <param name="line">The zero-based line.</param>
    /// <returns>The snippet.</returns>
    public string Snippet(string path, int line)
    {
        var text = Line(path, line)?.Trim() ?? string.Empty;
        return text.Length <= MaxSnippetLength ? text : text[..MaxSnippetLength];
    }

    /// <summary>The zero-based column of a whole word on a line, or -1.</summary>
    /// <param name="path">The full path.</param>
    /// <param name="line">The zero-based line.</param>
    /// <param name="word">The word.</param>
    /// <returns>The column.</returns>
    public int ColumnOf(string path, int line, string word)
    {
        var text = Line(path, line);
        if (text is null || word.Length == 0)
        {
            return -1;
        }

        for (var index = text.IndexOf(word, StringComparison.Ordinal); index >= 0; index = text.IndexOf(word, index + 1, StringComparison.Ordinal))
        {
            var before = index == 0 || !IsIdentifierChar(text[index - 1]);
            var after = index + word.Length >= text.Length || !IsIdentifierChar(text[index + word.Length]);
            if (before && after)
            {
                return index;
            }
        }

        return -1;
    }

    private static bool IsIdentifierChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    private string[]? Load(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        if (_files.TryGetValue(path, out var cached))
        {
            return cached;
        }

        string[]? lines = null;
        try
        {
            var info = new FileInfo(path);
            if (info.Exists && info.Length <= MaxFileBytes)
            {
                lines = File.ReadAllLines(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            lines = null;
        }

        _files[path] = lines;
        return lines;
    }
}
