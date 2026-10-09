using System.Collections.Concurrent;

namespace Pillion.ModelGateway;

/// <summary>A snapshot of the autocomplete counters (design section 13: the prefix cache hit rate target is 70% during steady typing).</summary>
/// <param name="Requests">Completion requests received.</param>
/// <param name="CacheHits">Requests served from the cache with the same prefix (repeated keystrokes, backspaces).</param>
/// <param name="TypeThroughHits">Requests served from the cache because the user typed the start of a cached completion.</param>
/// <param name="CacheMisses">Requests the cache could not serve.</param>
/// <param name="ModelRequests">Requests sent to a model (attempts, including fallbacks).</param>
/// <param name="Dropped">Cloud requests dropped at the hard limit.</param>
/// <param name="Failed">Requests that failed.</param>
/// <param name="Cancelled">Requests the caller cancelled (usually the next keystroke).</param>
public sealed record AutocompleteCountersSnapshot(
    long Requests,
    long CacheHits,
    long TypeThroughHits,
    long CacheMisses,
    long ModelRequests,
    long Dropped,
    long Failed,
    long Cancelled)
{
    /// <summary>Gets the share of cache lookups that hit (exact or type-through), or 0 before the first lookup.</summary>
    public double HitRate => CacheHits + TypeThroughHits + CacheMisses == 0 ? 0 : (CacheHits + TypeThroughHits) / (double)(CacheHits + TypeThroughHits + CacheMisses);
}

/// <summary>The gateway's autocomplete counters. Thread-safe; one per gateway (a singleton in Pillion Host).</summary>
public sealed class AutocompleteCounters
{
    private long _requests;
    private long _modelRequests;
    private long _dropped;
    private long _failed;
    private long _cancelled;
    private AutocompleteCache? _cache;

    /// <summary>Records a received request.</summary>
    public void Request() => Interlocked.Increment(ref _requests);

    /// <summary>Records a request sent to a model.</summary>
    public void ModelRequest() => Interlocked.Increment(ref _modelRequests);

    /// <summary>Records a dropped cloud request.</summary>
    public void Dropped() => Interlocked.Increment(ref _dropped);

    /// <summary>Records a failed request.</summary>
    public void Failed() => Interlocked.Increment(ref _failed);

    /// <summary>Records a cancelled request.</summary>
    public void Cancelled() => Interlocked.Increment(ref _cancelled);

    /// <summary>Gets a snapshot, including the cache's hit and miss counts.</summary>
    /// <returns>The snapshot.</returns>
    public AutocompleteCountersSnapshot Snapshot()
    {
        var cache = Volatile.Read(ref _cache)?.GetCounts() ?? default;
        return new AutocompleteCountersSnapshot(
            Interlocked.Read(ref _requests),
            cache.Hits,
            cache.TypeThroughHits,
            cache.Misses,
            Interlocked.Read(ref _modelRequests),
            Interlocked.Read(ref _dropped),
            Interlocked.Read(ref _failed),
            Interlocked.Read(ref _cancelled));
    }

    /// <summary>Attaches the cache whose hit and miss counts the snapshot reports.</summary>
    /// <param name="cache">The cache.</param>
    internal void Attach(AutocompleteCache cache) => Volatile.Write(ref _cache, cache);
}

/// <summary>
/// The measured prompt-evaluation rate of each local model, from the time to the first token of its completions, and
/// the first-token budget it implies (design section 6: local timeouts scale with prompt size and the measured rate).
/// Thread-safe.
/// </summary>
public sealed class PromptEvaluationRates
{
    /// <summary>The rate assumed before anything was measured: a 1.5B model on a desktop CPU reads a few hundred tokens a second.</summary>
    public const double DefaultTokensPerSecond = 200;

    /// <summary>How many times the predicted prompt-evaluation time a request may take before it reports that it is slow.</summary>
    public const double SlackFactor = 3;

    private const double Smoothing = 0.3;
    private const int MinimumSampleTokens = 64;

    private readonly ConcurrentDictionary<string, double> _rates = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Records one completion's time to first token.</summary>
    /// <param name="model">The qualified model id.</param>
    /// <param name="promptTokens">The prompt tokens.</param>
    /// <param name="timeToFirstToken">The time to the first token.</param>
    public void Observe(string model, int promptTokens, TimeSpan timeToFirstToken)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (promptTokens < MinimumSampleTokens || timeToFirstToken <= TimeSpan.Zero)
        {
            return;
        }

        var rate = promptTokens / timeToFirstToken.TotalSeconds;
        _rates.AddOrUpdate(model, rate, (_, old) => (old * (1 - Smoothing)) + (rate * Smoothing));
    }

    /// <summary>Gets the measured rate of a model.</summary>
    /// <param name="model">The qualified model id.</param>
    /// <returns>Tokens per second, or <see langword="null"/> before the first measurement.</returns>
    public double? RateOf(string model) => _rates.TryGetValue(model, out var rate) ? rate : null;

    /// <summary>
    /// Gets the first-token budget of a local request: <see cref="SlackFactor"/> times the predicted prompt-evaluation
    /// time at the measured rate, never less than <paramref name="floor"/>. Past it the request reports progress and keeps
    /// waiting; it is not failed.
    /// </summary>
    /// <param name="model">The qualified model id.</param>
    /// <param name="promptTokens">The prompt tokens.</param>
    /// <param name="floor">The smallest budget (10 s for autocomplete).</param>
    /// <returns>The budget.</returns>
    public TimeSpan BudgetFor(string model, int promptTokens, TimeSpan floor)
    {
        var rate = RateOf(model) ?? DefaultTokensPerSecond;
        var predicted = TimeSpan.FromSeconds(SlackFactor * Math.Max(0, promptTokens) / Math.Max(1, rate));
        return predicted > floor ? predicted : floor;
    }
}
