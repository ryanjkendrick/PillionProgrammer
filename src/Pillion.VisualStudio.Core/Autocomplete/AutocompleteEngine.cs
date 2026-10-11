using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Pillion.Contracts;

namespace Pillion.VisualStudio.Core.Autocomplete;

/// <summary>
/// The shared part of the autocomplete request pipeline, one per Visual Studio process: the host connection, the
/// settings, the latency window that drives degradation, the recent-edit and open-document trackers, and the throttled
/// failure log. Each text view gets an <see cref="AutocompleteSession"/> from <see cref="CreateSession"/>.
/// </summary>
public sealed class AutocompleteEngine
{
    /// <summary>
    /// The shortest time between two updates of a suggestion that grows while it streams (design section 13: the UI
    /// thread gets batched updates, no more than one display update per 50 ms). The first visible text is shown at once:
    /// there is no grace period any more (the 300 ms <c>PartialGrace</c> made a 57 to 171 ms first chunk a 420 to 611 ms
    /// suggestion at Gate 1).
    /// </summary>
    public static readonly TimeSpan DisplayUpdateInterval = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// Whether a single-line suggestion is shown at its first chunk and then grown. Off: the Visual Studio integration
    /// tests on 18.x and 17.14 (CI run 38114630716) showed that <c>SuggestionServiceBase.TryUpdateProposal</c> returns
    /// the proposal but does not change the displayed ghost text, so a suggestion shown at its first chunk stayed at that
    /// chunk and Tab took only it ("var total = Calculat"). A single-line suggestion is therefore shown when its line is
    /// complete, with no grace period; a multi-line one still at its first complete line. Switch this on once growing the
    /// displayed proposal is shown to work.
    /// </summary>
    public static bool ShowSingleLineEarly => false;

    /// <summary>How long the language-service declaration lookup may run past the debounce before the heuristic is used.</summary>
    public static readonly TimeSpan DeclarationBudget = TimeSpan.FromMilliseconds(100);

    /// <summary>The token limit of a single-line request.</summary>
    public const int SingleLineMaxTokens = 64;

    /// <summary>The token limit of a multi-line request.</summary>
    public const int MultiLineMaxTokens = 256;

    private readonly AutocompleteAvailability? _availability;
    private readonly string _instance = Guid.NewGuid().ToString("N").Substring(0, 6);
    private long _attempts;
    private int _sessions;

    /// <summary>Initializes a new instance of the <see cref="AutocompleteEngine"/> class.</summary>
    /// <param name="connection">The connection to Pillion Host.</param>
    /// <param name="availability">The feature flag and settings, or <see langword="null"/> for the defaults.</param>
    /// <param name="clock">The clock (debounce and latency).</param>
    /// <param name="log">The logger, or <see langword="null"/>.</param>
    public AutocompleteEngine(IHostConnection connection, AutocompleteAvailability? availability, ISupervisorClock clock, PillionLog? log)
    {
        Connection = connection ?? throw new ArgumentNullException(nameof(connection));
        Clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _availability = availability;
        Log = log;
        Latency = new LatencyWindow(() => clock.UtcNow);
        Metrics = new AutocompleteMetrics(clock, Latency);
        Failures = new ThrottledLog(log, () => clock.UtcNow);
        Latency.DegradedChanged += (_, degraded) => log?.Info(
            degraded ? "autocomplete.degraded" : "autocomplete.recovered",
            degraded
                ? "Autocomplete p95 is over 1.5 s for the last 5 minutes: debounce 350 ms, single-line only."
                : "Autocomplete latency recovered: debounce 200 ms, multi-line allowed.");
    }

    /// <summary>Gets the connection to Pillion Host.</summary>
    public IHostConnection Connection { get; }

    /// <summary>Gets the clock.</summary>
    public ISupervisorClock Clock { get; }

    /// <summary>Gets the logger, or <see langword="null"/>.</summary>
    public PillionLog? Log { get; }

    /// <summary>Gets the latency window (p50, p95 and the degradation state).</summary>
    public LatencyWindow Latency { get; }

    /// <summary>
    /// Gets the measurements (work item 1.5): outcomes, acceptance and show rates, times to first chunk and suggestion.
    /// The percentiles and the degradation state it reports are read from <see cref="Latency"/>.
    /// </summary>
    public AutocompleteMetrics Metrics { get; }

    /// <summary>Gets the recent edits.</summary>
    public EditTracker Edits { get; } = new();

    /// <summary>Gets the open documents in most-recently-used order.</summary>
    public OpenDocumentTracker OpenDocuments { get; } = new();

    /// <summary>Gets the throttled failure log (at most one line a minute).</summary>
    public ThrottledLog Failures { get; }

    /// <summary>Gets the current settings (the defaults until the host has answered).</summary>
    public AutocompleteSettings Settings => _availability?.Settings ?? AutocompleteSettings.Default;

    /// <summary>
    /// The availability gate every request path passes before anything is sent (Gate 1 open issue 3): the
    /// <c>autocomplete</c> feature flag and the user's disabled languages, read from the current settings.
    /// </summary>
    /// <param name="languageId">The document's language id.</param>
    /// <returns><see cref="SuppressionReason.FeatureDisabled"/>, <see cref="SuppressionReason.LanguageDisabled"/> or <see cref="SuppressionReason.None"/>.</returns>
    public SuppressionReason Gate(string? languageId)
    {
        var settings = Settings;
        return !settings.Enabled ? SuppressionReason.FeatureDisabled
            : SuppressionRules.IsLanguageDisabled(languageId, settings.DisabledLanguages) ? SuppressionReason.LanguageDisabled
            : SuppressionReason.None;
    }

    /// <summary>Gets a value indicating whether the host (or the test override, <see cref="AutocompleteOverrides"/>) can take requests now.</summary>
    public bool HostUsable => AutocompleteOverrides.ModelService is not null || Connection.State is HostState.Ready or HostState.Degraded;

    /// <summary>Asks the host for its autocomplete counters (cache hits, host-side latencies). Never throws.</summary>
    /// <param name="timeout">How long to wait for the host.</param>
    /// <param name="cancellationToken">A token that cancels the call.</param>
    /// <returns>The counters, or <see langword="null"/> when the host is not ready, is too old to know the call or does not answer in time.</returns>
    public async Task<AutocompleteHostCounters?> TryGetHostCountersAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (!HostUsable)
        {
            return null;
        }

        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linked.CancelAfter(timeout);
            return await Connection.GetProxy<IDiagnosticsService>().GetAutocompleteCountersAsync(linked.Token).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Host numbers are a courtesy next to the extension's own; any failure just leaves them out.
        catch (Exception)
#pragma warning restore CA1031
        {
            return null;
        }
    }

    /// <summary>Creates the session for one text view.</summary>
    /// <returns>The session.</returns>
    public AutocompleteSession CreateSession() =>
        new(this, Interlocked.Increment(ref _sessions).ToString(CultureInfo.InvariantCulture));

    /// <summary>Gets the next attempt number (unique in the process, so proposal ids never repeat).</summary>
    internal long NextAttempt() => Interlocked.Increment(ref _attempts);

    /// <summary>Builds the request id of an attempt.</summary>
    internal string RequestIdFor(long attempt) => "ac-" + _instance + "-" + attempt.ToString(CultureInfo.InvariantCulture);

    /// <summary>Builds a completion request from the captured context.</summary>
    internal CompletionRequest BuildRequest(string requestId, CompletionInput input, CapturedContext context, bool multiLine, string? declaration) => new()
    {
        RequestId = requestId,
        Prefix = context.Prefix,
        Suffix = context.Suffix,
        LanguageId = input.LanguageId,
        FilePath = input.FilePath,
        MaxTokens = multiLine ? MultiLineMaxTokens : SingleLineMaxTokens,
        TaskClass = TaskClass.Autocomplete,
        CaretLine = context.CaretLine,
        CaretColumn = context.CaretColumn,
        EnclosingDeclaration = declaration,
        OpenDocuments = OpenDocuments.Snapshot(),
        RecentEdits = Edits.ToRequestStrings(),
        AllowMultiLine = multiLine,
        StopSequences = multiLine ? [] : ["\n"],
    };
}
