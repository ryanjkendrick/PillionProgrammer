using Microsoft.Extensions.AI;
using Pillion.Common.Secrets;
using Pillion.Common.Settings;
using Pillion.Contracts;
using Pillion.ModelGateway.Providers.Ollama;
using Pillion.ModelGateway.Tests.TestSupport;
using AiChatMessage = Microsoft.Extensions.AI.ChatMessage;
using AiChatRole = Microsoft.Extensions.AI.ChatRole;

namespace Pillion.ModelGateway.Tests.Ollama;

/// <summary>A fact that needs the local Ollama and the tool model of <see cref="OllamaToolModelTests"/> installed.</summary>
public sealed class OllamaToolModelFactAttribute : FactAttribute
{
    public OllamaToolModelFactAttribute()
    {
        if (!LiveOllama.IsReachable)
        {
            Skip = $"Ollama is not answering at {LiveOllama.Endpoint}api/version.";
        }
        else if (!LiveOllama.Has(OllamaToolModelTests.Model))
        {
            Skip = $"{OllamaToolModelTests.Model} is not installed.";
        }
    }
}

/// <summary>
/// The tool transport against a real, tool-capable local model through the whole gateway (work item 3.23). Runs in the
/// gate, not in CI: take the shared Ollama lock before running it on the development machine.
/// </summary>
[Trait("Category", "Model")]
public sealed class OllamaToolModelTests
{
    /// <summary>The environment variable that names the model to drive; <c>qwen2.5-coder:7b</c> when unset.</summary>
    public const string ModelVariable = "PILLION_TEST_OLLAMA_TOOL_MODEL";

    /// <summary>
    /// The model: by default the 7B coder, the smallest installed model that both lists the <c>tools</c> capability and answers
    /// from a tool result (the 1.5B calls the tool but then repeats the call instead of answering). On the 8 GB development
    /// GPU each turn with the 7B takes about four minutes, because the residency manager swaps it in beside the pinned 1.5B
    /// autocomplete model and restores the pin afterwards; <see cref="ModelVariable"/> names another model.
    /// </summary>
    public static string Model { get; } = Environment.GetEnvironmentVariable(ModelVariable) is { Length: > 0 } configured ? configured : "qwen2.5-coder:7b";

    private static readonly TimeSpan Budget = TimeSpan.FromMinutes(10);

    [OllamaToolModelFact]
    public async Task GetWeather_IsCalledNatively_AndTheResultIsUsedInTheAnswer()
    {
        var settings = new PillionSettings();
        settings.Providers.Add(new ProviderSettings { Id = "ollama", Kind = ProviderKind.Ollama, Endpoint = LiveOllama.Endpoint });
        var options = new ModelGatewayOptions();
        using var registry = new ProviderRegistry(new StaticGatewaySettingsSource(settings), new InMemorySecretStore(), [new OllamaProviderFactory()], ModelCatalog.Default);
        var inventory = new ModelInventory();
        var breakers = new CircuitBreakerRegistry(options);
        var log = new InMemoryOutboundLog(options);
        var router = new ModelRouter(registry, ModelCatalog.Default, inventory, new AlwaysReachable(), breakers, new NoCostCaps());
        var gateway = new ModelGateway(registry, router, inventory, ModelCatalog.Default, PromptTemplates.Default, new AutocompleteCache(options), breakers, new ConcurrencyLimiter(options), log, new AlwaysReachable(), new NoCostCaps(), new NoProgressSink(), options);
        using var cts = new CancellationTokenSource(Budget);
        await gateway.ListModelsAsync(cts.Token);

        var messages = new List<AiChatMessage>
        {
            new(AiChatRole.System, "You are a weather assistant. Use the get_weather tool to look up the weather before answering; never guess."),
            new(AiChatRole.User, "What is the weather in Paris right now?"),
        };
        ToolChatRequest Request(string id) => new()
        {
            RequestId = id,
            Messages = messages,
            Tools = [TestTools.GetWeather],
            TaskClass = TaskClass.Agent,
            ModelId = "ollama/" + Model,
            MaxOutputTokens = 256,
            Temperature = 0,
        };

        var route = gateway.ResolveToolRoute(Request("route"));
        Assert.Equal(ToolCallingSupport.Native, route.ToolCalling);
        Assert.Equal("ollama/" + Model, route.QualifiedId);

        // Turn 1: the model asks for the weather.
        var first = await gateway.ChatWithToolsAsync(Request("m1"), cts.Token).CollectAsync(cts.Token);
        var firstTurn = $"transport {first[^1].Transport}, finish {first[^1].FinishReason}, error {first[^1].Error?.Message ?? "none"}, text \"{string.Concat(first.Select(u => u.Text))}\", calls {string.Join(",", first.Where(u => u.ToolCall is not null).Select(u => u.ToolCall!.Name))}";
        Assert.All(first, u => Assert.Equal(ToolTransport.Native, u.Transport));
        Assert.True(first[^1].IsFinal, firstTurn);
        Assert.True(first[^1].Error is null, firstTurn);
        Assert.True(first.Count(u => u.ToolCall is not null) == 1, firstTurn);
        var call = first.Single(u => u.ToolCall is not null).ToolCall!;
        Assert.Equal("get_weather", call.Name);
        Assert.Contains("paris", TestTools.Argument(call, "city")?.ToLowerInvariant() ?? string.Empty, StringComparison.Ordinal);
        Assert.Equal(ChatFinishReason.ToolCalls, first[^1].FinishReason);

        // Turn 2: the runtime answers the call and the model answers the user from it.
        messages.Add(new AiChatMessage(AiChatRole.Assistant, [.. first.Where(u => u.Text is not null).Select(u => (AIContent)new TextContent(u.Text!)), call]));
        messages.Add(new AiChatMessage(AiChatRole.Tool, [new FunctionResultContent(call.CallId, "{\"temperature_c\":18,\"conditions\":\"light rain\"}")]));
        var second = await gateway.ChatWithToolsAsync(Request("m2"), cts.Token).CollectAsync(cts.Token);
        var answer = string.Concat(second.Where(u => u.Text is not null).Select(u => u.Text));
        var secondTurn = $"transport {second[^1].Transport}, finish {second[^1].FinishReason}, error {second[^1].Error?.Message ?? "none"}, text \"{answer}\", calls {string.Join(",", second.Where(u => u.ToolCall is not null).Select(u => u.ToolCall!.Name))}";
        Assert.True(second[^1].Error is null, secondTurn);
        Assert.True(answer.Contains("18", StringComparison.Ordinal) || answer.Contains("rain", StringComparison.OrdinalIgnoreCase), secondTurn);

        var records = log.GetRecent();
        Assert.Equal(2, records.Count);
        Assert.Equal(["get_weather"], records[0].ToolsOffered);
        Assert.Equal(["get_weather"], records[0].ToolCalls);
        Assert.True(records[0].ToolArgumentBytes > 0);
        Assert.Equal(System.Text.Encoding.UTF8.GetByteCount("{\"temperature_c\":18,\"conditions\":\"light rain\"}"), records[1].ToolResultBytes);
        Assert.All(records, r => Assert.True(r.IsLocal));
    }
}
