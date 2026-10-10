using System;
using System.Windows.Threading;
using Pillion.VisualStudio.IntegrationTests;

namespace Pillion.VisualStudio.Tests;

/// <summary>
/// The host kill integration test's verdict and its attribution of a UI-thread gap (the sources are compiled in from
/// tests/Pillion.VisualStudio.IntegrationTests, which only runs inside Visual Studio).
/// </summary>
public class HostKillVerdictTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(100);

    [Fact]
    public void UiOnlyStall_HeldByPillionForMoreThan500Ms_Fails()
    {
        var verdict = ResponsivenessVerdict.Judge(Ms(2225), Ms(274), Budget, Ms(600));

        Assert.NotNull(verdict.Failure);
        Assert.Contains("Pillion's code held the UI thread for 600 ms", verdict.Failure, StringComparison.Ordinal);
        Assert.Null(verdict.Warning);
    }

    [Fact]
    public void UiOnlyStall_HeldByOtherCode_PassesWithAWarning()
    {
        var verdict = ResponsivenessVerdict.Judge(Ms(2225), Ms(274), Budget, Ms(40));

        Assert.Null(verdict.Failure);
        Assert.NotNull(verdict.Warning);
        Assert.StartsWith("WARNING:", verdict.Warning, StringComparison.Ordinal);
        Assert.Contains("only 40 ms", verdict.Warning, StringComparison.Ordinal);
    }

    [Fact]
    public void UiOnlyStall_NotAttributed_Fails()
    {
        var verdict = ResponsivenessVerdict.Judge(Ms(2225), Ms(274), Budget, pillionShare: null);

        Assert.NotNull(verdict.Failure);
        Assert.Null(verdict.Warning);
    }

    [Fact]
    public void UiStallLongerThanBackgroundByTheBudget_HeldByOtherCode_PassesWithAWarning()
    {
        var verdict = ResponsivenessVerdict.Judge(Ms(3000), Ms(900), Budget, Ms(0));

        Assert.Null(verdict.Failure);
        Assert.NotNull(verdict.Warning);
    }

    [Fact]
    public void StallOverTheHardLimit_HeldByPillionOrNotAttributed_Fails()
    {
        Assert.NotNull(ResponsivenessVerdict.Judge(Ms(5100), Ms(250), Budget, Ms(600)).Failure);
        Assert.NotNull(ResponsivenessVerdict.Judge(Ms(5100), Ms(250), Budget, pillionShare: null).Failure);
        Assert.NotNull(ResponsivenessVerdict.Judge(Ms(5100), Ms(4000), Budget, pillionShare: null).Failure);
    }

    [Fact]
    public void StallOverTheHardLimit_HeldByOtherCode_PassesWithAWarning()
    {
        var verdict = ResponsivenessVerdict.Judge(Ms(5100), Ms(250), Budget, Ms(0));

        Assert.Null(verdict.Failure);
        Assert.StartsWith("WARNING:", verdict.Warning, StringComparison.Ordinal);
    }

    [Fact]
    public void MachineStall_WithoutAttribution_PassesWithTheMachineStallWarning()
    {
        var verdict = ResponsivenessVerdict.Judge(Ms(2300), Ms(1900), Budget, pillionShare: null);

        Assert.Null(verdict.Failure);
        Assert.Contains("the machine stalled", verdict.Warning, StringComparison.Ordinal);
    }

    [Fact]
    public void GapUnderTheBudget_Passes()
    {
        var verdict = ResponsivenessVerdict.Judge(Ms(880), Ms(264), Budget, Ms(0));

        Assert.Null(verdict.Failure);
        Assert.Null(verdict.Warning);
    }

    [Fact]
    public void PillionTime_CountsPillionOperationsWithinTheInterval_AndNotTheTestsOwn()
    {
        var result = new UiThreadMonitorResult(
            [
                Operation(1000, 300, "Pillion.VisualStudio.PillionPackage+<RefreshStatusBarAsync>d__42"),
                Operation(1900, 400, "Pillion.VisualStudio.OutputPaneWriter+<FlushAsync>d__6"), // Half inside the interval.
                Operation(1400, 500, "Pillion.VisualStudio.IntegrationTests.DispatcherProbe+<>c__DisplayClass2_1.<ProbeAsync>b__1"),
                Operation(1300, 100, "Microsoft.VisualStudio.Editor.Implementation.Something"),
            ],
            [],
            Interval);

        Assert.Equal(Ms(500), result.PillionTimeWithin(Ms(1000), Ms(2100)));
    }

    [Fact]
    public void PillionTime_AddsOneSampleIntervalPerSampleWithAPillionFrame_OutsidePillionOperations()
    {
        var result = new UiThreadMonitorResult(
            [Operation(1000, 1000, "Microsoft.VisualStudio.Shell.Something")],
            [
                Sample(1300, "System.Threading.Monitor.Enter", "Pillion.VisualStudio.Core.HostSupervisor.get_State"),
                Sample(1400, "System.Threading.Monitor.Enter", "Pillion.VisualStudio.Core.HostSupervisor.get_State"),
                Sample(1500, "Microsoft.VisualStudio.Shell.Something"),
                Sample(1600, "Pillion.VisualStudio.IntegrationTests.UiThreadMonitor.Sample"),
            ],
            Interval);

        Assert.Equal(Ms(200), result.PillionTimeWithin(Ms(1000), Ms(2000)));
    }

    [Fact]
    public void PillionTime_DoesNotCountASampleInsideAPillionOperationTwice()
    {
        var result = new UiThreadMonitorResult(
            [Operation(1000, 800, "Pillion.VisualStudio.PillionPackage+<RefreshStatusBarAsync>d__42")],
            [Sample(1300, "Pillion.VisualStudio.PillionPackage+<RefreshStatusBarAsync>d__42.MoveNext")],
            Interval);

        Assert.Equal(Ms(800), result.PillionTimeWithin(Ms(1000), Ms(1800)));
    }

    [Fact]
    public void PillionTime_IsUnknown_WhenASampleInTheIntervalFailed()
    {
        var result = new UiThreadMonitorResult(
            [Operation(1000, 2000, "Microsoft.VisualStudio.Shell.Something")],
            [Sample(1500, UiThreadMonitor.FailedSamplePrefix + ": ThreadStateException: no)")],
            Interval);

        Assert.Null(result.PillionTimeWithin(Ms(1000), Ms(3000)));
    }

    [Fact]
    public void PillionTime_IsUnknown_WhenTheMonitorSawLessThanHalfOfTheInterval()
    {
        // A 2 s gap with a single 100 ms sample and no operations: "no Pillion frame seen" must not read as "not Pillion".
        var result = new UiThreadMonitorResult([], [Sample(1500, "Microsoft.VisualStudio.Shell.Something")], Interval);

        Assert.Null(result.PillionTimeWithin(Ms(1000), Ms(3000)));
    }

    [Fact]
    public void PillionTime_IsZero_ForAStallSampledThroughoutInOtherCode()
    {
        var samples = new UiStackSample[18];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = Sample(1200 + (i * 100), "System.Threading.WaitHandle.WaitOne", "Microsoft.VisualStudio.Shell.Something");
        }

        var result = new UiThreadMonitorResult([], samples, Interval);

        Assert.Equal(TimeSpan.Zero, result.PillionTimeWithin(Ms(1000), Ms(3000)));
    }

    [Fact]
    public void PillionTime_CountsThePillionHopsTheProductTimed_OnceWhereTheyOverlapOtherEvidence()
    {
        var result = new UiThreadMonitorResult(
            [Operation(1000, 1000, "Microsoft.VisualStudio.Services.TaskSchedulerService.VsUIBackgroundPriorityScheduler.<OnTaskQueued>b__10_0")],
            [Sample(1500, "Pillion.VisualStudio.OutputPaneWriter+<FlushAsync>d__6.MoveNext")],
            Interval,
            [Hop(1300, 300, "output.flush"), Hop(1700, 50, "package.statusbar")]);

        // The hop 1300-1600 already contains the sample's 1400-1500, so 300 + 50 ms.
        Assert.Equal(Ms(350), result.PillionTimeWithin(Ms(1000), Ms(2000)));
    }

    [Fact]
    public void PillionTime_TreatsAStretchInsideOneLongOperationAsWatched()
    {
        var result = new UiThreadMonitorResult([Operation(900, 2200, "Microsoft.VisualStudio.Shell.Something")], [], Interval);

        Assert.Equal(TimeSpan.Zero, result.PillionTimeWithin(Ms(1000), Ms(3000)));
    }

    [Fact]
    public void PillionTime_IsUnknown_WhenNothingWasSeenForMoreThan400Ms()
    {
        var result = new UiThreadMonitorResult(
            [Operation(1000, 10, "Microsoft.VisualStudio.Shell.Something"), Operation(1500, 10, "Microsoft.VisualStudio.Shell.Something")],
            [],
            Interval);

        Assert.Null(result.PillionTimeWithin(Ms(1000), Ms(1600)));
    }

    private static TimeSpan Ms(double milliseconds) => TimeSpan.FromMilliseconds(milliseconds);

    private static UiOperationTiming Hop(double start, double duration, string name) =>
        new(Ms(start), Ms(duration), DispatcherPriority.Invalid, "Pillion.VisualStudio UI work: " + name);

    private static UiOperationTiming Operation(double start, double duration, string description) =>
        new(Ms(start), Ms(duration), DispatcherPriority.Normal, description);

    private static UiStackSample Sample(double at, params string[] frames) => new(Ms(at), frames);
}
