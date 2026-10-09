using System.Text;
using Pillion.Common.Secrets;
using Pillion.Common.Settings;
using Pillion.Contracts;
using Pillion.ModelGateway.Providers.Ollama;
using Pillion.ModelGateway.Tests.Ollama;
using Pillion.ModelGateway.Tests.TestSupport;

namespace Pillion.ModelGateway.Tests.Context;

/// <summary>
/// Context assembly against a real local model. Skipped when Ollama does not answer or <c>gemma4:e2b</c> is not installed, so
/// CI (which has neither) skips them. Run with <c>--filter Category=Live</c>.
/// </summary>
[Trait("Category", "Live")]
public sealed class ContextLiveTests : IDisposable
{
    // A cold load on the development machine takes about a minute; allow for a slow disk.
    private static readonly TimeSpan Budget = TimeSpan.FromMinutes(8);

    private readonly ProviderRegistry _registry;
    private readonly InMemoryOutboundLog _log;
    private readonly ModelGateway _gateway;

    public ContextLiveTests()
    {
        var settings = new PillionSettings();
        settings.Providers.Add(new ProviderSettings { Id = "ollama", Kind = ProviderKind.Ollama, Endpoint = LiveOllama.Endpoint });
        settings.Routing.Profiles[Profile.LocalFast] = "ollama/" + LiveOllama.ChatModel;
        settings.Routing.Profiles[Profile.LocalStrong] = "ollama/" + LiveOllama.ChatModel;
        var options = new ModelGatewayOptions();
        _registry = new ProviderRegistry(new StaticGatewaySettingsSource(settings), new InMemorySecretStore(), [new OllamaProviderFactory()], ModelCatalog.Default);
        var inventory = new ModelInventory();
        var breakers = new CircuitBreakerRegistry(options);
        _log = new InMemoryOutboundLog(options);
        var router = new ModelRouter(_registry, ModelCatalog.Default, inventory, new AlwaysReachable(), breakers, new NoCostCaps());
        _gateway = new ModelGateway(_registry, router, inventory, ModelCatalog.Default, PromptTemplates.Default, new AutocompleteCache(options), breakers, new ConcurrencyLimiter(options), _log, new AlwaysReachable(), new NoCostCaps(), new NoProgressSink(), options);
    }

    public void Dispose() => _registry.Dispose();

    [LiveOllamaChatFact]
    public async Task Chat_WithAPinnedFile_AnswersFromThePin_AndTheOutboundRecordShowsItsTokens()
    {
        using var cts = new CancellationTokenSource(Budget);
        await _gateway.ListModelsAsync(cts.Token);
        const string Source =
            "// Project notes\n// The code name of this project is Zephyrion.\npublic static class Project { public const string Name = \"Zephyrion\"; }\n";
        var pin = new MentionResolution
        {
            Mention = "@file:Project.cs",
            IsResolved = true,
            Kind = "file",
            DisplayName = "Project.cs",
            FilePath = @"C:\src\Project.cs",
            Content = Source,
            TokenCount = TokenCounter.Default.ForModel(LiveOllama.ChatModel).Count(Source),
        };
        var request = new ChatRequest
        {
            RequestId = "live-pin",
            ModelId = "ollama/" + LiveOllama.ChatModel,
            Profile = Profile.Auto,
            Pins = [pin],
            Messages = [new ChatMessage { Role = ChatRole.User, Content = "What is the code name of the project? Answer with that one word only." }],
        };

        var chunks = await _gateway.ChatAsync(request, TaskClass.Chat, cts.Token).CollectAsync(cts.Token);

        var reply = string.Concat(chunks.Select(c => c.Text));
        Assert.Contains("zephyrion", reply, StringComparison.OrdinalIgnoreCase);
        Assert.Null(chunks[^1].Error);
        var record = Assert.Single(_log.GetRecent());
        Assert.Equal(OutboundOutcome.Succeeded, record.Outcome);
        Assert.True(record.IsLocal);
        Assert.Equal(3, record.ContextItems); // System prompt, the pin, the message.
        Assert.Equal(0, record.ContextTrimmed);
        Assert.True(record.EstimatedPromptTokens >= pin.TokenCount, $"The record shows {record.EstimatedPromptTokens} prompt tokens; the pin alone is {pin.TokenCount}.");
        Assert.True(chunks[^1].PromptTokens >= pin.TokenCount);
    }

    [LiveOllamaChatFact]
    public async Task TheSummariser_CompressesALongFakeHistory_WithTheLocalModel()
    {
        using var cts = new CancellationTokenSource(Budget);
        await _gateway.ListModelsAsync(cts.Token);
        var history = FakeHistory(12);
        var transcriptLength = history.Sum(m => m.Content.Length);

        var summary = await new GatewayHistorySummariser(_gateway).SummariseAsync(
            new SummariseRequest("live", "live-sum", null, history, 256, KeepLocal: true),
            cts.Token);

        Assert.False(string.IsNullOrWhiteSpace(summary));
        Assert.True(summary.Length < transcriptLength / 2, $"The summary has {summary.Length} characters for {transcriptLength} of history.");
        var records = _log.GetRecent();
        Assert.NotEmpty(records);
        Assert.All(records, r =>
        {
            Assert.Equal(TaskClass.Summarise, r.TaskClass);
            Assert.True(r.IsLocal);
            Assert.Equal(LiveOllama.ChatModel, r.ModelId);
        });
    }

    [LiveOllamaChatFact]
    public async Task ALongConversation_IsCompressedBeforeTheChat_AndStillAnswers()
    {
        using var cts = new CancellationTokenSource(Budget);
        await _gateway.ListModelsAsync(cts.Token);
        var messages = FakeHistory(24);
        messages.Add(new ChatMessage { Role = ChatRole.User, Content = "In one short sentence: which topic did we discuss first?" });
        var request = new ChatRequest
        {
            RequestId = "live-long",
            ConversationId = "live-long",
            ModelId = "ollama/" + LiveOllama.ChatModel,
            Profile = Profile.Auto,
            ProjectRules = "Answer briefly.",
            Messages = messages,
        };

        var chunks = await _gateway.ChatAsync(request, TaskClass.Chat, cts.Token).CollectAsync(cts.Token);

        Assert.False(string.IsNullOrWhiteSpace(string.Concat(chunks.Select(c => c.Text))));
        Assert.Null(chunks[^1].Error);
        var records = _log.GetRecent();
        Assert.Contains(records, r => r.TaskClass == TaskClass.Summarise);
        var chat = records.Last(r => r.TaskClass == TaskClass.Chat);
        Assert.Equal(OutboundOutcome.Succeeded, chat.Outcome);
        Assert.True(chat.EstimatedPromptTokens < 8192, $"The chat prompt has {chat.EstimatedPromptTokens} tokens against an 8,192-token window.");
    }

    /// <summary>A made-up conversation about shipping an invoicing feature, <paramref name="turns"/> exchanges of about 250 tokens each.</summary>
    private static List<ChatMessage> FakeHistory(int turns)
    {
        string[] topics = ["invoice rounding", "the retry policy", "a flaky integration test", "the customer export", "database indexes", "the nightly job", "feature flags", "the audit log"];
        var messages = new List<ChatMessage>();
        for (var i = 0; i < turns; i++)
        {
            var topic = topics[i % topics.Length];
            var question = new StringBuilder($"Turn {i + 1}: I want to talk about {topic}. ");
            for (var n = 0; n < 8; n++)
            {
                question.Append("We noticed that the behaviour of ").Append(topic).Append(" changed after the last release and nobody is sure why it happened. ");
            }

            var answer = new StringBuilder($"On {topic}: ");
            for (var n = 0; n < 8; n++)
            {
                answer.Append("A reasonable first step is to add logging around ").Append(topic).Append(", reproduce the problem with a small test, and compare against the previous release. ");
            }

            messages.Add(new ChatMessage { Role = ChatRole.User, Content = question.ToString() });
            messages.Add(new ChatMessage { Role = ChatRole.Assistant, Content = answer.ToString() });
        }

        return messages;
    }
}
