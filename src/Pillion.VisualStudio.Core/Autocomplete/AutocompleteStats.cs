using System;
using System.Collections.Generic;

namespace Pillion.VisualStudio.Core.Autocomplete;

/// <summary>How one autocomplete request ended, from the user's point of view.</summary>
public enum AutocompleteOutcome
{
    /// <summary>Still in flight.</summary>
    Pending,

    /// <summary>A suggestion was shown and is still on offer (not accepted or rejected yet).</summary>
    Shown,

    /// <summary>A suggestion was shown and accepted whole (Tab, or typed through).</summary>
    Accepted,

    /// <summary>A suggestion was shown and part of it was accepted word by word (Ctrl+Right), and the rest was not taken.</summary>
    AcceptedWord,

    /// <summary>A suggestion was shown and dismissed, or the user typed something else.</summary>
    Rejected,

    /// <summary>The next keystroke or caret move cancelled the request (or the suggestion was never displayed).</summary>
    CancelledByKeystroke,

    /// <summary>Nothing was requested: the document, the setting or a completion list ruled it out, or the host was not ready.</summary>
    Suppressed,

    /// <summary>The request failed.</summary>
    Error,

    /// <summary>The model answered with nothing worth showing.</summary>
    Empty,
}

/// <summary>What the status bar says about autocomplete besides the host state.</summary>
public enum AutocompleteIndicator
{
    /// <summary>Nothing to add.</summary>
    None,

    /// <summary>A request has been waiting for the model longer than <see cref="AutocompleteMetrics.SlowAfter"/>.</summary>
    Waiting,

    /// <summary>Autocomplete is degraded: the p95 over five minutes is above 1.5 s.</summary>
    Degraded,
}

/// <summary>A median and a 95th percentile (nearest rank) over some samples.</summary>
/// <param name="count">The number of samples.</param>
/// <param name="p50">The median, or <see langword="null"/> without samples.</param>
/// <param name="p95">The 95th percentile, or <see langword="null"/> without samples.</param>
public sealed class DurationPercentiles(int count, TimeSpan? p50, TimeSpan? p95)
{
    /// <summary>Gets the empty set of percentiles.</summary>
    public static DurationPercentiles Empty { get; } = new(0, null, null);

    /// <summary>Gets the number of samples.</summary>
    public int Count { get; } = count;

    /// <summary>Gets the median, or <see langword="null"/> without samples.</summary>
    public TimeSpan? P50 { get; } = p50;

    /// <summary>Gets the 95th percentile, or <see langword="null"/> without samples.</summary>
    public TimeSpan? P95 { get; } = p95;

    /// <summary>Computes the percentiles of some samples.</summary>
    /// <param name="samples">The samples, in any order. The list is sorted in place.</param>
    /// <returns>The percentiles.</returns>
    public static DurationPercentiles From(List<TimeSpan> samples)
    {
        if (samples is null)
        {
            throw new ArgumentNullException(nameof(samples));
        }

        if (samples.Count == 0)
        {
            return Empty;
        }

        samples.Sort();
        return new DurationPercentiles(samples.Count, LatencyWindow.Percentile(samples, 0.50), LatencyWindow.Percentile(samples, 0.95));
    }
}

/// <summary>Requests, suggestions shown and suggestions accepted for one language.</summary>
/// <param name="requests">The requests sent to the host.</param>
/// <param name="shown">The suggestions shown.</param>
/// <param name="accepted">The suggestions of which something was accepted (whole or by word).</param>
public sealed class LanguageStats(long requests, long shown, long accepted)
{
    /// <summary>Gets the requests sent to the host.</summary>
    public long Requests { get; } = requests;

    /// <summary>Gets the suggestions shown.</summary>
    public long Shown { get; } = shown;

    /// <summary>Gets the suggestions of which something was accepted (whole or by word).</summary>
    public long Accepted { get; } = accepted;
}

/// <summary>
/// Autocomplete figures over a span of time (the rolling window, a reporting window) or since the package loaded
/// (<see cref="AutocompleteMetricsSnapshot.Total"/>, which keeps counts only). Immutable. Counts and durations only:
/// there is no text, file name or repository name anywhere in it.
/// </summary>
public sealed class AutocompleteStats
{
    /// <summary>Initializes a new instance of the <see cref="AutocompleteStats"/> class.</summary>
    /// <param name="counts">The counts.</param>
    /// <param name="timeToFirstChunk">Request sent to the host's first chunk.</param>
    /// <param name="timeToSuggestion">Request sent to the first suggestion worth showing.</param>
    internal AutocompleteStats(MetricCounts counts, DurationPercentiles timeToFirstChunk, DurationPercentiles timeToSuggestion)
    {
        Attempts = counts.Attempts;
        Requests = counts.Requests;
        Shown = counts.Shown;
        Accepted = counts.Accepted;
        AcceptedWord = counts.AcceptedWord;
        Rejected = counts.Rejected;
        CancelledByKeystroke = counts.Cancelled;
        Suppressed = counts.Suppressed;
        Errors = counts.Errors;
        Empty = counts.Empty;
        InFlight = counts.InFlight;
        MultiLine = counts.MultiLine;
        DegradedRequests = counts.DegradedRequests;
        TimeToFirstChunk = timeToFirstChunk;
        TimeToSuggestion = timeToSuggestion;
        Languages = counts.LanguageStats();
    }

    /// <summary>Gets the number of attempts the pipeline started (a keystroke that is cancelled during the debounce is one).</summary>
    public long Attempts { get; }

    /// <summary>Gets the number of requests sent to the host (attempts that survived the debounce).</summary>
    public long Requests { get; }

    /// <summary>Gets the number of suggestions shown (once each, whatever became of them).</summary>
    public long Shown { get; }

    /// <summary>Gets the number of shown suggestions that were accepted whole.</summary>
    public long Accepted { get; }

    /// <summary>Gets the number of shown suggestions of which words were accepted but not the whole.</summary>
    public long AcceptedWord { get; }

    /// <summary>Gets the number of shown suggestions that were dismissed or typed over.</summary>
    public long Rejected { get; }

    /// <summary>Gets the number of attempts a keystroke or caret move cancelled (before or after the request was sent).</summary>
    public long CancelledByKeystroke { get; }

    /// <summary>Gets the number of times nothing was requested because of a suppression rule or an unready host.</summary>
    public long Suppressed { get; }

    /// <summary>Gets the number of requests that failed.</summary>
    public long Errors { get; }

    /// <summary>Gets the number of requests the model answered with nothing worth showing.</summary>
    public long Empty { get; }

    /// <summary>Gets the number of attempts still in flight.</summary>
    public long InFlight { get; }

    /// <summary>Gets the number of requests that asked for a multi-line suggestion.</summary>
    public long MultiLine { get; }

    /// <summary>Gets the number of requests made while the pipeline was degraded.</summary>
    public long DegradedRequests { get; }

    /// <summary>Gets the suggestions of which something was accepted, whole or by word.</summary>
    public long AcceptedAny => Accepted + AcceptedWord;

    /// <summary>Gets the share of requests that produced a shown suggestion (<see cref="Shown"/> over <see cref="Requests"/>), or <see langword="null"/> without requests.</summary>
    public double? ShowRate => Requests == 0 ? null : (double)Shown / Requests;

    /// <summary>Gets the share of shown suggestions of which something was accepted (<see cref="AcceptedAny"/> over <see cref="Shown"/>), or <see langword="null"/> when none was shown.</summary>
    public double? AcceptanceRate => Shown == 0 ? null : (double)AcceptedAny / Shown;

    /// <summary>Gets the time from sending a request to the host's first chunk. Empty in cumulative figures.</summary>
    public DurationPercentiles TimeToFirstChunk { get; }

    /// <summary>Gets the time from sending a request to the first suggestion worth showing. Empty in cumulative figures.</summary>
    public DurationPercentiles TimeToSuggestion { get; }

    /// <summary>Gets the requests, shown and accepted suggestions per language identifier.</summary>
    public IReadOnlyDictionary<string, LanguageStats> Languages { get; }
}

/// <summary>A view of the metrics at one moment.</summary>
public sealed class AutocompleteMetricsSnapshot
{
    /// <summary>Initializes a new instance of the <see cref="AutocompleteMetricsSnapshot"/> class.</summary>
    /// <param name="at">When the snapshot was taken.</param>
    /// <param name="windowLength">The length of the rolling window.</param>
    /// <param name="recent">The figures over the rolling window.</param>
    /// <param name="total">The cumulative counts since the package loaded.</param>
    /// <param name="degradation">Why and since when autocomplete is degraded, or <see langword="null"/>.</param>
    /// <param name="indicator">The status bar indicator.</param>
    internal AutocompleteMetricsSnapshot(
        DateTimeOffset at,
        TimeSpan windowLength,
        AutocompleteStats recent,
        AutocompleteStats total,
        DegradationInfo? degradation,
        AutocompleteIndicator indicator)
    {
        At = at;
        WindowLength = windowLength;
        Recent = recent;
        Total = total;
        Degradation = degradation;
        Indicator = indicator;
    }

    /// <summary>Gets when the snapshot was taken.</summary>
    public DateTimeOffset At { get; }

    /// <summary>Gets the length of the rolling window (five minutes).</summary>
    public TimeSpan WindowLength { get; }

    /// <summary>Gets the figures over the rolling window; <see cref="AutocompleteStats.TimeToSuggestion"/> is the degradation rule's own window.</summary>
    public AutocompleteStats Recent { get; }

    /// <summary>Gets the cumulative counts since the package loaded (percentiles are empty).</summary>
    public AutocompleteStats Total { get; }

    /// <summary>Gets a value indicating whether autocomplete is degraded (350 ms debounce, single-line only).</summary>
    public bool IsDegraded => Degradation is not null;

    /// <summary>Gets why and since when autocomplete is degraded, or <see langword="null"/> while it is not.</summary>
    public DegradationInfo? Degradation { get; }

    /// <summary>Gets what the status bar adds to the host state.</summary>
    public AutocompleteIndicator Indicator { get; }
}

/// <summary>
/// What the performance suite (work item 1.18) and the diagnostics read: autocomplete figures over the last five
/// minutes and since the package loaded. Thread-safe.
/// </summary>
public interface IAutocompleteMetrics
{
    /// <summary>Gets the current figures.</summary>
    /// <returns>The snapshot.</returns>
    AutocompleteMetricsSnapshot Snapshot();

    /// <summary>Forgets everything recorded so far, including the degradation state (a fresh start for a measurement run).</summary>
    void Reset();
}
