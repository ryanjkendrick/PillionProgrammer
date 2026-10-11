using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;
using Pillion.Contracts;
using Pillion.VisualStudio.Core.Autocomplete;

namespace Pillion.VisualStudio.Tests;

/// <summary>
/// When a suggestion counts as shown (decision of 2026-10-11, Gate 1 open issue 4): a single-line suggestion is shown
/// at its first chunk with visible text and grows as chunks stream; a multi-line one is shown at its first complete
/// line and the rest streams in; at most one update per 50 ms; "time to suggestion" is the first visible text. Also
/// the availability gate on every request (Gate 1 open issue 3) and the fence guard (open issue 2).
/// </summary>
public sealed class AutocompleteDisplayTests
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

    private const string Body = "class C\n{\n    int M()\n    {\n        \n    }\n}\n";

    private readonly ManualClock _clock = new();
    private readonly FakeCompletionModel _model = new() { Manual = true };

    [Fact]
    public async Task SingleLine_IsShownAtItsFirstChunk_WithoutAGracePeriod()
    {
        _model.Respond = _ => ["numbers", ".Min", "();"];
        var (engine, session) = CreateSession();

        var attempt = await StartAndSendAsync(session, InputAt("var smallest = "));
        _clock.Advance(TimeSpan.FromMilliseconds(120));
        _model.Release();

        var ready = await attempt.Ready.WithTimeout(Timeout);
        Assert.NotNull(ready);
        Assert.False(ready!.IsComplete);
        Assert.Equal("numbers", ready.Text);
        Assert.Equal(TimeSpan.FromMilliseconds(120), ready.Latency);
        Assert.False(attempt.Completed.IsCompleted);
        Assert.Empty(_clock.Pending); // nothing waits on a grace period

        // Time to suggestion is the first visible text, the same as the first chunk here.
        var stats = engine.Metrics.Snapshot().Recent;
        Assert.Equal(TimeSpan.FromMilliseconds(120), stats.TimeToSuggestion.P50);
        Assert.Equal(TimeSpan.FromMilliseconds(120), engine.Latency.Snapshot().P50);

        _model.Release(2);
        var complete = await attempt.Completed.WithTimeout(Timeout);
        Assert.Equal("numbers.Min();", complete!.Text);
        Assert.True(complete.IsComplete);
    }

    [Fact]
    public async Task SingleLine_Grows_AtMostOnceEvery50ms()
    {
        _model.Respond = _ => ["numbers", ".Min", "(", ")", ";"];
        var (_, session) = CreateSession();
        var updates = new ConcurrentQueue<string>();

        var attempt = await StartAndSendAsync(session, InputAt("var smallest = "));
        attempt.Updated += (_, update) => updates.Enqueue(update.Text);
        _model.Release();
        Assert.Equal("numbers", (await attempt.Ready.WithTimeout(Timeout))!.Text);

        // 10 ms later: too soon for an update; one is scheduled for the end of the interval.
        _clock.Advance(TimeSpan.FromMilliseconds(10));
        _model.Release();
        await _clock.WhenWaitingAsync(TimeSpan.FromMilliseconds(40));
        Assert.Empty(updates);

        // More chunks inside the same interval are folded into that one update.
        _model.Release();
        await Wait.UntilAsync(() => attempt.Latest is not null, "the chunk was taken");
        await Task.Delay(50);
        Assert.Single(_clock.Pending);
        Assert.Empty(updates);

        _clock.Advance(TimeSpan.FromMilliseconds(40));
        await Wait.UntilAsync(() => updates.Count == 1, "one update after 50 ms");
        Assert.Equal("numbers.Min(", updates.Single());

        _model.Release(2);
        var complete = await attempt.Completed.WithTimeout(Timeout);
        Assert.Equal("numbers.Min();", complete!.Text);
        await Task.Delay(50);
        Assert.Single(updates); // no update after the whole suggestion
    }

    [Fact]
    public async Task SingleLine_WhitespaceAlone_IsNotShown()
    {
        _model.Respond = _ => [" ", "numbers.Min();"];
        var (_, session) = CreateSession();

        var attempt = await StartAndSendAsync(session, InputAt("var smallest = "));
        _model.Release();
        await Task.Delay(100);
        Assert.False(attempt.Ready.IsCompleted);

        _model.Release();
        Assert.Equal(" numbers.Min();", (await attempt.Ready.WithTimeout(Timeout))!.Text);
    }

    [Fact]
    public async Task SingleLine_HoldsATailThatCouldRepeatTheTextRightOfTheCaret()
    {
        // The caret is inside "Max(|);": a streamed ")" might be the start of the ");" already there.
        const string Text = "class C\n{\n    int M(int a, int b) => Math.Max();\n}\n";
        _model.Respond = _ => ["a, b)", ";"];
        var (_, session) = CreateSession();
        var input = new CompletionInput { DocumentKey = "c.cs", FilePath = "c.cs", LanguageId = "csharp", Text = new StringDocumentText(Text), Caret = Text.IndexOf("Max(", StringComparison.Ordinal) + 4 };

        var attempt = await StartAndSendAsync(session, input);
        _model.Release();

        Assert.Equal("a, b", (await attempt.Ready.WithTimeout(Timeout))!.Text);
        _model.Release();
        Assert.Equal("a, b", (await attempt.Completed.WithTimeout(Timeout))!.Text);
    }

    [Fact]
    public async Task MultiLine_IsShownAtItsFirstCompleteLine_AndTheRestStreamsIn()
    {
        _model.Respond = _ => ["\n        var x", " = 1;\n", "        var y = 2;\n", "        return x + y;\n"];
        var (engine, session) = CreateSession();
        var updates = new ConcurrentQueue<string>();
        var input = new CompletionInput { DocumentKey = "c.cs", FilePath = "c.cs", LanguageId = "csharp", Text = new StringDocumentText(Body), Caret = Body.IndexOf("        \n", StringComparison.Ordinal) + 8 };

        var attempt = await StartAndSendAsync(session, input);
        attempt.Updated += (_, update) => updates.Enqueue(update.Text);
        Assert.True(_model.Requests.Single().AllowMultiLine);

        // Half a line: nothing to show yet.
        _clock.Advance(TimeSpan.FromMilliseconds(80));
        _model.Release();
        await Task.Delay(100);
        Assert.False(attempt.Ready.IsCompleted);

        // The first line completes: shown at once.
        _clock.Advance(TimeSpan.FromMilliseconds(40));
        _model.Release();
        var ready = await attempt.Ready.WithTimeout(Timeout);
        Assert.Equal("\n        var x = 1;", ready!.Text);
        Assert.False(ready.IsComplete);
        Assert.Equal(TimeSpan.FromMilliseconds(120), engine.Latency.Snapshot().P50);

        // The next line, 50 ms later, is an update.
        _clock.Advance(TimeSpan.FromMilliseconds(50));
        _model.Release();
        await Wait.UntilAsync(() => updates.Count == 1, "the second line streamed in");
        Assert.Equal("\n        var x = 1;\n        var y = 2;", updates.Single());

        _model.Release();
        var complete = await attempt.Completed.WithTimeout(Timeout);
        Assert.Equal("\n        var x = 1;\n        var y = 2;\n        return x + y;", complete!.Text);
        Assert.Equal(complete.Text, attempt.Latest!.Text);
    }

    [Fact]
    public async Task MultiLine_WhatIsShownHasPassedTheStopRules()
    {
        // The block ends after "}"; the next member never shows, not even for a moment.
        _model.Respond = _ => ["\n    {\n", "        return numbers.Min();\n", "    }\n", "\n    public int Other()\n"];
        var (_, session) = CreateSession();
        var shown = new ConcurrentQueue<string>();
        var text = "class C\n{\n    public static int Min(int[] numbers)\n}\n";
        var input = new CompletionInput { DocumentKey = "c.cs", FilePath = "c.cs", LanguageId = "csharp", Text = new StringDocumentText(text), Caret = text.IndexOf(")", StringComparison.Ordinal) + 1 };

        var attempt = await StartAndSendAsync(session, input);
        attempt.Updated += (_, update) => shown.Enqueue(update.Text);
        for (var i = 0; i < 4; i++)
        {
            _clock.Advance(TimeSpan.FromMilliseconds(60));
            _model.Release();
            await Task.Delay(30);
        }

        var complete = await attempt.Completed.WithTimeout(Timeout);
        var first = await attempt.Ready.WithTimeout(Timeout);
        Assert.Equal("\n    {\n        return numbers.Min();\n    }", complete!.Text);
        Assert.DoesNotContain(shown.Concat([first!.Text]), t => t.IndexOf("Other", StringComparison.Ordinal) >= 0);
    }

    [Fact]
    public async Task SingleLineFence_IsNeverShown()
    {
        // The 7B's Gate 1 ghost text.
        _model.Respond = _ => ["``", "`csharp", "\n"];
        var (engine, session) = CreateSession();

        var attempt = await StartAndSendAsync(session, InputAt("var smallest = "));
        _model.Release(3);

        Assert.Null(await attempt.Completed.WithTimeout(Timeout));
        Assert.Null(await attempt.Ready.WithTimeout(Timeout));
        Assert.Equal(1, engine.Metrics.Snapshot().Recent.Empty);
    }

    [Fact]
    public async Task MultiLineFence_IsSkippedAtTheStart_AndEndsTheSuggestionAfterContent()
    {
        _model.Respond = _ => ["```csharp\n", "        var x = 1;\n", "```\n", "It looks like you're done.\n"];
        var (_, session) = CreateSession();
        var input = new CompletionInput { DocumentKey = "c.cs", FilePath = "c.cs", LanguageId = "csharp", Text = new StringDocumentText(Body), Caret = Body.IndexOf("        \n", StringComparison.Ordinal) + 8 };

        var attempt = await StartAndSendAsync(session, input);
        _model.Release(4);

        var complete = await attempt.Completed.WithTimeout(Timeout);
        Assert.Equal("        var x = 1;", complete!.Text);
        Assert.Equal(CompletionStopReason.Fence, complete.StopReason);
    }

    [Theory]
    [InlineData(false, null)]
    [InlineData(true, "csharp")]
    public async Task Gate_FeatureOffOrLanguageOff_NoRequestOnAnyPath(bool enabled, string? disabledLanguage)
    {
        var connection = new FakeHostConnection(HostState.Ready).With<IModelService>(_model);
        using var availability = new AutocompleteAvailability(connection);
        availability.Apply(new AutocompleteSettings(enabled, disabledLanguage is null ? [] : [disabledLanguage], announceSuggestions: true));
        var engine = new AutocompleteEngine(connection, availability, _clock, log: null);
        var session = engine.CreateSession();
        Assert.NotEqual(SuppressionReason.None, engine.Gate("csharp"));

        // Every way the extension starts an attempt: the broker's debounced request, the request after an accept or
        // "Pillion: Suggest" (immediate), a forced multi-line one, and the coexistence path's request outside the broker
        // (the same Start with no broker token).
        CompletionInput[] inputs =
        [
            InputAt("var smallest = "),
            With(InputAt("var smallest = "), immediate: true),
            With(InputAt("var smallest = "), immediate: true, forceMultiLine: true),
        ];
        foreach (var input in inputs)
        {
            using var broker = new CancellationTokenSource();
            Assert.Null(await session.Start(input, broker.Token).Completed.WithTimeout(Timeout));
            Assert.Null(await session.Start(input, CancellationToken.None).Completed.WithTimeout(Timeout));
        }

        Assert.Empty(_clock.Pending); // not even a debounce was started
        await Task.Delay(100);
        Assert.Empty(_model.Requests);
        Assert.Equal(6, engine.Metrics.Snapshot().Recent.Suppressed);
        Assert.Equal(0, engine.Metrics.Snapshot().Recent.Requests);
    }

    [Fact]
    public async Task Gate_OtherLanguages_StillRequest_WhenOneIsOff()
    {
        var connection = new FakeHostConnection(HostState.Ready).With<IModelService>(_model);
        using var availability = new AutocompleteAvailability(connection);
        availability.Apply(new AutocompleteSettings(true, ["python"], announceSuggestions: true));
        var engine = new AutocompleteEngine(connection, availability, _clock, log: null);
        _model.Manual = false;

        var result = await engine.CreateSession().Start(With(InputAt("var smallest = "), immediate: true), CancellationToken.None).Completed.WithTimeout(Timeout);

        Assert.NotNull(result);
        Assert.Single(_model.Requests);
        Assert.Equal(SuppressionReason.LanguageDisabled, engine.Gate("python"));
    }

    [Fact]
    public async Task Gate_FeatureSwitchedOffDuringTheDebounce_SendsNothing()
    {
        var connection = new FakeHostConnection(HostState.Ready).With<IModelService>(_model);
        using var availability = new AutocompleteAvailability(connection);
        var engine = new AutocompleteEngine(connection, availability, _clock, log: null);
        var session = engine.CreateSession();

        var attempt = session.Start(InputAt("var smallest = "), CancellationToken.None);
        await _clock.WhenWaitingAsync(Debounce);
        availability.Apply(new AutocompleteSettings(enabled: false, [], announceSuggestions: true));
        _clock.Advance(Debounce);

        Assert.Null(await attempt.Completed.WithTimeout(Timeout));
        await Task.Delay(50);
        Assert.Empty(_model.Requests);
    }

    [Fact]
    public async Task Gate_ShutWhileAnAttemptIsInFlight_CancelsIt()
    {
        var connection = new FakeHostConnection(HostState.Ready).With<IModelService>(_model);
        using var availability = new AutocompleteAvailability(connection);
        var engine = new AutocompleteEngine(connection, availability, _clock, log: null);
        var session = engine.CreateSession();
        var first = await StartAndSendAsync(session, InputAt("var smallest = "));

        availability.Apply(new AutocompleteSettings(enabled: false, [], announceSuggestions: true));
        var second = session.Start(InputAt("var smallest = "), CancellationToken.None);

        Assert.Null(await second.Completed.WithTimeout(Timeout));
        Assert.Null(await first.Completed.WithTimeout(Timeout));
        Assert.Single(_model.Requests);
    }

    [Fact]
    public async Task CancelPendingExcept_KeepsTheGivenAttempt()
    {
        var (_, session) = CreateSession();
        var attempt = await StartAndSendAsync(session, InputAt("var smallest = "));

        session.CancelPendingExcept(attempt.Id);
        Assert.False(attempt.IsCancelled);

        session.CancelPendingExcept(attempt.Id + 1);
        Assert.True(attempt.IsCancelled);
    }

    private static CompletionInput With(CompletionInput input, bool immediate = false, bool forceMultiLine = false)
    {
        input.Immediate = immediate;
        input.ForceMultiLine = forceMultiLine;
        return input;
    }

    private static CompletionInput InputAt(string after) => new()
    {
        DocumentKey = @"C:\src\Calculator.cs",
        FilePath = @"C:\src\Calculator.cs",
        LanguageId = "csharp",
        Text = new StringDocumentText(Code),
        Caret = Code.IndexOf(after, StringComparison.Ordinal) + after.Length,
    };

    /// <summary>Starts an attempt, waits out the debounce and returns once the request reached the fake host.</summary>
    private async Task<CompletionAttempt> StartAndSendAsync(AutocompleteSession session, CompletionInput input)
    {
        var before = _model.Requests.Count;
        var attempt = session.Start(input, CancellationToken.None);
        await _clock.WhenWaitingAsync(Debounce);
        _clock.Advance(Debounce);
        await Wait.UntilAsync(() => _model.Requests.Count == before + 1, "the request reached the host");
        return attempt;
    }

    private (AutocompleteEngine Engine, AutocompleteSession Session) CreateSession()
    {
        var connection = new FakeHostConnection(HostState.Ready).With<IModelService>(_model);
        var engine = new AutocompleteEngine(connection, availability: null, _clock, log: null);
        return (engine, engine.CreateSession());
    }
}
