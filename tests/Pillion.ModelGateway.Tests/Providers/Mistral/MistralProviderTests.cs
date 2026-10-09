using System.Net;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Pillion.Common.Secrets;
using Pillion.Common.Settings;
using Pillion.Contracts;
using Pillion.ModelGateway.Providers.Mistral;
using Pillion.ModelGateway.Tests.Providers.Cloud;
using Pillion.ModelGateway.Tests.TestSupport;
using AiChatMessage = Microsoft.Extensions.AI.ChatMessage;
using AiChatRole = Microsoft.Extensions.AI.ChatRole;

namespace Pillion.ModelGateway.Tests.Providers.Mistral;

/// <summary>The Mistral adapter (chat and Codestral FIM) against fixtures in the documented wire shape.</summary>
public sealed class MistralProviderTests
{
    // Fabricated.
    internal const string Key = "unit-test-key-0001";

    private static readonly AiChatMessage[] Hello = [new AiChatMessage(AiChatRole.User, "Say hello")];

    internal static FakeCloudHandler Defaults() => new FakeCloudHandler()
        .Serve("GET v1/models", "Mistral", "models.json")
        .Serve("POST v1/chat/completions", "Mistral", "chat-stream.sse")
        .Serve("POST v1/fim/completions", "Mistral", "fim-stream.sse");

    internal static ProviderSettings Settings(string id, string? extensionJson = null, string endpoint = "")
    {
        var settings = new ProviderSettings { Id = id, Kind = ProviderKind.Mistral, Endpoint = endpoint };
        if (extensionJson is not null)
        {
            settings.ExtensionData = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(extensionJson);
        }

        return settings;
    }

    internal static MistralProvider Create(FakeCloudHandler handler, InMemorySecretStore secrets, string endpoint = "") =>
        (MistralProvider)new MistralProviderFactory(secrets, () => handler).Create(
            Settings("mistral", null, endpoint),
            new ProviderCreationContext(null, ModelCatalog.Default, NullLoggerFactory.Instance, null));

    private static InMemorySecretStore SecretsWithKey()
    {
        var secrets = new InMemorySecretStore();
        secrets.Set("mistral", Key);
        return secrets;
    }

    private static Task<List<ChatResponseUpdate>> ChatAsync(MistralProvider provider, string model = "mistral-small-latest", ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        provider.GetChatClient(model).GetStreamingResponseAsync(Hello, options, cancellationToken).CollectAsync(cancellationToken);

    private static Task<List<string>> FimAsync(MistralProvider provider, FimRequest? request = null, string model = "codestral-latest", CancellationToken cancellationToken = default) =>
        provider.GetFimClient(model)!.CompleteAsync(request ?? new FimRequest { Prefix = "int Add(int a, int b) => ", Suffix = ";" }, cancellationToken).CollectAsync(cancellationToken);

    // Creation.
    [Fact]
    public void TheProvider_IsCloud_AndNeedsAKey()
    {
        using var provider = Create(Defaults(), SecretsWithKey());
        var factory = new MistralProviderFactory(new InMemorySecretStore());

        Assert.False(provider.IsLocal);
        Assert.Equal(ProviderKind.Mistral, provider.Kind);
        Assert.Equal("Mistral", provider.DisplayName);
        Assert.True(factory.RequiresApiKey(Settings("mistral")));
        Assert.Equal(FeatureNames.MistralProvider, factory.FeatureFlag);
        Assert.Equal("https://api.mistral.ai/v1/", provider.Endpoint.AbsoluteUri);
    }

    [Fact]
    public async Task ABareHostEndpoint_GetsTheV1Root_AndAPathIsTakenAsGiven()
    {
        var handler = Defaults().Serve("POST gw/mistral/chat/completions", "Mistral", "chat-stream.sse");
        using var bare = Create(handler, SecretsWithKey(), "https://mistral.internal.example");
        using var withPath = Create(handler, SecretsWithKey(), "https://gateway.example/gw/mistral");

        Assert.Equal("https://mistral.internal.example/v1/", bare.Endpoint.AbsoluteUri);
        await ChatAsync(withPath);
        Assert.Single(handler.RequestsTo("gw/mistral/chat/completions"));
    }

    // Models.
    [Fact]
    public async Task ListModels_ReadsCapabilitiesFromTheListing()
    {
        var handler = Defaults();
        using var provider = Create(handler, SecretsWithKey());

        var models = await provider.ListModelsAsync(CancellationToken.None);

        Assert.Equal(["codestral-latest", "mistral-small-latest", "mistral-embed"], models.Select(m => m.ModelId));
        Assert.All(models, m => Assert.Equal("mistral", m.ProviderId));
        var codestral = models[0];
        Assert.True(codestral.Capabilities.Fim);
        Assert.True(codestral.Capabilities.Chat);
        Assert.Equal(128_000, codestral.Capabilities.ContextWindow);
        Assert.Equal(ToolCallingSupport.Native, codestral.Capabilities.ToolCalling);

        var small = models[1];
        Assert.False(small.Capabilities.Fim);
        Assert.True(small.Capabilities.Vision);
        Assert.Equal(262_144, small.Capabilities.ContextWindow);

        var embed = models[2];
        Assert.False(embed.Capabilities.Chat);
        Assert.True(embed.Capabilities.Embeddings);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("Bearer " + Key, request.Header("authorization"));
    }

    [Fact]
    public async Task ListModels_AcceptsABareArray()
    {
        var handler = Defaults().Serve("GET v1/models", "Mistral", "models-array.json");
        using var provider = Create(handler, SecretsWithKey());

        var models = await provider.ListModelsAsync(CancellationToken.None);

        var model = Assert.Single(models);
        Assert.Equal("codestral-latest", model.ModelId);
        Assert.Equal(32_768, model.Capabilities.ContextWindow);
        Assert.True(model.Capabilities.Fim);
    }

    // Chat.
    [Fact]
    public async Task Chat_StreamsTextInPieces_WithUsage_AndNamesTheModel()
    {
        var handler = Defaults();
        using var provider = Create(handler, SecretsWithKey());

        var updates = await ChatAsync(provider);

        Assert.Equal(["Hello", " from", " Pillion"], updates.Where(u => !string.IsNullOrEmpty(u.Text)).Select(u => u.Text));
        Assert.All(updates, u => Assert.Equal("mistral-small-latest", u.ModelId));
        var usage = Assert.Single(updates.SelectMany(u => u.Contents).OfType<UsageContent>()).Details;
        Assert.Equal(18, usage.InputTokenCount);
        Assert.Equal(6, usage.OutputTokenCount);
        Assert.Equal(ChatFinishReason.Stop, updates.Last(u => u.FinishReason is not null).FinishReason);
    }

    [Fact]
    public async Task Chat_SendsAnOpenAiShapedRequest_WithoutStreamOptions()
    {
        var handler = Defaults();
        using var provider = Create(handler, SecretsWithKey());

        await ChatAsync(provider, options: new ChatOptions { MaxOutputTokens = 123, Temperature = 0.3f, StopSequences = ["END"] });

        var request = Assert.Single(handler.RequestsTo("v1/chat/completions"));
        var body = request.Body!;
        Assert.Equal("mistral-small-latest", body["model"]!.GetValue<string>());
        Assert.True(body["stream"]!.GetValue<bool>());
        Assert.Equal(123, body["max_tokens"]!.GetValue<int>());
        Assert.False(body.ContainsKey("max_completion_tokens"));
        Assert.Equal(0.3f, body["temperature"]!.GetValue<float>());
        Assert.Equal(["END"], body["stop"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.False(body.ContainsKey("stream_options"));
        Assert.Equal("user", body["messages"]![0]!["role"]!.GetValue<string>());
        Assert.Equal("Bearer " + Key, request.Header("authorization"));
    }

    [Fact]
    public async Task Chat_Cancelled_MidStream_StopsTheEnumeration()
    {
        var handler = Defaults().On("POST v1/chat/completions", _ => Stalling.Sse(
            "data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"Hel\"},\"finish_reason\":null}]}\n\n"));
        using var provider = Create(handler, SecretsWithKey());
        using var cts = new CancellationTokenSource();
        var seen = new List<string>();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var update in provider.GetChatClient("mistral-small-latest").GetStreamingResponseAsync(Hello, null, cts.Token))
            {
                seen.Add(update.Text);
                await cts.CancelAsync();
            }
        });

        Assert.Equal(["Hel"], seen);
    }

    // FIM.
    [Fact]
    public async Task Fim_PostsPromptAndSuffix_AndStreamsTheCompletion()
    {
        var handler = Defaults();
        using var provider = Create(handler, SecretsWithKey());

        var pieces = await FimAsync(provider, new FimRequest { Prefix = "int Add(int a, int b) => ", Suffix = ";", MaxTokens = 64, Temperature = 0.1f, StopSequences = ["\n\n"] });

        Assert.Equal(["a", " + ", "b"], pieces);
        var request = Assert.Single(handler.RequestsTo("v1/fim/completions"));
        var body = request.Body!;
        Assert.Equal("codestral-latest", body["model"]!.GetValue<string>());
        Assert.Equal("int Add(int a, int b) => ", body["prompt"]!.GetValue<string>());
        Assert.Equal(";", body["suffix"]!.GetValue<string>());
        Assert.True(body["stream"]!.GetValue<bool>());
        Assert.Equal(64, body["max_tokens"]!.GetValue<int>());
        Assert.Equal(0.1f, body["temperature"]!.GetValue<float>());
        Assert.Equal(["\n\n"], body["stop"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal("Bearer " + Key, request.Header("authorization"));
        Assert.False(body.ContainsKey("messages"));
    }

    [Fact]
    public async Task Fim_OmitsUnsetOptions_AndAcceptsMessageContentAndTextShapes()
    {
        var handler = Defaults().On("POST v1/fim/completions", _ => FakeCloudHandler.Respond(
            HttpStatusCode.OK,
            "data: {\"choices\":[{\"index\":0,\"message\":{\"content\":\"x\"}}]}\n\ndata: {\"choices\":[{\"index\":0,\"text\":\"y\"}]}\n\ndata: [DONE]\n\n",
            "text/event-stream"));
        using var provider = Create(handler, SecretsWithKey());

        var pieces = await FimAsync(provider);

        Assert.Equal(["x", "y"], pieces);
        var body = handler.Requests.Single().Body!;
        Assert.False(body.ContainsKey("max_tokens"));
        Assert.False(body.ContainsKey("temperature"));
        Assert.False(body.ContainsKey("stop"));
    }

    [Fact]
    public void GetFimClient_IsOfferedOnlyForFimCapableModels()
    {
        using var provider = Create(Defaults(), SecretsWithKey());

        Assert.NotNull(provider.GetFimClient("codestral-latest"));
        Assert.NotNull(provider.GetFimClient("codestral-2508"));
        Assert.Null(provider.GetFimClient("mistral-small-latest"));
        Assert.Null(provider.GetFimClient("mistral-medium-3-5"));
    }

    [Fact]
    public async Task Fim_Cancelled_MidStream_StopsTheEnumeration()
    {
        var handler = Defaults().On("POST v1/fim/completions", _ => Stalling.Sse(
            "data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"a\"},\"finish_reason\":null}]}\n\n"));
        using var provider = Create(handler, SecretsWithKey());
        using var cts = new CancellationTokenSource();
        var seen = new List<string>();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var piece in provider.GetFimClient("codestral-latest")!.CompleteAsync(new FimRequest { Prefix = "x" }, cts.Token))
            {
                seen.Add(piece);
                await cts.CancelAsync();
            }
        });

        Assert.Equal(["a"], seen);
    }

    [Fact]
    public async Task Fim_ErrorsAreMapped()
    {
        var handler = Defaults().Fail("POST v1/fim/completions", HttpStatusCode.Unauthorized, "Mistral", "error-401.json");
        using var provider = Create(handler, SecretsWithKey());

        var ex = await Assert.ThrowsAsync<ProviderException>(() => FimAsync(provider));

        Assert.Contains("rejected the API key", ex.Message, StringComparison.Ordinal);
        Assert.False(ex.IsRetryable);
    }

    // Errors.
    [Fact]
    public async Task Error401_IsProviderUnavailable_AndNotRetryable()
    {
        var handler = Defaults().Fail("POST v1/chat/completions", HttpStatusCode.Unauthorized, "Mistral", "error-401.json");
        using var provider = Create(handler, SecretsWithKey());

        var ex = await Assert.ThrowsAsync<ProviderException>(() => ChatAsync(provider));

        Assert.Equal(PillionErrorCode.ProviderUnavailable, ex.Code);
        Assert.Contains("rejected the API key", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Key, ex.Message, StringComparison.Ordinal);
        Assert.False(ex.IsRetryable);
        Assert.False(ex.IsProviderFault);
    }

    [Fact]
    public async Task ErrorNamingAnInvalidModel_With404_IsModelNotInstalled()
    {
        var handler = Defaults().Fail("POST v1/chat/completions", HttpStatusCode.NotFound, "Mistral", "error-404-model.json");
        using var provider = Create(handler, SecretsWithKey());

        var ex = await Assert.ThrowsAsync<ProviderException>(() => ChatAsync(provider, "mistral-imaginary-9"));

        Assert.Equal(PillionErrorCode.ModelNotInstalled, ex.Code);
    }

    [Fact]
    public async Task Error400_AboutTheMaximumContextLength_IsContextOverflow()
    {
        var handler = Defaults().Fail("POST v1/chat/completions", HttpStatusCode.BadRequest, "Mistral", "error-context-length.json");
        using var provider = Create(handler, SecretsWithKey());

        var ex = await Assert.ThrowsAsync<ProviderException>(() => ChatAsync(provider));

        Assert.Equal(PillionErrorCode.ContextOverflow, ex.Code);
    }

    [Fact]
    public async Task Error422_ShowsTheValidationMessage_WithoutRetry()
    {
        var handler = Defaults().Fail("POST v1/chat/completions", (HttpStatusCode)422, "Mistral", "error-422.json");
        using var provider = Create(handler, SecretsWithKey());

        var ex = await Assert.ThrowsAsync<ProviderException>(() => ChatAsync(provider));

        Assert.Contains("Extra inputs are not permitted", ex.Message, StringComparison.Ordinal);
        Assert.False(ex.IsRetryable);
    }

    [Fact]
    public async Task Error429_IsTransient()
    {
        var handler = Defaults().Fail("POST v1/chat/completions", HttpStatusCode.TooManyRequests, "Mistral", "error-429.json");
        using var provider = Create(handler, SecretsWithKey());

        var ex = await Assert.ThrowsAsync<ProviderException>(() => ChatAsync(provider));

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
        var fim = await Assert.ThrowsAsync<ProviderException>(() => FimAsync(provider));

        Assert.Contains("connection refused", ex.Message, StringComparison.Ordinal);
        Assert.Contains("connection refused", fim.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnErrorMidStream_AfterSomeText_IsATransientFailure()
    {
        var handler = Defaults().Serve("POST v1/chat/completions", "Mistral", "chat-error-midstream.sse");
        using var provider = Create(handler, SecretsWithKey());
        var seen = new List<string>();

        var ex = await Assert.ThrowsAsync<ProviderException>(async () =>
        {
            await foreach (var update in provider.GetChatClient("mistral-small-latest").GetStreamingResponseAsync(Hello))
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
    public async Task Probe_WithARejectedKey_IsReachableButNotUsable()
    {
        var handler = Defaults().Fail("GET v1/models", HttpStatusCode.Unauthorized, "Mistral", "error-401.json");
        using var provider = Create(handler, SecretsWithKey());

        var detailed = await provider.ProbeDetailedAsync(CancellationToken.None);

        Assert.True(detailed.IsReachable);
        Assert.False(detailed.IsAuthenticated);
        Assert.False((await provider.ProbeAsync(CancellationToken.None)).IsReachable);
    }

    [Fact]
    public async Task Probe_WhenTheServiceIsUnreachable_DoesNotThrow()
    {
        var handler = Defaults();
        handler.RefuseConnections = true;
        using var provider = Create(handler, SecretsWithKey());

        Assert.False((await provider.ProbeAsync(CancellationToken.None)).IsReachable);
    }
}
