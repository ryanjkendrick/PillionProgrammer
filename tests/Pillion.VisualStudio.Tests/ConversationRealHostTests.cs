using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;
using Pillion.Contracts;
using Pillion.VisualStudio.Core;

namespace Pillion.VisualStudio.Tests;

// The tests await their own TaskCompletionSource tasks and the view models' tasks; no main thread is involved.
#pragma warning disable VSTHRD003

/// <summary>
/// The Chat window's saved conversations over the real pipe: tests/Pillion.FakeHost keeps conversations in memory the way
/// Pillion Host keeps them in the Local Store (the canned two, appended to by chat requests that carry a conversation id).
/// A second window on the same connection stands for the next Visual Studio session.
/// </summary>
[Collection(RealProcessTestGroup.Name)]
public sealed class ConversationRealHostTests : IDisposable
{
    private const string ChunkDelayVariable = "PILLION_FAKEHOST_CHUNK_DELAY_MS";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private readonly JoinableTaskContext _context = Task.Run(() => new JoinableTaskContext()).Result;
    private readonly string _logDirectory = Path.Combine(Path.GetTempPath(), "pillion-conv-tests-" + Guid.NewGuid().ToString("N"));
    private readonly PillionLog _log;

    public ConversationRealHostTests()
    {
        _log = new PillionLog(_logDirectory);
        Environment.SetEnvironmentVariable(ChunkDelayVariable, "1");
    }

    private static string FakeHostPath => typeof(ConversationRealHostTests).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .Single(a => a.Key == "FakeHostPath").Value!;

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(ChunkDelayVariable, null);
        _context.Dispose();
        try
        {
            Directory.Delete(_logDirectory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task TheWindowListsOpensAndContinuesTheHostsConversations_AndANewWindowSeesWhatTheLastOneSaved()
    {
        using var supervisor = await StartAsync();
        using var chat = NewWindow(supervisor);
        chat.Start();
        await Wait.UntilAsync(() => chat.Storage == ConversationStorage.Available, "the list is read", timeoutMs: 30000);

        // The canned conversations, newest first, behind the empty one that is open.
        Assert.Equal(["New conversation", "Fix the build", "Welcome to Pillion"], chat.Conversations.Select(c => c.Title));
        var build = chat.Conversations[1];
        chat.CurrentConversation = build;
        await Wait.UntilAsync(() => build.IsLoaded && !build.IsRestoringPins, "the conversation is read", timeoutMs: 30000);
        Assert.Equal(["Why does the build fail?", "Program.cs calls a method that no longer exists."], chat.Turns.Select(t => t.Text));
        var cited = Assert.Single(chat.Turns[1].Citations);
        Assert.Equal("Program.cs:12-15", cited.Text);
        Assert.Equal("Program.cs", Assert.Single(build.Pins).DisplayName);

        // A new conversation: the first message is saved by the host as the chat runs, cited spans included.
        var fresh = chat.Conversations[0];
        chat.CurrentConversation = fresh;
        chat.InputText = "Where is Main? /cite";
        await chat.SendAsync().WithTimeout(Timeout);
        var reply = chat.Turns.Last();
        Assert.True(fresh.IsPersisted, $"reply {reply.State} ({reply.ErrorMessage}); storage {chat.Storage}; text '{reply.Text}'");
        Assert.Equal(2, chat.Turns.Last().Citations.Count);

        // The next session: a new window reads the list the host kept.
        using var next = NewWindow(supervisor);
        next.Start();
        await Wait.UntilAsync(() => next.Storage == ConversationStorage.Available, "the list is read", timeoutMs: 30000);
        Assert.Equal(["New conversation", "Where is Main? /cite", "Fix the build", "Welcome to Pillion"], next.Conversations.Select(c => c.Title));
        var saved = next.Conversations[1];
        Assert.Equal(fresh.Id, saved.Id);
        next.CurrentConversation = saved;
        await Wait.UntilAsync(() => saved.IsLoaded, "the saved conversation is read", timeoutMs: 30000);
        Assert.Equal(["Where is Main? /cite", saved.Turns[1].Text], next.Turns.Select(t => t.Text));
        Assert.Equal(2, saved.Turns[1].Citations.Count);

        // Rename and delete reach the host.
        var conversations = supervisor.GetProxy<IConversationService>();
        next.RenameConversationCommand.Execute(saved);
        next.RenameText = "Main question";
        next.CommitRename();
        await Wait.UntilAsync(
            () => conversations.GetConversationAsync(saved.Id, CancellationToken.None).Result?.Conversation.Title == "Main question",
            "the new name reaches the host",
            timeoutMs: 30000);
        Assert.True(next.DeleteConversation(saved));
        await Wait.UntilAsync(() => conversations.GetConversationAsync(saved.Id, CancellationToken.None).Result is null, "the delete reaches the host", timeoutMs: 30000);
    }

    [Fact]
    public async Task DeleteAllOnThePrivacyPage_EmptiesTheHost_AndTheOpenWindow()
    {
        using var supervisor = await StartAsync();
        var events = new ConversationEvents();
        using var chat = NewWindow(supervisor, events);
        chat.Start();
        await Wait.UntilAsync(() => chat.Storage == ConversationStorage.Available && chat.Conversations.Count == 3, "the list is read", timeoutMs: 30000);
        using var privacy = new PrivacyOptionsViewModel(supervisor, _context.Factory, _log, events) { ConfirmDeleteAll = _ => true };
        await privacy.LoadAsync().WithTimeout(Timeout);

        await privacy.DeleteAllConversationsAsync().WithTimeout(Timeout);

        Assert.StartsWith("Deleted 2 conversations", privacy.DeleteAllStatus, StringComparison.Ordinal);
        await Wait.UntilAsync(() => chat.Conversations.Count == 1, "the window forgets the conversations", timeoutMs: 30000);
        Assert.Empty((await supervisor.GetProxy<IConversationService>().ListConversationsAsync(new ConversationListRequest(), CancellationToken.None)).Items);
    }

    private ChatViewModel NewWindow(HostSupervisor supervisor, ConversationEvents? events = null) =>
        new(supervisor, _context.Factory, new ChatViewModelOptions { Log = _log, FlushInterval = TimeSpan.Zero, ConversationEvents = events, ConfirmDelete = _ => true });

    private async Task<HostSupervisor> StartAsync()
    {
        Assert.True(File.Exists(FakeHostPath), "Build tests/Pillion.FakeHost first: " + FakeHostPath);
        var launcher = new HostProcessLauncher(
            new HostLauncherOptions(FakeHostPath, typeof(HostSupervisor).Assembly.Location, HostLocalTargets.CreateNotImplemented())
            {
                LogDirectory = _logDirectory,
            },
            _log);
        var supervisor = new HostSupervisor(launcher, _context.Factory, _log, SystemClock.Instance, new RestartPolicy());
        supervisor.Start();
        await supervisor.WhenReadyAsync(CancellationToken.None).WithTimeout(Timeout);
        return supervisor;
    }
}
