using System;
using System.Collections.Generic;
using System.Globalization;

namespace Pillion.VisualStudio.Core.Autocomplete;

/// <summary>A point-in-time view of the autocomplete latency window.</summary>
/// <param name="count">The number of samples in the window.</param>
/// <param name="p50">The median latency, or <see langword="null"/> with no samples.</param>
/// <param name="p95">The 95th percentile latency, or <see langword="null"/> with no samples.</param>
/// <param name="degraded">Whether autocomplete is degraded (350 ms debounce, single-line only).</param>
public sealed class LatencySnapshot(int count, TimeSpan? p50, TimeSpan? p95, bool degraded)
{
    /// <summary>Gets the number of samples in the window.</summary>
    public int Count { get; } = count;

    /// <summary>Gets the median latency, or <see langword="null"/> with no samples.</summary>
    public TimeSpan? P50 { get; } = p50;

    /// <summary>Gets the 95th percentile latency, or <see langword="null"/> with no samples.</summary>
    public TimeSpan? P95 { get; } = p95;

    /// <summary>Gets a value indicating whether autocomplete is degraded (350 ms debounce, single-line only).</summary>
    public bool Degraded { get; } = degraded;

    /// <summary>Formats the snapshot for the status text, for example <c>autocomplete p50 420 ms, p95 910 ms (37 in 5 min)</c>.</summary>
    /// <returns>The text.</returns>
    public override string ToString()
    {
        if (Count == 0 || P50 is null || P95 is null)
        {
            return "autocomplete: no suggestions timed in the last 5 minutes";
        }

        return string.Format(
            CultureInfo.InvariantCulture,
            "autocomplete p50 {0:0} ms, p95 {1:0} ms ({2} in 5 min){3}",
            P50.Value.TotalMilliseconds,
            P95.Value.TotalMilliseconds,
            Count,
            Degraded ? ", degraded: 350 ms debounce, single-line" : string.Empty);
    }
}

/// <summary>Why and since when autocomplete is degraded.</summary>
/// <param name="since">When the p95 crossed the threshold.</param>
/// <param name="p95">The p95 at that moment.</param>
/// <param name="samples">The number of samples in the window at that moment.</param>
public sealed class DegradationInfo(DateTimeOffset since, TimeSpan p95, int samples)
{
    /// <summary>Gets when the p95 crossed the threshold.</summary>
    public DateTimeOffset Since { get; } = since;

    /// <summary>Gets the p95 when it crossed the threshold.</summary>
    public TimeSpan P95 { get; } = p95;

    /// <summary>Gets the number of samples in the window when it crossed the threshold.</summary>
    public int Samples { get; } = samples;

    /// <summary>Gets the reason in words, for example <c>p95 1820 ms over the 1500 ms limit in 31 suggestions</c>.</summary>
    public string Reason => string.Format(
        CultureInfo.InvariantCulture,
        "p95 {0:0} ms over the {1:0} ms limit in {2} suggestions",
        P95.TotalMilliseconds,
        LatencyWindow.DegradeAbove.TotalMilliseconds,
        Samples);
}

/// <summary>
/// Autocomplete latency over a sliding five-minute window, kept in memory, and the degradation rule of design section
/// 13: when the p95 over the window exceeds 1.5 s, the debounce rises to 350 ms and requests are single-line until the
/// p95 is back under the threshold. Thread-safe. This is the one source of truth for time to suggestion and for the
/// degradation state: <see cref="AutocompleteMetrics"/> reads its percentiles and state from here and never keeps a
/// second copy.
/// </summary>
public sealed class LatencyWindow
{
    /// <summary>The window length.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(5);

    /// <summary>The p95 above which autocomplete degrades.</summary>
    public static readonly TimeSpan DegradeAbove = TimeSpan.FromMilliseconds(1500);

    /// <summary>
    /// The fewest samples in the window before the rule applies. With 20 the nearest-rank p95 is the 19th value, so one
    /// cold model load (several seconds) cannot trip it on its own.
    /// </summary>
    public const int MinimumSamples = 20;

    /// <summary>The normal debounce.</summary>
    public static readonly TimeSpan NormalDebounce = TimeSpan.FromMilliseconds(200);

    /// <summary>The debounce while degraded.</summary>
    public static readonly TimeSpan DegradedDebounce = TimeSpan.FromMilliseconds(350);

    private readonly object _gate = new();
    private readonly Queue<(DateTimeOffset At, TimeSpan Latency)> _samples = new();
    private readonly Func<DateTimeOffset> _now;
    private bool _degraded;
    private DegradationInfo? _degradation;

    /// <summary>Initializes a new instance of the <see cref="LatencyWindow"/> class.</summary>
    /// <param name="now">The time source.</param>
    public LatencyWindow(Func<DateTimeOffset> now)
    {
        _now = now ?? throw new ArgumentNullException(nameof(now));
    }

    /// <summary>Raised, on the recording thread, when autocomplete enters or leaves the degraded state.</summary>
    public event EventHandler<bool>? DegradedChanged;

    /// <summary>Gets a value indicating whether autocomplete is degraded.</summary>
    public bool IsDegraded
    {
        get
        {
            Evaluate(out var changed, out var degraded);
            if (changed)
            {
                DegradedChanged?.Invoke(this, degraded);
            }

            return degraded;
        }
    }

    /// <summary>Gets why and since when autocomplete is degraded, or <see langword="null"/> while it is not.</summary>
    public DegradationInfo? Degradation
    {
        get
        {
            _ = IsDegraded;
            lock (_gate)
            {
                return _degraded ? _degradation : null;
            }
        }
    }

    /// <summary>Gets the debounce to use now: 200 ms, or 350 ms while degraded.</summary>
    public TimeSpan Debounce => IsDegraded ? DegradedDebounce : NormalDebounce;

    /// <summary>Records the latency of one suggestion (request sent to first visible suggestion).</summary>
    /// <param name="latency">The latency.</param>
    public void Record(TimeSpan latency)
    {
        lock (_gate)
        {
            _samples.Enqueue((_now(), latency));
        }

        _ = IsDegraded;
    }

    /// <summary>Forgets every sample and leaves the degraded state (used by <see cref="AutocompleteMetrics.Reset"/>).</summary>
    public void Clear()
    {
        bool changed;
        lock (_gate)
        {
            _samples.Clear();
            changed = _degraded;
            _degraded = false;
            _degradation = null;
        }

        if (changed)
        {
            DegradedChanged?.Invoke(this, false);
        }
    }

    /// <summary>Gets the current p50, p95 and state.</summary>
    /// <returns>The snapshot.</returns>
    public LatencySnapshot Snapshot()
    {
        var degraded = IsDegraded;
        lock (_gate)
        {
            Prune();
            if (_samples.Count == 0)
            {
                return new LatencySnapshot(0, null, null, degraded);
            }

            var sorted = Sorted();
            return new LatencySnapshot(sorted.Length, Percentile(sorted, 0.50), Percentile(sorted, 0.95), degraded);
        }
    }

    /// <summary>Gets the nearest-rank percentile of sorted samples.</summary>
    /// <param name="sorted">The samples in ascending order.</param>
    /// <param name="fraction">The percentile as a fraction (0.95 for p95).</param>
    /// <returns>The percentile.</returns>
    public static TimeSpan Percentile(IReadOnlyList<TimeSpan> sorted, double fraction)
    {
        if (sorted is null || sorted.Count == 0)
        {
            throw new ArgumentException("At least one sample is needed.", nameof(sorted));
        }

        var rank = (int)Math.Ceiling(fraction * sorted.Count);
        return sorted[Math.Min(sorted.Count, Math.Max(1, rank)) - 1];
    }

    private void Evaluate(out bool changed, out bool degraded)
    {
        lock (_gate)
        {
            Prune();
            var p95 = _samples.Count >= MinimumSamples ? Percentile(Sorted(), 0.95) : TimeSpan.Zero;
            var next = _samples.Count >= MinimumSamples && p95 > DegradeAbove;
            changed = next != _degraded;
            if (changed)
            {
                _degradation = next ? new DegradationInfo(_now(), p95, _samples.Count) : null;
            }

            _degraded = next;
            degraded = next;
        }
    }

    private void Prune()
    {
        var oldest = _now() - Window;
        while (_samples.Count > 0 && _samples.Peek().At < oldest)
        {
            _samples.Dequeue();
        }
    }

    private TimeSpan[] Sorted()
    {
        var values = new TimeSpan[_samples.Count];
        var i = 0;
        foreach (var sample in _samples)
        {
            values[i++] = sample.Latency;
        }

        Array.Sort(values);
        return values;
    }
}
