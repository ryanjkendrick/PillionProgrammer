using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging.Abstractions;
using Pillion.Common;
using Pillion.Common.Settings;
using Pillion.Contracts;
using Pillion.Host.Conversations;
using Pillion.Host.Models;
using Pillion.Host.Tests.TestSupport;
using Pillion.LocalStore;
using Pillion.LocalStore.Conversations;
using Pillion.ModelGateway;
using ContractChatMessage = Pillion.Contracts.ChatMessage;
using ContractChatRole = Pillion.Contracts.ChatRole;

namespace Pillion.Host.Tests;

/// <summary>
/// Work item 2.2 in the host, over a real encrypted store in a temporary folder: chat requests are saved as they run (the user
/// message, then the reply once, or what arrived with a stopped marker), the conversation service reads and edits them, the
/// Outbound log is written to the store as records are produced and read back from it, and Delete all clears everything.
/// </summary>
#pragma warning disable CA1001 // The store, sink and folder are disposed by DisposeAsync.
public sealed class ConversationPersistenceTests : IAsyncLifetime
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private readonly TempDirectory _data = new();
    private readonly FakeFlags _flags = new();
    private readonly ManualTimeProvider _time = new();
    private readonly List<ConversationStore> _stores = [];
    private readonly StubGateway _gateway = new();

    private InMemoryOutboundLog _outbound = null!;
    private ConversationStore _store = null!;
    private StoreOutboundSink _sink = null!;
    private ConversationRecorder _recorder = null!;
    private ModelRpcService _models = null!;
    private ConversationRpcService _conversations = null!;

    public Task InitializeAsync()
    {
        Open();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        foreach (var store in _stores)
        {
            await store.DisposeAsync();
        }

        _data.Dispose();
    }

    [Fact]
    public async Task AChatWithAnUnknownConversationId_CreatesItTitledFromTheFirstLine_AndSavesBothMessages()
    {
        _gateway.Reply("The parser ", "is in Parser.cs.", final: new ChatChunk
        {
            ModelId = "gpt-test",
            ProviderId = "openai",
            PromptTokens = 120,
            CompletionTokens = 9,
            Citations = [new Citation { FilePath = @"C:\src\Parser.cs", StartLine = 10, EndLine = 24, ChunkId = "idx:3", Breadcrumb = "src/Parser.cs > Parser", ReplyOffset = 11 }],
        });
        var firstLine = new string('a', 80);

        await ChatAsync(Request("c1", $"\n  {firstLine}  \nsecond line"));

        var detail = (await _conversations.GetConversationAsync("c1", CancellationToken.None))!;
        Assert.Equal(new string('a', 60), detail.Conversation.Title);
        Assert.Equal(2, detail.Conversation.MessageCount);
        Assert.Equal("openai/gpt-test", detail.Conversation.LastModelId);
        Assert.Equal(ContractChatRole.User, detail.Messages[0].Role);
        Assert.Contains("second line", detail.Messages[0].Content, StringComparison.Ordinal);
        var reply = detail.Messages[1];
        Assert.Equal(ContractChatRole.Assistant, reply.Role);
        Assert.Equal("The parser is in Parser.cs.", reply.Content);
        Assert.Equal("gpt-test", reply.ModelId);
        Assert.Equal("openai", reply.ProviderId);
        Assert.Equal(120, reply.PromptTokens);
        Assert.Equal(9, reply.CompletionTokens);
        var citation = Assert.Single(reply.Citations);
        Assert.Equal(@"C:\src\Parser.cs", citation.FilePath);
        Assert.Equal((10, 24, 11), (citation.StartLine, citation.EndLine, citation.ReplyOffset));
        Assert.Equal("idx:3", citation.ChunkId);
        Assert.Equal("src/Parser.cs > Parser", citation.Breadcrumb);
    }

    [Fact]
    public async Task TheSavedConversation_SurvivesAHostRestart()
    {
        _gateway.Reply("Hello there.", final: Final("fake-chat", "fake"));
        await ChatAsync(Request("c1", "Hi"));
        await _store.DisposeAsync();

        Open(); // A new host process: nothing is in memory.

        var page = await _conversations.ListConversationsAsync(new ConversationListRequest(), CancellationToken.None);
        var item = Assert.Single(page.Items);
        Assert.Equal(("c1", "Hi", 2), (item.Id, item.Title, item.MessageCount));
        var detail = (await _conversations.GetConversationAsync("c1", CancellationToken.None))!;
        Assert.Equal(["Hi", "Hello there."], detail.Messages.Select(m => m.Content));
    }

    [Fact]
    public async Task ARequestWithoutAConversationId_IsNotSaved()
    {
        _gateway.Reply("Quick answer.", final: Final("m", "p"));

        await ChatAsync(Request(string.Empty, "Anonymous question"));

        Assert.Empty((await _conversations.ListConversationsAsync(new ConversationListRequest(), CancellationToken.None)).Items);
    }

    [Fact]
    public async Task ASecondTurn_AppendsToTheSameConversation_AndMovesItUp()
    {
        _gateway.Reply("one", final: Final("m", "p"));
        await ChatAsync(Request("older", "first"));
        _time.Advance(TimeSpan.FromMinutes(1));
        _gateway.Reply("two", final: Final("m", "p"));
        await ChatAsync(Request("newer", "other"));
        _time.Advance(TimeSpan.FromMinutes(1));
        _gateway.Reply("three", final: Final("m", "p"));

        await ChatAsync(Request("older", "second", history: [("first", "one")]));

        var page = await _conversations.ListConversationsAsync(new ConversationListRequest(), CancellationToken.None);
        Assert.Equal(["older", "newer"], page.Items.Select(i => i.Id));
        Assert.Equal(4, page.Items[0].MessageCount);
        Assert.Equal("first", page.Items[0].Title); // The first message names it; later ones do not.
        var detail = (await _conversations.GetConversationAsync("older", CancellationToken.None))!;
        Assert.Equal(["first", "one", "second", "three"], detail.Messages.Select(m => m.Content));
    }

    [Fact]
    public async Task AReplyStoppedPartWay_IsSavedWithWhatArrived_AndAStoppedMarker()
    {
        using var cancel = new CancellationTokenSource();
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _gateway.Script = (_, token) => Hang(["partial answer "], reached, token);

        var chat = ChatAsync(Request("c1", "Explain"), cancel.Token);
        await reached.Task.WaitAsync(Wait);
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => chat);

        var repository = await _store.GetRepositoryAsync();
        var stored = (await repository.GetMessagesAsync("c1"))[^1];
        Assert.True(stored.Cancelled);
        Assert.Equal("partial answer ", stored.Content);
        var message = (await _conversations.GetConversationAsync("c1", CancellationToken.None))!.Messages[^1];
        Assert.Equal("partial answer " + ConversationMapping.StoppedMarker, message.Content);
    }

    [Fact]
    public async Task AReplyStoppedBeforeAnyText_SavesOnlyTheUserMessage()
    {
        using var cancel = new CancellationTokenSource();
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _gateway.Script = (_, token) => Hang([], reached, token);

        var chat = ChatAsync(Request("c1", "Explain"), cancel.Token);
        await reached.Task.WaitAsync(Wait);
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => chat);

        var detail = (await _conversations.GetConversationAsync("c1", CancellationToken.None))!;
        Assert.Equal(ContractChatRole.User, Assert.Single(detail.Messages).Role);
    }

    [Fact]
    public async Task AFailedReply_IsNotSaved_AndRetryingItDoesNotRepeatTheUserMessage()
    {
        _gateway.Script = (_, _) => Fail(new InvalidOperationException("provider down"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => ChatAsync(Request("c1", "Explain")));
        Assert.Equal(ContractChatRole.User, Assert.Single((await _conversations.GetConversationAsync("c1", CancellationToken.None))!.Messages).Role);

        _gateway.Reply("It works now.", final: Final("m", "p"));
        await ChatAsync(Request("c1", "Explain")); // The Chat window sends the same history again.

        var detail = (await _conversations.GetConversationAsync("c1", CancellationToken.None))!;
        Assert.Equal(["Explain", "It works now."], detail.Messages.Select(m => m.Content));
    }

    [Fact]
    public async Task AReplyThatEndsWithAnError_KeepsTheUserMessageOnly()
    {
        _gateway.Reply("half an ans", final: new ChatChunk
        {
            ModelId = "m",
            ProviderId = "p",
            Error = new PillionError { Code = PillionErrorCode.ProviderUnavailable, Message = "Connection lost." },
        });

        await ChatAsync(Request("c1", "Explain"));

        Assert.Single((await _conversations.GetConversationAsync("c1", CancellationToken.None))!.Messages);
    }

    [Fact]
    public async Task ACloudReplysCost_IsWorkedOutFromTheCatalogPrices_AndALocalOnesIsZero()
    {
        _outbound.Record(Record("cloud-1", isLocal: false, modelId: "claude-fable-5-1"));
        _outbound.Record(Record("local-1", isLocal: true, modelId: "qwen"));
        _gateway.Reply("cloud", final: Final("claude-fable-5-1", "anthropic", prompt: 1_000_000, completion: 100_000));
        await ChatAsync(Request("c1", "q", requestId: "cloud-1"));
        _gateway.Reply("local", final: Final("qwen", "ollama", prompt: 500, completion: 50));
        await ChatAsync(Request("c2", "q", requestId: "local-1"));

        var repository = await _store.GetRepositoryAsync();
        Assert.Equal(15.0, (await repository.GetMessagesAsync("c1"))[1].Cost!.Value, precision: 6); // $10 in + $5 out.
        Assert.Equal(0.0, (await repository.GetMessagesAsync("c2"))[1].Cost);
    }

    [Fact]
    public async Task WithTheLocalStoreFlagOff_NothingIsSaved_AndTheServiceNamesTheFlag()
    {
        _flags.Disabled.Add(FeatureNames.LocalStore);
        _gateway.Reply("ok", final: Final("m", "p"));

        await ChatAsync(Request("c1", "Hi"));
        _outbound.Record(Record("r1"));

        var error = await Assert.ThrowsAsync<PillionRpcException>(() => _conversations.ListConversationsAsync(new ConversationListRequest(), CancellationToken.None));
        Assert.Equal(PillionErrorCode.PolicyDenied, error.Error.Code);
        Assert.Contains("localStore", error.Error.Message, StringComparison.Ordinal);
        _flags.Disabled.Clear();
        Assert.Empty((await _conversations.ListConversationsAsync(new ConversationListRequest(), CancellationToken.None)).Items);
        Assert.Empty(await (await _store.GetRepositoryAsync()).GetRecentOutboundAsync());
    }

    [Fact]
    public async Task AStoreThatCannotBeUsed_FailsTheServiceWithStorageUnavailable_ButNotTheChat()
    {
        var broken = new ThrowingStore(new InvalidOperationException("disk on fire"));
        var recorder = new ConversationRecorder(broken, _flags, time: _time);
        var models = new ModelRpcService(_gateway, _flags, recorder);
        var service = new ConversationRpcService(broken, _flags, time: _time);
        _gateway.Reply("still answers", final: Final("m", "p"));

        var text = string.Concat((await Collect(models.ChatAsync(Request("c1", "Hi"), CancellationToken.None))).Select(c => c.Text));
        var error = await Assert.ThrowsAsync<PillionRpcException>(() => service.ListConversationsAsync(new ConversationListRequest(), CancellationToken.None));

        Assert.Equal("still answers", text);
        Assert.Equal(PillionErrorCode.StorageUnavailable, error.Error.Code);

        var keyProblem = new ThrowingStore(new LocalStoreException(LocalStoreErrorKind.KeyMismatch, "The key does not open the conversations database."));
        var passed = await Assert.ThrowsAsync<LocalStoreException>(() => new ConversationRpcService(keyProblem, _flags).GetConversationAsync("c1", CancellationToken.None));
        Assert.Equal(LocalStoreErrorKind.KeyMismatch, passed.Kind);
    }

    [Fact]
    public async Task Create_ReturnsAnExistingConversationUnchanged_AndNamesAnEmptyIdForItself()
    {
        var first = await _conversations.CreateConversationAsync(new ConversationCreateRequest { Id = "c1", Title = "  My title  ", ModelId = "p/m" }, CancellationToken.None);
        var again = await _conversations.CreateConversationAsync(new ConversationCreateRequest { Id = "c1", Title = "Other", ModelId = "x/y" }, CancellationToken.None);
        var generated = await _conversations.CreateConversationAsync(new ConversationCreateRequest(), CancellationToken.None);

        Assert.Equal(("c1", "My title", "p/m", 0), (first.Id, first.Title, first.LastModelId, first.MessageCount));
        Assert.Equal(("My title", "p/m"), (again.Title, again.LastModelId));
        Assert.Equal(32, generated.Id.Length);
        Assert.Equal(string.Empty, generated.Title);
    }

    [Fact]
    public async Task AConversationCreatedEmpty_IsNamedByItsFirstMessage()
    {
        await _conversations.CreateConversationAsync(new ConversationCreateRequest { Id = "c1" }, CancellationToken.None);
        _gateway.Reply("ok", final: Final("m", "p"));

        await ChatAsync(Request("c1", "Name me from this"));

        Assert.Equal("Name me from this", (await _conversations.GetConversationAsync("c1", CancellationToken.None))!.Conversation.Title);
    }

    [Fact]
    public async Task Rename_TrimsTheTitle_KeepsItWhenEmpty_AndDoesNotMoveTheConversation()
    {
        await _conversations.CreateConversationAsync(new ConversationCreateRequest { Id = "old", Title = "Old" }, CancellationToken.None);
        _time.Advance(TimeSpan.FromMinutes(5));
        await _conversations.CreateConversationAsync(new ConversationCreateRequest { Id = "new", Title = "New" }, CancellationToken.None);

        Assert.True(await _conversations.RenameConversationAsync("old", "  Renamed  ", CancellationToken.None));
        Assert.True(await _conversations.RenameConversationAsync("new", "   ", CancellationToken.None));
        Assert.False(await _conversations.RenameConversationAsync("nope", "x", CancellationToken.None));

        var items = (await _conversations.ListConversationsAsync(new ConversationListRequest(), CancellationToken.None)).Items;
        Assert.Equal([("new", "New"), ("old", "Renamed")], items.Select(i => (i.Id, i.Title)));
    }

    [Fact]
    public async Task Delete_RemovesAConversationWithItsMessages_AndAnswersFalseForAnUnknownOne()
    {
        _gateway.Reply("ok", final: Final("m", "p"));
        await ChatAsync(Request("c1", "Hi"));
        await _conversations.SetPinsAsync("c1", [new ConversationPin { Kind = MentionKinds.File, Target = @"C:\a.cs", TokenCount = 5 }], CancellationToken.None);

        Assert.True(await _conversations.DeleteConversationAsync("c1", CancellationToken.None));
        Assert.False(await _conversations.DeleteConversationAsync("c1", CancellationToken.None));

        Assert.Null(await _conversations.GetConversationAsync("c1", CancellationToken.None));
        var repository = await _store.GetRepositoryAsync();
        Assert.Empty(await repository.GetMessagesAsync("c1"));
        Assert.Empty(await repository.GetPinsAsync("c1"));
    }

    [Fact]
    public async Task ListConversations_PagesNewestFirst_AndSaysWhenMoreFollow()
    {
        for (var i = 0; i < 5; i++)
        {
            await _conversations.CreateConversationAsync(new ConversationCreateRequest { Id = "c" + i, Title = "T" + i }, CancellationToken.None);
            _time.Advance(TimeSpan.FromMinutes(1));
        }

        var first = await _conversations.ListConversationsAsync(new ConversationListRequest { Offset = 0, Limit = 2 }, CancellationToken.None);
        var second = await _conversations.ListConversationsAsync(new ConversationListRequest { Offset = 2, Limit = 2 }, CancellationToken.None);
        var last = await _conversations.ListConversationsAsync(new ConversationListRequest { Offset = 4, Limit = 2 }, CancellationToken.None);
        var all = await _conversations.ListConversationsAsync(new ConversationListRequest(), CancellationToken.None);

        Assert.Equal(["c4", "c3"], first.Items.Select(i => i.Id));
        Assert.True(first.HasMore);
        Assert.Equal(["c2", "c1"], second.Items.Select(i => i.Id));
        Assert.True(second.HasMore);
        Assert.Equal(["c0"], last.Items.Select(i => i.Id));
        Assert.False(last.HasMore);
        Assert.Equal(5, all.Items.Length);
        Assert.False(all.HasMore);
    }

    [Fact]
    public async Task Pins_ComeBackInTheOrderTheyWereSet_WithTheirNamesAndTokenCounts()
    {
        await _conversations.CreateConversationAsync(new ConversationCreateRequest { Id = "c1" }, CancellationToken.None);
        ConversationPin[] pins =
        [
            new() { Kind = MentionKinds.File, Target = @"C:\src\Zed.cs", DisplayName = "Zed.cs", TokenCount = 1200 },
            new() { Kind = MentionKinds.Errors, Target = string.Empty, DisplayName = "Errors", TokenCount = 40 },
            new() { Kind = MentionKinds.Folder, Target = @"C:\src", DisplayName = "src", TokenCount = 300 },
        ];

        Assert.True(await _conversations.SetPinsAsync("c1", pins, CancellationToken.None));
        Assert.False(await _conversations.SetPinsAsync("nope", pins, CancellationToken.None));

        var back = (await _conversations.GetConversationAsync("c1", CancellationToken.None))!.Pins;
        Assert.Equal(["Zed.cs", "Errors", "src"], back.Select(p => p.DisplayName));
        Assert.Equal([1200, 40, 300], back.Select(p => p.TokenCount));
        Assert.Equal([MentionKinds.File, MentionKinds.Errors, MentionKinds.Folder], back.Select(p => p.Kind));

        Assert.True(await _conversations.SetPinsAsync("c1", [], CancellationToken.None));
        Assert.Empty((await _conversations.GetConversationAsync("c1", CancellationToken.None))!.Pins);
    }

    [Fact]
    public async Task TheOutboundLog_IsWrittenToTheStoreAsRecordsAreProduced_AndSurvivesARestart()
    {
        _outbound.Record(Record("r1", prompt: 100));
        _outbound.Record(Record("r2", prompt: 200, outcome: OutboundOutcome.Failed, error: PillionErrorCode.ProviderUnavailable));
        var diagnostics = Diagnostics();

        var live = await diagnostics.GetOutboundRecordsAsync(10, CancellationToken.None);
        await _store.DisposeAsync();

        Open(); // A new host: an empty in-memory log, the same store.
        var afterRestart = await Diagnostics().GetOutboundRecordsAsync(10, CancellationToken.None);

        Assert.Equal(["r2", "r1"], live.Select(r => r.RequestId));
        Assert.Equal(["r2", "r1"], afterRestart.Select(r => r.RequestId));
        Assert.Equal((200, "Failed", PillionErrorCode.ProviderUnavailable), (afterRestart[0].PromptTokens, afterRestart[0].Outcome, afterRestart[0].ErrorCode));
        Assert.Equal(100, afterRestart[1].PromptTokens);
    }

    [Fact]
    public async Task TheOutboundLogView_FallsBackToTheInMemoryLog_WhenTheStoreIsOff()
    {
        _outbound.Record(Record("r1"));
        await _sink.FlushAsync(Wait);
        _flags.Disabled.Add(FeatureNames.LocalStore);
        _outbound.Record(Record("r2"));

        var records = await Diagnostics().GetOutboundRecordsAsync(10, CancellationToken.None);

        Assert.Equal(["r2", "r1"], records.Select(r => r.RequestId));
        _flags.Disabled.Clear();
        Assert.Equal(["r1"], (await (await _store.GetRepositoryAsync()).GetRecentOutboundAsync()).Select(e => e.RequestId)); // r2 was never written.
    }

    [Fact]
    public async Task DeleteAll_ClearsConversationsMessagesPinsTheStoredOutboundLogAndTheInMemoryOne()
    {
        _gateway.Reply("ok", final: Final("m", "p"));
        await ChatAsync(Request("c1", "Hi"));
        await _conversations.SetPinsAsync("c1", [new ConversationPin { Kind = MentionKinds.File, Target = "a.cs" }], CancellationToken.None);
        _outbound.Record(Record("r1"));
        _outbound.Record(Record("r2"));
        await _sink.FlushAsync(Wait);

        var result = await _conversations.DeleteAllConversationsAsync(CancellationToken.None);

        Assert.Equal((1, 2, 1, 2), (result.Conversations, result.Messages, result.Pins, result.OutboundEntries));
        Assert.Empty(_outbound.GetRecent());
        Assert.Empty((await _conversations.ListConversationsAsync(new ConversationListRequest(), CancellationToken.None)).Items);
        Assert.Empty(await Diagnostics().GetOutboundRecordsAsync(10, CancellationToken.None));
        var repository = await _store.GetRepositoryAsync();
        Assert.Empty(await repository.GetRecentOutboundAsync());

        // Records produced afterwards are kept again.
        _outbound.Record(Record("r3"));
        Assert.Equal(["r3"], (await Diagnostics().GetOutboundRecordsAsync(10, CancellationToken.None)).Select(r => r.RequestId));
    }

    [Fact]
    public async Task ARecordQueuedBeforeDeleteAll_IsNotWrittenAfterIt()
    {
        var slow = new GatedStore(_store);
        var sink = new StoreOutboundSink(slow, _flags, _time);
        var service = new ConversationRpcService(_store, _flags, sink, _outbound, _time);
        sink.Write(Record("queued")); // The sink's writer has taken it and waits for the store to open.

        await service.DeleteAllConversationsAsync(CancellationToken.None);
        slow.Open();
        await sink.FlushAsync(Wait);

        Assert.Empty(await (await _store.GetRepositoryAsync()).GetRecentOutboundAsync());
        await sink.DisposeAsync();
    }

    [Fact]
    public void TitleFrom_TakesTheFirstLineWithText_AtMostSixtyCharacters_WithoutSplittingAPair()
    {
        Assert.Equal("Hello", ConversationMapping.TitleFrom("\r\n\r\n  Hello  \r\nworld"));
        Assert.Equal(string.Empty, ConversationMapping.TitleFrom("  \n \r\n"));
        Assert.Equal(string.Empty, ConversationMapping.TitleFrom(null));
        Assert.Equal(60, ConversationMapping.TitleFrom(new string('x', 200)).Length);
        var pairAtTheCut = new string('x', 59) + "\U0001F600" + "tail";
        Assert.Equal(new string('x', 59), ConversationMapping.TitleFrom(pairAtTheCut));
    }

    [Fact]
    public void Citations_RoundTripThroughTheirStoredForm_AndGarbageReadsAsNone()
    {
        Citation[] citations =
        [
            new() { FilePath = @"C:\a.cs", StartLine = 0, EndLine = 3, ChunkId = string.Empty, Breadcrumb = "a", ReplyOffset = -1 },
            new() { FilePath = @"C:\b.cs", StartLine = 7, EndLine = 9, ChunkId = "i:1", Breadcrumb = string.Empty, ReplyOffset = 0 },
        ];

        var back = ConversationMapping.ReadCitations(ConversationMapping.WriteCitations(citations));

        Assert.Equal(citations.Select(c => (c.FilePath, c.StartLine, c.EndLine, c.ChunkId, c.Breadcrumb, c.ReplyOffset)), back.Select(c => (c.FilePath, c.StartLine, c.EndLine, c.ChunkId, c.Breadcrumb, c.ReplyOffset)));
        Assert.Null(ConversationMapping.WriteCitations([]));
        Assert.Empty(ConversationMapping.ReadCitations("{not json"));
        Assert.Empty(ConversationMapping.ReadCitations(null));
    }

    private static ChatChunk Final(string model, string provider, int prompt = 10, int completion = 5) =>
        new() { ModelId = model, ProviderId = provider, PromptTokens = prompt, CompletionTokens = completion };

    private static ChatRequest Request(string conversationId, string text, string requestId = "req", (string User, string Assistant)[]? history = null)
    {
        var messages = new List<ContractChatMessage>();
        foreach (var (user, assistant) in history ?? [])
        {
            messages.Add(new ContractChatMessage { Role = ContractChatRole.User, Content = user });
            messages.Add(new ContractChatMessage { Role = ContractChatRole.Assistant, Content = assistant });
        }

        messages.Add(new ContractChatMessage { Role = ContractChatRole.User, Content = text });
        return new ChatRequest { RequestId = requestId, ConversationId = conversationId, Messages = messages };
    }

    private static OutboundRecord Record(
        string requestId,
        bool isLocal = true,
        string modelId = "m",
        int prompt = 10,
        OutboundOutcome outcome = OutboundOutcome.Succeeded,
        PillionErrorCode? error = null) =>
        new(DateTimeOffset.UtcNow, requestId, "p", modelId, TaskClass.Chat, isLocal, prompt, 5, 0, TimeSpan.FromMilliseconds(40), outcome, error);

    private static async IAsyncEnumerable<ChatChunk> Hang(string[] texts, TaskCompletionSource reached, [EnumeratorCancellation] CancellationToken token)
    {
        foreach (var text in texts)
        {
            yield return new ChatChunk { Text = text, ModelId = "m", ProviderId = "p" };
        }

        reached.TrySetResult();
        await Task.Delay(Timeout.Infinite, token);
    }

    private static async IAsyncEnumerable<ChatChunk> Fail(Exception exception)
    {
        await Task.Yield();
        throw exception;
#pragma warning disable CS0162 // Makes the method an iterator.
        yield break;
#pragma warning restore CS0162
    }

    private static async Task<List<ChatChunk>> Collect(IAsyncEnumerable<ChatChunk> stream, CancellationToken cancellationToken = default)
    {
        var chunks = new List<ChatChunk>();
        await foreach (var chunk in stream.WithCancellation(cancellationToken))
        {
            chunks.Add(chunk);
        }

        return chunks;
    }

    private Task<List<ChatChunk>> ChatAsync(ChatRequest request, CancellationToken cancellationToken = default) =>
        Collect(_models.ChatAsync(request, cancellationToken), cancellationToken);

    private DiagnosticsRpcService Diagnostics() =>
        new(new AutocompleteCounters(), _outbound, _time, features: _flags, store: _sink);

    /// <summary>Opens the store, sink and services on the temporary folder, as a host start does (the in-memory log starts empty).</summary>
    private void Open()
    {
        _store = new ConversationStore(new PillionPaths(_data.Path), _flags, NullLoggerFactory.Instance);
        _stores.Add(_store);
        _sink = new StoreOutboundSink(_store, _flags, _time);
        _outbound = new InMemoryOutboundLog(new ModelGatewayOptions(), null, [_sink]);
        _recorder = new ConversationRecorder(_store, _flags, outbound: _outbound, time: _time);
        _models = new ModelRpcService(_gateway, _flags, _recorder);
        _conversations = new ConversationRpcService(_store, _flags, _sink, _outbound, _time);
    }

    private sealed class FakeFlags : IFeatureFlags
    {
        public event EventHandler? Changed
        {
            add { }
            remove { }
        }

        public ConcurrentBag<string> Disabled { get; } = [];

        public bool IsEnabled(string feature) => !Disabled.Contains(feature);
    }

    private sealed class StubGateway : IModelGateway
    {
        public Func<ChatRequest, CancellationToken, IAsyncEnumerable<ChatChunk>>? Script { get; set; }

        public void Reply(string text, ChatChunk final) => Script = (request, _) => Stream(request, [text], final);

        public void Reply(string first, string second, ChatChunk final) => Script = (request, _) => Stream(request, [first, second], final);

        public IAsyncEnumerable<ChatChunk> ChatAsync(ChatRequest request, TaskClass taskClass, CancellationToken cancellationToken) =>
            (Script ?? throw new InvalidOperationException("No script."))(request, cancellationToken);

        public IAsyncEnumerable<CompletionChunk> CompleteAsync(CompletionRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<EmbeddingResult> EmbedAsync(EmbeddingRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<ModelDescriptor>> ListModelsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<ProviderTestResult> TestProviderAsync(string providerId, CancellationToken cancellationToken) => throw new NotSupportedException();

        private static async IAsyncEnumerable<ChatChunk> Stream(ChatRequest request, string[] texts, ChatChunk final)
        {
            foreach (var text in texts)
            {
                await Task.Yield();
                yield return new ChatChunk { RequestId = request.RequestId, Text = text, ModelId = final.ModelId, ProviderId = final.ProviderId };
            }

            final.RequestId = request.RequestId;
            final.IsFinal = true;
            yield return final;
        }
    }

    private sealed class ThrowingStore(Exception exception) : IConversationStore
    {
        public Task<IConversationRepository> GetRepositoryAsync(CancellationToken cancellationToken = default) => Task.FromException<IConversationRepository>(exception);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>A store whose repository is not handed out until <see cref="Open"/> is called.</summary>
    private sealed class GatedStore(IConversationStore inner) : IConversationStore
    {
        private readonly TaskCompletionSource _open = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Open() => _open.TrySetResult();

        public async Task<IConversationRepository> GetRepositoryAsync(CancellationToken cancellationToken = default)
        {
            await _open.Task.WaitAsync(cancellationToken);
            return await inner.GetRepositoryAsync(cancellationToken);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
