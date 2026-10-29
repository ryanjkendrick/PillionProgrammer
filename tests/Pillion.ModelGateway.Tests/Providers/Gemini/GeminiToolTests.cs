using Microsoft.Extensions.AI;
using Pillion.Common.Secrets;
using Pillion.ModelGateway.Providers.Gemini;
using Pillion.ModelGateway.Tests.Providers.Cloud;
using Pillion.ModelGateway.Tests.TestSupport;

namespace Pillion.ModelGateway.Tests.Providers.Gemini;

/// <summary>The Gemini tool transport against fixtures in the documented <c>generateContent</c> shape (work item 3.23).</summary>
public sealed class GeminiToolTests
{
    private const string ChatPath = "v1beta/models/gemini-3.8-flash:streamGenerateContent";

    private static GeminiProvider Create(FakeCloudHandler handler)
    {
        var secrets = new InMemorySecretStore();
        secrets.Set("gem", GeminiProviderTests.Key);
        return GeminiProviderTests.Create(handler, secrets);
    }

    private static Task<List<ChatResponseUpdate>> ChatAsync(GeminiProvider provider, IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages, ChatOptions? options) =>
        provider.GetChatClient("gemini-3.8-flash").GetStreamingResponseAsync(messages, options, CancellationToken.None).CollectAsync();

    [Fact]
    public async Task TheRequest_CarriesFunctionDeclarations_FunctionCallParts_AndFunctionResponsePartsNamedAfterTheCall()
    {
        var handler = GeminiProviderTests.Defaults();
        using var provider = Create(handler);

        await ChatAsync(provider, TestTools.RoundTrip(), new ChatOptions { Tools = [TestTools.GetWeather, TestTools.RunCommand] });

        var body = handler.RequestsTo(ChatPath).Single().Body!;
        var declarations = Assert.Single(body["tools"]!.AsArray())!["functionDeclarations"]!.AsArray();
        Assert.Equal(["get_weather", "run_command"], declarations.Select(d => d!["name"]!.GetValue<string>()));
        Assert.Equal("object", declarations[0]!["parameters"]!["type"]!.GetValue<string>());

        // The OpenAPI subset: no $schema, no additionalProperties.
        Assert.Null(declarations[1]!["parameters"]!["$schema"]);
        Assert.Null(declarations[1]!["parameters"]!["additionalProperties"]);
        Assert.Equal("string", declarations[1]!["parameters"]!["properties"]!["command"]!["type"]!.GetValue<string>());
        Assert.Null(body["toolConfig"]);

        var contents = body["contents"]!.AsArray();
        Assert.Equal(["user", "model", "user"], contents.Select(c => c!["role"]!.GetValue<string>()));
        var model = contents[1]!["parts"]!.AsArray();
        Assert.Equal("Let me check.", model[0]!["text"]!.GetValue<string>());
        Assert.Equal("get_weather", model[1]!["functionCall"]!["name"]!.GetValue<string>());
        Assert.Equal("Paris", model[1]!["functionCall"]!["args"]!["city"]!.GetValue<string>());
        var response = Assert.Single(contents[2]!["parts"]!.AsArray())!["functionResponse"]!;
        Assert.Equal("get_weather", response["name"]!.GetValue<string>());
        Assert.Equal(TestTools.WeatherResult, response["response"]!["result"]!.GetValue<string>());
    }

    [Fact]
    public async Task ToolMode_MapsToFunctionCallingConfig()
    {
        var handler = GeminiProviderTests.Defaults();
        using var provider = Create(handler);

        await ChatAsync(provider, TestTools.Question(), new ChatOptions { Tools = [TestTools.GetWeather], ToolMode = ChatToolMode.RequireSpecific("get_weather") });
        await ChatAsync(provider, TestTools.Question(), new ChatOptions { Tools = [TestTools.GetWeather], ToolMode = ChatToolMode.None });

        var requests = handler.RequestsTo(ChatPath).ToList();
        var config = requests[0].Body!["toolConfig"]!["functionCallingConfig"]!;
        Assert.Equal("ANY", config["mode"]!.GetValue<string>());
        Assert.Equal(["get_weather"], config["allowedFunctionNames"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal("NONE", requests[1].Body!["toolConfig"]!["functionCallingConfig"]!["mode"]!.GetValue<string>());
    }

    [Fact]
    public async Task AFunctionCallPart_BecomesACall_WithAnIdMadeForTheResponse()
    {
        var handler = GeminiProviderTests.Defaults().Serve(GeminiProviderTests.StreamRoute, "Gemini", "tool-call.sse");
        using var provider = Create(handler);

        var updates = await ChatAsync(provider, TestTools.Question(), new ChatOptions { Tools = [TestTools.GetWeather] });

        Assert.Equal("I'll look that up.", string.Concat(updates.Select(u => u.Text)));
        var call = Assert.Single(updates.SelectMany(u => u.Contents).OfType<FunctionCallContent>());
        Assert.Equal("get_weather", call.Name);
        Assert.StartsWith("call_", call.CallId, StringComparison.Ordinal);
        Assert.EndsWith("_0_get_weather", call.CallId, StringComparison.Ordinal);
        Assert.Equal("Paris", TestTools.Argument(call, "city"));
        Assert.Equal("celsius", TestTools.Argument(call, "unit"));
        Assert.Equal(ChatFinishReason.ToolCalls, updates[^1].FinishReason);
        Assert.Equal(80, Assert.Single(updates.SelectMany(u => u.Contents).OfType<UsageContent>()).Details.InputTokenCount);
    }

    [Fact]
    public async Task ParallelFunctionCalls_InOnePart_List_EachBecomeACall()
    {
        var handler = GeminiProviderTests.Defaults().Serve(GeminiProviderTests.StreamRoute, "Gemini", "tool-calls-parallel.sse");
        using var provider = Create(handler);

        var updates = await ChatAsync(provider, TestTools.Question(), new ChatOptions { Tools = [TestTools.GetWeather] });

        var calls = updates.SelectMany(u => u.Contents).OfType<FunctionCallContent>().ToList();
        Assert.Equal(["Paris", "London"], calls.Select(c => TestTools.Argument(c, "city")));
        Assert.NotEqual(calls[0].CallId, calls[1].CallId);
    }

    [Fact]
    public async Task WithoutTools_TheBodyHasNoToolsProperty()
    {
        var handler = GeminiProviderTests.Defaults();
        using var provider = Create(handler);

        await ChatAsync(provider, TestTools.Question(), null);

        Assert.Null(handler.RequestsTo(ChatPath).Single().Body!["tools"]);
    }
}
