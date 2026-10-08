using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Pillion.Common.Secrets;
using Pillion.Common.Settings;
using Pillion.ModelGateway.Providers.OpenAICompatible;
using Pillion.ModelGateway.Tests.TestSupport;
using AiChatMessage = Microsoft.Extensions.AI.ChatMessage;
using AiChatRole = Microsoft.Extensions.AI.ChatRole;

namespace Pillion.ModelGateway.Tests.Providers.OpenAICompatible;

/// <summary>
/// The server the live tests talk to. Set <c>PILLION_TEST_OPENAI_COMPAT_ENDPOINT</c> (for example
/// <c>http://localhost:1234/v1</c>) to enable them; <c>PILLION_TEST_OPENAI_COMPAT_KEY</c> supplies a key if the server wants
/// one and <c>PILLION_TEST_OPENAI_COMPAT_MODEL</c> names the chat model (otherwise the first listed chat model is used).
/// Nothing is read from anywhere else, so the tests never pick up a key from the machine.
/// </summary>
internal static class LiveOpenAICompatible
{
    public const string EndpointVariable = "PILLION_TEST_OPENAI_COMPAT_ENDPOINT";

    public const string KeyVariable = "PILLION_TEST_OPENAI_COMPAT_KEY";

    public const string ModelVariable = "PILLION_TEST_OPENAI_COMPAT_MODEL";

    public static string? Endpoint => Environment.GetEnvironmentVariable(EndpointVariable) is { Length: > 0 } value ? value : null;

    public static OpenAICompatibleProvider Create()
    {
        var secrets = new InMemorySecretStore();
        if (Environment.GetEnvironmentVariable(KeyVariable) is { Length: > 0 } key)
        {
            secrets.Set("live", key);
        }

        return (OpenAICompatibleProvider)new OpenAICompatibleProviderFactory(secrets).Create(
            new ProviderSettings { Id = "live", Kind = ProviderKind.OpenAiCompatible, Endpoint = Endpoint! },
            new ProviderCreationContext(null, ModelCatalog.Default, NullLoggerFactory.Instance, null));
    }
}

/// <summary>A fact that runs only when <c>PILLION_TEST_OPENAI_COMPAT_ENDPOINT</c> is set.</summary>
public sealed class LiveOpenAICompatibleFactAttribute : FactAttribute
{
    public LiveOpenAICompatibleFactAttribute()
    {
        if (LiveOpenAICompatible.Endpoint is null)
        {
            Skip = $"Set {LiveOpenAICompatible.EndpointVariable} to an OpenAI-compatible endpoint to run this test.";
        }
    }
}

[Trait("Category", "Live")]
public class OpenAICompatibleLiveTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromMinutes(5);

    private static async Task<string> ChatModelAsync(OpenAICompatibleProvider provider, CancellationToken cancellationToken)
    {
        if (Environment.GetEnvironmentVariable(LiveOpenAICompatible.ModelVariable) is { Length: > 0 } configured)
        {
            return configured;
        }

        var models = await provider.ListModelsAsync(cancellationToken);
        return models.FirstOrDefault(m => m.Capabilities.Chat)?.ModelId
            ?? throw new InvalidOperationException($"The server lists no chat model; set {LiveOpenAICompatible.ModelVariable}.");
    }

    [LiveOpenAICompatibleFact]
    public async Task Probe_ReportsTheServer()
    {
        using var provider = LiveOpenAICompatible.Create();
        using var cts = new CancellationTokenSource(Budget);

        var probe = await provider.ProbeDetailedAsync(cts.Token);

        Assert.True(probe.IsReachable, probe.Message);
        Assert.NotEqual(false, probe.IsAuthenticated);
    }

    [LiveOpenAICompatibleFact]
    public async Task ListModels_ReturnsModelsNamingTheProvider()
    {
        using var provider = LiveOpenAICompatible.Create();
        using var cts = new CancellationTokenSource(Budget);

        var models = await provider.ListModelsAsync(cts.Token);

        Assert.NotEmpty(models);
        Assert.All(models, m => Assert.Equal("live", m.ProviderId));
    }

    [LiveOpenAICompatibleFact]
    public async Task Chat_StreamsAReplyNamingTheModel()
    {
        using var provider = LiveOpenAICompatible.Create();
        using var cts = new CancellationTokenSource(Budget);
        var model = await ChatModelAsync(provider, cts.Token);

        var updates = await provider.GetChatClient(model).GetStreamingResponseAsync(
            [new AiChatMessage(AiChatRole.User, "Reply with exactly the word: pillion")],
            new ChatOptions { Temperature = 0, MaxOutputTokens = 32 },
            cts.Token).CollectAsync(cts.Token);

        Assert.False(string.IsNullOrWhiteSpace(string.Concat(updates.Select(u => u.Text))));
        Assert.All(updates, u => Assert.Equal(model, u.ModelId));
    }
}
