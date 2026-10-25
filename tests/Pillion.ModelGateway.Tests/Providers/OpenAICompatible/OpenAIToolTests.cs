using System.Net;
using Microsoft.Extensions.AI;
using Pillion.Common.Secrets;
using Pillion.Common.Settings;
using Pillion.ModelGateway.Providers.OpenAICompatible;
using Pillion.ModelGateway.Providers.Tools;
using Pillion.ModelGateway.Tests.TestSupport;

namespace Pillion.ModelGateway.Tests.Providers.OpenAICompatible;

/// <summary>The OpenAI-compatible tool transport against fixtures in the documented Chat Completions shape (work item 3.23).</summary>
public sealed class OpenAIToolTests
{
    private const string ChatPath = "v1/chat/completions";

    private static OpenAICompatibleProvider Create(FakeOpenAIHandler handler, ProviderKind kind = ProviderKind.OpenAiCompatible)
    {
        var secrets = new InMemorySecretStore();
        secrets.Set("gw", OpenAICompatibleProviderTests.Key);
        return OpenAICompatibleProviderTests.Create(handler, secrets, kind: kind);
    }

    private static FakeOpenAIHandler Serving(string fixture)
    {
        var handler = FakeOpenAIHandler.WithDefaults();
        handler.Text("POST " + ChatPath, FakeOpenAIHandler.Fixture(fixture), "text/event-stream");
        return handler;
    }

    private static ChatOptions WithTools() => new() { Tools = [TestTools.GetWeather] };

    private static Task<List<ChatResponseUpdate>> ChatAsync(OpenAICompatibleProvider provider, IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages, ChatOptions? options) =>
        provider.GetChatClient("gpt-6.1-sol").GetStreamingResponseAsync(messages, options, CancellationToken.None).CollectAsync();

    [Fact]
    public async Task TheRequest_CarriesToolsAsFunctions_AssistantToolCalls_AndToolMessagesWithTheCallId()
    {
        var handler = FakeOpenAIHandler.WithDefaults();
        using var provider = Create(handler);

        await ChatAsync(provider, TestTools.RoundTrip(), new ChatOptions { Tools = [TestTools.GetWeather, TestTools.RunCommand] });

        var body = handler.RequestsTo(ChatPath).Single().Body!;
        var tools = body["tools"]!.AsArray();
        Assert.Equal(2, tools.Count);
        Assert.Equal("function", tools[0]!["type"]!.GetValue<string>());
        Assert.Equal("get_weather", tools[0]!["function"]!["name"]!.GetValue<string>());
        Assert.Equal("Gets the current weather for a city.", tools[0]!["function"]!["description"]!.GetValue<string>());
        Assert.Equal("object", tools[0]!["function"]!["parameters"]!["type"]!.GetValue<string>());
        Assert.Equal(["city"], tools[0]!["function"]!["parameters"]!["required"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Null(body["tool_choice"]);

        var messages = body["messages"]!.AsArray();
        Assert.Equal(["system", "user", "assistant", "tool"], messages.Select(m => m!["role"]!.GetValue<string>()));
        var assistant = messages[2]!.AsObject();
        Assert.Equal("Let me check.", assistant["content"]!.GetValue<string>());
        var call = Assert.Single(assistant["tool_calls"]!.AsArray())!.AsObject();
        Assert.Equal("call_1", call["id"]!.GetValue<string>());
        Assert.Equal("function", call["type"]!.GetValue<string>());
        Assert.Equal("get_weather", call["function"]!["name"]!.GetValue<string>());
        Assert.Equal("{\"city\":\"Paris\"}", call["function"]!["arguments"]!.GetValue<string>());
        var tool = messages[3]!.AsObject();
        Assert.Equal("call_1", tool["tool_call_id"]!.GetValue<string>());
        Assert.Equal(TestTools.WeatherResult, tool["content"]!.GetValue<string>());
    }

    [Fact]
    public async Task AnAssistantTurnWithOnlyCalls_HasNullContent_AndToolChoiceMapsToTheApiValues()
    {
        var handler = FakeOpenAIHandler.WithDefaults();
        using var provider = Create(handler);
        var messages = TestTools.Question();
        messages.Add(new Microsoft.Extensions.AI.ChatMessage(ChatRole.Assistant, [new FunctionCallContent("call_a", "get_weather", TestTools.Arguments("""{"city":"Oslo"}"""))]));
        messages.Add(new Microsoft.Extensions.AI.ChatMessage(ChatRole.Tool, [new FunctionResultContent("call_a", "cold"), new FunctionResultContent("call_b", "colder")]));

        await ChatAsync(provider, messages, new ChatOptions { Tools = [TestTools.GetWeather], ToolMode = ChatToolMode.RequireAny });
        await ChatAsync(provider, TestTools.Question(), new ChatOptions { Tools = [TestTools.GetWeather], ToolMode = ChatToolMode.RequireSpecific("get_weather") });
        await ChatAsync(provider, TestTools.Question(), new ChatOptions { Tools = [TestTools.GetWeather], ToolMode = ChatToolMode.None });

        var requests = handler.RequestsTo(ChatPath).ToList();
        var wire = requests[0].Body!["messages"]!.AsArray();
        Assert.Equal(["system", "user", "assistant", "tool", "tool"], wire.Select(m => m!["role"]!.GetValue<string>()));
        Assert.Null(wire[2]!["content"]);
        Assert.Equal(["call_a", "call_b"], wire.Skip(3).Select(m => m!["tool_call_id"]!.GetValue<string>()));
        Assert.Equal("required", requests[0].Body!["tool_choice"]!.GetValue<string>());
        Assert.Equal("get_weather", requests[1].Body!["tool_choice"]!["function"]!["name"]!.GetValue<string>());
        Assert.Equal("none", requests[2].Body!["tool_choice"]!.GetValue<string>());
    }

    [Fact]
    public async Task WithoutTools_TheBodyHasNoToolsProperty_AndTextStreamsAsBefore()
    {
        var handler = FakeOpenAIHandler.WithDefaults();
        using var provider = Create(handler);

        var updates = await ChatAsync(provider, TestTools.Question(), null);

        Assert.Equal("Hello from Pillion", string.Concat(updates.Select(u => u.Text)));
        Assert.Null(handler.RequestsTo(ChatPath).Single().Body!["tools"]);
    }

    [Fact]
    public async Task StreamedToolCallDeltas_SplitMidToken_AreAssembledIntoOneCall_AfterTheText()
    {
        using var provider = Create(Serving("tool-call-stream.sse"));

        var updates = await ChatAsync(provider, TestTools.Question(), WithTools());

        Assert.Equal("Checking the weather.", string.Concat(updates.Select(u => u.Text)));
        var call = Assert.Single(updates.SelectMany(u => u.Contents).OfType<FunctionCallContent>());
        Assert.Equal("call_abc123", call.CallId);
        Assert.Equal("get_weather", call.Name);
        Assert.Equal("Paris", TestTools.Argument(call, "city"));
        Assert.Equal("celsius", TestTools.Argument(call, "unit"));
        var final = updates.Single(u => u.FinishReason is not null);
        Assert.Equal(ChatFinishReason.ToolCalls, final.FinishReason);
        Assert.Contains(final.Contents, c => c is FunctionCallContent);
        var usage = Assert.Single(updates.SelectMany(u => u.Contents).OfType<UsageContent>());
        Assert.Equal(120, usage.Details.InputTokenCount);
        Assert.Equal(18, usage.Details.OutputTokenCount);
    }

    [Fact]
    public async Task ParallelToolCalls_InterleavedByIndex_EachBecomeACall()
    {
        using var provider = Create(Serving("tool-calls-parallel.sse"));

        var updates = await ChatAsync(provider, TestTools.Question(), WithTools());

        var calls = updates.SelectMany(u => u.Contents).OfType<FunctionCallContent>().ToList();
        Assert.Equal(["call_first", "call_second"], calls.Select(c => c.CallId));
        Assert.Equal(["Paris", "London"], calls.Select(c => TestTools.Argument(c, "city")));
        Assert.Empty(string.Concat(updates.Select(u => u.Text)));
    }

    [Fact]
    public async Task MalformedArguments_AreARetryableProviderError()
    {
        using var provider = Create(Serving("tool-call-malformed.sse"));

        var ex = await Assert.ThrowsAsync<ProviderException>(() => ChatAsync(provider, TestTools.Question(), WithTools()));

        Assert.True(ex.IsRetryable);
        Assert.False(ex.IsProviderFault);
    }

    [Fact]
    public async Task AServerThatRejectsTools_IsToolsNotSupported_OnlyWhenToolsWereSent()
    {
        var handler = FakeOpenAIHandler.WithDefaults();
        handler.Error("POST " + ChatPath, HttpStatusCode.BadRequest, FakeOpenAIHandler.Fixture("error-400-tools.json"));
        using var provider = Create(handler);

        var refused = await Assert.ThrowsAsync<ToolsNotSupportedException>(() => ChatAsync(provider, TestTools.Question(), WithTools()));
        Assert.False(refused.IsProviderFault);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("gpt-6.1-sol", refused.Message, StringComparison.Ordinal);

        var plain = await Assert.ThrowsAsync<ProviderException>(() => ChatAsync(provider, TestTools.Question(), null));
        Assert.IsNotType<ToolsNotSupportedException>(plain);
    }

    [Fact]
    public async Task TheOfficialApi_SendsTheSameToolShape()
    {
        var handler = FakeOpenAIHandler.WithDefaults();
        using var provider = Create(handler, ProviderKind.OpenAi);

        await provider.GetChatClient("gpt-5.4").GetStreamingResponseAsync(TestTools.Question(), WithTools(), CancellationToken.None).CollectAsync();

        var body = handler.RequestsTo(ChatPath).Single().Body!;
        Assert.Equal("get_weather", body["tools"]![0]!["function"]!["name"]!.GetValue<string>());
    }
}
