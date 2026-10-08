using System.Net;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Pillion.Common.Secrets;
using Pillion.Common.Settings;
using Pillion.Contracts;
using Pillion.ModelGateway.Providers.OpenAICompatible;
using Pillion.ModelGateway.Tests.TestSupport;
using AiChatMessage = Microsoft.Extensions.AI.ChatMessage;
using AiChatRole = Microsoft.Extensions.AI.ChatRole;

namespace Pillion.ModelGateway.Tests.Providers.OpenAICompatible;

public class OpenAICompatibleProviderTests
{
    // Fabricated; the 401 fixture echoes a masked copy of it, as OpenAI does.
    internal const string Key = "unit-test-key-0001";

    private const string CloudEndpoint = "https://gateway.example/v1";

    private static readonly AiChatMessage[] Hello = [new AiChatMessage(AiChatRole.User, "Say hello")];

    internal static OpenAICompatibleProvider Create(
        FakeOpenAIHandler handler,
        InMemorySecretStore secrets,
        string endpoint = CloudEndpoint,
        ProviderKind kind = ProviderKind.OpenAiCompatible,
        string? extensionJson = null,
        params string[] models)
    {
        var settings = new ProviderSettings { Id = "gw", Kind = kind, Endpoint = endpoint, Models = [.. models] };
        if (extensionJson is not null)
        {
            settings.ExtensionData = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(extensionJson);
        }

        IModelProviderFactory factory = kind == ProviderKind.OpenAi
            ? new OpenAIProviderFactory(secrets, _ => handler)
            : new OpenAICompatibleProviderFactory(secrets, _ => handler);
        return (OpenAICompatibleProvider)factory.Create(settings, new ProviderCreationContext(null, ModelCatalog.Default, Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance, null));
    }

    private static InMemorySecretStore SecretsWithKey()
    {
        var secrets = new InMemorySecretStore();
        secrets.Set("gw", Key);
        return secrets;
    }

    private static Task<List<ChatResponseUpdate>> ChatAsync(OpenAICompatibleProvider provider, string model = "gpt-6.1-sol", ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        provider.GetChatClient(model).GetStreamingResponseAsync(Hello, options, cancellationToken).CollectAsync(cancellationToken);

    // Creation and endpoints.
    [Fact]
    public void Loopback_IsLocal_AndNeedsNoKey()
    {
        using var provider = Create(FakeOpenAIHandler.WithDefaults(), new InMemorySecretStore(), "http://localhost:1234/v1");
        var factory = new OpenAICompatibleProviderFactory(new InMemorySecretStore());

        Assert.True(provider.IsLocal);
        Assert.Equal(ProviderKind.OpenAiCompatible, provider.Kind);
        Assert.False(factory.RequiresApiKey(new ProviderSettings { Kind = ProviderKind.OpenAiCompatible, Endpoint = "http://localhost:1234/v1" }));
        Assert.False(factory.RequiresApiKey(new ProviderSettings { Kind = ProviderKind.OpenAiCompatible, Endpoint = "http://127.0.0.1:8080" }));
        Assert.False(factory.RequiresApiKey(new ProviderSettings { Kind = ProviderKind.OpenAiCompatible, Endpoint = "http://[::1]:8080/v1" }));
    }

    [Fact]
    public void RemoteHost_IsCloud_AndNeedsAKey()
    {
        using var provider = Create(FakeOpenAIHandler.WithDefaults(), new InMemorySecretStore(), "http://gpu-box.lan:8000/v1");
        var factory = new OpenAICompatibleProviderFactory(new InMemorySecretStore());

        Assert.False(provider.IsLocal);
        Assert.True(factory.RequiresApiKey(new ProviderSettings { Kind = ProviderKind.OpenAiCompatible, Endpoint = CloudEndpoint }));
    }

    [Fact]
    public void OpenAiKind_DefaultsToTheOpenAiEndpoint_AndNeedsAKey()
    {
        using var provider = Create(FakeOpenAIHandler.WithDefaults(), new InMemorySecretStore(), endpoint: string.Empty, kind: ProviderKind.OpenAi);
        var factory = new OpenAIProviderFactory(new InMemorySecretStore());

        Assert.Equal(new Uri("https://api.openai.com/v1/"), provider.Options.Endpoint);
        Assert.Equal(ProviderKind.OpenAi, provider.Kind);
        Assert.Equal("OpenAI", provider.DisplayName);
        Assert.False(provider.IsLocal);
        Assert.Equal(ProviderKind.OpenAi, factory.Kind);
        Assert.True(factory.RequiresApiKey(new ProviderSettings { Kind = ProviderKind.OpenAi }));
    }

    [Theory]
    [InlineData("http://localhost:1234", "http://localhost:1234/v1/")]
    [InlineData("http://localhost:1234/v1", "http://localhost:1234/v1/")]
    [InlineData("https://openrouter.ai/api/v1/", "https://openrouter.ai/api/v1/")]
    [InlineData("https://gw.example/openai/v1?ignored=1", "https://gw.example/openai/v1/")]
    public void Endpoint_IsNormalisedToABaseWithTheVersionSegment(string configured, string expected)
    {
        using var provider = Create(FakeOpenAIHandler.WithDefaults(), new InMemorySecretStore(), configured);

        Assert.Equal(new Uri(expected), provider.Options.Endpoint);
    }

    [Fact]
    public void MissingOrInvalidEndpoint_IsRejectedAtCreation()
    {
        var secrets = new InMemorySecretStore();

        Assert.Throws<InvalidOperationException>(() => Create(FakeOpenAIHandler.WithDefaults(), secrets, endpoint: string.Empty));
        Assert.Throws<UriFormatException>(() => Create(FakeOpenAIHandler.WithDefaults(), secrets, "ftp://nope"));
        Assert.Throws<UriFormatException>(() => Create(FakeOpenAIHandler.WithDefaults(), secrets, "not a url"));
        Assert.Throws<UriFormatException>(() => Create(FakeOpenAIHandler.WithDefaults(), secrets, extensionJson: """{ "proxy": "socks5://x" }"""));
    }

    [Fact]
    public void FactoryOfAnotherKind_RefusesTheSettings()
    {
        var factory = new OpenAIProviderFactory(new InMemorySecretStore());

        Assert.Throws<ArgumentException>(() => factory.Create(
            new ProviderSettings { Id = "x", Kind = ProviderKind.OpenAiCompatible, Endpoint = CloudEndpoint },
            new ProviderCreationContext(null, ModelCatalog.Default, Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance, null)));
    }

    // Model listing.
    [Fact]
    public async Task ListModels_ReadsTheModelList_IntoCapabilities()
    {
        var handler = FakeOpenAIHandler.WithDefaults();
        using var provider = Create(handler, SecretsWithKey());

        var models = await provider.ListModelsAsync(CancellationToken.None);

        Assert.Equal(["gpt-6.1-sol", "gpt-6-luna", "text-embedding-3-small", "gpt-4o-mini-tts", "qwen2.5-coder-7b-instruct"], models.Select(m => m.ModelId));
        Assert.All(models, m => Assert.Equal("gw", m.ProviderId));
        Assert.All(models, m => Assert.False(m.Capabilities.IsLocal));

        var sol = models[0];
        Assert.True(sol.Capabilities.Chat);
        Assert.True(sol.Capabilities.Vision);
        Assert.Equal(ToolCallingSupport.Native, sol.Capabilities.ToolCalling);
        Assert.Equal(200000, sol.Capabilities.ContextWindow);
        Assert.Equal("gw/gpt-6.1-sol", sol.ToDescriptor(isAvailable: true).Id);

        var embedding = models.Single(m => m.ModelId == "text-embedding-3-small");
        Assert.True(embedding.Capabilities.Embeddings);
        Assert.False(embedding.Capabilities.Chat);
        Assert.False(models.Single(m => m.ModelId == "gpt-4o-mini-tts").Capabilities.Chat);

        // The server's own context_length wins over the catalog; cloud endpoints never offer FIM.
        var coder = models.Single(m => m.ModelId == "qwen2.5-coder-7b-instruct");
        Assert.Equal(16384, coder.Capabilities.ContextWindow);
        Assert.False(coder.Capabilities.Fim);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("v1/models", request.Path);
        Assert.Equal("gateway.example", request.Host);
        Assert.Equal("Bearer " + Key, request.Authorization);
    }

    [Fact]
    public async Task ListModels_OnALocalServerThatDeclaresFim_MarksOnlyFimFamilies()
    {
        using var declared = Create(FakeOpenAIHandler.WithDefaults(), new InMemorySecretStore(), "http://localhost:1234/v1", extensionJson: """{ "supportsFim": true }""");
        using var plain = Create(FakeOpenAIHandler.WithDefaults(), new InMemorySecretStore(), "http://localhost:1234/v1");

        var withFlag = await declared.ListModelsAsync(CancellationToken.None);
        var withoutFlag = await plain.ListModelsAsync(CancellationToken.None);

        Assert.All(withFlag, m => Assert.True(m.Capabilities.IsLocal));
        Assert.True(withFlag.Single(m => m.ModelId == "qwen2.5-coder-7b-instruct").Capabilities.Fim);
        Assert.False(withFlag.Single(m => m.ModelId == "gpt-6.1-sol").Capabilities.Fim);
        Assert.DoesNotContain(withoutFlag, m => m.Capabilities.Fim);
    }

    [Fact]
    public async Task ListModels_WhenModelsIsNotFound_FallsBackToTheConfiguredModels()
    {
        var handler = new FakeOpenAIHandler();
        using var provider = Create(handler, SecretsWithKey(), CloudEndpoint, ProviderKind.OpenAiCompatible, null, "my-chat-model", "gpt-6-luna");

        var models = await provider.ListModelsAsync(CancellationToken.None);

        Assert.Equal(["my-chat-model", "gpt-6-luna"], models.Select(m => m.ModelId));
        Assert.True(models[1].Capabilities.Chat);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task ListModels_WhenModelsIsNotFoundAndNothingIsConfigured_OffersTheCatalogDefaults()
    {
        using var provider = Create(new FakeOpenAIHandler(), SecretsWithKey(), endpoint: string.Empty, kind: ProviderKind.OpenAi);

        var models = await provider.ListModelsAsync(CancellationToken.None);

        Assert.Equal(["gpt-6-astra", "gpt-6-luna", "gpt-6.1-sol"], models.Select(m => m.ModelId).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task ListModels_WithARejectedKey_Throws()
    {
        var handler = new FakeOpenAIHandler();
        handler.Error("GET v1/models", HttpStatusCode.Unauthorized, FakeOpenAIHandler.Fixture("error-401.json"));
        using var provider = Create(handler, SecretsWithKey());

        var ex = await Assert.ThrowsAsync<ProviderException>(() => provider.ListModelsAsync(CancellationToken.None));

        Assert.Equal(PillionErrorCode.ProviderUnavailable, ex.Code);
        Assert.Equal(HttpStatusCode.Unauthorized, ex.StatusCode);
    }

    // Chat.
    [Fact]
    public async Task Chat_StreamsServerSentEvents_ThroughDone_WithUsage()
    {
        var handler = FakeOpenAIHandler.WithDefaults();
        using var provider = Create(handler, SecretsWithKey());

        var updates = await ChatAsync(provider);

        Assert.Equal("Hello from Pillion", string.Concat(updates.Select(u => u.Text)));
        Assert.DoesNotContain("AFTER DONE", string.Concat(updates.Select(u => u.Text)), StringComparison.Ordinal);
        Assert.All(updates, u => Assert.Equal("gpt-6.1-sol", u.ModelId));
        Assert.Contains(updates, u => u.FinishReason == ChatFinishReason.Stop);
        var usage = updates.SelectMany(u => u.Contents.OfType<UsageContent>()).Single().Details;
        Assert.Equal(21, usage.InputTokenCount);
        Assert.Equal(3, usage.OutputTokenCount);
        Assert.Equal(24, usage.TotalTokenCount);

        var body = handler.RequestsTo("v1/chat/completions").Single().Body!;
        Assert.Equal("gpt-6.1-sol", body["model"]!.GetValue<string>());
        Assert.True(body["stream"]!.GetValue<bool>());
        Assert.True(body["stream_options"]!["include_usage"]!.GetValue<bool>());
        Assert.Equal("user", body["messages"]![0]!["role"]!.GetValue<string>());
        Assert.Equal("Say hello", body["messages"]![0]!["content"]!.GetValue<string>());
    }

    [Fact]
    public async Task Chat_ChatOptions_AreSentAsMaxTokensForCompatibleServers()
    {
        var handler = FakeOpenAIHandler.WithDefaults();
        using var provider = Create(handler, SecretsWithKey());

        await ChatAsync(provider, options: new ChatOptions { Temperature = 0.2f, MaxOutputTokens = 64, StopSequences = ["END"] });

        var body = handler.RequestsTo("v1/chat/completions").Single().Body!;
        Assert.Equal(64, body["max_tokens"]!.GetValue<int>());
        Assert.Null(body["max_completion_tokens"]);
        Assert.Equal(0.2, body["temperature"]!.GetValue<double>(), 3);
        Assert.Equal("END", body["stop"]![0]!.GetValue<string>());
    }

    [Fact]
    public async Task Chat_OnTheOfficialApi_UsesMaxCompletionTokens_AndDropsTemperatureForReasoningModels()
    {
        var handler = FakeOpenAIHandler.WithDefaults();
        using var provider = Create(handler, SecretsWithKey(), endpoint: string.Empty, kind: ProviderKind.OpenAi);
        var options = new ChatOptions { Temperature = 0.2f, MaxOutputTokens = 64, StopSequences = ["END"] };

        await ChatAsync(provider, "gpt-6.1-sol", options);
        await ChatAsync(provider, "gpt-4.1", options);

        var bodies = handler.RequestsTo("v1/chat/completions").Select(r => r.Body!).ToList();
        Assert.Equal(64, bodies[0]["max_completion_tokens"]!.GetValue<int>());
        Assert.Null(bodies[0]["max_tokens"]);
        Assert.Null(bodies[0]["temperature"]);
        Assert.Null(bodies[0]["stop"]);
        Assert.NotNull(bodies[1]["temperature"]);
        Assert.NotNull(bodies[1]["stop"]);
        Assert.All(handler.Requests, r => Assert.Equal("api.openai.com", r.Host));
    }

    [Fact]
    public async Task Chat_AnExplicitModelInTheOptions_OverridesTheClientsModel()
    {
        var handler = FakeOpenAIHandler.WithDefaults();
        using var provider = Create(handler, SecretsWithKey());

        var updates = await ChatAsync(provider, "gpt-6-luna", new ChatOptions { ModelId = "gpt-6.1-sol" });

        Assert.All(updates, u => Assert.Equal("gpt-6.1-sol", u.ModelId));
        Assert.Equal("gpt-6.1-sol", handler.RequestsTo("v1/chat/completions").Single().Body!["model"]!.GetValue<string>());
    }

    [Fact]
    public async Task Chat_ImagesAreSentAsImageUrlParts()
    {
        var handler = FakeOpenAIHandler.WithDefaults();
        using var provider = Create(handler, SecretsWithKey());
        var message = new AiChatMessage(AiChatRole.User, [new TextContent("What is this?"), new DataContent(new byte[] { 1, 2, 3 }, "image/png")]);

        await provider.GetChatClient("gpt-6.1-sol").GetStreamingResponseAsync([message]).CollectAsync();

        var content = handler.RequestsTo("v1/chat/completions").Single().Body!["messages"]![0]!["content"]!.AsArray();
        Assert.Equal("text", content[0]!["type"]!.GetValue<string>());
        Assert.Equal("data:image/png;base64,AQID", content[1]!["image_url"]!["url"]!.GetValue<string>());
    }

    [Fact]
    public async Task Chat_AServerThatIgnoresStreaming_YieldsTheWholeAnswer()
    {
        var handler = new FakeOpenAIHandler();
        handler.Text("POST v1/chat/completions", FakeOpenAIHandler.Fixture("chat-nonstreamed.json"), "application/json");
        using var provider = Create(handler, SecretsWithKey());

        var updates = await ChatAsync(provider);

        Assert.Equal("A whole answer.", string.Concat(updates.Select(u => u.Text)));
        Assert.Contains(updates, u => u.FinishReason == ChatFinishReason.Length);
        Assert.Equal(9, updates.SelectMany(u => u.Contents.OfType<UsageContent>()).Single().Details.InputTokenCount);
    }

    [Fact]
    public async Task Chat_GetResponseAsync_AssemblesTheStreamedAnswer()
    {
        using var provider = Create(FakeOpenAIHandler.WithDefaults(), SecretsWithKey());

        var response = await provider.GetChatClient("gpt-6.1-sol").GetResponseAsync(Hello);

        Assert.Equal("Hello from Pillion", response.Text);
        Assert.Equal(24, response.Usage!.TotalTokenCount);
    }

    [Fact]
    public async Task Chat_CancelledMidStream_StopsWithOperationCanceled()
    {
        var handler = new FakeOpenAIHandler();
        handler.On("POST v1/chat/completions", _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StallingContent("data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"Hel\"}}]}\n\n", "text/event-stream"),
        });
        using var provider = Create(handler, SecretsWithKey());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var seen = new List<string>();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var update in provider.GetChatClient("gpt-6.1-sol").GetStreamingResponseAsync(Hello, null, cts.Token))
            {
                seen.Add(update.Text);
                await cts.CancelAsync();
            }
        });

        Assert.Equal(["Hel"], seen);
    }

    [Fact]
    public async Task Chat_AnErrorEventMidStream_ThrowsAfterTheEarlierText()
    {
        var handler = new FakeOpenAIHandler();
        handler.Text("POST v1/chat/completions", FakeOpenAIHandler.Fixture("chat-error-midstream.sse"), "text/event-stream");
        using var provider = Create(handler, SecretsWithKey());
        var seen = new List<string>();

        var ex = await Assert.ThrowsAsync<ProviderException>(async () =>
        {
            await foreach (var update in provider.GetChatClient("gpt-6.1-sol").GetStreamingResponseAsync(Hello))
            {
                seen.Add(update.Text);
            }
        });

        Assert.Equal(["Partial"], seen);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, ex.StatusCode);
        Assert.True(ex.IsRetryable);
        Assert.Contains("overloaded", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Chat_AServerThatRejectsStreamOptions_IsRetriedOnceWithoutThem_AndRemembered()
    {
        var handler = FakeOpenAIHandler.WithDefaults();
        handler.On("POST v1/chat/completions", request => request.Body!.ContainsKey("stream_options")
            ? FakeOpenAIHandler.Respond(HttpStatusCode.BadRequest, FakeOpenAIHandler.Fixture("error-stream-options.json"))
            : FakeOpenAIHandler.Respond(HttpStatusCode.OK, FakeOpenAIHandler.Fixture("chat-stream.sse"), "text/event-stream"));
        using var provider = Create(handler, SecretsWithKey());

        var first = await ChatAsync(provider);
        var second = await ChatAsync(provider);

        Assert.Equal("Hello from Pillion", string.Concat(first.Select(u => u.Text)));
        Assert.Equal("Hello from Pillion", string.Concat(second.Select(u => u.Text)));
        var bodies = handler.RequestsTo("v1/chat/completions").Select(r => r.Body!).ToList();
        Assert.Equal(3, bodies.Count);
        Assert.True(bodies[0].ContainsKey("stream_options"));
        Assert.False(bodies[1].ContainsKey("stream_options"));
        Assert.False(bodies[2].ContainsKey("stream_options"));
    }

    // Errors.
    [Fact]
    public async Task Chat_Unauthorised_SaysTheKeyWasRejected_WithoutEchoingIt()
    {
        var handler = new FakeOpenAIHandler();
        handler.Error("POST v1/chat/completions", HttpStatusCode.Unauthorized, FakeOpenAIHandler.Fixture("error-401.json"));
        using var provider = Create(handler, SecretsWithKey());

        var ex = await Assert.ThrowsAsync<ProviderException>(() => ChatAsync(provider));

        Assert.Equal(PillionErrorCode.ProviderUnavailable, ex.Code);
        Assert.Equal(HttpStatusCode.Unauthorized, ex.StatusCode);
        Assert.Contains("rejected the API key", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Key, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("unit-te", ex.Message, StringComparison.Ordinal);
        Assert.False(ex.IsProviderFault);
        Assert.False(ex.IsRetryable);
    }

    [Fact]
    public async Task Chat_Forbidden_IsTreatedLikeARejectedKey()
    {
        var handler = new FakeOpenAIHandler();
        handler.Error("POST v1/chat/completions", HttpStatusCode.Forbidden, """{ "error": { "message": "Country, region, or territory not supported" } }""");
        using var provider = Create(handler, SecretsWithKey());

        var ex = await Assert.ThrowsAsync<ProviderException>(() => ChatAsync(provider));

        Assert.Equal(PillionErrorCode.ProviderUnavailable, ex.Code);
        Assert.Contains("rejected the API key or denied access", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Country", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Chat_UnauthorisedWithoutAKey_SaysAKeyIsNeeded()
    {
        var handler = new FakeOpenAIHandler();
        handler.Error("POST v1/chat/completions", HttpStatusCode.Unauthorized, FakeOpenAIHandler.Fixture("error-401.json"));
        using var provider = Create(handler, new InMemorySecretStore(), "http://localhost:1234/v1");

        var ex = await Assert.ThrowsAsync<ProviderException>(() => ChatAsync(provider));

        Assert.Contains("requires an API key", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Chat_UnknownModel_IsModelNotInstalled()
    {
        var handler = new FakeOpenAIHandler();
        handler.Error("POST v1/chat/completions", HttpStatusCode.NotFound, FakeOpenAIHandler.Fixture("error-model-not-found.json"));
        using var provider = Create(handler, SecretsWithKey());

        var ex = await Assert.ThrowsAsync<ProviderException>(() => ChatAsync(provider, "gpt-9-imaginary"));

        Assert.Equal(PillionErrorCode.ModelNotInstalled, ex.Code);
        Assert.Contains("gpt-9-imaginary", ex.Message, StringComparison.Ordinal);
        Assert.False(ex.IsProviderFault);
    }

    [Fact]
    public async Task Chat_A404ThatIsNotAboutTheModel_PointsAtTheEndpoint()
    {
        using var provider = Create(new FakeOpenAIHandler(), SecretsWithKey());

        var ex = await Assert.ThrowsAsync<ProviderException>(() => ChatAsync(provider));

        Assert.Equal(PillionErrorCode.ProviderUnavailable, ex.Code);
        Assert.Contains("/v1", ex.Message, StringComparison.Ordinal);
        Assert.False(ex.IsProviderFault);
    }

    [Fact]
    public async Task Chat_ContextLengthError_IsContextOverflow()
    {
        var handler = new FakeOpenAIHandler();
        handler.Error("POST v1/chat/completions", HttpStatusCode.BadRequest, FakeOpenAIHandler.Fixture("error-context-length.json"));
        using var provider = Create(handler, SecretsWithKey());

        var ex = await Assert.ThrowsAsync<ProviderException>(() => ChatAsync(provider));

        Assert.Equal(PillionErrorCode.ContextOverflow, ex.Code);
        Assert.False(ex.IsProviderFault);
        Assert.False(ex.IsRetryable);
    }

    [Theory]
    [InlineData("""{ "error": "the request exceeds the available context size, try increasing it" }""")]
    [InlineData("""{ "error": { "message": "prompt is too long: 300000 tokens > 200000 maximum" } }""")]
    [InlineData("""{ "object": "error", "message": "This model's maximum context length is 8192 tokens.", "code": 400 }""")]
    public async Task Chat_OtherServersContextWording_IsContextOverflow(string body)
    {
        var handler = new FakeOpenAIHandler();
        handler.Error("POST v1/chat/completions", HttpStatusCode.BadRequest, body);
        using var provider = Create(handler, SecretsWithKey());

        var ex = await Assert.ThrowsAsync<ProviderException>(() => ChatAsync(provider));

        Assert.Equal(PillionErrorCode.ContextOverflow, ex.Code);
    }

    [Fact]
    public async Task Chat_OtherBadRequests_ReportTheServersMessage_Truncated()
    {
        var handler = new FakeOpenAIHandler();
        handler.Error("POST v1/chat/completions", HttpStatusCode.BadRequest, JsonSerializer.Serialize(new { error = new { message = "Unsupported value: " + new string('x', 500) } }));
        using var provider = Create(handler, SecretsWithKey());

        var ex = await Assert.ThrowsAsync<ProviderException>(() => ChatAsync(provider));

        Assert.Equal(PillionErrorCode.ProviderUnavailable, ex.Code);
        Assert.Contains("Unsupported value", ex.Message, StringComparison.Ordinal);
        Assert.True(ex.Message.Length < 400);
        Assert.False(ex.IsProviderFault);
    }

    [Fact]
    public async Task Chat_RateLimited_IsATransientProviderException()
    {
        var handler = new FakeOpenAIHandler();
        handler.Error("POST v1/chat/completions", HttpStatusCode.TooManyRequests, FakeOpenAIHandler.Fixture("error-429.json"));
        using var provider = Create(handler, SecretsWithKey());

        var ex = await Assert.ThrowsAsync<ProviderException>(() => ChatAsync(provider));

        Assert.Equal(PillionErrorCode.ProviderUnavailable, ex.Code);
        Assert.Equal(HttpStatusCode.TooManyRequests, ex.StatusCode);
        Assert.True(ex.IsRetryable);
        Assert.True(ex.IsProviderFault);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task Chat_ServerErrors_AreTransientProviderExceptions(HttpStatusCode status)
    {
        var handler = new FakeOpenAIHandler();
        handler.Error("POST v1/chat/completions", status, "upstream trouble", "text/plain");
        using var provider = Create(handler, SecretsWithKey());

        var ex = await Assert.ThrowsAsync<ProviderException>(() => ChatAsync(provider));

        Assert.Equal(status, ex.StatusCode);
        Assert.True(ex.IsRetryable);
        Assert.True(ex.IsProviderFault);
    }

    [Fact]
    public async Task Chat_ARedirect_IsNotFollowed()
    {
        var handler = new FakeOpenAIHandler();
        handler.On("POST v1/chat/completions", _ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TemporaryRedirect);
            response.Headers.Location = new Uri("https://elsewhere.example/v1/chat/completions");
            return response;
        });
        using var provider = Create(handler, SecretsWithKey());

        var ex = await Assert.ThrowsAsync<ProviderException>(() => ChatAsync(provider));

        Assert.Contains("redirect", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Chat_ConnectionRefused_IsProviderUnavailable()
    {
        using var provider = Create(new FakeOpenAIHandler { RefuseConnections = true }, new InMemorySecretStore(), "http://localhost:1234/v1");

        var ex = await Assert.ThrowsAsync<ProviderException>(() => ChatAsync(provider));

        Assert.Equal(PillionErrorCode.ProviderUnavailable, ex.Code);
        Assert.Contains("not reachable", ex.Message, StringComparison.Ordinal);
        Assert.True(ex.IsProviderFault);
    }

    [Fact]
    public async Task Chat_AnHtmlPageInsteadOfJson_IsReportedAsAnUnreadableResponse()
    {
        var handler = new FakeOpenAIHandler();
        handler.Text("POST v1/chat/completions", "<html>Sign in to continue</html>", "application/json");
        using var provider = Create(handler, SecretsWithKey());

        var ex = await Assert.ThrowsAsync<ProviderException>(() => ChatAsync(provider));

        Assert.Contains("OpenAI-compatible", ex.Message, StringComparison.Ordinal);
    }

    // Authentication.
    [Fact]
    public async Task Bearer_IsSentWhenAKeyExists_AndOmittedForALoopbackServerWithoutOne()
    {
        var withKey = FakeOpenAIHandler.WithDefaults();
        var withoutKey = FakeOpenAIHandler.WithDefaults();
        using var keyed = Create(withKey, SecretsWithKey(), "http://localhost:1234/v1");
        using var keyless = Create(withoutKey, new InMemorySecretStore(), "http://localhost:1234/v1");

        await ChatAsync(keyed);
        await ChatAsync(keyless);
        await keyless.ListModelsAsync(CancellationToken.None);

        Assert.Equal("Bearer " + Key, Assert.Single(withKey.Requests).Authorization);
        Assert.All(withoutKey.Requests, r => Assert.Null(r.Authorization));
        Assert.Equal(2, withoutKey.Requests.Count);
    }

    [Fact]
    public async Task Key_IsReadFromTheSecretStoreForEveryRequest()
    {
        var handler = FakeOpenAIHandler.WithDefaults();
        var secrets = SecretsWithKey();
        using var provider = Create(handler, secrets);

        await ChatAsync(provider);
        secrets.Set("gw", "unit-test-key-0002");
        await ChatAsync(provider);
        secrets.Delete("gw");
        await provider.ListModelsAsync(CancellationToken.None);

        Assert.Equal(["Bearer " + Key, "Bearer unit-test-key-0002", null], handler.Requests.Select(r => r.Authorization));
    }

    [Fact]
    public async Task AKeyWithControlCharacters_IsRefused_WithoutBeingSent()
    {
        var handler = FakeOpenAIHandler.WithDefaults();
        var secrets = new InMemorySecretStore();
        secrets.Set("gw", "line1\r\nX-Injected: yes");
        using var provider = Create(handler, secrets);

        var ex = await Assert.ThrowsAsync<ProviderException>(() => ChatAsync(provider));

        Assert.Contains("control characters", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Injected", ex.Message, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    // FIM.
    [Fact]
    public void Fim_IsOnlyOfferedByALocalServerThatDeclaresIt()
    {
        var secrets = new InMemorySecretStore();
        using var cloudDeclared = Create(FakeOpenAIHandler.WithDefaults(), secrets, CloudEndpoint, extensionJson: """{ "supportsFim": true }""");
        using var localUndeclared = Create(FakeOpenAIHandler.WithDefaults(), secrets, "http://localhost:1234/v1");
        using var localDeclared = Create(FakeOpenAIHandler.WithDefaults(), secrets, "http://localhost:1234/v1", extensionJson: """{ "supportsFim": true }""");

        Assert.Null(cloudDeclared.GetFimClient("qwen2.5-coder-7b-instruct"));
        Assert.Null(localUndeclared.GetFimClient("qwen2.5-coder-7b-instruct"));
        Assert.NotNull(localDeclared.GetFimClient("qwen2.5-coder-7b-instruct"));
    }

    [Fact]
    public async Task Fim_PostsPrefixAndSuffixToCompletions_AndStreamsTheText()
    {
        var handler = FakeOpenAIHandler.WithDefaults();
        using var provider = Create(handler, new InMemorySecretStore(), "http://localhost:1234/v1", extensionJson: """{ "supportsFim": true }""");

        var pieces = await provider.GetFimClient("qwen2.5-coder-7b-instruct")!.CompleteAsync(
            new FimRequest { Prefix = "int Add(int a, int b) => ", Suffix = ";", MaxTokens = 32, Temperature = 0, StopSequences = ["\n\n"] },
            CancellationToken.None).CollectAsync();

        Assert.Equal(["a +", " b"], pieces);
        var body = handler.RequestsTo("v1/completions").Single().Body!;
        Assert.Equal("qwen2.5-coder-7b-instruct", body["model"]!.GetValue<string>());
        Assert.Equal("int Add(int a, int b) => ", body["prompt"]!.GetValue<string>());
        Assert.Equal(";", body["suffix"]!.GetValue<string>());
        Assert.Equal(32, body["max_tokens"]!.GetValue<int>());
        Assert.True(body["stream"]!.GetValue<bool>());
        Assert.Equal("\n\n", body["stop"]![0]!.GetValue<string>());
    }

    // Probe.
    [Fact]
    public async Task Probe_WithAnAcceptedKey_ReportsReachableAuthenticatedAndTheServer()
    {
        var handler = new FakeOpenAIHandler();
        handler.On("GET v1/models", _ =>
        {
            var response = FakeOpenAIHandler.Respond(HttpStatusCode.OK, FakeOpenAIHandler.Fixture("models.json"));
            response.Headers.TryAddWithoutValidation("Server", "uvicorn");
            return response;
        });
        using var provider = Create(handler, SecretsWithKey());

        var detailed = await provider.ProbeDetailedAsync(CancellationToken.None);
        var probe = await provider.ProbeAsync(CancellationToken.None);

        Assert.True(detailed.IsReachable);
        Assert.True(detailed.IsAuthenticated);
        Assert.Equal("uvicorn", detailed.Server);
        Assert.Equal(6, detailed.ModelCount);
        Assert.True(probe.IsReachable);
        Assert.Equal("uvicorn", probe.Version);
        Assert.Contains("accepted", probe.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Key, probe.Message, StringComparison.Ordinal);
        Assert.All(handler.Requests, r => Assert.Equal("Bearer " + Key, r.Authorization));
    }

    [Fact]
    public async Task Probe_WithARejectedKey_IsReachableButNotAuthenticated()
    {
        var handler = new FakeOpenAIHandler();
        handler.Error("GET v1/models", HttpStatusCode.Unauthorized, FakeOpenAIHandler.Fixture("error-401.json"));
        using var provider = Create(handler, SecretsWithKey());

        var detailed = await provider.ProbeDetailedAsync(CancellationToken.None);
        var probe = await provider.ProbeAsync(CancellationToken.None);

        Assert.True(detailed.IsReachable);
        Assert.False(detailed.IsAuthenticated);
        Assert.False(probe.IsReachable);
        Assert.Contains("rejected the API key", probe.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Key, probe.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("unit-te", probe.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Probe_ALocalServerWithoutAKey_NeedsNone()
    {
        var handler = FakeOpenAIHandler.WithDefaults();
        using var provider = Create(handler, new InMemorySecretStore(), "http://localhost:1234/v1");

        var detailed = await provider.ProbeDetailedAsync(CancellationToken.None);

        Assert.True(detailed.IsReachable);
        Assert.Null(detailed.IsAuthenticated);
        Assert.Contains("no API key needed", detailed.Message, StringComparison.Ordinal);
        Assert.Null(Assert.Single(handler.Requests).Authorization);
    }

    [Fact]
    public async Task Probe_AServerWithoutAModelList_IsStillReachable()
    {
        using var provider = Create(new FakeOpenAIHandler(), SecretsWithKey());

        var probe = await provider.ProbeAsync(CancellationToken.None);

        Assert.True(probe.IsReachable);
        Assert.Contains("no model list", probe.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Probe_ConnectionRefused_DoesNotThrow()
    {
        using var provider = Create(new FakeOpenAIHandler { RefuseConnections = true }, new InMemorySecretStore(), "http://localhost:1234/v1");

        var probe = await provider.ProbeAsync(CancellationToken.None);

        Assert.False(probe.IsReachable);
        Assert.Contains("not reachable", probe.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Probe_AServerError_IsNotUsable()
    {
        var handler = new FakeOpenAIHandler();
        handler.Error("GET v1/models", HttpStatusCode.BadGateway, "bad gateway", "text/plain");
        using var provider = Create(handler, SecretsWithKey());

        var probe = await provider.ProbeAsync(CancellationToken.None);

        Assert.False(probe.IsReachable);
        Assert.Contains("502", probe.Message, StringComparison.Ordinal);
    }
}
