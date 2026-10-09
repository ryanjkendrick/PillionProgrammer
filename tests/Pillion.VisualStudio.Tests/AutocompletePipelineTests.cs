using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;
using Pillion.Contracts;
using Pillion.VisualStudio.Core;
using Pillion.VisualStudio.Core.Autocomplete;
using Xunit.Abstractions;

namespace Pillion.VisualStudio.Tests;

public sealed class AutocompletePipelineTests(ITestOutputHelper output)
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
    public async Task Start_WaitsTheDebounceBeforeAskingTheHost()
    {
        var (_, session) = CreateSession();

        var attempt = session.Start(InputAt("var smallest = "), CancellationToken.None);
        await _clock.WhenWaitingAsync(Debounce);
        Assert.Empty(_model.Requests);

        _clock.Advance(TimeSpan.FromMilliseconds(199));
        await Task.Delay(50);
        Assert.Empty(_model.Requests);

        _clock.Advance(TimeSpan.FromMilliseconds(1));
        var result = await attempt.Completed.WithTimeout(Timeout);
        Assert.NotNull(result);
        Assert.Equal("42;", result!.Text);
        Assert.Single(_model.Requests);
        Assert.Equal(attempt.RequestId, _model.Requests[0].RequestId);
    }

    [Fact]
    public async Task Start_CancelsThePreviousAttempt_OnlyTheLastReachesTheHost()
    {
        var (_, session) = CreateSession();

        var first = session.Start(InputAt("var smallest = "), CancellationToken.None);
        await _clock.WhenWaitingAsync(Debounce);
        _clock.Advance(TimeSpan.FromMilliseconds(150));
        var second = session.Start(InputAt("var smallest = "), CancellationToken.None);

        Assert.Null(await first.Completed.WithTimeout(Timeout));
        Assert.True(first.IsCancelled);
        await Wait.UntilAsync(() => _clock.Pending.Count == 1, "only the second debounce is pending");
        _clock.Advance(Debounce);

        Assert.NotNull(await second.Completed.WithTimeout(Timeout));
        Assert.Single(_model.Requests);
        Assert.Equal(second.RequestId, _model.Requests[0].RequestId);
        Assert.NotEqual(first.RequestId, second.RequestId);
    }

    [Fact]
    public async Task CancelPendingUnless_KeepsAnAttemptForTheNewTextAndCancelsAnOlderOne()
    {
        var (_, session) = CreateSession();
        var input = InputAt("var smallest = ");
        input.TextVersion = 7;

        var attempt = session.Start(input, CancellationToken.None);
        session.CancelPendingUnless(7, input.Caret);
        Assert.False(attempt.IsCancelled);

        session.CancelPendingUnless(7, input.Caret - 1); // Caret moved.
        Assert.True(attempt.IsCancelled);

        var next = session.Start(input, CancellationToken.None);
        session.CancelPendingUnless(8, caret: null); // A keystroke made version 8.
        Assert.True(next.IsCancelled);
        Assert.Null(await next.Completed.WithTimeout(Timeout));
    }

    [Fact]
    public async Task BrokerCancellation_EndsTheAttemptWithNoSuggestion()
    {
        var (_, session) = CreateSession();
        using var broker = new CancellationTokenSource();

        var attempt = session.Start(InputAt("var smallest = "), broker.Token);
        await _clock.WhenWaitingAsync(Debounce);
        broker.Cancel();

        Assert.Null(await attempt.Completed.WithTimeout(Timeout));
        Assert.Empty(_model.Requests);
    }

    [Fact]
    public async Task KeystrokeDuringTheStream_CancelsItAndDiscardsTheLateChunks()
    {
        _model.Manual = true;
        _model.Respond = _ => ["Math.Min", "(a, b);"];
        var (_, session) = CreateSession();

        var first = session.Start(InputAt("var smallest = "), CancellationToken.None);
        await _clock.WhenWaitingAsync(Debounce);
        _clock.Advance(Debounce);
        await Wait.UntilAsync(() => _model.Requests.Count == 1, "the first request reached the host");
        _model.Release();

        session.CancelPending();
        Assert.Null(await first.Completed.WithTimeout(Timeout));
        await Wait.UntilAsync(() => Volatile.Read(ref _model.StreamsCancelled) == 1, "the stream was cancelled");
    }

    [Fact]
    public async Task ChunksOfAnotherRequest_AreIgnored()
    {
        _model.ForeignChunkId = "ac-someone-else-1";
        _model.Respond = _ => ["Stale(); ", "Math.Min(a, b);"];
        var (_, session) = CreateSession();

        var attempt = session.Start(InputAt("var smallest = "), CancellationToken.None);
        await _clock.WhenWaitingAsync(Debounce);
        _clock.Advance(Debounce);

        var result = await attempt.Completed.WithTimeout(Timeout);
        Assert.Equal("Math.Min(a, b);", result!.Text);
    }

    [Fact]
    public async Task ImmediateAttempt_SkipsTheDebounce()
    {
        var (_, session) = CreateSession();
        var input = InputAt("var smallest = ");
        input.Immediate = true;

        var result = await session.Start(input, CancellationToken.None).Completed.WithTimeout(Timeout);

        Assert.NotNull(result);
        Assert.Empty(_clock.Pending);
    }

    [Fact]
    public async Task Request_CarriesTheCapturedContext()
    {
        var (engine, session) = CreateSession();
        engine.OpenDocuments.Opened(@"C:\src\Other.cs");
        engine.OpenDocuments.Opened(@"C:\src\Calculator.cs");
        engine.Edits.Record(@"C:\src\Calculator.cs", 4, "        var smallest = ");

        var attempt = session.Start(InputAt("var smallest = "), CancellationToken.None);
        await _clock.WhenWaitingAsync(Debounce);
        _clock.Advance(Debounce);
        await attempt.Completed.WithTimeout(Timeout);

        var request = _model.Requests.Single();
        Assert.Equal(TaskClass.Autocomplete, request.TaskClass);
        Assert.Equal("csharp", request.LanguageId);
        Assert.Equal(@"C:\src\Calculator.cs", request.FilePath);
        Assert.Equal(4, request.CaretLine);
        Assert.Equal(23, request.CaretColumn);
        Assert.EndsWith("var smallest = ", request.Prefix, StringComparison.Ordinal);
        Assert.StartsWith("\n    }", request.Suffix, StringComparison.Ordinal);
        Assert.Equal("public static int Min(int[] numbers)", request.EnclosingDeclaration);
        Assert.Equal([@"C:\src\Calculator.cs", @"C:\src\Other.cs"], request.OpenDocuments);
        Assert.Equal([@"C:\src\Calculator.cs:5: var smallest ="], request.RecentEdits);
        Assert.False(request.AllowMultiLine);
        Assert.Equal(["\n"], request.StopSequences);
        Assert.Equal(AutocompleteEngine.SingleLineMaxTokens, request.MaxTokens);
    }

    [Fact]
    public async Task Request_CapsThePrefixAndSuffix()
    {
        var (_, session) = CreateSession();
        var text = new string('a', 20_000) + "|" + new string('b', 5_000);
        var input = new CompletionInput
        {
            DocumentKey = "big.cs",
            FilePath = "big.cs",
            LanguageId = "csharp",
            Text = new StringDocumentText(text.Replace("|", string.Empty)),
            Caret = 20_000,
        };

        var attempt = session.Start(input, CancellationToken.None);
        await _clock.WhenWaitingAsync(Debounce);
        _clock.Advance(Debounce);
        await attempt.Completed.WithTimeout(Timeout);

        var request = _model.Requests.Single();
        Assert.Equal(ContextCapture.MaxPrefixLength, request.Prefix.Length);
        Assert.Equal(ContextCapture.MaxSuffixLength, request.Suffix.Length);
    }

    [Fact]
    public async Task UsesTheLanguageServiceDeclaration_WhenItAnswersInTime()
    {
        var (_, session) = CreateSession();
        var input = InputAt("var smallest = ");
        input.DeclarationProvider = _ => Task.FromResult<string?>("// in Calculator\npublic static int Min(int[] numbers)");

        var attempt = session.Start(input, CancellationToken.None);
        await _clock.WhenWaitingAsync(Debounce);
        _clock.Advance(Debounce);
        await attempt.Completed.WithTimeout(Timeout);

        Assert.Equal("// in Calculator\npublic static int Min(int[] numbers)", _model.Requests.Single().EnclosingDeclaration);
    }

    [Fact]
    public async Task FallsBackToTheHeuristic_WhenTheLanguageServiceIsSlow()
    {
        var (_, session) = CreateSession();
        var input = InputAt("var smallest = ");
        input.DeclarationProvider = async token =>
        {
            await Task.Delay(System.Threading.Timeout.Infinite, token);
            return "never";
        };

        var attempt = session.Start(input, CancellationToken.None);
        await _clock.WhenWaitingAsync(Debounce);
        _clock.Advance(Debounce);
        await _clock.WhenWaitingAsync(AutocompleteEngine.DeclarationBudget);
        _clock.Advance(AutocompleteEngine.DeclarationBudget);
        await attempt.Completed.WithTimeout(Timeout);

        Assert.Equal("public static int Min(int[] numbers)", _model.Requests.Single().EnclosingDeclaration);
    }

    [Fact]
    public async Task MultiLineRequest_StopsAtTheBlockEndAndCancelsTheRestOnTheHost()
    {
        _model.Respond = _ => ["\n    {\n        return numbers.Min();\n", "    }\n\n    public int Other()\n", "    {\n    }\n"];
        var (_, session) = CreateSession();
        var text = "class C\n{\n    public static int Min(int[] numbers)\n}\n";
        var caret = text.IndexOf(")", StringComparison.Ordinal) + 1;
        var input = new CompletionInput { DocumentKey = "c.cs", FilePath = "c.cs", LanguageId = "csharp", Text = new StringDocumentText(text), Caret = caret };

        var attempt = session.Start(input, CancellationToken.None);
        await _clock.WhenWaitingAsync(Debounce);
        _clock.Advance(Debounce);
        var result = await attempt.Completed.WithTimeout(Timeout);

        var request = _model.Requests.Single();
        Assert.True(request.AllowMultiLine);
        Assert.Empty(request.StopSequences);
        Assert.Equal(AutocompleteEngine.MultiLineMaxTokens, request.MaxTokens);
        Assert.True(result!.IsMultiLine);
        Assert.Equal(MultiLineReason.AfterDeclaration, result.MultiLineReason);
        Assert.Equal(CompletionStopReason.BlockEnd, result.StopReason);
        Assert.Equal("\n    {\n        return numbers.Min();\n    }", result.Text);
        await Wait.UntilAsync(() => _model.Cancelled.Contains(request.RequestId), "the host was told to stop");
    }

    [Fact]
    public async Task MultiLine_IsReadyWithTheFirstLinesWhenTheRestIsSlow()
    {
        _model.Manual = true;
        _model.Respond = _ => ["\n        var x = 1;\n", "        return x;\n"];
        var (_, session) = CreateSession();
        var text = "class C\n{\n    int M()\n    {\n        \n    }\n}\n";
        var caret = text.IndexOf("        \n", StringComparison.Ordinal) + 8;
        var input = new CompletionInput { DocumentKey = "c.cs", FilePath = "c.cs", LanguageId = "csharp", Text = new StringDocumentText(text), Caret = caret };

        var attempt = session.Start(input, CancellationToken.None);
        await _clock.WhenWaitingAsync(Debounce);
        _clock.Advance(Debounce);
        await Wait.UntilAsync(() => _model.Requests.Count == 1, "the request reached the host");
        _model.Release();
        await _clock.WhenWaitingAsync(AutocompleteEngine.PartialGrace);
        _clock.Advance(AutocompleteEngine.PartialGrace);

        var ready = await attempt.Ready.WithTimeout(Timeout);
        Assert.False(ready!.IsComplete);
        Assert.Equal("\n        var x = 1;", ready.Text);
        Assert.False(attempt.Completed.IsCompleted);

        _model.Release();
        var complete = await attempt.Completed.WithTimeout(Timeout);
        Assert.True(complete!.IsComplete);
        Assert.Equal("\n        var x = 1;\n        return x;", complete.Text);
    }

    [Fact]
    public async Task PreviousAcceptAtLineEnd_MakesTheNextRequestMultiLineOnce()
    {
        var (_, session) = CreateSession();
        session.PreviousAcceptedAtLineEnd = true;

        var attempt = session.Start(InputAt("var smallest = "), CancellationToken.None);
        await _clock.WhenWaitingAsync(Debounce);
        _clock.Advance(Debounce);
        var result = await attempt.Completed.WithTimeout(Timeout);

        Assert.Equal(MultiLineReason.AfterAcceptAtLineEnd, result!.MultiLineReason);
        Assert.False(session.PreviousAcceptedAtLineEnd);
    }

    [Fact]
    public async Task Degraded_UsesThe350msDebounceAndSingleLine()
    {
        var (engine, session) = CreateSession();
        for (var i = 0; i < LatencyWindow.MinimumSamples; i++)
        {
            engine.Latency.Record(TimeSpan.FromSeconds(2));
        }

        Assert.True(engine.Latency.IsDegraded);
        var text = "class C\n{\n    public static int Min(int[] numbers)\n}\n";
        var caret = text.IndexOf(")", StringComparison.Ordinal) + 1;
        var input = new CompletionInput { DocumentKey = "c.cs", FilePath = "c.cs", LanguageId = "csharp", Text = new StringDocumentText(text), Caret = caret };

        var attempt = session.Start(input, CancellationToken.None);
        await _clock.WhenWaitingAsync(LatencyWindow.DegradedDebounce);
        _clock.Advance(LatencyWindow.DegradedDebounce);
        var result = await attempt.Completed.WithTimeout(Timeout);

        Assert.Equal(MultiLineReason.Degraded, result!.MultiLineReason);
        Assert.False(_model.Requests.Single().AllowMultiLine);
    }

    [Fact]
    public async Task HostFailures_NeverSurface_AndAreLoggedOncePerMinute()
    {
        var connection = new FakeHostConnection(HostState.Ready); // No IModelService: GetProxy throws PillionRpcException.
        var engine = new AutocompleteEngine(connection, availability: null, _clock, log: null);
        var session = engine.CreateSession();
        var input = InputAt("var smallest = ");
        input.Immediate = true;

        for (var i = 0; i < 5; i++)
        {
            Assert.Null(await session.Start(input, CancellationToken.None).Completed.WithTimeout(Timeout));
        }

        Assert.Equal(1, engine.Failures.Written);
        _clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Null(await session.Start(input, CancellationToken.None).Completed.WithTimeout(Timeout));
        Assert.Equal(2, engine.Failures.Written);
    }

    [Fact]
    public async Task RemoteFailure_EndsWithNoSuggestion()
    {
        _model.Failure = new PillionRpcException(new PillionError { Code = PillionErrorCode.ProviderUnavailable, Message = "down" });
        var (engine, session) = CreateSession();
        var input = InputAt("var smallest = ");
        input.Immediate = true;

        Assert.Null(await session.Start(input, CancellationToken.None).Completed.WithTimeout(Timeout));
        Assert.Equal(1, engine.Failures.Written);
    }

    [Fact]
    public async Task HostNotReady_SkipsTheRequestWithoutLogging()
    {
        var connection = new FakeHostConnection(HostState.Restarting).With<IModelService>(_model);
        var engine = new AutocompleteEngine(connection, availability: null, _clock, log: null);
        var input = InputAt("var smallest = ");
        input.Immediate = true;

        Assert.Null(await engine.CreateSession().Start(input, CancellationToken.None).Completed.WithTimeout(Timeout));
        Assert.Empty(_model.Requests);
        Assert.Equal(0, engine.Failures.Written);
    }

    [Fact]
    public async Task Latency_IsRecordedFromSendToSuggestion()
    {
        _model.Manual = true;
        var (engine, session) = CreateSession();

        var attempt = session.Start(InputAt("var smallest = "), CancellationToken.None);
        await _clock.WhenWaitingAsync(Debounce);
        _clock.Advance(Debounce);
        await Wait.UntilAsync(() => _model.Requests.Count == 1, "the request reached the host");
        _clock.Advance(TimeSpan.FromMilliseconds(420));
        _model.Release();
        await attempt.Completed.WithTimeout(Timeout);

        var snapshot = engine.Latency.Snapshot();
        Assert.Equal(1, snapshot.Count);
        Assert.Equal(TimeSpan.FromMilliseconds(420), snapshot.P50);
    }

    /// <summary>
    /// The keystroke cost of autocomplete on the UI thread: per keystroke the editor records the edit, cancels the
    /// attempt in flight and starts a new one (everything else runs on the thread pool). Measured against a fake host
    /// that streams slowly, with the real clock; the budget is +2 ms at p95 (design section 13).
    /// </summary>
    [Fact]
    public async Task KeystrokeOverhead_IsWithinTwoMillisecondsAtP95()
    {
        _model.ChunkDelay = TimeSpan.FromMilliseconds(30);
        _model.Respond = _ => ["Math", ".Min", "(a, b);"];
        var connection = new FakeHostConnection(HostState.Ready).With<IModelService>(_model);
        var engine = new AutocompleteEngine(connection, availability: null, SystemClock.Instance, log: null);
        var session = engine.CreateSession();
        var baseText = Code;
        const int Keystrokes = 600;
        var samples = new double[Keystrokes];
        var stopwatch = new Stopwatch();

        // Warm up the JIT.
        for (var i = 0; i < 20; i++)
        {
            Keystroke(i);
        }

        for (var i = 0; i < Keystrokes; i++)
        {
            stopwatch.Restart();
            Keystroke(i + 100);
            stopwatch.Stop();
            samples[i] = stopwatch.Elapsed.TotalMilliseconds;
            if (i % 50 == 0)
            {
                await Task.Delay(250); // Let some attempts pass the debounce and stream, as in real typing pauses.
            }
        }

        session.CancelPending();
        Array.Sort(samples);
        var p50 = samples[(int)Math.Ceiling(0.50 * Keystrokes) - 1];
        var p95 = samples[(int)Math.Ceiling(0.95 * Keystrokes) - 1];
        output.WriteLine($"Keystroke overhead over {Keystrokes} keystrokes: p50 {p50:0.000} ms, p95 {p95:0.000} ms, max {samples[Keystrokes - 1]:0.000} ms; {_model.Requests.Count} requests reached the host.");
        Assert.True(p95 < 2.0, $"p95 {p95:0.000} ms exceeds the 2 ms budget");

        void Keystroke(int n)
        {
            var text = baseText.Insert(baseText.IndexOf("= ", StringComparison.Ordinal) + 2, new string('x', n % 40));
            var caret = baseText.IndexOf("= ", StringComparison.Ordinal) + 2 + (n % 40);
            engine.Edits.Record(@"C:\src\Calculator.cs", 4, "        var smallest = " + new string('x', n % 40));
            session.CancelPendingUnless(n, caret: null);
            session.Start(
                new CompletionInput
                {
                    DocumentKey = @"C:\src\Calculator.cs",
                    FilePath = @"C:\src\Calculator.cs",
                    LanguageId = "csharp",
                    Text = new StringDocumentText(text),
                    TextVersion = n,
                    Caret = caret,
                },
                CancellationToken.None);
        }
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
