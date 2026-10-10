using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace Pillion.VisualStudio.Core.Autocomplete;

/// <summary>
/// Once per window (five minutes), while autocomplete is in use, writes the window's figures as one JSON line to the
/// local metrics log and a one-line summary to the Output pane (through <see cref="PillionLog"/>). Nothing is sent
/// anywhere: the opt-in upload of design section 12 is a later item. A window in which autocomplete did nothing writes
/// nothing. Counts and durations only.
/// </summary>
public sealed class AutocompleteMetricsReporter : IDisposable
{
    /// <summary>The reporting window.</summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromMinutes(5);

    /// <summary>The shortest window the test knob allows.</summary>
    public static readonly TimeSpan MinimumInterval = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The environment variable that shortens the reporting window (in seconds, 5 to 300) so the log line can be
    /// checked without waiting five minutes. Not a documented setting; the rolling five-minute window and the
    /// degradation rule are unaffected.
    /// </summary>
    public const string IntervalVariable = "PILLION_METRICS_WINDOW_SECONDS";

    /// <summary>How long the host gets to answer for its counters before the line is written without them.</summary>
    private static readonly TimeSpan HostTimeout = TimeSpan.FromSeconds(2);

    private readonly AutocompleteEngine _engine;
    private readonly AutocompleteMetricsLogWriter? _writer;
    private readonly TimeSpan _interval;
    private readonly CancellationTokenSource _stop = new();
    private DateTimeOffset _windowStart;
    private int _started;

    /// <summary>Initializes a new instance of the <see cref="AutocompleteMetricsReporter"/> class.</summary>
    /// <param name="engine">The engine whose metrics are reported.</param>
    /// <param name="writer">The metrics log, or <see langword="null"/> to write the Output pane summary only.</param>
    /// <param name="interval">The window, or <see langword="null"/> for five minutes.</param>
    public AutocompleteMetricsReporter(AutocompleteEngine engine, AutocompleteMetricsLogWriter? writer, TimeSpan? interval = null)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _writer = writer;
        _interval = interval ?? DefaultInterval;
        _windowStart = engine.Clock.UtcNow;
    }

    /// <summary>Reads the reporting window from <see cref="IntervalVariable"/>.</summary>
    /// <param name="value">The variable's value, or <see langword="null"/>.</param>
    /// <returns>The window: the value in seconds clamped to 5 to 300, or five minutes when it is absent or not a number.</returns>
    public static TimeSpan ParseInterval(string? value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds)
            ? TimeSpan.FromSeconds(Math.Max((int)MinimumInterval.TotalSeconds, Math.Min((int)DefaultInterval.TotalSeconds, seconds)))
            : DefaultInterval;

    /// <summary>Starts the timer loop on the thread pool. Returns at once; calling it again does nothing.</summary>
    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            return;
        }

        _windowStart = _engine.Clock.UtcNow;
        _ = Task.Run(() => RunAsync(_stop.Token), CancellationToken.None);
    }

    /// <summary>Stops the loop.</summary>
    public void Dispose()
    {
        try
        {
            _stop.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already disposed.
        }

        _stop.Dispose();
    }

    /// <summary>
    /// Reports the window that ends now. Called by the loop once per interval; public so a test can drive it.
    /// </summary>
    /// <param name="cancellationToken">A token that cancels the host call.</param>
    /// <returns><see langword="true"/> when something was reported (autocomplete did something in the window).</returns>
    public async Task<bool> ReportAsync(CancellationToken cancellationToken)
    {
        var end = _engine.Clock.UtcNow;
        var start = _windowStart;
        _windowStart = end;
        var stats = _engine.Metrics.Summarize(start);
        if (stats.Attempts == 0 && stats.Suppressed == 0)
        {
            return false;
        }

        var host = await _engine.TryGetHostCountersAsync(HostTimeout, cancellationToken).ConfigureAwait(false);
        var degradation = _engine.Latency.Degradation;
        _writer?.Append(AutocompleteMetricsJson.Create(start, end, stats, degradation, host));
        _engine.Log?.Info(
            "autocomplete.metrics",
            AutocompleteMetricsText.Summary(stats, end - start, degradation, host));
        return true;
    }

    private async Task RunAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                await _engine.Clock.DelayAsync(_interval, token).ConfigureAwait(false);
                try
                {
                    await ReportAsync(token).ConfigureAwait(false);
                }
#pragma warning disable CA1031 // Reporting is a courtesy; one failed window must not stop the next.
                catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
                {
                    _engine.Log?.Warning("autocomplete.metrics.failed", "A metrics report failed.", ("error", ex.GetType().Name));
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Visual Studio is closing.
        }
    }
}
