using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Pillion.VisualStudio.Core;
using Pillion.VisualStudio.Core.Signals;

namespace Pillion.VisualStudio.Tests;

/// <summary>A clock whose delays complete only when the test moves time forward.</summary>
internal sealed class SignalsClock : ISupervisorClock
{
    private readonly object _gate = new();
    private readonly List<(DateTimeOffset Due, TaskCompletionSource<bool> Done)> _delays = [];
    private DateTimeOffset _now = new(2026, 10, 9, 9, 0, 0, TimeSpan.Zero);

    public DateTimeOffset UtcNow
    {
        get
        {
            lock (_gate)
            {
                return _now;
            }
        }
    }

    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        var done = new TaskCompletionSource<bool>();
        lock (_gate)
        {
            _delays.Add((_now + delay, done));
        }

        cancellationToken.Register(() => done.TrySetCanceled(cancellationToken));
        return done.Task;
    }

    /// <summary>Moves time forward and completes the delays that fall due (continuations run on this thread).</summary>
    public void Advance(TimeSpan by)
    {
        List<TaskCompletionSource<bool>> due;
        lock (_gate)
        {
            _now += by;
            due = _delays.Where(d => d.Due <= _now).Select(d => d.Done).ToList();
            _delays.RemoveAll(d => d.Due <= _now);
        }

        foreach (var item in due)
        {
            item.TrySetResult(true);
        }
    }
}

internal sealed class FakeErrorListRow : IErrorListRow
{
    private readonly Dictionary<string, object?> _values = new(StringComparer.Ordinal);

    public FakeErrorListRow Set(string key, object? value)
    {
        _values[key] = value;
        return this;
    }

    public bool TryGetValue(string keyName, out object? value) => _values.TryGetValue(keyName, out value);
}

internal sealed class FakeErrorList(params DiagnosticInfo[] items) : IErrorListSource
{
    public Task<IReadOnlyList<DiagnosticInfo>> ReadAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<DiagnosticInfo>>(items);
}

internal sealed class FakeRoslyn : IRoslynDiagnosticsSource
{
    public string? UnavailableReason { get; set; }

    public IReadOnlyList<DiagnosticInfo>? Result { get; set; }

    public string? AskedFor { get; private set; }

    public Task<IReadOnlyList<DiagnosticInfo>?> ReadAsync(string path, CancellationToken cancellationToken)
    {
        AskedFor = path;
        return Task.FromResult(Result);
    }
}

internal sealed class FakeOutput(string? text) : IOutputPaneSource
{
    public string? AskedFor { get; private set; }

    public Task<string?> ReadTailAsync(string paneName, int maxChars, CancellationToken cancellationToken)
    {
        AskedFor = paneName;
        return Task.FromResult(text);
    }
}

public class SignalsTests
{
    private static DiagnosticInfo Diagnostic(string? file, int line, string code, IdeSeverity severity = IdeSeverity.Error, DiagnosticSource source = DiagnosticSource.ErrorList) =>
        new(file, line, 5, severity, code, "message " + code, "App", source);

    /// <summary>Lets continuations that the manual clock completed run, then waits for the hub's queue to drain.</summary>
    private static async Task SettleAsync(IdeSignalsHub hub)
    {
        await Task.Delay(40);
        await hub.WhenIdleAsync();
    }

    [Fact]
    public async Task Debounce_RaisesDiagnosticsChanged_OnceAfter500MsOfQuiet()
    {
        var clock = new SignalsClock();
        using var hub = new IdeSignalsHub(clock, null);
        var raised = 0;
        hub.DiagnosticsChanged += (_, _) => Interlocked.Increment(ref raised);

        for (var i = 0; i < 5; i++)
        {
            hub.NotifyDiagnosticsMayHaveChanged();
            clock.Advance(TimeSpan.FromMilliseconds(100));
        }

        await SettleAsync(hub);
        Assert.Equal(0, raised);

        clock.Advance(TimeSpan.FromMilliseconds(399));
        await SettleAsync(hub);
        Assert.Equal(0, raised);

        clock.Advance(TimeSpan.FromMilliseconds(1));
        await SettleAsync(hub);
        Assert.Equal(1, raised);

        clock.Advance(TimeSpan.FromSeconds(5));
        await SettleAsync(hub);
        Assert.Equal(1, raised);
    }

    [Fact]
    public async Task Debounce_TwoBurstsFarApart_RaiseTwice()
    {
        var clock = new SignalsClock();
        using var hub = new IdeSignalsHub(clock, null);
        var raised = 0;
        hub.DiagnosticsChanged += (_, _) => Interlocked.Increment(ref raised);

        hub.NotifyDiagnosticsMayHaveChanged();
        clock.Advance(TimeSpan.FromMilliseconds(500));
        await SettleAsync(hub);
        hub.NotifyDiagnosticsMayHaveChanged();
        hub.NotifyDiagnosticsMayHaveChanged();
        clock.Advance(TimeSpan.FromMilliseconds(500));
        await SettleAsync(hub);

        Assert.Equal(2, raised);
    }

    [Fact]
    public async Task Debounce_ACeaselessStream_IsHeldBackNoLongerThanTheMaximumWait()
    {
        var clock = new SignalsClock();
        using var hub = new IdeSignalsHub(clock, null);
        var raised = 0;
        hub.DiagnosticsChanged += (_, _) => Interlocked.Increment(ref raised);

        // A trigger every 400 ms never leaves a 500 ms gap; the event must still come within the 3 s cap.
        var elapsed = TimeSpan.Zero;
        while (elapsed < TimeSpan.FromSeconds(3) && raised == 0)
        {
            hub.NotifyDiagnosticsMayHaveChanged();
            clock.Advance(TimeSpan.FromMilliseconds(400));
            elapsed += TimeSpan.FromMilliseconds(400);
            await SettleAsync(hub);
        }

        Assert.Equal(1, raised);
        Assert.True(elapsed <= IdeSignalsHub.DiagnosticsMaxWait + TimeSpan.FromMilliseconds(400), $"Raised after {elapsed}.");
    }

    [Fact]
    public async Task Events_AreRaisedInTheOrderTheyWereReported_AndNeverInsideTheNotifyCall()
    {
        var clock = new SignalsClock();
        using var hub = new IdeSignalsHub(clock, null);
        using var release = new ManualResetEventSlim();
        var seen = new List<string>();
        void Note(string text)
        {
            lock (seen)
            {
                seen.Add(text);
            }
        }

        // The first listener holds the queue until the test lets it go. If the hub raised events inside the Notify call (on the
        // caller's stack), that call would sit here and the listener would have finished before the assertion below. Comparing
        // thread ids instead is not a valid check: a caller that is a thread-pool thread is free to run the queued work as
        // soon as it yields, so it can legitimately be the thread that raises the events.
        hub.DocumentOpened += (_, e) =>
        {
            _ = release.Wait(TimeSpan.FromSeconds(30));
            Note("opened " + e.Path);
        };
        hub.BuildStarted += (_, _) => Note("started");
        hub.BuildStateChanged += (_, e) => Note("state " + e.IsBuilding);
        hub.BuildFinished += (_, e) => Note($"finished {e.Success} {e.ProjectCount} {e.Duration.TotalSeconds:0}");
        hub.DocumentSaved += (_, e) => Note("saved " + e.Path);
        hub.DocumentClosed += (_, e) => Note("closed " + e.Path);
        hub.ActiveDocumentChanged += (_, e) => Note("active " + (e.Path ?? "none"));

        hub.NotifyDocumentOpened(@"C:\a.cs");
        lock (seen)
        {
            Assert.Empty(seen);
        }

        hub.NotifyActiveDocumentChanged(@"C:\a.cs");
        hub.NotifyBuildStarted();
        hub.NotifyProjectBuildStarted();
        hub.NotifyProjectBuildStarted();
        clock.Advance(TimeSpan.FromSeconds(7));
        hub.NotifyBuildFinished(success: false);
        hub.NotifyDocumentSaved(@"C:\a.cs");
        hub.NotifyDocumentClosed(@"C:\a.cs");
        lock (seen)
        {
            Assert.Empty(seen);
        }

        release.Set();
        await hub.WhenIdleAsync();

        Assert.Equal(
            ["opened C:\\a.cs", "active C:\\a.cs", "started", "state True", "finished False 2 7", "state False", "saved C:\\a.cs", "closed C:\\a.cs"],
            seen);
        Assert.Null(hub.ActiveDocumentPath);
    }

    [Fact]
    public async Task AListenerThatThrows_DoesNotStopOtherListenersOrLaterEvents()
    {
        using var hub = new IdeSignalsHub(new SignalsClock(), null);
        var seen = new List<string>();
        hub.DocumentOpened += (_, _) => throw new InvalidOperationException("boom");
        hub.DocumentOpened += (_, e) => { lock (seen) { seen.Add(e.Path!); } };

        hub.NotifyDocumentOpened(@"C:\a.cs");
        hub.NotifyDocumentOpened(@"C:\b.cs");
        await hub.WhenIdleAsync();

        Assert.Equal([@"C:\a.cs", @"C:\b.cs"], seen);
    }

    [Fact]
    public async Task BuildActivity_FollowsTheBuild_AndStateChangesAreRaisedOncePerFlip()
    {
        var clock = new SignalsClock();
        using var hub = new IdeSignalsHub(clock, null);
        var states = new List<bool>();
        hub.BuildStateChanged += (_, e) => { lock (states) { states.Add(e.IsBuilding); } };

        Assert.False(hub.BuildActivity.IsBuilding);
        hub.NotifyBuildStarted();
        hub.NotifyBuildStarted();
        Assert.True(hub.BuildActivity.IsBuilding);
        Assert.Equal(clock.UtcNow, hub.BuildActivity.StartedAt);
        hub.NotifyBuildFinished(success: true);
        hub.NotifyBuildFinished(success: true);
        Assert.False(hub.BuildActivity.IsBuilding);
        Assert.Null(hub.BuildActivity.StartedAt);
        await hub.WhenIdleAsync();

        Assert.Equal([true, false], states);
    }

    [Fact]
    public async Task ActiveDocumentChanged_IgnoresRepeats()
    {
        using var hub = new IdeSignalsHub(new SignalsClock(), null);
        var count = 0;
        hub.ActiveDocumentChanged += (_, _) => Interlocked.Increment(ref count);

        hub.NotifyActiveDocumentChanged(@"C:\a.cs");
        hub.NotifyActiveDocumentChanged(@"c:\A.cs");
        hub.NotifyActiveDocumentChanged(@"C:\b.cs");
        await hub.WhenIdleAsync();

        Assert.Equal(2, count);
    }

    [Fact]
    public void RecentEvents_KeepOnlyTheLast200_OldestFirst_WithTimestamps()
    {
        var clock = new SignalsClock();
        using var hub = new IdeSignalsHub(clock, null);
        for (var i = 0; i < 250; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(10));
            hub.NotifyDocumentSaved($@"C:\file{i}.cs");
        }

        var events = hub.RecentEvents;

        Assert.Equal(200, events.Count);
        Assert.Equal(@"C:\file50.cs", events[0].Detail);
        Assert.Equal(@"C:\file249.cs", events[events.Count - 1].Detail);
        Assert.All(events, e => Assert.Equal("DocumentSaved", e.Kind));
        Assert.True(events[0].Timestamp < events[events.Count - 1].Timestamp);
        Assert.Contains("DocumentSaved", hub.StatusText);
    }

    [Fact]
    public void OutputTail_ShortText_IsReturnedWhole()
    {
        Assert.Equal("abc", OutputTail.Truncate("abc", 3));
        Assert.Equal(string.Empty, OutputTail.Truncate(string.Empty, 10));
    }

    [Fact]
    public void OutputTail_LongText_KeepsTheEnd_WithAMarker_WithinTheLimit()
    {
        var lines = Enumerable.Range(1, 200).Select(i => $"line {i:000} of the build output").ToArray();
        var text = string.Join("\n", lines);

        var tail = OutputTail.Truncate(text, 500);

        Assert.True(tail.Length <= 500, $"Length {tail.Length}.");
        Assert.StartsWith(OutputTail.Marker + "\n", tail, StringComparison.Ordinal);
        Assert.EndsWith("line 200 of the build output", tail, StringComparison.Ordinal);
        Assert.DoesNotContain("line 001", tail);

        // The first kept line is whole.
        var firstKept = tail.Substring(OutputTail.Marker.Length + 1).Split('\n')[0];
        Assert.Matches(@"^line \d{3} of the build output$", firstKept);
    }

    [Fact]
    public void OutputTail_TinyLimit_ReturnsThePlainTail()
    {
        Assert.Equal("789", OutputTail.Truncate("0123456789", 3));
        Assert.Throws<ArgumentOutOfRangeException>(() => OutputTail.Truncate("abc", 0));
    }

    [Fact]
    public void Mapper_MapsAnErrorListRow_ToADiagnostic()
    {
        var entry = new FakeErrorListRow()
            .Set(TableKeys.DocumentName, @"C:\src\App\Program.cs")
            .Set(TableKeys.Line, 11)
            .Set(TableKeys.Column, 4)
            .Set(TableKeys.ErrorSeverity, 0)
            .Set(TableKeys.ErrorCode, "CS1002")
            .Set(TableKeys.Text, "; expected")
            .Set(TableKeys.ProjectName, "App");

        var diagnostic = ErrorListMapper.Map(entry);

        Assert.NotNull(diagnostic);
        Assert.Equal(@"C:\src\App\Program.cs", diagnostic.File);
        Assert.Equal(12, diagnostic.Line);
        Assert.Equal(5, diagnostic.Column);
        Assert.Equal(IdeSeverity.Error, diagnostic.Severity);
        Assert.Equal("CS1002", diagnostic.Code);
        Assert.Equal("; expected", diagnostic.Message);
        Assert.Equal("App", diagnostic.Project);
        Assert.Equal(DiagnosticSource.ErrorList, diagnostic.Source);
    }

    [Theory]
    [InlineData(0, IdeSeverity.Error)]
    [InlineData(1, IdeSeverity.Warning)]
    [InlineData(2, IdeSeverity.Info)]
    [InlineData(99, IdeSeverity.Info)]
    public void Mapper_MapsTheSeverityCategory(int category, IdeSeverity expected)
    {
        var entry = new FakeErrorListRow().Set(TableKeys.Text, "text").Set(TableKeys.ErrorSeverity, category);

        Assert.Equal(expected, ErrorListMapper.Map(entry)!.Severity);
    }

    [Fact]
    public void Mapper_MissingColumns_GiveUnknownPositionAndNulls()
    {
        var entry = new FakeErrorListRow().Set(TableKeys.Text, "No file here").Set(TableKeys.Line, -1).Set(TableKeys.ProjectName, string.Empty);

        var diagnostic = ErrorListMapper.Map(entry)!;

        Assert.Null(diagnostic.File);
        Assert.Equal(0, diagnostic.Line);
        Assert.Equal(0, diagnostic.Column);
        Assert.Null(diagnostic.Code);
        Assert.Null(diagnostic.Project);
        Assert.Equal(IdeSeverity.Info, diagnostic.Severity);
    }

    [Fact]
    public void Mapper_ARowWithNoTextAndNoCode_IsSkipped() =>
        Assert.Null(ErrorListMapper.Map(new FakeErrorListRow().Set(TableKeys.Line, 3)));

    [Fact]
    public async Task Solution_ReturnsTheErrorListEntries_ErrorsFirst()
    {
        using var hub = new IdeSignalsHub(new SignalsClock(), null)
        {
            ErrorList = new FakeErrorList(
                Diagnostic(@"C:\b.cs", 3, "CS0168", IdeSeverity.Warning),
                Diagnostic(@"C:\b.cs", 1, "CS1002"),
                Diagnostic(@"C:\a.cs", 9, "CS0103")),
        };

        var result = await hub.GetDiagnosticsAsync(DiagnosticScope.Solution, CancellationToken.None);

        Assert.Equal(["CS0103", "CS1002", "CS0168"], result.Select(d => d.Code));
    }

    [Fact]
    public async Task ActiveDocument_MergesRoslynWithTheErrorListEntriesForThatFile_WithoutDuplicates()
    {
        var roslyn = new FakeRoslyn
        {
            Result =
            [
                Diagnostic(@"C:\a.cs", 3, "CS1002", source: DiagnosticSource.Roslyn),
                Diagnostic(@"C:\a.cs", 8, "CS0103", source: DiagnosticSource.Roslyn),
            ],
        };
        using var hub = new IdeSignalsHub(new SignalsClock(), null)
        {
            ErrorList = new FakeErrorList(
                Diagnostic(@"C:\a.cs", 3, "CS1002"),
                Diagnostic(@"c:\A.cs", 20, "IDE0005", IdeSeverity.Info),
                Diagnostic(@"C:\other.cs", 1, "CS9999")),
            Roslyn = roslyn,
        };
        hub.NotifyActiveDocumentChanged(@"C:\a.cs");

        var result = await hub.GetDiagnosticsAsync(DiagnosticScope.ActiveDocument, CancellationToken.None);

        Assert.Equal(@"C:\a.cs", roslyn.AskedFor);
        Assert.Equal(["CS1002", "CS0103", "IDE0005"], result.Select(d => d.Code));
        Assert.Equal(DiagnosticSource.Roslyn, result[0].Source);
        Assert.Equal(DiagnosticSource.ErrorList, result[2].Source);
    }

    [Fact]
    public async Task ActiveDocument_WithRoslynUnavailable_UsesTheErrorListOnly_AndTheNoteSaysSo()
    {
        var roslyn = new FakeRoslyn { UnavailableReason = "the Visual Studio workspace is not loaded", Result = [Diagnostic(@"C:\a.cs", 1, "SHOULD_NOT_APPEAR")] };
        using var hub = new IdeSignalsHub(new SignalsClock(), null)
        {
            ErrorList = new FakeErrorList(Diagnostic(@"C:\a.cs", 3, "CS1002")),
            Roslyn = roslyn,
        };
        hub.NotifyActiveDocumentChanged(@"C:\a.cs");

        var result = await hub.GetDiagnosticsAsync(DiagnosticScope.ActiveDocument, CancellationToken.None);

        Assert.Equal(["CS1002"], result.Select(d => d.Code));
        Assert.Null(roslyn.AskedFor);
        Assert.Contains("the Visual Studio workspace is not loaded", hub.DiagnosticsNote);
        Assert.Contains("filters", hub.DiagnosticsNote);
    }

    [Fact]
    public async Task ActiveDocument_WithNoActiveDocument_IsEmpty()
    {
        using var hub = new IdeSignalsHub(new SignalsClock(), null) { ErrorList = new FakeErrorList(Diagnostic(@"C:\a.cs", 3, "CS1002")) };

        Assert.Empty(await hub.GetDiagnosticsAsync(DiagnosticScope.ActiveDocument, CancellationToken.None));
    }

    [Fact]
    public async Task Output_IsTailTruncatedToTheRequestedLength_AndMissingPanesAreNull()
    {
        var text = string.Join("\n", Enumerable.Range(1, 500).Select(i => $"row {i}"));
        var output = new FakeOutput(text);
        using var hub = new IdeSignalsHub(new SignalsClock(), null) { Output = output };

        var tail = await hub.GetOutputPaneTextAsync(" Build ", 200, CancellationToken.None);

        Assert.Equal("Build", output.AskedFor);
        Assert.NotNull(tail);
        Assert.True(tail.Length <= 200);
        Assert.EndsWith("row 500", tail, StringComparison.Ordinal);

        hub.Output = new FakeOutput(null);
        Assert.Null(await hub.GetOutputPaneTextAsync("Tests", 200, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => hub.GetOutputPaneTextAsync("Build", 0, CancellationToken.None));
    }

    /// <summary>Issue #49: the Error List is connected by the first diagnostics query, once, and not before.</summary>
    [Fact]
    public async Task ErrorList_IsConnectedByTheFirstDiagnosticsQuery_Once()
    {
        var connections = 0;
        using var hub = new IdeSignalsHub(new SignalsClock(), null);
        hub.ErrorListConnector = _ =>
        {
            connections++;
            hub.ErrorList = new FakeErrorList(Diagnostic(@"C:\a.cs", 3, "CS1002"));
            return Task.CompletedTask;
        };
        Assert.StartsWith("The Error List is not connected yet", hub.DiagnosticsNote, StringComparison.Ordinal);
        hub.NotifyBuildFinished(success: false);
        await hub.WhenIdleAsync();
        Assert.Equal(0, connections); // A build does not connect it; a query does.

        var first = await hub.GetDiagnosticsAsync(DiagnosticScope.Solution, CancellationToken.None);
        var second = await hub.GetDiagnosticsAsync(DiagnosticScope.Solution, CancellationToken.None);

        Assert.Equal(["CS1002"], first.Select(d => d.Code));
        Assert.Equal(["CS1002"], second.Select(d => d.Code));
        Assert.Equal(1, connections);
        Assert.Contains("filters", hub.DiagnosticsNote);
    }

    /// <summary>Issue #49: the explicit path phase 3's self-healing uses before it subscribes to DiagnosticsChanged.</summary>
    [Fact]
    public async Task EnsureDiagnosticsConnected_ConnectsWithoutAQuery_SharesOneRun_AndRetriesAfterAFailure()
    {
        var attempts = 0;
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var hub = new IdeSignalsHub(new SignalsClock(), null);
        hub.ErrorListConnector = async _ =>
        {
            attempts++;
            await release.Task;
            if (attempts == 1)
            {
                throw new InvalidOperationException("the Error List service was not available");
            }

            hub.ErrorList = new FakeErrorList();
        };

        var a = hub.EnsureDiagnosticsConnectedAsync(CancellationToken.None);
        var b = hub.EnsureDiagnosticsConnectedAsync(CancellationToken.None);
        release.SetResult(true);
        await Task.WhenAll(a, b); // A failed connection is logged, not thrown; both waiters shared the one run.
        Assert.Equal(1, attempts);
        Assert.Null(hub.ErrorList);

        await hub.EnsureDiagnosticsConnectedAsync(CancellationToken.None); // The next call tries again.
        Assert.Equal(2, attempts);
        Assert.NotNull(hub.ErrorList);
        await hub.EnsureDiagnosticsConnectedAsync(CancellationToken.None);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task EnsureDiagnosticsConnected_WithoutAConnector_DoesNothing()
    {
        using var hub = new IdeSignalsHub(new SignalsClock(), null);

        await hub.EnsureDiagnosticsConnectedAsync(CancellationToken.None);

        Assert.Null(hub.ErrorList);
        Assert.StartsWith("The Error List is not available yet.", hub.DiagnosticsNote, StringComparison.Ordinal);
    }

    /// <summary>Issue #49: the Output window is connected by the first pane query; a finished build does not connect it.</summary>
    [Fact]
    public async Task OutputPanes_AreConnectedByTheFirstPaneQuery_NotByABuild()
    {
        var connections = 0;
        using var hub = new IdeSignalsHub(new SignalsClock(), null);
        hub.OutputConnector = _ =>
        {
            connections++;
            hub.Output = new FakeOutput("1>Build failed.");
            return Task.CompletedTask;
        };

        hub.NotifyBuildStarted();
        hub.NotifyBuildFinished(success: false);
        await hub.WhenIdleAsync();
        await Task.Delay(50);
        Assert.Equal(0, connections);
        Assert.Null(hub.LastBuild!.Output); // The tail is captured only once the Output window is connected.

        Assert.Equal("1>Build failed.", await hub.GetOutputPaneTextAsync("Build", 100, CancellationToken.None));
        Assert.Equal("1>Build failed.", await hub.GetOutputPaneTextAsync("Build", 100, CancellationToken.None));
        Assert.Equal(1, connections);
    }

    [Fact]
    public void DisconnectSteps_RunNewestFirst_OnlyOnce_AndAFailingStepDoesNotStopTheOthers()
    {
        var order = new List<string>();
        var steps = new DisconnectSteps();
        steps.Add("first", () => order.Add("first"));
        steps.Add("broken", DereferenceNull); // What the Error List table control did at shutdown.
        steps.Add("last", () => order.Add("last"));

        var failed = steps.RunAll(log: null);
        var again = steps.RunAll(log: null);

        Assert.Equal(1, failed);
        Assert.Equal(0, again);
        Assert.Equal(["last", "first"], order);
        Assert.Equal(0, steps.Count);
    }

    [Fact]
    public void DisconnectSteps_LogTheFailedStepByName_WithoutThrowing()
    {
        var lines = new List<string>();
        var log = new PillionLog(directory: null) { Sink = line => { lock (lines) { lines.Add(line); } } };
        var steps = new DisconnectSteps();
        steps.Add("the Error List", DereferenceNull);

        Assert.Equal(1, steps.RunAll(log));

        var line = Assert.Single(lines);
        Assert.Contains("signals.dispose.failed", line, StringComparison.Ordinal);
        Assert.Contains("the Error List", line, StringComparison.Ordinal);
        Assert.Contains("NullReferenceException", line, StringComparison.Ordinal);
    }

    // A real NullReferenceException, as thrown when Visual Studio has already torn the object down.
    private static void DereferenceNull()
    {
        string? text = null;
        _ = text!.Length;
    }
}
