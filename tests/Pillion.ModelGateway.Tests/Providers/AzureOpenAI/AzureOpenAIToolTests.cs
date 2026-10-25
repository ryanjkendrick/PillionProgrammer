using Microsoft.Extensions.AI;
using Pillion.Common.Secrets;
using Pillion.ModelGateway.Providers.AzureOpenAI;
using Pillion.ModelGateway.Tests.Providers.Cloud;
using Pillion.ModelGateway.Tests.TestSupport;

namespace Pillion.ModelGateway.Tests.Providers.AzureOpenAI;

/// <summary>The Azure OpenAI tool transport: the OpenAI shape under the deployment URL (work item 3.23).</summary>
public sealed class AzureOpenAIToolTests
{
    private const string ChatPath = "openai/deployments/prod-chat/chat/completions";

    private static AzureOpenAIProvider Create(FakeCloudHandler handler)
    {
        var secrets = new InMemorySecretStore();
        secrets.Set("azure", AzureOpenAIProviderTests.Key);
        return AzureOpenAIProviderTests.Create(handler, secrets);
    }

    [Fact]
    public async Task TheRequest_CarriesToolsAndToolMessages_WithoutAModel()
    {
        var handler = AzureOpenAIProviderTests.Defaults();
        using var provider = Create(handler);

        await provider.GetChatClient("prod-chat").GetStreamingResponseAsync(TestTools.RoundTrip(), new ChatOptions { Tools = [TestTools.GetWeather] }, CancellationToken.None).CollectAsync();

        var body = handler.RequestsTo(ChatPath).Single().Body!;
        Assert.Null(body["model"]);
        Assert.Equal("get_weather", body["tools"]![0]!["function"]!["name"]!.GetValue<string>());
        var messages = body["messages"]!.AsArray();
        Assert.Equal(["system", "user", "assistant", "tool"], messages.Select(m => m!["role"]!.GetValue<string>()));
        Assert.Equal("call_1", messages[3]!["tool_call_id"]!.GetValue<string>());
        Assert.Equal("call_1", messages[2]!["tool_calls"]![0]!["id"]!.GetValue<string>());
    }

    [Fact]
    public async Task AStreamedToolCall_IsAssembled_PastTheContentFilterChunk()
    {
        var handler = AzureOpenAIProviderTests.Defaults().Serve(AzureOpenAIProviderTests.ChatRoute, "AzureOpenAI", "tool-call-stream.sse");
        using var provider = Create(handler);

        var updates = await provider.GetChatClient("prod-chat").GetStreamingResponseAsync(TestTools.Question(), new ChatOptions { Tools = [TestTools.GetWeather] }, CancellationToken.None).CollectAsync();

        var call = Assert.Single(updates.SelectMany(u => u.Contents).OfType<FunctionCallContent>());
        Assert.Equal("call_az1", call.CallId);
        Assert.Equal("Paris", TestTools.Argument(call, "city"));
        Assert.Equal(ChatFinishReason.ToolCalls, updates.Single(u => u.FinishReason is not null).FinishReason);
        Assert.Equal(99, Assert.Single(updates.SelectMany(u => u.Contents).OfType<UsageContent>()).Details.InputTokenCount);
        Assert.All(updates, u => Assert.Equal("prod-chat", u.ModelId));
    }
}
