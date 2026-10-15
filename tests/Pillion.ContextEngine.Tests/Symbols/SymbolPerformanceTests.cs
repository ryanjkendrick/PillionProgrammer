using System.Diagnostics;
using Pillion.ContextEngine.Symbols;
using Xunit.Abstractions;

namespace Pillion.ContextEngine.Tests.Symbols;

/// <summary>
/// Benchmark-style tests with bounds generous enough for a shared CI runner; the measured figures are logged. The map
/// for a 100,000-line repository should render well under a second once the files have been extracted.
/// </summary>
public sealed class SymbolPerformanceTests(ITestOutputHelper output)
{
    private static readonly TimeSpan s_bound = TimeSpan.FromSeconds(10);

    [Fact]
    public void A_100000_line_csharp_file_extracts_and_maps_within_the_bound()
    {
        var text = SyntheticRepository.SingleFile(100_000);
        var extractor = new FileSymbolExtractor();
        extractor.Extract("warmup.cs", SyntheticRepository.SingleFile(2_000));

        var extract = Stopwatch.StartNew();
        var extraction = extractor.Extract("src/Generated.cs", text);
        extract.Stop();

        var (graph, map, build, rank, render) = Map([extraction]);

        var lines = text.Count(c => c == '\n');
        output.WriteLine($"{lines:N0} lines, {text.Length / 1024.0 / 1024.0:F1} MB of C#: {extraction.Definitions.Count:N0} definitions and {extraction.References.Count:N0} references extracted in {extract.Elapsed.TotalMilliseconds:N0} ms");
        output.WriteLine($"graph: {graph.Edges.Count:N0} edges built in {build.TotalMilliseconds:N0} ms; ranked in {rank.TotalMilliseconds:N0} ms ({graph.Ranking.SymbolIterations} iterations); map of {map.TokenCount} tokens rendered in {render.TotalMilliseconds:N0} ms");
        Assert.True(lines >= 100_000);
        Assert.True(extraction.IsComplete);
        Assert.True(extraction.Definitions.Count > 10_000);
        Assert.True(graph.Edges.Count > 10_000);
        Assert.True(extract.Elapsed < s_bound, $"Extraction took {extract.Elapsed}.");
        Assert.True(build + rank + render < s_bound, $"The map took {build + rank + render}.");
    }

    [Fact]
    public void A_100000_line_repository_maps_within_the_bound_after_extraction()
    {
        var files = SyntheticRepository.Files(100, 1_000);
        var extractor = new FileSymbolExtractor();
        extractor.Extract("warmup.cs", SyntheticRepository.SingleFile(2_000));

        var extract = Stopwatch.StartNew();
        var extractions = files.Select(f => extractor.Extract(f.Path, f.Text)).ToList();
        extract.Stop();

        var (graph, map, build, rank, render) = Map(extractions);

        var lines = files.Sum(f => f.Text.Count(c => c == '\n'));
        output.WriteLine($"{files.Count} files, {lines:N0} lines: {graph.Definitions.Count:N0} definitions and {graph.Statistics.References:N0} references extracted in {extract.Elapsed.TotalMilliseconds:N0} ms");
        output.WriteLine($"resolution: {graph.Statistics}");
        output.WriteLine($"graph built in {build.TotalMilliseconds:N0} ms; ranked in {rank.TotalMilliseconds:N0} ms ({graph.Ranking.SymbolIterations} symbol and {graph.Ranking.FileIterations} file iterations); map rendered in {render.TotalMilliseconds:N0} ms; total after extraction {(build + rank + render).TotalMilliseconds:N0} ms");
        output.WriteLine(map.Text);
        Assert.True(lines >= 100_000);
        Assert.True(graph.Ranking.SymbolsConverged);
        Assert.True(map.TokenCount <= RepoMapOptions.Default.MaxTokens);
        Assert.True(extract.Elapsed < s_bound * 3, $"Extraction took {extract.Elapsed}.");
        Assert.True(build + rank + render < s_bound, $"The map took {build + rank + render}.");
    }

    private static (SymbolGraph Graph, RepoMap Map, TimeSpan Build, TimeSpan Rank, TimeSpan Render) Map(IEnumerable<SymbolExtraction> extractions)
    {
        var stopwatch = Stopwatch.StartNew();
        var builder = new SymbolGraphBuilder();
        foreach (var extraction in extractions)
        {
            builder.AddFile(extraction);
        }

        var graph = builder.Build();
        var build = stopwatch.Elapsed;

        stopwatch.Restart();
        _ = graph.Ranking;
        var rank = stopwatch.Elapsed;

        stopwatch.Restart();
        var map = RepoMapRenderer.Render(graph);
        var render = stopwatch.Elapsed;
        return (graph, map, build, rank, render);
    }
}
