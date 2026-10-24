namespace Pillion.ContextEngine.Retrieval;

/// <summary>
/// Reciprocal Rank Fusion (Cormack, Clarke and Buettcher, 2009): an item at 1-based rank <c>r</c> of a list scores
/// <c>weight / (k + r)</c>, summed over the lists it appears in. It needs no score calibration between lists, which is
/// why BM25 and cosine similarity can be fused without normalising either.
/// </summary>
public static class ReciprocalRankFusion
{
    /// <summary>The usual constant, and the design's (section 7): <c>k = 60</c>.</summary>
    public const int DefaultK = 60;

    /// <summary>The contribution of one list rank.</summary>
    /// <param name="rank">The 1-based rank; 0 or less contributes nothing.</param>
    /// <param name="k">The constant.</param>
    /// <param name="weight">The list's weight.</param>
    /// <returns><c>weight / (k + rank)</c>, or 0.</returns>
    public static double Contribution(int rank, int k = DefaultK, double weight = 1.0) => rank <= 0 ? 0 : weight / (k + rank);

    /// <summary>Fuses ranked lists. An item that occurs twice in one list counts at its first (best) position only.</summary>
    /// <typeparam name="TKey">The item type.</typeparam>
    /// <param name="lists">The lists, each best first.</param>
    /// <param name="k">The constant.</param>
    /// <returns>Each item's fused score.</returns>
    public static Dictionary<TKey, double> Fuse<TKey>(IEnumerable<IReadOnlyList<TKey>> lists, int k = DefaultK)
        where TKey : notnull
    {
        ArgumentNullException.ThrowIfNull(lists);
        ArgumentOutOfRangeException.ThrowIfNegative(k);
        var scores = new Dictionary<TKey, double>();
        foreach (var list in lists)
        {
            foreach (var (key, rank) in Ranks(list))
            {
                scores[key] = scores.GetValueOrDefault(key) + Contribution(rank, k);
            }
        }

        return scores;
    }

    /// <summary>The 1-based rank of each distinct item of a list (its first position).</summary>
    /// <typeparam name="TKey">The item type.</typeparam>
    /// <param name="list">The list, best first.</param>
    /// <returns>The ranks by item.</returns>
    public static Dictionary<TKey, int> Ranks<TKey>(IReadOnlyList<TKey> list)
        where TKey : notnull
    {
        ArgumentNullException.ThrowIfNull(list);
        var ranks = new Dictionary<TKey, int>(list.Count);
        var next = 1;
        foreach (var key in list)
        {
            if (ranks.TryAdd(key, next))
            {
                next++;
            }
        }

        return ranks;
    }
}
