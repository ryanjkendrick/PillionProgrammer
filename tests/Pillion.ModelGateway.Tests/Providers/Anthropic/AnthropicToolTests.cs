using Microsoft.Extensions.AI;
using Pillion.Common.Secrets;
using Pillion.ModelGateway.Providers.Anthropic;
using Pillion.ModelGateway.Tests.Providers.Cloud;
using Pillion.ModelGateway.Tests.TestSupport;

namespace Pillion.ModelGateway.Tests.Providers.Anthropic;

/// <summary>The Anthropic tool transport against fixtures in the documented Messages API shape (work item 3.23).</summary>
public sealed class AnthropicToolTests
{
    private static AnthropicProvider Create(FakeCloudHandler handler)
    {
        var secrets = new InMemorySecretStore();
        secrets.Set("claude", AnthropicProviderTests.Key);
        return AnthropicProviderTests.Create(handler, secrets);
    }

    private static ChatOptions WithTools() => new() { Tools = [TestTools.GetWeather] };

    private static Task<List<ChatResponseUpdate>> ChatAsync(AnthropicProvider provider, IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages, ChatOptions? options) =>
        provider.GetChatClient("claude-sonnet-5-5").GetStreamingResponseAsync(messages, options, CancellationToken.None).CollectAsync();

    [Fact]
    public async Task TheRequest_CarriesToolsAsInputSchema_ToolUseBlocks_AndToolResultBlocksInAUserTurn()
    {
        var handler = AnthropicProviderTests.Defaults().Serve("POST v1/messages", "Anthropic", "chat-stream.sse");
        using var provider = Create(handler);

        await ChatAsync(provider, TestTools.RoundTrip(), new ChatOptions { Tools = [TestTools.GetWeather, TestTools.RunCommand], ToolMode = ChatToolMode.Auto });

        var body = handler.RequestsTo("v1/messages").Single().Body!;
        var tools = body["tools"]!.AsArray();
        Assert.Equal(["get_weather", "run_command"], tools.Select(t => t!["name"]!.GetValue<string>()));
        Assert.Equal("Gets the current weather for a city.", tools[0]!["description"]!.GetValue<string>());
        Assert.Equal("object", tools[0]!["input_schema"]!["type"]!.GetValue<string>());
        Assert.Equal("string", tools[0]!["input_schema"]!["properties"]!["city"]!["type"]!.GetValue<string>());
        Assert.Null(body["tool_choice"]);
        Assert.Equal("You are a test assistant.", body["system"]!.GetValue<string>());

        var messages = body["messages"]!.AsArray();
        Assert.Equal(3, messages.Count);
        Assert.Equal("user", messages[0]!["role"]!.GetValue<string>());
        var assistant = messages[1]!.AsObject();
        Assert.Equal("assistant", assistant["role"]!.GetValue<string>());
        var blocks = assistant["content"]!.AsArray();
        Assert.Equal("text", blocks[0]!["type"]!.GetValue<string>());
        Assert.Equal("Let me check.", blocks[0]!["text"]!.GetValue<string>());
        Assert.Equal("tool_use", blocks[1]!["type"]!.GetValue<string>());
        Assert.Equal("call_1", blocks[1]!["id"]!.GetValue<string>());
        Assert.Equal("get_weather", blocks[1]!["name"]!.GetValue<string>());
        Assert.Equal("Paris", blocks[1]!["input"]!["city"]!.GetValue<string>());
        var result = messages[2]!.AsObject();
        Assert.Equal("user", result["role"]!.GetValue<string>());
        var resultBlock = Assert.Single(result["content"]!.AsArray())!.AsObject();
        Assert.Equal("tool_result", resultBlock["type"]!.GetValue<string>());
        Assert.Equal("call_1", resultBlock["tool_use_id"]!.GetValue<string>());
        Assert.Equal(TestTools.WeatherResult, resultBlock["content"]!.GetValue<string>());
    }

    [Fact]
    public async Task ToolChoice_MapsToTheApiShapes_AndConsecutiveResultsMergeIntoOneUserTurn()
    {
        var handler = AnthropicProviderTests.Defaults();
        using var provider = Create(handler);
        var messages = TestTools.RoundTrip();
        messages.Add(new Microsoft.Extensions.AI.ChatMessage(ChatRole.Tool, [new FunctionResultContent("call_2", "second")]));

        await ChatAsync(provider, messages, new ChatOptions { Tools = [TestTools.GetWeather], ToolMode = ChatToolMode.RequireSpecific("get_weather") });
        await ChatAsync(provider, TestTools.Question(), new ChatOptions { Tools = [TestTools.GetWeather], ToolMode = ChatToolMode.RequireAny });
        await ChatAsync(provider, TestTools.Question(), new ChatOptions { Tools = [TestTools.GetWeather], ToolMode = ChatToolMode.None });

        var requests = handler.RequestsTo("v1/messages").ToList();
        Assert.Equal("tool", requests[0].Body!["tool_choice"]!["type"]!.GetValue<string>());
        Assert.Equal("get_weather", requests[0].Body!["tool_choice"]!["name"]!.GetValue<string>());
        Assert.Equal("any", requests[1].Body!["tool_choice"]!["type"]!.GetValue<string>());
        Assert.Equal("none", requests[2].Body!["tool_choice"]!["type"]!.GetValue<string>());

        var turns = requests[0].Body!["messages"]!.AsArray();
        Assert.Equal(3, turns.Count);
        Assert.Equal(["call_1", "call_2"], turns[2]!["content"]!.AsArray().Select(b => b!["tool_use_id"]!.GetValue<string>()));
    }

    [Fact]
    public async Task WithoutTools_TheBodyHasNoToolsProperty()
    {
        var handler = AnthropicProviderTests.Defaults();
        using var provider = Create(handler);

        await ChatAsync(provider, TestTools.Question(), null);

        var body = handler.RequestsTo("v1/messages").Single().Body!;
        Assert.Null(body["tools"]);
        Assert.Null(body["tool_choice"]);
    }

    [Fact]
    public async Task AStreamedToolUse_IsAssembledFromInputJsonDeltas_AfterTheText()
    {
        var handler = AnthropicProviderTests.Defaults().Serve("POST v1/messages", "Anthropic", "tool-call.sse");
        using var provider = Create(handler);

        var updates = await ChatAsync(provider, TestTools.Question(), WithTools());

        Assert.Equal("Let me check the weather.", string.Concat(updates.Select(u => u.Text)));
        var call = Assert.Single(updates.SelectMany(u => u.Contents).OfType<FunctionCallContent>());
        Assert.Equal("toolu_01A09q90qw90lq917835lq9", call.CallId);
        Assert.Equal("get_weather", call.Name);
        Assert.Equal("Paris", TestTools.Argument(call, "city"));
        Assert.Equal("celsius", TestTools.Argument(call, "unit"));
        Assert.Equal(ChatFinishReason.ToolCalls, updates[^1].FinishReason);
        var usage = Assert.Single(updates.SelectMany(u => u.Contents).OfType<UsageContent>());
        Assert.Equal(412, usage.Details.InputTokenCount);
        Assert.Equal(61, usage.Details.OutputTokenCount);

        // The text came before the call, as in the stream.
        var order = updates.SelectMany(u => u.Contents).Where(c => c is TextContent or FunctionCallContent).Select(c => c.GetType().Name).ToList();
        Assert.Equal(["TextContent", "FunctionCallContent"], order);
    }

    [Fact]
    public async Task ParallelToolUseBlocks_EachBecomeACall()
    {
        var handler = AnthropicProviderTests.Defaults().Serve("POST v1/messages", "Anthropic", "tool-calls-parallel.sse");
        using var provider = Create(handler);

        var updates = await ChatAsync(provider, TestTools.Question(), WithTools());

        var calls = updates.SelectMany(u => u.Contents).OfType<FunctionCallContent>().ToList();
        Assert.Equal(["toolu_01First", "toolu_01Second"], calls.Select(c => c.CallId));
        Assert.Equal(["Paris", "London"], calls.Select(c => TestTools.Argument(c, "city")));
        Assert.Equal(ChatFinishReason.ToolCalls, updates[^1].FinishReason);
    }

    [Fact]
    public async Task MalformedToolInput_IsARetryableProviderError()
    {
        var handler = AnthropicProviderTests.Defaults().Serve("POST v1/messages", "Anthropic", "tool-call-malformed.sse");
        using var provider = Create(handler);

        var ex = await Assert.ThrowsAsync<ProviderException>(() => ChatAsync(provider, TestTools.Question(), WithTools()));

        Assert.True(ex.IsRetryable);
        Assert.False(ex.IsProviderFault);
        Assert.Contains("get_weather", ex.Message, StringComparison.Ordinal);
    }
}
