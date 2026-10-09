using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Pillion.Common.Secrets;
using Pillion.Common.Settings;
using Pillion.ModelGateway.Providers.Mistral;
using Pillion.ModelGateway.Tests.Providers.Cloud;
using Pillion.ModelGateway.Tests.TestSupport;
using AiChatMessage = Microsoft.Extensions.AI.ChatMessage;
using AiChatRole = Microsoft.Extensions.AI.ChatRole;

namespace Pillion.ModelGateway.Tests.Providers.Mistral;

/// <summary>
/// Talks to the real Mistral API. Set <c>PILLION_TEST_MISTRAL_KEY</c> to run them; nothing is read from anywhere else, so
/// the tests never pick up a key from the machine. They spend a few tokens.
/// </summary>
[Trait("Category", "Live")]
public sealed class MistralLiveTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromMinutes(2);

    private static MistralProvider Create()
    {
        var secrets = new InMemorySecretStore();
        secrets.Set("live", LiveEnvironment.Get(LiveEnvironment.MistralKey));
        return (MistralProvider)new MistralProviderFactory(secrets).Create(
            new ProviderSettings { Id = "live", Kind = ProviderKind.Mistral },
            new ProviderCreationContext(null, ModelCatalog.Default, NullLoggerFactory.Instance, null));
    }

    [LiveFact(LiveEnvironment.MistralKey)]
    public async Task Probe_ReportsReachableAndAuthenticated()
    {
        using var provider = Create();
        using var cts = new CancellationTokenSource(Budget);

        var probe = await provider.ProbeDetailedAsync(cts.Token);

        Assert.True(probe.IsReachable, probe.Message);
        Assert.True(probe.IsAuthenticated, probe.Message);
    }

    [LiveFact(LiveEnvironment.MistralKey)]
    public async Task ListModels_ReturnsModelsNamingTheProvider_AndMarksCodestralFim()
    {
        using var provider = Create();
        using var cts = new CancellationTokenSource(Budget);

        var models = await provider.ListModelsAsync(cts.Token);

        Assert.NotEmpty(models);
        Assert.All(models, m => Assert.Equal("live", m.ProviderId));
        Assert.Contains(models, m => m.Capabilities.Fim);
    }

    [LiveFact(LiveEnvironment.MistralKey)]
    public async Task Chat_StreamsAReplyNamingTheModel()
    {
        using var provider = Create();
        using var cts = new CancellationTokenSource(Budget);
        const string model = "mistral-small-latest";

        var updates = await provider.GetChatClient(model).GetStreamingResponseAsync(
            [new AiChatMessage(AiChatRole.User, "Reply with exactly the word: pillion")],
            new ChatOptions { MaxOutputTokens = 32 },
            cts.Token).CollectAsync(cts.Token);

        Assert.False(string.IsNullOrWhiteSpace(string.Concat(updates.Select(u => u.Text))));
        Assert.All(updates, u => Assert.Equal(model, u.ModelId));
    }

    [LiveFact(LiveEnvironment.MistralKey)]
    public async Task Fim_CompletesBetweenPrefixAndSuffix()
    {
        using var provider = Create();
        using var cts = new CancellationTokenSource(Budget);
        var client = provider.GetFimClient("codestral-latest") ?? throw new InvalidOperationException("No FIM client for codestral-latest.");

        var pieces = await client.CompleteAsync(new FimRequest { Prefix = "def add(a, b):\n    return ", Suffix = "\n", MaxTokens = 16, Temperature = 0 }, cts.Token).CollectAsync(cts.Token);

        Assert.False(string.IsNullOrWhiteSpace(string.Concat(pieces)));
    }
}
