using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Pillion.Common.Settings;
using Pillion.Contracts;
using Pillion.ModelGateway.Providers.Ollama;
using Pillion.ModelGateway.Tests.TestSupport;
using AiChatMessage = Microsoft.Extensions.AI.ChatMessage;
using AiChatRole = Microsoft.Extensions.AI.ChatRole;

namespace Pillion.ModelGateway.Tests.Ollama;

/// <summary>
/// What the local Ollama server offers, probed once per test run. Live tests skip themselves when
/// <c>http://localhost:11434/api/version</c> does not answer, so CI (which has no Ollama) skips them.
/// </summary>
internal static class LiveOllama
{
    public const string Endpoint = "http://localhost:11434/";
    public const string ChatModel = "gemma4:e2b";

    private static readonly Lazy<(bool Reachable, IReadOnlyList<string> Models, string? InsertModel)> State = new(Probe);

    public static bool IsReachable => State.Value.Reachable;

    public static bool HasChatModel => State.Value.Models.Contains(ChatModel, StringComparer.OrdinalIgnoreCase);

    public static string? InsertModel => State.Value.InsertModel;

    private static (bool, IReadOnlyList<string>, string?) Probe()
    {
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(Endpoint), Timeout = TimeSpan.FromSeconds(3) };
            using var version = http.GetAsync("api/version").GetAwaiter().GetResult();
            if (!version.IsSuccessStatusCode)
            {
                return (false, [], null);
            }

            using var tags = JsonDocument.Parse(http.GetStringAsync("api/tags").GetAwaiter().GetResult());
            var models = tags.RootElement.GetProperty("models").EnumerateArray().Select(m => m.GetProperty("name").GetString()!).ToList();
            string? insert = null;
            foreach (var model in models)
            {
                using var content = new StringContent($"{{\"model\":\"{model}\"}}");
                using var response = http.PostAsync("api/show", content).GetAwaiter().GetResult();
                using var show = JsonDocument.Parse(response.Content.ReadAsStringAsync().GetAwaiter().GetResult());
                if (show.RootElement.TryGetProperty("capabilities", out var caps) && caps.EnumerateArray().Any(c => c.GetString() == "insert"))
                {
                    insert = model;
                    break;
                }
            }

            return (true, models, insert);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException)
        {
            return (false, [], null);
        }
    }
}

/// <summary>A fact that runs only when the local Ollama server answers.</summary>
public sealed class LiveOllamaFactAttribute : FactAttribute
{
    public LiveOllamaFactAttribute()
    {
        if (!LiveOllama.IsReachable)
        {
            Skip = $"Ollama is not answering at {LiveOllama.Endpoint}api/version.";
        }
    }
}

/// <summary>A fact that needs the chat model used by the live tests.</summary>
public sealed class LiveOllamaChatFactAttribute : FactAttribute
{
    public LiveOllamaChatFactAttribute()
    {
        if (!LiveOllama.IsReachable)
        {
            Skip = $"Ollama is not answering at {LiveOllama.Endpoint}api/version.";
        }
        else if (!LiveOllama.HasChatModel)
        {
            Skip = $"{LiveOllama.ChatModel} is not installed.";
        }
    }
}

/// <summary>A fact that needs an installed model with the <c>insert</c> capability.</summary>
public sealed class LiveOllamaFimFactAttribute : FactAttribute
{
    public LiveOllamaFimFactAttribute()
    {
        if (!LiveOllama.IsReachable)
        {
            Skip = $"Ollama is not answering at {LiveOllama.Endpoint}api/version.";
        }
        else if (LiveOllama.InsertModel is null)
        {
            Skip = "No installed Ollama model reports the 'insert' capability.";
        }
    }
}

[Trait("Category", "Live")]
public class OllamaLiveTests
{
    // A cold load on the development machine takes about a minute; allow for a slow disk.
    private static readonly TimeSpan Budget = TimeSpan.FromMinutes(5);

    private static OllamaProvider Create() => (OllamaProvider)new OllamaProviderFactory().Create(
        new ProviderSettings { Id = "ollama", Kind = ProviderKind.Ollama, Endpoint = LiveOllama.Endpoint },
        new ProviderCreationContext(null, ModelCatalog.Default, NullLoggerFactory.Instance, null));

    [LiveOllamaFact]
    public async Task ListModels_ReturnsInstalledModelsWithCapabilities()
    {
        using var provider = Create();

        var probe = await provider.ProbeAsync(CancellationToken.None);
        var models = await provider.ListModelsAsync(CancellationToken.None);

        Assert.True(probe.IsReachable, probe.Message);
        Assert.NotEmpty(models);
        Assert.All(models, m =>
        {
            Assert.True(m.Capabilities.IsLocal);
            Assert.StartsWith("sha256:", m.Digest, StringComparison.Ordinal);
            Assert.True(m.Capabilities.ContextWindow > 0);
        });
    }

    [LiveOllamaChatFact]
    public async Task Chat_WithGemma_StreamsAReplyNamingTheModel()
    {
        using var provider = Create();
        using var cts = new CancellationTokenSource(Budget);

        var updates = await provider.GetChatClient(LiveOllama.ChatModel).GetStreamingResponseAsync(
            [new AiChatMessage(AiChatRole.User, "Reply with exactly the word: pillion")],
            new ChatOptions
            {
                Temperature = 0,
                MaxOutputTokens = 16,
                AdditionalProperties = new AdditionalPropertiesDictionary { [GatewayOptionKeys.Profile] = Profile.LocalFast },
            },
            cts.Token).CollectAsync(cts.Token);

        var text = string.Concat(updates.Select(u => u.Text));
        Assert.False(string.IsNullOrWhiteSpace(text));
        Assert.All(updates, u => Assert.Equal(LiveOllama.ChatModel, u.ModelId));
        Assert.NotNull(updates[^1].FinishReason);
    }

    [LiveOllamaChatFact]
    public async Task Gateway_ChatWithAnExplicitModel_NamesTheOllamaModelOnEveryChunk()
    {
        var settings = new PillionSettings();
        settings.Providers.Add(new ProviderSettings { Id = "ollama", Kind = ProviderKind.Ollama, Endpoint = LiveOllama.Endpoint });
        var options = new ModelGatewayOptions();
        using var registry = new ProviderRegistry(new StaticGatewaySettingsSource(settings), new Common.Secrets.InMemorySecretStore(), [new OllamaProviderFactory()], ModelCatalog.Default);
        var inventory = new ModelInventory();
        var breakers = new CircuitBreakerRegistry(options);
        var log = new InMemoryOutboundLog(options);
        var router = new ModelRouter(registry, ModelCatalog.Default, inventory, new AlwaysReachable(), breakers, new NoCostCaps());
        var gateway = new ModelGateway(registry, router, inventory, ModelCatalog.Default, PromptTemplates.Default, new AutocompleteCache(options), breakers, new ConcurrencyLimiter(options), log, new AlwaysReachable(), new NoCostCaps(), new NoProgressSink(), options);
        using var cts = new CancellationTokenSource(Budget);

        await gateway.ListModelsAsync(cts.Token);
        var chunks = await gateway.ChatAsync(GatewayHarness.ChatRequest("Reply with exactly the word: pillion", modelId: "ollama/" + LiveOllama.ChatModel), TaskClass.Chat, cts.Token).CollectAsync(cts.Token);

        Assert.False(string.IsNullOrWhiteSpace(string.Concat(chunks.Select(c => c.Text))));
        Assert.All(chunks, c => Assert.Equal(LiveOllama.ChatModel, c.ModelId));
        Assert.All(chunks, c => Assert.Equal("ollama", c.ProviderId));
        Assert.True(chunks[^1].IsFinal);
        var record = Assert.Single(log.GetRecent());
        Assert.Equal(OutboundOutcome.Succeeded, record.Outcome);
        Assert.True(record.IsLocal);
    }

    [LiveOllamaFimFact]
    public async Task Fim_WithAnInsertCapableModel_CompletesBetweenPrefixAndSuffix()
    {
        using var provider = Create();
        using var cts = new CancellationTokenSource(Budget);

        var pieces = await provider.GetFimClient(LiveOllama.InsertModel!)!.CompleteAsync(
            new FimRequest { Prefix = "def add(a, b):\n    return ", Suffix = "\n\nprint(add(1, 2))\n", MaxTokens = 16, Temperature = 0, Profile = Profile.LocalFast },
            cts.Token).CollectAsync(cts.Token);

        Assert.False(string.IsNullOrWhiteSpace(string.Concat(pieces)));
    }
}
