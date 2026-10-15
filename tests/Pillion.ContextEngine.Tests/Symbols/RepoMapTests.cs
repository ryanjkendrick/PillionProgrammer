using Pillion.ContextEngine.Chunking;
using Pillion.ContextEngine.Symbols;

namespace Pillion.ContextEngine.Tests.Symbols;

public sealed class RepoMapTests
{
    private static readonly Lazy<SymbolGraph> s_synthetic = new(() =>
    {
        var builder = new SymbolGraphBuilder();
        foreach (var (path, text) in SyntheticRepository.Files(20, 300))
        {
            builder.AddFile(SymbolFixture.Extract(path, text));
        }

        return builder.Build();
    });

    private static SymbolGraph Fixtures()
    {
        var builder = new SymbolGraphBuilder();
        foreach (string fixture in FixtureDumpTests.Fixtures())
        {
            builder.AddFile(SymbolFixture.Extract(fixture));
        }

        return builder.Build();
    }

    [Theory]
    [InlineData(20)]
    [InlineData(60)]
    [InlineData(150)]
    [InlineData(400)]
    [InlineData(1000)]
    [InlineData(4000)]
    public void The_map_fits_the_budget_and_counts_its_tokens_exactly(int budget)
    {
        var options = RepoMapOptions.Default with { MaxTokens = budget };

        var map = RepoMapRenderer.Render(s_synthetic.Value, options);

        Assert.True(map.TokenCount <= budget, $"{map.TokenCount} tokens for a budget of {budget}.");
        Assert.Equal(map.Text.Length == 0 ? 0 : TokenCounters.ApproximateWordPiece(map.Text), map.TokenCount);
        Assert.False(map.IsComplete);
        if (budget >= 60)
        {
            Assert.NotEmpty(map.SymbolIds);

            // Nearly full: one more line would not have fitted for long.
            Assert.True(map.TokenCount > budget * 0.6, $"{map.TokenCount} tokens for a budget of {budget}.");
        }
    }

    [Fact]
    public void A_larger_budget_shows_everything_a_smaller_one_does()
    {
        var small = RepoMapRenderer.Render(s_synthetic.Value, RepoMapOptions.Default with { MaxTokens = 200 });
        var large = RepoMapRenderer.Render(s_synthetic.Value, RepoMapOptions.Default with { MaxTokens = 800 });

        Assert.Subset(large.SymbolIds.ToHashSet(), small.SymbolIds.ToHashSet());
        Assert.True(large.SymbolIds.Count > small.SymbolIds.Count);
    }

    [Fact]
    public void The_top_ranked_core_services_come_first()
    {
        var map = RepoMapRenderer.Render(s_synthetic.Value, RepoMapOptions.Default with { MaxTokens = 300 });

        // File 0 holds the services most of the generated calls go to.
        Assert.Equal("src/Module0/File0.cs", map.Files[0]);
        Assert.StartsWith("src/Module0/File0.cs\n  namespace Generated.Module0\n    class Service0_", map.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Focus_files_come_first_with_their_symbols()
    {
        var graph = s_synthetic.Value;
        var leastRanked = graph.Ranking.RankedFiles[^1];
        var unfocused = RepoMapRenderer.Render(graph, RepoMapOptions.Default with { MaxTokens = 300 });
        Assert.DoesNotContain(leastRanked, unfocused.Files);

        var focused = RepoMapRenderer.Render(graph, RepoMapOptions.Default with { MaxTokens = 300, FocusFiles = [leastRanked, "no/such/file.cs"] });

        Assert.Equal(leastRanked, focused.Files[0]);
        Assert.StartsWith(leastRanked + "\n", focused.Text, StringComparison.Ordinal);
        var focusIds = graph.DefinitionsIn(leastRanked).Select(d => d.Id).ToHashSet();
        Assert.True(focused.SymbolIds.Count(focusIds.Contains) >= 5);
        Assert.True(focused.TokenCount <= 300);
    }

    [Fact]
    public void The_map_is_stable_across_runs_and_graph_builds()
    {
        var options = RepoMapOptions.Default with { MaxTokens = 500 };
        var first = RepoMapRenderer.Render(s_synthetic.Value, options);

        var rebuilt = new SymbolGraphBuilder();
        foreach (var (path, text) in Enumerable.Reverse(SyntheticRepository.Files(20, 300)))
        {
            rebuilt.AddFile(SymbolFixture.Extract(path, text));
        }

        var second = RepoMapRenderer.Render(rebuilt.Build(), options);
        Assert.Equal(first.Text, second.Text);
        Assert.Equal(first.Text, RepoMapRenderer.Render(s_synthetic.Value, options).Text);
    }

    [Fact]
    public void Lines_are_paths_then_kind_name_signature_indented_by_nesting()
    {
        var graph = Fixtures();

        var map = RepoMapRenderer.Render(graph, RepoMapOptions.Default with { MaxTokens = 100_000, FocusFiles = ["src/Greeter.cs"] });

        Assert.True(map.IsComplete);
        var lines = map.Text.Split('\n');
        Assert.Equal("src/Greeter.cs", lines[0]);
        Assert.Equal("  namespace Fixtures.Sample", lines[1]);
        Assert.Equal("    class Greeter<T> : IDisposable where T : class", lines[2]);
        Assert.Contains("      method Greet(string name)", lines);
        Assert.Contains("      constructor Greeter(string prefix)", lines);
        Assert.Contains("      property Count", lines);
        Assert.Contains("  function load(path)", lines);
        Assert.Contains("  procedure GetOrders @Customer NVARCHAR(100)", lines);
        Assert.DoesNotContain(lines, line => line.Contains("module ", StringComparison.Ordinal) || line.Contains("enum-member", StringComparison.Ordinal));
        Assert.All(lines.Where(l => !l.StartsWith(' ')), path => Assert.Contains(path, map.Files));
        Assert.Equal(map.Files.Count, map.Files.Distinct().Count());
    }

    [Fact]
    public void A_budget_too_small_for_anything_gives_an_empty_map()
    {
        var map = RepoMapRenderer.Render(Fixtures(), RepoMapOptions.Default with { MaxTokens = 3 });

        Assert.Equal(string.Empty, map.Text);
        Assert.Empty(map.Files);
        Assert.False(map.IsComplete);

        var empty = RepoMapRenderer.Render(SymbolGraphBuilder.Build([], []));
        Assert.True(empty.IsComplete);
        Assert.Equal(string.Empty, empty.Text);
    }

    [Fact]
    public void Long_signatures_are_cut_to_the_line_length()
    {
        var parameters = string.Join(", ", Enumerable.Range(0, 30).Select(i => $"int parameter{i}"));
        var builder = new SymbolGraphBuilder();
        builder.AddFile(SymbolFixture.Extract("src/Long.cs", $"class Long {{ void Method({parameters}) {{ }} }}"));

        var map = RepoMapRenderer.Render(builder.Build(), RepoMapOptions.Default with { MaxLineLength = 80 });

        var line = map.Text.Split('\n').Single(l => l.Contains("method", StringComparison.Ordinal));
        Assert.Equal(80, line.Length);
        Assert.EndsWith("…", line, StringComparison.Ordinal);
    }
}
