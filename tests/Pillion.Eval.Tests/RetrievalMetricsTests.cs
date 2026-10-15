using Pillion.Eval.Golden;
using Pillion.Eval.Metrics;
using Pillion.Eval.Retrieval;

namespace Pillion.Eval.Tests;

/// <summary>The metric computations, against expectations worked out by hand.</summary>
public sealed class RetrievalMetricsTests
{
    private static readonly RankedResult[] Results =
    [
        new("src/A.cs", "src/A.cs > Ns > Alpha > Run"),
        new("src/B.cs", "src/B.cs > Ns > Beta"),
        new("src/C.cs", "src/C.cs > Ns > Gamma<T> > Load (part 2 of 3)"),
        new("src/B.cs", "src/B.cs > Ns > Beta > Save"),
    ];

    [Fact]
    public void TheFileRank_IsThePositionOfTheFirstResultInAnExpectedFile()
    {
        Assert.Equal(2, RetrievalMetrics.FirstFileRank(Results, ["src/B.cs"]));
        Assert.Equal(1, RetrievalMetrics.FirstFileRank(Results, ["src/C.cs", "src/A.cs"]));
        Assert.Equal(3, RetrievalMetrics.FirstFileRank(Results, [@"src\C.cs"])); // separators and case do not matter
        Assert.Equal(3, RetrievalMetrics.FirstFileRank(Results, ["SRC/c.cs"]));
        Assert.Equal(0, RetrievalMetrics.FirstFileRank(Results, ["src/D.cs"]));
        Assert.Equal(0, RetrievalMetrics.FirstFileRank(Results, ["src/C.cs"], k: 2)); // beyond the cut-off is a miss
    }

    [Fact]
    public void TheSymbolRank_IsThePositionOfTheFirstBreadcrumbHoldingAnExpectedSymbol()
    {
        Assert.Equal(4, RetrievalMetrics.FirstSymbolRank(Results, ["Beta.Save"]));
        Assert.Equal(2, RetrievalMetrics.FirstSymbolRank(Results, ["Beta"])); // a type matches its header chunk
        Assert.Equal(3, RetrievalMetrics.FirstSymbolRank(Results, ["Gamma.Load"])); // generic arguments and part markers are ignored
        Assert.Equal(1, RetrievalMetrics.FirstSymbolRank(Results, ["Ns.Alpha"]));
        Assert.Equal(0, RetrievalMetrics.FirstSymbolRank(Results, ["Alpha.Save"])); // the parts must be consecutive in that order
        Assert.Equal(0, RetrievalMetrics.FirstSymbolRank(Results, ["A.cs"])); // the path is not a symbol
        Assert.Equal(0, RetrievalMetrics.FirstSymbolRank(Results, []));
        Assert.Equal(0, RetrievalMetrics.FirstSymbolRank(Results, ["Beta.Save"], k: 3));
    }

    [Fact]
    public void RecallAtK_IsTheShareOfQueriesWithAHitInTheFirstK()
    {
        int[] ranks = [1, 3, 0, 10, 11, 2];

        // Hits in the first 10: ranks 1, 3, 10 and 2 = 4 of 6. In the first 1: one. In the first 5: 1, 3, 2 = 3.
        Assert.Equal(4 / 6.0, RetrievalMetrics.RecallAt(ranks), 12);
        Assert.Equal(1 / 6.0, RetrievalMetrics.RecallAt(ranks, 1), 12);
        Assert.Equal(3 / 6.0, RetrievalMetrics.RecallAt(ranks, 5), 12);
        Assert.Equal(0, RetrievalMetrics.RecallAt([]));
    }

    [Fact]
    public void TheMeanReciprocalRank_CountsOnlyRanksWithinTheCutOff()
    {
        int[] ranks = [1, 2, 4, 0, 20];

        // (1 + 1/2 + 1/4 + 0 + 0) / 5 = 1.75 / 5 = 0.35; rank 20 is beyond 10 and counts as 0.
        Assert.Equal(0.35, RetrievalMetrics.MeanReciprocalRank(ranks), 12);

        // With a cut-off of 2: (1 + 1/2) / 5 = 0.3.
        Assert.Equal(0.3, RetrievalMetrics.MeanReciprocalRank(ranks, 2), 12);
        Assert.Equal(1.0, RetrievalMetrics.MeanReciprocalRank([1, 1, 1]), 12);
        Assert.Equal(0, RetrievalMetrics.MeanReciprocalRank([]));
    }

    [Fact]
    public void Percentiles_UseTheNearestRank()
    {
        double[] values = [50, 10, 40, 20, 30, 60, 70, 80, 90, 100];

        Assert.Equal(50, RetrievalMetrics.Percentile(values, 50)); // ceil(0.5 * 10) = 5th smallest
        Assert.Equal(100, RetrievalMetrics.Percentile(values, 95)); // ceil(9.5) = 10th
        Assert.Equal(90, RetrievalMetrics.Percentile(values, 90));
        Assert.Equal(10, RetrievalMetrics.Percentile(values, 1));
        Assert.Equal(7, RetrievalMetrics.Percentile([7], 95));
        Assert.Equal(0, RetrievalMetrics.Percentile([], 50));
    }

    [Fact]
    public void AModeSummary_AggregatesRanksLatenciesAndKinds()
    {
        GoldenQuery[] queries =
        [
            new() { Id = "a", Query = "q", ExpectedFiles = ["x"], ExpectedSymbols = ["S"], Kind = "where" },
            new() { Id = "b", Query = "q", ExpectedFiles = ["x"], ExpectedSymbols = ["S"], Kind = "where" },
            new() { Id = "c", Query = "q", ExpectedFiles = ["x"], Kind = "concept" },
            new() { Id = "d", Query = "q", ExpectedFiles = ["x"], ExpectedSymbols = ["S"], Kind = "concept" },
        ];
        var outcomes = new Dictionary<string, QueryModeResult>
        {
            ["a"] = new(1, 2, 10, false, []),
            ["b"] = new(5, 0, 20, true, []),
            ["c"] = new(0, 0, 30, false, []),
            ["d"] = new(2, 1, 40, false, []),
        };

        var summary = RetrievalRunner.Summarize("hybrid", queries, outcomes);

        Assert.Equal(4, summary.Queries);
        Assert.Equal(0.75, summary.FileRecallAt10, 12); // a, b, d
        Assert.Equal(0.25, summary.FileRecallAt1, 12); // a
        Assert.Equal(0.75, summary.FileRecallAt5, 12);
        Assert.Equal((1 + 0.2 + 0 + 0.5) / 4, summary.FileMrr, 12);
        Assert.Equal(3, summary.SymbolQueries); // c has no symbols and is left out of the symbol figures
        Assert.Equal(2 / 3.0, summary.SymbolRecallAt10, 12); // a and d
        Assert.Equal((0.5 + 0 + 1) / 3, summary.SymbolMrr, 12);
        Assert.Equal(20, summary.P50Ms);
        Assert.Equal(40, summary.P95Ms);
        Assert.Equal(1, summary.Degraded);
        Assert.Equal(new KindSummary(2, 1.0, 0.6, 0.5), summary.ByKind["where"]);
        Assert.Equal(new KindSummary(2, 0.5, 0.25, 1.0), summary.ByKind["concept"]);
    }
}
