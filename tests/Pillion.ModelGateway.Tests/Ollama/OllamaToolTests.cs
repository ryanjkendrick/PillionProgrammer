using System.Net;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Pillion.Common.Settings;
using Pillion.Contracts;
using Pillion.ModelGateway.Providers.Ollama;
using Pillion.ModelGateway.Providers.Tools;
using Pillion.ModelGateway.Tests.TestSupport;

namespace Pillion.ModelGateway.Tests.Ollama;

/// <summary>The Ollama tool transport against fixtures in the documented <c>/api/chat</c> shape (work item 3.23).</summary>
public sealed class OllamaToolTests
{
    private static OllamaProvider Create(FakeOllamaHandler handler) =>
        (OllamaProvider)new OllamaProviderFactory(() => handler).Create(
            new ProviderSettings { Id = "ollama", Kind = ProviderKind.Ollama },
            new ProviderCreationContext(null, ModelCatalog.Default, NullLoggerFactory.Instance, null));

    private static ChatOptions Options(params AITool[] tools) => new()
    {
        Tools = tools.Length == 0 ? null : [.. tools],
        AdditionalProperties = new AdditionalPropertiesDictionary { [GatewayOptionKeys.Profile] = Profile.LocalStrong },
    };

    private static Task<List<ChatResponseUpdate>> ChatAsync(OllamaProvider provider, IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages, ChatOptions options, string model = "qwen2.5-coder:1.5b") =>
        provider.GetChatClient(model).GetStreamingResponseAsync(messages, options, CancellationToken.None).CollectAsync();

    [Fact]
    public async Task TheRequest_CarriesToolsAsFunctions_AssistantToolCallsWithObjectArguments_AndToolMessagesWithTheToolName()
    {
        var handler = FakeOllamaHandler.WithDefaults();
        using var provider = Create(handler);

        await ChatAsync(provider, TestTools.RoundTrip(), Options(TestTools.GetWeather));

        var body = handler.BodiesFor("api/chat").Single();
        var tool = Assert.Single(body["tools"]!.AsArray())!;
        Assert.Equal("function", tool["type"]!.GetValue<string>());
        Assert.Equal("get_weather", tool["function"]!["name"]!.GetValue<string>());
        Assert.Equal("object", tool["function"]!["parameters"]!["type"]!.GetValue<string>());

        var messages = body["messages"]!.AsArray();
        Assert.Equal(["system", "user", "assistant", "tool"], messages.Select(m => m!["role"]!.GetValue<string>()));
        var call = Assert.Single(messages[2]!["tool_calls"]!.AsArray())!;
        Assert.Equal("get_weather", call["function"]!["name"]!.GetValue<string>());
        Assert.Equal("Paris", call["function"]!["arguments"]!["city"]!.GetValue<string>());
        Assert.Equal(TestTools.WeatherResult, messages[3]!["content"]!.GetValue<string>());
        Assert.Equal("get_weather", messages[3]!["tool_name"]!.GetValue<string>());
        Assert.Equal(8192, body["options"]!["num_ctx"]!.GetValue<int>());
    }

    [Fact]
    public async Task WithoutTools_TheBodyHasNoToolsProperty()
    {
        var handler = FakeOllamaHandler.WithDefaults();
        using var provider = Create(handler);

        var updates = await ChatAsync(provider, TestTools.Question(), Options(), "gemma4:e2b");

        Assert.Equal("hello there", string.Concat(updates.Select(u => u.Text)));
        Assert.Null(handler.BodiesFor("api/chat").Single()["tools"]);
    }

    [Fact]
    public async Task AToolCallMessage_BecomesACall_WithTheFinishReasonToolCalls()
    {
        var handler = FakeOllamaHandler.WithDefaults();
        handler.Json("POST api/chat", FakeOllamaHandler.Fixture("chat-tool-call.ndjson"), "application/x-ndjson");
        using var provider = Create(handler);

        var updates = await ChatAsync(provider, TestTools.Question(), Options(TestTools.GetWeather));

        var call = Assert.Single(updates.SelectMany(u => u.Contents).OfType<FunctionCallContent>());
        Assert.Equal("get_weather", call.Name);
        Assert.StartsWith("call_", call.CallId, StringComparison.Ordinal);
        Assert.Equal("Paris", TestTools.Argument(call, "city"));
        Assert.Equal("celsius", TestTools.Argument(call, "unit"));
        Assert.Equal(ChatFinishReason.ToolCalls, updates[^1].FinishReason);
        var usage = Assert.Single(updates.SelectMany(u => u.Contents).OfType<UsageContent>());
        Assert.Equal(210, usage.Details.InputTokenCount);
        Assert.Equal(28, usage.Details.OutputTokenCount);
        Assert.All(updates, u => Assert.Equal("qwen2.5-coder:7b", u.ModelId));
    }

    [Fact]
    public async Task ParallelToolCalls_KeepAServerId_AndParseStringArguments()
    {
        var handler = FakeOllamaHandler.WithDefaults();
        handler.Json("POST api/chat", FakeOllamaHandler.Fixture("chat-tool-calls-parallel.ndjson"), "application/x-ndjson");
        using var provider = Create(handler);

        var updates = await ChatAsync(provider, TestTools.Question(), Options(TestTools.GetWeather));

        Assert.Equal("Looking both up.", string.Concat(updates.Select(u => u.Text)));
        var calls = updates.SelectMany(u => u.Contents).OfType<FunctionCallContent>().ToList();
        Assert.Equal(2, calls.Count);
        Assert.Equal("call_ollama_1", calls[0].CallId);
        Assert.Equal(["Paris", "London"], calls.Select(c => TestTools.Argument(c, "city")));
        Assert.NotEqual(calls[0].CallId, calls[1].CallId);
    }

    [Fact]
    public async Task AModelWithoutTheToolsCapability_IsToolsNotSupported()
    {
        var handler = FakeOllamaHandler.WithDefaults();
        handler.On("POST api/chat", (_, _) => FakeOllamaHandler.Respond(HttpStatusCode.BadRequest, FakeOllamaHandler.Fixture("error-no-tools.json")));
        using var provider = Create(handler);

        var ex = await Assert.ThrowsAsync<ToolsNotSupportedException>(() => ChatAsync(provider, TestTools.Question(), Options(TestTools.GetWeather), "gemma4:e2b"));

        Assert.False(ex.IsProviderFault);
        Assert.Contains("gemma4:e2b", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListModels_ReadsTheToolsCapability_FromShow()
    {
        var handler = FakeOllamaHandler.WithDefaults();
        handler.On("POST api/show", (_, body) => body?["model"]?.GetValue<string>() switch
        {
            "gemma4:e2b" => FakeOllamaHandler.Respond(HttpStatusCode.OK, FakeOllamaHandler.Fixture("show-gemma4.json")),
            "qwen2.5-coder:1.5b" => FakeOllamaHandler.Respond(HttpStatusCode.OK, FakeOllamaHandler.Fixture("show-qwen-coder-7b.json")),
            "qwen3-embedding:0.6b" => FakeOllamaHandler.Respond(HttpStatusCode.OK, FakeOllamaHandler.Fixture("show-qwen3-embedding.json")),
            _ => FakeOllamaHandler.Respond(HttpStatusCode.NotFound, FakeOllamaHandler.Fixture("error-model-not-found.json")),
        });
        using var provider = Create(handler);

        var models = await provider.ListModelsAsync(CancellationToken.None);

        Assert.Equal(ToolCallingSupport.Native, models.Single(m => m.ModelId == "qwen2.5-coder:1.5b").Capabilities.ToolCalling);
        Assert.Equal(ToolCallingSupport.None, models.Single(m => m.ModelId == "qwen3-embedding:0.6b").Capabilities.ToolCalling);
    }
}
