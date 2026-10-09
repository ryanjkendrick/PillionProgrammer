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
public sealed class PillionPackageTests(ITestOutputHelper output) : PillionIdeTest(output)
{
    /// <summary>The design's budget for the UI thread while the host is killed (design section 4, Failure modes).</summary>
    private static readonly TimeSpan MaxUiGap = TimeSpan.FromSeconds(2);

    /// <summary>A posted operation that waited this long for the UI thread is marked in the output.</summary>
    private static readonly TimeSpan SlowOperation = TimeSpan.FromMilliseconds(500);

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

        await OpenScratchFolderAsync();
        var took = await WaitForPackageLoadedAsync(TimeSpan.FromSeconds(60));
        Output.WriteLine($"Package loaded {took.TotalMilliseconds:0} ms after the folder opened.");

        // Initialization continues on a background thread after the package object exists; let it finish.
        await PillionHostChecks.WaitForReadyAsync(TimeSpan.FromSeconds(30), HangMitigatingCancellationToken);

        var log = await GetActivityLogAsync();
        Assert.True(log.RawText.Length > 0, "The in-memory Activity Log is empty (was Visual Studio started with /log?).");
        var about = log.EntriesAbout(PackageGuid).ToList();
        foreach (var entry in about)
        {
            Output.WriteLine("Activity Log: " + entry);
        }

        var errors = about.Where(e => e.IsError).ToList();
        Assert.True(errors.Count == 0, "Activity Log errors about Pillion:" + Environment.NewLine + string.Join(Environment.NewLine, errors));
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
        using var uiCpu = ThreadCpuMeter.ForCurrentThread();
        var uiCpuBefore = uiCpu.Read();
        var timeline = Stopwatch.StartNew();
        var interval = TimeSpan.FromMilliseconds(250);
        var window = TimeSpan.FromSeconds(15);
        var heartbeat = BackgroundHeartbeat.RunAsync(timeline, window, interval, HangMitigatingCancellationToken);
        var probe = DispatcherProbe.RunAsync(Application.Current.Dispatcher, timeline, window, interval, HangMitigatingCancellationToken);
        var restart = PillionHostChecks.KillHostAndWaitForReadyAsync(TimeSpan.FromSeconds(15), HangMitigatingCancellationToken, timeline);
        var responsiveness = await probe;
        var background = await heartbeat;
        var report = await restart;
        var uiCpuUsed = uiCpu.Read() - uiCpuBefore;

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

        var verdict = ResponsivenessVerdict.Judge(responsiveness.LongestGap, background.LongestGap, MaxUiGap);
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

    private async Task<IVsWindowFrame?> FindChatWindowAsync()
    {
        await JoinableTaskFactory.SwitchToMainThreadAsync(HangMitigatingCancellationToken);
        var shell = await TestServices.Shell.GetRequiredGlobalServiceAsync<SVsUIShell, IVsUIShell>(HangMitigatingCancellationToken);
        var guid = ChatToolWindowGuid;
        return ErrorHandler.Succeeded(shell.FindToolWindow((uint)__VSFINDTOOLWIN.FTW_fFindFirst, ref guid, out var frame)) ? frame : null;
    }
}
