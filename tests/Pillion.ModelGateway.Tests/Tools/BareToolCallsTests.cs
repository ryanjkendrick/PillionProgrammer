using Pillion.ModelGateway.Providers.Tools;
using Pillion.ModelGateway.Tests.TestSupport;

namespace Pillion.ModelGateway.Tests.Tools;

/// <summary>A tool call written as plain text by a local model (Qwen2.5-Coder without its <c>&lt;tool_call&gt;</c> tags) is caught; anything else streams through.</summary>
public sealed class BareToolCallsTests
{
    private static BareToolCalls Detector(params string[] offered) => new(offered.Length == 0 ? ["get_weather"] : offered, "resp", "Ollama");

    [Fact]
    public void ABareCallObject_StreamedInPieces_BecomesACall_AndNoTextIsEmitted()
    {
        var detector = Detector();

        Assert.Equal(string.Empty, detector.Push("{\"name\": \"get_weather\", "));
        Assert.Equal(string.Empty, detector.Push("\"arguments\": {\"city\": \"Par"));
        Assert.Equal(string.Empty, detector.Push("is\", \"unit\": \"celsius\"}}"));
        var (calls, text) = detector.Finish();

        Assert.Equal(string.Empty, text);
        var call = Assert.Single(calls);
        Assert.Equal("get_weather", call.Name);
        Assert.Equal("call_resp_0_get_weather", call.CallId);
        Assert.Equal("Paris", TestTools.Argument(call, "city"));
        Assert.Equal("celsius", TestTools.Argument(call, "unit"));
    }

    [Fact]
    public void TaggedCalls_OneOrMore_AreAllFound()
    {
        var detector = Detector();

        detector.Push("<tool_call>\n{\"name\": \"get_weather\", \"arguments\": {\"city\": \"Paris\"}}\n</tool_call>\n");
        detector.Push("<tool_call>\n{\"name\": \"get_weather\", \"arguments\": {\"city\": \"London\"}}\n</tool_call>");
        var (calls, text) = detector.Finish();

        Assert.Equal(string.Empty, text);
        Assert.Equal(["Paris", "London"], calls.Select(c => TestTools.Argument(c, "city")));
        Assert.NotEqual(calls[0].CallId, calls[1].CallId);
    }

    [Fact]
    public void AFencedCall_IsFound_AndAFencedNonCallIsReleasedAsText()
    {
        // qwen2.5-coder:7b through Ollama answered this way on the second run (2026-10-29).
        var detector = Detector();
        Assert.Equal(string.Empty, detector.Push("```json\n{\"name\": \"get_weather\", "));
        Assert.Equal(string.Empty, detector.Push("\"arguments\": {\"city\": \"Paris\"}}\n```"));
        var (calls, text) = detector.Finish();
        Assert.Equal(string.Empty, text);
        Assert.Equal("Paris", TestTools.Argument(Assert.Single(calls), "city"));

        var other = Detector();
        Assert.Equal(string.Empty, other.Push("```csharp\nvar x = 1;\n```"));
        var (none, code) = other.Finish();
        Assert.Empty(none);
        Assert.Equal("```csharp\nvar x = 1;\n```", code);
    }

    [Fact]
    public void OrdinaryText_StreamsThroughAtOnce_AndNothingIsHeldAfterwards()
    {
        var detector = Detector();

        Assert.Equal("The weather", detector.Push("The weather"));
        Assert.Equal(" is {\"not\": \"a call\"}", detector.Push(" is {\"not\": \"a call\"}"));
        var (calls, text) = detector.Finish();

        Assert.Empty(calls);
        Assert.Equal(string.Empty, text);
    }

    [Fact]
    public void LeadingWhitespace_IsHeldWithTheObject_AndReleasedWithIt()
    {
        var detector = Detector();

        Assert.Equal(string.Empty, detector.Push("  \n"));
        Assert.Equal("  \nHello", detector.Push("Hello"));
    }

    [Theory]
    [InlineData("{\"temperature_c\": 18}")]
    [InlineData("{\"name\": \"unknown_tool\", \"arguments\": {}}")]
    [InlineData("{\"name\": \"get_weather\", \"arguments\": \"not json\"}")]
    [InlineData("{\"name\": \"get_weather\"} trailing words")]
    [InlineData("{\"name\": \"get_weather\", \"arguments\": {\"city\": \"Paris\"")]
    public void JsonThatIsNotACallToAnOfferedTool_IsReleasedAsText(string text)
    {
        var detector = Detector();

        Assert.Equal(string.Empty, detector.Push(text));
        var (calls, released) = detector.Finish();

        Assert.Empty(calls);
        Assert.Equal(text, released);
    }

    [Fact]
    public void ACallWithoutArguments_HasAnEmptyArgumentObject()
    {
        var detector = Detector("list_dir");

        detector.Push("{\"name\": \"list_dir\"}");
        var (calls, _) = detector.Finish();

        var call = Assert.Single(calls);
        Assert.Equal("list_dir", call.Name);
        Assert.Empty(call.Arguments!);
    }

    [Fact]
    public void WithNoToolsOffered_EverythingStreamsThrough()
    {
        var detector = new BareToolCalls([], "resp", "Ollama");

        Assert.Equal("{\"name\": \"get_weather\"}", detector.Push("{\"name\": \"get_weather\"}"));
        Assert.Empty(detector.Finish().Calls);
    }
}
