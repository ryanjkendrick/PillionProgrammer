namespace Pillion.ContextEngine.Symbols;

/// <summary>Settings for <see cref="SymbolRanker"/>.</summary>
public sealed record PageRankOptions
{
    /// <summary>The default options: damping 0.85, at most 50 iterations, converged when the ranks (which sum to 1) move less than 1e-6 in total in one iteration.</summary>
    public static PageRankOptions Default { get; } = new();

    /// <summary>The damping factor (the chance of following an edge rather than jumping anywhere).</summary>
    public double Damping { get; init; } = 0.85;

    /// <summary>The most iterations.</summary>
    public int MaxIterations { get; init; } = 50;

    /// <summary>Convergence: the sum of absolute rank changes in one iteration below which iteration stops.</summary>
    public double Tolerance { get; init; } = 1e-6;
}

/// <summary>
/// Ranks a <see cref="SymbolGraph"/> (design section 7: "the repo map ranks files and symbols by PageRank over the
/// reference graph"). Two weighted PageRanks run, one over symbols and one over files:
/// <list type="bullet">
/// <item>Symbol graph: a node per definition plus a node per file for file-level code; an edge per resolved reference
/// from its source (the enclosing definition, else the file) to its target, weighted 1 for calls, type uses and
/// inheritance and 0.5 for imports and other uses, divided by the reference's candidate count, so an ambiguous
/// reference spreads its weight. Self-references are ignored.</item>
/// <item>File graph: a node per file; an edge from the referencing file to the defining file for each cross-file edge,
/// with the same weights.</item>
/// </list>
/// Both use the damping and convergence of <see cref="PageRankOptions"/>, with the rank of nodes without outgoing
/// edges spread evenly. The final score multiplies the PageRank by <c>1 + ln(1 + n)</c>, where <c>n</c> is the number of
/// other files that refer to the definition (or file), the boost for definitions used across the codebase. Results
/// are deterministic: nodes and edges are processed in a fixed order and ties are broken by path, line and name.
/// </summary>
public static class SymbolRanker
{
    /// <summary>Ranks a graph.</summary>
    /// <param name="graph">The graph.</param>
    /// <param name="options">The PageRank settings; <see cref="PageRankOptions.Default"/> when null.</param>
    /// <returns>The ranking.</returns>
    public static SymbolRanking Rank(SymbolGraph graph, PageRankOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(graph);
        options ??= PageRankOptions.Default;
        var definitions = graph.Definitions;
        var files = graph.Files;
        var fileIndex = new Dictionary<string, int>(files.Count, StringComparer.Ordinal);
        for (var i = 0; i < files.Count; i++)
        {
            fileIndex[files[i]] = i;
        }

        // Symbol graph: definitions, then one node per file for file-level code.
        var symbolEdges = new Dictionary<(int From, int To), double>();
        var fileEdges = new Dictionary<(int From, int To), double>();
        var symbolReferrers = new HashSet<(int Target, int File)>();
        var fileReferrers = new HashSet<(int Target, int File)>();
        foreach (var edge in graph.Edges)
        {
            var to = graph.IndexOf(edge.ToSymbolId);
            if (to < 0 || !fileIndex.TryGetValue(edge.FilePath, out var sourceFile))
            {
                continue;
            }

            var weight = KindWeight(edge.Kind) / Math.Max(1, edge.CandidateCount);
            var from = edge.FromSymbolId is { } fromId ? graph.IndexOf(fromId) : -1;
            from = from >= 0 ? from : definitions.Count + sourceFile;
            if (from != to)
            {
                symbolEdges[(from, to)] = symbolEdges.GetValueOrDefault((from, to)) + weight;
            }

            if (fileIndex.TryGetValue(definitions[to].FilePath, out var targetFile) && targetFile != sourceFile)
            {
                symbolReferrers.Add((to, sourceFile));
                fileReferrers.Add((targetFile, sourceFile));
                fileEdges[(sourceFile, targetFile)] = fileEdges.GetValueOrDefault((sourceFile, targetFile)) + weight;
            }
        }

        var symbolRun = Compute(definitions.Count + files.Count, symbolEdges, options);
        var fileRun = Compute(files.Count, fileEdges, options);

        var symbolBoost = new int[definitions.Count];
        foreach (var (target, _) in symbolReferrers)
        {
            symbolBoost[target]++;
        }

        var fileBoost = new int[files.Count];
        foreach (var (target, _) in fileReferrers)
        {
            fileBoost[target]++;
        }

        var symbolScores = new double[definitions.Count];
        for (var i = 0; i < definitions.Count; i++)
        {
            symbolScores[i] = symbolRun.Ranks[i] * (1 + Math.Log(1 + symbolBoost[i]));
        }

        var fileScores = new double[files.Count];
        for (var i = 0; i < files.Count; i++)
        {
            fileScores[i] = fileRun.Ranks[i] * (1 + Math.Log(1 + fileBoost[i]));
        }

        return new SymbolRanking(graph, symbolScores, symbolBoost, fileScores, symbolRun, fileRun);
    }

    /// <summary>The weight of an edge before it is split between candidates.</summary>
    internal static double KindWeight(SymbolReferenceKind kind) => kind switch
    {
        SymbolReferenceKind.Import or SymbolReferenceKind.Use => 0.5,
        _ => 1.0,
    };

    /// <summary>Weighted PageRank by power iteration; ranks sum to 1.</summary>
    internal static PageRankRun Compute(int count, Dictionary<(int From, int To), double> weighted, PageRankOptions options)
    {
        if (count == 0)
        {
            return new PageRankRun([], 0, true);
        }

        // A fixed edge order keeps floating-point sums, and so the result, identical run to run.
        var edges = weighted.OrderBy(pair => pair.Key.From).ThenBy(pair => pair.Key.To)
            .Select(pair => (pair.Key.From, pair.Key.To, pair.Value)).ToArray();
        var outWeight = new double[count];
        foreach (var (from, _, weight) in edges)
        {
            outWeight[from] += weight;
        }

        var damping = Math.Clamp(options.Damping, 0, 1);
        var ranks = new double[count];
        var next = new double[count];
        Array.Fill(ranks, 1.0 / count);
        var iterations = 0;
        var converged = false;
        while (iterations < Math.Max(1, options.MaxIterations))
        {
            iterations++;
            var dangling = 0.0;
            for (var i = 0; i < count; i++)
            {
                if (outWeight[i] == 0)
                {
                    dangling += ranks[i];
                }
            }

            var teleport = ((1 - damping) + (damping * dangling)) / count;
            Array.Fill(next, teleport);
            foreach (var (from, to, weight) in edges)
            {
                next[to] += damping * ranks[from] * weight / outWeight[from];
            }

            var change = 0.0;
            for (var i = 0; i < count; i++)
            {
                change += Math.Abs(next[i] - ranks[i]);
            }

            (ranks, next) = (next, ranks);
            if (change < options.Tolerance)
            {
                converged = true;
                break;
            }
        }

        return new PageRankRun(ranks, iterations, converged);
    }
}

/// <summary>One PageRank computation.</summary>
/// <param name="Ranks">The ranks by node, summing to 1.</param>
/// <param name="Iterations">The iterations run.</param>
/// <param name="Converged">Whether the tolerance was reached before the iteration cap.</param>
internal sealed record PageRankRun(double[] Ranks, int Iterations, bool Converged);

/// <summary>The scores of a graph's symbols and files, and both in rank order.</summary>
public sealed class SymbolRanking
{
    private readonly SymbolGraph _graph;
    private readonly double[] _symbolScores;
    private readonly int[] _referringFiles;
    private readonly double[] _fileScores;
    private readonly Dictionary<string, int> _fileIndex;

    internal SymbolRanking(SymbolGraph graph, double[] symbolScores, int[] referringFiles, double[] fileScores, PageRankRun symbols, PageRankRun files)
    {
        _graph = graph;
        _symbolScores = symbolScores;
        _referringFiles = referringFiles;
        _fileScores = fileScores;
        _fileIndex = graph.Files.Select((path, i) => (path, i)).ToDictionary(pair => pair.path, pair => pair.i, StringComparer.Ordinal);
        SymbolIterations = symbols.Iterations;
        SymbolsConverged = symbols.Converged;
        FileIterations = files.Iterations;
        FilesConverged = files.Converged;

        var order = Enumerable.Range(0, symbolScores.Length).ToArray();
        Array.Sort(order, CompareSymbols);
        SymbolOrder = order;
        RankedSymbols = [.. order.Select(i => graph.Definitions[i])];

        var fileOrder = Enumerable.Range(0, fileScores.Length).ToArray();
        Array.Sort(fileOrder, (a, b) =>
        {
            var byScore = fileScores[b].CompareTo(fileScores[a]);
            return byScore != 0 ? byScore : string.CompareOrdinal(graph.Files[a], graph.Files[b]);
        });
        RankedFiles = [.. fileOrder.Select(i => graph.Files[i])];
    }

    /// <summary>Every definition, best first (ties broken by path, line, name and id).</summary>
    public IReadOnlyList<SymbolDefinition> RankedSymbols { get; }

    /// <summary>Every file, best first (ties broken by path).</summary>
    public IReadOnlyList<string> RankedFiles { get; }

    /// <summary>The iterations of the symbol PageRank.</summary>
    public int SymbolIterations { get; }

    /// <summary>Whether the symbol PageRank converged before the iteration cap.</summary>
    public bool SymbolsConverged { get; }

    /// <summary>The iterations of the file PageRank.</summary>
    public int FileIterations { get; }

    /// <summary>Whether the file PageRank converged before the iteration cap.</summary>
    public bool FilesConverged { get; }

    /// <summary>Indices into the graph's definitions, best first.</summary>
    internal int[] SymbolOrder { get; }

    /// <summary>A definition's score (PageRank times the cross-file boost); 0 for an unknown id.</summary>
    /// <param name="symbolId">The id.</param>
    /// <returns>The score.</returns>
    public double ScoreOf(long symbolId) => _graph.IndexOf(symbolId) is var index and >= 0 ? _symbolScores[index] : 0;

    /// <summary>The number of other files that refer to a definition.</summary>
    /// <param name="symbolId">The id.</param>
    /// <returns>The count; 0 for an unknown id.</returns>
    public int ReferringFilesOf(long symbolId) => _graph.IndexOf(symbolId) is var index and >= 0 ? _referringFiles[index] : 0;

    /// <summary>A file's score; 0 for an unknown file.</summary>
    /// <param name="path">The file path.</param>
    /// <returns>The score.</returns>
    public double FileScoreOf(string path) => _fileIndex.TryGetValue(path ?? string.Empty, out var index) ? _fileScores[index] : 0;

    internal double ScoreAt(int index) => _symbolScores[index];

    internal int CompareSymbols(int a, int b)
    {
        var byScore = _symbolScores[b].CompareTo(_symbolScores[a]);
        if (byScore != 0)
        {
            return byScore;
        }

        var x = _graph.Definitions[a];
        var y = _graph.Definitions[b];
        var order = string.CompareOrdinal(x.FilePath, y.FilePath);
        if (order == 0)
        {
            order = x.StartLine.CompareTo(y.StartLine);
        }

        if (order == 0)
        {
            order = string.CompareOrdinal(x.Name, y.Name);
        }

        return order != 0 ? order : x.Id.CompareTo(y.Id);
    }
}
