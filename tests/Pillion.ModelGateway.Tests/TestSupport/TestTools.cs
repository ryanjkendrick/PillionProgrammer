using Microsoft.Extensions.AI;
using AiChatMessage = Microsoft.Extensions.AI.ChatMessage;
using AiChatRole = Microsoft.Extensions.AI.ChatRole;

namespace Pillion.ModelGateway.Tests.TestSupport;

/// <summary>Tool declarations and conversations the tool-transport tests share.</summary>
internal static class TestTools
{
    public const string WeatherResult = "{\"temperature_c\":18,\"conditions\":\"light rain\"}";

    /// <summary>A <c>get_weather(city, unit)</c> declaration.</summary>
    public static AIFunctionDeclaration GetWeather { get; } = ToolDeclarations.Create(
        "get_weather",
        "Gets the current weather for a city.",
        """{"type":"object","properties":{"city":{"type":"string","description":"The city name."},"unit":{"type":"string","enum":["celsius","fahrenheit"]}},"required":["city"]}""");

    /// <summary>A declaration whose schema carries keywords Gemini rejects (<c>$schema</c>, <c>additionalProperties</c>).</summary>
    public static AIFunctionDeclaration RunCommand { get; } = ToolDeclarations.Create(
        "run_command",
        "Runs a shell command.",
        """{"$schema":"https://json-schema.org/draft/2020-12/schema","type":"object","properties":{"command":{"type":"string"},"cwd":{"type":"string"}},"required":["command"],"additionalProperties":false}""");

    /// <summary>A tool-call arguments dictionary from JSON, each value a <see cref="System.Text.Json.JsonElement"/>.</summary>
    public static Dictionary<string, object?> Arguments(string json)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        using var document = System.Text.Json.JsonDocument.Parse(json);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            result[property.Name] = property.Value.Clone();
        }

        return result;
    }

    /// <summary>The first turn: a system prompt and a question that wants the weather tool.</summary>
    public static List<AiChatMessage> Question(string text = "What is the weather in Paris?") =>
    [
        new AiChatMessage(AiChatRole.System, "You are a test assistant."),
        new AiChatMessage(AiChatRole.User, text),
    ];

    /// <summary>
    /// A round trip: the question, the assistant's text and <c>get_weather</c> call (<c>call_1</c>), and the tool result the
    /// runtime produced for it.
    /// </summary>
    public static List<AiChatMessage> RoundTrip(string result = WeatherResult, string callArgumentsJson = """{"city":"Paris"}""")
    {
        var messages = Question();
        messages.Add(new AiChatMessage(AiChatRole.Assistant,
        [
            new TextContent("Let me check."),
            new FunctionCallContent("call_1", "get_weather", Arguments(callArgumentsJson)),
        ]));
        messages.Add(new AiChatMessage(AiChatRole.Tool, [new FunctionResultContent("call_1", result)]));
        return messages;
    }

    /// <summary>The string value of a tool-call argument.</summary>
    public static string? Argument(FunctionCallContent call, string name) =>
        call.Arguments is not null && call.Arguments.TryGetValue(name, out var value) ? value switch
        {
            System.Text.Json.JsonElement element => element.GetString(),
            string text => text,
            _ => value?.ToString(),
        } : null;
}
