using Pillion.Contracts;
using Pillion.ModelGateway;

namespace Pillion.Host.Models;

/// <summary>
/// Implements <see cref="IDiagnosticsService"/>: a read-only view of the Model Gateway's autocomplete counters and of the
/// autocomplete records in the Outbound log. It reads what the gateway already keeps (the counters and the ring buffer)
/// and holds no state of its own; it never returns text, only counts and durations.
/// </summary>
internal sealed class DiagnosticsRpcService : IDiagnosticsService
{
    /// <summary>The length of the window the Outbound figures cover (design section 13: five minutes).</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(5);

    private readonly AutocompleteCounters _counters;
    private readonly IOutboundLog _outbound;
    private readonly TimeProvider _time;

    /// <summary>Initializes a new instance of the <see cref="DiagnosticsRpcService"/> class.</summary>
    /// <param name="counters">The gateway's autocomplete counters.</param>
    /// <param name="outbound">The Outbound log.</param>
    /// <param name="time">The clock.</param>
    public DiagnosticsRpcService(AutocompleteCounters counters, IOutboundLog outbound, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(counters);
        ArgumentNullException.ThrowIfNull(outbound);
        ArgumentNullException.ThrowIfNull(time);
        _counters = counters;
        _outbound = outbound;
        _time = time;
    }

    /// <inheritdoc/>
    public Task<AutocompleteHostCounters> GetAutocompleteCountersAsync(CancellationToken cancellationToken) =>
        Task.FromResult(Build(_counters.Snapshot(), _outbound.GetRecent(), _time.GetUtcNow()));

    /// <summary>Builds the report from the counters and the Outbound records.</summary>
    /// <param name="counters">The counters snapshot.</param>
    /// <param name="records">The Outbound records, any task class.</param>
    /// <param name="now">The current time.</param>
    /// <returns>The report.</returns>
    internal static AutocompleteHostCounters Build(AutocompleteCountersSnapshot counters, IReadOnlyList<OutboundRecord> records, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(counters);
        ArgumentNullException.ThrowIfNull(records);

        var cutoff = now - Window;
        var recent = records
            .Where(r => r.TaskClass == TaskClass.Autocomplete && r.Timestamp >= cutoff)
            .ToList();

        // Model requests only: a cache hit sends nothing, so its time says nothing about the model.
        var sent = recent.Where(r => !r.CacheHit).ToList();
        var firstTokens = sent.Where(r => r.TimeToFirstToken is not null && r.Outcome == OutboundOutcome.Succeeded)
            .Select(r => r.TimeToFirstToken!.Value.TotalMilliseconds)
            .ToList();
        var totals = sent.Where(r => r.Outcome == OutboundOutcome.Succeeded)
            .Select(r => r.Duration.TotalMilliseconds)
            .ToList();
        var counted = sent.Where(r => r.PromptTokensCounted).ToList();
        var estimated = sent.Where(r => !r.PromptTokensCounted).ToList();

        return new AutocompleteHostCounters
        {
            Requests = counters.Requests,
            CacheHits = counters.CacheHits,
            TypeThroughHits = counters.TypeThroughHits,
            CacheMisses = counters.CacheMisses,
            HitRate = counters.HitRate,
            ModelRequests = counters.ModelRequests,
            Dropped = counters.Dropped,
            Failed = counters.Failed,
            Cancelled = counters.Cancelled,
            WindowSeconds = (int)Window.TotalSeconds,
            WindowRequests = recent.Count,
            WindowCacheHits = recent.Count - sent.Count,
            FirstTokenP50Ms = Percentile(firstTokens, 0.50),
            FirstTokenP95Ms = Percentile(firstTokens, 0.95),
            TotalP50Ms = Percentile(totals, 0.50),
            TotalP95Ms = Percentile(totals, 0.95),
            PromptCountedRequests = counted.Count,
            PromptEstimatedRequests = estimated.Count,
            PromptTokensCounted = counted.Sum(r => (long)r.EstimatedPromptTokens),
            PromptTokensEstimated = estimated.Sum(r => (long)r.EstimatedPromptTokens),
            PromptTokensMax = sent.Count == 0 ? 0 : sent.Max(r => r.EstimatedPromptTokens),
        };
    }

    /// <summary>The nearest-rank percentile (the same rule as the extension's window).</summary>
    private static double? Percentile(List<double> values, double fraction)
    {
        if (values.Count == 0)
        {
            return null;
        }

        values.Sort();
        var rank = (int)Math.Ceiling(fraction * values.Count);
        return values[Math.Min(values.Count, Math.Max(1, rank)) - 1];
    }
}
