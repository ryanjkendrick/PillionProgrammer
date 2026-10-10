using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Xunit.Abstractions;

namespace Pillion.VisualStudio.IntegrationTests;

/// <summary>
/// The in-editor paths of phase 1: the proposal source (work items 1.1 and 1.4), here, and the inline difference view
/// tests of work item 1.7 in AutocompleteAndInlineEditTests.InlineChat.cs. No model is needed: the ghost-text tests give
/// autocomplete a canned completion service (<see cref="AutocompleteChecks"/>), and whatever competitor the instance
/// has (Copilot on 18.x, IntelliCode on 17.x) stays enabled.
/// </summary>
[IdeSettings(MinVersion = IdeTarget.Version, MaxVersion = IdeTarget.Version)]
public sealed partial class AutocompleteAndInlineEditTests(ITestOutputHelper output) : PillionIdeTest(output)
{
    /// <summary>The message of IntelliCodeRequirement.Message in the extension.</summary>
    private const string IntelliCodeMessage = "Inline suggestions need IntelliCode's automatic code completions on Visual Studio 2022";

    private string? _ghostPath;

    [IdeFact]
    public async Task ProposalSource_ShowsGhostText_WithTheCompetitorEnabled()
    {
        await EnsurePackageLoadedAsync();
        var view = await OpenGhostDocumentAsync();
        ProposalWatch? watch = null;
        try
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync(HangMitigatingCancellationToken);
            watch = ProposalWatch.Start(view);
            Assert.True(watch is not null, "Visual Studio has no suggestion service in this instance.");
            var original = view.TextBuffer.CurrentSnapshot.GetText();

            // The last key goes through the view's command chain, as from the keyboard: the broker asks Pillion's source on TypeChar.
            await WaitForPillionProposalAsync(view, watch!, "Pillion's proposal after typing", () => TypeStatementPrefix(view, line: 6));

            await AcceptSuggestionAsync(view);
            var accepted = view.TextBuffer.CurrentSnapshot.GetLineFromLineNumber(6).GetText();
            Output.WriteLine($"After Tab: {accepted.Trim()}");
            Assert.Equal("        var total = " + AutocompleteChecks.CannedCompletion, accepted);
            Assert.NotEqual(original, view.TextBuffer.CurrentSnapshot.GetText());

            // Esc dismisses and leaves the buffer as it was.
            await WaitForPillionProposalAsync(view, watch!, "Pillion's proposal on the next line", () => TypeStatementPrefix(view, line: 7));
            await JoinableTaskFactory.SwitchToMainThreadAsync(HangMitigatingCancellationToken);
            var beforeEsc = view.TextBuffer.CurrentSnapshot.GetText();
            AutocompleteChecks.Exec(view, VSConstants.VSStd2KCmdID.CANCEL);
            await WaitUntilAsync(
                async () =>
                {
                    await JoinableTaskFactory.SwitchToMainThreadAsync(HangMitigatingCancellationToken);
                    return !AutocompleteChecks.IsPillionDisplayed(view);
                },
                TimeSpan.FromSeconds(5),
                "Esc to dismiss the proposal").ConfigureAwait(true);
            Assert.Equal(beforeEsc, view.TextBuffer.CurrentSnapshot.GetText());

            // "Pillion: Suggest" (work item 1.4) asks for one suggestion at the caret, whatever else is displayed.
            await WaitForPillionProposalAsync(view, watch!, "Pillion: Suggest to show a proposal", () => AutocompleteChecks.Suggest(view));
            Output.WriteLine("Typed, accepted with Tab, dismissed with Esc and asked again with Pillion: Suggest.");
        }
        finally
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync(HangMitigatingCancellationToken);
            watch?.Dispose();
            await CloseGhostDocumentAsync();
        }
    }

    [IdeFact]
    public async Task ProposalSource_NoProposal_WhileACompletionListItemIsSelected()
    {
        await EnsurePackageLoadedAsync();
        var view = await OpenGhostDocumentAsync();
        try
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync(HangMitigatingCancellationToken);
            view.Caret.MoveTo(view.TextSnapshot.GetLineFromLineNumber(6).End);
            // Typing a letter opens Roslyn's completion list with the best match selected.
            AutocompleteChecks.Type(view, "        Con");
            string? selected = null;
            await WaitUntilAsync(
                async () =>
                {
                    await JoinableTaskFactory.SwitchToMainThreadAsync(HangMitigatingCancellationToken);
                    selected = AutocompleteChecks.SelectedCompletionItem(view);
                    return selected is not null;
                },
                TimeSpan.FromSeconds(20),
                "a completion list with a selected item").ConfigureAwait(true);
            Output.WriteLine($"Completion list open with \"{selected}\" selected.");

            // The canned service answers in about 50 ms: check every 50 ms for two seconds while the list stays open.
            var checks = 0;
            for (var i = 0; i < 40; i++)
            {
                await JoinableTaskFactory.SwitchToMainThreadAsync(HangMitigatingCancellationToken);
                if (AutocompleteChecks.SelectedCompletionItem(view) is null)
                {
                    break;
                }

                checks++;
                Assert.False(AutocompleteChecks.IsPillionDisplayed(view), "A Pillion proposal was shown while a completion list had a selected item.");
                await Task.Delay(50, HangMitigatingCancellationToken).ConfigureAwait(true);
            }

            await JoinableTaskFactory.SwitchToMainThreadAsync(HangMitigatingCancellationToken);
            Output.WriteLine($"No Pillion proposal in {checks} checks ({checks * 50} ms) while the list had a selected item; displayed now: {AutocompleteChecks.DisplayedProposalId(view) ?? "nothing"}.");
            AutocompleteChecks.Exec(view, VSConstants.VSStd2KCmdID.CANCEL);
            Assert.True(checks >= 4, "The completion list closed before it could be checked for 200 ms.");
        }
        finally
        {
            await CloseGhostDocumentAsync();
        }
    }

    [IdeFact(MaxVersion = VisualStudioVersion.VS2022)]
    public async Task IntelliCodeCheck_ReportsWhenAutomaticCompletionsAreOff()
    {
        await EnsurePackageLoadedAsync();
        var previous = await AutocompleteChecks.SetIntelliCodeCompletionsAsync(2);
        Output.WriteLine($"IntelliCode automatic completions switched off (was {previous ?? "<unset>"}).");
        bool seenStatus = false, seenOutput = false;
        try
        {
            await WaitUntilAsync(
                async () =>
                {
                    // The check runs again whenever the Tools menu (here: the Open Chat command's status) is queried.
                    await JoinableTaskFactory.SwitchToMainThreadAsync(HangMitigatingCancellationToken);
                    QueryOpenChatStatus();
                    seenStatus |= AutocompleteChecks.StatusBarText().Contains(IntelliCodeMessage);
                    seenOutput |= (await AutocompleteChecks.PillionOutputTextAsync(HangMitigatingCancellationToken)).Contains(IntelliCodeMessage);
                    await JoinableTaskFactory.SwitchToMainThreadAsync(HangMitigatingCancellationToken);
                    return seenStatus && seenOutput;
                },
                TimeSpan.FromSeconds(30),
                "the status bar and the Output pane to report IntelliCode's completions off").ConfigureAwait(true);
            await JoinableTaskFactory.SwitchToMainThreadAsync(HangMitigatingCancellationToken);
            Output.WriteLine("Status bar: " + AutocompleteChecks.StatusBarText());
        }
        finally
        {
            await AutocompleteChecks.SetIntelliCodeCompletionsAsync(previous is int value ? value : 0);
            await JoinableTaskFactory.SwitchToMainThreadAsync(HangMitigatingCancellationToken);
            Output.WriteLine($"Seen in the status bar: {seenStatus}; in the Output pane: {seenOutput}; status bar now: {AutocompleteChecks.StatusBarText()}");
        }

        await WaitUntilAsync(
            async () =>
            {
                await JoinableTaskFactory.SwitchToMainThreadAsync(HangMitigatingCancellationToken);
                QueryOpenChatStatus();
                return !AutocompleteChecks.StatusBarText().Contains(IntelliCodeMessage);
            },
            TimeSpan.FromSeconds(20),
            "the status bar to clear once the completions are on again").ConfigureAwait(true);
    }

    /// <summary>
    /// Accepts the displayed proposal the way Tab does: Visual Studio 18 binds Tab to <c>Edit.AcceptSuggestion</c> in the
    /// "Inline Suggestions Active" scope; Visual Studio 2022 (IntelliCode) handles the Tab key command itself.
    /// </summary>
    private async Task AcceptSuggestionAsync(IWpfTextView view)
    {
        try
        {
            await TestServices.Shell.ExecuteCommandAsync("Edit.AcceptSuggestion", HangMitigatingCancellationToken);
            Output.WriteLine("Accepted with Edit.AcceptSuggestion.");
        }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            Output.WriteLine($"Edit.AcceptSuggestion: {ex.Message}; sending Tab.");
            await JoinableTaskFactory.SwitchToMainThreadAsync(HangMitigatingCancellationToken);
            AutocompleteChecks.Exec(view, VSConstants.VSStd2KCmdID.TAB);
        }

        await JoinableTaskFactory.SwitchToMainThreadAsync(HangMitigatingCancellationToken);
    }

    private static void QueryOpenChatStatus()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (ServiceProvider.GlobalProvider.GetService(typeof(EnvDTE.DTE)) is EnvDTE.DTE dte)
        {
            _ = dte.Commands.Item("Tools.PillionOpenChat").IsAvailable;
        }
    }

    /// <summary>
    /// Puts "        var total = " at the end of a line, as typing would leave it, with the last key typed: the text before the
    /// final space goes in as an edit and the space through the view's command chain. Typing the letters opens Roslyn's
    /// completion list, and Pillion shows nothing while a list is open (see the next test), so how long the list lingered
    /// on a loaded runner decided whether this test passed. A space opens no list; the proposal broker still sees a TypeChar.
    /// </summary>
    private static void TypeStatementPrefix(IWpfTextView view, int line)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        const string beforeTheLastKey = "        var total =";
        var end = view.TextSnapshot.GetLineFromLineNumber(line).End.Position;
        using (var edit = view.TextBuffer.CreateEdit())
        {
            _ = edit.Insert(end, beforeTheLastKey);
            _ = edit.Apply();
        }

        view.Caret.MoveTo(new SnapshotPoint(view.TextSnapshot, end + beforeTheLastKey.Length));
        AutocompleteChecks.Type(view, " ");
    }

    /// <summary>
    /// Runs <paramref name="keys"/> on the UI thread and waits for Pillion's proposal to be displayed in the view: for the
    /// suggestion service's <c>ProposalDisplayed</c> event (or a Pillion proposal that is already displayed and still
    /// matches). A timeout says what the editor and Pillion's adapter did instead: whether Pillion's source reached the
    /// model, what is displayed, the completion list.
    /// </summary>
    private async Task WaitForPillionProposalAsync(IWpfTextView view, ProposalWatch watch, string what, Action keys)
    {
        var timeout = TimeSpan.FromSeconds(20);
        await JoinableTaskFactory.SwitchToMainThreadAsync(HangMitigatingCancellationToken);
        var shown = watch.WhenNextPillionShownAsync();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        keys();
        while (!shown.IsCompleted)
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync(HangMitigatingCancellationToken);
            if (AutocompleteChecks.IsPillionDisplayed(view))
            {
                break;
            }

            if (clock.Elapsed > timeout)
            {
                var state = AutocompleteChecks.DescribeState(view);
                var broker = await AutocompleteChecks.ProbeBrokerAsync(view, TimeSpan.FromSeconds(8), HangMitigatingCancellationToken);
                throw new TimeoutException(
                    $"Timed out after {timeout.TotalSeconds:0} s waiting for {what}. {state}; {watch.Describe()}; {broker}.");
            }

            _ = await Task.WhenAny(shown, Task.Delay(250, HangMitigatingCancellationToken)).ConfigureAwait(true);
        }

        await JoinableTaskFactory.SwitchToMainThreadAsync(HangMitigatingCancellationToken);
        Output.WriteLine($"{what}: displayed after {clock.ElapsedMilliseconds} ms ({AutocompleteChecks.CannedRequestCount} canned request(s) so far; {watch.Describe()}).");
    }

    private async Task<IWpfTextView> OpenGhostDocumentAsync()
    {
        await JoinableTaskFactory.SwitchToMainThreadAsync(HangMitigatingCancellationToken);
        AutocompleteChecks.InstallCannedCompletions();
        var folder = Path.Combine(Path.GetTempPath(), "pillion-ide-tests", "ghost-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(folder);
        _ghostPath = Path.Combine(folder, "Totals.cs");
        File.WriteAllText(
            _ghostPath,
            string.Join(
                "\r\n",
                "namespace Scratch;",
                string.Empty,
                "internal static class Totals",
                "{",
                "    public static void Print(int[] numbers)",
                "    {",
                string.Empty,
                string.Empty,
                "    }",
                "}",
                string.Empty));

        VsShellUtilities.OpenDocument(ServiceProvider.GlobalProvider, _ghostPath);
        IWpfTextView? view = null;
        await WaitUntilAsync(
            async () =>
            {
                view = await TestServices.Editor.GetActiveTextViewAsync(HangMitigatingCancellationToken);
                return view.TextBuffer.CurrentSnapshot.GetText().Contains("class Totals");
            },
            TimeSpan.FromSeconds(30),
            "the ghost-text document to open").ConfigureAwait(true);

        // The broker serves the focused view: another test may have left a tool window (Pillion Chat) focused.
        await WaitUntilAsync(
            async () =>
            {
                await JoinableTaskFactory.SwitchToMainThreadAsync(HangMitigatingCancellationToken);
                _ = view!.VisualElement.Focus();
                return view.HasAggregateFocus;
            },
            TimeSpan.FromSeconds(10),
            "the ghost-text document to get keyboard focus").ConfigureAwait(true);
        Output.WriteLine($"Opened {_ghostPath}");
        return view!;
    }

    private async Task CloseGhostDocumentAsync()
    {
        await JoinableTaskFactory.SwitchToMainThreadAsync(HangMitigatingCancellationToken);
        AutocompleteChecks.RemoveCannedCompletions();
        if (_ghostPath is not null
            && VsShellUtilities.IsDocumentOpen(ServiceProvider.GlobalProvider, _ghostPath, Guid.Empty, out _, out _, out var frame)
            && frame is not null)
        {
            _ = frame.CloseFrame((uint)__FRAMECLOSE.FRAMECLOSE_NoSave);
        }

        try
        {
            if (_ghostPath is not null)
            {
                Directory.Delete(Path.GetDirectoryName(_ghostPath)!, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // %TEMP% is cleaned up eventually.
        }
    }
}
