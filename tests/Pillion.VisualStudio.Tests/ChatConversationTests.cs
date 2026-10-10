using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;
using Pillion.Contracts;
using Pillion.VisualStudio.Core;

namespace Pillion.VisualStudio.Tests;

// The tests await their own TaskCompletionSource tasks and the view models' tasks; no main thread is involved.
#pragma warning disable VSTHRD003

public sealed class ChatConversationTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly JoinableTaskContext _context = Task.Run(() => new JoinableTaskContext()).Result;
    private readonly ScriptedModelService _models = new();
    private readonly FakeHostConnection _connection;
    private readonly List<string> _confirmations = [];
    private bool _confirm = true;

    public ChatConversationTests()
    {
        _connection = new FakeHostConnection().With<IModelService>(_models).With<ISettingsService>(TestModels.Settings());
    }

    public void Dispose() => _context.Dispose();

    [Fact]
    public async Task ANewViewModelHasOneEmptyConversationAndNewIsDisabledUntilItHasMessages()
    {
        using var vm = await StartAsync();

        var only = Assert.Single(vm.Conversations);
        Assert.Same(only, vm.CurrentConversation);
        Assert.Same(only.Turns, vm.Turns);
        Assert.Equal("New conversation", only.Title);
        Assert.False(vm.NewConversationCommand.CanExecute(null));
        Assert.False(vm.DeleteConversationCommand.CanExecute(null));

        await ExchangeAsync(vm, "How do I read a file?", "Use File.ReadAllText.");

        Assert.Equal("How do I read a file?", only.Title);
        Assert.Equal("How do I read a file?, 2 messages", only.AutomationName);
        Assert.True(vm.NewConversationCommand.CanExecute(null));
        Assert.True(vm.DeleteConversationCommand.CanExecute(null));
    }

    [Fact]
    public async Task New_StartsAnEmptyConversationAtTheTopAndSendsOnlyItsOwnHistory()
    {
        using var vm = await StartAsync();
        await ExchangeAsync(vm, "first question", "first answer");
        var first = vm.CurrentConversation;
        var turnsChanged = 0;
        vm.PropertyChanged += (_, e) => turnsChanged += e.PropertyName == nameof(ChatViewModel.Turns) ? 1 : 0;

        vm.NewConversationCommand.Execute(null);

        Assert.Equal(2, vm.Conversations.Count);
        Assert.NotSame(first, vm.CurrentConversation);
        Assert.Same(vm.CurrentConversation, vm.Conversations[0]);
        Assert.Empty(vm.Turns);
        Assert.Equal(1, turnsChanged);
        Assert.False(vm.NewConversationCommand.CanExecute(null));

        var script = _models.Next();
        vm.InputText = "second question";
        var send = vm.SendAsync();
        var request = await script.Started.Task.WithTimeout(Timeout);
        script.Finish();
        await send.WithTimeout(Timeout);

        Assert.Equal(["second question"], request.Messages.Select(m => m.Content));
        Assert.Equal(2, first.Turns.Count);
    }

    [Fact]
    public async Task Switch_ShowsTheOtherTranscriptAndContinuesItsHistory()
    {
        using var vm = await StartAsync();
        await ExchangeAsync(vm, "about files", "files answer");
        var files = vm.CurrentConversation;
        vm.NewConversation();
        await ExchangeAsync(vm, "about threads", "threads answer");

        vm.CurrentConversation = files;

        Assert.Equal(["about files", "files answer"], vm.Turns.Select(t => t.Text));
        var script = _models.Next();
        vm.InputText = "more about files";
        var send = vm.SendAsync();
        var request = await script.Started.Task.WithTimeout(Timeout);
        script.Finish();
        await send.WithTimeout(Timeout);
        Assert.Equal(["about files", "files answer", "more about files"], request.Messages.Select(m => m.Content));
    }

    [Fact]
    public async Task Switch_StopsAStreamingReplyAndKeepsItsTextInItsConversation()
    {
        using var vm = await StartAsync();
        await ExchangeAsync(vm, "old", "old answer");
        var old = vm.CurrentConversation;
        vm.NewConversation();
        var script = _models.Next();
        vm.InputText = "streaming";
        var send = vm.SendAsync();
        await script.Started.Task.WithTimeout(Timeout);
        script.Send("partial ");
        await Wait.UntilAsync(() => vm.Turns.Last().Text == "partial ", "the first chunk is shown");
        var streaming = vm.CurrentConversation;

        vm.CurrentConversation = old;
        await send.WithTimeout(Timeout);

        Assert.False(vm.IsSending);
        Assert.True(await script.Cancelled.Task.WithTimeout(Timeout));
        var reply = streaming.Turns.Last();
        Assert.Equal(ChatTurnState.Stopped, reply.State);
        Assert.Equal("partial ", reply.Text);
        Assert.Equal(2, vm.Turns.Count);
        vm.InputText = "can send again";
        Assert.True(vm.SendCommand.CanExecute(null));
    }

    [Fact]
    public async Task Rename_SavesATrimmedNameThatNoLongerFollowsTheFirstMessage()
    {
        using var vm = await StartAsync();
        await ExchangeAsync(vm, "question", "answer");

        vm.RenameConversationCommand.Execute(null);
        Assert.True(vm.IsRenaming);
        Assert.Equal("question", vm.RenameText);
        Assert.False(vm.RenameConversationCommand.CanExecute(null));
        vm.RenameText = "  Refactoring notes  ";
        vm.CommitRenameCommand.Execute(null);

        Assert.False(vm.IsRenaming);
        Assert.Equal("Refactoring notes", vm.CurrentConversation.Title);
        Assert.True(vm.CurrentConversation.HasCustomTitle);
        vm.Clear();
        Assert.Equal("Refactoring notes", vm.CurrentConversation.Title);
    }

    [Fact]
    public async Task Rename_CancelOrABlankNameKeepsTheTitle()
    {
        using var vm = await StartAsync();
        await ExchangeAsync(vm, "question", "answer");

        vm.BeginRename();
        vm.RenameText = "Something else";
        vm.CancelRenameCommand.Execute(null);
        vm.BeginRename();
        vm.RenameText = "   ";
        vm.CommitRename();

        Assert.Equal("question", vm.CurrentConversation.Title);
        Assert.False(vm.CurrentConversation.HasCustomTitle);
        Assert.False(vm.IsRenaming);
    }

    [Fact]
    public async Task Delete_AsksForAConversationWithMessagesAndShowsTheNeighbour()
    {
        using var vm = await StartAsync();
        await ExchangeAsync(vm, "older", "a");
        var older = vm.CurrentConversation;
        vm.NewConversation();
        await ExchangeAsync(vm, "newer", "b");
        var newer = vm.CurrentConversation;

        _confirm = false;
        Assert.False(vm.DeleteConversation(newer));
        Assert.Equal(["newer"], _confirmations);
        Assert.Equal(2, vm.Conversations.Count);

        _confirm = true;
        vm.DeleteConversationCommand.Execute(null);

        Assert.Equal([older], vm.Conversations);
        Assert.Same(older, vm.CurrentConversation);
        Assert.Equal("older", vm.Turns[0].Text);
    }

    [Fact]
    public async Task Delete_TheLastConversationLeavesANewEmptyOne()
    {
        using var vm = await StartAsync();
        await ExchangeAsync(vm, "only", "answer");
        var only = vm.CurrentConversation;

        Assert.True(vm.DeleteConversation(only));

        var fresh = Assert.Single(vm.Conversations);
        Assert.NotSame(only, fresh);
        Assert.Same(fresh, vm.CurrentConversation);
        Assert.Empty(vm.Turns);
        Assert.False(vm.DeleteConversationCommand.CanExecute(null));
    }

    [Fact]
    public async Task Delete_AnEmptyConversationDoesNotAsk()
    {
        using var vm = await StartAsync();
        await ExchangeAsync(vm, "kept", "answer");
        vm.NewConversation();
        var empty = vm.CurrentConversation;

        Assert.True(vm.DeleteConversation(empty));

        Assert.Empty(_confirmations);
        Assert.Equal("kept", Assert.Single(vm.Conversations).Title);
    }

    [Theory]
    [InlineData("Short question", "Short question")]
    [InlineData("\n\n  First line  \nsecond line", "First line")]
    [InlineData("Explain how the incremental Markdown renderer decides which blocks to keep", "Explain how the incremental Markdown renderer…")]
    [InlineData("   ", null)]
    public void TitleFrom_UsesTheFirstLineCutAtAWord(string message, string? title) =>
        Assert.Equal(title, ChatConversation.TitleFrom(message));

    private async Task<ChatViewModel> StartAsync()
    {
        var vm = new ChatViewModel(
            _connection,
            _context.Factory,
            new ChatViewModelOptions
            {
                FlushInterval = TimeSpan.Zero,
                ConfirmDelete = c =>
                {
                    _confirmations.Add(c.Title);
                    return _confirm;
                },
            });
        vm.Start();
        await Wait.UntilAsync(() => vm.ModelPicker.SelectedModel is not null, "the models are listed");
        return vm;
    }

    private async Task ExchangeAsync(ChatViewModel vm, string question, string answer)
    {
        var script = _models.Next();
        vm.InputText = question;
        var send = vm.SendAsync();
        await script.Started.Task.WithTimeout(Timeout);
        script.Send(answer, isFinal: true);
        await send.WithTimeout(Timeout);
    }
}
