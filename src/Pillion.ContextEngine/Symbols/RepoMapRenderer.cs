using System.Text;
using Pillion.ContextEngine.Chunking;

namespace Pillion.ContextEngine.Symbols;

/// <summary>Settings for <see cref="RepoMapRenderer"/>.</summary>
public sealed record RepoMapOptions
{
    /// <summary>The default options: 1,024 tokens counted with <see cref="TokenCounters.ApproximateWordPiece"/>, no focus files.</summary>
    public static RepoMapOptions Default { get; } = new();

    /// <summary>The most tokens the rendered map may take (the context assembler's repo map cap).</summary>
    public int MaxTokens { get; init; } = 1024;

    /// <summary>Counts tokens the way the receiving model does (the chat model's tokenizer, not the embedding model's).</summary>
    public TokenCounter TokenCounter { get; init; } = TokenCounters.ApproximateWordPiece;

    /// <summary>
    /// Files whose symbols are included first, in this order and before any ranked symbol (open, recently edited or
    /// mentioned files, the retrieval boost of design section 7). Paths as stored; unknown paths are ignored.
    /// </summary>
    public IReadOnlyList<string> FocusFiles { get; init; } = [];

    /// <summary>Kinds left out of the map. Default: modules (the file line stands for them) and enum members.</summary>
    public IReadOnlySet<SymbolKind> ExcludedKinds { get; init; } = new HashSet<SymbolKind> { SymbolKind.Module, SymbolKind.EnumMember };

    /// <summary>The longest symbol line, indentation included; longer signatures end in an ellipsis.</summary>
    public int MaxLineLength { get; init; } = 160;
}

/// <summary>A rendered repo map.</summary>
/// <param name="Text">The outline: a file path, then its symbols indented by nesting, as <c>kind name(signature)</c>.</param>
/// <param name="TokenCount">The tokens <see cref="Text"/> takes in the options' counter.</param>
/// <param name="Files">The files in the map, in order.</param>
/// <param name="SymbolIds">The definitions in the map, in order (enclosing definitions shown for context included).</param>
/// <param name="IsComplete">True when every eligible symbol fitted the budget.</param>
public sealed record RepoMap(string Text, int TokenCount, IReadOnlyList<string> Files, IReadOnlyList<long> SymbolIds, bool IsComplete);

/// <summary>
/// Renders the repo map (design section 7): the top-ranked symbols of a <see cref="SymbolGraph"/> as a compact outline
/// within a token budget. Symbols are taken in order (all symbols of the focus files, best first, then every other
/// symbol by <see cref="SymbolRanking"/>), as many as fit; each brings its enclosing definitions for context. Files
/// appear focus files first, then by file rank; within a file, symbols are in source order and indented by nesting:
/// <code>
/// src/Orders/OrderService.cs
///   namespace Orders
///     class OrderService : IOrderService
///       method Place(Order order)
/// </code>
/// The output is deterministic for the same graph and options.
/// </summary>
public static class RepoMapRenderer
{
    private const string Indent = "  ";

    /// <summary>Renders the map with the graph's default ranking.</summary>
    /// <param name="graph">The graph.</param>
    /// <param name="options">The budget, counter and focus files; <see cref="RepoMapOptions.Default"/> when null.</param>
    /// <returns>The map.</returns>
    public static RepoMap Render(SymbolGraph graph, RepoMapOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(graph);
        return Render(graph, graph.Ranking, options);
    }

    /// <summary>Renders the map with a given ranking.</summary>
    /// <param name="graph">The graph.</param>
    /// <param name="ranking">A ranking of the same graph.</param>
    /// <param name="options">The budget, counter and focus files; <see cref="RepoMapOptions.Default"/> when null.</param>
    /// <returns>The map.</returns>
    public static RepoMap Render(SymbolGraph graph, SymbolRanking ranking, RepoMapOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(ranking);
        options ??= RepoMapOptions.Default;
        ArgumentNullException.ThrowIfNull(options.TokenCounter);
        return new Renderer(graph, ranking, options).Render();
    }

    private sealed class Renderer
    {
        private readonly SymbolGraph _graph;
        private readonly SymbolRanking _ranking;
        private readonly RepoMapOptions _options;
        private readonly List<int> _order = [];
        private readonly Dictionary<string, int> _fileOrder = new(StringComparer.Ordinal);
        private readonly string?[] _lines;

        public Renderer(SymbolGraph graph, SymbolRanking ranking, RepoMapOptions options)
        {
            _graph = graph;
            _ranking = ranking;
            _options = options;
            _lines = new string?[graph.Definitions.Count];

            // Focus files first, in the order given, then every file by rank.
            var focus = new HashSet<string>(StringComparer.Ordinal);
            foreach (var path in options.FocusFiles ?? [])
            {
                if (path is not null && graph.DefinitionsIn(path).Count > 0 && focus.Add(path))
                {
                    _fileOrder[path] = _fileOrder.Count;
                }
            }

            foreach (var path in ranking.RankedFiles)
            {
                _fileOrder.TryAdd(path, _fileOrder.Count);
            }

            // Symbols: the focus files' symbols (best first within each file), then everything else by rank.
            foreach (var path in focus)
            {
                var indices = graph.DefinitionsIn(path).Select(d => graph.IndexOf(d.Id)).Where(IsEligible).ToArray();
                Array.Sort(indices, ranking.CompareSymbols);
                _order.AddRange(indices);
            }

            foreach (var index in ranking.SymbolOrder)
            {
                if (IsEligible(index) && !focus.Contains(graph.Definitions[index].FilePath))
                {
                    _order.Add(index);
                }
            }
        }

        public RepoMap Render()
        {
            var count = _order.Count;
            var best = Build(0);
            if (!Fits(best))
            {
                return new RepoMap(string.Empty, 0, [], [], count == 0);
            }

            // Grow the prefix exponentially, then binary-search the largest one that fits.
            var low = 0;
            var high = -1;
            for (var take = Math.Min(16, count); take > 0; take = Math.Min(take * 2, count))
            {
                var candidate = Build(take);
                if (!Fits(candidate))
                {
                    high = take;
                    break;
                }

                low = take;
                best = candidate;
                if (take == count)
                {
                    break;
                }
            }

            if (high > 0)
            {
                while (high - low > 1)
                {
                    var middle = low + ((high - low) / 2);
                    var candidate = Build(middle);
                    if (Fits(candidate))
                    {
                        low = middle;
                        best = candidate;
                    }
                    else
                    {
                        high = middle;
                    }
                }
            }

            return best with { IsComplete = low == count };
        }

        private bool Fits(RepoMap map) => map.TokenCount <= _options.MaxTokens;

        private bool IsEligible(int index) => !_options.ExcludedKinds.Contains(_graph.Definitions[index].Kind);

        /// <summary>Renders the first <paramref name="take"/> symbols of the order with their enclosing definitions.</summary>
        private RepoMap Build(int take)
        {
            var shown = new HashSet<int>();
            for (var i = 0; i < take; i++)
            {
                for (var index = _order[i]; index >= 0; index = _graph.ParentIndex(index))
                {
                    if (IsEligible(index) && !shown.Add(index))
                    {
                        break;
                    }
                }
            }

            var byFile = shown
                .GroupBy(index => _graph.Definitions[index].FilePath, StringComparer.Ordinal)
                .OrderBy(group => _fileOrder.GetValueOrDefault(group.Key, int.MaxValue))
                .ThenBy(group => group.Key, StringComparer.Ordinal)
                .ToList();

            var text = new StringBuilder();
            var files = new List<string>(byFile.Count);
            var ids = new List<long>(shown.Count);
            foreach (var group in byFile)
            {
                if (text.Length > 0)
                {
                    text.Append('\n');
                }

                text.Append(group.Key);
                files.Add(group.Key);
                foreach (var index in group.Order())
                {
                    text.Append('\n').Append(Line(index));
                    ids.Add(_graph.Definitions[index].Id);
                }
            }

            var rendered = text.ToString();
            var tokens = rendered.Length == 0 ? 0 : _options.TokenCounter(rendered);
            return new RepoMap(rendered, tokens, files, ids, false);
        }

        private string Line(int index)
        {
            if (_lines[index] is { } cached)
            {
                return cached;
            }

            var depth = 1;
            for (var parent = _graph.ParentIndex(index); parent >= 0; parent = _graph.ParentIndex(parent))
            {
                if (IsEligible(parent))
                {
                    depth++;
                }
            }

            var definition = _graph.Definitions[index];
            var builder = new StringBuilder();
            for (var i = 0; i < depth; i++)
            {
                builder.Append(Indent);
            }

            builder.Append(SymbolKinds.Name(definition.Kind)).Append(' ').Append(SignatureText.FromName(definition.Signature, definition.Name));
            var limit = Math.Max(_options.MaxLineLength, (depth * Indent.Length) + 8);
            if (builder.Length > limit)
            {
                builder.Length = limit - 1;
                builder.Append('…');
            }

            var line = builder.ToString();
            _lines[index] = line;
            return line;
        }
    }
}
