using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;
using Newtonsoft.Json.Linq;
using Pillion.Contracts;
using Pillion.VisualStudio.Core;
using Pillion.VisualStudio.Core.Autocomplete;

namespace Pillion.VisualStudio.Tests;

/// <summary>The rolling window, outcome counting and degradation state of <see cref="AutocompleteMetrics"/>, with a fake clock.</summary>
public sealed class AutocompleteMetricsTests
{
    private readonly ManualClock _clock = new();
    private readonly LatencyWindow _latency;
    private readonly AutocompleteMetrics _metrics;
    private long _attempt;

    public AutocompleteMetricsTests()
    {
        _latency = new LatencyWindow(() => _clock.UtcNow);
        _metrics = new AutocompleteMetrics(_clock, _latency);
    }

    [Fact]
    public void Percentiles_AreNearestRankOverTheWindow_AndComeFromTheDegradationWindow()
    {
        for (var i = 1; i <= 20; i++)
        {
            Complete(TimeSpan.FromMilliseconds(i * 100));
        }

        var snapshot = _metrics.Snapshot();

        Assert.Equal(20, snapshot.Recent.TimeToSuggestion.Count);
        Assert.Equal(TimeSpan.FromMilliseconds(1000), snapshot.Recent.TimeToSuggestion.P50);
        Assert.Equal(TimeSpan.FromMilliseconds(1900), snapshot.Recent.TimeToSuggestion.P95);

        // One source of truth: the degradation window the rule reads reports the same numbers.
        var window = _latency.Snapshot();
        Assert.Equal(window.P50, snapshot.Recent.TimeToSuggestion.P50);
        Assert.Equal(window.P95, snapshot.Recent.TimeToSuggestion.P95);

        // And the per-request view of the same samples (the reporting window) agrees for a window this long.
        var summary = _metrics.Summarize(_clock.UtcNow - AutocompleteMetrics.Window);
        Assert.Equal(snapshot.Recent.TimeToSuggestion.P50, summary.TimeToSuggestion.P50);
        Assert.Equal(snapshot.Recent.TimeToSuggestion.P95, summary.TimeToSuggestion.P95);
    }

    [Fact]
    public void FirstChunkPercentiles_AreMeasuredFromSend()
    {
        for (var i = 1; i <= 4; i++)
        {
            var id = Start();
            _metrics.RequestSent(id, multiLine: false, degraded: false);
            _clock.Advance(TimeSpan.FromMilliseconds(i * 50));
            _metrics.FirstChunk(id);
            _metrics.SuggestionReady(id, TimeSpan.FromMilliseconds(i * 50 + 10));
            _metrics.RequestEnded(id);
        }

        var stats = _metrics.Snapshot().Recent;

        Assert.Equal(4, stats.TimeToFirstChunk.Count);
        Assert.Equal(TimeSpan.FromMilliseconds(100), stats.TimeToFirstChunk.P50);
        Assert.Equal(TimeSpan.FromMilliseconds(200), stats.TimeToFirstChunk.P95);
    }

    [Fact]
    public void Eviction_DropsRequestsAfterFiveMinutes_ButKeepsTheCumulativeCounts()
    {
        Complete(TimeSpan.FromMilliseconds(400), shown: true, accepted: true);
        _clock.Advance(TimeSpan.FromMinutes(4));
        Complete(TimeSpan.FromMilliseconds(500), shown: true);

        var before = _metrics.Snapshot();
        Assert.Equal(2, before.Recent.Requests);

        _clock.Advance(TimeSpan.FromMinutes(1) + TimeSpan.FromSeconds(1));
        var after = _metrics.Snapshot();

        Assert.Equal(1, after.Recent.Requests);
        Assert.Equal(1, after.Recent.Shown);
        Assert.Equal(0, after.Recent.AcceptedAny);
        Assert.Equal(2, after.Total.Requests);
        Assert.Equal(2, after.Total.Shown);
        Assert.Equal(1, after.Total.Accepted);
        Assert.Equal(1, after.Recent.TimeToSuggestion.Count);

        _clock.Advance(TimeSpan.FromMinutes(5));
        var empty = _metrics.Snapshot();
        Assert.Equal(0, empty.Recent.Attempts);
        Assert.Null(empty.Recent.ShowRate);
        Assert.Null(empty.Recent.AcceptanceRate);
        Assert.Equal(2, empty.Total.Requests);
        Assert.Equal(0, empty.Recent.TimeToSuggestion.Count);
    }

    [Fact]
    public void ShowRate_IsShownOverRequests_AndAcceptanceRate_IsAcceptedOverShown()
    {
        // 10 requests: 5 shown. Of those, 2 accepted whole, 1 by word, 1 rejected, 1 still on offer.
        var ids = Enumerable.Range(0, 10).Select(_ => Start()).ToList();
        foreach (var id in ids)
        {
            _metrics.RequestSent(id, multiLine: false, degraded: false);
            _metrics.SuggestionReady(id, TimeSpan.FromMilliseconds(300));
            _metrics.RequestEnded(id);
        }

        foreach (var id in ids.Take(5))
        {
            _metrics.SuggestionShown(id);
        }

        _metrics.SuggestionAccepted(ids[0]);
        _metrics.SuggestionAccepted(ids[1]);
        _metrics.WordAccepted(ids[2]);
        _metrics.SuggestionRejected(ids[3]);

        var stats = _metrics.Snapshot().Recent;

        Assert.Equal(10, stats.Requests);
        Assert.Equal(5, stats.Shown);
        Assert.Equal(2, stats.Accepted);
        Assert.Equal(1, stats.AcceptedWord);
        Assert.Equal(1, stats.Rejected);
        Assert.Equal(0.5, stats.ShowRate);
        Assert.Equal(3 / 5.0, stats.AcceptanceRate);
        Assert.Equal(5, stats.CancelledByKeystroke); // Produced a suggestion the editor never displayed.
    }

    [Fact]
    public void ARejectionAfterAnAcceptance_DoesNotUndoIt_AndAShownEventIsCountedOnce()
    {
        var id = Start();
        _metrics.RequestSent(id, multiLine: true, degraded: false);
        _metrics.RequestEnded(id);
        _metrics.SuggestionShown(id);
        _metrics.SuggestionShown(id); // Re-shown while typed through.
        _metrics.SuggestionRejected(id);
        _metrics.WordAccepted(id);
        _metrics.SuggestionRejected(id);

        var stats = _metrics.Snapshot().Recent;

        Assert.Equal(1, stats.Shown);
        Assert.Equal(1, stats.AcceptedWord);
        Assert.Equal(0, stats.Rejected);

        _metrics.SuggestionAccepted(id);
        stats = _metrics.Snapshot().Recent;
        Assert.Equal(1, stats.Accepted);
        Assert.Equal(0, stats.AcceptedWord);
    }

    [Fact]
    public void Outcomes_AreCountedPerKind()
    {
        var cancelledBeforeSend = Start();
        _metrics.RequestEnded(cancelledBeforeSend);

        SendAndEnd(AutocompleteOutcome.Empty);
        SendAndEnd(AutocompleteOutcome.Error);
        var cancelledInFlight = Start();
        _metrics.RequestSent(cancelledInFlight, multiLine: false, degraded: false);
        _metrics.RequestEnded(cancelledInFlight);
        var multiLine = Start();
        _metrics.RequestSent(multiLine, multiLine: true, degraded: true);
        var unsent = Start();
        _metrics.RequestFailed(unsent, AutocompleteOutcome.Suppressed); // Host not ready.
        _metrics.RequestEnded(unsent);
        _metrics.Suppressed("csharp");

        var stats = _metrics.Snapshot().Recent;

        Assert.Equal(1, stats.Empty);
        Assert.Equal(1, stats.Errors);
        Assert.Equal(2, stats.CancelledByKeystroke);
        Assert.Equal(2, stats.Suppressed);
        Assert.Equal(1, stats.InFlight);
        Assert.Equal(5, stats.Attempts); // Suppressed ones never became attempts.
        Assert.Equal(4, stats.Requests);
        Assert.Equal(1, stats.MultiLine);
        Assert.Equal(1, stats.DegradedRequests);
    }

    [Fact]
    public void Degradation_EntersAtTwentySamplesOverTheLimit_ReportsWhySince_AndRecoversWhenTheWindowEmpties()
    {
        var changes = 0;
        _metrics.IndicatorChanged += (_, _) => changes++;

        for (var i = 0; i < LatencyWindow.MinimumSamples - 1; i++)
        {
            Complete(TimeSpan.FromSeconds(2));
        }

        Assert.False(_metrics.Snapshot().IsDegraded); // One short of the minimum.

        var degradedAt = _clock.UtcNow;
        Complete(TimeSpan.FromSeconds(2));
        var snapshot = _metrics.Snapshot();

        Assert.True(snapshot.IsDegraded);
        Assert.Equal(AutocompleteIndicator.Degraded, snapshot.Indicator);
        Assert.Equal(degradedAt, snapshot.Degradation!.Since);
        Assert.Equal(TimeSpan.FromSeconds(2), snapshot.Degradation.P95);
        Assert.Equal(20, snapshot.Degradation.Samples);
        Assert.Contains("p95 2000 ms over the 1500 ms limit in 20 suggestions", snapshot.Degradation.Reason, StringComparison.Ordinal);
        Assert.Equal(1, changes);

        _clock.Advance(LatencyWindow.Window + TimeSpan.FromSeconds(1));
        snapshot = _metrics.Snapshot();

        Assert.False(snapshot.IsDegraded);
        Assert.Null(snapshot.Degradation);
        Assert.Equal(AutocompleteIndicator.None, snapshot.Indicator);
        Assert.Equal(2, changes);
    }

    [Fact]
    public void Degradation_RecoversWhenFastSuggestionsPullTheP95Down()
    {
        for (var i = 0; i < 20; i++)
        {
            Complete(TimeSpan.FromSeconds(2));
        }

        Assert.True(_metrics.Snapshot().IsDegraded);

        // The 20 slow ones stay in the window; they fall under the p95 once they are 5% or less of it.
        for (var i = 0; i < 400; i++)
        {
            Complete(TimeSpan.FromMilliseconds(200));
        }

        var snapshot = _metrics.Snapshot();
        Assert.False(snapshot.IsDegraded);
        Assert.Equal(420, snapshot.Recent.TimeToSuggestion.Count);
        Assert.Equal(TimeSpan.FromMilliseconds(200), snapshot.Recent.TimeToSuggestion.P50);
    }

    [Fact]
    public void RecordedDegradedFlag_MarksRequestsMadeWhileDegraded()
    {
        var id = Start();
        _metrics.RequestSent(id, multiLine: false, degraded: true);
        _metrics.RequestEnded(id);

        Assert.Equal(1, _metrics.Snapshot().Recent.DegradedRequests);
    }

    [Fact]
    public async Task Indicator_ShowsWaitingOnlyAfterTheModelHasKeptARequestWaitingForASecond()
    {
        var changes = 0;
        _metrics.IndicatorChanged += (_, _) => Interlocked.Increment(ref changes);
        var id = Start();
        _metrics.RequestSent(id, multiLine: false, degraded: false);
        await _clock.WhenWaitingAsync(AutocompleteMetrics.SlowAfter);

        _clock.Advance(TimeSpan.FromMilliseconds(999));
        Assert.Equal(AutocompleteIndicator.None, _metrics.Indicator);

        _clock.Advance(TimeSpan.FromMilliseconds(1));
        await Wait.UntilAsync(() => _metrics.Indicator == AutocompleteIndicator.Waiting && Volatile.Read(ref changes) == 1, "the request counts as waiting");

        _metrics.FirstChunk(id);
        Assert.Equal(AutocompleteIndicator.None, _metrics.Indicator);
        Assert.Equal(2, Volatile.Read(ref changes));
    }

    [Fact]
    public async Task Indicator_NeverShowsWaitingForARequestThatAnsweredInTime()
    {
        var id = Start();
        _metrics.RequestSent(id, multiLine: false, degraded: false);
        await _clock.WhenWaitingAsync(AutocompleteMetrics.SlowAfter);
        _metrics.FirstChunk(id);
        _metrics.RequestEnded(id);

        _clock.Advance(TimeSpan.FromSeconds(5));
        await Task.Delay(50);

        Assert.Equal(AutocompleteIndicator.None, _metrics.Indicator);
        Assert.Empty(_clock.Pending);
    }

    [Fact]
    public void Language_IsRestrictedToSafeCharacters_SoAPathNeverReachesTheLog()
    {
        var id = Interlocked.Increment(ref _attempt);
        _metrics.RequestStarted(id, @"C:\x y.cs");
        _metrics.RequestSent(id, multiLine: false, degraded: false);
        var id2 = Interlocked.Increment(ref _attempt);
        _metrics.RequestStarted(id2, "csharp");
        _metrics.RequestSent(id2, multiLine: false, degraded: false);
        var id3 = Interlocked.Increment(ref _attempt);
        _metrics.RequestStarted(id3, new string('x', 100));
        _metrics.RequestSent(id3, multiLine: false, degraded: false);

        var languages = _metrics.Snapshot().Recent.Languages;

        Assert.Equal(["csharp", "other", "plaintext"], languages.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray());
        Assert.Equal(1, languages["csharp"].Requests);
    }

    [Fact]
    public void Summarize_CoversOnlyRequestsMadeSinceTheGivenTime()
    {
        Complete(TimeSpan.FromMilliseconds(900), shown: true);
        _clock.Advance(TimeSpan.FromMinutes(1));
        var from = _clock.UtcNow;
        Complete(TimeSpan.FromMilliseconds(100), shown: true, accepted: true);
        Complete(TimeSpan.FromMilliseconds(200));

        var stats = _metrics.Summarize(from);

        Assert.Equal(2, stats.Requests);
        Assert.Equal(1, stats.Shown);
        Assert.Equal(1, stats.Accepted);
        Assert.Equal(TimeSpan.FromMilliseconds(100), stats.TimeToSuggestion.P50);
        Assert.Equal(TimeSpan.FromMilliseconds(200), stats.TimeToSuggestion.P95);
        Assert.Equal(3, _metrics.Snapshot().Recent.Requests);
    }

    [Fact]
    public void Reset_ForgetsEverythingIncludingTheDegradedState()
    {
        for (var i = 0; i < 20; i++)
        {
            Complete(TimeSpan.FromSeconds(2), shown: true);
        }

        Assert.True(_metrics.Snapshot().IsDegraded);

        _metrics.Reset();
        var snapshot = _metrics.Snapshot();

        Assert.False(snapshot.IsDegraded);
        Assert.Equal(0, snapshot.Recent.Attempts);
        Assert.Equal(0, snapshot.Total.Attempts);
        Assert.Equal(0, snapshot.Recent.TimeToSuggestion.Count);
    }

    [Fact]
    public void EventsForAnUnknownAttempt_AreIgnored()
    {
        _metrics.SuggestionShown(999);
        _metrics.SuggestionAccepted(999);
        _metrics.FirstChunk(999);
        _metrics.RequestEnded(999);

        Assert.Equal(0, _metrics.Snapshot().Total.Attempts);
    }

    private long Start()
    {
        var id = Interlocked.Increment(ref _attempt);
        _metrics.RequestStarted(id, "csharp");
        return id;
    }

    private void SendAndEnd(AutocompleteOutcome reason)
    {
        var id = Start();
        _metrics.RequestSent(id, multiLine: false, degraded: false);
        _metrics.RequestFailed(id, reason);
        _metrics.RequestEnded(id);
    }

    /// <summary>One request that sent, got its suggestion after <paramref name="latency"/>, and optionally was shown and accepted.</summary>
    private void Complete(TimeSpan latency, bool shown = false, bool accepted = false)
    {
        var id = Start();
        _metrics.RequestSent(id, multiLine: false, degraded: false);
        _latency.Record(latency);
        _metrics.SuggestionReady(id, latency);
        _metrics.RequestEnded(id);
        if (shown)
        {
            _metrics.SuggestionShown(id);
        }

        if (accepted)
        {
            _metrics.SuggestionAccepted(id);
        }
    }
}

/// <summary>Outcome recording from the real pipeline (fake host, fake clock) and from the adapter's events.</summary>
public sealed class AutocompleteMetricsPipelineTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(200);

    private const string Code =
        "public static class Calculator\n" +
        "{\n" +
        "    public static int Min(int[] numbers)\n" +
        "    {\n" +
        "        var smallest = \n" +
        "    }\n" +
        "}\n";

    private readonly ManualClock _clock = new();
    private readonly FakeCompletionModel _model = new();

    [Fact]
    public async Task ASuggestion_RecordsFirstChunkAndSuggestionTimes_ThenShownAndAccepted()
    {
        _model.Manual = true;
        _model.Respond = _ => ["Math.Min", "(a, b);"];
        var (engine, session) = CreateSession();

        var attempt = session.Start(InputAt("var smallest = "), CancellationToken.None);
        await _clock.WhenWaitingAsync(Debounce);
        _clock.Advance(Debounce);
        await Wait.UntilAsync(() => _model.Requests.Count == 1, "the request reached the host");
        _clock.Advance(TimeSpan.FromMilliseconds(120));
        _model.Release();
        await Wait.UntilAsync(() => engine.Metrics.Snapshot().Recent.TimeToFirstChunk.Count == 1, "the first chunk was recorded");
        _clock.Advance(TimeSpan.FromMilliseconds(300));
        _model.Release();
        Assert.NotNull(await attempt.Completed.WithTimeout(Timeout));

        var afterAnswer = engine.Metrics.Snapshot().Recent;
        Assert.Equal(1, afterAnswer.Requests);
        Assert.Equal(TimeSpan.FromMilliseconds(120), afterAnswer.TimeToFirstChunk.P50);
        Assert.Equal(TimeSpan.FromMilliseconds(120), afterAnswer.TimeToSuggestion.P50); // the first visible text, not the whole line
        Assert.Equal(0, afterAnswer.Shown);

        engine.Metrics.SuggestionShown(attempt.Id);
        engine.Metrics.SuggestionAccepted(attempt.Id);

        var stats = engine.Metrics.Snapshot().Recent;
        Assert.Equal(1, stats.Shown);
        Assert.Equal(1, stats.Accepted);
        Assert.Equal(1.0, stats.ShowRate);
        Assert.Equal(1.0, stats.AcceptanceRate);
        Assert.Equal(1, stats.Languages["csharp"].Accepted);
        Assert.Equal(0, stats.InFlight);
    }

    [Fact]
    public async Task AKeystrokeDuringTheDebounce_IsCancelledByKeystroke_AndNeverSent()
    {
        var (engine, session) = CreateSession();

        var first = session.Start(InputAt("var smallest = "), CancellationToken.None);
        await _clock.WhenWaitingAsync(Debounce);
        session.CancelPending();
        Assert.Null(await first.Completed.WithTimeout(Timeout));

        var stats = engine.Metrics.Snapshot().Recent;
        Assert.Equal(1, stats.Attempts);
        Assert.Equal(0, stats.Requests);
        Assert.Equal(1, stats.CancelledByKeystroke);
        Assert.Empty(_model.Requests);
    }

    [Fact]
    public async Task AKeystrokeDuringTheStream_IsCancelledByKeystroke_AfterTheRequestWasSent()
    {
        _model.Manual = true;
        var (engine, session) = CreateSession();

        var attempt = session.Start(InputAt("var smallest = "), CancellationToken.None);
        await _clock.WhenWaitingAsync(Debounce);
        _clock.Advance(Debounce);
        await Wait.UntilAsync(() => _model.Requests.Count == 1, "the request reached the host");
        session.CancelPending();
        Assert.Null(await attempt.Completed.WithTimeout(Timeout));

        var stats = engine.Metrics.Snapshot().Recent;
        Assert.Equal(1, stats.Requests);
        Assert.Equal(1, stats.CancelledByKeystroke);
        Assert.Equal(0, stats.Shown);
        Assert.Equal(0, stats.InFlight);
    }

    [Fact]
    public async Task AHostFailure_IsAnError_AndAnEmptyAnswerIsEmpty()
    {
        _model.Failure = new PillionRpcException(new PillionError { Code = PillionErrorCode.ProviderUnavailable, Message = "down" });
        var (engine, session) = CreateSession();
        var input = InputAt("var smallest = ");
        input.Immediate = true;
        Assert.Null(await session.Start(input, CancellationToken.None).Completed.WithTimeout(Timeout));

        _model.Failure = null;
        _model.Respond = _ => [" "];
        Assert.Null(await session.Start(input, CancellationToken.None).Completed.WithTimeout(Timeout));

        var stats = engine.Metrics.Snapshot().Recent;
        Assert.Equal(1, stats.Errors);
        Assert.Equal(1, stats.Empty);
        Assert.Equal(2, stats.Requests);
    }

    [Fact]
    public async Task AHostThatIsNotReady_IsSuppressed_NotAnError()
    {
        var connection = new FakeHostConnection(HostState.Restarting).With<IModelService>(_model);
        var engine = new AutocompleteEngine(connection, availability: null, _clock, log: null);
        var input = InputAt("var smallest = ");
        input.Immediate = true;

        Assert.Null(await engine.CreateSession().Start(input, CancellationToken.None).Completed.WithTimeout(Timeout));

        var stats = engine.Metrics.Snapshot().Recent;
        Assert.Equal(1, stats.Suppressed);
        Assert.Equal(0, stats.Errors);
        Assert.Equal(0, stats.Attempts);
    }

    [Fact]
    public async Task TheDegradationRule_ReadsTheSameWindowTheMetricsReport()
    {
        var (engine, session) = CreateSession();
        for (var i = 0; i < LatencyWindow.MinimumSamples; i++)
        {
            engine.Latency.Record(TimeSpan.FromSeconds(2));
        }

        var input = InputAt("var smallest = ");
        var attempt = session.Start(input, CancellationToken.None);
        await _clock.WhenWaitingAsync(LatencyWindow.DegradedDebounce);
        _clock.Advance(LatencyWindow.DegradedDebounce);
        await attempt.Completed.WithTimeout(Timeout);

        var snapshot = engine.Metrics.Snapshot();
        Assert.True(snapshot.IsDegraded);
        Assert.True(engine.Latency.IsDegraded);
        Assert.Equal(engine.Latency.Snapshot().P95, snapshot.Recent.TimeToSuggestion.P95);
        Assert.Equal(1, snapshot.Recent.DegradedRequests);
        Assert.Contains("degraded since", AutocompleteMetricsText.Describe(snapshot, host: null), StringComparison.Ordinal);
    }

    private (AutocompleteEngine Engine, AutocompleteSession Session) CreateSession()
    {
        var connection = new FakeHostConnection(HostState.Ready).With<IModelService>(_model);
        var engine = new AutocompleteEngine(connection, availability: null, _clock, log: null);
        return (engine, engine.CreateSession());
    }

    private static CompletionInput InputAt(string after) => new()
    {
        DocumentKey = @"C:\src\Calculator.cs",
        FilePath = @"C:\src\Calculator.cs",
        LanguageId = "csharp",
        Text = new StringDocumentText(Code),
        Caret = Code.IndexOf(after, StringComparison.Ordinal) + after.Length,
    };
}

/// <summary>The words of the status text, the JSON line, the local log and the five-minute report.</summary>
public sealed class AutocompleteMetricsOutputTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly ManualClock _clock = new();
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "pillion-metrics-" + Guid.NewGuid().ToString("N"));

    /// <summary>
    /// Reads the lines of a file that the metrics writer may be appending to. <see cref="File.ReadAllLines(string)"/> opens
    /// with <see cref="FileShare.Read"/>, which an open write handle of a concurrent writer refuses ("being used by another
    /// process"); sharing writes and deletes lets the reader coexist with the writer, as a person tailing the file would.
    /// </summary>
    private static string[] ReadLines(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        var lines = new List<string>();
        while (reader.ReadLine() is { } line)
        {
            lines.Add(line);
        }

        return [.. lines];
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    [Fact]
    public void StatusSuffix_IsEmptyUnlessWaitingOrDegraded()
    {
        Assert.Equal(string.Empty, AutocompleteMetricsText.StatusSuffix(AutocompleteIndicator.None));
        Assert.Equal(", waiting for suggestion", AutocompleteMetricsText.StatusSuffix(AutocompleteIndicator.Waiting));
        Assert.Equal(", suggestions slow", AutocompleteMetricsText.StatusSuffix(AutocompleteIndicator.Degraded));
        Assert.Equal("Pillion: Ready, Online, suggestions slow", HostStatusText.Format(HostState.Ready, 0, 3, ConnectivityState.Online) + AutocompleteMetricsText.StatusSuffix(AutocompleteIndicator.Degraded));
    }

    [Fact]
    public void Describe_ListsTheClientAndHostNumbers()
    {
        var engine = EngineWithActivity(out _);
        var text = AutocompleteMetricsText.Describe(engine.Metrics.Snapshot(), SampleHost());

        Assert.Contains("Autocomplete, last 5 min", text, StringComparison.Ordinal);
        Assert.Contains("3 sent to the host, 2 shown (67% of requests); 1 taken (50% of shown: 1 whole, 0 by word); 1 rejected", text, StringComparison.Ordinal);
        Assert.Contains("Time to suggestion: p50 400 ms, p95 600 ms (3 samples)", text, StringComparison.Ordinal);
        Assert.Contains("State: normal", text, StringComparison.Ordinal);
        Assert.Contains("cache hit rate 50% (40 repeats, 20 typed-through, 60 misses)", text, StringComparison.Ordinal);
        Assert.Contains("time to first token p50 410 ms, p95 980 ms; total p50 520 ms, p95 1210 ms", text, StringComparison.Ordinal);
        Assert.Contains("40 counted with the tokenizer, 5 estimated; largest 1010 tokens", text, StringComparison.Ordinal);
        Assert.Contains("Since Visual Studio started: 3 sent, 2 shown, 1 taken", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Describe_WithoutHostNumbers_SaysSo()
    {
        var engine = EngineWithActivity(out _);

        var text = AutocompleteMetricsText.Describe(engine.Metrics.Snapshot(), host: null);

        Assert.Contains("Host: no numbers", text, StringComparison.Ordinal);
    }

    [Fact]
    public void JsonLine_HasCountsAndDurationsOnly()
    {
        var engine = EngineWithActivity(out var start);
        var stats = engine.Metrics.Summarize(start);
        var end = _clock.UtcNow;

        var line = AutocompleteMetricsJson.Create(start, end, stats, degradation: null, SampleHost());

        Assert.DoesNotContain('\n', line);
        var json = JObject.Parse(line);
        Assert.Equal(1, (int)json["schema"]!);
        Assert.Equal(3, (long)json["requests"]!);
        Assert.Equal(2, (long)json["shown"]!);
        Assert.Equal(1, (long)json["accepted"]!);
        Assert.Equal(1, (long)json["rejected"]!);
        Assert.False((bool)json["degraded"]!);
        Assert.Equal(400, (double)json["suggestionMs"]!["p50"]!);
        Assert.Equal(1, (long)json["languages"]!["csharp"]!["accepted"]!);
        Assert.Equal(0.5, (double)json["host"]!["hitRate"]!);

        // Every key is a known one: nothing that could hold a path, a prompt or code.
        var keys = AllKeys(json).Select(k => Regex.Replace(k, "^languages\\.[a-z0-9+#_-]+", "languages.<language>")).ToHashSet(StringComparer.Ordinal);
        Assert.Subset(KnownKeys, keys);

        // Every string value is a timestamp (the only strings the line carries besides keys).
        foreach (var value in json.DescendantsAndSelf().OfType<JValue>().Where(v => v.Type == JTokenType.String))
        {
            Assert.Matches("^\\d{4}-\\d\\d-\\d\\dT\\d\\d:\\d\\d:\\d\\dZ$", (string)value!);
        }

        Assert.DoesNotContain("Calculator", line, StringComparison.Ordinal);
        Assert.DoesNotContain(@"C:\", line, StringComparison.Ordinal);
        Assert.DoesNotContain("var smallest", line, StringComparison.Ordinal);
    }

    [Fact]
    public void JsonLine_WithADegradedStateAndNoHost_CarriesTheReason()
    {
        var engine = EngineWithActivity(out var start);
        for (var i = 0; i < 20; i++)
        {
            engine.Latency.Record(TimeSpan.FromSeconds(2));
        }

        var line = AutocompleteMetricsJson.Create(start, _clock.UtcNow, engine.Metrics.Summarize(start), engine.Latency.Degradation, host: null);

        var json = JObject.Parse(line);
        Assert.True((bool)json["degraded"]!);
        Assert.Equal(2000, (double)json["degradation"]!["p95Ms"]!);
        Assert.Equal(JTokenType.Null, json["host"]!.Type);
    }

    [Fact]
    public void LogWriter_AppendsOneLinePerCallToTheDatedFile()
    {
        var writer = new AutocompleteMetricsLogWriter(_directory, () => _clock.UtcNow);

        Assert.True(writer.Append("{\"a\":1}"));
        Assert.True(writer.Append("{\"a\":2}"));

        var path = Path.Combine(_directory, "metrics-20261009.jsonl");
        Assert.Equal("metrics-20261009.jsonl", AutocompleteMetricsLogWriter.FileNameFor(_clock.UtcNow));
        Assert.Equal(["{\"a\":1}", "{\"a\":2}"], ReadLines(path));
        Assert.False(File.ReadAllBytes(path).Take(3).SequenceEqual(new byte[] { 0xEF, 0xBB, 0xBF }));
    }

    [Fact]
    public void LogWriter_AppliesTheSevenDayRetention_OnlyToMetricsFiles()
    {
        Directory.CreateDirectory(_directory);
        var old = Path.Combine(_directory, "metrics-20260901.jsonl");
        var recent = Path.Combine(_directory, "metrics-20261007.jsonl");
        var other = Path.Combine(_directory, "vsix-20260901.log");
        foreach (var path in new[] { old, recent, other })
        {
            File.WriteAllText(path, "x");
        }

        File.SetLastWriteTimeUtc(old, _clock.UtcNow.UtcDateTime.AddDays(-8));
        File.SetLastWriteTimeUtc(recent, _clock.UtcNow.UtcDateTime.AddDays(-2));
        File.SetLastWriteTimeUtc(other, _clock.UtcNow.UtcDateTime.AddDays(-30));

        new AutocompleteMetricsLogWriter(_directory, () => _clock.UtcNow).Append("{}");

        Assert.False(File.Exists(old));
        Assert.True(File.Exists(recent));
        Assert.True(File.Exists(other));
    }

    [Fact]
    public void Retention_DeletesTheOldestFilesBeyondTheSizeCap()
    {
        Directory.CreateDirectory(_directory);
        for (var day = 1; day <= 3; day++)
        {
            var path = Path.Combine(_directory, $"metrics-2026100{day}.jsonl");
            File.WriteAllText(path, new string('x', 100));
            File.SetLastWriteTimeUtc(path, _clock.UtcNow.UtcDateTime.AddDays(-4 + day));
        }

        var deleted = AutocompleteMetricsLogWriter.ApplyRetention(_directory, _clock.UtcNow, TimeSpan.FromDays(7), maxTotalBytes: 250);

        Assert.Equal([Path.Combine(_directory, "metrics-20261001.jsonl")], deleted);
        Assert.True(File.Exists(Path.Combine(_directory, "metrics-20261003.jsonl")));
    }

    [Fact]
    public void LogWriter_NeverThrows_WhenTheFolderCannotBeUsed()
    {
        Directory.CreateDirectory(_directory);
        var blocker = Path.Combine(_directory, "file");
        File.WriteAllText(blocker, "x");

        Assert.False(new AutocompleteMetricsLogWriter(Path.Combine(blocker, "sub")).Append("{}"));
    }

    [Theory]
    [InlineData(null, 300)]
    [InlineData("", 300)]
    [InlineData("abc", 300)]
    [InlineData("20", 20)]
    [InlineData("1", 5)]
    [InlineData("9999", 300)]
    public void ParseInterval_TakesSecondsClampedToFiveToThreeHundred(string? value, int expectedSeconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), AutocompleteMetricsReporter.ParseInterval(value));
    }

    [Fact]
    public async Task Reporter_WritesOneLineAndOneSummaryPerFiveMinuteWindow_AndNothingWhenIdle()
    {
        var lines = new List<string>();
        var log = new PillionLog(directory: null, () => _clock.UtcNow) { Sink = line => { lock (lines) { lines.Add(line); } } };
        var diagnostics = new FakeDiagnostics(SampleHost());
        var connection = new FakeHostConnection(HostState.Ready).With<IDiagnosticsService>(diagnostics);
        var engine = new AutocompleteEngine(connection, availability: null, _clock, log);
        var writer = new AutocompleteMetricsLogWriter(_directory, () => _clock.UtcNow);
        using var reporter = new AutocompleteMetricsReporter(engine, writer);
        reporter.Start();
        await _clock.WhenWaitingAsync(AutocompleteMetricsReporter.DefaultInterval);

        Record(engine, shown: true, accepted: true);
        Record(engine, shown: true);
        _clock.Advance(AutocompleteMetricsReporter.DefaultInterval);
        var path = Path.Combine(_directory, AutocompleteMetricsLogWriter.FileNameFor(_clock.UtcNow));
        await Wait.UntilAsync(() => File.Exists(path) && ReadLines(path).Length == 1, "the first window was written");

        // A window with no autocomplete activity writes nothing.
        await _clock.WhenWaitingAsync(AutocompleteMetricsReporter.DefaultInterval);
        _clock.Advance(AutocompleteMetricsReporter.DefaultInterval);
        await _clock.WhenWaitingAsync(AutocompleteMetricsReporter.DefaultInterval);
        Assert.Single(ReadLines(path));

        var json = JObject.Parse(ReadLines(path)[0]);
        Assert.Equal(2, (long)json["requests"]!);
        Assert.Equal(2, (long)json["shown"]!);
        Assert.Equal(300, (int)json["seconds"]!);
        Assert.Equal(1, diagnostics.Calls);
        Assert.Equal(0.5, (double)json["host"]!["hitRate"]!);

        string summary;
        lock (lines)
        {
            summary = Assert.Single(lines);
        }

        Assert.Contains("info autocomplete.metrics", summary, StringComparison.Ordinal);
        Assert.Contains("2 sent, 2 shown (100%), 1 taken (50%)", summary, StringComparison.Ordinal);
        Assert.Contains("host cache hit rate 50%", summary, StringComparison.Ordinal);
        Assert.DoesNotContain('\n', summary);
    }

    [Fact]
    public async Task Reporter_WritesTheLineWithoutHostNumbers_WhenTheHostCannotBeAsked()
    {
        var connection = new FakeHostConnection(HostState.Ready); // No IDiagnosticsService: the call fails.
        var engine = new AutocompleteEngine(connection, availability: null, _clock, log: null);
        var writer = new AutocompleteMetricsLogWriter(_directory, () => _clock.UtcNow);
        using var reporter = new AutocompleteMetricsReporter(engine, writer, TimeSpan.FromSeconds(30));
        Record(engine, shown: false, accepted: false);

        _clock.Advance(TimeSpan.FromSeconds(30));
        Assert.True(await reporter.ReportAsync(CancellationToken.None).WithTimeout(Timeout));

        var path = Path.Combine(_directory, AutocompleteMetricsLogWriter.FileNameFor(_clock.UtcNow));
        var json = JObject.Parse(ReadLines(path)[0]);
        Assert.Equal(JTokenType.Null, json["host"]!.Type);
        Assert.Equal(30, (int)json["seconds"]!);
    }

    [Fact]
    public async Task CoalescingNotifier_RunsAtOnce_ThenAtMostOncePerInterval()
    {
        var runs = 0;
        var notifier = new CoalescingNotifier(_clock, TimeSpan.FromSeconds(2), () => Interlocked.Increment(ref runs));

        notifier.Poke();
        Assert.Equal(1, Volatile.Read(ref runs));

        _clock.Advance(TimeSpan.FromMilliseconds(500));
        notifier.Poke();
        notifier.Poke();
        notifier.Poke();
        Assert.Equal(1, Volatile.Read(ref runs));
        await _clock.WhenWaitingAsync(TimeSpan.FromMilliseconds(1500));

        _clock.Advance(TimeSpan.FromMilliseconds(1500));
        await Wait.UntilAsync(() => Volatile.Read(ref runs) == 2, "the coalesced run happened");
        await Task.Delay(50);
        Assert.Equal(2, Volatile.Read(ref runs));

        _clock.Advance(TimeSpan.FromSeconds(3));
        notifier.Poke();
        Assert.Equal(3, Volatile.Read(ref runs));
    }

    private static readonly HashSet<string> KnownKeys = new(StringComparer.Ordinal)
    {
        "schema", "windowStart", "windowEnd", "seconds", "attempts", "requests", "shown", "accepted", "acceptedWord", "rejected", "displaced",
        "cancelled", "suppressed", "errors", "empty", "multiLine", "degradedRequests", "showRate", "acceptanceRate",
        "firstChunkMs", "firstChunkMs.count", "firstChunkMs.p50", "firstChunkMs.p95",
        "suggestionMs", "suggestionMs.count", "suggestionMs.p50", "suggestionMs.p95",
        "degraded", "degradation", "degradation.since", "degradation.p95Ms", "degradation.samples",
        "languages", "languages.<language>", "languages.<language>.requests", "languages.<language>.shown", "languages.<language>.accepted",
        "host", "host.requests", "host.cacheHits", "host.typeThroughHits", "host.cacheMisses", "host.hitRate", "host.modelRequests",
        "host.failed", "host.cancelled", "host.windowRequests", "host.windowCacheHits",
        "host.firstTokenMs", "host.firstTokenMs.p50", "host.firstTokenMs.p95", "host.totalMs", "host.totalMs.p50", "host.totalMs.p95",
        "host.promptCounted", "host.promptEstimated", "host.promptTokensMax",
    };

    private static IEnumerable<string> AllKeys(JToken token, string prefix = "")
    {
        if (token is JObject obj)
        {
            foreach (var property in obj.Properties())
            {
                var path = prefix.Length == 0 ? property.Name : prefix + "." + property.Name;
                yield return path;
                foreach (var nested in AllKeys(property.Value, path))
                {
                    yield return nested;
                }
            }
        }
    }

    private static AutocompleteHostCounters SampleHost() => new()
    {
        Requests = 120,
        CacheHits = 40,
        TypeThroughHits = 20,
        CacheMisses = 60,
        HitRate = 0.5,
        ModelRequests = 61,
        Dropped = 1,
        Failed = 2,
        Cancelled = 30,
        WindowSeconds = 300,
        WindowRequests = 80,
        WindowCacheHits = 35,
        FirstTokenP50Ms = 410.25,
        FirstTokenP95Ms = 980.25,
        TotalP50Ms = 520.25,
        TotalP95Ms = 1210.25,
        PromptCountedRequests = 40,
        PromptEstimatedRequests = 5,
        PromptTokensCounted = 36000,
        PromptTokensEstimated = 4200,
        PromptTokensMax = 1010,
    };

    private AutocompleteEngine EngineWithActivity(out DateTimeOffset start)
    {
        var engine = new AutocompleteEngine(new FakeHostConnection(HostState.Ready), availability: null, _clock, log: null);
        start = _clock.UtcNow;
        Record(engine, shown: true, accepted: true, latency: 200);
        Record(engine, shown: true, accepted: false, latency: 400, rejected: true);
        Record(engine, shown: false, accepted: false, latency: 600);
        return engine;
    }

    private static long _next;

    private static void Record(AutocompleteEngine engine, bool shown, bool accepted = false, int latency = 300, bool rejected = false)
    {
        var metrics = engine.Metrics;
        var id = Interlocked.Increment(ref _next);
        metrics.RequestStarted(id, "csharp");
        metrics.RequestSent(id, multiLine: false, degraded: false);
        engine.Latency.Record(TimeSpan.FromMilliseconds(latency));
        metrics.SuggestionReady(id, TimeSpan.FromMilliseconds(latency));
        metrics.RequestEnded(id);
        if (shown)
        {
            metrics.SuggestionShown(id);
        }

        if (accepted)
        {
            metrics.SuggestionAccepted(id);
        }

        if (rejected)
        {
            metrics.SuggestionRejected(id);
        }
    }

    private sealed class FakeDiagnostics(AutocompleteHostCounters counters) : IDiagnosticsService
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public Task<AutocompleteHostCounters> GetAutocompleteCountersAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(counters);
        }

        public Task<OutboundRecordDto[]> GetOutboundRecordsAsync(int maxCount, CancellationToken cancellationToken) => Task.FromResult<OutboundRecordDto[]>([]);

        public Task<ContextReportDto> PreviewContextAsync(ChatRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
