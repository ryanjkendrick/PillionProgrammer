using Pillion.Contracts;
using Pillion.LocalStore.Conversations;

namespace Pillion.LocalStore.Tests;

public sealed class ConversationRepositoryTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 15, 9, 30, 0, 123, TimeSpan.Zero);
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task EveryTable_RoundTripsThroughAReopen()
    {
        var conversation = new ConversationRecord("conv-1", "Refactor the parser", T0, T0, "gpt-5");
        var outbound = new OutboundLogEntry(
            T0.AddSeconds(1),
            "req-1",
            "openai",
            "gpt-5",
            TaskClass.Chat,
            IsLocal: false,
            PromptTokens: 1234,
            CompletionTokens: 56,
            RedactionCount: 2,
            Duration: TimeSpan.FromMilliseconds(987),
            Outcome: "Failed",
            ErrorCode: PillionErrorCode.ProviderUnavailable,
            PromptTokensCounted: true,
            CacheHit: false,
            TimeToFirstToken: TimeSpan.FromMilliseconds(321),
            ContextItems: 4,
            ContextTrimmed: 1,
            Spans: """[{"path":"src/a.cs","start":1,"end":20}]""");
        var cacheHit = new OutboundLogEntry(T0.AddSeconds(2), "req-2", "ollama", "qwen", TaskClass.Autocomplete, true, 0, 12, 0, TimeSpan.FromMilliseconds(3), "Succeeded", CacheHit: true);
        MessageRecord assistant;
        long firstId;

        await using (var database = await TestDatabases.OpenConversationsAsync(_temp))
        {
            var repository = new ConversationRepository(database);
            await repository.SaveConversationAsync(conversation);
            await repository.AddMessageAsync(new NewMessage("conv-1", "user", "Make it faster", T0.AddMinutes(1)));
            assistant = await repository.AddMessageAsync(new NewMessage("conv-1", "assistant", "Here is a faster parser.", T0.AddMinutes(2), "gpt-5", "openai", 1234, 56, 0.0123));
            await repository.ReplacePinsAsync("conv-1", [new PinRecord("file", "src/Parser.cs", 900), new PinRecord("errors", "", 40)]);
            firstId = await repository.AppendOutboundAsync(outbound);
            await repository.AppendOutboundAsync(cacheHit);
        }

        await using (var database = await TestDatabases.OpenConversationsAsync(_temp))
        {
            var repository = new ConversationRepository(database);

            Assert.Equal(conversation with { Updated = T0.AddMinutes(2) }, await repository.GetConversationAsync("conv-1"));
            var messages = await repository.GetMessagesAsync("conv-1");
            Assert.Equal(2, messages.Count);
            Assert.Equal("user", messages[0].Role);
            Assert.Null(messages[0].Model);
            Assert.Equal(assistant, messages[1]);
            Assert.True(messages[0].Id < messages[1].Id);
            Assert.Equal([new PinRecord("errors", "", 40), new PinRecord("file", "src/Parser.cs", 900)], await repository.GetPinsAsync("conv-1"));

            var log = await repository.GetRecentOutboundAsync();
            Assert.Equal(2, log.Count);
            Assert.Equal(outbound with { Id = firstId }, log[0]);
            Assert.Equal(cacheHit with { Id = firstId + 1 }, log[1]);
        }
    }

    [Fact]
    public async Task SavingAgain_UpdatesTitleAndModel_ButKeepsTheCreatedTime()
    {
        await using var database = await TestDatabases.OpenConversationsAsync(_temp);
        var repository = new ConversationRepository(database);

        await repository.SaveConversationAsync(new ConversationRecord("c", "Old", T0, T0));
        await repository.SaveConversationAsync(new ConversationRecord("c", "New", T0.AddDays(1), T0.AddDays(1), "m"));

        Assert.Equal(new ConversationRecord("c", "New", T0, T0.AddDays(1), "m"), await repository.GetConversationAsync("c"));
        Assert.Null(await repository.GetConversationAsync("missing"));
    }

    [Fact]
    public async Task Conversations_AreListedMostRecentlyUpdatedFirst()
    {
        await using var database = await TestDatabases.OpenConversationsAsync(_temp);
        var repository = new ConversationRepository(database);
        await repository.SaveConversationAsync(new ConversationRecord("a", "A", T0, T0));
        await repository.SaveConversationAsync(new ConversationRecord("b", "B", T0, T0.AddHours(1)));
        await repository.SaveConversationAsync(new ConversationRecord("c", "C", T0, T0.AddHours(2)));
        await repository.AddMessageAsync(new NewMessage("a", "user", "bump", T0.AddHours(3)));

        Assert.Equal(["a", "c", "b"], (await repository.ListConversationsAsync()).Select(c => c.Id));
        Assert.Equal(["a"], (await repository.ListConversationsAsync(limit: 1)).Select(c => c.Id));
    }

    [Fact]
    public async Task AMessageOrPinsForAnUnknownConversation_AreRejected()
    {
        await using var database = await TestDatabases.OpenConversationsAsync(_temp);
        var repository = new ConversationRepository(database);

        await Assert.ThrowsAsync<KeyNotFoundException>(() => repository.AddMessageAsync(new NewMessage("nope", "user", "hi", T0)));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => repository.ReplacePinsAsync("nope", [new PinRecord("file", "a.cs", 1)]));
        Assert.Empty(await repository.GetMessagesAsync("nope"));
    }

    [Fact]
    public async Task DeletingAConversation_DeletesItsMessagesAndPins_Only()
    {
        await using var database = await TestDatabases.OpenConversationsAsync(_temp);
        var repository = new ConversationRepository(database);
        foreach (var id in new[] { "keep", "drop" })
        {
            await repository.SaveConversationAsync(new ConversationRecord(id, id, T0, T0));
            await repository.AddMessageAsync(new NewMessage(id, "user", "hello", T0));
            await repository.ReplacePinsAsync(id, [new PinRecord("file", "a.cs", 3)]);
        }

        Assert.True(await repository.DeleteConversationAsync("drop"));
        Assert.False(await repository.DeleteConversationAsync("drop"));

        Assert.Null(await repository.GetConversationAsync("drop"));
        Assert.Empty(await repository.GetMessagesAsync("drop"));
        Assert.Empty(await repository.GetPinsAsync("drop"));
        Assert.Single(await repository.GetMessagesAsync("keep"));
        Assert.Single(await repository.GetPinsAsync("keep"));
    }

    [Fact]
    public async Task RecentOutbound_ReturnsTheLatestEntries_OldestFirst()
    {
        await using var database = await TestDatabases.OpenConversationsAsync(_temp);
        var repository = new ConversationRepository(database);
        for (var i = 0; i < 5; i++)
        {
            await repository.AppendOutboundAsync(new OutboundLogEntry(T0.AddSeconds(i), $"r{i}", "p", "m", TaskClass.Chat, true, 1, 1, 0, TimeSpan.Zero, "Succeeded"));
        }

        Assert.Equal(["r2", "r3", "r4"], (await repository.GetRecentOutboundAsync(limit: 3)).Select(e => e.RequestId));
    }

    [Fact]
    public async Task DeleteAll_EmptiesEveryTable_AndRemovesMigrationBackups()
    {
        await using var database = await TestDatabases.OpenConversationsAsync(_temp);
        var repository = new ConversationRepository(database);
        await repository.SaveConversationAsync(new ConversationRecord("c", "C", T0, T0));
        await repository.AddMessageAsync(new NewMessage("c", "user", "one", T0));
        await repository.AddMessageAsync(new NewMessage("c", "assistant", "two", T0));
        await repository.ReplacePinsAsync("c", [new PinRecord("file", "a.cs", 3)]);
        await repository.AppendOutboundAsync(new OutboundLogEntry(T0, "r", "p", "m", TaskClass.Chat, true, 1, 1, 0, TimeSpan.Zero, "Succeeded"));
        await File.WriteAllBytesAsync(_temp.File("conversations.db.v1.bak"), [1, 2, 3]);

        var result = await repository.DeleteAllAsync();

        Assert.Equal(new DeleteAllResult(1, 2, 1, 1, 1), result);
        Assert.Empty(await repository.ListConversationsAsync());
        Assert.Empty(await repository.GetRecentOutboundAsync());
        Assert.Empty(await repository.GetMessagesAsync("c"));
        Assert.False(File.Exists(_temp.File("conversations.db.v1.bak")));
        Assert.Equal("1", await database.ScalarAsync("SELECT value FROM meta WHERE key = 'schema_version'")); // The schema stays.
    }
}
