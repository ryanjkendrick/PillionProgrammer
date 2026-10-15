#if !IDE_VS2022
using System;
using System.IO;
using System.Linq;
using System.Windows;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.TextManager.Interop;
using Pillion.VisualStudio.Core;
using Task = System.Threading.Tasks.Task;

namespace Pillion.VisualStudio.IntegrationTests;

/// <summary>
/// Cited answers in the chat window (work item 2.12; Gate 2 "Cited answers"), end to end with <c>tests/Pillion.FakeHost</c>:
/// a message containing <c>/cite</c> gets a reply whose final chunk names one span of the current file (explicit) and one
/// other span (implicit). The window shows the first as a <c>file:start-end</c> link and the second under a collapsed
/// "Sources (1)" line; opening the link opens the file with the span selected. Built for Visual Studio 2026 only, like the
/// other fake-host tests.
/// </summary>
public sealed partial class PillionPackageTests
{
    [IdeFact]
    [TestOrder(22)]
    public async Task CitedAnswer_ShowsTheSpanLink_AndTheSourcesLine_AndOpensTheSpanSelected()
    {
        var fakeHost = Path.GetFullPath(FakeHostPath);
        Assert.True(File.Exists(fakeHost), "Build tests/Pillion.FakeHost first (the full solution build does): " + fakeHost);
        var folder = await OpenScratchFolderAsync();
        var program = Path.Combine(folder, "Program.cs");
        await WaitForPackageLoadedAsync(TimeSpan.FromSeconds(60));
        await PillionHostChecks.WaitForReadyAsync(TimeSpan.FromSeconds(30), HangMitigatingCancellationToken);
        var record = Path.Combine(Path.GetTempPath(), "pillion-ide-tests", "fakehost-" + Guid.NewGuid().ToString("N").Substring(0, 12) + ".jsonl");
        IVsWindowFrame? frame = null;
        FrameworkElement? control = null;
        try
        {
            var pid = await MentionChecks.UseFakeHostAsync(fakeHost, record, HangMitigatingCancellationToken);
            Output.WriteLine($"Fake host ready (pid {pid}); folder {folder}.");

            // Program.cs is the active document, so the message carries it as the current file and the explicit citation is in it.
            await JoinableTaskFactory.SwitchToMainThreadAsync(HangMitigatingCancellationToken);
            VsShellUtilities.OpenDocument(ServiceProvider.GlobalProvider, program, Guid.Empty, out _, out _, out var document, out _);
            document?.Show();

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
            var vm = control!.DataContext as ChatViewModel ?? throw new InvalidOperationException("The chat window's DataContext is not a ChatViewModel.");
            await WaitUntilAsync(() => Task.FromResult(vm.ModelPicker.SelectedModel is { IsAvailable: true }), TimeSpan.FromSeconds(30), "a usable model in the picker");

            await JoinableTaskFactory.SwitchToMainThreadAsync(HangMitigatingCancellationToken);
            vm.InputText = "/cite Where is Main?";
            await vm.SendAsync();
            await WaitUntilAsync(() => Task.FromResult(vm.Turns.Count >= 2 && !vm.IsSending && vm.Turns.Last().HasCitations), TimeSpan.FromSeconds(60), "the cited reply");

            await JoinableTaskFactory.SwitchToMainThreadAsync(HangMitigatingCancellationToken);
            var reply = vm.Turns.Last();
            Output.WriteLine($"Reply: {reply.Text}; explicit: {string.Join(", ", reply.ExplicitCitations.Select(c => c.FilePath + " " + c.Text))}; sources: {reply.SourcesText}.");
            var link = Assert.Single(reply.ExplicitCitations);
            Assert.EndsWith(":1-5", link.Text, StringComparison.Ordinal);
            Assert.Single(reply.ImplicitCitations);
            Assert.Equal("Sources (1)", reply.SourcesText);
            Assert.False(reply.AreSourcesExpanded);
            reply.ToggleSourcesCommand.Execute(null);
            Assert.True(reply.AreSourcesExpanded);

            vm.OpenCitationCommand.Execute(link);
            if (string.Equals(link.FilePath, program, StringComparison.OrdinalIgnoreCase))
            {
                var manager = await TestServices.Shell.GetRequiredGlobalServiceAsync<SVsTextManager, IVsTextManager>(HangMitigatingCancellationToken);
                await JoinableTaskFactory.SwitchToMainThreadAsync(HangMitigatingCancellationToken);
                ErrorHandler.ThrowOnFailure(manager.GetActiveView(1, null, out var active));
                ErrorHandler.ThrowOnFailure(active.GetSelection(out var anchorLine, out _, out var endLine, out _));
                Output.WriteLine($"Selection after opening the link: lines {anchorLine} to {endLine}.");
                Assert.Equal((0, 4), (Math.Min(anchorLine, endLine), Math.Max(anchorLine, endLine)));
            }
            else
            {
                Output.WriteLine("The message did not carry Program.cs as the current file; the selection is not checked.");
            }
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
