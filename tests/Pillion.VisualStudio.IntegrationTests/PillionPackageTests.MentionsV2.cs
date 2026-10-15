#if !IDE_VS2022
using System;
using System.IO;
using System.Windows;
using EnvDTE;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Task = System.Threading.Tasks.Task;

namespace Pillion.VisualStudio.IntegrationTests;

/// <summary>
/// Mentions v2 (work item 2.13), the kinds whose content only Visual Studio has: <c>@output</c> (an Output pane's tail) and
/// <c>@build</c> (the last build's outcome and output, kept by the IDE signals when the build finished). Both pick the
/// entry with the keyboard in the chat window, with <c>tests/Pillion.FakeHost</c> behind the package, which pins the
/// payload the extension sends as it is. Built for Visual Studio 2026 only, like the other fake host test.
/// </summary>
public sealed partial class PillionPackageTests
{
    [IdeFact]
    [TestOrder(22)]
    public async Task MentionsV2_OutputBuild_PinsTheBuildPanesText()
    {
        var fakeHost = Path.GetFullPath(FakeHostPath);
        Assert.True(File.Exists(fakeHost), "Build tests/Pillion.FakeHost first (the full solution build does): " + fakeHost);
        await OpenScratchFolderAsync();
        await WaitForPackageLoadedAsync(TimeSpan.FromSeconds(60));
        await PillionHostChecks.WaitForReadyAsync(TimeSpan.FromSeconds(30), HangMitigatingCancellationToken);
        var marker = "pillion-output-marker-" + Guid.NewGuid().ToString("N").Substring(0, 8);
        await MentionChecks.WriteToBuildPaneAsync(marker, HangMitigatingCancellationToken);

        await WithFakeHostChatAsync(async control =>
        {
            var pin = await MentionChecks.PinFromPickerAsync(control, "@output:", "@output:build", Output.WriteLine, HangMitigatingCancellationToken);

            Assert.Equal("output", pin.Kind);
            Assert.Contains(marker, pin.Content, StringComparison.Ordinal);
        });
    }

    [IdeFact]
    [TestOrder(23)]
    public async Task MentionsV2_Build_PinsTheLastBuildsOutcomeAndOutput()
    {
        var fakeHost = Path.GetFullPath(FakeHostPath);
        Assert.True(File.Exists(fakeHost), "Build tests/Pillion.FakeHost first (the full solution build does): " + fakeHost);
        var folder = CreateScratchSolution(withCompileError: true);
        try
        {
            await OpenSolutionAsync(folder);
            await WaitForPackageLoadedAsync(TimeSpan.FromSeconds(60));
            await PillionHostChecks.WaitForReadyAsync(TimeSpan.FromSeconds(30), HangMitigatingCancellationToken);
            await BuildAndWaitAsync();

            await WithFakeHostChatAsync(async control =>
            {
                var pin = await MentionChecks.PinFromPickerAsync(control, "@build", "@build", Output.WriteLine, HangMitigatingCancellationToken);

                Assert.Equal("build", pin.Kind);
                Assert.StartsWith("Build failed: ", pin.Content, StringComparison.Ordinal);
                Assert.Contains("CS0029", pin.Content, StringComparison.Ordinal);
            });
        }
        finally
        {
            await CloseSolutionAsync();
            TryDelete(folder);
        }
    }

    // Uses Pillion.VisualStudio.Core types, so it must not run (be compiled by the JIT) before the package has loaded them.
    private async Task BuildAndWaitAsync()
    {
        using var recording = await SignalRecording.StartAsync(TimeSpan.FromSeconds(60), HangMitigatingCancellationToken);
        await JoinableTaskFactory.SwitchToMainThreadAsync(HangMitigatingCancellationToken);
        var dte = await TestServices.Shell.GetRequiredGlobalServiceAsync<SDTE, DTE>(HangMitigatingCancellationToken);
        dte.Solution.SolutionBuild.Build(true);
        await recording.WaitForAsync("BuildFinished", TimeSpan.FromSeconds(120), HangMitigatingCancellationToken);
        await WaitUntilAsync(
            () => Task.FromResult(recording.Signals.LastBuild?.Output is not null),
            TimeSpan.FromSeconds(30),
            "the IDE signals to keep the Build pane's tail");
        Output.WriteLine("Last build: " + recording.Signals.LastBuild!.Summary());
    }

    /// <summary>Puts the fake host behind the package, opens the chat window, runs the check and restores the shipped host.</summary>
    private async Task WithFakeHostChatAsync(Func<FrameworkElement, Task> check)
    {
        var record = Path.Combine(Path.GetTempPath(), "pillion-ide-tests", "fakehost-" + Guid.NewGuid().ToString("N").Substring(0, 12) + ".jsonl");
        IVsWindowFrame? frame = null;
        FrameworkElement? control = null;
        try
        {
            var pid = await MentionChecks.UseFakeHostAsync(Path.GetFullPath(FakeHostPath), record, HangMitigatingCancellationToken);
            Output.WriteLine($"Fake host ready (pid {pid}).");
            await TestServices.Shell.ExecuteCommandAsync("Tools.PillionOpenChat", HangMitigatingCancellationToken);
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
            ErrorHandler.ThrowOnFailure(frame!.GetProperty((int)__VSFPROPID.VSFPROPID_DocView, out var view));
            control = (view as ToolWindowPane)?.Content as FrameworkElement;
            Assert.True(control is not null, "The Pillion Chat window has no content.");
            await check(control!);
        }
        finally
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync(HangMitigatingCancellationToken);
            if (control is not null)
            {
                MentionChecks.Reset(control);
            }

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
}
#endif
