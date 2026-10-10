using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Pillion.VisualStudio.Core.Autocomplete;

/// <summary>How a shown suggestion ended; later values win (an accepted suggestion stays accepted if a dismissal follows).</summary>
internal enum Resolution
{
    None = 0,
    Rejected = 1,
    AcceptedWord = 2,
    Accepted = 3,
}

/// <summary>What the pipeline and the editor adapter know about one request. Guarded by the owner's lock.</summary>
internal sealed class RequestRecord
{
    public RequestRecord(long attempt, string language, DateTimeOffset requestedAt)
    {
        Attempt = attempt;
        Language = language;
        RequestedAt = requestedAt;
    }

    public long Attempt { get; }

    public string Language { get; }

    public DateTimeOffset RequestedAt { get; }

    public DateTimeOffset? SentAt { get; set; }

    public bool MultiLine { get; set; }

    public bool Degraded { get; set; }

    public TimeSpan? FirstChunk { get; set; }

    public TimeSpan? Suggestion { get; set; }

    public bool Shown { get; set; }

    public Resolution Resolution { get; set; }

    /// <summary>Gets or sets why the request produced nothing (<see cref="AutocompleteOutcome.Empty"/>, <see cref="AutocompleteOutcome.Error"/>, <see cref="AutocompleteOutcome.Suppressed"/>), or <see cref="AutocompleteOutcome.Pending"/>.</summary>
    public AutocompleteOutcome Terminal { get; set; }

    public bool Ended { get; set; }

    /// <summary>Gets or sets a value indicating whether the indicator counted this request as waiting too long.</summary>
    public bool Slow { get; set; }

    public CancellationTokenSource? SlowWatch { get; set; }

    /// <summary>Gets the outcome as of now.</summary>
    public AutocompleteOutcome Outcome
    {
        get
        {
            if (Shown)
            {
                return Resolution switch
                {
                    Resolution.Accepted => AutocompleteOutcome.Accepted,
                    Resolution.AcceptedWord => AutocompleteOutcome.AcceptedWord,
                    Resolution.Rejected => AutocompleteOutcome.Rejected,
                    _ => AutocompleteOutcome.Shown,
                };
            }

            if (Terminal != AutocompleteOutcome.Pending)
            {
                return Terminal;
            }

            // A suggestion the editor never displayed, or an attempt cut short, is the next keystroke's doing.
            return Ended ? AutocompleteOutcome.CancelledByKeystroke : AutocompleteOutcome.Pending;
        }
    }
}

/// <summary>Running counts over some requests.</summary>
internal sealed class MetricCounts
{
    private readonly Dictionary<string, long[]> _languages = new(StringComparer.Ordinal);

    public long Attempts { get; private set; }

    public long Requests { get; private set; }

    public long Shown { get; private set; }

    public long Accepted { get; private set; }

    public long AcceptedWord { get; private set; }

    public long Rejected { get; private set; }

    public long Cancelled { get; private set; }

    public long Suppressed { get; private set; }

    public long Errors { get; private set; }

    public long Empty { get; private set; }

    public long InFlight { get; private set; }

    public long MultiLine { get; private set; }

    public long DegradedRequests { get; private set; }

    public void Add(RequestRecord record)
    {
        if (record.Terminal == AutocompleteOutcome.Suppressed)
        {
            Suppressed++;
            return;
        }

        Attempts++;
        if (record.SentAt is not null)
        {
            Requests++;
            Language(record.Language)[0]++;
            if (record.MultiLine)
            {
                MultiLine++;
            }

            if (record.Degraded)
            {
                DegradedRequests++;
            }
        }

        if (record.Shown)
        {
            Shown++;
            Language(record.Language)[1]++;
        }

        switch (record.Outcome)
        {
            case AutocompleteOutcome.Accepted:
                Accepted++;
                Language(record.Language)[2]++;
                break;
            case AutocompleteOutcome.AcceptedWord:
                AcceptedWord++;
                Language(record.Language)[2]++;
                break;
            case AutocompleteOutcome.Rejected:
                Rejected++;
                break;
            case AutocompleteOutcome.CancelledByKeystroke:
                Cancelled++;
                break;
            case AutocompleteOutcome.Error:
                Errors++;
                break;
            case AutocompleteOutcome.Empty:
                Empty++;
                break;
            case AutocompleteOutcome.Pending:
                InFlight++;
                break;
        }
    }

    public void Clear()
    {
        Attempts = Requests = Shown = Accepted = AcceptedWord = Rejected = Cancelled = Suppressed = Errors = Empty = InFlight = MultiLine = DegradedRequests = 0;
        _languages.Clear();
    }

    public void Add(MetricCounts other)
    {
        Attempts += other.Attempts;
        Requests += other.Requests;
        Shown += other.Shown;
        Accepted += other.Accepted;
        AcceptedWord += other.AcceptedWord;
        Rejected += other.Rejected;
        Cancelled += other.Cancelled;
        Suppressed += other.Suppressed;
        Errors += other.Errors;
        Empty += other.Empty;
        InFlight += other.InFlight;
        MultiLine += other.MultiLine;
        DegradedRequests += other.DegradedRequests;
        foreach (var pair in other._languages)
        {
            var mine = Language(pair.Key);
            for (var i = 0; i < mine.Length; i++)
            {
                mine[i] += pair.Value[i];
            }
        }
    }

    public IReadOnlyDictionary<string, LanguageStats> LanguageStats()
    {
        var result = new SortedDictionary<string, LanguageStats>(StringComparer.Ordinal);
        foreach (var pair in _languages)
        {
            result[pair.Key] = new LanguageStats(pair.Value[0], pair.Value[1], pair.Value[2]);
        }

        return result;
    }

    private long[] Language(string id)
    {
        if (!_languages.TryGetValue(id, out var counts))
        {
            counts = new long[3];
            _languages[id] = counts;
        }

        return counts;
    }
}

/// <summary>
/// The autocomplete measurements of one Visual Studio process (work item 1.5), local only: for every request the time
/// it was made, when the first chunk and the suggestion arrived, how it ended (shown, accepted, accepted by word,
/// rejected, cancelled by a keystroke, suppressed, error, empty), whether it was multi-line and whether the pipeline
/// was degraded. Requests are kept for five minutes (the rolling window); older ones are folded into the cumulative
/// counts and dropped. The pipeline (<see cref="AutocompleteSession"/>) records the request side and the editor
/// adapter the display, accept and reject side. Time to suggestion and the degradation state are not copied: they are
/// read from the engine's <see cref="LatencyWindow"/>, which the degradation rule uses, so there is one source of truth.
/// Never holds text, file names or repository names. Thread-safe; no call blocks beyond a short lock.
/// </summary>
public sealed class AutocompleteMetrics : IAutocompleteMetrics
{
    /// <summary>How long a request may wait for the model before the status bar says so.</summary>
    public static readonly TimeSpan SlowAfter = TimeSpan.FromSeconds(1);

    /// <summary>The most requests kept; the oldest are folded into the cumulative counts beyond this (a safety cap, not reached when typing).</summary>
    internal const int MaxRecords = 20_000;

    private readonly object _gate = new();
    private readonly ISupervisorClock _clock;
    private readonly LatencyWindow _latency;
    private readonly Queue<RequestRecord> _records = new();
    private readonly Dictionary<long, RequestRecord> _byAttempt = [];
    private readonly MetricCounts _retired = new();
    private int _slowCount;
    private AutocompleteIndicator _announced;

    /// <summary>Initializes a new instance of the <see cref="AutocompleteMetrics"/> class.</summary>
    /// <param name="clock">The clock (timestamps and the slow-request timer).</param>
    /// <param name="latency">The latency window the degradation rule reads.</param>
    public AutocompleteMetrics(ISupervisorClock clock, LatencyWindow latency)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _latency = latency ?? throw new ArgumentNullException(nameof(latency));
        _latency.DegradedChanged += (_, _) => RaiseIndicatorIfChanged();
    }

    /// <summary>Raised, on a pool thread, when <see cref="Indicator"/> changes. Handlers must not block.</summary>
    public event EventHandler? IndicatorChanged;

    /// <summary>Gets the length of the rolling window (five minutes).</summary>
    public static TimeSpan Window => LatencyWindow.Window;

    /// <summary>Gets what the status bar adds to the host state: degraded, a request waiting for the model, or nothing.</summary>
    public AutocompleteIndicator Indicator
    {
        get
        {
            if (_latency.IsDegraded)
            {
                return AutocompleteIndicator.Degraded;
            }

            lock (_gate)
            {
                return _slowCount > 0 ? AutocompleteIndicator.Waiting : AutocompleteIndicator.None;
            }
        }
    }

    /// <inheritdoc/>
    public AutocompleteMetricsSnapshot Snapshot()
    {
        var degradation = _latency.Degradation;
        var latency = _latency.Snapshot();
        var now = _clock.UtcNow;
        var firstChunks = new List<TimeSpan>();
        var recent = new MetricCounts();
        var total = new MetricCounts();
        int slow;
        lock (_gate)
        {
            Prune(now);
            foreach (var record in _records)
            {
                recent.Add(record);
                if (record.FirstChunk is { } first)
                {
                    firstChunks.Add(first);
                }
            }

            total.Add(_retired);
            total.Add(recent);
            slow = _slowCount;
        }

        var suggestion = latency.P50 is { } p50 && latency.P95 is { } p95
            ? new DurationPercentiles(latency.Count, p50, p95)
            : DurationPercentiles.Empty;
        var indicator = degradation is not null ? AutocompleteIndicator.Degraded : slow > 0 ? AutocompleteIndicator.Waiting : AutocompleteIndicator.None;
        return new AutocompleteMetricsSnapshot(
            now,
            Window,
            new AutocompleteStats(recent, DurationPercentiles.From(firstChunks), suggestion),
            new AutocompleteStats(total, DurationPercentiles.Empty, DurationPercentiles.Empty),
            degradation,
            indicator);
    }

    /// <summary>
    /// Summarises the requests made from <paramref name="from"/> on (the reporting window of the five-minute log line).
    /// Times to suggestion come from the requests themselves here, since the degradation window covers a fixed five
    /// minutes; for a five-minute span the two agree.
    /// </summary>
    /// <param name="from">The start of the span (UTC).</param>
    /// <returns>The figures.</returns>
    public AutocompleteStats Summarize(DateTimeOffset from)
    {
        var firstChunks = new List<TimeSpan>();
        var suggestions = new List<TimeSpan>();
        var counts = new MetricCounts();
        lock (_gate)
        {
            Prune(_clock.UtcNow);
            foreach (var record in _records)
            {
                if (record.RequestedAt < from)
                {
                    continue;
                }

                counts.Add(record);
                if (record.FirstChunk is { } first)
                {
                    firstChunks.Add(first);
                }

                if (record.Suggestion is { } ready)
                {
                    suggestions.Add(ready);
                }
            }
        }

        return new AutocompleteStats(counts, DurationPercentiles.From(firstChunks), DurationPercentiles.From(suggestions));
    }

    /// <inheritdoc/>
    public void Reset()
    {
        List<CancellationTokenSource> watches = [];
        lock (_gate)
        {
            foreach (var record in _records)
            {
                if (record.SlowWatch is { } watch)
                {
                    watches.Add(watch);
                    record.SlowWatch = null;
                }
            }

            _records.Clear();
            _byAttempt.Clear();
            _retired.Clear();
            _slowCount = 0;
        }

        foreach (var watch in watches)
        {
            CancelAndDispose(watch);
        }

        _latency.Clear();
        RaiseIndicatorIfChanged();
    }

    /// <summary>Records that the editor showed a suggestion (once per request, however often it is shown again while typed through).</summary>
    /// <param name="attempt">The attempt number from the proposal id.</param>
    public void SuggestionShown(long attempt) => Update(attempt, r => r.Shown = true);

    /// <summary>Records that the whole suggestion was accepted (Tab, or typed through to its end).</summary>
    /// <param name="attempt">The attempt number from the proposal id.</param>
    public void SuggestionAccepted(long attempt) => Update(attempt, r => Resolve(r, Resolution.Accepted));

    /// <summary>Records that the next word of the suggestion was accepted (Ctrl+Right).</summary>
    /// <param name="attempt">The attempt number from the proposal id.</param>
    public void WordAccepted(long attempt) => Update(attempt, r => Resolve(r, Resolution.AcceptedWord));

    /// <summary>Records that the suggestion was dismissed or typed over.</summary>
    /// <param name="attempt">The attempt number from the proposal id.</param>
    public void SuggestionRejected(long attempt) => Update(attempt, r => Resolve(r, Resolution.Rejected));

    /// <summary>Records that nothing was requested for a keystroke: a suppression rule, a completion list with a selected item.</summary>
    /// <param name="languageId">The language identifier.</param>
    public void Suppressed(string languageId)
    {
        var now = _clock.UtcNow;
        var record = new RequestRecord(0, SafeLanguage(languageId), now) { Terminal = AutocompleteOutcome.Suppressed, Ended = true };
        lock (_gate)
        {
            Prune(now);
            Enqueue(record);
        }
    }

    /// <summary>Records the start of an attempt (called by the pipeline for every keystroke that asks for a suggestion).</summary>
    internal void RequestStarted(long attempt, string languageId)
    {
        var now = _clock.UtcNow;
        var record = new RequestRecord(attempt, SafeLanguage(languageId), now);
        lock (_gate)
        {
            Prune(now);
            Enqueue(record);
            _byAttempt[attempt] = record;
        }
    }

    /// <summary>Records that the attempt survived the debounce and the request goes to the host now.</summary>
    internal void RequestSent(long attempt, bool multiLine, bool degraded)
    {
        var now = _clock.UtcNow;
        CancellationToken token;
        lock (_gate)
        {
            if (!_byAttempt.TryGetValue(attempt, out var record) || record.Ended)
            {
                return;
            }

            record.SentAt = now;
            record.MultiLine = multiLine;
            record.Degraded = degraded;
            var watch = new CancellationTokenSource();
            record.SlowWatch = watch;
            token = watch.Token;
        }

        _ = WatchSlowAsync(attempt, token);
    }

    /// <summary>Records the first chunk of the host's answer.</summary>
    internal void FirstChunk(long attempt)
    {
        var now = _clock.UtcNow;
        Update(attempt, r =>
        {
            if (r.FirstChunk is null && r.SentAt is { } sent)
            {
                r.FirstChunk = now - sent;
                ClearSlow(r);
            }
        });
    }

    /// <summary>Records that the first suggestion worth showing is ready.</summary>
    internal void SuggestionReady(long attempt, TimeSpan latency) =>
        Update(attempt, r =>
        {
            r.Suggestion ??= latency;
            ClearSlow(r);
        });

    /// <summary>Records why the attempt produced nothing: <see cref="AutocompleteOutcome.Empty"/>, <see cref="AutocompleteOutcome.Error"/> or <see cref="AutocompleteOutcome.Suppressed"/>.</summary>
    internal void RequestFailed(long attempt, AutocompleteOutcome reason) =>
        Update(attempt, r =>
        {
            if (r.Terminal == AutocompleteOutcome.Pending)
            {
                r.Terminal = reason;
            }
        });

    /// <summary>Records that the pipeline has finished with the attempt, whatever came of it.</summary>
    internal void RequestEnded(long attempt) =>
        Update(attempt, r =>
        {
            r.Ended = true;
            ClearSlow(r);
        });

    /// <summary>Gets the most recent request records, newest last (for tests).</summary>
    internal IReadOnlyList<RequestRecord> Records()
    {
        lock (_gate)
        {
            return [.. _records];
        }
    }

    private static void Resolve(RequestRecord record, Resolution resolution)
    {
        if (resolution > record.Resolution)
        {
            record.Resolution = resolution;
        }
    }

    private static void CancelAndDispose(CancellationTokenSource watch)
    {
        try
        {
            watch.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already finished.
        }
        finally
        {
            watch.Dispose();
        }
    }

    /// <summary>Keeps only characters that can appear in a language identifier, so a stray value can never carry a path.</summary>
    private static string SafeLanguage(string? languageId)
    {
        if (string.IsNullOrEmpty(languageId) || languageId!.Length > 24)
        {
            return DocumentLanguage.PlainText;
        }

        foreach (var c in languageId)
        {
            if (!((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '+' || c == '#' || c == '-' || c == '_'))
            {
                return "other";
            }
        }

        return languageId;
    }

    private void Update(long attempt, Action<RequestRecord> change)
    {
        var raise = false;
        lock (_gate)
        {
            if (!_byAttempt.TryGetValue(attempt, out var record))
            {
                return;
            }

            var before = record.Slow;
            change(record);
            raise = before != record.Slow;
        }

        if (raise)
        {
            RaiseIndicatorIfChanged();
        }
    }

    /// <summary>Stops the slow-request timer of a record and takes it out of the waiting count. Call under the lock.</summary>
    private void ClearSlow(RequestRecord record)
    {
        if (record.SlowWatch is { } watch)
        {
            record.SlowWatch = null;
            CancelAndDispose(watch);
        }

        if (record.Slow)
        {
            record.Slow = false;
            _slowCount--;
        }
    }

    private void Enqueue(RequestRecord record)
    {
        _records.Enqueue(record);
        while (_records.Count > MaxRecords)
        {
            Retire(_records.Dequeue());
        }
    }

    private void Prune(DateTimeOffset now)
    {
        var oldest = now - Window;
        while (_records.Count > 0 && _records.Peek().RequestedAt < oldest)
        {
            Retire(_records.Dequeue());
        }
    }

    private void Retire(RequestRecord record)
    {
        ClearSlow(record);
        record.Ended = true;
        _byAttempt.Remove(record.Attempt);
        _retired.Add(record);
    }

    private async Task WatchSlowAsync(long attempt, CancellationToken token)
    {
        try
        {
            await _clock.DelayAsync(SlowAfter, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        var raise = false;
        lock (_gate)
        {
            if (_byAttempt.TryGetValue(attempt, out var record) && !record.Ended && record.FirstChunk is null && record.Suggestion is null && !record.Slow)
            {
                record.Slow = true;
                _slowCount++;
                raise = true;
            }
        }

        if (raise)
        {
            RaiseIndicatorIfChanged();
        }
    }

    private void RaiseIndicatorIfChanged()
    {
        var current = Indicator;
        lock (_gate)
        {
            if (_announced == current)
            {
                return;
            }

            _announced = current;
        }

        IndicatorChanged?.Invoke(this, EventArgs.Empty);
    }
}
