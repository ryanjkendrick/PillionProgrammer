using Pillion.Contracts;
using Pillion.LocalStore.Conversations;

namespace Pillion.LocalStore.Tests;

public sealed class RetentionTests : IDisposable
{
    private static readonly DateTimeOffset LongAgo = new(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task ByDefault_ConversationsAndTheOutboundLog_AreKeptUntilTheUserDeletesThem()
    {
        await using var store = new ConversationStore(_temp.NewPaths(), new FakeFeatureFlags());
        await SeedAsync(await store.GetRepositoryAsync());
        var retention = new ConversationRetention(store);

        Assert.Null(retention.Policy.Conversations.MaxAge);
        Assert.Null(retention.Policy.OutboundLog.MaxAge);
        Assert.Equal(new PruneResult(0, 0), await retention.PruneAsync());

        var repository = await store.GetRepositoryAsync();
        Assert.Single(await repository.ListConversationsAsync());
        Assert.Single(await repository.GetRecentOutboundAsync());
    }

    [Fact]
    public async Task AnAgeLimit_PrunesOnlyOlderData()
    {
        await using var store = new ConversationStore(_temp.NewPaths(), new FakeFeatureFlags());
        var repository = await store.GetRepositoryAsync();
        await SeedAsync(repository);
        var recent = DateTimeOffset.UtcNow;
        await repository.SaveConversationAsync(new ConversationRecord("recent", "R", recent, recent));
        await repository.AppendOutboundAsync(new OutboundLogEntry(recent, "new", "p", "m", TaskClass.Chat, true, 1, 1, 0, TimeSpan.Zero, "Succeeded"));
        var retention = new ConversationRetention(
            store,
            new ConversationRetentionPolicy { Conversations = new RetentionRule(TimeSpan.FromDays(30)), OutboundLog = new RetentionRule(TimeSpan.FromDays(30)) });

        Assert.Equal(new PruneResult(1, 1), await retention.PruneAsync());

        Assert.Equal(["recent"], (await repository.ListConversationsAsync()).Select(c => c.Id));
        Assert.Equal(["new"], (await repository.GetRecentOutboundAsync()).Select(e => e.RequestId));
    }

    [Fact]
    public async Task DeleteAll_RemovesEverything()
    {
        await using var store = new ConversationStore(_temp.NewPaths(), new FakeFeatureFlags());
        await SeedAsync(await store.GetRepositoryAsync());

        var result = await new ConversationRetention(store).DeleteAllAsync();

        Assert.Equal(1, result.Conversations);
        Assert.Equal(1, result.OutboundEntries);
        var repository = await store.GetRepositoryAsync();
        Assert.Empty(await repository.ListConversationsAsync());
        Assert.Empty(await repository.GetRecentOutboundAsync());
    }

    private static async Task SeedAsync(IConversationRepository repository)
    {
        await repository.SaveConversationAsync(new ConversationRecord("old", "Old", LongAgo, LongAgo));
        await repository.AddMessageAsync(new NewMessage("old", "user", "hello", LongAgo));
        await repository.AppendOutboundAsync(new OutboundLogEntry(LongAgo, "old", "p", "m", TaskClass.Chat, true, 1, 1, 0, TimeSpan.Zero, "Succeeded"));
    }
}
