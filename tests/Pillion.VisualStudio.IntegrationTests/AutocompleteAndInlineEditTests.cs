using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
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
        try
        {
            // Typed through the view's command chain, as from the keyboard: the broker asks Pillion's source on TypeChar.
            var original = await TypeStatementPrefixAsync(view, line: 6);
            await WaitForPillionProposalAsync(view, "Pillion's proposal after typing");

            await JoinableTaskFactory.SwitchToMainThreadAsync(HangMitigatingCancellationToken);
            AutocompleteChecks.Exec(view, VSConstants.VSStd2KCmdID.TAB);
            var accepted = view.TextBuffer.CurrentSnapshot.GetLineFromLineNumber(6).GetText();
            Output.WriteLine($"After Tab: {accepted.Trim()}");
            Assert.Equal("        var total = " + AutocompleteChecks.CannedCompletion, accepted);
            Assert.NotEqual(original, view.TextBuffer.CurrentSnapshot.GetText());

            // Esc dismisses and leaves the buffer as it was.
            await TypeStatementPrefixAsync(view, line: 7);
            await WaitForPillionProposalAsync(view, "Pillion's proposal on the next line");
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
            AutocompleteChecks.Suggest(view);
            await WaitForPillionProposalAsync(view, "Pillion: Suggest to show a proposal");
            Output.WriteLine("Typed, accepted with Tab, dismissed with Esc and asked again with Pillion: Suggest.");
        }
        finally
        {
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
            AutocompleteChecks.Type(view, "        Cons");
            AutocompleteChecks.Exec(view, VSConstants.VSStd2KCmdID.COMPLETEWORD);
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

            // The canned service answers in about 50 ms; give the editor two seconds to (not) show anything.
            var checks = 0;
            for (var i = 0; i < 8; i++)
            {
                await Task.Delay(250, HangMitigatingCancellationToken).ConfigureAwait(true);
                await JoinableTaskFactory.SwitchToMainThreadAsync(HangMitigatingCancellationToken);
                if (!AutocompleteChecks.IsCompletionActive(view))
                {
                    break;
                }

                checks++;
                Assert.False(AutocompleteChecks.IsPillionDisplayed(view), "A Pillion proposal was shown while a completion list had a selected item.");
            }

            AutocompleteChecks.Exec(view, VSConstants.VSStd2KCmdID.CANCEL);
            Output.WriteLine($"No Pillion proposal in {checks} checks while the list had a selected item.");
            Assert.True(checks > 0, "The completion list closed before it could be checked.");
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
        try
        {
            await WaitUntilAsync(
                async () =>
                {
                    // The check runs again whenever the Tools menu (here: the Open Chat command's status) is queried.
                    await JoinableTaskFactory.SwitchToMainThreadAsync(HangMitigatingCancellationToken);
                    QueryOpenChatStatus();
                    return AutocompleteChecks.StatusBarText().Contains(IntelliCodeMessage)
                        && AutocompleteChecks.PillionOutputText().Contains(IntelliCodeMessage);
                },
                TimeSpan.FromSeconds(20),
                "the status bar and the Output pane to report IntelliCode's completions off").ConfigureAwait(true);
            await JoinableTaskFactory.SwitchToMainThreadAsync(HangMitigatingCancellationToken);
            Output.WriteLine("Status bar: " + AutocompleteChecks.StatusBarText());
        }
        finally
        {
            await AutocompleteChecks.SetIntelliCodeCompletionsAsync(previous is int value ? value : 0);
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

    private static void QueryOpenChatStatus()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (ServiceProvider.GlobalProvider.GetService(typeof(EnvDTE.DTE)) is EnvDTE.DTE dte)
        {
            _ = dte.Commands.Item("Tools.PillionOpenChat").IsAvailable;
        }
    }

    private async Task<string> TypeStatementPrefixAsync(IWpfTextView view, int line)
    {
        await JoinableTaskFactory.SwitchToMainThreadAsync(HangMitigatingCancellationToken);
        view.Caret.MoveTo(view.TextSnapshot.GetLineFromLineNumber(line).End);
        var original = view.TextBuffer.CurrentSnapshot.GetText();
        AutocompleteChecks.Type(view, "        var total = ");
        return original;
    }

    private Task WaitForPillionProposalAsync(IWpfTextView view, string what) =>
        WaitUntilAsync(
            async () =>
            {
                await JoinableTaskFactory.SwitchToMainThreadAsync(HangMitigatingCancellationToken);
                var id = AutocompleteChecks.DisplayedProposalId(view);
                if (id is not null && !AutocompleteChecks.IsPillionDisplayed(view))
                {
                    Output.WriteLine($"Another provider's proposal is displayed: {id}");
                }

                return AutocompleteChecks.IsPillionDisplayed(view);
            },
            TimeSpan.FromSeconds(20),
            what);

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
