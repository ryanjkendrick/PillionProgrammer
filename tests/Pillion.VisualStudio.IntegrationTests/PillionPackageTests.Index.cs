#if !IDE_VS2022
using System;
using System.IO;
using System.Windows;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Task = System.Threading.Tasks.Task;

namespace Pillion.VisualStudio.IntegrationTests;

/// <summary>
/// The Index window (work item 2.15), end to end with <c>tests/Pillion.FakeHost</c> behind the package and
/// <c>PILLION_FAKEHOST_INDEX=ready</c>: the Tools &gt; Pillion: Open Index command shows the window, the root row shows the
/// canned index, Pause and Resume go through the host and update the header, and the Excluded files tab lists the canned
/// files with the secret-bearing ones first. The fake host is put in place for this test only and the shipped host is
/// restored afterwards. Built for Visual Studio 2026 only, like the mention test.
/// </summary>
public sealed partial class PillionPackageTests
{
    [IdeFact]
    [TestOrder(21)]
    public async Task IndexWindow_ShowsTheRootRow_AndPauseAndResumeGoThroughTheHost()
    {
        var fakeHost = Path.GetFullPath(FakeHostPath);
        Assert.True(File.Exists(fakeHost), "Build tests/Pillion.FakeHost first (the full solution build does): " + fakeHost);
        var folder = await OpenScratchFolderAsync();
        await WaitForPackageLoadedAsync(TimeSpan.FromSeconds(60));
        await PillionHostChecks.WaitForReadyAsync(TimeSpan.FromSeconds(30), HangMitigatingCancellationToken);
        var record = Path.Combine(Path.GetTempPath(), "pillion-ide-tests", "fakehost-" + Guid.NewGuid().ToString("N").Substring(0, 12) + ".jsonl");
        IVsWindowFrame? frame = null;
        try
        {
            Environment.SetEnvironmentVariable(IndexWindowChecks.IndexVariable, "ready");
            var pid = await MentionChecks.UseFakeHostAsync(fakeHost, record, HangMitigatingCancellationToken);
            Output.WriteLine($"Fake host ready (pid {pid}) with the canned index; folder {folder}.");

            await TestServices.Shell.ExecuteCommandAsync("Tools.PillionOpenIndex", HangMitigatingCancellationToken);
            await WaitUntilAsync(
                async () =>
                {
                    frame = await FindIndexWindowAsync();
                    await JoinableTaskFactory.SwitchToMainThreadAsync(HangMitigatingCancellationToken);
                    return frame is not null && frame.IsVisible() == VSConstants.S_OK;
                },
                TimeSpan.FromSeconds(30),
                "the Pillion Index window to be shown");

            await JoinableTaskFactory.SwitchToMainThreadAsync(HangMitigatingCancellationToken);
            ErrorHandler.ThrowOnFailure(frame!.GetProperty((int)__VSFPROPID.VSFPROPID_Caption, out var caption));
            Assert.Equal("Pillion Index", caption as string);
            ErrorHandler.ThrowOnFailure(frame.GetProperty((int)__VSFPROPID.VSFPROPID_DocView, out var view));
            var control = (view as ToolWindowPane)?.Content as FrameworkElement;
            Assert.True(control is not null, "The Pillion Index window has no content.");

            var report = await IndexWindowChecks.CheckRowsAndPauseAsync(control!, Output.WriteLine, HangMitigatingCancellationToken);

            Assert.Equal(IndexWindowChecks.CannedRoot, report.Root);
            Assert.Equal(6, report.ExcludedEntries);
            Assert.True(report.SecretFirst, "The first excluded entry is not secret-bearing.");
        }
        finally
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync(HangMitigatingCancellationToken);
            frame?.CloseFrame((uint)__FRAMECLOSE.FRAMECLOSE_NoSave);
            var restored = await MentionChecks.RestoreHostAsync(HangMitigatingCancellationToken);
            Output.WriteLine($"Pillion Host restored (pid {restored}).");
            try
            {
                File.Delete(record);
            }
            catch (IOException)
            {
                // %TEMP% is cleaned up eventually.
            }
        }
    }

    private async System.Threading.Tasks.Task<IVsWindowFrame?> FindIndexWindowAsync()
    {
        await JoinableTaskFactory.SwitchToMainThreadAsync(HangMitigatingCancellationToken);
        var shell = await TestServices.Shell.GetRequiredGlobalServiceAsync<SVsUIShell, IVsUIShell>(HangMitigatingCancellationToken);
        var guid = IndexToolWindowGuid;
        return ErrorHandler.Succeeded(shell.FindToolWindow((uint)__VSFINDTOOLWIN.FTW_fFindFirst, ref guid, out var frame)) ? frame : null;
    }
}
#endif
