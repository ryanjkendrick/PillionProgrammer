using System;
using System.Linq;
using System.Threading;
using Pillion.VisualStudio.Core;

namespace Pillion.VisualStudio.Tests;

/// <summary>The UI thread timings debug switch (one class: the switch is process-wide, and xunit runs a class's tests one at a time).</summary>
public sealed class UiThreadTimingsTests : IDisposable
{
    private readonly bool _wasEnabled = UiThreadTimings.Enabled;

    public UiThreadTimingsTests()
    {
        UiThreadTimings.Clear();
    }

    public void Dispose()
    {
        UiThreadTimings.Enabled = _wasEnabled;
        UiThreadTimings.Clear();
    }

    [Fact]
    public void Off_RecordsNothing()
    {
        UiThreadTimings.Enabled = false;

        using (UiThreadTimings.Measure("package.statusbar"))
        {
        }

        Assert.Empty(UiThreadTimings.Snapshot());
    }

    [Fact]
    public void On_RecordsEachMeasurement_WithItsNameAndDuration()
    {
        UiThreadTimings.Enabled = true;

        using (UiThreadTimings.Measure("output.flush"))
        {
            Thread.Sleep(20);
        }

        using (UiThreadTimings.Measure("package.statusbar"))
        {
        }

        var timings = UiThreadTimings.Snapshot();
        Assert.Equal(["output.flush", "package.statusbar"], timings.Select(t => t.Name));
        Assert.True(timings[0].Duration >= TimeSpan.FromMilliseconds(15), $"output.flush took {timings[0].Duration.TotalMilliseconds} ms");
        Assert.True(timings[1].StartTimestamp >= timings[0].EndTimestamp);
    }

    [Fact]
    public void KeepsOnlyTheMostRecentCapacityTimings()
    {
        UiThreadTimings.Enabled = true;

        for (var i = 0; i < UiThreadTimings.Capacity + 5; i++)
        {
            using (UiThreadTimings.Measure("hop" + i))
            {
            }
        }

        var timings = UiThreadTimings.Snapshot();
        Assert.Equal(UiThreadTimings.Capacity, timings.Count);
        Assert.Equal("hop5", timings[0].Name);
    }

    [Fact]
    public void Clear_ForgetsEverything()
    {
        UiThreadTimings.Enabled = true;
        using (UiThreadTimings.Measure("intellicode.check"))
        {
        }

        UiThreadTimings.Clear();

        Assert.Empty(UiThreadTimings.Snapshot());
    }
}
