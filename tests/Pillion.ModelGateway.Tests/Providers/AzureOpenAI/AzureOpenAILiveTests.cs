using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Pillion.Common.Secrets;
using Pillion.Common.Settings;
using Pillion.ModelGateway.Providers.AzureOpenAI;
using Pillion.ModelGateway.Tests.Providers.Cloud;
using Pillion.ModelGateway.Tests.TestSupport;
using AiChatMessage = Microsoft.Extensions.AI.ChatMessage;
using AiChatRole = Microsoft.Extensions.AI.ChatRole;

namespace Pillion.ModelGateway.Tests.Providers.AzureOpenAI;

/// <summary>
/// Talks to a real Azure OpenAI resource. Set <c>PILLION_TEST_AZURE_OPENAI_ENDPOINT</c> and
/// <c>PILLION_TEST_AZURE_OPENAI_KEY</c> to run them; <c>PILLION_TEST_AZURE_OPENAI_DEPLOYMENT</c> names a chat deployment
/// for the chat test (otherwise that test is skipped). Nothing is read from anywhere else, so the tests never pick up a
/// key from the machine.
/// </summary>
[Trait("Category", "Live")]
public sealed class AzureOpenAILiveTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromMinutes(2);

    private static AzureOpenAIProvider Create(params string[] deployments)
    {
        var secrets = new InMemorySecretStore();
        secrets.Set("live", LiveEnvironment.Get(LiveEnvironment.AzureKey));
        return (AzureOpenAIProvider)new AzureOpenAIProviderFactory(secrets).Create(
            new ProviderSettings
            {
                Id = "live",
                Kind = ProviderKind.AzureOpenAi,
                Endpoint = LiveEnvironment.Get(LiveEnvironment.AzureEndpoint),
                Models = [.. deployments],
            },
            new ProviderCreationContext(null, ModelCatalog.Default, NullLoggerFactory.Instance, null));
    }

    [LiveFact(LiveEnvironment.AzureEndpoint, LiveEnvironment.AzureKey)]
    public async Task Probe_ReportsReachable_AndNotRejected()
    {
        using var provider = Create();
        using var cts = new CancellationTokenSource(Budget);

        var probe = await provider.ProbeDetailedAsync(cts.Token);

        Assert.True(probe.IsReachable, probe.Message);
        Assert.NotEqual(false, probe.IsAuthenticated);
    }

    [LiveFact(LiveEnvironment.AzureEndpoint, LiveEnvironment.AzureKey, LiveEnvironment.AzureDeployment)]
    public async Task Chat_StreamsAReplyNamingTheDeployment()
    {
        var deployment = LiveEnvironment.Get(LiveEnvironment.AzureDeployment);
        using var provider = Create(deployment);
        using var cts = new CancellationTokenSource(Budget);

        var models = await provider.ListModelsAsync(cts.Token);
        var updates = await provider.GetChatClient(deployment).GetStreamingResponseAsync(
            [new AiChatMessage(AiChatRole.User, "Reply with exactly the word: pillion")],
            new ChatOptions { MaxOutputTokens = 256 },
            cts.Token).CollectAsync(cts.Token);

        Assert.Equal(deployment, Assert.Single(models).ModelId);
        Assert.False(string.IsNullOrWhiteSpace(string.Concat(updates.Select(u => u.Text))));
        Assert.All(updates, u => Assert.Equal(deployment, u.ModelId));
    }
}
