#if !IDE_VS2022
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Newtonsoft.Json.Linq;
using Task = System.Threading.Tasks.Task;

namespace Pillion.VisualStudio.IntegrationTests;

/// <summary>
/// Mentions in the chat window (work item 1.9), end to end with <c>tests/Pillion.FakeHost</c> behind the package: type
/// <c>@</c>, pick a file of the open folder with the keyboard, send, and check that the host received one pin. The fake host
/// is put in place for this test only (the package's PILLION_TEST_HOST_PATH, set inside devenv.exe, and Pillion: Retry) and
/// the shipped host is restored afterwards. Built for Visual Studio 2026 only: the Visual Studio 2022 CI job runs a
/// downloaded test build without the fake host.
/// </summary>
public sealed partial class PillionPackageTests
{
    /// <summary>Gets the fake host built next to this repository's tests (set by the project file).</summary>
    private static string FakeHostPath => typeof(PillionPackageTests).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .Single(a => a.Key == "FakeHostPath").Value!;

    [IdeFact]
    [TestOrder(20)]
    public async Task Mentions_TypeAtPickAFileAndSend_TheHostReceivesOnePin()
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
            var pid = await MentionChecks.UseFakeHostAsync(fakeHost, record, HangMitigatingCancellationToken);
            Output.WriteLine($"Fake host ready (pid {pid}); folder {folder}.");

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
            var control = (view as ToolWindowPane)?.Content as FrameworkElement;
            Assert.True(control is not null, "The Pillion Chat window has no content.");
            try
            {
                var report = await MentionChecks.PickFileAndSendAsync(control!, "Program.cs", Output.WriteLine, HangMitigatingCancellationToken);

                Assert.Equal(string.Empty, report.InputAfterPick);
                Assert.Contains("Fake Local Coder", report.ReplyHeader, StringComparison.Ordinal);
                Assert.True(report.OutboundRows >= 1, "The Outbound log shows no request.");
            }
            finally
            {
                MentionChecks.Reset(control!);
            }

            // What the fake host received: one chat request, with one pin, the picked file.
            var lines = File.ReadAllLines(record).Where(l => l.Trim().Length > 0).ToList();
            foreach (var line in lines)
            {
                Output.WriteLine("Fake host received: " + line);
            }

            var chat = JObject.Parse(Assert.Single(lines));
            var pins = (JArray)chat["pins"]!;
            var pin = (JObject)Assert.Single(pins);
            Assert.Equal("file", (string?)pin["kind"]);
            Assert.Equal("Program.cs", (string?)pin["name"]);
            Assert.False((bool)pin["sensitive"]!);
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
}
#endif
