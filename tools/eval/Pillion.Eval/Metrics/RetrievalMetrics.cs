using Pillion.Eval.Golden;

namespace Pillion.Eval.Metrics;

/// <summary>One ranked search result as the metrics see it: its corpus-relative file and its chunk breadcrumb.</summary>
/// <param name="File">The corpus-relative path, forward slashes.</param>
/// <param name="Breadcrumb">The chunk breadcrumb.</param>
internal sealed record RankedResult(string File, string Breadcrumb);

/// <summary>
/// The retrieval metrics of design section 15. A query's file rank is the 1-based position of the first result in one of
/// its expected files (0 when none of the first k is); its symbol rank is the position of the first result whose breadcrumb
/// contains one of its expected symbols. Recall at k is the share of queries whose rank is between 1 and k (a hit in the
/// first k), and the mean reciprocal rank is the mean of 1/rank with 0 for a miss in the first k.
/// </summary>
internal static class RetrievalMetrics
{
    /// <summary>The cut-off of the gate metric (recall at 10).</summary>
    public const int K = 10;

    /// <summary>Finds the rank of the first result in an expected file.</summary>
    /// <param name="results">The results, best first.</param>
    /// <param name="expectedFiles">The expected corpus-relative files.</param>
    /// <param name="k">How many results count.</param>
    /// <returns>The 1-based rank, or 0 when none of the first <paramref name="k"/> results is in an expected file.</returns>
    public static int FirstFileRank(IReadOnlyList<RankedResult> results, IReadOnlyCollection<string> expectedFiles, int k = K)
    {
        ArgumentNullException.ThrowIfNull(results);
        ArgumentNullException.ThrowIfNull(expectedFiles);
        var wanted = new HashSet<string>(expectedFiles.Select(NormalizePath), StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < Math.Min(k, results.Count); i++)
        {
            if (wanted.Contains(NormalizePath(results[i].File)))
            {
                return i + 1;
            }
        }

        return 0;
    }

    /// <summary>Finds the rank of the first result whose breadcrumb contains an expected symbol.</summary>
    /// <param name="results">The results, best first.</param>
    /// <param name="expectedSymbols">The expected symbols.</param>
    /// <param name="k">How many results count.</param>
    /// <returns>The 1-based rank, or 0 when none of the first <paramref name="k"/> results matches (or there are no symbols).</returns>
    public static int FirstSymbolRank(IReadOnlyList<RankedResult> results, IReadOnlyCollection<string> expectedSymbols, int k = K)
    {
        ArgumentNullException.ThrowIfNull(results);
        ArgumentNullException.ThrowIfNull(expectedSymbols);
        var symbols = expectedSymbols.Select(BreadcrumbSymbols.SplitSymbol).Where(s => s.Count > 0).ToList();
        if (symbols.Count == 0)
        {
            return 0;
        }

        for (var i = 0; i < Math.Min(k, results.Count); i++)
        {
            var names = BreadcrumbSymbols.Names(results[i].Breadcrumb);
            if (symbols.Any(s => BreadcrumbSymbols.Contains(names, s)))
            {
                return i + 1;
            }
        }

        return 0;
    }

    /// <summary>The share of queries with a hit in the first k.</summary>
    /// <param name="ranks">One rank per query (0 = no hit).</param>
    /// <param name="k">The cut-off.</param>
    /// <returns>A value in [0, 1]; 0 for no queries.</returns>
    public static double RecallAt(IReadOnlyCollection<int> ranks, int k = K)
    {
        ArgumentNullException.ThrowIfNull(ranks);
        return ranks.Count == 0 ? 0 : ranks.Count(r => r >= 1 && r <= k) / (double)ranks.Count;
    }

    /// <summary>The mean reciprocal rank, counting only ranks within the cut-off.</summary>
    /// <param name="ranks">One rank per query (0 = no hit).</param>
    /// <param name="k">The cut-off.</param>
    /// <returns>A value in [0, 1]; 0 for no queries.</returns>
    public static double MeanReciprocalRank(IReadOnlyCollection<int> ranks, int k = K)
    {
        ArgumentNullException.ThrowIfNull(ranks);
        return ranks.Count == 0 ? 0 : ranks.Sum(r => r >= 1 && r <= k ? 1.0 / r : 0) / ranks.Count;
    }

    /// <summary>The nearest-rank percentile.</summary>
    /// <param name="values">The values (any order).</param>
    /// <param name="percent">The percentile, in (0, 100].</param>
    /// <returns>The smallest value with at least <paramref name="percent"/>% of the values at or below it; 0 for no values.</returns>
    public static double Percentile(IReadOnlyCollection<double> values, double percent)
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(percent);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(percent, 100);
        if (values.Count == 0)
        {
            return 0;
        }

        var sorted = values.Order().ToArray();
        var index = (int)Math.Ceiling(percent / 100 * sorted.Length) - 1;
        return sorted[Math.Clamp(index, 0, sorted.Length - 1)];
    }

    /// <summary>Normalizes a corpus-relative path for comparison (forward slashes, no leading <c>./</c> or slash).</summary>
    /// <param name="path">The path.</param>
    /// <returns>The normalized path.</returns>
    public static string NormalizePath(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var normalized = path.Replace('\\', '/').Trim();
        while (normalized.StartsWith("./", StringComparison.Ordinal))
        {
            normalized = normalized[2..];
        }

        return normalized.TrimStart('/');
    }
}
