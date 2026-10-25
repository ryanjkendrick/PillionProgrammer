using System.Net;
using Microsoft.Extensions.AI;
using Pillion.Common.Secrets;
using Pillion.ModelGateway.Providers.Mistral;
using Pillion.ModelGateway.Providers.Tools;
using Pillion.ModelGateway.Tests.Providers.Cloud;
using Pillion.ModelGateway.Tests.TestSupport;

namespace Pillion.ModelGateway.Tests.Providers.Mistral;

/// <summary>The Mistral tool transport: the OpenAI shape, with Mistral's nine-character call ids and index-less deltas (work item 3.23).</summary>
public sealed class MistralToolTests
{
    private const string ChatPath = "v1/chat/completions";

    private static MistralProvider Create(FakeCloudHandler handler)
    {
        var secrets = new InMemorySecretStore();
        secrets.Set("mistral", MistralProviderTests.Key);
        return MistralProviderTests.Create(handler, secrets);
    }

    private static Task<List<ChatResponseUpdate>> ChatAsync(MistralProvider provider, IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages, ChatOptions? options) =>
        provider.GetChatClient("mistral-small-latest").GetStreamingResponseAsync(messages, options, CancellationToken.None).CollectAsync();

    [Fact]
    public async Task TheRequest_CarriesToolsAndToolMessages_WithoutStreamOptions()
    {
        var handler = MistralProviderTests.Defaults();
        using var provider = Create(handler);

        await ChatAsync(provider, TestTools.RoundTrip(), new ChatOptions { Tools = [TestTools.GetWeather] });

        var body = handler.RequestsTo(ChatPath).Single().Body!;
        Assert.Null(body["stream_options"]);
        Assert.Equal("get_weather", body["tools"]![0]!["function"]!["name"]!.GetValue<string>());
        var messages = body["messages"]!.AsArray();
        Assert.Equal(["system", "user", "assistant", "tool"], messages.Select(m => m!["role"]!.GetValue<string>()));
        Assert.Equal("{\"city\":\"Paris\"}", messages[2]!["tool_calls"]![0]!["function"]!["arguments"]!.GetValue<string>());
        Assert.Equal("call_1", messages[3]!["tool_call_id"]!.GetValue<string>());
        Assert.Equal(TestTools.WeatherResult, messages[3]!["content"]!.GetValue<string>());
    }

    [Fact]
    public async Task AToolCallStreamedWithoutAnIndex_IsAssembledUnderItsId()
    {
        var handler = MistralProviderTests.Defaults().Serve("POST " + ChatPath, "Mistral", "tool-call-stream.sse");
        using var provider = Create(handler);

        var updates = await ChatAsync(provider, TestTools.Question(), new ChatOptions { Tools = [TestTools.GetWeather] });

        var call = Assert.Single(updates.SelectMany(u => u.Contents).OfType<FunctionCallContent>());
        Assert.Equal("AbC123xyZ", call.CallId);
        Assert.Equal("get_weather", call.Name);
        Assert.Equal("Paris", TestTools.Argument(call, "city"));
        Assert.Equal(ChatFinishReason.ToolCalls, updates.Single(u => u.FinishReason is not null).FinishReason);
        Assert.Equal(88, Assert.Single(updates.SelectMany(u => u.Contents).OfType<UsageContent>()).Details.InputTokenCount);
    }

    [Fact]
    public async Task AModelWithoutFunctionCalling_IsToolsNotSupported()
    {
        var handler = MistralProviderTests.Defaults().FailText("POST " + ChatPath, HttpStatusCode.BadRequest, """{"object":"error","message":"Function calling is not enabled for this model","type":"invalid_request_error","param":null,"code":"3230"}""", "application/json");
        using var provider = Create(handler);

        var ex = await Assert.ThrowsAsync<ToolsNotSupportedException>(() => ChatAsync(provider, TestTools.Question(), new ChatOptions { Tools = [TestTools.GetWeather] }));

        Assert.False(ex.IsProviderFault);
    }
}
