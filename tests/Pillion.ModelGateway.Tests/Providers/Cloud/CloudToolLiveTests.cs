using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Pillion.Common.Secrets;
using Pillion.Common.Settings;
using Pillion.Contracts;
using Pillion.ModelGateway.Providers.Anthropic;
using Pillion.ModelGateway.Providers.AzureOpenAI;
using Pillion.ModelGateway.Providers.Gemini;
using Pillion.ModelGateway.Providers.Mistral;
using Pillion.ModelGateway.Tests.Providers.OpenAICompatible;
using Pillion.ModelGateway.Tests.TestSupport;
using AiChatMessage = Microsoft.Extensions.AI.ChatMessage;
using AiChatRole = Microsoft.Extensions.AI.ChatRole;

namespace Pillion.ModelGateway.Tests.Providers.Cloud;

/// <summary>
/// One tool round trip per cloud provider against the real API (work item 3.23, the gate's live row): the model calls
/// <c>get_weather</c> natively and then answers from the result. Each test runs only with its provider's key in the
/// environment (<c>PILLION_TEST_*_KEY</c>), as the other live tests do; nothing is read from anywhere else.
/// </summary>
[Trait("Category", "Live")]
public sealed class CloudToolLiveTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromMinutes(2);

    private static ProviderCreationContext Context() => new(null, ModelCatalog.Default, NullLoggerFactory.Instance, null);

    private static InMemorySecretStore Secrets(string variable)
    {
        var secrets = new InMemorySecretStore();
        secrets.Set("live", LiveEnvironment.Get(variable));
        return secrets;
    }

    /// <summary>Asks for the weather, checks the native call, answers it and checks the final text.</summary>
    private static async Task RoundTripAsync(IChatClient client, CancellationToken cancellationToken)
    {
        var options = new ChatOptions { Tools = [TestTools.GetWeather], MaxOutputTokens = 256 };
        var messages = new List<AiChatMessage>
        {
            new(AiChatRole.System, "You are a weather assistant. Always call the get_weather tool before answering a weather question."),
            new(AiChatRole.User, "What is the weather in Paris right now?"),
        };

        var first = await client.GetStreamingResponseAsync(messages, options, cancellationToken).CollectAsync(cancellationToken);
        var call = Assert.Single(first.SelectMany(u => u.Contents).OfType<FunctionCallContent>());
        Assert.Equal("get_weather", call.Name);
        Assert.Contains("paris", TestTools.Argument(call, "city")?.ToLowerInvariant() ?? string.Empty, StringComparison.Ordinal);
        Assert.Equal(ChatFinishReason.ToolCalls, first.Last(u => u.FinishReason is not null).FinishReason);

        var assistant = new List<AIContent>();
        var text = string.Concat(first.Select(u => u.Text));
        if (text.Length > 0)
        {
            assistant.Add(new TextContent(text));
        }

        assistant.Add(call);
        messages.Add(new AiChatMessage(AiChatRole.Assistant, assistant));
        messages.Add(new AiChatMessage(AiChatRole.Tool, [new FunctionResultContent(call.CallId, TestTools.WeatherResult)]));

        var second = await client.GetStreamingResponseAsync(messages, options, cancellationToken).CollectAsync(cancellationToken);
        var answer = string.Concat(second.Select(u => u.Text));
        Assert.True(answer.Contains("18", StringComparison.Ordinal) || answer.Contains("rain", StringComparison.OrdinalIgnoreCase), answer);
    }

    [LiveFact(LiveEnvironment.AnthropicKey)]
    public async Task Anthropic_GetWeather_RoundTrip()
    {
        using var provider = (AnthropicProvider)new AnthropicProviderFactory(Secrets(LiveEnvironment.AnthropicKey)).Create(new ProviderSettings { Id = "live", Kind = ProviderKind.Anthropic }, Context());
        using var cts = new CancellationTokenSource(Budget);

        await RoundTripAsync(provider.GetChatClient(ModelCatalog.Default.GetDefaultModel(ProviderKind.Anthropic, Profile.Fast)!), cts.Token);
    }

    [LiveFact(LiveEnvironment.GeminiKey)]
    public async Task Gemini_GetWeather_RoundTrip()
    {
        using var provider = (GeminiProvider)new GeminiProviderFactory(Secrets(LiveEnvironment.GeminiKey)).Create(new ProviderSettings { Id = "live", Kind = ProviderKind.Gemini }, Context());
        using var cts = new CancellationTokenSource(Budget);

        await RoundTripAsync(provider.GetChatClient(ModelCatalog.Default.GetDefaultModel(ProviderKind.Gemini, Profile.Fast)!), cts.Token);
    }

    [LiveFact(LiveEnvironment.MistralKey)]
    public async Task Mistral_GetWeather_RoundTrip()
    {
        using var provider = (MistralProvider)new MistralProviderFactory(Secrets(LiveEnvironment.MistralKey)).Create(new ProviderSettings { Id = "live", Kind = ProviderKind.Mistral }, Context());
        using var cts = new CancellationTokenSource(Budget);

        await RoundTripAsync(provider.GetChatClient(ModelCatalog.Default.GetDefaultModel(ProviderKind.Mistral, Profile.Balanced)!), cts.Token);
    }

    [LiveFact(LiveEnvironment.AzureEndpoint, LiveEnvironment.AzureKey, LiveEnvironment.AzureDeployment)]
    public async Task AzureOpenAI_GetWeather_RoundTrip()
    {
        var deployment = LiveEnvironment.Get(LiveEnvironment.AzureDeployment);
        using var provider = (AzureOpenAIProvider)new AzureOpenAIProviderFactory(Secrets(LiveEnvironment.AzureKey)).Create(
            new ProviderSettings { Id = "live", Kind = ProviderKind.AzureOpenAi, Endpoint = LiveEnvironment.Get(LiveEnvironment.AzureEndpoint), Models = [deployment] },
            Context());
        using var cts = new CancellationTokenSource(Budget);

        await RoundTripAsync(provider.GetChatClient(deployment), cts.Token);
    }

    [LiveOpenAICompatibleFact]
    public async Task OpenAICompatible_GetWeather_RoundTrip()
    {
        using var provider = LiveOpenAICompatible.Create();
        using var cts = new CancellationTokenSource(Budget);
        var model = Environment.GetEnvironmentVariable(LiveOpenAICompatible.ModelVariable) is { Length: > 0 } configured
            ? configured
            : (await provider.ListModelsAsync(cts.Token)).First(m => m.Capabilities.Chat).ModelId;

        await RoundTripAsync(provider.GetChatClient(model), cts.Token);
    }
}
