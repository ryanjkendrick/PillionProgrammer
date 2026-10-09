using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Pillion.Common.Secrets;
using Pillion.Common.Settings;
using Pillion.ModelGateway.Providers.Gemini;
using Pillion.ModelGateway.Tests.Providers.Cloud;
using Pillion.ModelGateway.Tests.TestSupport;
using AiChatMessage = Microsoft.Extensions.AI.ChatMessage;
using AiChatRole = Microsoft.Extensions.AI.ChatRole;

namespace Pillion.ModelGateway.Tests.Providers.Gemini;

/// <summary>
/// Talks to the real Gemini API. Set <c>PILLION_TEST_GEMINI_KEY</c> to run them; nothing is read from anywhere else, so
/// the tests never pick up a key from the machine. They spend a few tokens on the cheapest catalog model.
/// </summary>
[Trait("Category", "Live")]
public sealed class GeminiLiveTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromMinutes(2);

    private static GeminiProvider Create()
    {
        var secrets = new InMemorySecretStore();
        secrets.Set("live", LiveEnvironment.Get(LiveEnvironment.GeminiKey));
        return (GeminiProvider)new GeminiProviderFactory(secrets).Create(
            new ProviderSettings { Id = "live", Kind = ProviderKind.Gemini },
            new ProviderCreationContext(null, ModelCatalog.Default, NullLoggerFactory.Instance, null));
    }

    [LiveFact(LiveEnvironment.GeminiKey)]
    public async Task Probe_ReportsReachableAndAuthenticated()
    {
        using var provider = Create();
        using var cts = new CancellationTokenSource(Budget);

        var probe = await provider.ProbeDetailedAsync(cts.Token);

        Assert.True(probe.IsReachable, probe.Message);
        Assert.True(probe.IsAuthenticated, probe.Message);
    }

    [LiveFact(LiveEnvironment.GeminiKey)]
    public async Task ListModels_ReturnsModelsNamingTheProvider()
    {
        using var provider = Create();
        using var cts = new CancellationTokenSource(Budget);

        var models = await provider.ListModelsAsync(cts.Token);

        Assert.NotEmpty(models);
        Assert.All(models, m => Assert.Equal("live", m.ProviderId));
        Assert.All(models, m => Assert.DoesNotContain("models/", m.ModelId, StringComparison.Ordinal));
    }

    [LiveFact(LiveEnvironment.GeminiKey)]
    public async Task Chat_StreamsAReplyNamingTheModel()
    {
        using var provider = Create();
        using var cts = new CancellationTokenSource(Budget);
        var model = ModelCatalog.Default.GetDefaultModel(ProviderKind.Gemini, Pillion.Contracts.Profile.Fast)!;

        var updates = await provider.GetChatClient(model).GetStreamingResponseAsync(
            [new AiChatMessage(AiChatRole.User, "Reply with exactly the word: pillion")],
            new ChatOptions { MaxOutputTokens = 256, Reasoning = new ReasoningOptions { Effort = ReasoningEffort.Low } },
            cts.Token).CollectAsync(cts.Token);

        Assert.False(string.IsNullOrWhiteSpace(string.Concat(updates.Select(u => u.Text))));
        Assert.All(updates, u => Assert.Equal(model, u.ModelId));
        Assert.Contains(updates.SelectMany(u => u.Contents).OfType<UsageContent>(), u => u.Details.InputTokenCount > 0);
    }
}
