using Pillion.ContextEngine.Symbols;

namespace Pillion.ContextEngine.Tests.Symbols;

public sealed class SymbolRankerTests
{
    private static SymbolDefinition Function(string path, string name) => new(path, name, SymbolKind.Function, string.Empty, 1, 3, $"def {name}()", "python");

    private static SymbolReference Call(string path, string name) => new()
    {
        FilePath = path,
        Line = 2,
        Name = name,
        Kind = SymbolReferenceKind.Call,
        Language = "python",
    };

    /// <summary>A, B and C call D; D calls E.</summary>
    private static SymbolGraph HandBuilt(bool reversed = false)
    {
        SymbolDefinition[] definitions = [Function("f1.py", "A"), Function("f2.py", "B"), Function("f3.py", "C"), Function("f4.py", "D"), Function("f5.py", "E")];
        SymbolReference[] references = [Call("f1.py", "D"), Call("f2.py", "D"), Call("f3.py", "D"), Call("f4.py", "E")];
        return reversed
            ? SymbolGraphBuilder.Build(definitions.Reverse(), references.Reverse())
            : SymbolGraphBuilder.Build(definitions, references);
    }

    [Fact]
    public void The_most_referenced_symbol_ranks_first_and_what_it_calls_second()
    {
        var graph = HandBuilt();
        var ranking = graph.Ranking;

        Assert.Equal(["D", "E", "A", "B", "C"], ranking.RankedSymbols.Select(d => d.Name));
        Assert.Equal(["f4.py", "f5.py", "f1.py", "f2.py", "f3.py"], ranking.RankedFiles);
        Assert.True(ranking.SymbolsConverged);
        Assert.True(ranking.FilesConverged);
        Assert.InRange(ranking.SymbolIterations, 2, 50);
        Assert.Equal(3, ranking.ReferringFilesOf(graph.FindDefinitions("D")[0].Id));
        Assert.True(ranking.ScoreOf(graph.FindDefinitions("B")[0].Id) > 0);
        Assert.Equal(ranking.ScoreOf(graph.FindDefinitions("B")[0].Id), ranking.ScoreOf(graph.FindDefinitions("C")[0].Id));
        Assert.Equal(0, ranking.ScoreOf(42));
    }

    [Fact]
    public void Ranking_is_deterministic_and_independent_of_input_order()
    {
        var one = HandBuilt().Ranking;
        var two = HandBuilt(reversed: true).Ranking;

        Assert.Equal(one.RankedSymbols.Select(d => d.Id), two.RankedSymbols.Select(d => d.Id));
        foreach (var definition in one.RankedSymbols)
        {
            Assert.Equal(one.ScoreOf(definition.Id), two.ScoreOf(definition.Id));
        }

        var files = SyntheticRepository.Files(8, 200).Select(f => SymbolFixture.Extract(f.Path, f.Text)).ToList();
        var forward = new SymbolGraphBuilder();
        var backward = new SymbolGraphBuilder();
        files.ForEach(forward.AddFile);
        Enumerable.Reverse(files).ToList().ForEach(backward.AddFile);
        Assert.Equal(forward.Build().Ranking.RankedSymbols.Select(d => d.Id), backward.Build().Ranking.RankedSymbols.Select(d => d.Id));
    }

    [Fact]
    public void PageRank_sums_to_one_is_uniform_on_a_cycle_and_favours_the_hub_of_a_star()
    {
        var cycle = SymbolRanker.Compute(3, new() { [(0, 1)] = 1, [(1, 2)] = 1, [(2, 0)] = 1 }, PageRankOptions.Default);
        Assert.True(cycle.Converged);
        Assert.All(cycle.Ranks, rank => Assert.Equal(1.0 / 3, rank, 9));

        var star = SymbolRanker.Compute(5, new() { [(1, 0)] = 1, [(2, 0)] = 1, [(3, 0)] = 1, [(4, 0)] = 1 }, PageRankOptions.Default);
        Assert.Equal(1.0, star.Ranks.Sum(), 9);
        Assert.All(star.Ranks.Skip(1), rank => Assert.True(star.Ranks[0] > rank * 3));

        // Weights matter: the heavier edge carries more rank.
        var weighted = SymbolRanker.Compute(3, new() { [(0, 1)] = 3, [(0, 2)] = 1 }, PageRankOptions.Default);
        Assert.True(weighted.Ranks[1] > weighted.Ranks[2]);

        Assert.Empty(SymbolRanker.Compute(0, [], PageRankOptions.Default).Ranks);
    }

    [Fact]
    public void Iteration_stops_at_the_cap_when_it_has_not_converged()
    {
        var edges = new Dictionary<(int From, int To), double> { [(0, 1)] = 1, [(1, 2)] = 1, [(2, 0)] = 1, [(0, 2)] = 5 };
        var capped = SymbolRanker.Compute(3, edges, PageRankOptions.Default with { MaxIterations = 2 });

        Assert.Equal(2, capped.Iterations);
        Assert.False(capped.Converged);

        var chain = SymbolRanker.Compute(3, new() { [(0, 1)] = 1, [(1, 2)] = 1 }, PageRankOptions.Default);
        Assert.True(chain.Converged);
        Assert.True(chain.Iterations < 50);
        Assert.True(chain.Ranks[2] > chain.Ranks[1] && chain.Ranks[1] > chain.Ranks[0]);
    }

    [Fact]
    public void Definitions_used_from_many_files_rank_above_definitions_used_often_from_one()
    {
        // X is called once from each of three files; Y three times from one function in one file.
        SymbolDefinition[] definitions =
        [
            Function("lib/x.py", "X"), Function("lib/y.py", "Y"),
            Function("a.py", "Fa"), Function("b.py", "Fb"), Function("c.py", "Fc"), Function("d.py", "Fd"),
        ];
        SymbolReference[] references =
        [
            Call("a.py", "X"), Call("b.py", "X"), Call("c.py", "X"),
            Call("d.py", "Y"), Call("d.py", "Y") with { Line = 3 }, Call("d.py", "Y") with { Line = 1 },
        ];

        var graph = SymbolGraphBuilder.Build(definitions, references);
        var ranking = graph.Ranking;
        var x = graph.FindDefinitions("X")[0].Id;
        var y = graph.FindDefinitions("Y")[0].Id;

        Assert.Equal(3, ranking.ReferringFilesOf(x));
        Assert.Equal(1, ranking.ReferringFilesOf(y));
        Assert.True(ranking.ScoreOf(x) > ranking.ScoreOf(y));
        Assert.Equal("X", ranking.RankedSymbols[0].Name);
    }
}
