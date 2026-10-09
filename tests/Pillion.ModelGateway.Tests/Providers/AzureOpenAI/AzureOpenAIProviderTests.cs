using System.Net;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Pillion.Common.Secrets;
using Pillion.Common.Settings;
using Pillion.Contracts;
using Pillion.ModelGateway.Providers.AzureOpenAI;
using Pillion.ModelGateway.Tests.Providers.Cloud;
using Pillion.ModelGateway.Tests.TestSupport;
using AiChatMessage = Microsoft.Extensions.AI.ChatMessage;
using AiChatRole = Microsoft.Extensions.AI.ChatRole;

namespace Pillion.ModelGateway.Tests.Providers.AzureOpenAI;

/// <summary>The Azure OpenAI adapter against fixtures in the documented wire shape (key and Entra ID paths).</summary>
public sealed class AzureOpenAIProviderTests
{
    // Fabricated.
    internal const string Key = "unit-test-key-0001";

    internal const string Token = "unit-test-token-0002";

    internal const string Endpoint = "https://contoso.openai.azure.com";

    internal const string ChatRoute = "POST openai/deployments/prod-chat/chat/completions";

    private static readonly AiChatMessage[] Hello = [new AiChatMessage(AiChatRole.User, "Say hello")];

    internal static FakeCloudHandler Defaults() => new FakeCloudHandler()
        .Serve("GET openai/models", "AzureOpenAI", "models.json")
        .Serve(ChatRoute, "AzureOpenAI", "chat-stream.sse");

    internal static ProviderSettings Settings(string id = "azure", string? extensionJson = null, string endpoint = Endpoint, params string[] deployments)
    {
        var settings = new ProviderSettings { Id = id, Kind = ProviderKind.AzureOpenAi, Endpoint = endpoint, Models = [.. deployments.Length == 0 ? ["prod-chat"] : deployments] };
        if (extensionJson is not null)
        {
            settings.ExtensionData = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(extensionJson);
        }

        return settings;
    }

    internal static AzureOpenAIProvider Create(FakeCloudHandler handler, InMemorySecretStore secrets, ITokenCredentialProvider? credentials = null, string? extensionJson = null, string endpoint = Endpoint, params string[] deployments) =>
        (AzureOpenAIProvider)new AzureOpenAIProviderFactory(secrets, credentials, () => handler).Create(
            Settings("azure", extensionJson, endpoint, deployments),
            new ProviderCreationContext(null, ModelCatalog.Default, NullLoggerFactory.Instance, null));

    private static InMemorySecretStore SecretsWithKey()
    {
        var secrets = new InMemorySecretStore();
        secrets.Set("azure", Key);
        return secrets;
    }

    private static Task<List<ChatResponseUpdate>> ChatAsync(AzureOpenAIProvider provider, string deployment = "prod-chat", ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        provider.GetChatClient(deployment).GetStreamingResponseAsync(Hello, options, cancellationToken).CollectAsync(cancellationToken);

    // Creation.
    [Fact]
    public void TheProvider_IsCloud_AndHasNoFim()
    {
        using var provider = Create(Defaults(), SecretsWithKey());

        Assert.False(provider.IsLocal);
        Assert.Equal(ProviderKind.AzureOpenAi, provider.Kind);
        Assert.Equal("Azure OpenAI", provider.DisplayName);
        Assert.Equal("https://contoso.openai.azure.com/", provider.Endpoint.AbsoluteUri);
        Assert.Equal("2024-10-21", provider.ApiVersion);
        Assert.Equal("https://cognitiveservices.azure.com/.default", provider.TokenScope);
        Assert.Equal(AzureAuthentication.Auto, provider.Authentication);
        Assert.Null(provider.GetFimClient("prod-chat"));
        Assert.Equal(FeatureNames.AzureOpenAIProvider, new AzureOpenAIProviderFactory(new InMemorySecretStore()).FeatureFlag);
    }

    [Fact]
    public void WithoutAnEndpoint_TheProviderCannotBeCreated()
    {
        var factory = new AzureOpenAIProviderFactory(new InMemorySecretStore());

        var ex = Assert.Throws<InvalidOperationException>(() => factory.Create(
            Settings(endpoint: string.Empty),
            new ProviderCreationContext(null, ModelCatalog.Default, NullLoggerFactory.Instance, null)));

        Assert.Contains("needs an endpoint", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RequiresApiKey_DependsOnWhetherTheHostCanSupplyEntraTokens()
    {
        var withoutCredentials = new AzureOpenAIProviderFactory(new InMemorySecretStore());
        var withCredentials = new AzureOpenAIProviderFactory(new InMemorySecretStore(), new StaticTokenCredentialProvider(Token), null);
        var viaDi = new AzureOpenAIProviderFactory(new InMemorySecretStore(), [new StaticTokenCredentialProvider(Token)]);
        var viaDiEmpty = new AzureOpenAIProviderFactory(new InMemorySecretStore(), Array.Empty<ITokenCredentialProvider>());
        var keyOnly = Settings(extensionJson: """{ "authentication": "apiKey" }""");
        var entraOnly = Settings(extensionJson: """{ "authentication": "entra" }""");

        Assert.True(withoutCredentials.RequiresApiKey(Settings()));
        Assert.False(withCredentials.RequiresApiKey(Settings()));
        Assert.False(viaDi.RequiresApiKey(Settings()));
        Assert.True(viaDiEmpty.RequiresApiKey(Settings()));
        Assert.True(withCredentials.RequiresApiKey(keyOnly));
        Assert.False(withoutCredentials.RequiresApiKey(entraOnly));
    }

    [Fact]
    public void EntraAuthentication_WithoutACredentialProvider_ExplainsWhyTheProviderIsHidden()
    {
        var factory = new AzureOpenAIProviderFactory(new InMemorySecretStore());

        var ex = Assert.Throws<InvalidOperationException>(() => factory.Create(
            Settings(extensionJson: """{ "authentication": "entra" }"""),
            new ProviderCreationContext(null, ModelCatalog.Default, NullLoggerFactory.Instance, null)));

        Assert.Contains("Entra ID", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownAuthenticationValue_IsReported()
    {
        var factory = new AzureOpenAIProviderFactory(new InMemorySecretStore());

        var ex = Assert.Throws<InvalidOperationException>(() => factory.Create(
            Settings(extensionJson: """{ "authentication": "kerberos" }"""),
            new ProviderCreationContext(null, ModelCatalog.Default, NullLoggerFactory.Instance, null)));

        Assert.Contains("kerberos", ex.Message, StringComparison.Ordinal);
    }

    // Models.
    [Fact]
    public async Task ListModels_ReturnsTheConfiguredDeployments_WithCapabilitiesFromTheirFamily()
    {
        var handler = Defaults();
        using var provider = Create(
            handler,
            SecretsWithKey(),
            extensionJson: """{ "modelFamilies": { "prod-chat": "gpt-4o" } }""",
            deployments: ["prod-chat", "text-embedding-3-small", "unmapped-name", "prod-chat"]);

        var models = await provider.ListModelsAsync(CancellationToken.None);

        Assert.Equal(["prod-chat", "text-embedding-3-small", "unmapped-name"], models.Select(m => m.ModelId));
        Assert.All(models, m => Assert.Equal("azure", m.ProviderId));
        Assert.Equal(128_000, models[0].Capabilities.ContextWindow);
        Assert.True(models[0].Capabilities.Vision);
        Assert.True(models[1].Capabilities.Embeddings);
        Assert.Equal(4096, models[2].Capabilities.ContextWindow); // Unknown family: the catalog's default.
        Assert.All(models, m => Assert.False(m.Capabilities.Fim));

        // Listing reads the settings, not the network.
        Assert.Empty(handler.Requests);
    }

    // Chat on the key path.
    [Fact]
    public async Task Chat_UsesTheDeploymentUrl_TheApiVersion_AndTheApiKeyHeader()
    {
        var handler = Defaults();
        using var provider = Create(handler, SecretsWithKey());

        var updates = await ChatAsync(provider, options: new ChatOptions { MaxOutputTokens = 50, Temperature = 0.4f });

        Assert.Equal("Hello from Azure", string.Concat(updates.Select(u => u.Text)));
        Assert.All(updates, u => Assert.Equal("prod-chat", u.ModelId));
        var usage = Assert.Single(updates.SelectMany(u => u.Contents).OfType<UsageContent>()).Details;
        Assert.Equal(19, usage.InputTokenCount);
        Assert.Equal(3, usage.OutputTokenCount);
        Assert.Equal(ChatFinishReason.Stop, updates.Last(u => u.FinishReason is not null).FinishReason);

        var request = Assert.Single(handler.RequestsTo("openai/deployments/prod-chat/chat/completions"));
        Assert.Equal("contoso.openai.azure.com", request.Host);
        Assert.Equal("api-version=2024-10-21", request.Query);
        Assert.Equal(Key, request.Header("api-key"));
        Assert.Null(request.Header("authorization"));
        var body = request.Body!;
        Assert.False(body.ContainsKey("model"));
        Assert.True(body["stream"]!.GetValue<bool>());
        Assert.Equal(50, body["max_completion_tokens"]!.GetValue<int>());
        Assert.False(body.ContainsKey("max_tokens"));
        Assert.Equal(0.4f, body["temperature"]!.GetValue<float>());
        Assert.Equal("user", body["messages"]![0]!["role"]!.GetValue<string>());
    }

    [Fact]
    public async Task Chat_ADeploymentNameIsEscapedInThePath_AndTheApiVersionCanBeOverridden()
    {
        var handler = new FakeCloudHandler().Serve("POST openai/deployments/my deploy/chat/completions", "AzureOpenAI", "chat-stream.sse");
        using var provider = Create(handler, SecretsWithKey(), extensionJson: """{ "apiVersion": "2025-01-01-preview" }""", deployments: "my deploy");

        await ChatAsync(provider, "my deploy");

        var request = Assert.Single(handler.Requests);
        Assert.Equal("api-version=2025-01-01-preview", request.Query);
        Assert.Equal("openai/deployments/my deploy/chat/completions", request.Path);
    }

    [Fact]
    public async Task Chat_ReadsTheKeyForEveryRequest()
    {
        var handler = Defaults();
        var secrets = SecretsWithKey();
        using var provider = Create(handler, secrets);

        await ChatAsync(provider);
        secrets.Set("azure", "rotated-key-0003");
        await ChatAsync(provider);

        Assert.Equal([Key, "rotated-key-0003"], handler.Requests.Select(r => r.Header("api-key")));
    }

    [Fact]
    public async Task Chat_ReasoningDeployments_GetNoSamplingParameters()
    {
        var handler = new FakeCloudHandler().Serve("POST openai/deployments/gpt-5-reasoner/chat/completions", "AzureOpenAI", "chat-stream.sse");
        using var provider = Create(handler, SecretsWithKey(), deployments: "gpt-5-reasoner");

        await ChatAsync(provider, "gpt-5-reasoner", new ChatOptions { Temperature = 0.4f, StopSequences = ["END"] });

        var body = handler.Requests.Single().Body!;
        Assert.False(body.ContainsKey("temperature"));
        Assert.False(body.ContainsKey("stop"));
    }

    [Fact]
    public async Task Chat_Cancelled_MidStream_StopsTheEnumeration()
    {
        var handler = Defaults().On(ChatRoute, _ => Stalling.Sse(
            "data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"Hel\"},\"finish_reason\":null}]}\n\n"));
        using var provider = Create(handler, SecretsWithKey());
        using var cts = new CancellationTokenSource();
        var seen = new List<string>();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var update in provider.GetChatClient("prod-chat").GetStreamingResponseAsync(Hello, null, cts.Token))
            {
                seen.Add(update.Text);
                await cts.CancelAsync();
            }
        });

        Assert.Equal(["Hel"], seen);
    }

    // Chat on the Entra ID path.
    [Fact]
    public async Task EntraId_SendsABearerToken_ForTheConfiguredScope_AndNoApiKey()
    {
        var handler = Defaults();
        var credentials = new StaticTokenCredentialProvider(Token);
        using var provider = Create(handler, new InMemorySecretStore(), credentials);

        await ChatAsync(provider);

        var request = Assert.Single(handler.RequestsTo("openai/deployments/prod-chat/chat/completions"));
        Assert.Equal("Bearer " + Token, request.Header("authorization"));
        Assert.Null(request.Header("api-key"));
        Assert.Equal("api-version=2024-10-21", request.Query);
        Assert.Equal(["https://cognitiveservices.azure.com/.default"], credentials.RequestedScopes);
    }

    [Fact]
    public async Task EntraId_ACustomScope_IsRequested()
    {
        var handler = Defaults();
        var credentials = new StaticTokenCredentialProvider(Token);
        using var provider = Create(handler, new InMemorySecretStore(), credentials, """{ "tokenScope": "https://ai.azure.com/.default" }""");

        await ChatAsync(provider);

        Assert.Equal(["https://ai.azure.com/.default"], credentials.RequestedScopes);
    }

    [Fact]
    public async Task Auto_PrefersAStoredKey_OverEntraId()
    {
        var handler = Defaults();
        var credentials = new StaticTokenCredentialProvider(Token);
        using var provider = Create(handler, SecretsWithKey(), credentials);

        await ChatAsync(provider);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(Key, request.Header("api-key"));
        Assert.Null(request.Header("authorization"));
        Assert.Empty(credentials.RequestedScopes);
    }

    [Fact]
    public async Task ApiKeyMode_IgnoresTheCredentialProvider()
    {
        var handler = Defaults();
        var credentials = new StaticTokenCredentialProvider(Token);
        using var provider = Create(handler, new InMemorySecretStore(), credentials, """{ "authentication": "apiKey" }""");

        await ChatAsync(provider);

        // No key is stored and the credential provider is not consulted: the request goes out unauthenticated.
        var request = Assert.Single(handler.Requests);
        Assert.Null(request.Header("authorization"));
        Assert.Null(request.Header("api-key"));
        Assert.Empty(credentials.RequestedScopes);
    }

    [Fact]
    public async Task AnUnauthenticatedRequest_Answered401_SaysACredentialIsRequired()
    {
        var handler = Defaults().Fail(ChatRoute, HttpStatusCode.Unauthorized, "AzureOpenAI", "error-401.json");
        using var provider = Create(handler, new InMemorySecretStore(), extensionJson: """{ "authentication": "apiKey" }""");

        var ex = await Assert.ThrowsAsync<ProviderException>(() => ChatAsync(provider));

        Assert.Contains("requires a credential", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EntraMode_IgnoresAStoredKey()
    {
        var handler = Defaults();
        using var provider = Create(handler, SecretsWithKey(), new StaticTokenCredentialProvider(Token), """{ "authentication": "entra" }""");

        await ChatAsync(provider);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("Bearer " + Token, request.Header("authorization"));
        Assert.Null(request.Header("api-key"));
    }

    [Fact]
    public async Task EntraId_AFailingCredentialProvider_IsReportedWithoutSendingAnything()
    {
        var handler = Defaults();
        using var provider = Create(handler, new InMemorySecretStore(), new ThrowingCredentials());

        var ex = await Assert.ThrowsAsync<ProviderException>(() => ChatAsync(provider));

        Assert.Equal(PillionErrorCode.ProviderUnavailable, ex.Code);
        Assert.Contains("Entra ID token", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("tenant-secret-detail", ex.Message, StringComparison.Ordinal);
        Assert.False(ex.IsProviderFault);
        Assert.Empty(handler.Requests);
    }

    // Errors.
    [Fact]
    public async Task Error401_IsProviderUnavailable_AndDoesNotRepeatTheServersText()
    {
        var handler = Defaults().Fail(ChatRoute, HttpStatusCode.Unauthorized, "AzureOpenAI", "error-401.json");
        using var provider = Create(handler, SecretsWithKey());

        var ex = await Assert.ThrowsAsync<ProviderException>(() => ChatAsync(provider));

        Assert.Equal(PillionErrorCode.ProviderUnavailable, ex.Code);
        Assert.Contains("rejected the credential", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("subscription key", ex.Message, StringComparison.Ordinal);
        Assert.False(ex.IsRetryable);
        Assert.False(ex.IsProviderFault);
    }

    [Fact]
    public async Task DeploymentNotFound_IsModelNotInstalled()
    {
        var handler = Defaults().Fail(ChatRoute, HttpStatusCode.NotFound, "AzureOpenAI", "error-deployment-not-found.json");
        using var provider = Create(handler, SecretsWithKey());

        var ex = await Assert.ThrowsAsync<ProviderException>(() => ChatAsync(provider));

        Assert.Equal(PillionErrorCode.ModelNotInstalled, ex.Code);
        Assert.Contains("prod-chat", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Error400_ContextLengthExceeded_IsContextOverflow()
    {
        var handler = Defaults().Fail(ChatRoute, HttpStatusCode.BadRequest, "AzureOpenAI", "error-context-length.json");
        using var provider = Create(handler, SecretsWithKey());

        var ex = await Assert.ThrowsAsync<ProviderException>(() => ChatAsync(provider));

        Assert.Equal(PillionErrorCode.ContextOverflow, ex.Code);
    }

    [Fact]
    public async Task Error400_ContentFilter_IsNotRetried()
    {
        var handler = Defaults().Fail(ChatRoute, HttpStatusCode.BadRequest, "AzureOpenAI", "error-content-filter.json");
        using var provider = Create(handler, SecretsWithKey());

        var ex = await Assert.ThrowsAsync<ProviderException>(() => ChatAsync(provider));

        Assert.Equal(PillionErrorCode.ProviderUnavailable, ex.Code);
        Assert.Contains("content management policy", ex.Message, StringComparison.Ordinal);
        Assert.False(ex.IsRetryable);
    }

    [Fact]
    public async Task Error429_IsTransient()
    {
        var handler = Defaults().Fail(ChatRoute, HttpStatusCode.TooManyRequests, "AzureOpenAI", "error-429.json");
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

        Assert.Contains("connection refused", ex.Message, StringComparison.Ordinal);
        Assert.True(ex.IsProviderFault);
    }

    // Test connection.
    [Fact]
    public async Task Probe_WithAnAcceptedKey_ReportsReachableAndAuthenticated()
    {
        var handler = Defaults();
        using var provider = Create(handler, SecretsWithKey());

        var detailed = await provider.ProbeDetailedAsync(CancellationToken.None);

        Assert.True(detailed.IsReachable);
        Assert.True(detailed.IsAuthenticated);
        Assert.Equal(2, detailed.ModelCount);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("api-version=2024-10-21", request.Query);
        Assert.Equal(Key, request.Header("api-key"));
    }

    [Fact]
    public async Task Probe_WithEntraId_SendsABearerToken()
    {
        var handler = Defaults();
        using var provider = Create(handler, new InMemorySecretStore(), new StaticTokenCredentialProvider(Token));

        var detailed = await provider.ProbeDetailedAsync(CancellationToken.None);

        Assert.True(detailed.IsAuthenticated);
        Assert.Equal("Bearer " + Token, handler.Requests.Single().Header("authorization"));
    }

    [Fact]
    public async Task Probe_WithARejectedKey_IsReachableButNotUsable()
    {
        var handler = Defaults().Fail("GET openai/models", HttpStatusCode.Unauthorized, "AzureOpenAI", "error-401.json");
        using var provider = Create(handler, SecretsWithKey());

        var detailed = await provider.ProbeDetailedAsync(CancellationToken.None);

        Assert.True(detailed.IsReachable);
        Assert.False(detailed.IsAuthenticated);
        Assert.False((await provider.ProbeAsync(CancellationToken.None)).IsReachable);
    }

    [Fact]
    public async Task Probe_WhenTheModelListIsNotAvailable_IsReachableButUnverified()
    {
        var handler = Defaults().FailText("GET openai/models", HttpStatusCode.NotFound, "{}", "application/json");
        using var provider = Create(handler, SecretsWithKey());

        var detailed = await provider.ProbeDetailedAsync(CancellationToken.None);
        var probe = await provider.ProbeAsync(CancellationToken.None);

        Assert.True(detailed.IsReachable);
        Assert.Null(detailed.IsAuthenticated);
        Assert.True(probe.IsReachable);
    }

    [Fact]
    public async Task Probe_WhenTheResourceIsUnreachable_DoesNotThrow()
    {
        var handler = Defaults();
        handler.RefuseConnections = true;
        using var provider = Create(handler, SecretsWithKey());

        var probe = await provider.ProbeAsync(CancellationToken.None);

        Assert.False(probe.IsReachable);
        Assert.Contains("refused", probe.Message, StringComparison.Ordinal);
    }

    private sealed class ThrowingCredentials : ITokenCredentialProvider
    {
        public ValueTask<string> GetTokenAsync(string scope, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("tenant-secret-detail");
    }
}
