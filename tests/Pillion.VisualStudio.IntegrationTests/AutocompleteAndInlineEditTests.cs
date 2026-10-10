using System.Threading.Tasks;
using Xunit.Abstractions;

namespace Pillion.VisualStudio.IntegrationTests;

/// <summary>
/// The in-editor paths of phase 1: placeholders for the proposal source (work item 1.1), named and skipped so the gate's
/// evidence list is visible in every test run, and the inline difference view tests of work item 1.7 in
/// AutocompleteAndInlineEditTests.InlineChat.cs. Work item 1.1 replaces each placeholder body with the steps in its
/// comment, written against its public types, and removes the Skip.
/// </summary>
[IdeSettings(MinVersion = IdeTarget.Version, MaxVersion = IdeTarget.Version)]
public sealed partial class AutocompleteAndInlineEditTests(ITestOutputHelper output) : PillionIdeTest(output)
{
    private const string WaitingFor1_1 = "Waiting for work item 1.1 (proposal source).";

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
}
