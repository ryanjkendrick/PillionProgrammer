using System;
using System.Globalization;
using System.Text;
using Pillion.Contracts;

namespace Pillion.VisualStudio.Core.Autocomplete;

/// <summary>
/// The words for the autocomplete measurements: the status bar suffix, the section in <c>Pillion: Show Status</c> and
/// the one-line summary written to the Output pane every five minutes. Counts and durations only.
/// </summary>
public static class AutocompleteMetricsText
{
    /// <summary>Gets what the status bar adds after the host state for an indicator: empty, ", waiting for suggestion" or ", suggestions slow".</summary>
    /// <param name="indicator">The indicator.</param>
    /// <returns>The suffix, with its leading comma.</returns>
    public static string StatusSuffix(AutocompleteIndicator indicator) => indicator switch
    {
        AutocompleteIndicator.Degraded => ", suggestions slow",
        AutocompleteIndicator.Waiting => ", waiting for suggestion",
        _ => string.Empty,
    };

    /// <summary>Formats a percentage, for example <c>74%</c>, or <c>n/a</c> without a base.</summary>
    /// <param name="rate">The fraction from 0 to 1, or <see langword="null"/>.</param>
    /// <returns>The text.</returns>
    public static string Percent(double? rate) => rate is { } value
        ? string.Format(CultureInfo.InvariantCulture, "{0:0}%", value * 100)
        : "n/a";

    /// <summary>Formats a duration in whole milliseconds, for example <c>420 ms</c>, or <c>n/a</c>.</summary>
    /// <param name="duration">The duration, or <see langword="null"/>.</param>
    /// <returns>The text.</returns>
    public static string Millis(TimeSpan? duration) => duration is { } value
        ? string.Format(CultureInfo.InvariantCulture, "{0:0} ms", value.TotalMilliseconds)
        : "n/a";

    /// <summary>
    /// Describes the measurements for <c>Pillion: Show Status</c>: the extension's own figures over the last five
    /// minutes and since the package loaded, the degradation state, and the host's numbers beside them.
    /// </summary>
    /// <param name="snapshot">The extension's figures.</param>
    /// <param name="host">The host's counters, or <see langword="null"/> when it could not be asked.</param>
    /// <returns>Several lines, without a trailing line break.</returns>
    public static string Describe(AutocompleteMetricsSnapshot snapshot, AutocompleteHostCounters? host)
    {
        if (snapshot is null)
        {
            throw new ArgumentNullException(nameof(snapshot));
        }

        var recent = snapshot.Recent;
        var builder = new StringBuilder();
        builder.AppendLine("Autocomplete, last " + SpanText(snapshot.WindowLength));
        builder.AppendLine(string.Format(
            CultureInfo.InvariantCulture,
            "  {0} sent to the host, {1} shown ({2} of requests); {3} taken ({4} of shown: {5} whole, {6} by word); {7} rejected; {8} displaced by another provider",
            recent.Requests,
            recent.Shown,
            Percent(recent.ShowRate),
            recent.AcceptedAny,
            Percent(recent.AcceptanceRate),
            recent.Accepted,
            recent.AcceptedWord,
            recent.Rejected,
            recent.Displaced));
        builder.AppendLine(string.Format(
            CultureInfo.InvariantCulture,
            "  Cancelled by typing: {0}; suppressed: {1}; empty: {2}; errors: {3}; multi-line: {4}; in flight: {5}",
            recent.CancelledByKeystroke,
            recent.Suppressed,
            recent.Empty,
            recent.Errors,
            recent.MultiLine,
            recent.InFlight));
        builder.AppendLine("  Time to first chunk: " + Durations(recent.TimeToFirstChunk));
        builder.AppendLine("  Time to suggestion: " + Durations(recent.TimeToSuggestion));
        builder.AppendLine("  State: " + DegradationText(snapshot.Degradation));
        builder.AppendLine(string.Format(
            CultureInfo.InvariantCulture,
            "  Since Visual Studio started: {0} sent, {1} shown, {2} taken, {3} errors",
            snapshot.Total.Requests,
            snapshot.Total.Shown,
            snapshot.Total.AcceptedAny,
            snapshot.Total.Errors));

        if (host is null)
        {
            builder.Append("Host: no numbers (Pillion Host is not ready, or did not answer)");
            return builder.ToString();
        }

        builder.AppendLine(string.Format(
            CultureInfo.InvariantCulture,
            "Host, since it started: {0} requests; cache hit rate {1} ({2} repeats, {3} typed-through, {4} misses); {5} model requests, {6} failed, {7} cancelled, {8} dropped",
            host.Requests,
            Percent(host.CacheHits + host.TypeThroughHits + host.CacheMisses == 0 ? null : host.HitRate),
            host.CacheHits,
            host.TypeThroughHits,
            host.CacheMisses,
            host.ModelRequests,
            host.Failed,
            host.Cancelled,
            host.Dropped));
        builder.AppendLine(string.Format(
            CultureInfo.InvariantCulture,
            "Host, last {0} minutes: {1} requests ({2} from the cache); time to first token {3}; total {4}",
            host.WindowSeconds / 60,
            host.WindowRequests,
            host.WindowCacheHits,
            Durations(host.FirstTokenP50Ms, host.FirstTokenP95Ms),
            Durations(host.TotalP50Ms, host.TotalP95Ms)));
        builder.Append(string.Format(
            CultureInfo.InvariantCulture,
            "  Prompts: {0} counted with the tokenizer, {1} estimated; largest {2} tokens",
            host.PromptCountedRequests,
            host.PromptEstimatedRequests,
            host.PromptTokensMax));
        return builder.ToString();
    }

    /// <summary>
    /// Writes the one-line summary of a reporting window for the Output pane, for example
    /// <c>Autocomplete, last 5 min: 42 sent, 31 shown (74%), 12 taken (39%), p50 420 ms, p95 910 ms, normal; host cache hit rate 68%</c>.
    /// </summary>
    /// <param name="stats">The figures of the window.</param>
    /// <param name="span">The length of the window.</param>
    /// <param name="degradation">Why autocomplete is degraded, or <see langword="null"/>.</param>
    /// <param name="host">The host's counters, or <see langword="null"/>.</param>
    /// <returns>The line.</returns>
    public static string Summary(AutocompleteStats stats, TimeSpan span, DegradationInfo? degradation, AutocompleteHostCounters? host)
    {
        if (stats is null)
        {
            throw new ArgumentNullException(nameof(stats));
        }

        var text = string.Format(
            CultureInfo.InvariantCulture,
            "Autocomplete, last {0}: {1} sent, {2} shown ({3}), {4} taken ({5}), {6} cancelled by typing, {7} errors, time to suggestion {8}, {9}",
            SpanText(span),
            stats.Requests,
            stats.Shown,
            Percent(stats.ShowRate),
            stats.AcceptedAny,
            Percent(stats.AcceptanceRate),
            stats.CancelledByKeystroke,
            stats.Errors,
            Durations(stats.TimeToSuggestion),
            degradation is null ? "normal" : "degraded");
        if (host is not null && host.CacheHits + host.TypeThroughHits + host.CacheMisses > 0)
        {
            text += string.Format(CultureInfo.InvariantCulture, "; host cache hit rate {0}", Percent(host.HitRate));
        }

        return text;
    }

    private static string SpanText(TimeSpan span) => span.TotalMinutes >= 1
        ? string.Format(CultureInfo.InvariantCulture, "{0:0.#} min", span.TotalMinutes)
        : string.Format(CultureInfo.InvariantCulture, "{0:0} s", span.TotalSeconds);

    private static string Durations(DurationPercentiles percentiles) => percentiles.Count == 0
        ? "no samples yet"
        : string.Format(
            CultureInfo.InvariantCulture,
            "p50 {0}, p95 {1} ({2} samples)",
            Millis(percentiles.P50),
            Millis(percentiles.P95),
            percentiles.Count);

    private static string Durations(double? p50, double? p95) => p50 is null || p95 is null
        ? "no samples yet"
        : string.Format(CultureInfo.InvariantCulture, "p50 {0:0} ms, p95 {1:0} ms", p50.Value, p95.Value);

    private static string DegradationText(DegradationInfo? degradation) => degradation is null
        ? "normal (200 ms debounce, multi-line allowed)"
        : string.Format(
            CultureInfo.InvariantCulture,
            "degraded since {0:HH:mm:ss}: {1}; 350 ms debounce, single-line only",
            degradation.Since.ToLocalTime(),
            degradation.Reason);
}
