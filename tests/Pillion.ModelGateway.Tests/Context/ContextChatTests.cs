using Pillion.Contracts;
using Pillion.ModelGateway.Tests.TestSupport;

namespace Pillion.ModelGateway.Tests.Context;

/// <summary>Chat requests that carry pins, a current file or history, through the gateway with fake providers.</summary>
public class ContextChatTests
{
    private const string LocalModel = "local/qwen2.5-coder:14b";

    private static ChatRequest Plain(string text, string? model = null) => GatewayHarness.ChatRequest(text, model);

    private static string Lines(int tokens, string label)
    {
        var builder = new System.Text.StringBuilder();
        var n = 0;
        while (builder.Length < tokens * 4)
        {
            builder.Append(label).Append(n++).Append(' ').Append('y', 28).Append('\n');
        }

        return builder.ToString();
    }

    private static async Task<List<ChatChunk>> ChatAsync(GatewayHarness harness, ChatRequest request) =>
        await harness.Gateway.ChatAsync(request, TaskClass.Chat, CancellationToken.None).CollectAsync();

    [Fact]
    public async Task APinnedFile_ReachesTheModelInTheSystemMessage_AndTheOutboundRecordCountsTheItems()
    {
        using var harness = new GatewayHarness();
        var request = Plain("What does it do?", LocalModel);
        request.Pins =
        [
            new MentionResolution { Mention = "@file:Calc.cs", IsResolved = true, Kind = "file", FilePath = @"C:\src\Calc.cs", DisplayName = "Calc.cs", Content = "public static class Calc { }", TokenCount = 8 },
        ];

        var chunks = await ChatAsync(harness, request);

        Assert.True(chunks[^1].IsFinal);
        Assert.True(harness.Local!.ReceivedChats.TryDequeue(out var messages));
        Assert.Equal(2, messages.Count);
        Assert.Equal(Microsoft.Extensions.AI.ChatRole.System, messages[0].Role);
        Assert.StartsWith(PromptTemplates.Default.ChatSystemPrompt, messages[0].Text, StringComparison.Ordinal);
        Assert.Contains("public static class Calc { }", messages[0].Text, StringComparison.Ordinal);
        Assert.Equal("What does it do?", messages[1].Text);

        var record = Assert.Single(harness.OutboundLog.GetRecent());
        Assert.Equal(TaskClass.Chat, record.TaskClass);
        Assert.Equal(3, record.ContextItems); // System prompt, the pin, the message.
        Assert.Equal(0, record.ContextTrimmed);
        Assert.True(record.EstimatedPromptTokens > 8);
        Assert.Equal(record.EstimatedPromptTokens, chunks[^1].PromptTokens);
    }

    [Fact]
    public async Task APlainRequest_IsSentAsBefore_WithNoContextCounts()
    {
        using var harness = new GatewayHarness();

        await ChatAsync(harness, Plain("Hi", LocalModel));

        Assert.True(harness.Local!.ReceivedChats.TryDequeue(out var messages));
        Assert.Equal(2, messages.Count);
        Assert.Equal(PromptTemplates.Default.ChatSystemPrompt, messages[0].Text);
        Assert.Equal("Hi", messages[1].Text);
        var record = Assert.Single(harness.OutboundLog.GetRecent());
        Assert.Equal(0, record.ContextItems);
        Assert.Equal(0, record.ContextTrimmed);
    }

    [Fact]
    public async Task TruncatedAndDroppedPins_AreCountedInTheOutboundRecord()
    {
        using var harness = new GatewayHarness();
        harness.Local!.Models.Add(harness.Local.Model("small-chat", context: 3000));
        harness.ListAll();
        var request = Plain("Summarise.", "local/small-chat");
        request.Pins =
        [
            new MentionResolution { Mention = "@file:Big.cs", IsResolved = true, Kind = "file", FilePath = @"C:\src\Big.cs", DisplayName = "Big.cs", Content = Lines(4000, "big") },
            new MentionResolution { Mention = "@file:Other.cs", IsResolved = true, Kind = "file", FilePath = @"C:\src\Other.cs", DisplayName = "Other.cs", Content = Lines(2000, "other") },
        ];

        await ChatAsync(harness, request);

        var record = Assert.Single(harness.OutboundLog.GetRecent());
        Assert.Equal(4, record.ContextItems);
        Assert.InRange(record.ContextTrimmed, 1, 2);
        Assert.InRange(record.EstimatedPromptTokens, 1, 3000);
        Assert.True(harness.Local.ReceivedChats.TryDequeue(out var messages));
        Assert.Contains("lines omitted", messages[0].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASensitivePin_NeverReachesACloudModel_ButDoesReachALocalOne()
    {
        using var harness = new GatewayHarness();
        MentionResolution[] pins =
        [
            new MentionResolution { Mention = "@file:.env", IsResolved = true, Kind = "file", FilePath = @"C:\src\.env", DisplayName = ".env", Content = "TOP_SECRET_VALUE=1", IsSensitive = true },
        ];

        var cloudRequest = Plain("Hi", "cloud/cloud-balanced");
        cloudRequest.Pins = pins;
        await ChatAsync(harness, cloudRequest);
        var localRequest = Plain("Hi", LocalModel);
        localRequest.RequestId = "r2";
        localRequest.Pins = pins;
        await ChatAsync(harness, localRequest);

        Assert.True(harness.Cloud!.ReceivedChats.TryDequeue(out var cloudMessages));
        Assert.DoesNotContain("TOP_SECRET_VALUE", cloudMessages[0].Text, StringComparison.Ordinal);
        Assert.True(harness.Local!.ReceivedChats.TryDequeue(out var localMessages));
        Assert.Contains("TOP_SECRET_VALUE", localMessages[0].Text, StringComparison.Ordinal);
        var records = harness.OutboundLog.GetRecent();
        Assert.Equal(1, records[0].ContextTrimmed);
        Assert.Equal(0, records[1].ContextTrimmed);
    }

    [Fact]
    public async Task APinnedSecretInTheContent_IsStillRedactedForACloudModel()
    {
        using var harness = new GatewayHarness();
        var request = Plain("Hi", "cloud/cloud-balanced");
        request.Pins =
        [
            new MentionResolution { Mention = "@file:a.cs", IsResolved = true, Kind = "file", FilePath = @"C:\src\a.cs", DisplayName = "a.cs", Content = "var key = \"" + "AKIA" + "Q7RZ3MBX" + "5TLW2KNV" + "\";" },
        ];

        await ChatAsync(harness, request);

        Assert.True(harness.Cloud!.ReceivedChats.TryDequeue(out var messages));
        Assert.DoesNotContain("Q7RZ3MBX", messages[0].Text, StringComparison.Ordinal);
        Assert.Equal(1, Assert.Single(harness.OutboundLog.GetRecent()).RedactionCount);
    }

    [Fact]
    public async Task LongHistory_IsSummarisedByASummariseRequest_AndTheSummaryIsReusedOnTheNextTurn()
    {
        using var harness = new GatewayHarness();
        harness.Local!.Models.Add(harness.Local.Model("small-chat", context: 6000));
        harness.ListAll();
        harness.Local.Chat = (messages, _, ct) => messages[0].Text.Contains("You compress a conversation", StringComparison.Ordinal)
            ? FakeProvider.Pieces(ct, "The user is ", "refactoring Calc.")
            : FakeProvider.Pieces(ct, "Sure.");

        var history = new List<ChatMessage>();
        for (var i = 0; i < 10; i++)
        {
            history.Add(new ChatMessage { Role = ChatRole.User, Content = $"q{i} " + Lines(300, "u") });
            history.Add(new ChatMessage { Role = ChatRole.Assistant, Content = $"a{i} " + Lines(300, "a") });
        }

        ChatRequest Build(string text, string id) => new()
        {
            RequestId = id,
            Profile = Profile.Auto,
            ModelId = "local/small-chat",
            ConversationId = "conv-1",
            ProjectRules = "Be brief.",
            Messages = [.. history, new ChatMessage { Role = ChatRole.User, Content = text }],
        };

        await ChatAsync(harness, Build("first", "r1"));
        var summaries = harness.OutboundLog.GetRecent().Count(r => r.TaskClass == TaskClass.Summarise);
        await ChatAsync(harness, Build("second", "r2"));

        var records = harness.OutboundLog.GetRecent();
        Assert.True(summaries >= 1);
        Assert.Equal(summaries, records.Count(r => r.TaskClass == TaskClass.Summarise)); // The second turn reused the cached summary.
        var summaryRecords = records.Where(r => r.TaskClass == TaskClass.Summarise).ToList();
        Assert.All(summaryRecords, r => Assert.Equal("qwen2.5-coder:1.5b", r.ModelId)); // Kept local because the chat is local; Local-fast profile.
        Assert.All(summaryRecords, r => Assert.True(r.IsLocal));
        Assert.StartsWith("r1-summary", summaryRecords[0].RequestId, StringComparison.Ordinal);
        var chats = records.Where(r => r.TaskClass == TaskClass.Chat).ToList();
        Assert.Equal(2, chats.Count);
        Assert.All(chats, r => Assert.Equal("small-chat", r.ModelId));
        Assert.All(chats, r => Assert.True(r.EstimatedPromptTokens <= 6000 - 1500 + 40));
        Assert.All(chats, r => Assert.True(r.ContextItems >= 4));

        var received = harness.Local.ReceivedChats.ToList();
        Assert.Equal(summaries + 2, received.Count);
        Assert.Contains("You compress a conversation", received[0][0].Text, StringComparison.Ordinal);
        Assert.Contains("q0 ", received[0][1].Text, StringComparison.Ordinal);
        var firstChat = received[summaries];
        var secondChat = received[summaries + 1];
        Assert.Contains("The user is refactoring Calc.", firstChat[0].Text, StringComparison.Ordinal);
        Assert.Contains("The user is refactoring Calc.", secondChat[0].Text, StringComparison.Ordinal);
        Assert.Contains("Be brief.", firstChat[0].Text, StringComparison.Ordinal);
        Assert.Equal("first", firstChat[^1].Text);
        Assert.Equal("second", secondChat[^1].Text);
    }

    [Fact]
    public async Task WhenTheChatGoesToTheCloud_TheSummaryUsesTheFastCloudProfile()
    {
        using var harness = new GatewayHarness();
        harness.Cloud!.Models = [harness.Cloud.Model("cloud-fast", fim: true), harness.Cloud.Model("cloud-balanced", context: 6000), harness.Cloud.Model("cloud-frontier")];
        harness.ListAll();
        harness.Cloud.Chat = (messages, _, ct) => messages[0].Text.Contains("You compress a conversation", StringComparison.Ordinal)
            ? FakeProvider.Pieces(ct, "Summary text.")
            : FakeProvider.Pieces(ct, "Done.");
        var history = new List<ChatMessage>();
        for (var i = 0; i < 10; i++)
        {
            history.Add(new ChatMessage { Role = ChatRole.User, Content = $"q{i} " + Lines(300, "u") });
            history.Add(new ChatMessage { Role = ChatRole.Assistant, Content = $"a{i} " + Lines(300, "a") });
        }

        var request = new ChatRequest
        {
            RequestId = "r1",
            Profile = Profile.Auto,
            ConversationId = "c",
            CurrentFile = @"C:\src\A.cs",
            CurrentFileRegion = "class A { }",
            Messages = [.. history, new ChatMessage { Role = ChatRole.User, Content = "go" }],
        };

        await ChatAsync(harness, request);

        var records = harness.OutboundLog.GetRecent();
        Assert.All(records.Take(records.Count - 1), r =>
        {
            Assert.Equal(TaskClass.Summarise, r.TaskClass);
            Assert.Equal("cloud-fast", r.ModelId);
            Assert.False(r.IsLocal);
        });
        Assert.True(records.Count >= 2);
        Assert.Equal(TaskClass.Chat, records[^1].TaskClass);
        Assert.Equal("cloud-balanced", records[^1].ModelId);
    }

    [Fact]
    public async Task IfSummarisingFails_TheChatStillGoesThrough_WithoutTheOlderTurns()
    {
        using var harness = new GatewayHarness(withCloud: false);
        harness.Local!.Models.Add(harness.Local.Model("small-chat", context: 6000));
        harness.ListAll();
        harness.Local.Chat = (messages, _, ct) => messages[0].Text.Contains("You compress a conversation", StringComparison.Ordinal)
            ? Throwing(FakeProvider.Unreachable())
            : FakeProvider.Pieces(ct, "Answer.");
        var history = new List<ChatMessage>();
        for (var i = 0; i < 10; i++)
        {
            history.Add(new ChatMessage { Role = ChatRole.User, Content = $"q{i} " + Lines(300, "u") });
            history.Add(new ChatMessage { Role = ChatRole.Assistant, Content = $"a{i} " + Lines(300, "a") });
        }

        var request = new ChatRequest
        {
            RequestId = "r1",
            Profile = Profile.Auto,
            ModelId = "local/small-chat",
            ConversationId = "c",
            ProjectRules = "Rules.",
            Messages = [.. history, new ChatMessage { Role = ChatRole.User, Content = "go" }],
        };

        var chunks = await ChatAsync(harness, request);

        Assert.Equal("Answer.", string.Concat(chunks.Select(c => c.Text)));
        Assert.Null(chunks[^1].Error);
        var chat = harness.OutboundLog.GetRecent().Last(r => r.TaskClass == TaskClass.Chat);
        Assert.True(chat.ContextTrimmed >= 1);
    }

    [Fact]
    public async Task AMessageThatCannotFitTheModelsWindow_FailsWithContextOverflow()
    {
        using var harness = new GatewayHarness(withCloud: false);
        harness.Local!.Models = [harness.Local.Model("tiny", context: 1000)];
        harness.ListAll();
        var request = Plain(Lines(3000, "w"), "local/tiny");
        request.ProjectRules = "Rules.";

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => ChatAsync(harness, request));

        Assert.Equal(PillionErrorCode.ContextOverflow, Assert.IsType<PillionRpcException>(ex).Error.Code);
        Assert.Equal(0, harness.Local.ChatCalls);
    }

    private static async IAsyncEnumerable<string> Throwing(Exception exception)
    {
        await Task.Yield();
        throw exception;
#pragma warning disable CS0162 // Unreachable code detected: the iterator needs a yield to be an async iterator.
        yield break;
#pragma warning restore CS0162
    }
}
