using Microsoft.Extensions.AI;
using Pillion.Common.Secrets;
using Pillion.Common.Settings;
using Pillion.Contracts;
using Pillion.ModelGateway.Providers.Ollama;
using Pillion.ModelGateway.Tests.TestSupport;
using AiChatMessage = Microsoft.Extensions.AI.ChatMessage;
using AiChatRole = Microsoft.Extensions.AI.ChatRole;

namespace Pillion.ModelGateway.Tests.Ollama;

/// <summary>A fact that needs the local Ollama and a named installed model.</summary>
public sealed class OllamaModelFactAttribute : FactAttribute
{
    public OllamaModelFactAttribute(string model)
    {
        if (!LiveOllama.IsReachable)
        {
            Skip = $"Ollama is not answering at {LiveOllama.Endpoint}api/version.";
        }
        else if (!LiveOllama.Has(model))
        {
            Skip = $"{model} is not installed.";
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
    /// <summary>The smallest installed model whose <c>ollama show</c> lists the <c>tools</c> capability and that follows a tool prompt.</summary>
    public const string Model = "qwen2.5-coder:7b";

    private static readonly TimeSpan Budget = TimeSpan.FromMinutes(5);

    [OllamaModelFact(Model)]
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
        };

        var route = gateway.ResolveToolRoute(Request("route"));
        Assert.Equal(ToolCallingSupport.Native, route.ToolCalling);
        Assert.Equal("ollama/" + Model, route.QualifiedId);

        // Turn 1: the model asks for the weather.
        var first = await gateway.ChatWithToolsAsync(Request("m1"), cts.Token).CollectAsync(cts.Token);
        Assert.All(first, u => Assert.Equal(ToolTransport.Native, u.Transport));
        Assert.True(first[^1].IsFinal);
        Assert.Null(first[^1].Error);
        var call = Assert.Single(first.Where(u => u.ToolCall is not null)).ToolCall!;
        Assert.Equal("get_weather", call.Name);
        Assert.Contains("paris", TestTools.Argument(call, "city")?.ToLowerInvariant() ?? string.Empty, StringComparison.Ordinal);
        Assert.Equal(ChatFinishReason.ToolCalls, first[^1].FinishReason);

        // Turn 2: the runtime answers the call and the model answers the user from it.
        messages.Add(new AiChatMessage(AiChatRole.Assistant, [.. first.Where(u => u.Text is not null).Select(u => (AIContent)new TextContent(u.Text!)), call]));
        messages.Add(new AiChatMessage(AiChatRole.Tool, [new FunctionResultContent(call.CallId, "{\"temperature_c\":18,\"conditions\":\"light rain\"}")]));
        var second = await gateway.ChatWithToolsAsync(Request("m2"), cts.Token).CollectAsync(cts.Token);
        var answer = string.Concat(second.Where(u => u.Text is not null).Select(u => u.Text));
        Assert.Null(second[^1].Error);
        Assert.True(answer.Contains("18", StringComparison.Ordinal) || answer.Contains("rain", StringComparison.OrdinalIgnoreCase), answer);

        var records = log.GetRecent();
        Assert.Equal(2, records.Count);
        Assert.Equal(["get_weather"], records[0].ToolsOffered);
        Assert.Equal(["get_weather"], records[0].ToolCalls);
        Assert.True(records[0].ToolArgumentBytes > 0);
        Assert.Equal(System.Text.Encoding.UTF8.GetByteCount("{\"temperature_c\":18,\"conditions\":\"light rain\"}"), records[1].ToolResultBytes);
        Assert.All(records, r => Assert.True(r.IsLocal));
    }
}
