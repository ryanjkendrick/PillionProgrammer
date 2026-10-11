using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell.Interop;
using Xunit.Abstractions;
using Task = System.Threading.Tasks.Task;

namespace Pillion.VisualStudio.IntegrationTests;

/// <summary>
/// The package, Pillion Host and the Chat window in a real Visual Studio. The tests share one Experimental Instance
/// and one Pillion Host per Visual Studio version and run in <see cref="TestOrderAttribute"/> order. None of them needs
/// a model: Pillion Host reaches Ready with no provider configured.
/// </summary>
[IdeSettings(MinVersion = IdeTarget.Version, MaxVersion = IdeTarget.Version)]
[TestCaseOrderer(TestOrderer.TypeName, TestOrderer.AssemblyName)]
public sealed partial class PillionPackageTests(ITestOutputHelper output) : PillionIdeTest(output)
{
    /// <summary>The design's budget for the UI thread while the host is killed (design section 4, Failure modes).</summary>
    private static readonly TimeSpan MaxUiGap = TimeSpan.FromSeconds(2);

    /// <summary>A posted operation that waited this long for the UI thread is marked in the output.</summary>
    private static readonly TimeSpan SlowOperation = TimeSpan.FromMilliseconds(500);

    /// <summary>The generous limit of <see cref="PackageLoad_UsesLittleUiThreadCpu"/> on the UI thread's whole CPU time, which holds Visual Studio's own work too (the design budget is 50 ms for Pillion's).</summary>
    private static readonly TimeSpan MaxPackageLoadUiCpu = TimeSpan.FromMilliseconds(500);

    /// <summary>The design budget (section 13) for Pillion's own time on the UI thread between the package load and Ready.</summary>
    private static readonly TimeSpan MaxPillionLoadMainThread = TimeSpan.FromMilliseconds(50);

    /// <summary>The budget for the part of <c>InitializeAsync</c> before its first switch to the UI thread (it blocks the shell on a synchronous load).</summary>
    private static readonly TimeSpan MaxPreAwait = TimeSpan.FromMilliseconds(10);

    /// <summary>Gets or sets what the first folder open of the run measured (the tests of a run share one instance).</summary>
    private static PackageLoadCpuReport? PackageLoadMeasurement { get; set; }

    [IdeFact]
    [TestOrder(0)]
    public async Task VisualStudio_IsAtLeast17_14()
    {
        var version = await GetVisualStudioVersionAsync();
        Output.WriteLine($"Visual Studio {version}, process {System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName}");
        Assert.True(version >= MinimumVisualStudioVersion, $"Visual Studio {version} is older than {MinimumVisualStudioVersion}, the oldest version Pillion supports.");
    }

    [IdeFact]
    [TestOrder(1)]
    public async Task Package_LoadsOnFolderOpen_WithNoActivityLogErrors()
    {
        Assert.False(
            await IsPackageLoadedAsync(),
            "The Pillion package was already loaded before any folder was opened: either another test ran first (check the test order) or something else loads it.");

        // Opens the folder, waits for the package and Ready, and samples the UI thread's CPU time on the way (reported
        // by PackageLoad_UsesLittleUiThreadCpu).
        var log = await OpenFolderAndMeasurePackageLoadAsync();
        Assert.True(log.RawText.Length > 0, "The in-memory Activity Log is empty (was Visual Studio started with /log?).");
        var about = log.EntriesAbout(PackageGuid).ToList();
        foreach (var entry in about)
        {
            Output.WriteLine("Activity Log: " + entry);
        }

        var errors = about.Where(e => e.IsError).ToList();
        Assert.True(errors.Count == 0, "Activity Log errors about Pillion:" + Environment.NewLine + string.Join(Environment.NewLine, errors));
    }

    /// <summary>
    /// Gate 1 row "VSIX cost in devenv.exe" (design section 13: 0 ms blocking at start-up, under 50 ms total UI-thread
    /// CPU). This reports the UI thread's total CPU time from the Activity Log's "Begin package load" of the Pillion
    /// package to the host's Ready, which also holds whatever else Visual Studio ran on that thread meanwhile (the folder
    /// open finishing, other packages), next to an idle baseline of the same length, and Pillion's own timed stretches on
    /// the UI thread over the same window (the test switches the product's UiThreadTimings on before it opens the folder),
    /// split by stretch, with the number of visits to the UI thread and the time InitializeAsync spends before its first
    /// switch. It asserts Pillion's own UI-thread time under 50 ms and that pre-await part under 10 ms (numbers printed
    /// first, so a failure shows them); the CPU of the whole UI thread, which Pillion does not control, only under a
    /// generous 500 ms. The extension-on against extension-off launches of tools/gate1 are in the Gate 1 report.
    /// </summary>
    [IdeFact]
    [TestOrder(2)]
    public async Task PackageLoad_UsesLittleUiThreadCpu()
    {
        if (PackageLoadMeasurement is null)
        {
            // Run on its own (a test filter): measure here, if the package has not loaded yet.
            Assert.False(await IsPackageLoadedAsync(), "The package is already loaded and Package_LoadsOnFolderOpen_WithNoActivityLogErrors did not measure its load; run both together.");
            _ = await OpenFolderAndMeasurePackageLoadAsync();
        }

        var measured = PackageLoadMeasurement!;
        Output.WriteLine(measured.ToString());
        var timed = measured.PillionTimed;
        Output.WriteLine("PackageLoadTimings: " + (timed is null ? "{}" : PackageLoadTimingsJson(timed)));
        Assert.True(timed is { Enabled: true }, "Pillion's UI-thread timings were not recorded during the package load. " + measured);
        Assert.True(
            timed!.MainThreadTotal < MaxPillionLoadMainThread,
            $"Pillion's own time on the UI thread between the package load and Ready was {timed.MainThreadTotal.TotalMilliseconds:0.0} ms (budget {MaxPillionLoadMainThread.TotalMilliseconds:0} ms). {timed}");
        Assert.True(
            timed.PreAwait is { } preAwait && preAwait < MaxPreAwait,
            $"InitializeAsync spent {timed.PreAwait?.TotalMilliseconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) ?? "(not recorded)"} ms before its first switch to the UI thread (budget {MaxPreAwait.TotalMilliseconds:0} ms). {timed}");
        Assert.True(
            measured.UiCpuLoadToReady <= MaxPackageLoadUiCpu,
            $"The UI thread used {measured.UiCpuLoadToReady.TotalMilliseconds:0} ms of CPU between the package load and Ready (limit {MaxPackageLoadUiCpu.TotalMilliseconds:0} ms). {measured}");
    }

    [IdeFact]
    [TestOrder(2)]
    public async Task HostConnection_ReachesReady_Within30Seconds()
    {
        await EnsurePackageLoadedAsync();

        var report = await PillionHostChecks.WaitForReadyAsync(TimeSpan.FromSeconds(30), HangMitigatingCancellationToken);

        Output.WriteLine($"Pillion Host {report.State} after {report.Elapsed.TotalMilliseconds:0} ms; IHostConnection from {report.ServiceAssembly}");
        Assert.Equal("Ready", report.State);
        Assert.Single(await HostProcesses.FindChildrenAsync());
    }

    [IdeFact]
    [TestOrder(3)]
    public async Task KillingTheHost_KeepsTheUIResponsive_AndReconnectsWithin15Seconds()
    {
        await EnsurePackageLoadedAsync();
        await PillionHostChecks.WaitForReadyAsync(TimeSpan.FromSeconds(30), HangMitigatingCancellationToken);

        // Two probes run side by side for the same 15 s, on one clock. The dispatcher probe runs on the thread pool and
        // posts to the UI thread every 250 ms; this test's own awaits leave the UI thread free in the meantime, as a
        // user's Visual Studio would be. The heartbeat is a dedicated thread that sleeps 250 ms and records when it
        // wakes: it has nothing to do with the UI thread, so when both show a long gap together the machine (a shared CI
        // runner starved of CPU, a pause of the whole process) stalled, and when only the UI thread does, the UI thread
        // was kept busy.
        // Beside them, UiThreadMonitor times every dispatcher operation and samples the UI thread's stack in a stall, and
        // the product's UiThreadTimings switch times Pillion's own UI-thread work, so a gap can be attributed.
        using var uiCpu = ThreadCpuMeter.ForCurrentThread();
        var uiCpuBefore = uiCpu.Read();
        PillionHostChecks.StartUiThreadTimings();
        var timeline = Stopwatch.StartNew();
        var interval = TimeSpan.FromMilliseconds(250);
        var window = TimeSpan.FromSeconds(15);
        using var monitor = UiThreadMonitor.Start(Application.Current.Dispatcher, timeline, TimeSpan.Zero);
        var heartbeat = BackgroundHeartbeat.RunAsync(timeline, window, interval, HangMitigatingCancellationToken);
        var probe = DispatcherProbe.RunAsync(Application.Current.Dispatcher, timeline, window, interval, HangMitigatingCancellationToken);
        var restart = PillionHostChecks.KillHostAndWaitForReadyAsync(TimeSpan.FromSeconds(15), HangMitigatingCancellationToken, timeline);
        var responsiveness = await probe;
        var background = await heartbeat;
        var report = await restart;
        var uiCpuUsed = uiCpu.Read() - uiCpuBefore;
        var watched = monitor.Stop(PillionHostChecks.StopUiThreadTimings(timeline));

        Output.WriteLine($"Killed Pillion.Host.exe pid {report.KilledProcessId} at {report.KilledAt.TotalMilliseconds:0} ms on the probes' clock; left Ready after {report.LeftReadyAfter.TotalMilliseconds:0} ms; Ready again after {report.ReadyAgainAfter.TotalMilliseconds:0} ms with pid {report.NewProcessId}.");
        foreach (var transition in report.Transitions)
        {
            Output.WriteLine("  " + transition);
        }

        Output.WriteLine("UI thread: " + responsiveness);
        Output.WriteLine("Background thread: " + background);
        Output.WriteLine($"UI thread CPU time over the {timeline.Elapsed.TotalMilliseconds:0} ms window: {uiCpuUsed.TotalMilliseconds:0} ms (a long gap with little CPU time means the UI thread was blocked or starved, not busy).");
        Output.WriteLine($"Posted operations (times in ms on the probes' clock, where the kill happened at {report.KilledAt.TotalMilliseconds:0}; slow = waited {SlowOperation.TotalMilliseconds:0} ms or more):");
        foreach (var line in responsiveness.FormatOperations(SlowOperation))
        {
            Output.WriteLine(line);
        }

        // Whose code held the UI thread: every dispatcher operation is timed and, in a stall, the stack is sampled. The
        // samples printed are those of the longest gap; the gap's first 200 ms come before the sampler notices it.
        foreach (var line in watched.Format(TimeSpan.FromMilliseconds(50), responsiveness.LongestGapStart, responsiveness.LongestGapEnd))
        {
            Output.WriteLine(line);
        }

        var pillionShare = watched.PillionTimeWithin(responsiveness.LongestGapStart, responsiveness.LongestGapEnd);
        Output.WriteLine(pillionShare is { } share
            ? $"Pillion's own UI thread time within the longest gap: {share.TotalMilliseconds:0} ms of {responsiveness.LongestGap.TotalMilliseconds:0} ms."
            : $"Pillion's own UI thread time within the longest gap: unknown (the monitor did not see enough of the {responsiveness.LongestGap.TotalMilliseconds:0} ms gap).");

        var verdict = ResponsivenessVerdict.Judge(responsiveness.LongestGap, background.LongestGap, MaxUiGap, pillionShare);
        if (verdict.Warning is not null)
        {
            Output.WriteLine(verdict.Warning);
        }

        Assert.True(verdict.Failure is null, verdict.Failure + " UI thread: " + responsiveness + "; background thread: " + background + ".");
        Assert.Equal(responsiveness.Posted, responsiveness.Answered);
        Assert.True(report.ReadyAgainAfter <= TimeSpan.FromSeconds(15), $"Ready again only after {report.ReadyAgainAfter.TotalSeconds:0.0} s.");
        Assert.NotEqual(0, report.NewProcessId);
        Assert.NotEqual(report.KilledProcessId, report.NewProcessId);
    }

    [IdeFact]
    [TestOrder(4)]
    public async Task OpenChatCommand_ShowsThePillionChatWindow()
    {
        await EnsurePackageLoadedAsync();
        await PillionHostChecks.WaitForReadyAsync(TimeSpan.FromSeconds(30), HangMitigatingCancellationToken);

        await TestServices.Shell.ExecuteCommandAsync("Tools.PillionOpenChat", HangMitigatingCancellationToken);

        IVsWindowFrame? frame = null;
        await WaitUntilAsync(
            async () =>
            {
                frame = await FindChatWindowAsync();
                await JoinableTaskFactory.SwitchToMainThreadAsync(HangMitigatingCancellationToken);
                return frame is not null && frame.IsVisible() == VSConstants.S_OK;
            },
            TimeSpan.FromSeconds(30),
            "the Pillion Chat window to be shown");

        await JoinableTaskFactory.SwitchToMainThreadAsync(HangMitigatingCancellationToken);
        ErrorHandler.ThrowOnFailure(frame!.GetProperty((int)__VSFPROPID.VSFPROPID_Caption, out var caption));
        Output.WriteLine($"Tool window caption: {caption}");
        Assert.Equal("Pillion Chat", caption as string);

        ErrorHandler.ThrowOnFailure(frame.CloseFrame((uint)__FRAMECLOSE.FRAMECLOSE_NoSave));
    }

    /// <summary>
    /// Opens a scratch folder, waits for the package to load by itself and for Ready, sampling the UI thread's CPU time
    /// every 10 ms from a background thread on the way, then samples an idle stretch of the same length as the load for a
    /// baseline. Stores the result in <see cref="PackageLoadMeasurement"/> and returns the Activity Log.
    /// </summary>
    private async Task<ActivityLog> OpenFolderAndMeasurePackageLoadAsync()
    {
        await JoinableTaskFactory.SwitchToMainThreadAsync(HangMitigatingCancellationToken);
        var uiThreadId = Environment.CurrentManagedThreadId;

        // The product's UiThreadTimings reads this variable when the package first uses it, which is at load: setting it
        // here, before the folder opens, records the package's own initialization. (The name is a literal in PillionHostChecks, and
        // nothing in this method names a Pillion.VisualStudio.Core type, so the assembly is not loaded before the package loads it.)
        var timingsVariable = Environment.GetEnvironmentVariable(PillionHostChecks.UiThreadTimingsVariable);
        Environment.SetEnvironmentVariable(PillionHostChecks.UiThreadTimingsVariable, "1");
        using var sampler = UiThreadCpuSampler.Start(ThreadCpuMeter.CurrentThreadId, TimeSpan.FromMilliseconds(10));
        var folderOpening = sampler.Mark();
        var folderOpeningTimestamp = Stopwatch.GetTimestamp();

        await OpenScratchFolderAsync();
        var took = await WaitForPackageLoadedAsync(TimeSpan.FromSeconds(60));
        var loadedSeen = sampler.Mark();
        Output.WriteLine($"Package loaded {took.TotalMilliseconds:0} ms after the folder opened.");

        // Initialization continues on a background thread after the package object exists; let it finish.
        await PillionHostChecks.WaitForReadyAsync(TimeSpan.FromSeconds(30), HangMitigatingCancellationToken);
        var ready = sampler.Mark();
        var readyTimestamp = Stopwatch.GetTimestamp();

        // Pillion's own timed UI-thread work in the same window (only with PILLION_UI_THREAD_TIMINGS=1 from the start).
        var timed = PillionHostChecks.SummarizeLoad(folderOpeningTimestamp, readyTimestamp, uiThreadId);
        PillionHostChecks.RestoreUiThreadTimingsVariable(timingsVariable); // The rest of the run is untimed unless the gate asked for it.

        var log = await GetActivityLogAsync();
        var begin = log.EntriesAbout(PackageGuid)
            .Where(e => e.Time is not null && e.Description.StartsWith("Begin package load", StringComparison.OrdinalIgnoreCase))
            .Select(e => e.Time)
            .FirstOrDefault();
        var loadStart = begin is { } beginUtc && beginUtc >= folderOpening.AddSeconds(-1) ? beginUtc : loadedSeen;

        // An idle stretch as long as the load, with the test's own awaits leaving the UI thread free.
        var idleLength = ready - loadStart;
        if (idleLength < TimeSpan.FromSeconds(1))
        {
            idleLength = TimeSpan.FromSeconds(1);
        }

        var idleStart = sampler.Mark();
        await Task.Delay(idleLength, HangMitigatingCancellationToken);
        var idleEnd = sampler.Mark();

        PackageLoadMeasurement = new PackageLoadCpuReport(
            LoadStartSource: begin is null ? "package seen loaded (no \"Begin package load\" entry with a time in the Activity Log)" : "Activity Log \"Begin package load\"",
            FolderOpenToReady: ready - folderOpening,
            LoadToReady: ready - loadStart,
            UiCpuFolderOpenToReady: sampler.CpuBetween(folderOpening, ready),
            UiCpuLoadToReady: sampler.CpuBetween(loadStart, ready),
            UiCpuIdleBaseline: sampler.CpuBetween(idleStart, idleEnd),
            IdleLength: idleEnd - idleStart,
            Samples: sampler.Count)
        {
            PillionTimed = timed,
        };
        return log;
    }

    /// <summary>One JSON line with the load timings, for scripts that collect the runs (the TRX holds the output).</summary>
    private static string PackageLoadTimingsJson(PackageLoadTimings timed) =>
        new Newtonsoft.Json.Linq.JObject
        {
            ["recorded"] = timed.Enabled,
            ["mainThreadMs"] = Math.Round(timed.MainThreadTotal.TotalMilliseconds, 2),
            ["mainThreadVisits"] = timed.MainThreadVisits,
            ["preAwaitMs"] = timed.PreAwait is { } p ? Math.Round(p.TotalMilliseconds, 2) : null,
            ["preAwaitOnUiThread"] = timed.PreAwaitOnUiThread,
            ["stretches"] = new Newtonsoft.Json.Linq.JArray(timed.Stretches.Select(s => new Newtonsoft.Json.Linq.JObject { ["name"] = s.Name, ["count"] = s.Count, ["ms"] = Math.Round(s.Total.TotalMilliseconds, 2) })),
        }.ToString(Newtonsoft.Json.Formatting.None);

    private async Task<IVsWindowFrame?> FindChatWindowAsync()
    {
        await JoinableTaskFactory.SwitchToMainThreadAsync(HangMitigatingCancellationToken);
        var shell = await TestServices.Shell.GetRequiredGlobalServiceAsync<SVsUIShell, IVsUIShell>(HangMitigatingCancellationToken);
        var guid = ChatToolWindowGuid;
        return ErrorHandler.Succeeded(shell.FindToolWindow((uint)__VSFINDTOOLWIN.FTW_fFindFirst, ref guid, out var frame)) ? frame : null;
    }
}
