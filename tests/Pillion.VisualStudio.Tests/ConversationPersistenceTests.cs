using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;
using Pillion.Contracts;
using Pillion.VisualStudio.Core;

namespace Pillion.VisualStudio.Tests;

// The tests await their own TaskCompletionSource tasks and the view models' tasks; no main thread is involved.
#pragma warning disable VSTHRD003

/// <summary>
/// Work item 2.2 in the Chat window: the conversation list loads from the host (newest first, paged), opening a conversation
/// reads its messages, citations and pins, rename, delete and Clear follow the host, Delete all on the Privacy page empties the
/// window, and a host that cannot save conversations leaves the in-memory behaviour with a one-line notice.
/// </summary>
public sealed class ConversationPersistenceTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private static readonly DateTime T0 = new(2026, 10, 14, 9, 0, 0, DateTimeKind.Utc);
    private const string ProgramPath = @"C:\src\Program.cs";

    private readonly JoinableTaskContext _context = Task.Run(() => new JoinableTaskContext()).Result;
    private readonly ScriptedModelService _models = new();
    private readonly ScriptedConversationService _service = new();
    private readonly FakeMentionService _mentions = new();
    private readonly ConversationEvents _events = new();
    private readonly ConcurrentQueue<(string Path, int Line)> _opened = new();
    private readonly List<string> _confirmations = [];
    private readonly FakeHostConnection _connection;

    public ConversationPersistenceTests()
    {
        _connection = new FakeHostConnection()
            .With<IModelService>(_models)
            .With<ISettingsService>(TestModels.Settings())
            .With<IConversationService>(_service)
            .With<IContextService>(_mentions);
    }

    public void Dispose() => _context.Dispose();

    [Fact]
    public async Task TheListLoadsFromTheHost_NewestFirst_BehindTheEmptyConversationThatIsOpen()
    {
        _service.Seed("older", "Welcome", T0);
        _service.Seed("newer", "Fix the build", T0.AddHours(3), [_service.Message(ChatRole.User, "q"), _service.Message(ChatRole.Assistant, "a")]);

        using var vm = await StartAsync();

        Assert.Equal(["New conversation", "Fix the build", "Welcome"], vm.Conversations.Select(c => c.Title));
        Assert.Equal(ConversationStorage.Available, vm.Storage);
        Assert.False(vm.HasConversationsNotice);
        Assert.False(vm.HasMoreConversations);
        Assert.False(vm.CurrentConversation.IsPersisted);
        var newer = vm.Conversations[1];
        Assert.True(newer.IsPersisted);
        Assert.False(newer.IsLoaded);
        Assert.False(newer.IsEmpty); // The host says it has messages, although they are not read yet.
        Assert.Equal("Fix the build, 2 messages", newer.AutomationName);
        Assert.Equal(1, _service.CallCount("list:"));
        Assert.Equal(0, _service.CallCount("get:")); // Messages are read when a conversation is opened, not before.
    }

    [Fact]
    public async Task TheListIsReadAPageAtATime_WithoutDuplicates()
    {
        for (var i = 0; i < 120; i++)
        {
            _service.Seed("c" + i.ToString("000", CultureInfo.InvariantCulture), "Conversation " + i, T0.AddMinutes(i));
        }

        using var vm = await StartAsync();

        Assert.Equal(1 + ChatViewModel.ConversationPageSize, vm.Conversations.Count);
        Assert.True(vm.HasMoreConversations);
        Assert.Equal("Conversation 119", vm.Conversations[1].Title);

        await vm.LoadMoreConversationsAsync();
        Assert.Equal(1 + 100, vm.Conversations.Count);
        Assert.True(vm.HasMoreConversations);

        await vm.LoadMoreConversationsAsync();
        Assert.Equal(1 + 120, vm.Conversations.Count);
        Assert.False(vm.HasMoreConversations);
        Assert.Equal(120, vm.Conversations.Select(c => c.Id).Distinct().Count(id => id.Length == 4));
        Assert.Equal(["list:0/50", "list:50/50", "list:100/50"], _service.Calls.Where(c => c.StartsWith("list:", StringComparison.Ordinal)));

        await vm.LoadMoreConversationsAsync(); // Nothing more: no call.
        Assert.Equal(3, _service.CallCount("list:"));
    }

    [Fact]
    public async Task OpeningAConversation_ReadsItsMessagesCitationsAndPins()
    {
        var citation = new Citation { FilePath = ProgramPath, StartLine = 11, EndLine = 14, ChunkId = "i:1", Breadcrumb = "src/Program.cs > Program > Main", ReplyOffset = 5 };
        _service.Seed(
            "conv",
            "Fix the build",
            T0,
            [
                _service.Message(ChatRole.User, "Why does the build fail?"),
                _service.Message(ChatRole.Assistant, "Program.cs calls a removed method.", "qwen2.5-coder:7b", "ollama", [citation]),
                _service.Message(ChatRole.User, "And now?"),
                _service.Message(ChatRole.Assistant, "It was cut off", "qwen2.5-coder:7b", "ollama", cancelled: true),
            ],
            [
                new ConversationPin { Kind = MentionKinds.File, Target = ProgramPath, DisplayName = "Program.cs", TokenCount = 120 },
                new ConversationPin { Kind = MentionKinds.Errors, Target = string.Empty, DisplayName = "Errors", TokenCount = 40 },
            ]);
        using var vm = await StartAsync();
        var conversation = vm.Conversations.Single(c => c.Id == "conv");

        vm.CurrentConversation = conversation;
        await Wait.UntilAsync(() => conversation.IsLoaded && !conversation.IsRestoringPins, "the conversation is read and its pins restored");

        Assert.Same(conversation, vm.CurrentConversation);
        Assert.Equal(["Why does the build fail?", "Program.cs calls a removed method.", "And now?", "It was cut off"], vm.Turns.Select(t => t.Text));
        Assert.Equal([ChatTurnRole.User, ChatTurnRole.Assistant, ChatTurnRole.User, ChatTurnRole.Assistant], vm.Turns.Select(t => t.Role));
        var reply = vm.Turns[1];
        Assert.Equal(ModelOption.ReplyHeaderFor(vm.ModelPicker.Models, "ollama", "qwen2.5-coder:7b"), reply.Header);
        Assert.Equal(ChatTurnState.Complete, reply.State);
        Assert.Equal(ChatTurnState.Stopped, vm.Turns[3].State); // Stopped replies are marked by the host's Cancelled flag.

        var cited = Assert.Single(reply.Citations);
        Assert.Equal("Program.cs:12-15", cited.Text);
        Assert.Equal(ProgramPath, cited.FilePath);
        Assert.Equal(11, cited.StartLine);
        cited.OpenCommand!.Execute(cited);
        Assert.Equal([(ProgramPath, 11)], _opened);

        // The file pin was read again; the errors pin holds client-only text, so it stays stale and nothing is sent for it.
        Assert.Equal(["Program.cs", "Errors"], conversation.Pins.Select(p => p.DisplayName));
        Assert.False(conversation.Pins[0].IsStale);
        Assert.True(conversation.Pins[1].IsStale);
        Assert.Equal("40", conversation.Pins[1].TokenBadge);
        Assert.Equal("@file:" + ProgramPath, Assert.Single(_mentions.Requests).Mention);
        Assert.Equal(0, _service.CallCount("pins:")); // Restoring does not write the pins back.

        // The next message continues the history and sends only the pins that have content.
        var script = _models.Next();
        vm.InputText = "Try again";
        var send = vm.SendAsync();
        var request = await script.Started.Task.WithTimeout(Timeout);
        script.Send("ok", isFinal: true);
        await send.WithTimeout(Timeout);
        Assert.Equal(["Why does the build fail?", "Program.cs calls a removed method.", "And now?", "It was cut off", "Try again"], request.Messages.Select(m => m.Content));
        Assert.Equal("Program.cs", Assert.Single(request.Pins).DisplayName);
        Assert.Equal(conversation.Id, request.ConversationId);
    }

    [Fact]
    public async Task APinThatCannotBeReadAgain_StaysStale_WithItsSavedTokenBadge()
    {
        _service.Seed("conv", "T", T0, [_service.Message(ChatRole.User, "q")], [new ConversationPin { Kind = MentionKinds.File, Target = @"C:\gone\Old.cs", DisplayName = "Old.cs", TokenCount = 1500 }]);
        _mentions.Resolve = request => new MentionResolution { Mention = request.Mention, IsResolved = false, Reason = "File not found." };
        using var vm = await StartAsync();
        var conversation = vm.Conversations.Single(c => c.Id == "conv");

        vm.CurrentConversation = conversation;
        await Wait.UntilAsync(() => conversation.IsLoaded && !conversation.IsRestoringPins, "the conversation is read");

        var pin = Assert.Single(conversation.Pins);
        Assert.True(pin.IsStale);
        Assert.Equal("Old.cs", pin.DisplayName);
        Assert.Equal("1.5k", pin.TokenBadge);
        Assert.Contains("Pin it again", pin.ToolTip, StringComparison.Ordinal);
        Assert.Contains("stale", pin.AutomationName, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpeningAConversation_ShowsThatItIsLoading_AndBlocksSendingUntilItIsRead()
    {
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _service.GetGate = gate.Task;
        _service.Seed("conv", "T", T0, [_service.Message(ChatRole.User, "q"), _service.Message(ChatRole.Assistant, "a")]);
        using var vm = await StartAsync();
        var conversation = vm.Conversations.Single(c => c.Id == "conv");

        vm.CurrentConversation = conversation;
        await Wait.UntilAsync(() => conversation.IsLoading, "the read starts");
        vm.InputText = "typed while loading";
        Assert.False(vm.SendCommand.CanExecute(null));

        gate.SetResult(true);
        await Wait.UntilAsync(() => conversation.IsLoaded, "the conversation is read");
        Assert.False(conversation.IsLoading);
        Assert.True(vm.SendCommand.CanExecute(null));
    }

    [Fact]
    public async Task AConversationThatCannotBeRead_ShowsWhy_AndCanBeReadAgain()
    {
        _service.Seed("conv", "T", T0, [_service.Message(ChatRole.User, "q")]);
        _service.GetFailure = new PillionRpcException(new PillionError { Code = PillionErrorCode.StorageUnavailable, Message = "The conversation database is locked." });
        using var vm = await StartAsync();
        var conversation = vm.Conversations.Single(c => c.Id == "conv");

        vm.CurrentConversation = conversation;
        await Wait.UntilAsync(() => conversation.HasLoadError, "the failure is shown");

        Assert.Contains("The conversation database is locked.", conversation.LoadError, StringComparison.Ordinal);
        Assert.False(conversation.IsLoaded);
        vm.InputText = "hello";
        Assert.False(vm.SendCommand.CanExecute(null)); // Sending would leave the stored history out.
        Assert.True(vm.RetryLoadConversationCommand.CanExecute(null));

        _service.GetFailure = null;
        vm.RetryLoadConversationCommand.Execute(null);
        await Wait.UntilAsync(() => conversation.IsLoaded, "the retry reads it");
        Assert.False(conversation.HasLoadError);
        Assert.Equal(["q"], vm.Turns.Select(t => t.Text));
    }

    [Fact]
    public async Task AConversationDeletedElsewhere_LeavesTheListWhenItIsOpened()
    {
        _service.Seed("gone", "Gone", T0, [_service.Message(ChatRole.User, "q")]);
        _service.Seed("kept", "Kept", T0.AddHours(1));
        using var vm = await StartAsync();
        var gone = vm.Conversations.Single(c => c.Id == "gone");
        await _service.DeleteConversationAsync("gone", default);

        vm.CurrentConversation = gone;
        await Wait.UntilAsync(() => !vm.Conversations.Contains(gone), "the conversation leaves the list");

        Assert.DoesNotContain(vm.Conversations, c => c.Id == "gone");
        Assert.NotSame(gone, vm.CurrentConversation);
        Assert.Contains(vm.CurrentConversation, vm.Conversations);
    }

    [Fact]
    public async Task Rename_FollowsTheHost_ForASavedConversation_AndNotForOneItDoesNotHaveYet()
    {
        _service.Seed("conv", "Old name", T0, [_service.Message(ChatRole.User, "q")]);
        using var vm = await StartAsync();

        // An unsaved conversation keeps its new name locally; the host hears it when the conversation is created.
        vm.BeginRename();
        vm.RenameText = "Local name";
        vm.CommitRename();
        Assert.Equal("Local name", vm.CurrentConversation.Title);
        Assert.Equal(0, _service.CallCount("rename:"));

        // A saved one is renamed on the host, from the list's context menu (the command takes the conversation).
        var saved = vm.Conversations.Single(c => c.Id == "conv");
        vm.RenameConversationCommand.Execute(saved);
        Assert.Same(saved, vm.CurrentConversation);
        Assert.True(vm.IsRenaming);
        Assert.Equal("Old name", vm.RenameText);
        vm.RenameText = "  New name  ";
        vm.CommitRenameCommand.Execute(null);

        await Wait.UntilAsync(() => _service.CallCount("rename:") == 1, "the rename reaches the host");
        Assert.Equal(["rename:conv:New name"], _service.Calls.Where(c => c.StartsWith("rename:", StringComparison.Ordinal)));
        Assert.Equal("New name", _service.TitleOf("conv"));
        Assert.False(vm.HasConversationsNotice);

        // Committing the same name again changes nothing and calls nothing.
        vm.BeginRename();
        vm.CommitRename();
        await Task.Delay(50);
        Assert.Equal(1, _service.CallCount("rename:"));
    }

    [Fact]
    public async Task Delete_FollowsTheHost_ForASavedConversation_AfterTheConfirmation()
    {
        _service.Seed("a", "A", T0, [_service.Message(ChatRole.User, "q")]);
        _service.Seed("b", "B", T0.AddHours(1), [_service.Message(ChatRole.User, "q")]);
        using var vm = await StartAsync();
        var a = vm.Conversations.Single(c => c.Id == "a");
        var b = vm.Conversations.Single(c => c.Id == "b");

        Assert.True(vm.DeleteConversationCommand.CanExecute(a)); // From the context menu: any entry, not only the open one.
        vm.DeleteConversationCommand.Execute(a);

        Assert.Equal(["A"], _confirmations);
        Assert.DoesNotContain(a, vm.Conversations);
        await Wait.UntilAsync(() => !_service.Has("a"), "the delete reaches the host");
        Assert.Contains(b, vm.Conversations);
        Assert.Equal(["delete:a"], _service.Calls.Where(c => c.StartsWith("delete:", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Delete_DeclinedLeavesTheConversationOnTheHost()
    {
        _service.Seed("a", "A", T0, [_service.Message(ChatRole.User, "q")]);
        using var vm = await StartAsync(confirm: false);
        var a = vm.Conversations.Single(c => c.Id == "a");

        Assert.False(vm.DeleteConversation(a));

        Assert.Contains(a, vm.Conversations);
        Assert.Equal(0, _service.CallCount("delete:"));
    }

    [Fact]
    public async Task ADeleteThatCannotBeSaved_SaysSo_AndTheConversationIsGoneFromTheWindow()
    {
        _service.Seed("a", "A", T0, [_service.Message(ChatRole.User, "q")]);
        using var vm = await StartAsync();
        var a = vm.Conversations.Single(c => c.Id == "a");
        _service.Failure = new PillionRpcException(new PillionError { Code = PillionErrorCode.ProviderUnavailable, Message = "Disk is full." });

        Assert.True(vm.DeleteConversation(a));
        await Wait.UntilAsync(() => vm.HasConversationsNotice, "the notice appears");

        Assert.Contains("Disk is full.", vm.ConversationsNotice, StringComparison.Ordinal);
        Assert.Equal(ConversationStorage.Available, vm.Storage); // One failed delete does not turn saving off.
    }

    [Fact]
    public async Task Clear_OnASavedConversation_DeletesTheHostsCopy_AndTheWindowKeepsALocalOne()
    {
        _service.Seed("conv", "Saved title", T0, [_service.Message(ChatRole.User, "q"), _service.Message(ChatRole.Assistant, "a")]);
        using var vm = await StartAsync();
        var conversation = vm.Conversations.Single(c => c.Id == "conv");
        vm.CurrentConversation = conversation;
        await Wait.UntilAsync(() => conversation.IsLoaded, "the conversation is read");

        vm.ClearCommand.Execute(null);

        await Wait.UntilAsync(() => !_service.Has("conv"), "the host's copy is deleted");
        Assert.Empty(vm.Turns);
        Assert.False(conversation.IsPersisted);
        Assert.Equal("New conversation", conversation.Title); // It follows the next first message again.
        Assert.Contains(conversation, vm.Conversations);
    }

    [Fact]
    public async Task PinsOfASavedConversation_AreSavedWhenTheyChange()
    {
        _service.Seed("conv", "T", T0, [_service.Message(ChatRole.User, "q")]);
        using var vm = await StartAsync();
        var conversation = vm.Conversations.Single(c => c.Id == "conv");
        vm.CurrentConversation = conversation;
        await Wait.UntilAsync(() => conversation.IsLoaded && !conversation.IsRestoringPins, "the conversation is read");

        await vm.Mentions.ResolveTextAsync("@file:" + ProgramPath);
        await Wait.UntilAsync(() => _service.PinsSet.ContainsKey("conv") && _service.PinsSet["conv"].Length == 1, "the pin is saved");

        var saved = _service.PinsSet["conv"][0];
        Assert.Equal((MentionKinds.File, ProgramPath, "Program.cs", 5), (saved.Kind, saved.Target, saved.DisplayName, saved.TokenCount));

        vm.Mentions.RemovePin(conversation.Pins[0]);
        await Wait.UntilAsync(() => _service.PinsSet["conv"].Length == 0, "the removal is saved");
    }

    [Fact]
    public async Task TheFirstMessage_OfANewConversationWithANameAndPins_CreatesItOnTheHostFirst_AndItMovesToTheTopAfterTheReply()
    {
        _service.Seed("other", "Other", T0, [_service.Message(ChatRole.User, "q")]);
        using var vm = await StartAsync();
        var fresh = vm.CurrentConversation;
        vm.BeginRename();
        vm.RenameText = "My plan";
        vm.CommitRename();
        await vm.Mentions.ResolveTextAsync("@file:" + ProgramPath);
        Assert.Equal(0, _service.CallCount("create:"));

        var script = _models.Next();
        vm.InputText = "Plan the work";
        var send = vm.SendAsync();
        await script.Started.Task.WithTimeout(Timeout);
        Assert.Equal(["create:" + fresh.Id + ":My plan", "pins:" + fresh.Id + ":1"], _service.Calls.Where(c => !c.StartsWith("list:", StringComparison.Ordinal)));
        script.Send("Here it is.", isFinal: true);
        await send.WithTimeout(Timeout);

        Assert.True(fresh.IsPersisted);
        Assert.Equal(0, vm.Conversations.IndexOf(fresh));
    }

    [Fact]
    public async Task AReplyInAnOlderConversation_MovesItToTheTopOfTheList()
    {
        _service.Seed("older", "Older", T0, [_service.Message(ChatRole.User, "q"), _service.Message(ChatRole.Assistant, "a")]);
        _service.Seed("newer", "Newer", T0.AddHours(1), [_service.Message(ChatRole.User, "q"), _service.Message(ChatRole.Assistant, "a")]);
        using var vm = await StartAsync();
        var older = vm.Conversations.Single(c => c.Id == "older");
        vm.CurrentConversation = older;
        await Wait.UntilAsync(() => older.IsLoaded, "the conversation is read");

        var script = _models.Next();
        vm.InputText = "one more";
        var send = vm.SendAsync();
        await script.Started.Task.WithTimeout(Timeout);
        script.Send("done", isFinal: true);
        await send.WithTimeout(Timeout);

        Assert.Same(older, vm.Conversations[0]);
        Assert.Same(older, vm.CurrentConversation);
        Assert.Equal(0, _service.CallCount("create:")); // The host has it already: no create before the message.
    }

    [Fact]
    public async Task AfterTheFirstReply_TheWindowAdoptsTheTitleTheHostGaveTheConversation()
    {
        using var vm = await StartAsync();
        var fresh = vm.CurrentConversation;
        var message = "Explain how the incremental Markdown renderer decides which blocks to keep and which to rebuild";
        _service.Seed(fresh.Id, message, T0);

        var script = _models.Next();
        vm.InputText = message;
        var send = vm.SendAsync();
        await script.Started.Task.WithTimeout(Timeout);
        Assert.EndsWith("…", fresh.Title, StringComparison.Ordinal); // The window's own 48-character title until the host's arrives.
        script.Send("It compares blocks.", isFinal: true);
        await send.WithTimeout(Timeout);
        await Wait.UntilAsync(() => fresh.Title == message, "the host's title is adopted");

        Assert.True(fresh.IsPersisted);
    }

    [Fact]
    public async Task ATitleTheDeveloperChose_IsNotReplacedByTheHostsAfterTheFirstReply()
    {
        using var vm = await StartAsync();
        var fresh = vm.CurrentConversation;
        vm.BeginRename();
        vm.RenameText = "My plan";
        vm.CommitRename();
        var script = _models.Next();
        vm.InputText = "Plan";
        var send = vm.SendAsync();
        await script.Started.Task.WithTimeout(Timeout);
        _service.Seed(fresh.Id, "Something else", T0);
        script.Send("ok", isFinal: true);
        await send.WithTimeout(Timeout);
        await Task.Delay(100);

        Assert.Equal("My plan", fresh.Title);
    }

    [Fact]
    public async Task ACitationOnALiveReply_IsShownAsALink_AndOpensTheLine()
    {
        using var vm = await StartAsync();
        var script = _models.Next();
        vm.InputText = "Where is Main?";
        var send = vm.SendAsync();
        await script.Started.Task.WithTimeout(Timeout);
        script.SendFinalWithCitations("In Program.cs.", new Citation { FilePath = ProgramPath, StartLine = 11, EndLine = 11 }, new Citation { FilePath = ProgramPath, StartLine = 11, EndLine = 11 });
        await send.WithTimeout(Timeout);

        var item = Assert.Single(vm.Turns.Last().Citations); // The same span cited twice shows once.
        Assert.Equal("Program.cs:12", item.Text);
        Assert.True(vm.Turns.Last().HasCitations);
        vm.OpenCitationCommand.Execute(item);
        Assert.Equal([(ProgramPath, 11)], _opened);
    }

    [Fact]
    public async Task WhenTheLocalStoreFlagIsOff_TheListIsTheInMemoryOne_WithANotice_AndNothingIsSent()
    {
        _service.Failure = new PillionRpcException(new PillionError { Code = PillionErrorCode.PolicyDenied, Message = "Saved conversations are turned off by settings or policy (feature localStore)." });
        using var vm = await StartAsync();

        Assert.Equal(ConversationStorage.Unavailable, vm.Storage);
        Assert.Equal(ChatStrings.ConversationsNotSavedOff, vm.ConversationsNotice);
        Assert.True(vm.HasConversationsNotice);
        Assert.Single(vm.Conversations);

        // The window works as it did before saving existed.
        var first = vm.CurrentConversation;
        vm.BeginRename();
        vm.RenameText = "Local";
        vm.CommitRename();
        await vm.Mentions.ResolveTextAsync("@file:" + ProgramPath);
        var script = _models.Next();
        vm.InputText = "Hello";
        var send = vm.SendAsync();
        await script.Started.Task.WithTimeout(Timeout);
        script.Send("Hi", isFinal: true);
        await send.WithTimeout(Timeout);
        vm.NewConversation();
        vm.DeleteConversation(first);

        Assert.Equal(["list:0/50"], _service.Calls);
    }

    [Fact]
    public async Task WhenTheStoreIsBroken_TheNoticeSaysWhy_AndTurningTheFlagBackOnBringsTheConversationsBack()
    {
        _service.Seed("conv", "Saved", T0, [_service.Message(ChatRole.User, "q")]);
        _service.Failure = new PillionRpcException(new PillionError { Code = PillionErrorCode.StorageUnavailable, Message = "The conversation store key does not open the database." });
        using var vm = await StartAsync();

        Assert.Equal(ConversationStorage.Unavailable, vm.Storage);
        Assert.Contains("The conversation store key does not open the database.", vm.ConversationsNotice, StringComparison.Ordinal);
        Assert.DoesNotContain(vm.Conversations, c => c.Id == "conv");

        _service.Failure = null;
        await _connection.RaiseSettingsChangedAsync("features");
        await Wait.UntilAsync(() => vm.Storage == ConversationStorage.Available, "the list is read again");

        Assert.False(vm.HasConversationsNotice);
        Assert.Contains(vm.Conversations, c => c.Id == "conv");
    }

    [Fact]
    public async Task AnyOtherFailureToListConversations_TurnsSavingOffWithTheReasonInTheNotice()
    {
        _service.Failure = new InvalidOperationException("the host said no");

        using var vm = await StartAsync();

        Assert.Equal(ConversationStorage.Unavailable, vm.Storage);
        Assert.Contains("the host said no", vm.ConversationsNotice, StringComparison.Ordinal);
        Assert.Single(vm.Conversations);
    }

    [Fact]
    public async Task AHostWithoutTheService_IsNotANotice_ItIsJustNotAvailableYet()
    {
        var connection = new FakeHostConnection().With<IModelService>(_models).With<ISettingsService>(TestModels.Settings());
        using var vm = new ChatViewModel(connection, _context.Factory, new ChatViewModelOptions { FlushInterval = TimeSpan.Zero });
        vm.Start();
        await Wait.UntilAsync(() => vm.ModelPicker.SelectedModel is not null, "the models are listed");
        await Task.Delay(100);

        Assert.Equal(ConversationStorage.Unknown, vm.Storage);
        Assert.False(vm.HasConversationsNotice);
        Assert.Single(vm.Conversations);
        Assert.False(vm.IsLoadingConversations);
    }

    [Fact]
    public async Task TheListIsReadAgainWhenTheHostIsBack()
    {
        using var vm = await StartAsync();
        _service.Seed("later", "Appeared later", T0);

        await _connection.SetStateAsync(HostState.Restarting);
        await _connection.SetStateAsync(HostState.Ready);
        await Wait.UntilAsync(() => vm.Conversations.Any(c => c.Id == "later"), "the list is read after the restart");

        Assert.False(vm.HasConversationsNotice);
        Assert.Equal(ConversationStorage.Available, vm.Storage);
    }

    [Fact]
    public async Task AnOpenConversation_KeepsItsTranscript_WhenTheListIsReadAgainAfterARestart()
    {
        _service.Seed("conv", "Saved", T0, [_service.Message(ChatRole.User, "q"), _service.Message(ChatRole.Assistant, "a")]);
        using var vm = await StartAsync();
        var conversation = vm.Conversations.Single(c => c.Id == "conv");
        vm.CurrentConversation = conversation;
        await Wait.UntilAsync(() => conversation.IsLoaded, "the conversation is read");
        var calls = _service.CallCount("list:");

        await _connection.SetStateAsync(HostState.Restarting);
        await _connection.SetStateAsync(HostState.Ready);
        await Wait.UntilAsync(() => _service.CallCount("list:") > calls, "the list is read again");
        await Task.Delay(50);

        Assert.Single(vm.Conversations, c => c.Id == "conv");
        Assert.Same(conversation, vm.Conversations.Single(c => c.Id == "conv"));
        Assert.Equal(2, conversation.Turns.Count);
        Assert.Equal(1, _service.CallCount("get:")); // Read once; the restart does not read it again.
    }

    [Fact]
    public async Task DeleteAllOnThePrivacyPage_EmptiesTheOpenWindow()
    {
        _service.Seed("a", "A", T0, [_service.Message(ChatRole.User, "q")]);
        _service.Seed("b", "B", T0.AddHours(1), [_service.Message(ChatRole.User, "q")]);
        using var vm = await StartAsync();
        var b = vm.Conversations.Single(c => c.Id == "b");
        vm.CurrentConversation = b;
        await Wait.UntilAsync(() => b.IsLoaded, "the conversation is read");
        using var privacy = new PrivacyOptionsViewModel(_connection, _context.Factory, null, _events) { ConfirmDeleteAll = _ => true };
        await privacy.LoadAsync();

        await privacy.DeleteAllConversationsAsync();
        await Wait.UntilAsync(() => vm.Conversations.Count == 1 && vm.Conversations[0].IsEmpty && !vm.Conversations.Contains(b), "the window forgets the conversations");

        Assert.Same(vm.Conversations[0], vm.CurrentConversation);
        Assert.False(vm.CurrentConversation.IsPersisted);
        Assert.Empty(vm.Turns);
        Assert.DoesNotContain(_service.Calls, c => c.StartsWith("delete:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Privacy_DeleteAll_AsksFirst_ReportsWhatWasDeleted_AndTellsTheWindow()
    {
        _service.Seed("a", "A", T0, [_service.Message(ChatRole.User, "q"), _service.Message(ChatRole.Assistant, "a")], [new ConversationPin { Kind = MentionKinds.File, Target = "x" }]);
        _service.Seed("b", "B", T0);
        var raised = 0;
        _events.AllDeleted += (_, _) => raised++;
        var asked = new List<string>();
        var answer = false;
        using var vm = new PrivacyOptionsViewModel(_connection, _context.Factory, null, _events)
        {
            ConfirmDeleteAll = message =>
            {
                asked.Add(message);
                return answer;
            },
        };
        await vm.LoadAsync();

        await vm.DeleteAllConversationsAsync(); // Declined.
        Assert.Equal([PrivacyOptionsViewModel.DeleteAllConfirmation], asked);
        Assert.Equal(0, _service.CallCount("deleteAll"));
        Assert.False(vm.HasDeleteAllStatus);

        answer = true;
        await vm.DeleteAllConversationsAsync();

        Assert.Equal(1, _service.CallCount("deleteAll"));
        Assert.Equal("Deleted 2 conversations, 2 messages, 1 pins and 7 Outbound log entries.", vm.DeleteAllStatus);
        Assert.Equal(1, raised);
        Assert.False(vm.IsDeletingAll);
    }

    [Fact]
    public async Task Privacy_DeleteAll_ThatFails_SaysWhy_AndTellsNoOne()
    {
        _service.Failure = new PillionRpcException(new PillionError { Code = PillionErrorCode.PolicyDenied, Message = "Saved conversations are turned off by settings or policy (feature localStore)." });
        var raised = 0;
        _events.AllDeleted += (_, _) => raised++;
        using var vm = new PrivacyOptionsViewModel(_connection, _context.Factory, null, _events) { ConfirmDeleteAll = _ => true };
        await vm.LoadAsync();

        await vm.DeleteAllConversationsAsync();

        Assert.StartsWith("Could not delete: ", vm.DeleteAllStatus, StringComparison.Ordinal);
        Assert.Contains("localStore", vm.DeleteAllStatus, StringComparison.Ordinal);
        Assert.Equal(0, raised);
    }

    [Fact]
    public async Task Privacy_DeleteAll_IsUnavailableWithoutAConfirmation()
    {
        using var vm = new PrivacyOptionsViewModel(_connection, _context.Factory, null, _events);
        await vm.LoadAsync();

        Assert.False(vm.DeleteAllConversationsCommand.CanExecute(null));
        await vm.DeleteAllConversationsAsync();
        Assert.Equal(0, _service.CallCount("deleteAll"));

        vm.ConfirmDeleteAll = _ => true;
        Assert.True(vm.DeleteAllConversationsCommand.CanExecute(null));
    }

    [Fact]
    public async Task ASpanCitation_OpensWithTheSpanSelected_WhenTheWindowCanSelectSpans()
    {
        var spans = new List<(string Path, int Start, int End)>();
        using var vm = await StartAsync(openSpan: (path, start, end) => spans.Add((path, start, end)));
        var script = _models.Next();
        vm.InputText = "Where is the total?";
        var send = vm.SendAsync();
        await script.Started.Task.WithTimeout(Timeout);
        script.SendFinalWithCitations(
            "In ComputeTotal.",
            new Citation { FilePath = ProgramPath, StartLine = 8, EndLine = 11, ReplyOffset = 3 },
            new Citation { FilePath = ProgramPath, StartLine = 20, EndLine = 24, ReplyOffset = -1 });
        await send.WithTimeout(Timeout);

        var turn = vm.Turns.Last();
        var link = Assert.Single(turn.ExplicitCitations);
        Assert.Equal("Program.cs:9-12", link.Text);
        var source = Assert.Single(turn.ImplicitCitations);
        Assert.Equal("Sources (1)", turn.SourcesText);
        vm.OpenCitationCommand.Execute(link);
        vm.OpenCitationCommand.Execute(source);
        Assert.Equal([(ProgramPath, 8, 11), (ProgramPath, 20, 24)], spans);
        Assert.Empty(_opened); // the line-only action is the fallback
    }

    private async Task<ChatViewModel> StartAsync(bool confirm = true, Action<string, int, int>? openSpan = null)
    {
        var vm = new ChatViewModel(
            _connection,
            _context.Factory,
            new ChatViewModelOptions
            {
                FlushInterval = TimeSpan.Zero,
                ConversationEvents = _events,
                OpenCitation = (path, line) => _opened.Enqueue((path, line)),
                OpenCitationSpan = openSpan,
                ConfirmDelete = c =>
                {
                    _confirmations.Add(c.Title);
                    return confirm;
                },
            });
        vm.Start();
        await Wait.UntilAsync(() => vm.ModelPicker.SelectedModel is not null, "the models are listed");
        await Wait.UntilAsync(() => _service.CallCount("list:") >= 1 && !vm.IsLoadingConversations, "the list is read");
        return vm;
    }
}
