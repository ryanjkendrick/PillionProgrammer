using System.Net;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Pillion.Common.Secrets;
using Pillion.Common.Settings;
using Pillion.Contracts;
using Pillion.ModelGateway.Providers.Anthropic;
using Pillion.ModelGateway.Tests.Providers.Cloud;
using Pillion.ModelGateway.Tests.TestSupport;
using AiChatMessage = Microsoft.Extensions.AI.ChatMessage;
using AiChatRole = Microsoft.Extensions.AI.ChatRole;

namespace Pillion.ModelGateway.Tests.Providers.Anthropic;

/// <summary>The Anthropic adapter against fixtures in the documented Messages API wire shape.</summary>
public sealed class AnthropicProviderTests
{
    // Fabricated; the 401 fixture echoes a masked copy of a key, as some servers do.
    internal const string Key = "unit-test-key-0001";

    private static readonly AiChatMessage[] Hello = [new AiChatMessage(AiChatRole.User, "Say hello")];

    internal static FakeCloudHandler Defaults() => new FakeCloudHandler()
        .Serve("GET v1/models", "Anthropic", "models.json")
        .Serve("POST v1/messages", "Anthropic", "chat-stream.sse");

    internal static ProviderSettings Entry(string? extensionJson = null, string endpoint = "") => Settings("claude", extensionJson, endpoint);

    internal static ProviderSettings Settings(string id, string? extensionJson = null, string endpoint = "")
    {
        var settings = new ProviderSettings { Id = id, Kind = ProviderKind.Anthropic, Endpoint = endpoint };
        if (extensionJson is not null)
        {
            settings.ExtensionData = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(extensionJson);
        }

        return settings;
    }

    internal static AnthropicProvider Create(FakeCloudHandler handler, InMemorySecretStore secrets, string? extensionJson = null, string endpoint = "") =>
        (AnthropicProvider)new AnthropicProviderFactory(secrets, () => handler).Create(
            Entry(extensionJson, endpoint),
            new ProviderCreationContext(null, ModelCatalog.Default, NullLoggerFactory.Instance, null));

    private static InMemorySecretStore SecretsWithKey()
    {
        var secrets = new InMemorySecretStore();
        secrets.Set("claude", Key);
        return secrets;
    }

    private static Task<List<ChatResponseUpdate>> ChatAsync(AnthropicProvider provider, string model = "claude-sonnet-5-5", ChatOptions? options = null, IEnumerable<AiChatMessage>? messages = null, CancellationToken cancellationToken = default) =>
        provider.GetChatClient(model).GetStreamingResponseAsync(messages ?? Hello, options, cancellationToken).CollectAsync(cancellationToken);

    private static string TextOf(IEnumerable<ChatResponseUpdate> updates) => string.Concat(updates.Select(u => u.Text));

    // Creation.
    [Fact]
    public void TheProvider_IsCloud_NeedsAKey_AndHasNoFimOrEmbeddings()
    {
        using var provider = Create(Defaults(), SecretsWithKey());
        var factory = new AnthropicProviderFactory(new InMemorySecretStore());

        Assert.False(provider.IsLocal);
        Assert.Equal(ProviderKind.Anthropic, provider.Kind);
        Assert.Equal("Anthropic", provider.DisplayName);
        Assert.True(factory.RequiresApiKey(Entry()));
        Assert.Equal(FeatureNames.AnthropicProvider, factory.FeatureFlag);
        Assert.Null(provider.GetFimClient("claude-haiku-5-5"));
        Assert.Equal("https://api.anthropic.com/", provider.Endpoint.AbsoluteUri);
        Assert.Equal("2023-06-01", provider.ApiVersion);
    }

    [Fact]
    public void TheFactory_RefusesAnotherKind()
    {
        var factory = new AnthropicProviderFactory(new InMemorySecretStore());

        Assert.Throws<ArgumentException>(() => factory.Create(
            new ProviderSettings { Id = "x", Kind = ProviderKind.Gemini },
            new ProviderCreationContext(null, ModelCatalog.Default, NullLoggerFactory.Instance, null)));
    }

    [Fact]
    public async Task ACustomEndpointAndVersion_AreHonoured()
    {
        var handler = Defaults().Serve("POST anthropic/v1/messages", "Anthropic", "chat-stream.sse");
        using var provider = Create(handler, SecretsWithKey(), """{ "anthropicVersion": "2099-01-01" }""", "https://gateway.example/anthropic");

        await ChatAsync(provider);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("gateway.example", request.Host);
        Assert.Equal("anthropic/v1/messages", request.Path);
        Assert.Equal("2099-01-01", request.Header("anthropic-version"));
    }

    // Models.
    [Fact]
    public async Task ListModels_ReadsLimitsAndCapabilitiesFromTheModelsApi()
    {
        var handler = Defaults();
        using var provider = Create(handler, SecretsWithKey());

        var models = await provider.ListModelsAsync(CancellationToken.None);

        Assert.Equal(["claude-sonnet-5-5", "claude-haiku-5-5", "claude-legacy-text"], models.Select(m => m.ModelId));
        Assert.All(models, m => Assert.Equal("claude", m.ProviderId));
        var sonnet = models[0];
        Assert.Equal("Claude Sonnet 5.5", sonnet.DisplayName);
        Assert.Equal(1_000_000, sonnet.Capabilities.ContextWindow);
        Assert.Equal(128_000, sonnet.Capabilities.MaxOutputTokens);
        Assert.True(sonnet.Capabilities.Vision);
        Assert.True(sonnet.Capabilities.ReasoningEffort);
        Assert.True(sonnet.Capabilities.Chat);
        Assert.False(sonnet.Capabilities.Fim);
        Assert.False(sonnet.Capabilities.Embeddings);
        Assert.False(sonnet.Capabilities.IsLocal);
        Assert.Equal(2m, sonnet.Capabilities.InputCostPerMillionTokens);
        Assert.Equal(10m, sonnet.Capabilities.OutputCostPerMillionTokens);

        // What the API reports wins over the catalog's family defaults.
        var legacy = models[2];
        Assert.Equal(200_000, legacy.Capabilities.ContextWindow);
        Assert.Equal(8192, legacy.Capabilities.MaxOutputTokens);
        Assert.False(legacy.Capabilities.Vision);
        Assert.False(legacy.Capabilities.ReasoningEffort);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("GET", request.Method);
        Assert.Equal(Key, request.Header("x-api-key"));
        Assert.Equal("2023-06-01", request.Header("anthropic-version"));
        Assert.Null(request.Header("authorization"));
    }

    [Fact]
    public async Task ListModels_FollowsPagination()
    {
        var handler = new FakeCloudHandler();
        handler.On("GET v1/models", request => request.Query.Contains("after_id=claude-sonnet-5-5", StringComparison.Ordinal)
            ? FakeCloudHandler.Respond(HttpStatusCode.OK, FakeCloudHandler.Fixture("Anthropic", "models-page2.json"))
            : FakeCloudHandler.Respond(HttpStatusCode.OK, FakeCloudHandler.Fixture("Anthropic", "models-page1.json")));
        using var provider = Create(handler, SecretsWithKey());

        var models = await provider.ListModelsAsync(CancellationToken.None);

        Assert.Equal(["claude-sonnet-5-5", "claude-haiku-5-5"], models.Select(m => m.ModelId));
        Assert.Equal(2, handler.Requests.Count);
    }

    // Chat.
    [Fact]
    public async Task Chat_StreamsTextInPieces_WithUsage_AndNamesTheModel()
    {
        var handler = Defaults();
        using var provider = Create(handler, SecretsWithKey());

        var updates = await ChatAsync(provider);

        Assert.Equal(["Hello", " from", " Pillion"], updates.Where(u => !string.IsNullOrEmpty(u.Text)).Select(u => u.Text));
        Assert.All(updates, u => Assert.Equal("claude-sonnet-5-5", u.ModelId));
        var usage = Assert.Single(updates.SelectMany(u => u.Contents).OfType<UsageContent>()).Details;
        Assert.Equal(30, usage.InputTokenCount); // 25 fresh + 5 read from the cache.
        Assert.Equal(15, usage.OutputTokenCount);
        Assert.Equal(45, usage.TotalTokenCount);
        Assert.Equal(ChatFinishReason.Stop, updates[^1].FinishReason);
    }

    [Fact]
    public async Task Chat_SendsTheDocumentedRequest()
    {
        var handler = Defaults();
        using var provider = Create(handler, SecretsWithKey());
        AiChatMessage[] messages =
        [
            new(AiChatRole.System, "You are terse."),
            new(AiChatRole.System, "Answer in English."),
            new(AiChatRole.User, "Say hello"),
            new(AiChatRole.Assistant, "Hello."),
            new(AiChatRole.User, "Again"),
        ];

        await ChatAsync(provider, messages: messages, options: new ChatOptions { StopSequences = ["END"], Temperature = 0.2f });

        var request = Assert.Single(handler.RequestsTo("v1/messages"));
        Assert.Equal("POST", request.Method);
        Assert.Equal(Key, request.Header("x-api-key"));
        Assert.Equal("2023-06-01", request.Header("anthropic-version"));
        Assert.Null(request.Header("authorization"));
        var body = request.Body!;
        Assert.Equal("claude-sonnet-5-5", body["model"]!.GetValue<string>());
        Assert.True(body["stream"]!.GetValue<bool>());
        Assert.Equal("You are terse.\n\nAnswer in English.", body["system"]!.GetValue<string>());
        Assert.Equal(8192, body["max_tokens"]!.GetValue<int>());
        Assert.Equal(["END"], body["stop_sequences"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal(["user", "assistant", "user"], body["messages"]!.AsArray().Select(m => m!["role"]!.GetValue<string>()));
        Assert.Equal("Again", body["messages"]![2]!["content"]!.GetValue<string>());

        // Sampling parameters are never sent: Claude Opus 4.7 and later reject non-default values.
        Assert.False(body.ContainsKey("temperature"));
        Assert.False(body.ContainsKey("top_p"));
        Assert.False(body.ContainsKey("stream_options"));
        Assert.False(body.ContainsKey("output_config"));
    }

    [Fact]
    public async Task Chat_MaxTokens_IsTheRequestsLimit_OrAFlooredDefault()
    {
        var handler = Defaults();
        using var provider = Create(handler, SecretsWithKey());

        await ChatAsync(provider, options: new ChatOptions { MaxOutputTokens = 321 });
        await ChatAsync(provider, model: "claude-3-legacy"); // The catalog's generic claude family allows 32,768; the default stays 8,192.

        Assert.Equal([321, 8192], handler.RequestsTo("v1/messages").Select(r => r.Body!["max_tokens"]!.GetValue<int>()));
    }

    [Theory]
    [InlineData(ReasoningEffort.Low, "low")]
    [InlineData(ReasoningEffort.Medium, "medium")]
    [InlineData(ReasoningEffort.High, "high")]
    [InlineData(ReasoningEffort.ExtraHigh, "xhigh")]
    [InlineData(ReasoningEffort.None, "low")]
    public async Task Chat_ReasoningEffort_IsMappedToOutputConfigEffort_WhereTheCatalogSaysTheModelSupportsIt(ReasoningEffort effort, string expected)
    {
        var handler = Defaults();
        using var provider = Create(handler, SecretsWithKey());

        await ChatAsync(provider, options: new ChatOptions { Reasoning = new ReasoningOptions { Effort = effort } });

        var request = Assert.Single(handler.RequestsTo("v1/messages"));
        Assert.Equal(expected, request.Body!["output_config"]!["effort"]!.GetValue<string>());
        Assert.False(request.Body.ContainsKey("thinking"));
    }

    [Fact]
    public async Task Chat_ReasoningEffort_IsNotSent_ForAModelWithoutTheControl_OrWhenUnset()
    {
        var handler = Defaults();
        using var provider = Create(handler, SecretsWithKey());

        await ChatAsync(provider, model: "claude-3-legacy", options: new ChatOptions { Reasoning = new ReasoningOptions { Effort = ReasoningEffort.High } });
        await ChatAsync(provider);

        Assert.All(handler.RequestsTo("v1/messages"), r => Assert.False(r.Body!.ContainsKey("output_config")));
    }

    [Fact]
    public async Task Chat_ThinkingBlocks_AreNotStreamed_AndTheStopReasonMapsToLength()
    {
        var handler = Defaults().Serve("POST v1/messages", "Anthropic", "chat-stream-thinking.sse");
        using var provider = Create(handler, SecretsWithKey());

        var updates = await ChatAsync(provider, "claude-opus-5-5");

        Assert.Equal("Hi there", TextOf(updates));
        Assert.DoesNotContain("greeting", TextOf(updates), StringComparison.Ordinal);
        Assert.Equal(ChatFinishReason.Length, updates[^1].FinishReason);
        var usage = Assert.Single(updates.SelectMany(u => u.Contents).OfType<UsageContent>()).Details;
        Assert.Equal(40, usage.InputTokenCount);
        Assert.Equal(64, usage.OutputTokenCount);
    }

    [Fact]
    public async Task Chat_SendsImagesAsBase64Blocks()
    {
        var handler = Defaults();
        using var provider = Create(handler, SecretsWithKey());
        var message = new AiChatMessage(AiChatRole.User, [new TextContent("What is this?"), new DataContent(new byte[] { 1, 2, 3 }, "image/png")]);

        await ChatAsync(provider, messages: [message]);

        var content = handler.RequestsTo("v1/messages").Single().Body!["messages"]![0]!["content"]!.AsArray();
        Assert.Equal("image", content[0]!["type"]!.GetValue<string>());
        Assert.Equal("image/png", content[0]!["source"]!["media_type"]!.GetValue<string>());
        Assert.Equal("AQID", content[0]!["source"]!["data"]!.GetValue<string>());
        Assert.Equal("What is this?", content[1]!["text"]!.GetValue<string>());
    }

    [Fact]
    public async Task Chat_AnExplicitModelOption_WinsOverTheClientsModel()
    {
        var handler = Defaults();
        using var provider = Create(handler, SecretsWithKey());

        var updates = await ChatAsync(provider, "claude-sonnet-5-5", new ChatOptions { ModelId = "claude-haiku-5-5" });

        Assert.All(updates, u => Assert.Equal("claude-haiku-5-5", u.ModelId));
        Assert.Equal("claude-haiku-5-5", handler.Requests.Single().Body!["model"]!.GetValue<string>());
    }

    [Fact]
    public async Task Chat_ReadsTheKeyForEveryRequest_AndNeverKeepsIt()
    {
        var handler = Defaults();
        var secrets = SecretsWithKey();
        using var provider = Create(handler, secrets);

        await ChatAsync(provider);
        secrets.Set("claude", "rotated-key-0002");
        await ChatAsync(provider);

        Assert.Equal([Key, "rotated-key-0002"], handler.Requests.Select(r => r.Header("x-api-key")));
    }

    [Fact]
    public async Task Chat_AKeyWithControlCharacters_IsRefusedBeforeAnythingIsSent()
    {
        var handler = Defaults();
        var secrets = new InMemorySecretStore();
        secrets.Set("claude", "bad\r\nkey");
        using var provider = Create(handler, secrets);

        var ex = await Assert.ThrowsAsync<ProviderException>(() => ChatAsync(provider));

        Assert.Contains("control characters", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("bad", ex.Message, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Chat_Cancelled_MidStream_StopsTheEnumeration()
    {
        var handler = Defaults().On("POST v1/messages", _ => Stalling.Sse(
            "event: message_start\ndata: {\"type\":\"message_start\",\"message\":{\"usage\":{\"input_tokens\":3,\"output_tokens\":1}}}\n\n" +
            "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"Hel\"}}\n\n"));
        using var provider = Create(handler, SecretsWithKey());
        using var cts = new CancellationTokenSource();
        var seen = new List<string>();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var update in provider.GetChatClient("claude-sonnet-5-5").GetStreamingResponseAsync(Hello, null, cts.Token))
            {
                seen.Add(update.Text);
                await cts.CancelAsync();
            }
        });

        Assert.Equal(["Hel"], seen);
    }

    // Errors.
    [Fact]
    public async Task Error401_IsProviderUnavailable_AndDoesNotRepeatTheServersText()
    {
        var handler = Defaults().Fail("POST v1/messages", HttpStatusCode.Unauthorized, "Anthropic", "error-401.json");
        using var provider = Create(handler, SecretsWithKey());

        var ex = await Assert.ThrowsAsync<ProviderException>(() => ChatAsync(provider));

        Assert.Equal(PillionErrorCode.ProviderUnavailable, ex.Code);
        Assert.Equal(HttpStatusCode.Unauthorized, ex.StatusCode);
        Assert.False(ex.IsRetryable);
        Assert.False(ex.IsProviderFault);
        Assert.Contains("rejected the API key", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-ant", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Key, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Error404_NamingTheModel_IsModelNotInstalled()
    {
        var handler = Defaults().Fail("POST v1/messages", HttpStatusCode.NotFound, "Anthropic", "error-404-model.json");
        using var provider = Create(handler, SecretsWithKey());

        var ex = await Assert.ThrowsAsync<ProviderException>(() => ChatAsync(provider, "claude-imaginary-9"));

        Assert.Equal(PillionErrorCode.ModelNotInstalled, ex.Code);
        Assert.Contains("claude-imaginary-9", ex.Message, StringComparison.Ordinal);
        Assert.False(ex.IsProviderFault);
    }

    [Fact]
    public async Task Error400_AboutThePromptLength_IsContextOverflow()
    {
        var handler = Defaults().Fail("POST v1/messages", HttpStatusCode.BadRequest, "Anthropic", "error-context-length.json");
        using var provider = Create(handler, SecretsWithKey());

        var ex = await Assert.ThrowsAsync<ProviderException>(() => ChatAsync(provider));

        Assert.Equal(PillionErrorCode.ContextOverflow, ex.Code);
        Assert.False(ex.IsRetryable);
    }

    [Fact]
    public async Task Error400_AboutCredit_IsABillingMessage_NotARetry()
    {
        var handler = Defaults().Fail("POST v1/messages", HttpStatusCode.BadRequest, "Anthropic", "error-credit-balance.json");
        using var provider = Create(handler, SecretsWithKey());

        var ex = await Assert.ThrowsAsync<ProviderException>(() => ChatAsync(provider));

        Assert.Equal(PillionErrorCode.ProviderUnavailable, ex.Code);
        Assert.Contains("billing", ex.Message, StringComparison.Ordinal);
        Assert.False(ex.IsRetryable);
    }

    [Theory]
    [InlineData(429, "error-429.json")]
    [InlineData(529, "error-529.json")]
    public async Task Error429And529_AreTransient(int status, string fixture)
    {
        var handler = Defaults().Fail("POST v1/messages", (HttpStatusCode)status, "Anthropic", fixture);
        using var provider = Create(handler, SecretsWithKey());

        var ex = await Assert.ThrowsAsync<ProviderException>(() => ChatAsync(provider));

        Assert.Equal(PillionErrorCode.ProviderUnavailable, ex.Code);
        Assert.Equal((HttpStatusCode)status, ex.StatusCode);
        Assert.True(ex.IsRetryable);
        Assert.True(ex.IsProviderFault);
    }

    [Fact]
    public async Task ConnectionRefused_IsProviderUnavailable()
    {
        var handler = Defaults();
        handler.RefuseConnections = true;
        using var provider = Create(handler, SecretsWithKey());

        var ex = await Assert.ThrowsAsync<ProviderException>(() => ChatAsync(provider));

        Assert.Equal(PillionErrorCode.ProviderUnavailable, ex.Code);
        Assert.Contains("connection refused", ex.Message, StringComparison.Ordinal);
        Assert.True(ex.IsProviderFault);
        await Assert.ThrowsAsync<ProviderException>(() => provider.ListModelsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task AnErrorEventMidStream_AfterSomeText_IsATransientFailure()
    {
        var handler = Defaults().Serve("POST v1/messages", "Anthropic", "chat-error-midstream.sse");
        using var provider = Create(handler, SecretsWithKey());
        var seen = new List<string>();

        var ex = await Assert.ThrowsAsync<ProviderException>(async () =>
        {
            await foreach (var update in provider.GetChatClient("claude-sonnet-5-5").GetStreamingResponseAsync(Hello))
            {
                seen.Add(update.Text);
            }
        });

        Assert.Equal(["Hel"], seen);
        Assert.Equal((HttpStatusCode)529, ex.StatusCode);
        Assert.True(ex.IsRetryable);
    }

    // Test connection.
    [Fact]
    public async Task Probe_WithAnAcceptedKey_ReportsReachableAndAuthenticated()
    {
        var handler = Defaults();
        using var provider = Create(handler, SecretsWithKey());

        var detailed = await provider.ProbeDetailedAsync(CancellationToken.None);
        var probe = await provider.ProbeAsync(CancellationToken.None);

        Assert.True(detailed.IsReachable);
        Assert.True(detailed.IsAuthenticated);
        Assert.Equal(3, detailed.ModelCount);
        Assert.Contains("accepted", detailed.Message, StringComparison.Ordinal);
        Assert.True(probe.IsReachable);
        Assert.Equal(Key, handler.Requests.First().Header("x-api-key"));
    }

    [Fact]
    public async Task Probe_WithARejectedKey_IsReachableButNotUsable()
    {
        var handler = Defaults().Fail("GET v1/models", HttpStatusCode.Unauthorized, "Anthropic", "error-401.json");
        using var provider = Create(handler, SecretsWithKey());

        var detailed = await provider.ProbeDetailedAsync(CancellationToken.None);
        var probe = await provider.ProbeAsync(CancellationToken.None);

        Assert.True(detailed.IsReachable);
        Assert.False(detailed.IsAuthenticated);
        Assert.Contains("rejected", detailed.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-ant", detailed.Message, StringComparison.Ordinal);
        Assert.False(probe.IsReachable);
    }

    [Fact]
    public async Task Probe_WhenTheServiceIsUnreachable_DoesNotThrow()
    {
        var handler = Defaults();
        handler.RefuseConnections = true;
        using var provider = Create(handler, SecretsWithKey());

        var probe = await provider.ProbeAsync(CancellationToken.None);

        Assert.False(probe.IsReachable);
        Assert.Contains("refused", probe.Message, StringComparison.Ordinal);
    }
}
