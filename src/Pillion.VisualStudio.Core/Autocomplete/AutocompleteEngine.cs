using System;
using System.Globalization;
using System.Threading;
using Pillion.Contracts;

namespace Pillion.VisualStudio.Core.Autocomplete;

/// <summary>
/// The shared part of the autocomplete request pipeline, one per Visual Studio process: the host connection, the
/// settings, the latency window that drives degradation, the recent-edit and open-document trackers, and the throttled
/// failure log. Each text view gets an <see cref="AutocompleteSession"/> from <see cref="CreateSession"/>.
/// </summary>
public sealed class AutocompleteEngine
{
    /// <summary>How long a multi-line attempt waits for the whole suggestion after its first line before showing what it has.</summary>
    public static readonly TimeSpan PartialGrace = TimeSpan.FromMilliseconds(300);

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

    /// <summary>Gets the recent edits.</summary>
    public EditTracker Edits { get; } = new();

    /// <summary>Gets the open documents in most-recently-used order.</summary>
    public OpenDocumentTracker OpenDocuments { get; } = new();

    /// <summary>Gets the throttled failure log (at most one line a minute).</summary>
    public ThrottledLog Failures { get; }

    /// <summary>Gets the current settings (the defaults until the host has answered).</summary>
    public AutocompleteSettings Settings => _availability?.Settings ?? AutocompleteSettings.Default;

    /// <summary>Gets a value indicating whether the host can take requests now.</summary>
    public bool HostUsable => Connection.State is HostState.Ready or HostState.Degraded;

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
