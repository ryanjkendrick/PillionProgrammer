using System.Net;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Pillion.Common.Secrets;
using Pillion.Common.Settings;
using Pillion.Contracts;
using Pillion.ModelGateway.Providers.Gemini;
using Pillion.ModelGateway.Tests.Providers.Cloud;
using Pillion.ModelGateway.Tests.TestSupport;
using AiChatMessage = Microsoft.Extensions.AI.ChatMessage;
using AiChatRole = Microsoft.Extensions.AI.ChatRole;

namespace Pillion.ModelGateway.Tests.Providers.Gemini;

/// <summary>The Gemini adapter against fixtures in the documented Gemini API wire shape.</summary>
public sealed class GeminiProviderTests
{
    // Fabricated.
    internal const string Key = "unit-test-key-0001";

    internal const string StreamRoute = "POST v1beta/models/gemini-3.8-flash:streamGenerateContent";

    private static readonly AiChatMessage[] Hello = [new AiChatMessage(AiChatRole.User, "Say hello")];

    internal static FakeCloudHandler Defaults() => new FakeCloudHandler()
        .Serve("GET v1beta/models", "Gemini", "models.json")
        .Serve(StreamRoute, "Gemini", "chat-stream.sse");

    internal static ProviderSettings Settings(string id, string? extensionJson = null, string endpoint = "")
    {
        var settings = new ProviderSettings { Id = id, Kind = ProviderKind.Gemini, Endpoint = endpoint };
        if (extensionJson is not null)
        {
            settings.ExtensionData = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(extensionJson);
        }

        return settings;
    }

    internal static GeminiProvider Create(FakeCloudHandler handler, InMemorySecretStore secrets, string? extensionJson = null, string endpoint = "") =>
        (GeminiProvider)new GeminiProviderFactory(secrets, () => handler).Create(
            Settings("gem", extensionJson, endpoint),
            new ProviderCreationContext(null, ModelCatalog.Default, NullLoggerFactory.Instance, null));

    private static InMemorySecretStore SecretsWithKey()
    {
        var secrets = new InMemorySecretStore();
        secrets.Set("gem", Key);
        return secrets;
    }

    private static Task<List<ChatResponseUpdate>> ChatAsync(GeminiProvider provider, string model = "gemini-3.8-flash", ChatOptions? options = null, IEnumerable<AiChatMessage>? messages = null, CancellationToken cancellationToken = default) =>
        provider.GetChatClient(model).GetStreamingResponseAsync(messages ?? Hello, options, cancellationToken).CollectAsync(cancellationToken);

    private static string TextOf(IEnumerable<ChatResponseUpdate> updates) => string.Concat(updates.Select(u => u.Text));

    // Creation.
    [Fact]
    public void TheProvider_IsCloud_NeedsAKey_AndHasNoFim()
    {
        using var provider = Create(Defaults(), SecretsWithKey());
        var factory = new GeminiProviderFactory(new InMemorySecretStore());

        Assert.False(provider.IsLocal);
        Assert.Equal(ProviderKind.Gemini, provider.Kind);
        Assert.Equal("Google Gemini", provider.DisplayName);
        Assert.True(factory.RequiresApiKey(Settings("gem")));
        Assert.Equal(FeatureNames.GeminiProvider, factory.FeatureFlag);
        Assert.Null(provider.GetFimClient("gemini-3.8-flash"));
        Assert.Equal("https://generativelanguage.googleapis.com/", provider.Endpoint.AbsoluteUri);
        Assert.Equal("v1beta", provider.ApiVersion);
    }

    // Models.
    [Fact]
    public async Task ListModels_StripsTheModelsPrefix_AndReadsLimitsAndMethods()
    {
        var handler = Defaults();
        using var provider = Create(handler, SecretsWithKey());

        var models = await provider.ListModelsAsync(CancellationToken.None);

        Assert.Equal(["gemini-3.8-flash", "gemini-3.5-flash-lite", "gemini-embedding-001"], models.Select(m => m.ModelId));
        Assert.All(models, m => Assert.Equal("gem", m.ProviderId));
        var flash = models[0];
        Assert.Equal("Gemini 3.8 Flash", flash.DisplayName);
        Assert.Equal(1_048_576, flash.Capabilities.ContextWindow);
        Assert.Equal(65_536, flash.Capabilities.MaxOutputTokens);
        Assert.True(flash.Capabilities.Chat);
        Assert.True(flash.Capabilities.Vision);
        Assert.True(flash.Capabilities.ReasoningEffort);
        Assert.False(flash.Capabilities.Embeddings);
        Assert.False(flash.Capabilities.Fim);
        Assert.Equal(1.5m, flash.Capabilities.InputCostPerMillionTokens);

        var embedding = models[2];
        Assert.True(embedding.Capabilities.Embeddings);
        Assert.False(embedding.Capabilities.Chat);
        Assert.Equal(2048, embedding.Capabilities.ContextWindow);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(Key, request.Header("x-goog-api-key"));
        Assert.Null(request.Header("authorization"));
        Assert.False(request.Query.Contains(Key, StringComparison.Ordinal), "The key must travel in a header, never in the URL.");
    }

    [Fact]
    public async Task ListModels_FollowsNextPageToken()
    {
        var handler = new FakeCloudHandler();
        handler.On("GET v1beta/models", request => request.Query.Contains("pageToken=page2token%3D%3D", StringComparison.Ordinal)
            ? FakeCloudHandler.Respond(HttpStatusCode.OK, FakeCloudHandler.Fixture("Gemini", "models-page2.json"))
            : FakeCloudHandler.Respond(HttpStatusCode.OK, FakeCloudHandler.Fixture("Gemini", "models-page1.json")));
        using var provider = Create(handler, SecretsWithKey());

        var models = await provider.ListModelsAsync(CancellationToken.None);

        Assert.Equal(["gemini-3.8-flash", "gemini-3.5-flash-lite"], models.Select(m => m.ModelId));
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
        Assert.All(updates, u => Assert.Equal("gemini-3.8-flash", u.ModelId));
        var usage = Assert.Single(updates.SelectMany(u => u.Contents).OfType<UsageContent>()).Details;
        Assert.Equal(21, usage.InputTokenCount);
        Assert.Equal(3, usage.OutputTokenCount);
        Assert.Equal(24, usage.TotalTokenCount);
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
            new(AiChatRole.User, "Say hello"),
            new(AiChatRole.Assistant, "Hello."),
            new(AiChatRole.User, "Again"),
        ];

        await ChatAsync(provider, messages: messages, options: new ChatOptions { MaxOutputTokens = 300, Temperature = 0.2f, StopSequences = ["END", "STOP"] });

        var request = Assert.Single(handler.RequestsTo("v1beta/models/gemini-3.8-flash:streamGenerateContent"));
        Assert.Equal("POST", request.Method);
        Assert.Equal("alt=sse", request.Query);
        Assert.Equal(Key, request.Header("x-goog-api-key"));
        var body = request.Body!;
        Assert.Equal("You are terse.", body["systemInstruction"]!["parts"]![0]!["text"]!.GetValue<string>());
        Assert.Equal(["user", "model", "user"], body["contents"]!.AsArray().Select(c => c!["role"]!.GetValue<string>()));
        Assert.Equal("Again", body["contents"]![2]!["parts"]![0]!["text"]!.GetValue<string>());
        var generation = body["generationConfig"]!;
        Assert.Equal(300, generation["maxOutputTokens"]!.GetValue<int>());
        Assert.Equal(0.2f, generation["temperature"]!.GetValue<float>());
        Assert.Equal(["END", "STOP"], generation["stopSequences"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.False(generation.AsObject().ContainsKey("thinkingConfig"));
        Assert.False(body.ContainsKey("model"));
    }

    [Theory]
    [InlineData(ReasoningEffort.Low, "low")]
    [InlineData(ReasoningEffort.None, "low")]
    [InlineData(ReasoningEffort.Medium, "medium")]
    [InlineData(ReasoningEffort.High, "high")]
    [InlineData(ReasoningEffort.ExtraHigh, "high")]
    public async Task Chat_ReasoningEffort_IsMappedToThinkingLevel_WhereTheCatalogSaysTheModelSupportsIt(ReasoningEffort effort, string expected)
    {
        var handler = Defaults();
        using var provider = Create(handler, SecretsWithKey());

        await ChatAsync(provider, options: new ChatOptions { Reasoning = new ReasoningOptions { Effort = effort } });

        var body = Assert.Single(handler.RequestsTo("v1beta/models/gemini-3.8-flash:streamGenerateContent")).Body!;
        Assert.Equal(expected, body["generationConfig"]!["thinkingConfig"]!["thinkingLevel"]!.GetValue<string>());
    }

    [Fact]
    public async Task Chat_ReasoningEffort_IsNotSent_ForAModelWithoutTheControl()
    {
        var handler = Defaults().Serve("POST v1beta/models/gemini-2.5-pro:streamGenerateContent", "Gemini", "chat-stream.sse");
        using var provider = Create(handler, SecretsWithKey());

        await ChatAsync(provider, "gemini-2.5-pro", new ChatOptions { Reasoning = new ReasoningOptions { Effort = ReasoningEffort.High } });

        Assert.Null(handler.Requests.Single().Body!["generationConfig"]);
    }

    [Fact]
    public async Task Chat_AModelNameWithThePrefix_IsAccepted()
    {
        var handler = Defaults();
        using var provider = Create(handler, SecretsWithKey());

        var updates = await ChatAsync(provider, "models/gemini-3.8-flash");

        Assert.All(updates, u => Assert.Equal("gemini-3.8-flash", u.ModelId));
        Assert.Single(handler.RequestsTo("v1beta/models/gemini-3.8-flash:streamGenerateContent"));
    }

    [Fact]
    public async Task Chat_ThoughtParts_AreNotStreamed_AndThinkingTokensCountAsOutput()
    {
        var handler = Defaults().Serve(StreamRoute, "Gemini", "chat-stream-thinking.sse");
        using var provider = Create(handler, SecretsWithKey());

        var updates = await ChatAsync(provider);

        Assert.Equal("Hi there", TextOf(updates));
        Assert.Equal(ChatFinishReason.Length, updates[^1].FinishReason);
        var usage = Assert.Single(updates.SelectMany(u => u.Contents).OfType<UsageContent>()).Details;
        Assert.Equal(30, usage.InputTokenCount);
        Assert.Equal(28, usage.OutputTokenCount); // 4 visible + 24 thinking.
        Assert.Equal(58, usage.TotalTokenCount);
    }

    [Fact]
    public async Task Chat_ABlockedPrompt_EndsWithAContentFilterFinish()
    {
        var handler = Defaults().Serve(StreamRoute, "Gemini", "chat-blocked.sse");
        using var provider = Create(handler, SecretsWithKey());

        var updates = await ChatAsync(provider);

        Assert.Equal(string.Empty, TextOf(updates));
        Assert.Equal(ChatFinishReason.ContentFilter, updates[^1].FinishReason);
    }

    [Fact]
    public async Task Chat_SendsImagesAsInlineData()
    {
        var handler = Defaults();
        using var provider = Create(handler, SecretsWithKey());
        var message = new AiChatMessage(AiChatRole.User, [new TextContent("What is this?"), new DataContent(new byte[] { 1, 2, 3 }, "image/png")]);

        await ChatAsync(provider, messages: [message]);

        var parts = handler.Requests.Single().Body!["contents"]![0]!["parts"]!.AsArray();
        Assert.Equal("What is this?", parts[0]!["text"]!.GetValue<string>());
        Assert.Equal("image/png", parts[1]!["inlineData"]!["mimeType"]!.GetValue<string>());
        Assert.Equal("AQID", parts[1]!["inlineData"]!["data"]!.GetValue<string>());
    }

    [Fact]
    public async Task Chat_Cancelled_MidStream_StopsTheEnumeration()
    {
        var handler = Defaults().On(StreamRoute, _ => Stalling.Sse(
            "data: {\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"Hel\"}],\"role\":\"model\"},\"index\":0}]}\n\n"));
        using var provider = Create(handler, SecretsWithKey());
        using var cts = new CancellationTokenSource();
        var seen = new List<string>();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var update in provider.GetChatClient("gemini-3.8-flash").GetStreamingResponseAsync(Hello, null, cts.Token))
            {
                seen.Add(update.Text);
                await cts.CancelAsync();
            }
        });

        Assert.Equal(["Hel"], seen);
    }

    // Errors.
    [Fact]
    public async Task InvalidApiKey_Reported_As400_IsAKeyRejection_NotARetry()
    {
        var handler = Defaults().Fail(StreamRoute, HttpStatusCode.BadRequest, "Gemini", "error-api-key-invalid.json");
        using var provider = Create(handler, SecretsWithKey());

        var ex = await Assert.ThrowsAsync<ProviderException>(() => ChatAsync(provider));

        Assert.Equal(PillionErrorCode.ProviderUnavailable, ex.Code);
        Assert.Contains("rejected the API key", ex.Message, StringComparison.Ordinal);
        Assert.False(ex.IsRetryable);
        Assert.False(ex.IsProviderFault);
    }

    [Fact]
    public async Task Error403_IsAKeyRejection()
    {
        var handler = Defaults().Fail(StreamRoute, HttpStatusCode.Forbidden, "Gemini", "error-403.json");
        using var provider = Create(handler, SecretsWithKey());

        var ex = await Assert.ThrowsAsync<ProviderException>(() => ChatAsync(provider));

        Assert.Equal(PillionErrorCode.ProviderUnavailable, ex.Code);
        Assert.Contains("rejected the API key", ex.Message, StringComparison.Ordinal);
        Assert.False(ex.IsProviderFault);
    }

    [Fact]
    public async Task Error404_NamingTheModel_IsModelNotInstalled()
    {
        var handler = Defaults().Fail("POST v1beta/models/gemini-imaginary-9:streamGenerateContent", HttpStatusCode.NotFound, "Gemini", "error-404-model.json");
        using var provider = Create(handler, SecretsWithKey());

        var ex = await Assert.ThrowsAsync<ProviderException>(() => ChatAsync(provider, "gemini-imaginary-9"));

        Assert.Equal(PillionErrorCode.ModelNotInstalled, ex.Code);
        Assert.False(ex.IsProviderFault);
    }

    [Fact]
    public async Task Error400_AboutTheInputTokenCount_IsContextOverflow()
    {
        var handler = Defaults().Fail(StreamRoute, HttpStatusCode.BadRequest, "Gemini", "error-context-length.json");
        using var provider = Create(handler, SecretsWithKey());

        var ex = await Assert.ThrowsAsync<ProviderException>(() => ChatAsync(provider));

        Assert.Equal(PillionErrorCode.ContextOverflow, ex.Code);
        Assert.False(ex.IsRetryable);
    }

    [Theory]
    [InlineData(429, "error-429.json")]
    [InlineData(503, "error-503.json")]
    public async Task Error429And503_AreTransient(int status, string fixture)
    {
        var handler = Defaults().Fail(StreamRoute, (HttpStatusCode)status, "Gemini", fixture);
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
    }

    [Fact]
    public async Task AnErrorMidStream_AfterSomeText_IsATransientFailure()
    {
        var handler = Defaults().Serve(StreamRoute, "Gemini", "chat-error-midstream.sse");
        using var provider = Create(handler, SecretsWithKey());
        var seen = new List<string>();

        var ex = await Assert.ThrowsAsync<ProviderException>(async () =>
        {
            await foreach (var update in provider.GetChatClient("gemini-3.8-flash").GetStreamingResponseAsync(Hello))
            {
                seen.Add(update.Text);
            }
        });

        Assert.Equal(["Hel"], seen);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, ex.StatusCode);
        Assert.True(ex.IsRetryable);
    }

    // Test connection.
    [Fact]
    public async Task Probe_WithAnAcceptedKey_ReportsReachableAndAuthenticated()
    {
        using var provider = Create(Defaults(), SecretsWithKey());

        var detailed = await provider.ProbeDetailedAsync(CancellationToken.None);

        Assert.True(detailed.IsReachable);
        Assert.True(detailed.IsAuthenticated);
        Assert.Equal(3, detailed.ModelCount);
        Assert.True((await provider.ProbeAsync(CancellationToken.None)).IsReachable);
    }

    [Fact]
    public async Task Probe_WithAnInvalidKey_ReportsAKeyRejection()
    {
        var handler = Defaults().Fail("GET v1beta/models", HttpStatusCode.BadRequest, "Gemini", "error-api-key-invalid.json");
        using var provider = Create(handler, SecretsWithKey());

        var detailed = await provider.ProbeDetailedAsync(CancellationToken.None);
        var probe = await provider.ProbeAsync(CancellationToken.None);

        Assert.True(detailed.IsReachable);
        Assert.False(detailed.IsAuthenticated);
        Assert.Contains("invalid API key", detailed.Message, StringComparison.Ordinal);
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
    }
}
