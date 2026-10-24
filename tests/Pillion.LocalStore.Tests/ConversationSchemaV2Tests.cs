using Pillion.LocalStore.Conversations;

namespace Pillion.LocalStore.Tests;

/// <summary>Schema 2 of <c>conversations.db</c> (work item 2.2): citations, the stopped flag, pin order and names, and the repository calls that use them.</summary>
public sealed class ConversationSchemaV2Tests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 15, 9, 30, 0, TimeSpan.Zero);
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task ASchema1Database_IsBackedUpAndMigrated_WithItsMessagesAndPinsIntact()
    {
        var schemaOne = new StoreSchema(ConversationsSchema.Name, [ConversationsSchema.Schema.Migrations[0]]);
        await using (var v1 = await EncryptedDatabase.OpenAsync(new EncryptedDatabaseOptions { DatabasePath = _temp.File("conversations.db") }, schemaOne))
        {
            await v1.WriteAsync(async (connection, transaction, token) =>
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO conversations (id, title, created, updated) VALUES ('c', 'Old', 1000, 2000);
                    INSERT INTO messages (conversation_id, role, content, created) VALUES ('c', 'user', 'hello', 1000), ('c', 'assistant', 'hi', 2000);
                    INSERT INTO pins (conversation_id, kind, target, token_count) VALUES ('c', 'file', 'b.cs', 5), ('c', 'errors', '', 9);
                    """;
                await command.ExecuteNonQueryAsync(token);
            });
        }

        await using var database = await TestDatabases.OpenConversationsAsync(_temp);
        var repository = new ConversationRepository(database);

        Assert.Equal(1, database.OpenedAtVersion);
        Assert.Equal(2, database.SchemaVersion);
        Assert.Equal(1, database.MinReaderVersion); // Additive: a build that only knows schema 1 can still use the file.
        Assert.Equal(_temp.File("conversations.db.v1.bak"), database.BackupPath);
        var messages = await repository.GetMessagesAsync("c");
        Assert.Equal(["hello", "hi"], messages.Select(m => m.Content));
        Assert.All(messages, m => Assert.False(m.Cancelled));
        Assert.All(messages, m => Assert.Null(m.Citations));
        Assert.Equal(["errors", "file"], (await repository.GetPinsAsync("c")).Select(p => p.Kind)); // Unordered pins fall back to kind and target.
        Assert.All(await repository.GetPinsAsync("c"), p => Assert.Equal(string.Empty, p.DisplayName));
    }

    [Fact]
    public async Task CitationsAndTheStoppedFlag_RoundTrip()
    {
        await using var database = await TestDatabases.OpenConversationsAsync(_temp);
        var repository = new ConversationRepository(database);
        await repository.SaveConversationAsync(new ConversationRecord("c", "T", T0, T0));

        await repository.AddMessageAsync(new NewMessage("c", "assistant", "partial", T0, Citations: "[{\"path\":\"a.cs\"}]", Cancelled: true));
        var last = await repository.GetLastMessageAsync("c");

        Assert.Equal("partial", last!.Content);
        Assert.True(last.Cancelled);
        Assert.Equal("[{\"path\":\"a.cs\"}]", last.Citations);
        Assert.Null(await repository.GetLastMessageAsync("nope"));
    }

    [Fact]
    public async Task Pins_KeepTheOrderGiven_AndTheirNames_AndARepeatedPinKeepsItsFirstPosition()
    {
        await using var database = await TestDatabases.OpenConversationsAsync(_temp);
        var repository = new ConversationRepository(database);
        await repository.SaveConversationAsync(new ConversationRecord("c", "T", T0, T0));

        await repository.ReplacePinsAsync("c", [new PinRecord("file", "z.cs", 1, "z"), new PinRecord("errors", "", 2, "Errors"), new PinRecord("file", "z.cs", 9, "z2")]);

        Assert.Equal([new PinRecord("file", "z.cs", 9, "z2"), new PinRecord("errors", "", 2, "Errors")], await repository.GetPinsAsync("c"));
    }

    [Fact]
    public async Task TryCreate_LeavesAnExistingConversationAlone()
    {
        await using var database = await TestDatabases.OpenConversationsAsync(_temp);
        var repository = new ConversationRepository(database);

        Assert.True(await repository.TryCreateConversationAsync(new ConversationRecord("c", "First", T0, T0, "m")));
        Assert.False(await repository.TryCreateConversationAsync(new ConversationRecord("c", "Second", T0.AddHours(1), T0.AddHours(1), "other")));

        Assert.Equal(new ConversationRecord("c", "First", T0, T0, "m"), await repository.GetConversationAsync("c"));
    }

    [Fact]
    public async Task RenameAndSetModel_DoNotMoveTheConversation()
    {
        await using var database = await TestDatabases.OpenConversationsAsync(_temp);
        var repository = new ConversationRepository(database);
        await repository.SaveConversationAsync(new ConversationRecord("c", "T", T0, T0.AddMinutes(5)));

        Assert.True(await repository.RenameConversationAsync("c", "Renamed"));
        Assert.True(await repository.SetConversationModelAsync("c", "p/m"));
        Assert.False(await repository.RenameConversationAsync("nope", "x"));
        Assert.False(await repository.SetConversationModelAsync("nope", "x"));

        Assert.Equal(new ConversationRecord("c", "Renamed", T0, T0.AddMinutes(5), "p/m"), await repository.GetConversationAsync("c"));
    }

    [Fact]
    public async Task ListConversationPage_CountsMessages_AndPagesWithAnOffset()
    {
        await using var database = await TestDatabases.OpenConversationsAsync(_temp);
        var repository = new ConversationRepository(database);
        for (var i = 0; i < 4; i++)
        {
            await repository.SaveConversationAsync(new ConversationRecord("c" + i, "T" + i, T0, T0.AddMinutes(i)));
        }

        await repository.AddMessageAsync(new NewMessage("c1", "user", "a", T0.AddMinutes(1)));
        await repository.AddMessageAsync(new NewMessage("c1", "assistant", "b", T0.AddMinutes(1)));

        var page = await repository.ListConversationPageAsync(1, 2);

        Assert.Equal(["c2", "c1"], page.Select(i => i.Conversation.Id));
        Assert.Equal([0, 2], page.Select(i => i.MessageCount));
        Assert.Empty(await repository.ListConversationPageAsync(10, 5));
    }
}
