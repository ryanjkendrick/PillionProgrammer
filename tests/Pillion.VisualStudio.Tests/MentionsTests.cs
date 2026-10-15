using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;
using Newtonsoft.Json.Linq;
using Pillion.Contracts;
using Pillion.VisualStudio.Core;
using Pillion.VisualStudio.Core.Mentions;
using Pillion.VisualStudio.Core.Signals;
using IdeDiagnostic = Pillion.VisualStudio.Core.Signals.DiagnosticInfo;

namespace Pillion.VisualStudio.Tests;

// The tests await their own tasks and the view models' tasks; no main thread is involved.
#pragma warning disable VSTHRD003

public sealed class MentionSyntaxTests
{
    [Theory]
    [InlineData("@Customer.cs", "", "Customer.cs", false)]
    [InlineData("@folder:src/Api", "folder", "src/Api", true)]
    [InlineData("@file:Program.cs", "file", "Program.cs", true)]
    [InlineData("@FILE:Program.cs", "file", "Program.cs", true)]
    [InlineData("@errors", "errors", "", false)]
    [InlineData("@selection", "selection", "", false)]
    [InlineData("@clipboard", "clipboard", "", false)]
    [InlineData("@file", "", "file", false)]
    [InlineData(@"@C:\src\A.cs", "", @"C:\src\A.cs", false)]
    [InlineData("@symbol:OrderService", "", "symbol:OrderService", false)]
    [InlineData("Customer.cs", "", "Customer.cs", false)]
    public void Parse_ReadsNameAndKindValue(string text, string kind, string value, bool separator)
    {
        var parsed = MentionSyntax.Parse(text);

        Assert.Equal(kind, parsed.Kind);
        Assert.Equal(value, parsed.Value);
        Assert.Equal(separator, parsed.HasKindSeparator);
    }

    [Fact]
    public void FindAt_FindsTheMentionBeingTyped()
    {
        var token = MentionSyntax.FindAt("explain @Prog", 13);

        Assert.NotNull(token);
        Assert.Equal(8, token!.Start);
        Assert.Equal(5, token.Length);
        Assert.Equal("Prog", token.Query);
        Assert.Equal("Prog", token.Parsed.Value);
    }

    [Fact]
    public void FindAt_CoversTheWholeWord_WhenTheCaretIsInsideIt()
    {
        var token = MentionSyntax.FindAt("@foo bar", 2);

        Assert.Equal(0, token!.Start);
        Assert.Equal(4, token.Length);
        Assert.Equal("f", token.Query);
    }

    [Theory]
    [InlineData("mail me@example.com", 19)]
    [InlineData("@foo bar", 6)]
    [InlineData("no mention", 5)]
    [InlineData("", 0)]
    [InlineData("@x", 5)]
    public void FindAt_IgnoresWhatIsNotAMention(string text, int caret) => Assert.Null(MentionSyntax.FindAt(text, caret));

    [Fact]
    public void FindAt_AcceptsAnAtAfterAnOpeningBracketOrQuote_AndABareAt()
    {
        Assert.Equal(string.Empty, MentionSyntax.FindAt("@", 1)!.Query);
        Assert.Equal("ab", MentionSyntax.FindAt("(@ab", 4)!.Query);
        Assert.Equal("file:x", MentionSyntax.FindAt("see \"@file:x", 12)!.Query);
    }
}

public sealed class MentionFileMatcherTests
{
    private static readonly string[] Roots = [@"C:\repo", @"C:\repo\src\App"];

    [Fact]
    public void MatchFiles_RanksAnExactName_ThenAPrefix_ThenASubstring_ThenLettersInOrder_ThenThePath()
    {
        string[] files =
        [
            @"C:\repo\src\App\Programs\Helper.cs",
            @"C:\repo\tests\MyProgram.cs",
            @"C:\repo\src\App\ProgramTests.cs",
            @"C:\repo\src\App\Program.cs",
            @"C:\repo\src\App\PrgOrderAudit.cs",
        ];

        var matches = MentionFileMatcher.MatchFiles(files, Roots, "program");

        // Two name prefixes (the shorter name first), a name substring, then a match in the folder path.
        Assert.Equal(["Program.cs", "ProgramTests.cs", "MyProgram.cs", "Helper.cs"], matches.Select(m => m.Name));
        Assert.Equal([1, 1, 2, 4], matches.Select(m => m.Rank));

        // The exact name, a name substring, the name's letters in order, the path's letters in order.
        var exact = MentionFileMatcher.MatchFiles(files, Roots, "Program.cs");
        Assert.Equal(["Program.cs", "MyProgram.cs", "ProgramTests.cs", "Helper.cs"], exact.Select(m => m.Name));
        Assert.Equal([0, 2, 3, 5], exact.Select(m => m.Rank));

        Assert.Equal("PrgOrderAudit.cs", Assert.Single(MentionFileMatcher.MatchFiles(files, Roots, "prgaud")).Name);
    }

    [Fact]
    public void MatchFiles_PrefersANameMatchOverAPathMatch()
    {
        string[] files = [@"C:\repo\src\Api\Startup.cs", @"C:\repo\src\ApiClient.cs"];

        var matches = MentionFileMatcher.MatchFiles(files, Roots, "api");

        Assert.Equal(["ApiClient.cs", "Startup.cs"], matches.Select(m => m.Name));
        Assert.Equal("src/Api/Startup.cs", matches[1].RelativePath);
    }

    [Fact]
    public void MatchFiles_WithASlash_MatchesTheRelativePath()
    {
        string[] files = [@"C:\repo\src\Api\Startup.cs", @"C:\repo\tests\Api\Startup.cs"];

        var matches = MentionFileMatcher.MatchFiles(files, [@"C:\repo"], @"src\api/startup.cs");

        Assert.Equal(@"C:\repo\src\Api\Startup.cs", Assert.Single(matches, m => m.Rank == 0).FullPath);
    }

    [Fact]
    public void MatchFiles_ReturnsAtMostTwenty_AndNothingForAnEmptyQuery()
    {
        var files = Enumerable.Range(0, 50).Select(i => $@"C:\repo\src\File{i:00}.cs").ToList();

        Assert.Equal(20, MentionFileMatcher.MatchFiles(files, Roots, "file").Count);
        Assert.Equal(MentionFileMatcher.MaxMatches, MentionFileMatcher.MatchFiles(files, Roots, "file").Count);
        Assert.Empty(MentionFileMatcher.MatchFiles(files, Roots, " "));
    }

    [Fact]
    public void MatchFolders_ListsTheFoldersThatHoldTheFiles()
    {
        string[] files = [@"C:\repo\src\Api\Controllers\Orders.cs", @"C:\repo\src\Web\Index.cs"];

        var matches = MentionFileMatcher.MatchFolders(files, [@"C:\repo"], "api");

        Assert.Equal(@"C:\repo\src\Api", matches[0].FullPath);
        Assert.Equal("src/Api", matches[0].RelativePath);
        Assert.DoesNotContain(matches, m => m.FullPath == @"C:\repo");
    }

    [Fact]
    public void Relative_UsesTheLongestRootThatContainsThePath()
    {
        Assert.Equal("Program.cs", MentionFileMatcher.Relative(@"C:\repo\src\App\Program.cs", Roots));
        Assert.Equal("tests/A.cs", MentionFileMatcher.Relative(@"C:\repo\tests\A.cs", Roots));
        Assert.Equal("D:/other/B.cs", MentionFileMatcher.Relative(@"D:\other\B.cs", Roots));
    }
}

public sealed class MentionPickerTests
{
    private static readonly WorkspaceFileList Files = new(
        [@"C:\repo"],
        [@"C:\repo\Program.cs", @"C:\repo\src\Errors.cs", @"C:\repo\src\Api\Orders.cs"]);

    [Fact]
    public void AnAt_ListsTheKinds()
    {
        var picker = new MentionPickerViewModel();

        picker.Update("@", 1, Files);

        Assert.True(picker.IsOpen);
        Assert.Equal(["@file", "@folder", "@selection", "@errors", "@clipboard"], picker.Items.Select(i => i.Label));
        Assert.Equal(0, picker.SelectedIndex);
        Assert.True(picker.Items[0].IsSelected);
        Assert.Equal(ChatStrings.MentionPickerHint, picker.Title);
    }

    [Fact]
    public void Typing_ListsMatchingKindsFirst_ThenFiles()
    {
        var picker = new MentionPickerViewModel();

        picker.Update("look at @err", 12, Files);

        Assert.Equal(["@errors", "Errors.cs"], picker.Items.Select(i => i.Label));
        Assert.Equal(MentionPickerItemKind.Kind, picker.Items[0].ItemKind);
        Assert.Equal(MentionPickerItemKind.File, picker.Items[1].ItemKind);
        Assert.Equal("src/Errors.cs", picker.Items[1].Detail);
        Assert.Equal(@"@file:C:\repo\src\Errors.cs", picker.Items[1].MentionText);
        Assert.Equal("@errors", picker.Items[0].MentionText);
    }

    [Fact]
    public void FileKind_ListsOnlyFiles_AndFolderKindOnlyFolders()
    {
        var picker = new MentionPickerViewModel();

        picker.Update("@file:", 6, Files);
        Assert.Empty(picker.Items);
        Assert.Equal(ChatStrings.MentionPickerFileHint, picker.Title);

        picker.Update("@file:ord", 9, Files);
        Assert.Equal(["Orders.cs"], picker.Items.Select(i => i.Label));

        picker.Update("@folder:api", 11, Files);
        var folder = Assert.Single(picker.Items);
        Assert.Equal(MentionPickerItemKind.Folder, folder.ItemKind);
        Assert.Equal(@"@folder:C:\repo\src\Api", folder.MentionText);
    }

    [Fact]
    public void NoMatches_SaysSo()
    {
        var picker = new MentionPickerViewModel();

        picker.Update("@zzzz", 5, Files);

        Assert.True(picker.IsOpen);
        Assert.Empty(picker.Items);
        Assert.Equal(-1, picker.SelectedIndex);
        Assert.Equal(ChatStrings.MentionPickerNoMatches, picker.Title);
    }

    [Fact]
    public void Move_Wraps_AndEscKeepsTheMentionClosedUntilTheCaretLeavesIt()
    {
        var picker = new MentionPickerViewModel();
        picker.Update("@", 1, Files);

        picker.Move(-1);
        Assert.Equal(4, picker.SelectedIndex);
        picker.Move(1);
        Assert.Equal(0, picker.SelectedIndex);
        Assert.Same(picker.Items[0], picker.SelectedItem);

        picker.Dismiss();
        Assert.False(picker.IsOpen);
        picker.Update("@f", 2, Files);
        Assert.False(picker.IsOpen);

        picker.Update("@f ", 3, Files);
        picker.Update("@f @", 4, Files);
        Assert.True(picker.IsOpen);
    }

    [Fact]
    public void ShowCandidates_ListsThePathsOfAnAmbiguousMention()
    {
        var picker = new MentionPickerViewModel();

        picker.ShowCandidates("file", "@Customer.cs", [@"C:\repo\A\Customer.cs", @"C:\repo\B\Customer.cs"], [@"C:\repo"]);
        picker.Update("text without a mention", 4, Files);

        Assert.True(picker.IsOpen);
        Assert.True(picker.IsCandidates);
        Assert.Equal(["A/Customer.cs", "B/Customer.cs"], picker.Items.Select(i => i.Detail));
        Assert.All(picker.Items, i => Assert.Equal(MentionPickerItemKind.Candidate, i.ItemKind));
        Assert.Contains("@Customer.cs", picker.Title, StringComparison.Ordinal);
        Assert.Equal(@"@file:C:\repo\A\Customer.cs", picker.Items[0].MentionText);
    }
}

public sealed class MentionsViewModelTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private static readonly WorkspaceFileList List = new([@"C:\src"], [@"C:\src\Program.cs", @"C:\src\Other.cs", @"C:\src\.env"]);

    private readonly JoinableTaskContext _context = Task.Run(() => new JoinableTaskContext()).Result;
    private readonly FakeMentionService _service = new();
    private readonly FakeWorkspaceFiles _files = new(List);
    private readonly FakeEditorContext _editor = new();
    private readonly ChatConversation _conversation = new();
    private readonly FakeHostConnection _connection;
    private readonly List<MentionInputEditEventArgs> _edits = [];

    public MentionsViewModelTests()
    {
        _connection = new FakeHostConnection().With<IContextService>(_service);
    }

    public void Dispose() => _context.Dispose();

    [Fact]
    public async Task PickingAFile_RemovesTheTypedMention_AndPinsTheResolution()
    {
        using var vm = Create();
        vm.UpdateInput("explain @Prog please", 13);
        var item = Assert.Single(vm.Picker.Items);

        await vm.CommitAsync(item).WithTimeout(Timeout);

        var edit = Assert.Single(_edits);
        Assert.Equal("explain please", edit.Apply("explain @Prog please"));
        Assert.Equal(8, edit.Caret);
        Assert.False(vm.Picker.IsOpen);
        var request = Assert.Single(_service.Requests);
        Assert.Equal("file", request.Kind);
        Assert.Equal(@"@file:C:\src\Program.cs", request.Mention);
        Assert.Equal(List.Files, request.CandidateFiles);
        Assert.Equal([@"C:\src"], request.WorkspaceRoots);
        Assert.Null(request.Payload);
        Assert.False(request.ApprovedSensitive);
        var pin = Assert.Single(_conversation.Pins);
        Assert.Equal("Program.cs", pin.DisplayName);
        Assert.Equal("5", pin.TokenBadge);
        Assert.False(pin.IsSensitive);
        Assert.False(vm.HasNotice);
    }

    [Fact]
    public async Task PickingTheFileKind_TypesItsPrefix_AndKeepsThePickerForFiles()
    {
        using var vm = Create();
        vm.UpdateInput("@", 1);

        await vm.CommitAsync(vm.Picker.Items[0]).WithTimeout(Timeout);

        var edit = Assert.Single(_edits);
        Assert.Equal("@file:", edit.Apply("@"));
        Assert.Equal(6, edit.Caret);
        Assert.Empty(_service.Requests);
        vm.UpdateInput("@file:", 6);
        Assert.True(vm.Picker.IsOpen);
        Assert.Equal(ChatStrings.MentionPickerFileHint, vm.Picker.Title);
    }

    [Theory]
    [InlineData("selection", "int x = 1;")]
    [InlineData("clipboard", "copied text")]
    [InlineData("errors", """{"severity":"error","id":"CS0029","message":"Cannot convert","file":"C:\\src\\Program.cs","line":3}""")]
    public async Task EditorKinds_SendTheirPayload(string kind, string payload)
    {
        _editor.Selection = kind == "selection" ? payload : null;
        _editor.Clipboard = kind == "clipboard" ? payload : null;
        _editor.Errors = kind == "errors" ? payload : string.Empty;
        _service.Resolve = r => FakeMentionService.Resolved(r, kind, null, r.Payload!, 3, kind);
        using var vm = Create();
        vm.UpdateInput("@" + kind, kind.Length + 1);
        var item = vm.Picker.Items.Single(i => i.ItemKind == MentionPickerItemKind.Kind && i.MentionKind == kind);

        await vm.CommitAsync(item).WithTimeout(Timeout);

        var request = Assert.Single(_service.Requests);
        Assert.Equal(kind, request.Kind);
        Assert.Equal("@" + kind, request.Mention);
        Assert.Equal(payload, request.Payload);
        Assert.Empty(request.CandidateFiles);
        Assert.Equal(kind, Assert.Single(_conversation.Pins).Kind);
    }

    [Fact]
    public async Task ASecretFile_AsksFirst_AndApprovalResendsWithApprovedSensitive()
    {
        _service.Resolve = r => r.ApprovedSensitive
            ? FakeMentionService.Resolved(r, ".env", @"C:\src\.env", "KEY=1", 2, sensitive: true)
            : new MentionResolution { Mention = r.Mention, Kind = "file", DisplayName = ".env", FilePath = @"C:\src\.env", RequiresApproval = true, Reason = "Looks secret." };
        using var vm = Create();

        await vm.ResolveTextAsync("@file:.env").WithTimeout(Timeout);

        Assert.True(vm.HasPendingApproval);
        Assert.Contains("holds secrets", vm.PendingApproval!.Prompt, StringComparison.Ordinal);
        Assert.Equal(".env", vm.PendingApproval.Name);
        Assert.Empty(_conversation.Pins);
        Assert.True(vm.ApproveCommand.CanExecute(null));

        vm.ApproveCommand.Execute(null);
        await Wait.UntilAsync(() => _conversation.Pins.Count == 1, "the approved file is pinned");

        Assert.False(vm.HasPendingApproval);
        Assert.Equal([false, true], _service.Requests.Select(r => r.ApprovedSensitive));
        Assert.True(_conversation.Pins[0].IsSensitive);
        Assert.Contains(ChatStrings.PinSensitiveShort, _conversation.Pins[0].AutomationName, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DecliningASecretFile_PinsNothing()
    {
        _service.Resolve = r => new MentionResolution { Mention = r.Mention, Kind = "file", DisplayName = ".env", RequiresApproval = true };
        using var vm = Create();
        await vm.ResolveTextAsync("@file:.env").WithTimeout(Timeout);

        vm.DeclineCommand.Execute(null);

        Assert.False(vm.HasPendingApproval);
        Assert.Single(_service.Requests);
        Assert.Empty(_conversation.Pins);
    }

    [Fact]
    public async Task AnAmbiguousMention_OpensTheSubPicker_AndPickingACandidatePinsIt()
    {
        _service.Resolve = r => r.Mention.EndsWith(@"B\Customer.cs", StringComparison.Ordinal)
            ? FakeMentionService.Resolved(r, "Customer.cs", @"C:\src\B\Customer.cs", "class B { }", 4)
            : new MentionResolution { Mention = r.Mention, Kind = "file", Candidates = [@"C:\src\A\Customer.cs", @"C:\src\B\Customer.cs"], Reason = "Two match." };
        using var vm = Create();

        await vm.ResolveTextAsync("@Customer.cs").WithTimeout(Timeout);

        Assert.True(vm.Picker.IsOpen);
        Assert.True(vm.Picker.IsCandidates);
        Assert.Equal(2, vm.Picker.Items.Count);
        Assert.True(vm.HandleKey(MentionKey.Down));
        Assert.True(vm.HandleKey(MentionKey.Enter));
        await Wait.UntilAsync(() => _conversation.Pins.Count == 1, "the candidate is pinned");

        Assert.Equal(@"@file:C:\src\B\Customer.cs", _service.Requests.Last().Mention);
        Assert.Empty(_edits);
        Assert.False(vm.Picker.IsOpen);
    }

    [Fact]
    public async Task AnUnresolvedMention_ShowsItsReason()
    {
        _service.Resolve = r => new MentionResolution { Mention = r.Mention, Reason = "No file matches." };
        using var vm = Create();

        await vm.ResolveTextAsync("@missing.cs").WithTimeout(Timeout);

        Assert.True(vm.HasNotice);
        Assert.Equal("@missing.cs: No file matches.", vm.Notice);
        Assert.Empty(_conversation.Pins);
        vm.DismissNoticeCommand.Execute(null);
        Assert.False(vm.HasNotice);
    }

    [Fact]
    public async Task PinningTheSameFileAgain_ReplacesThePinInPlace()
    {
        var tokens = 5;
        _service.Resolve = r => FakeMentionService.Resolved(r, "Program.cs", @"C:\src\Program.cs", "class Program { }", tokens++);
        using var vm = Create();
        await vm.ResolveTextAsync("@Other.cs").WithTimeout(Timeout);
        _service.Resolve = r => FakeMentionService.Resolved(r, r.Mention.Contains("Other") ? "Other.cs" : "Program.cs", r.Mention.Contains("Other") ? @"C:\src\Other.cs" : @"C:\src\Program.cs", "x", tokens++);
        await vm.ResolveTextAsync("@Other.cs").WithTimeout(Timeout);
        await vm.ResolveTextAsync("@Program.cs").WithTimeout(Timeout);
        await vm.ResolveTextAsync("@Program.cs").WithTimeout(Timeout);

        Assert.Equal(["Program.cs", "Other.cs"], _conversation.Pins.Select(p => p.DisplayName));
        Assert.Equal(8, _conversation.Pins[0].TokenCount);

        vm.RemovePinCommand.Execute(_conversation.Pins[0]);
        Assert.Equal(["Other.cs"], _conversation.Pins.Select(p => p.DisplayName));
    }

    [Fact]
    public void Keys_DriveThePicker_OnlyWhileItIsOpen()
    {
        using var vm = Create();

        Assert.False(vm.HandleKey(MentionKey.Enter));
        vm.UpdateInput("@", 1);
        Assert.True(vm.HandleKey(MentionKey.Down));
        Assert.Equal(1, vm.Picker.SelectedIndex);
        Assert.True(vm.HandleKey(MentionKey.Up));
        Assert.Equal(0, vm.Picker.SelectedIndex);
        Assert.True(vm.HandleKey(MentionKey.Escape));
        Assert.False(vm.Picker.IsOpen);

        vm.UpdateInput(string.Empty, 0);
        vm.UpdateInput("@zzzz", 5);
        Assert.True(vm.Picker.IsOpen);
        Assert.False(vm.HandleKey(MentionKey.Enter)); // Nothing to pick: Enter sends the message.
        Assert.False(vm.Picker.IsOpen);
    }

    [Fact]
    public void WhenDisabled_ThePickerNeverOpens()
    {
        using var vm = Create();
        vm.IsEnabled = false;

        vm.UpdateInput("@", 1);

        Assert.False(vm.Picker.IsOpen);
        Assert.False(vm.HandleKey(MentionKey.Enter));
    }

    [Fact]
    public async Task OpeningThePicker_AsksForTheFileList_AndANewListRefiltersIt()
    {
        var files = new FakeWorkspaceFiles(WorkspaceFileList.Empty);
        using var vm = new MentionsViewModel(_connection, _context.Factory, _conversation, files, _editor);
        vm.UpdateInput("@Prog", 5);
        Assert.DoesNotContain(vm.Picker.Items, i => i.ItemKind == MentionPickerItemKind.File);

        await Wait.UntilAsync(() => Volatile(files) > 0, "the file list is requested");
        files.Replace(List);
        await Wait.UntilAsync(() => vm.Picker.Items.Any(i => i.Label == "Program.cs"), "the picker shows the new list");
    }

    private static int Volatile(FakeWorkspaceFiles files) => System.Threading.Volatile.Read(ref files.Requests);

    private MentionsViewModel Create()
    {
        var vm = new MentionsViewModel(_connection, _context.Factory, _conversation, _files, _editor);
        vm.InputEditRequested += (_, e) => _edits.Add(e);
        return vm;
    }
}

public sealed class ChatMentionsTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly JoinableTaskContext _context = Task.Run(() => new JoinableTaskContext()).Result;
    private readonly ScriptedModelService _models = new();
    private readonly InMemorySettingsService _settings = TestModels.Settings();
    private readonly FakeMentionService _mentions = new();
    private readonly FakeDiagnostics _diagnostics = new();
    private readonly FakeEditorContext _editor = new() { CurrentFile = new CurrentFileContext(@"C:\src\Calc.cs", "var total = Calc.Add(1, 2);") };
    private readonly FakeHostConnection _connection;
    private string? _copied;

    public ChatMentionsTests()
    {
        _connection = new FakeHostConnection()
            .With<IModelService>(_models)
            .With<ISettingsService>(_settings)
            .With<IContextService>(_mentions)
            .With<IDiagnosticsService>(_diagnostics);
    }

    public void Dispose() => _context.Dispose();

    [Fact]
    public async Task PinsAreSentWithEveryMessageOfTheirConversation_AndStayWithIt()
    {
        using var vm = await StartAsync();
        var first = vm.CurrentConversation;
        await vm.Mentions.ResolveTextAsync("@Program.cs").WithTimeout(Timeout);

        var one = await ExchangeAsync(vm, "first");
        var two = await ExchangeAsync(vm, "second");

        Assert.Equal("Program.cs", Assert.Single(one.Pins).DisplayName);
        Assert.Equal("class Program { }", one.Pins[0].Content);
        Assert.Single(two.Pins);
        Assert.Equal(first.Id, one.ConversationId);
        Assert.Equal(first.Id, two.ConversationId);

        vm.NewConversation();
        Assert.Empty(vm.Mentions.Pins);
        var other = await ExchangeAsync(vm, "elsewhere");
        Assert.Empty(other.Pins);
        Assert.NotEqual(first.Id, other.ConversationId);

        vm.SwitchTo(first);
        Assert.Equal("Program.cs", Assert.Single(vm.Mentions.Pins).DisplayName);
    }

    [Fact]
    public async Task TheCurrentFileGoesWithAMessage_UnlessSwitchedOff_OrTheSelectionIsPinned()
    {
        using var vm = await StartAsync();

        var with = await ExchangeAsync(vm, "with");
        vm.IncludeCurrentFile = false;
        var without = await ExchangeAsync(vm, "without");
        vm.IncludeCurrentFile = true;
        _mentions.Resolve = r => FakeMentionService.Resolved(r, "Selection", null, "picked", 1, "selection");
        _editor.Selection = "picked";
        await vm.Mentions.ResolveTextAsync("@selection").WithTimeout(Timeout);
        var selection = await ExchangeAsync(vm, "selection");

        Assert.Equal(@"C:\src\Calc.cs", with.CurrentFile);
        Assert.Equal("var total = Calc.Add(1, 2);", with.CurrentFileRegion);
        Assert.Equal(string.Empty, without.CurrentFile);
        Assert.Null(without.CurrentFileRegion);
        Assert.Equal(string.Empty, selection.CurrentFile);
        Assert.Equal("selection", Assert.Single(selection.Pins).Kind);
        Assert.Equal(Profile.Auto, with.Profile);
    }

    [Fact]
    public async Task WithTheMentionsFlagOff_NoPinsOrCurrentFileAreSent()
    {
        _settings.Values[ChatAvailability.EffectiveFeaturesKey] = """{ "chat": true, "mentions": false }""";
        using var availability = new ChatAvailability(_connection);
        using var vm = await StartAsync(availability);
        await Wait.UntilAsync(() => !vm.IsMentionsEnabled, "the mentions flag is read");
        vm.CurrentConversation.Pins.Add(new MentionPin(FakeMentionService.Resolved(new MentionRequest { Mention = "@x" }, "x.cs", @"C:\x.cs", "x", 1)));

        var request = await ExchangeAsync(vm, "hi");

        Assert.False(vm.Mentions.IsEnabled);
        Assert.Empty(request.Pins);
        Assert.Equal(string.Empty, request.CurrentFile);
        Assert.True(vm.IsOutboundLogEnabled);
    }

    [Fact]
    public async Task WithTheOutboundLogViewFlagOff_InspectIsUnavailable()
    {
        _settings.Values[ChatAvailability.EffectiveFeaturesKey] = """{ "outboundLogView": false }""";
        using var availability = new ChatAvailability(_connection);
        using var vm = await StartAsync(availability);
        await Wait.UntilAsync(() => !vm.IsOutboundLogEnabled, "the outboundLogView flag is read");
        vm.InputText = "hi";

        Assert.False(vm.InspectContextCommand.CanExecute(null));
        Assert.True(vm.IsMentionsEnabled);
        Assert.True(vm.SendCommand.CanExecute(null));
    }

    [Fact]
    public async Task Inspect_PreviewsTheRequestSendWouldMake_AndRemoveUnpinsAndPreviewsAgain()
    {
        using var vm = await StartAsync();
        await vm.Mentions.ResolveTextAsync("@Program.cs").WithTimeout(Timeout);
        vm.InputText = "  explain it  ";
        Assert.True(vm.InspectContextCommand.CanExecute(null));

        await vm.InspectAsync().WithTimeout(Timeout);

        var preview = Assert.Single(_diagnostics.Previews);
        Assert.Equal("explain it", preview.Messages[preview.Messages.Count - 1].Content);
        Assert.Single(preview.Pins);
        Assert.Equal(@"C:\src\Calc.cs", preview.CurrentFile);
        Assert.Equal(vm.CurrentConversation.Id, preview.ConversationId);
        Assert.True(vm.Inspector.IsOpen);
        Assert.False(vm.Inspector.IsLoading);
        Assert.Equal(["System prompt", "Program.cs", "Your message"], vm.Inspector.Items.Select(i => i.Name));
        var pinItem = vm.Inspector.Items[1];
        Assert.True(pinItem.CanRemove);
        Assert.Equal(ChatStrings.InspectorKindPin, pinItem.KindLabel);
        Assert.False(vm.Inspector.Items[0].CanRemove);
        Assert.Equal(string.Empty, vm.Inspector.Items[0].KindPrefix); // "System prompt" is not repeated.
        Assert.Equal(ChatStrings.InspectorKindPin, pinItem.KindPrefix);
        Assert.Contains("qwen2.5-coder:7b", vm.Inspector.Summary, StringComparison.Ordinal);
        Assert.Empty(vm.Turns);

        vm.Inspector.RemoveCommand.Execute(pinItem);
        await Wait.UntilAsync(() => _diagnostics.Previews.Count == 2 && vm.Inspector.Items.Count == 2, "the inspector previews again without the pin");

        Assert.Empty(vm.Mentions.Pins);
        Assert.Empty(_diagnostics.Previews.Last().Pins);
    }

    [Fact]
    public async Task Inspector_ShowsCutDroppedAndRedactedItems()
    {
        _diagnostics.Preview = _ => new ContextReportDto
        {
            ModelId = "gpt-test",
            ProviderId = "openrouter",
            IsLocal = false,
            ContextWindow = 4096,
            OutputReserve = 1024,
            BudgetTokens = 3000,
            TotalTokens = 900,
            IsAssembled = true,
            Redacts = true,
            RedactionCount = 2,
            Items =
            [
                new() { Kind = "pin", Name = "Big.cs", Tokens = 800, OriginalTokens = 5000, Truncated = true, Note = "It was cut to fit the pin cap (750 tokens).", PinIndex = 0, RedactionCount = 2 },
                new() { Kind = "pin", Name = "Second.cs", Tokens = 0, OriginalTokens = 5000, Dropped = true, Note = "There was no room left in the budget.", PinIndex = 1 },
            ],
        };
        using var vm = await StartAsync();
        vm.InputText = "hi";

        await vm.InspectAsync().WithTimeout(Timeout);

        var cut = vm.Inspector.Items[0];
        var dropped = vm.Inspector.Items[1];
        Assert.Contains("800", cut.TokensText, StringComparison.Ordinal);
        Assert.Contains("5,000", cut.TokensText, StringComparison.Ordinal);
        Assert.StartsWith("Cut:", cut.StateText, StringComparison.Ordinal);
        Assert.True(cut.HasRedaction);
        Assert.StartsWith("Left out:", dropped.StateText, StringComparison.Ordinal);
        Assert.False(dropped.CanRemove); // The request carried no pins, so nothing maps to the window's pins.
        Assert.True(vm.Inspector.HasRedactionNotice);
        Assert.Contains("2", vm.Inspector.RedactionNotice, StringComparison.Ordinal);
        Assert.Contains("cloud", vm.Inspector.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SendFromTheInspector_SendsTheMessage_AndClosesIt()
    {
        using var vm = await StartAsync();
        vm.InputText = "go";
        await vm.InspectAsync().WithTimeout(Timeout);
        var script = _models.Next();

        vm.Inspector.SendCommand.Execute(null);
        var request = await script.Started.Task.WithTimeout(Timeout);
        script.Finish();
        await Wait.UntilAsync(() => !vm.IsSending, "the reply finishes");

        Assert.Equal("go", request.Messages[request.Messages.Count - 1].Content);
        Assert.False(vm.Inspector.IsOpen);
    }

    [Fact]
    public async Task RemovingARetrievedChunkOrTheRepoMap_InTheInspector_LeavesItOutOfThePreviewAndTheSend_ThenTheNextMessageHasItAgain()
    {
        _diagnostics.Preview = request =>
        {
            var excluded = new HashSet<string>(request.ExcludedContextKeys, StringComparer.Ordinal);
            var items = new[]
            {
                new ContextReportItemDto { Kind = "system", Name = "System prompt", Tokens = 30, OriginalTokens = 30 },
                new ContextReportItemDto { Kind = "repoMap", Name = "Repository map", Tokens = 120, OriginalTokens = 120, ContextKey = "repoMap" },
                new ContextReportItemDto { Kind = "retrieved", Name = "src/Orders/OrderService.cs:9-12", Tokens = 60, OriginalTokens = 60, ContextKey = "idx:1" },
                new ContextReportItemDto { Kind = "retrieved", Name = "src/Inventory/StockRoom.cs:3-8", Tokens = 50, OriginalTokens = 50, ContextKey = "idx:2" },
                new ContextReportItemDto { Kind = "query", Name = "Your message", Tokens = 4, OriginalTokens = 4 },
            };
            return new ContextReportDto { ModelId = "qwen2.5-coder:7b", ProviderId = "ollama", IsLocal = true, ContextWindow = 8192, OutputReserve = 2048, BudgetTokens = 6114, Items = [.. items.Where(i => !excluded.Contains(i.ContextKey))], IsAssembled = true };
        };
        using var vm = await StartAsync();
        vm.InputText = "how are totals computed";
        await vm.InspectAsync().WithTimeout(Timeout);

        var chunk = vm.Inspector.Items.Single(i => i.ContextKey == "idx:1");
        var map = vm.Inspector.Items.Single(i => i.ContextKey == "repoMap");
        Assert.True(chunk.CanRemove);
        Assert.True(map.CanRemove);
        Assert.Null(chunk.Pin);
        Assert.Equal(ChatStrings.InspectorKindRetrieved, chunk.KindLabel);
        Assert.Equal(ChatStrings.Format(ChatStrings.RemoveContextItemNameFormat, "src/Orders/OrderService.cs:9-12"), chunk.RemoveAutomationName);
        Assert.True(vm.Inspector.RemoveCommand.CanExecute(chunk));

        vm.Inspector.RemoveCommand.Execute(chunk);
        await Wait.UntilAsync(() => _diagnostics.Previews.Count == 2 && vm.Inspector.Items.All(i => i.ContextKey != "idx:1"), "the inspector previews again without the chunk");
        vm.Inspector.RemoveCommand.Execute(vm.Inspector.Items.Single(i => i.ContextKey == "repoMap"));
        await Wait.UntilAsync(() => _diagnostics.Previews.Count == 3 && vm.Inspector.Items.All(i => i.ContextKey != "repoMap"), "the inspector previews again without the map");
        Assert.Equal(["idx:1", "repoMap"], _diagnostics.Previews.Last().ExcludedContextKeys);

        var script = _models.Next();
        vm.Inspector.SendCommand.Execute(null);
        var sent = await script.Started.Task.WithTimeout(Timeout);
        script.Finish();
        await Wait.UntilAsync(() => !vm.IsSending, "the reply finishes");
        Assert.Equal(["idx:1", "repoMap"], sent.ExcludedContextKeys);

        // The removals belonged to that message.
        vm.InputText = "and taxes?";
        await vm.InspectAsync().WithTimeout(Timeout);
        Assert.Empty(_diagnostics.Previews.Last().ExcludedContextKeys);
        Assert.Contains(vm.Inspector.Items, i => i.ContextKey == "idx:1");
    }

    [Fact]
    public async Task APreviewFailure_IsShownInTheInspector()
    {
        _diagnostics.Preview = _ => throw new PillionRpcException(new PillionError { Code = PillionErrorCode.ModelNotInstalled, Message = "No model can serve chat." });
        using var vm = await StartAsync();
        vm.InputText = "hi";

        await vm.InspectAsync().WithTimeout(Timeout);

        Assert.True(vm.Inspector.HasError);
        Assert.Contains("No model can serve chat.", vm.Inspector.Error, StringComparison.Ordinal);
        Assert.Empty(vm.Inspector.Items);
    }

    [Fact]
    public async Task TheOutboundLog_IsRefreshedAfterEachReply_NewestFirst_AndCopiesAsText()
    {
        using var vm = await StartAsync();
        var reads = System.Threading.Volatile.Read(ref _diagnostics.OutboundReads);
        lock (_diagnostics.Records)
        {
            _diagnostics.Records.Add(Record("old", new DateTime(2026, 10, 10, 8, 0, 0, DateTimeKind.Utc), TaskClass.Autocomplete, cacheHit: true));
            _diagnostics.Records.Add(Record("new", new DateTime(2026, 10, 10, 9, 0, 0, DateTimeKind.Utc), TaskClass.Chat, error: PillionErrorCode.ProviderUnavailable));
        }

        await ExchangeAsync(vm, "hi");
        await Wait.UntilAsync(() => vm.OutboundLog.Records.Count == 2, "the Outbound log is refreshed after the reply");

        Assert.True(System.Threading.Volatile.Read(ref _diagnostics.OutboundReads) > reads);
        Assert.Equal(["new", "old"], vm.OutboundLog.Records.Select(r => r.Record.RequestId));
        var newest = vm.OutboundLog.Records[0];
        Assert.Equal("Chat", newest.TaskClass);
        Assert.Equal("ollama / gemma4:e2b", newest.ProviderModel);
        Assert.Equal(ChatStrings.LocalityLocal, newest.Locality);
        Assert.Equal("Failed (ProviderUnavailable)", newest.Outcome);
        Assert.Equal("2.5 s", newest.Duration);
        Assert.Equal(ChatStrings.OutboundCacheHit, vm.OutboundLog.Records[1].Tokens);
        Assert.Equal(ChatStrings.Format(ChatStrings.OutboundCountFormat, 2), vm.OutboundLog.Summary);

        Assert.True(vm.OutboundLog.CopyCommand.CanExecute(null));
        vm.OutboundLog.CopyCommand.Execute(null);
        var lines = _copied!.Split([Environment.NewLine], StringSplitOptions.None);
        Assert.Equal(3, lines.Length);
        Assert.StartsWith(ChatStrings.OutboundColumnTime + "\t", lines[0], StringComparison.Ordinal);
        Assert.Contains("\tChat\t", lines[1], StringComparison.Ordinal);
        Assert.DoesNotContain("hi", lines[1].Split('\t').Skip(1), StringComparer.Ordinal);
    }

    [Fact]
    public async Task TheOutboundLog_ShowsWhyItCouldNotBeRead()
    {
        var connection = new FakeHostConnection().With<IModelService>(_models).With<ISettingsService>(_settings);
        using var vm = new ChatViewModel(connection, _context.Factory, new ChatViewModelOptions { FlushInterval = TimeSpan.Zero });
        vm.Start();

        await vm.OutboundLog.RefreshAsync().WithTimeout(Timeout);

        Assert.True(vm.OutboundLog.HasError);
        Assert.Empty(vm.OutboundLog.Records);
        Assert.False(vm.OutboundLog.CopyCommand.CanExecute(null));
    }

    private static OutboundRecordDto Record(string id, DateTime at, TaskClass taskClass, bool cacheHit = false, PillionErrorCode? error = null) => new()
    {
        TimestampUtc = at,
        RequestId = id,
        ProviderId = "ollama",
        ModelId = "gemma4:e2b",
        TaskClass = taskClass,
        IsLocal = true,
        PromptTokens = 1200,
        CompletionTokens = 34,
        DurationMs = 2450,
        Outcome = error is null ? "Succeeded" : "Failed",
        ErrorCode = error,
        CacheHit = cacheHit,
    };

    private async Task<ChatViewModel> StartAsync(ChatAvailability? availability = null)
    {
        var vm = new ChatViewModel(_connection, _context.Factory, new ChatViewModelOptions
        {
            FlushInterval = TimeSpan.Zero,
            EditorContext = _editor,
            WorkspaceFiles = new FakeWorkspaceFiles(new WorkspaceFileList([@"C:\src"], [@"C:\src\Program.cs"])),
            Availability = availability,
            CopyText = text => _copied = text,
        });
        availability?.Start();
        vm.Start();
        await Wait.UntilAsync(() => vm.ModelPicker.SelectedModel is not null, "the models are listed");
        return vm;
    }

    private async Task<ChatRequest> ExchangeAsync(ChatViewModel vm, string question)
    {
        var script = _models.Next();
        vm.InputText = question;
        var send = vm.SendAsync();
        var request = await script.Started.Task.WithTimeout(Timeout);
        script.Send("ok", isFinal: true);
        await send.WithTimeout(Timeout);
        return request;
    }
}

public sealed class ErrorsPayloadAndFlagTests
{
    [Fact]
    public void ErrorsPayload_WritesOneJsonObjectPerLine_InTheResolversFormat()
    {
        var payload = ErrorsPayload.Format(
        [
            new IdeDiagnostic(@"C:\src\Program.cs", 3, 9, IdeSeverity.Error, "CS0029", "Cannot implicitly convert type 'string' to 'int'", "Scratch", DiagnosticSource.ErrorList),
            new IdeDiagnostic(null, 0, 0, IdeSeverity.Warning, null, "Project warning", null, DiagnosticSource.ErrorList),
            new IdeDiagnostic(@"C:\src\A.cs", 1, 1, IdeSeverity.Info, "IDE0005", "Using directive is unnecessary.", null, DiagnosticSource.Roslyn),
        ]);

        var lines = payload.Split('\n');
        Assert.Equal(3, lines.Length);
        var first = JObject.Parse(lines[0]);
        Assert.Equal("error", (string?)first["severity"]);
        Assert.Equal("CS0029", (string?)first["id"]);
        Assert.Equal("Cannot implicitly convert type 'string' to 'int'", (string?)first["message"]);
        Assert.Equal(@"C:\src\Program.cs", (string?)first["file"]);
        Assert.Equal(3, (int)first["line"]!);
        var second = JObject.Parse(lines[1]);
        Assert.Equal("warning", (string?)second["severity"]);
        Assert.Equal(string.Empty, (string?)second["id"]);
        Assert.Null(second["line"]);
        Assert.Equal("info", (string?)JObject.Parse(lines[2])["severity"]);
        Assert.Equal(string.Empty, ErrorsPayload.Format([]));
    }

    [Theory]
    [InlineData(null, "mentions", true)]
    [InlineData("{}", "mentions", true)]
    [InlineData("""{ "mentions": false }""", "mentions", false)]
    [InlineData("""{ "mentions": "no" }""", "mentions", true)]
    [InlineData("""{ "outboundLogView": false, "chat": true }""", "outboundLogView", false)]
    [InlineData("""{ "outboundLogView": false }""", "chat", true)]
    public void ReadFlag_IsOnUnlessTheHostSaysFalse(string? json, string flag, bool expected) =>
        Assert.Equal(expected, ChatAvailability.ReadFlag(json, flag));

    [Theory]
    [InlineData(0, "0")]
    [InlineData(950, "950")]
    [InlineData(1234, "1.2k")]
    [InlineData(15600, "15k")]
    public void TokenBadges_AreShort(int tokens, string expected) =>
        Assert.Equal(expected, MentionPin.FormatTokens(tokens));

    [Fact]
    public void MentionInputEdit_ClampsToTheText()
    {
        var edit = new MentionInputEditEventArgs(3, 10, "X", 4);

        Assert.Equal("abcX", edit.Apply("abcdef"));
        Assert.Equal("X", new MentionInputEditEventArgs(9, 1, "X", 1).Apply(string.Empty));
    }
}
