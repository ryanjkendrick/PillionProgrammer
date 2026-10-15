using System.Collections.Frozen;

namespace Pillion.ContextEngine.Symbols;

/// <summary>
/// The repository's definitions and the name-resolved reference edges between them, in memory, with the query helpers
/// the retrieval (work item 2.11) and workspace-query fallback (2.14) code uses. Built by
/// <see cref="SymbolGraphBuilder"/>; immutable and safe to share between threads. Everything in it is approximate
/// (syntax-derived definitions, name-based references).
/// </summary>
public sealed class SymbolGraph
{
    private static readonly int[] s_none = [];

    private readonly FrozenDictionary<long, int> _byId;
    private readonly Dictionary<string, int[]> _byName;
    private readonly Dictionary<string, int[]> _candidatesByName;
    private readonly Dictionary<string, (int Start, int Count)> _fileRanges;
    private readonly int[] _parents;
    private readonly Func<string, string> _partitionOf;
    private readonly Dictionary<string, string> _partitions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int[]> _innermost = new(StringComparer.Ordinal);
    private readonly Lazy<SymbolRanking> _ranking;
    private Dictionary<long, int[]> _incoming = [];
    private Dictionary<long, int[]> _outgoing = [];

    internal SymbolGraph(List<SymbolDefinition> definitions, List<string> files, Func<string, string> partitionOf)
    {
        Definitions = definitions;
        Files = files;
        _partitionOf = partitionOf;
        _byId = definitions.Select((d, i) => (d.Id, i)).ToFrozenDictionary(pair => pair.Id, pair => pair.i);
        _byName = Index(definitions, _ => true);
        _candidatesByName = Index(definitions, d => d.Kind != SymbolKind.Constructor);
        _fileRanges = new Dictionary<string, (int, int)>(StringComparer.Ordinal);
        _parents = new int[definitions.Count];
        var stack = new List<int>();
        for (var i = 0; i < definitions.Count; i++)
        {
            var definition = definitions[i];
            if (i == 0 || !string.Equals(definitions[i - 1].FilePath, definition.FilePath, StringComparison.Ordinal))
            {
                stack.Clear();
                _fileRanges[definition.FilePath] = (i, 0);
            }

            var range = _fileRanges[definition.FilePath];
            _fileRanges[definition.FilePath] = (range.Start, range.Count + 1);

            // Definitions are in source order with containers first, so the enclosing one is on the stack.
            while (stack.Count > 0 && definitions[stack[^1]].EndLine < definition.EndLine)
            {
                stack.RemoveAt(stack.Count - 1);
            }

            _parents[i] = stack.Count > 0 ? stack[^1] : -1;
            stack.Add(i);
        }

        _ranking = new Lazy<SymbolRanking>(() => SymbolRanker.Rank(this), LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>Every definition, ordered by path, then source order with containers before their members.</summary>
    public IReadOnlyList<SymbolDefinition> Definitions { get; }

    /// <summary>Every edge, ordered by file path, line, kind and target.</summary>
    public IReadOnlyList<SymbolEdge> Edges { get; private set; } = [];

    /// <summary>Every file that was added, with or without symbols, ordered by path.</summary>
    public IReadOnlyList<string> Files { get; }

    /// <summary>How the references resolved.</summary>
    public SymbolGraphStatistics Statistics { get; private set; } = new(0, 0, 0, 0, 0);

    /// <summary>The PageRank ranking with default settings, computed on first use.</summary>
    public SymbolRanking Ranking => _ranking.Value;

    /// <summary>A definition by id.</summary>
    /// <param name="symbolId">The id.</param>
    /// <returns>The definition, or null.</returns>
    public SymbolDefinition? GetDefinition(long symbolId) => _byId.TryGetValue(symbolId, out var index) ? Definitions[index] : null;

    /// <summary>The definitions in one file, in source order.</summary>
    /// <param name="path">The file path.</param>
    /// <returns>The definitions; empty for an unknown file.</returns>
    public IReadOnlyList<SymbolDefinition> DefinitionsIn(string path) =>
        _fileRanges.TryGetValue(path ?? string.Empty, out var range)
            ? Definitions.Skip(range.Start).Take(range.Count).ToList()
            : [];

    /// <summary>The definition that contains another one (a method's class, a class's namespace), or null at file scope.</summary>
    /// <param name="symbolId">The id.</param>
    /// <returns>The enclosing definition, or null.</returns>
    public SymbolDefinition? ParentOf(long symbolId) =>
        _byId.TryGetValue(symbolId, out var index) && _parents[index] >= 0 ? Definitions[_parents[index]] : null;

    /// <summary>
    /// The definitions with a name: an exact, case-sensitive match on <see cref="SymbolDefinition.Name"/> (constructors
    /// included), or, for a qualified name such as <c>OrderService.Place</c> or <c>linalg::Matrix</c> with no exact match,
    /// the definitions of the last part whose container (or file) is named by the part before it. Ordered by path and line.
    /// </summary>
    /// <param name="name">The name.</param>
    /// <returns>The definitions; empty when none match.</returns>
    public IReadOnlyList<SymbolDefinition> FindDefinitions(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return [];
        }

        if (_byName.TryGetValue(name, out var exact))
        {
            return [.. exact.Select(i => Definitions[i])];
        }

        var colon = name.LastIndexOf("::", StringComparison.Ordinal);
        var dot = name.LastIndexOf('.');
        var (cut, width) = colon > dot ? (colon, 2) : (dot, 1);
        if (cut <= 0 || cut + width >= name.Length || !_byName.TryGetValue(name[(cut + width)..], out var lastPart))
        {
            return [];
        }

        var head = name[..cut];
        var qualifier = head[(Math.Max(head.LastIndexOf('.'), head.LastIndexOf(':')) + 1)..];
        return [.. lastPart.Select(i => Definitions[i]).Where(d => SymbolGraphBuilder.ContainerMatches(d, qualifier))];
    }

    /// <summary>The edges that point at a definition (every resolved use of it), ordered by file and line.</summary>
    /// <param name="symbolId">The definition's id.</param>
    /// <returns>The edges; empty when nothing refers to it.</returns>
    public IReadOnlyList<SymbolEdge> FindReferences(long symbolId) =>
        _incoming.TryGetValue(symbolId, out var edges) ? [.. edges.Select(i => Edges[i])] : [];

    /// <summary>The edges that start in a definition (what its code refers to), ordered by file and line.</summary>
    /// <param name="symbolId">The definition's id.</param>
    /// <returns>The edges; empty when its code refers to nothing in the repository.</returns>
    public IReadOnlyList<SymbolEdge> ReferencesFrom(long symbolId) =>
        _outgoing.TryGetValue(symbolId, out var edges) ? [.. edges.Select(i => Edges[i])] : [];

    /// <summary>
    /// The definitions whose code calls (or constructs) a definition: the distinct sources of its
    /// <see cref="SymbolReferenceKind.Call"/> edges, ordered by path and line. Calls from file-level code have no source
    /// definition and are only in <see cref="FindReferences"/>.
    /// </summary>
    /// <param name="symbolId">The called definition's id.</param>
    /// <returns>The callers; empty when nothing calls it.</returns>
    public IReadOnlyList<SymbolDefinition> CallersOf(long symbolId)
    {
        if (!_incoming.TryGetValue(symbolId, out var edges))
        {
            return [];
        }

        var callers = new SortedSet<int>();
        foreach (var index in edges)
        {
            var edge = Edges[index];
            if (edge.Kind == SymbolReferenceKind.Call && edge.FromSymbolId is { } from && _byId.TryGetValue(from, out var caller))
            {
                callers.Add(caller);
            }
        }

        return [.. callers.Select(i => Definitions[i])];
    }

    /// <summary>The index of a definition in <see cref="Definitions"/>, or -1.</summary>
    internal int IndexOf(long symbolId) => _byId.TryGetValue(symbolId, out var index) ? index : -1;

    /// <summary>The index of the enclosing definition, or -1.</summary>
    internal int ParentIndex(int index) => _parents[index];

    /// <summary>The definitions a reference to <paramref name="name"/> may resolve to (constructors excluded).</summary>
    internal int[] CandidatesNamed(string name) => _candidatesByName.TryGetValue(name, out var candidates) ? candidates : s_none;

    internal string PartitionOf(string path)
    {
        if (!_partitions.TryGetValue(path, out var partition))
        {
            partition = _partitionOf(path) ?? string.Empty;
            _partitions[path] = partition;
        }

        return partition;
    }

    /// <summary>The innermost definition in a file whose lines contain a line, or -1. Used while building only.</summary>
    internal int InnermostAt(string path, int line)
    {
        if (!_fileRanges.TryGetValue(path, out var range))
        {
            return -1;
        }

        if (!_innermost.TryGetValue(path, out var lines))
        {
            var last = 0;
            for (var i = range.Start; i < range.Start + range.Count; i++)
            {
                last = Math.Max(last, Definitions[i].EndLine);
            }

            lines = new int[last + 1];
            Array.Fill(lines, -1);
            for (var i = range.Start; i < range.Start + range.Count; i++)
            {
                // Outer definitions come first, so inner ones overwrite them.
                var definition = Definitions[i];
                Array.Fill(lines, i, definition.StartLine, definition.EndLine - definition.StartLine + 1);
            }

            _innermost[path] = lines;
        }

        return line >= 0 && line < lines.Length ? lines[line] : -1;
    }

    internal void SetEdges(List<SymbolEdge> edges, SymbolGraphStatistics statistics)
    {
        Edges = edges;
        Statistics = statistics;
        _innermost.Clear();
        var incoming = new Dictionary<long, List<int>>();
        var outgoing = new Dictionary<long, List<int>>();
        for (var i = 0; i < edges.Count; i++)
        {
            Add(incoming, edges[i].ToSymbolId, i);
            if (edges[i].FromSymbolId is { } from)
            {
                Add(outgoing, from, i);
            }
        }

        _incoming = incoming.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray());
        _outgoing = outgoing.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray());

        static void Add(Dictionary<long, List<int>> map, long key, int index)
        {
            if (!map.TryGetValue(key, out var list))
            {
                list = [];
                map[key] = list;
            }

            list.Add(index);
        }
    }

    private static Dictionary<string, int[]> Index(List<SymbolDefinition> definitions, Func<SymbolDefinition, bool> include)
    {
        var map = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (var i = 0; i < definitions.Count; i++)
        {
            if (!include(definitions[i]))
            {
                continue;
            }

            if (!map.TryGetValue(definitions[i].Name, out var list))
            {
                list = [];
                map[definitions[i].Name] = list;
            }

            list.Add(i);
        }

        return map.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray(), StringComparer.Ordinal);
    }
}
