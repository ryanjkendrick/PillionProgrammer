namespace Pillion.ContextEngine.Chunking.Core;

/// <summary>
/// A syntactic unit found by a language chunker, before the assembler gives it its final line range and fits it into
/// the token cap. Lines are 1-based and inclusive and cover the unit's own syntax (attributes included, leading
/// comments not: the assembler hands those to the unit from the gap before it).
/// </summary>
internal sealed class ChunkUnit
{
    private List<int>? _extraSplits;
    private List<Func<IEnumerable<SplitPoint>>>? _splitSources;

    public ChunkUnit(int startLine, int endLine, ChunkKind kind, IReadOnlyList<string> container, string? name = null)
    {
        StartLine = startLine;
        EndLine = Math.Max(startLine, endLine);
        Kind = kind;
        Container = container;
        if (!string.IsNullOrWhiteSpace(name))
        {
            Names.Add(name);
        }
    }

    public int StartLine { get; set; }

    public int EndLine { get; set; }

    public ChunkKind Kind { get; }

    /// <summary>The enclosing namespace and type names, outermost first.</summary>
    public IReadOnlyList<string> Container { get; }

    /// <summary>The unit's own name, or several when units were merged (a group of fields, for example).</summary>
    public List<string> Names { get; } = [];

    /// <summary>
    /// Units with the same non-null key that follow each other (nothing else between them) are merged into one, so a
    /// run of using directives, fields or sibling elements becomes one chunk (split again at the units' starts when it
    /// is over the cap).
    /// </summary>
    public string? MergeKey { get; init; }

    /// <summary>Adds a lazily computed set of lines where a part may begin when the unit is split, all of rank 0.</summary>
    public void AddSplitSource(Func<IEnumerable<int>> source) =>
        (_splitSources ??= []).Add(() => source().Select(line => new SplitPoint(line, 0)));

    /// <summary>
    /// Adds a lazily computed set of split points with ranks (statement nesting depth): when a part must end, the
    /// assembler prefers the lowest rank among the acceptable points, so parts break between top-level statements.
    /// </summary>
    public void AddRankedSplitSource(Func<IEnumerable<SplitPoint>> source) => (_splitSources ??= []).Add(source);

    /// <summary>Adds one line where a part may begin.</summary>
    public void AddSplit(int line) => (_extraSplits ??= []).Add(line);

    /// <summary>The segments after the path: the container, then the name or names.</summary>
    public IReadOnlyList<string> Segments()
    {
        if (Names.Count == 0)
        {
            return Container;
        }

        var name = Names.Count == 1 ? Names[0]
            : string.Join(", ", Names.Take(3)) + (Names.Count > 3 ? ", \u2026" : string.Empty);
        return [.. Container, name];
    }

    /// <summary>Takes in a unit that overlaps this one or follows it with the same merge key.</summary>
    public void Absorb(ChunkUnit other, bool keepNames)
    {
        EndLine = Math.Max(EndLine, other.EndLine);
        if (other.StartLine > StartLine)
        {
            AddSplit(other.StartLine);
        }

        if (keepNames)
        {
            Names.AddRange(other.Names);
        }

        if (other._extraSplits is not null)
        {
            (_extraSplits ??= []).AddRange(other._extraSplits);
        }

        if (other._splitSources is not null)
        {
            (_splitSources ??= []).AddRange(other._splitSources);
        }
    }

    /// <summary>
    /// The split points strictly inside (<paramref name="startLine"/>, <paramref name="endLine"/>], one per line with
    /// the lowest rank seen for it, sorted by line.
    /// </summary>
    public SplitPoint[] ResolveSplits(int startLine, int endLine)
    {
        var ranks = new SortedDictionary<int, int>();
        void Add(int line, int rank)
        {
            if (line > startLine && line <= endLine)
            {
                ranks[line] = ranks.TryGetValue(line, out var existing) ? Math.Min(existing, rank) : rank;
            }
        }

        if (_extraSplits is not null)
        {
            foreach (var line in _extraSplits)
            {
                Add(line, 0);
            }
        }

        if (_splitSources is not null)
        {
            foreach (var source in _splitSources)
            {
                foreach (var point in source())
                {
                    Add(point.Line, Math.Max(0, point.Rank));
                }
            }
        }

        return [.. ranks.Select(pair => new SplitPoint(pair.Key, pair.Value))];
    }
}

/// <summary>A line where a split part may begin; lower ranks (shallower statements) are preferred.</summary>
internal readonly record struct SplitPoint(int Line, int Rank);
