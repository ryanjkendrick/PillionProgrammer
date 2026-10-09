using System.Threading.Tasks;
using Xunit.Abstractions;

namespace Pillion.VisualStudio.IntegrationTests;

/// <summary>
/// Placeholders for the in-editor paths of phase 1: the proposal source (work item 1.1) and the inline difference view
/// (work item 1.7). They are named and skipped now so the gate's evidence list is visible in every test run; the work
/// items replace each body with the steps in its comment, written against their public types, and remove the Skip.
/// </summary>
[IdeSettings(MinVersion = IdeTarget.Version, MaxVersion = IdeTarget.Version)]
public sealed class AutocompleteAndInlineEditTests(ITestOutputHelper output) : PillionIdeTest(output)
{
    private const string WaitingFor1_1 = "Waiting for work item 1.1 (proposal source).";
    private const string WaitingFor1_7 = "Waiting for work item 1.7 (inline difference view and apply).";

    [IdeFact(Skip = WaitingFor1_1)]
    public async Task ProposalSource_ShowsGhostText_WithTheCompetitorEnabled()
    {
        // 1.1: open the scratch folder's Program.cs, place the caret inside Main, type a statement prefix and wait for
        // the suggestion session; assert that the shown proposal comes from Pillion's proposal source (its provider
        // name or proposal id prefix), with IntelliCode's automatic completions on (17.x) or Copilot on (18.x).
        // Tab accepts it as one edit; Esc dismisses it and leaves the buffer unchanged.
        await EnsurePackageLoadedAsync();
    }

    [IdeFact(Skip = WaitingFor1_1)]
    public async Task ProposalSource_NoProposal_WhileACompletionListItemIsSelected()
    {
        // 1.1: trigger the completion list (Ctrl+Space), select an item, and assert Pillion offers no proposal until
        // the list is dismissed (Gate 1, "ghost text works").
        await EnsurePackageLoadedAsync();
    }

    [IdeFact(Skip = WaitingFor1_1, MaxVersion = VisualStudioVersion.VS2022)]
    public async Task IntelliCodeCheck_ReportsWhenAutomaticCompletionsAreOff()
    {
        // 1.1, 17.x only: turn IntelliCode's automatic completions off, reload, and assert the status bar and the
        // Pillion Output pane say so and nothing throws (Gate 1, "IntelliCode run-time check on 17.x").
        await EnsurePackageLoadedAsync();
    }

    [IdeFact(Skip = WaitingFor1_7)]
    public async Task InlineDifferenceView_EnterAppliesOneEditWithOneUndoStep()
    {
        // 1.7: select a method body, start inline chat with a canned edit from a fake inline-edit service, wait for
        // the inline difference view; press Enter and assert the buffer holds the edit, applied as a single
        // ITextEdit, and that one Undo restores the original text.
        await EnsurePackageLoadedAsync();
    }

    [IdeFact(Skip = WaitingFor1_7)]
    public async Task InlineDifferenceView_EscDiscards_LeavingNothingToUndo()
    {
        // 1.7: as above, then press Esc; assert the buffer is unchanged, the difference view is closed and the undo
        // stack has no new entry.
        await EnsurePackageLoadedAsync();
    }
}
