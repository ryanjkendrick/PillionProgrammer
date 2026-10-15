using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;
using Pillion.Contracts;
using Pillion.VisualStudio.Core;
using Pillion.VisualStudio.Core.Mentions;
using Pillion.VisualStudio.Core.Signals;

namespace Pillion.VisualStudio.Tests;

// The tests await their own tasks and the view models' tasks; no main thread is involved.
#pragma warning disable VSTHRD003

/// <summary>The picker's new kinds, grouping and options (work item 2.13).</summary>
public sealed class MentionPickerV2Tests
{
    private static readonly WorkspaceFileList Files = new(
        [@"C:\repo"],
        [@"C:\repo\Program.cs", @"C:\repo\src\Orders.cs", @"C:\repo\docs\guide.md", @"C:\repo\README.md", @"C:\repo\notes.txt"]);

    [Fact]
    public void Results_AreGroupedByKind_WithAHeadingOnTheFirstEntryOfEachGroup()
    {
        var picker = new MentionPickerViewModel();

        picker.Update("@o", 2, Files);

        var kinds = picker.Items.TakeWhile(i => i.ItemKind == MentionPickerItemKind.Kind).ToList();
        var files = picker.Items.SkipWhile(i => i.ItemKind == MentionPickerItemKind.Kind).ToList();
        Assert.Equal(["@output"], kinds.Select(k => k.Label)); // the kinds that start with "o"
        Assert.NotEmpty(files);
        Assert.All(files, f => Assert.Equal(MentionPickerItemKind.File, f.ItemKind));
        Assert.True(kinds[0].IsFirstInGroup);
        Assert.Equal(ChatStrings.MentionGroupKinds, kinds[0].Group);
        Assert.True(files[0].IsFirstInGroup);
        Assert.Equal(ChatStrings.MentionGroupFiles, files[0].Group);
        Assert.All(files.Skip(1), f => Assert.False(f.IsFirstInGroup));
        Assert.StartsWith(ChatStrings.MentionGroupFiles + ": ", files[0].AutomationName, StringComparison.Ordinal);
    }

    [Fact]
    public void Git_ListsItsOptions_AndACountNamesTheLog()
    {
        var picker = new MentionPickerViewModel();

        picker.Update("@git:", 5, Files);
        var options = picker.Items.Select(i => i.MentionText).ToList();
        picker.Update("@git:log:5", 10, Files);

        Assert.Equal(["@git", "@git:staged", "@git:log"], options);
        var log = Assert.Single(picker.Items);
        Assert.Equal("@git:log:5", log.MentionText);
        Assert.Equal(ChatStrings.Format(ChatStrings.MentionGitLogFormat, 5), log.Detail);
        Assert.Equal(ChatStrings.MentionGroupGit, log.Group);
    }

    [Fact]
    public void Output_ListsTheThreePanes_FilteredByWhatIsTyped()
    {
        var picker = new MentionPickerViewModel();

        picker.Update("@output:", 8, Files);
        var all = picker.Items.Select(i => i.MentionText).ToList();
        picker.Update("@output:de", 10, Files);

        Assert.Equal(["@output:build", "@output:debug", "@output:tests"], all);
        Assert.Equal("@output:debug", Assert.Single(picker.Items).MentionText);
        Assert.Equal(ChatStrings.MentionPickerOutputHint, new MentionPickerViewModel().Also(p => p.Update("@output:", 8, Files)).Title);
    }

    [Fact]
    public void Docs_ListsTheMarkdownAndTextFilesOfTheSolution_AndTheTypedName()
    {
        var picker = new MentionPickerViewModel();

        picker.Update("@docs:gui", 9, Files);

        Assert.Equal(["guide.md", "gui"], picker.Items.Select(i => i.Label));
        Assert.Equal(@"@docs:C:\repo\docs\guide.md", picker.Items[0].MentionText);
        Assert.Equal(MentionPickerItemKind.Option, picker.Items[1].ItemKind);
        Assert.Equal("@docs:gui", picker.Items[1].MentionText);
        Assert.True(picker.WantsHostItems);
    }

    [Fact]
    public void HostSymbols_AreAddedUnderTheirHeading_ButNotForAMentionTypedSince()
    {
        var picker = new MentionPickerViewModel();
        picker.Update("@symbol:Gre", 11, Files);
        var token = picker.Token!;
        var symbols = new[]
        {
            MentionPickerViewModel.SymbolItem(MentionPickerItemKind.Symbol, @"Shop.Greeter.Greet#C:\repo\src\Greeter.cs:12", Files.Roots),
            MentionPickerViewModel.SymbolItem(MentionPickerItemKind.Symbol, @"Shop.Greeter.GreetAll#C:\repo\src\Greeter.cs:20", Files.Roots),
        };

        Assert.True(picker.SetHostItems(token, symbols));
        Assert.Equal(["Gre", "Greet", "GreetAll"], picker.Items.Select(i => i.Label));
        Assert.Equal("Shop.Greeter.Greet, src/Greeter.cs:12", picker.Items[1].Detail);
        Assert.Equal(@"@symbol:Shop.Greeter.Greet#C:\repo\src\Greeter.cs:12", picker.Items[1].MentionText);
        Assert.True(picker.Items[0].IsFirstInGroup);
        Assert.False(picker.Items[1].IsFirstInGroup); // the typed name and the host's symbols share the Symbols heading

        picker.Update("@symbol:Greeting", 16, Files);
        Assert.False(picker.SetHostItems(token, symbols));
        Assert.Equal(["Greeting"], picker.Items.Select(i => i.Label)); // kept host entries must still match
    }

    [Fact]
    public void AnAmbiguousSymbol_ShowsItsCandidatesByName()
    {
        var picker = new MentionPickerViewModel();

        picker.ShowCandidates("symbol", "@symbol:Greet", [@"A.Greeter.Greet#C:\repo\a\Greeter.cs:5", @"B.Greeter.Greet#C:\repo\b\Greeter.vb:9"], Files.Roots);

        Assert.True(picker.IsCandidates);
        Assert.Equal(ChatStrings.Format(ChatStrings.MentionSymbolCandidatesTitleFormat, "@symbol:Greet"), picker.Title);
        Assert.Equal(["Greet", "Greet"], picker.Items.Select(i => i.Label));
        Assert.Equal(["A.Greeter.Greet, a/Greeter.cs:5", "B.Greeter.Greet, b/Greeter.vb:9"], picker.Items.Select(i => i.Detail));
    }

    [Theory]
    [InlineData("@git:log:5", new[] { "log", "5" })]
    [InlineData("@git:staged", new[] { "staged" })]
    [InlineData("@git", new string[0])]
    [InlineData("@output:debug", new[] { "debug" })]
    [InlineData("@symbol:A.B", new string[0])]
    public void ArgumentsOf_TheOptionKinds(string mention, string[] expected) =>
        Assert.Equal(expected, MentionSyntax.ArgumentsOf(MentionSyntax.Parse(mention)));
}

/// <summary>The mentions view model's new kinds against a fake host (work item 2.13).</summary>
public sealed class MentionsViewModelV2Tests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private static readonly WorkspaceFileList List = new([@"C:\src"], [@"C:\src\Program.cs", @"C:\src\README.md"]);

    private readonly JoinableTaskContext _context = Task.Run(() => new JoinableTaskContext()).Result;
    private readonly FakeMentionService _service = new();
    private readonly FakeWorkspaceFiles _files = new(List);
    private readonly FakeEditorContext _editor = new();
    private readonly ChatConversation _conversation = new();
    private readonly FakeHostConnection _connection;
    private readonly List<MentionInputEditEventArgs> _edits = [];

    public MentionsViewModelV2Tests()
    {
        _connection = new FakeHostConnection().With<IContextService>(_service);
    }

    public void Dispose() => _context.Dispose();

    [Fact]
    public async Task TypingAfterSymbol_AsksTheHost_AndPickingACandidatePinsItWithItsCitations()
    {
        _service.Resolve = r => r.Mention.Contains('#')
            ? Symbol(r, "Greet", @"C:\src\Greeter.cs", 11)
            : new MentionResolution { Mention = r.Mention, Kind = "symbol", Candidates = [@"Shop.Greeter.Greet#C:\src\Greeter.cs:12", @"Shop.Vb.Greeter.Greet#C:\src\Greeter.vb:27"] };
        using var vm = Create();

        vm.UpdateInput("@symbol:Gre", 11);
        await vm.WhenSuggestionsDoneAsync().WithTimeout(Timeout);

        var asked = Assert.Single(_service.Requests);
        Assert.Equal(("@symbol:Gre", "symbol", "symbol"), (asked.Mention, asked.Kind, asked.Qualifier));
        Assert.Empty(asked.CandidateFiles);
        Assert.Equal([@"C:\src"], asked.WorkspaceRoots);
        var symbol = vm.Picker.Items.First(i => i.ItemKind == MentionPickerItemKind.Symbol);
        Assert.Equal("Greet", symbol.Label);

        await vm.CommitAsync(symbol).WithTimeout(Timeout);

        Assert.Equal(2, _service.Requests.Count);
        var pinned = _service.Requests.Last();
        Assert.Equal(@"@symbol:Shop.Greeter.Greet#C:\src\Greeter.cs:12", pinned.Mention);
        var pin = Assert.Single(_conversation.Pins);
        Assert.Equal("Greet", pin.DisplayName);
        Assert.Equal(11, Assert.Single(pin.Resolution.Citations).StartLine);
        Assert.Equal(string.Empty, Assert.Single(_edits).Apply("@symbol:Gre"));
    }

    [Fact]
    public async Task AUniqueMatchWhileTyping_IsPinnedWithoutAskingAgain()
    {
        _service.Resolve = r => Symbol(r, "GreetAll", @"C:\src\Greeter.cs", 19);
        using var vm = Create();
        vm.UpdateInput("@symbol:GreetA", 14);
        await vm.WhenSuggestionsDoneAsync().WithTimeout(Timeout);
        var item = Assert.Single(vm.Picker.Items);
        Assert.Equal("GreetAll", item.Label);

        await vm.CommitAsync(item).WithTimeout(Timeout);

        Assert.Single(_service.Requests);
        Assert.Equal("GreetAll", Assert.Single(_conversation.Pins).DisplayName);
    }

    [Fact]
    public async Task PickingGit_OpensItsOptions_AndALogSendsItsArguments()
    {
        _service.Resolve = r => FakeMentionService.Resolved(r, "Git: last 5 commits", null, "commit ...", 50, "git");
        using var vm = Create();
        vm.UpdateInput("@gi", 3);

        await vm.CommitAsync(vm.Picker.Items.Single(i => i.Label == "@git")).WithTimeout(Timeout);
        Assert.Equal("@git:", Assert.Single(_edits).Apply("@gi"));
        Assert.Empty(_service.Requests);

        vm.UpdateInput("@git:log:5", 10);
        await vm.CommitAsync(Assert.Single(vm.Picker.Items)).WithTimeout(Timeout);

        var request = Assert.Single(_service.Requests);
        Assert.Equal(("@git:log:5", "git", "git"), (request.Mention, request.Kind, request.Qualifier));
        Assert.Equal(["log", "5"], request.Arguments);
        Assert.Empty(request.CandidateFiles);
        Assert.Null(request.Payload);
    }

    [Fact]
    public async Task TwoGitPins_Coexist_ButTheSameOneIsReplaced()
    {
        _service.Resolve = r => FakeMentionService.Resolved(r, r.Mention, null, "diff", 10, "git");
        using var vm = Create();

        await vm.ResolveTextAsync("@git").WithTimeout(Timeout);
        await vm.ResolveTextAsync("@git:staged").WithTimeout(Timeout);
        await vm.ResolveTextAsync("@git:staged").WithTimeout(Timeout);

        Assert.Equal(["@git", "@git:staged"], _conversation.Pins.Select(p => p.DisplayName));
    }

    [Theory]
    [InlineData("@output:debug", "output", "Debug")]
    [InlineData("@output", "output", "Build")]
    [InlineData("@build", "build", null)]
    [InlineData("@tests", "tests", null)]
    public async Task TheIdeKinds_SendWhatVisualStudioHasAsThePayload(string mention, string kind, string? pane)
    {
        _editor.Panes["Debug"] = "debug pane text";
        _editor.Panes["Build"] = "build pane text";
        _editor.LastBuild = "Build failed: 1 project in 2.0 s\nerror CS1002";
        _editor.TestResults = "Failed OrderTests.Totals";
        _service.Resolve = r => FakeMentionService.Resolved(r, kind, null, r.Payload!, 4, kind);
        using var vm = Create();

        await vm.ResolveTextAsync(mention).WithTimeout(Timeout);

        var request = Assert.Single(_service.Requests);
        Assert.Equal(kind, request.Kind);
        var expected = kind switch
        {
            "build" => _editor.LastBuild,
            "tests" => _editor.TestResults,
            _ => _editor.Panes[pane!],
        };
        Assert.Equal(expected, request.Payload);
        Assert.Equal(pane is null ? Array.Empty<string>() : [pane], _editor.PanesAsked);
        Assert.Equal(kind, Assert.Single(_conversation.Pins).Kind);
    }

    [Fact]
    public async Task RestoredSymbolAndGitPins_AreReadAgain_ButOutputPinsStayStale()
    {
        _service.Resolve = r => r.Kind == "symbol" ? Symbol(r, "Greet", @"C:\src\Greeter.cs", 11) : FakeMentionService.Resolved(r, "Git: working tree", null, "diff", 9, "git");
        using var vm = Create();

        await vm.RestorePinsAsync(_conversation,
        [
            new ConversationPin { Kind = "symbol", Target = @"Shop.Greeter.Greet#C:\src\Greeter.cs:12", DisplayName = "Greet", TokenCount = 30 },
            new ConversationPin { Kind = "git", Target = string.Empty, DisplayName = "Git: working tree", TokenCount = 9 },
            new ConversationPin { Kind = "output", Target = "debug", DisplayName = "Output: Debug", TokenCount = 5 },
        ]).WithTimeout(Timeout);

        Assert.Equal(["symbol", "git"], _service.Requests.Select(r => r.Kind));
        Assert.Equal(@"@symbol:Shop.Greeter.Greet#C:\src\Greeter.cs:12", _service.Requests.First().Mention);
        Assert.Equal([false, false, true], _conversation.Pins.Select(p => p.IsStale));
    }

    private static MentionResolution Symbol(MentionRequest request, string name, string path, int line)
    {
        var resolution = FakeMentionService.Resolved(request, name, path, "class X { }", 30, "symbol");
        resolution.Citations = [new Citation { FilePath = path, StartLine = line, EndLine = line + 3 }];
        return resolution;
    }

    private MentionsViewModel Create()
    {
        var vm = new MentionsViewModel(_connection, _context.Factory, _conversation, _files, _editor) { SuggestionDelay = TimeSpan.Zero };
        vm.InputEditRequested += (_, e) => _edits.Add(e);
        return vm;
    }
}

/// <summary>What the VSIX keeps for <c>@build</c> and <c>@tests</c> (work item 2.13).</summary>
public sealed class MentionSignalsTests
{
    [Fact]
    public async Task TheHub_KeepsTheLastBuild_WithTheBuildPanesTailCapturedWhenItFinished()
    {
        var clock = new FakeClock();
        using var hub = new IdeSignalsHub(clock, null) { Output = new FakeOutput("1>Order.cs(3,1): error CS1002\nBuild failed.") };
        Assert.Null(hub.LastBuild);

        hub.NotifyBuildStarted();
        hub.NotifyProjectBuildStarted();
        clock.Advance(TimeSpan.FromSeconds(2));
        hub.NotifyBuildFinished(success: false);

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (hub.LastBuild?.Output is null && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        var build = hub.LastBuild!;
        Assert.False(build.Success);
        Assert.Equal(1, build.ProjectCount);
        Assert.Equal(TimeSpan.FromSeconds(2), build.Duration);
        Assert.Equal("1>Order.cs(3,1): error CS1002\nBuild failed.", build.Output);
        Assert.StartsWith("Build failed: 1 project in 2.0 s, finished ", build.Summary(), StringComparison.Ordinal);
    }

    [Fact]
    public void TheTestsPane_KeepsTheFailureBlocksAndTheSummary()
    {
        var pane = string.Join(
            "\n",
            "Building Test Projects",
            "Starting test discovery",
            "Passed OrderTests.Adds [2 ms]",
            "Failed OrderTests.Totals [12 ms]",
            "  Error Message:",
            "   Assert.Equal() Failure",
            "  Stack Trace:",
            "     at OrderTests.Totals() in C:\\src\\OrderTests.cs:line 42",
            "Passed OrderTests.Removes [1 ms]",
            "========== Test run finished: 3 Tests (2 Passed, 1 Failed, 0 Skipped) run in 1.2 sec ==========");

        var kept = TestOutput.FailuresOrTail(pane);

        Assert.Equal(
            string.Join(
                "\n",
                "Failed OrderTests.Totals [12 ms]",
                "  Error Message:",
                "   Assert.Equal() Failure",
                "  Stack Trace:",
                "     at OrderTests.Totals() in C:\\src\\OrderTests.cs:line 42",
                "========== Test run finished: 3 Tests (2 Passed, 1 Failed, 0 Skipped) run in 1.2 sec =========="),
            kept);
    }

    [Fact]
    public void TheTestsPane_WithoutFailures_IsKeptWhole()
    {
        const string pane = "Passed OrderTests.Adds [2 ms]\n========== Test run finished: 1 Tests (1 Passed) ==========";

        Assert.Equal(pane, TestOutput.FailuresOrTail(pane));
    }
}

/// <summary>A small helper for inline setup in assertions.</summary>
internal static class TestExtensions
{
    public static T Also<T>(this T value, Action<T> action)
    {
        action(value);
        return value;
    }
}
