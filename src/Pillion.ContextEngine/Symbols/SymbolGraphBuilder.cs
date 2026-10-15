using Pillion.ContextEngine.Chunking;

namespace Pillion.ContextEngine.Symbols;

/// <summary>
/// Resolves references to definitions by name across a repository and builds the <see cref="SymbolGraph"/>. Holds the
/// latest extraction per file, so the indexing pipeline can replace or remove one file and rebuild; building is
/// independent of the order files were added in.
/// </summary>
/// <remarks>
/// <para>Resolution rules, for each reference:</para>
/// <list type="number">
/// <item>Candidates are the definitions with exactly the referenced name (ordinal comparison; constructors are not
/// candidates, a construction resolves to its type). A dotted or <c>::</c> name with no match is retried as its last
/// segment, qualified by the segment before it.</item>
/// <item>Candidates whose kind cannot be what the reference names are dropped (<see cref="SymbolKinds.IsCompatible"/>:
/// a type use names a type, a call names a callable or a type).</item>
/// <item>With a qualifier (<c>Point</c> in <c>Point::new</c>), candidates whose container ends with that name, or that
/// live in a file whose stem is that name, are preferred when there are any (container-qualified resolution).</item>
/// <item>An unqualified <see cref="SymbolReferenceKind.Use"/> (a bare identifier in C# or VB) resolves only within its
/// own file, where its field or property almost always is.</item>
/// <item>Otherwise candidates in the same file are preferred, then candidates in the same partition, then any.</item>
/// <item>One candidate gives one edge; up to <see cref="SymbolGraphOptions.MaxCandidates"/> give one edge each, marked
/// ambiguous; more give none (counted as too ambiguous). References with no candidate are counted as unresolved.</item>
/// </list>
/// <para>Not thread-safe; the graphs it builds are immutable and thread-safe.</para>
/// </remarks>
public sealed class SymbolGraphBuilder
{
    private readonly Dictionary<string, SymbolExtraction> _files = new(StringComparer.Ordinal);

    /// <summary>Creates a builder.</summary>
    /// <param name="options">The partition lookup and the ambiguity cap; <see cref="SymbolGraphOptions.Default"/> when null.</param>
    public SymbolGraphBuilder(SymbolGraphOptions? options = null)
    {
        Options = options ?? SymbolGraphOptions.Default;
    }

    /// <summary>The options.</summary>
    public SymbolGraphOptions Options { get; }

    /// <summary>The number of files held.</summary>
    public int FileCount => _files.Count;

    /// <summary>Adds a file's extraction, replacing any earlier one for the same path.</summary>
    /// <param name="extraction">The extraction.</param>
    public void AddFile(SymbolExtraction extraction)
    {
        ArgumentNullException.ThrowIfNull(extraction);
        _files[extraction.FilePath] = extraction;
    }

    /// <summary>Removes a file.</summary>
    /// <param name="path">The file path.</param>
    /// <returns>True when the file was held.</returns>
    public bool RemoveFile(string path) => _files.Remove(path ?? string.Empty);

    /// <summary>Builds a graph from loose definitions and references, grouped by their file paths.</summary>
    /// <param name="definitions">The definitions.</param>
    /// <param name="references">The references.</param>
    /// <param name="options">The options.</param>
    /// <returns>The graph.</returns>
    public static SymbolGraph Build(IEnumerable<SymbolDefinition> definitions, IEnumerable<SymbolReference> references, SymbolGraphOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        ArgumentNullException.ThrowIfNull(references);
        var builder = new SymbolGraphBuilder(options);
        var byFile = new Dictionary<string, (List<SymbolDefinition> Definitions, List<SymbolReference> References)>(StringComparer.Ordinal);
        foreach (var definition in definitions)
        {
            Group(definition.FilePath).Definitions.Add(definition);
        }

        foreach (var reference in references)
        {
            Group(reference.FilePath).References.Add(reference);
        }

        foreach (var (path, (fileDefinitions, fileReferences)) in byFile)
        {
            var language = fileDefinitions.FirstOrDefault()?.Language ?? fileReferences.FirstOrDefault()?.Language ?? LanguageIds.Text;
            builder.AddFile(new SymbolExtraction(path, language, fileDefinitions, fileReferences));
        }

        return builder.Build();

        (List<SymbolDefinition> Definitions, List<SymbolReference> References) Group(string path)
        {
            if (!byFile.TryGetValue(path, out var group))
            {
                group = ([], []);
                byFile[path] = group;
            }

            return group;
        }
    }

    /// <summary>Resolves every reference and builds the graph.</summary>
    /// <returns>The graph.</returns>
    public SymbolGraph Build()
    {
        var files = _files.Values.OrderBy(file => file.FilePath, StringComparer.Ordinal).ToList();
        var partitionOf = Options.PartitionOf ?? SymbolGraphOptions.TopLevelFolder;

        // Definitions in a stable order: by path, then source order with containers before their members.
        var seen = new HashSet<long>();
        var definitions = new List<SymbolDefinition>();
        foreach (var file in files)
        {
            foreach (var definition in file.Definitions.OrderBy(d => d, DefinitionOrder.Instance))
            {
                if (seen.Add(definition.Id))
                {
                    definitions.Add(definition);
                }
            }
        }

        var graph = new SymbolGraph(definitions, files.Select(f => f.FilePath).ToList(), partitionOf);
        var resolver = new Resolver(graph, Options);
        var edges = new List<SymbolEdge>();
        foreach (var file in files)
        {
            foreach (var reference in file.References)
            {
                resolver.Resolve(reference, edges);
            }
        }

        edges.Sort(EdgeOrder);
        graph.SetEdges(edges, resolver.Statistics(files.Sum(f => f.References.Count)));
        return graph;
    }

    private static int EdgeOrder(SymbolEdge a, SymbolEdge b)
    {
        var order = string.CompareOrdinal(a.FilePath, b.FilePath);
        if (order == 0)
        {
            order = a.Line.CompareTo(b.Line);
        }

        if (order == 0)
        {
            order = a.Kind.CompareTo(b.Kind);
        }

        if (order == 0)
        {
            order = a.ToSymbolId.CompareTo(b.ToSymbolId);
        }

        if (order == 0)
        {
            order = Nullable.Compare(a.FromSymbolId, b.FromSymbolId);
        }

        return order;
    }

    /// <summary>Source order within a file, containers before what they contain.</summary>
    internal sealed class DefinitionOrder : IComparer<SymbolDefinition>
    {
        public static DefinitionOrder Instance { get; } = new();

        public int Compare(SymbolDefinition? a, SymbolDefinition? b)
        {
            if (ReferenceEquals(a, b))
            {
                return 0;
            }

            if (a is null || b is null)
            {
                return a is null ? -1 : 1;
            }

            var order = string.CompareOrdinal(a.FilePath, b.FilePath);
            if (order == 0)
            {
                order = a.StartLine.CompareTo(b.StartLine);
            }

            if (order == 0)
            {
                order = b.EndLine.CompareTo(a.EndLine);
            }

            if (order == 0)
            {
                order = SymbolKinds.IsContainer(b.Kind).CompareTo(SymbolKinds.IsContainer(a.Kind));
            }

            if (order == 0)
            {
                order = string.CompareOrdinal(a.Name, b.Name);
            }

            if (order == 0)
            {
                order = a.Kind.CompareTo(b.Kind);
            }

            return order != 0 ? order : a.Id.CompareTo(b.Id);
        }
    }

    /// <summary>Resolves references against a graph's definitions.</summary>
    private sealed class Resolver(SymbolGraph graph, SymbolGraphOptions options)
    {
        private readonly List<int> _candidates = [];
        private readonly List<int> _preferred = [];
        private int _resolved;
        private int _ambiguous;
        private int _tooAmbiguous;
        private int _unresolved;

        public void Resolve(SymbolReference reference, List<SymbolEdge> edges)
        {
            var name = reference.Name;
            var qualifier = reference.Qualifier;
            var byName = graph.CandidatesNamed(name);
            if (byName.Length == 0 && SplitQualified(name) is { } split)
            {
                (name, var splitQualifier) = split;
                qualifier ??= splitQualifier;
                byName = graph.CandidatesNamed(name);
            }

            _candidates.Clear();
            foreach (var index in byName)
            {
                if (SymbolKinds.IsCompatible(reference.Kind, graph.Definitions[index].Kind))
                {
                    _candidates.Add(index);
                }
            }

            if (_candidates.Count == 0)
            {
                _unresolved++;
                return;
            }

            if (qualifier is not null)
            {
                Narrow(index => ContainerMatches(graph.Definitions[index], qualifier));
            }

            if (reference.Kind == SymbolReferenceKind.Use && reference.Qualifier is null)
            {
                _candidates.RemoveAll(index => !string.Equals(graph.Definitions[index].FilePath, reference.FilePath, StringComparison.Ordinal));
                if (_candidates.Count == 0)
                {
                    _unresolved++;
                    return;
                }
            }
            else
            {
                if (!Narrow(index => string.Equals(graph.Definitions[index].FilePath, reference.FilePath, StringComparison.Ordinal)))
                {
                    var partition = graph.PartitionOf(reference.FilePath);
                    Narrow(index => string.Equals(graph.PartitionOf(graph.Definitions[index].FilePath), partition, StringComparison.Ordinal));
                }
            }

            if (_candidates.Count > options.MaxCandidates)
            {
                _tooAmbiguous++;
                return;
            }

            var from = graph.InnermostAt(reference.FilePath, reference.Line);
            long? fromId = from >= 0 ? graph.Definitions[from].Id : null;
            foreach (var index in _candidates)
            {
                edges.Add(new SymbolEdge(fromId, graph.Definitions[index].Id, reference.FilePath, reference.Line, reference.Kind)
                {
                    CandidateCount = _candidates.Count,
                });
            }

            if (_candidates.Count > 1)
            {
                _ambiguous++;
            }
            else
            {
                _resolved++;
            }
        }

        public SymbolGraphStatistics Statistics(int references) => new(references, _resolved, _ambiguous, _tooAmbiguous, _unresolved);

        /// <summary>Keeps the candidates that match, when any do; returns whether the list was narrowed to them.</summary>
        private bool Narrow(Func<int, bool> match)
        {
            _preferred.Clear();
            foreach (var index in _candidates)
            {
                if (match(index))
                {
                    _preferred.Add(index);
                }
            }

            if (_preferred.Count == 0 || _preferred.Count == _candidates.Count)
            {
                return _preferred.Count > 0;
            }

            _candidates.Clear();
            _candidates.AddRange(_preferred);
            return true;
        }

        private static (string Name, string Qualifier)? SplitQualified(string name)
        {
            var colon = name.LastIndexOf("::", StringComparison.Ordinal);
            var dot = name.LastIndexOf('.');
            var (cut, width) = colon > dot ? (colon, 2) : (dot, 1);
            if (cut <= 0 || cut + width >= name.Length)
            {
                return null;
            }

            var head = name[..cut];
            var previousColon = head.LastIndexOf("::", StringComparison.Ordinal);
            var previousDot = head.LastIndexOf('.');
            var start = Math.Max(previousColon >= 0 ? previousColon + 2 : 0, previousDot >= 0 ? previousDot + 1 : 0);
            return (name[(cut + width)..], head[start..]);
        }
    }

    /// <summary>
    /// Whether a definition's container ends with the qualifier (generic arguments ignored, and the last part of a dotted
    /// namespace accepted), or the definition's file is named after it.
    /// </summary>
    internal static bool ContainerMatches(SymbolDefinition definition, string qualifier)
    {
        var container = definition.Container;
        if (container.Length > 0)
        {
            var last = container[(container.LastIndexOf(Breadcrumbs.Separator, StringComparison.Ordinal) + 1)..].Trim();
            var generic = last.IndexOfAny(['<', '(', '[']);
            if (generic > 0)
            {
                last = last[..generic].TrimEnd();
            }

            if (string.Equals(last, qualifier, StringComparison.Ordinal)
                || string.Equals(LastPart(last), qualifier, StringComparison.Ordinal))
            {
                return true;
            }
        }

        var path = definition.FilePath;
        var fileName = path[(path.LastIndexOfAny(['/', '\\']) + 1)..];
        var dot = fileName.IndexOf('.', StringComparison.Ordinal);
        return string.Equals(dot > 0 ? fileName[..dot] : fileName, qualifier, StringComparison.Ordinal);
    }

    private static string LastPart(string name)
    {
        var cut = Math.Max(name.LastIndexOf('.'), name.LastIndexOf(':'));
        return cut >= 0 ? name[(cut + 1)..] : name;
    }
}

/// <summary>How the references of a build resolved.</summary>
/// <param name="References">All references seen.</param>
/// <param name="Resolved">References with exactly one candidate.</param>
/// <param name="Ambiguous">References with several candidates (one edge each, marked ambiguous).</param>
/// <param name="TooAmbiguous">References with more candidates than the cap (no edges).</param>
/// <param name="Unresolved">References with no candidate (names defined outside the repository, locals, typos).</param>
public sealed record SymbolGraphStatistics(int References, int Resolved, int Ambiguous, int TooAmbiguous, int Unresolved);
