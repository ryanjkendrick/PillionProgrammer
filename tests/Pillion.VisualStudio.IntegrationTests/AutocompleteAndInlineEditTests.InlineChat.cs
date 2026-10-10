using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Input;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text.Editor;

namespace Pillion.VisualStudio.IntegrationTests;

/// <summary>
/// The inline difference view (work item 1.7; Gate 1 row "Inline chat preview"): Enter applies the proposal as one edit
/// with one undo step, and Esc leaves nothing to undo. No model is needed: <see cref="InlineChatChecks"/> gives inline chat
/// a canned inline edit service, and the keys are raised on the adornment's prompt as WPF key events.
/// </summary>
public sealed partial class AutocompleteAndInlineEditTests
{
    /// <summary>The command set of the Pillion commands (PackageGuids.CommandSetString in the extension).</summary>
    private static readonly Guid InlineChatCommandSet = new("444aebc2-1c36-43a9-ab09-2c2d4e04a11d");

    /// <summary>The "Pillion: Inline Chat" command id (PackageIds.InlineChatCommand).</summary>
    private const uint InlineChatCommandId = 0x0300;

    private int _openedVersion;
    private string? _scratchPath;

    [IdeFact]
    public async Task InlineDifferenceView_EnterAppliesOneEditWithOneUndoStep()
    {
        await EnsurePackageLoadedAsync();
        var view = await OpenScratchDocumentAsync();
        try
        {
            var (scope, original) = await StartInlineChatAsync(view);
            var before = InlineChatChecks.UndoState(view);
            Output.WriteLine($"Undo history before Enter: {Describe(before)}");

            var prompt = await InlineChatChecks.WaitForPromptAsync(view, TimeSpan.FromSeconds(10), HangMitigatingCancellationToken);
            await JoinableTaskFactory.SwitchToMainThreadAsync(HangMitigatingCancellationToken);
            InlineChatChecks.PressKey(prompt, Key.Enter); // Empty prompt: Enter applies the proposal.

            var expected = original.Replace(scope, InlineChatChecks.CannedProposal(scope));
            Assert.Equal(expected, view.TextBuffer.CurrentSnapshot.GetText());
            Assert.False(InlineChatChecks.IsOpen(view), "The adornment is still open after Enter.");
            var after = InlineChatChecks.UndoState(view);
            Output.WriteLine($"Undo history after Enter: {Describe(after)}");
            if (before is { } b && after is { } a)
            {
                Assert.Equal(b.Count + 1, a.Count);
                Assert.Equal("Pillion inline edit", a.Top);
            }

            await TestServices.Shell.ExecuteCommandAsync("Edit.Undo", HangMitigatingCancellationToken);
            await JoinableTaskFactory.SwitchToMainThreadAsync(HangMitigatingCancellationToken);
            Assert.Equal(original, view.TextBuffer.CurrentSnapshot.GetText());
            Output.WriteLine("One Undo restored the original text.");
        }
        finally
        {
            await CloseScratchDocumentAsync();
        }
    }

    [IdeFact]
    public async Task InlineDifferenceView_EscDiscards_LeavingNothingToUndo()
    {
        await EnsurePackageLoadedAsync();
        var view = await OpenScratchDocumentAsync();
        try
        {
            var (_, original) = await StartInlineChatAsync(view);
            var before = InlineChatChecks.UndoState(view);

            var prompt = await InlineChatChecks.WaitForPromptAsync(view, TimeSpan.FromSeconds(10), HangMitigatingCancellationToken);
            await JoinableTaskFactory.SwitchToMainThreadAsync(HangMitigatingCancellationToken);
            InlineChatChecks.PressKey(prompt, Key.Escape);

            Assert.False(InlineChatChecks.IsOpen(view), "The adornment is still open after Esc.");
            Assert.Equal(original, view.TextBuffer.CurrentSnapshot.GetText());
            Assert.Equal(_openedVersion, view.TextBuffer.CurrentSnapshot.Version.VersionNumber);
            var after = InlineChatChecks.UndoState(view);
            Output.WriteLine($"Undo history before Esc: {Describe(before)}; after: {Describe(after)}");
            Assert.Equal(before?.Count, after?.Count);
        }
        finally
        {
            await CloseScratchDocumentAsync();
        }
    }

    private static string Describe((int Count, string? Top)? state) =>
        state is { } s ? $"{s.Count} unit(s), newest \"{s.Top}\"" : "no editor undo history";

    /// <summary>
    /// Selects the scratch document's method, runs Tools.PillionInlineChat with the canned inline edit service, sends an
    /// instruction and waits for the complete proposal in the difference view; the buffer must still be unchanged.
    /// </summary>
    private async Task<(string Scope, string Original)> StartInlineChatAsync(IWpfTextView view)
    {
        await JoinableTaskFactory.SwitchToMainThreadAsync(HangMitigatingCancellationToken);
        InlineChatChecks.InstallCannedEdits();
        var original = view.TextBuffer.CurrentSnapshot.GetText();
        var scope = InlineChatChecks.SelectLines(view, 4, 8);
        _openedVersion = view.TextBuffer.CurrentSnapshot.Version.VersionNumber;

        // The package loads in the background and adds its commands at the end of its initialization, which can still be
        // running when IsPackageLoaded says it is loaded: try the command until it is there. By name first; when the
        // shell does not know the name (an Experimental Instance whose menu cache came from an older copy of the
        // extension), by its command id through the shell's command dispatcher, which reaches the loaded package.
        var attempts = 0;
        await WaitUntilAsync(
            async () =>
            {
                try
                {
                    await TestServices.Shell.ExecuteCommandAsync("Tools.PillionInlineChat", HangMitigatingCancellationToken);
                    return true;
                }
                catch (System.Runtime.InteropServices.COMException ex)
                {
                    Output.WriteLine($"Tools.PillionInlineChat by name: {ex.Message}");
                }

                if (++attempts < 4)
                {
                    return false;
                }

                await JoinableTaskFactory.SwitchToMainThreadAsync(HangMitigatingCancellationToken);
                var dispatcher = await TestServices.Shell.GetRequiredGlobalServiceAsync<SUIHostCommandDispatcher, Microsoft.VisualStudio.OLE.Interop.IOleCommandTarget>(HangMitigatingCancellationToken);
                var group = InlineChatCommandSet;
                var hr = dispatcher.Exec(ref group, InlineChatCommandId, 0, IntPtr.Zero, IntPtr.Zero);
                Output.WriteLine($"Pillion: Inline Chat by command id: 0x{hr:X8}");
                return hr == 0;
            },
            TimeSpan.FromSeconds(30),
            "the Pillion: Inline Chat command").ConfigureAwait(true);
        var prompt = await InlineChatChecks.WaitForPromptAsync(view, TimeSpan.FromSeconds(30), HangMitigatingCancellationToken);
        await JoinableTaskFactory.SwitchToMainThreadAsync(HangMitigatingCancellationToken);
        InlineChatChecks.Send(prompt, "add a comment");
        await InlineChatChecks.WaitForPreviewAsync(view, TimeSpan.FromSeconds(30), HangMitigatingCancellationToken);

        await JoinableTaskFactory.SwitchToMainThreadAsync(HangMitigatingCancellationToken);
        Assert.Equal(original, view.TextBuffer.CurrentSnapshot.GetText());
        Assert.Equal(_openedVersion, view.TextBuffer.CurrentSnapshot.Version.VersionNumber);
        Output.WriteLine("The proposal is shown in the inline difference view and the buffer is untouched.");
        return (scope, original);
    }

    private async Task<IWpfTextView> OpenScratchDocumentAsync()
    {
        var folder = Path.Combine(Path.GetTempPath(), "pillion-ide-tests", "inline-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(folder);
        _scratchPath = Path.Combine(folder, "Orders.cs");
        File.WriteAllText(
            _scratchPath,
            string.Join(
                "\r\n",
                "namespace Scratch;",
                string.Empty,
                "internal sealed class Orders",
                "{",
                "    public void Place(int count)",
                "    {",
                "        if (count < 0) throw new System.ArgumentOutOfRangeException(nameof(count));",
                "        Count += count;",
                "    }",
                string.Empty,
                "    public int Count { get; private set; }",
                "}",
                string.Empty));

        await JoinableTaskFactory.SwitchToMainThreadAsync(HangMitigatingCancellationToken);
        VsShellUtilities.OpenDocument(ServiceProvider.GlobalProvider, _scratchPath);
        IWpfTextView? view = null;
        await WaitUntilAsync(
            async () =>
            {
                view = await TestServices.Editor.GetActiveTextViewAsync(HangMitigatingCancellationToken);
                return view.TextBuffer.CurrentSnapshot.GetText().Contains("class Orders");
            },
            TimeSpan.FromSeconds(30),
            "the scratch document to open").ConfigureAwait(true);
        Output.WriteLine($"Opened {_scratchPath}");
        return view!;
    }

    private async Task CloseScratchDocumentAsync()
    {
        await JoinableTaskFactory.SwitchToMainThreadAsync(HangMitigatingCancellationToken);
        InlineChatChecks.RemoveCannedEdits();
        if (_scratchPath is not null
            && VsShellUtilities.IsDocumentOpen(ServiceProvider.GlobalProvider, _scratchPath, Guid.Empty, out _, out _, out var frame)
            && frame is not null)
        {
            _ = frame.CloseFrame((uint)__FRAMECLOSE.FRAMECLOSE_NoSave);
        }

        try
        {
            if (_scratchPath is not null)
            {
                Directory.Delete(Path.GetDirectoryName(_scratchPath)!, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // %TEMP% is cleaned up eventually.
        }
    }
}
