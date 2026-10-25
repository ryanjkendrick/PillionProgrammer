using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using Pillion.Contracts;
using Pillion.ModelGateway.Providers.Tools;
using Pillion.ModelGateway.Tests.TestSupport;

namespace Pillion.ModelGateway.Tests.Tools;

/// <summary>The pieces the adapters share: streamed-argument assembly, argument parsing, schemas and the tools-rejection rule.</summary>
public sealed class ToolJsonTests
{
    private static JsonElement Element(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    [Fact]
    public void StreamedToolCalls_AssembleArgumentsSplitMidToken_PerIndex()
    {
        var calls = new StreamedToolCalls("Fake", "resp");

        calls.Add(Element("""[{"index":0,"id":"call_a","type":"function","function":{"name":"get_weather","arguments":""}}]"""));
        calls.Add(Element("""[{"index":1,"id":"call_b","type":"function","function":{"name":"get_weather","arguments":"{\"ci"}}]"""));
        calls.Add(Element("""[{"index":0,"function":{"arguments":"{\"city\": \"Pa"}}]"""));
        calls.Add(Element("""[{"index":1,"function":{"arguments":"ty\": \"London\"}"}}]"""));
        calls.Add(Element("""[{"index":0,"function":{"arguments":"ris\"}"}}]"""));
        var completed = calls.Complete();

        Assert.Equal(["call_a", "call_b"], completed.Select(c => c.CallId));
        Assert.Equal(["Paris", "London"], completed.Select(c => TestTools.Argument(c, "city")));
        Assert.False(calls.HasCalls);
    }

    [Fact]
    public void StreamedToolCalls_WithoutAnIndex_StartOnAnId_AndContinueTheLastCall()
    {
        var calls = new StreamedToolCalls("Fake", "resp");

        calls.Add(Element("""[{"id":"AbC123xyZ","function":{"name":"get_weather","arguments":"{\"city\": \"Pa"}}]"""));
        calls.Add(Element("""[{"function":{"arguments":"ris\"}"}}]"""));
        var completed = calls.Complete();

        var call = Assert.Single(completed);
        Assert.Equal("AbC123xyZ", call.CallId);
        Assert.Equal("Paris", TestTools.Argument(call, "city"));
        Assert.Equal("{\"city\": \"Paris\"}", call.RawRepresentation);
    }

    [Fact]
    public void StreamedToolCalls_WithoutAnId_GetOneMadeFromTheResponse()
    {
        var calls = new StreamedToolCalls("Fake", "resp1");

        calls.Add(Element("""[{"index":0,"function":{"name":"get_weather","arguments":{"city":"Paris"}}}]"""));
        var call = Assert.Single(calls.Complete());

        Assert.Equal("call_resp1_0_get_weather", call.CallId);
        Assert.Equal("Paris", TestTools.Argument(call, "city"));
    }

    [Fact]
    public void MalformedArguments_AreARetryableProviderError_NotAProviderFault()
    {
        var ex = Assert.Throws<ProviderException>(() => ToolJson.ParseArguments("{\"city\": \"Paris\"", "get_weather", "Fake"));

        Assert.True(ex.IsRetryable);
        Assert.False(ex.IsProviderFault);
        Assert.Equal(PillionErrorCode.ProviderUnavailable, ex.Code);
        Assert.Contains("get_weather", ex.Message, StringComparison.Ordinal);
        Assert.Throws<ProviderException>(() => ToolJson.ParseArguments("[1,2]", "get_weather", "Fake"));
    }

    [Fact]
    public void EmptyArguments_AreAnEmptyObject()
    {
        Assert.Empty(ToolJson.ParseArguments(string.Empty, "noop", "Fake"));
        Assert.Empty(ToolJson.ParseArguments(null, "noop", "Fake"));
        Assert.Empty(ToolJson.ParseArguments("{}", "noop", "Fake"));
    }

    [Fact]
    public void Schema_IsADetachedCopy_AndTheGeminiScrubRemovesUnsupportedKeywords()
    {
        var schema = ToolJson.Schema(TestTools.RunCommand);
        Assert.Equal("object", schema["type"]!.GetValue<string>());
        Assert.NotNull(schema["$schema"]);

        var scrubbed = (JsonObject)ToolJson.ScrubForGemini(ToolJson.Schema(TestTools.RunCommand))!;

        Assert.Null(scrubbed["$schema"]);
        Assert.Null(scrubbed["additionalProperties"]);
        Assert.Equal("string", scrubbed["properties"]!["command"]!["type"]!.GetValue<string>());
        Assert.Equal(["command"], scrubbed["required"]!.AsArray().Select(n => n!.GetValue<string>()));

        // The declaration itself is untouched.
        Assert.Contains("$schema", TestTools.RunCommand.JsonSchema.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public void Declarations_KeepOnlyFunctionDeclarations()
    {
        var options = new ChatOptions { Tools = [TestTools.GetWeather, new HostedWebSearchTool(), TestTools.RunCommand] };

        Assert.Equal(["get_weather", "run_command"], ToolJson.Declarations(options).Select(d => d.Name));
        Assert.Empty(ToolJson.Declarations(null));
        Assert.Empty(ToolJson.Declarations(new ChatOptions()));
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, "registry.ollama.ai/library/gemma3:1b does not support tools", true)]
    [InlineData(HttpStatusCode.BadRequest, "Unknown parameter: 'tools'.", true)]
    [InlineData(HttpStatusCode.UnprocessableEntity, "Function calling is not enabled for this model", true)]
    [InlineData(HttpStatusCode.BadRequest, "prompt is too long", false)]
    [InlineData(HttpStatusCode.InternalServerError, "tools failed", false)]
    [InlineData(HttpStatusCode.Unauthorized, "invalid tools key", false)]
    public void IsToolRejection_RecognisesA400Or422ThatNamesToolsOrFunctions(HttpStatusCode status, string message, bool expected)
    {
        var ex = new ProviderException(PillionErrorCode.ProviderUnavailable, message, status);

        Assert.Equal(expected, ToolJson.IsToolRejection(ex));
    }

    [Fact]
    public async Task GuardTools_TranslatesARejection_OnlyWhenToolsWereSent()
    {
        static async IAsyncEnumerable<int> Failing()
        {
            await Task.Yield();
            throw new ProviderException(PillionErrorCode.ProviderUnavailable, "model does not support tools", HttpStatusCode.BadRequest);
#pragma warning disable CS0162 // Unreachable code: the iterator needs a yield.
            yield break;
#pragma warning restore CS0162
        }

        var translated = await Assert.ThrowsAsync<ToolsNotSupportedException>(() => ToolJson.GuardTools(Failing(), hasTools: true, "m", CancellationToken.None).CollectAsync());
        Assert.False(translated.IsProviderFault);
        Assert.False(translated.IsRetryable);
        Assert.Contains("'m'", translated.Message, StringComparison.Ordinal);

        var plain = await Assert.ThrowsAsync<ProviderException>(() => ToolJson.GuardTools(Failing(), hasTools: false, "m", CancellationToken.None).CollectAsync());
        Assert.IsNotType<ToolsNotSupportedException>(plain);
    }

    [Fact]
    public void ResultText_AndArgumentBytes_MeasureWhatIsSent()
    {
        var call = new FunctionCallContent("c", "get_weather", TestTools.Arguments("""{"city":"Paris"}"""));

        Assert.Equal("{\"city\":\"Paris\"}", ToolJson.ArgumentsJson(call));
        Assert.Equal(16, ToolJson.ArgumentBytes(call));
        Assert.Equal("{}", ToolJson.ArgumentsJson(new FunctionCallContent("c", "noop", null)));
        Assert.Equal("Paris", ToolJson.ArgumentsNode(call)["city"]!.GetValue<string>());
        Assert.Equal("text", ToolJson.ResultText(new FunctionResultContent("c", "text")));
        Assert.Equal("get_weather", ToolJson.NameOfCall(TestTools.RoundTrip(), "call_1"));
        Assert.Null(ToolJson.NameOfCall(TestTools.RoundTrip(), "call_2"));
    }
}
