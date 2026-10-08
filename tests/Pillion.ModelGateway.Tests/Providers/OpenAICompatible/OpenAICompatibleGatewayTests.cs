using System.Net;
using System.Text;
using System.Text.Json;
using Pillion.Common.Secrets;
using Pillion.Common.Settings;
using Pillion.Contracts;
using Pillion.ModelGateway.Providers.OpenAICompatible;
using Pillion.ModelGateway.Tests.TestSupport;

namespace Pillion.ModelGateway.Tests.Providers.OpenAICompatible;

/// <summary>The OpenAI-compatible provider behind the real gateway, router, registry and redaction.</summary>
public sealed class OpenAICompatibleGatewayTests
{
    private sealed class Rig : IDisposable
    {
        public Rig(FakeOpenAIHandler handler, string? key = OpenAICompatibleProviderTests.Key, string endpoint = "https://gateway.example/v1", Action<PillionSettings>? configure = null)
        {
            Handler = handler;
            Settings.Providers.Add(new ProviderSettings { Id = "cloudgw", Kind = ProviderKind.OpenAiCompatible, Endpoint = endpoint });
            if (key is not null)
            {
                Secrets.Set("cloudgw", key);
            }

            configure?.Invoke(Settings);
            var options = new ModelGatewayOptions { RetryBaseDelay = TimeSpan.FromMilliseconds(1), RetryMaxDelay = TimeSpan.FromMilliseconds(5) };
            Source = new StaticGatewaySettingsSource(Settings);
            Registry = new ProviderRegistry(Source, Secrets, [new OpenAICompatibleProviderFactory(Secrets, _ => handler)], ModelCatalog.Default);
            var inventory = new ModelInventory();
            var breakers = new CircuitBreakerRegistry(options);
            OutboundLog = new InMemoryOutboundLog(options);
            var router = new ModelRouter(Registry, ModelCatalog.Default, inventory, new AlwaysReachable(), breakers, new NoCostCaps());
            Gateway = new ModelGateway(
                Registry,
                router,
                inventory,
                ModelCatalog.Default,
                PromptTemplates.Default,
                new AutocompleteCache(options),
                breakers,
                new ConcurrencyLimiter(options),
                OutboundLog,
                new AlwaysReachable(),
                new NoCostCaps(),
                new NoProgressSink(),
                options);
        }

        public FakeOpenAIHandler Handler { get; }

        public PillionSettings Settings { get; } = new();

        public InMemorySecretStore Secrets { get; } = new();

        public StaticGatewaySettingsSource Source { get; }

        public ProviderRegistry Registry { get; }

        public InMemoryOutboundLog OutboundLog { get; }

        public ModelGateway Gateway { get; }

        public Task<List<ChatChunk>> ChatAsync(string text, string? modelId = null) =>
            Gateway.ChatAsync(GatewayHarness.ChatRequest(text, modelId), TaskClass.Chat, CancellationToken.None).CollectAsync();

        public void Dispose() => Registry.Dispose();
    }

    /// <summary>Builds a server-sent-event body that streams the given pieces and ends with <c>[DONE]</c>.</summary>
    private static string Sse(params string[] pieces)
    {
        var builder = new StringBuilder();
        foreach (var piece in pieces)
        {
            builder.Append("data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":").Append(JsonSerializer.Serialize(piece)).Append("},\"finish_reason\":null}]}\n\n");
        }

        builder.Append("data: {\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n");
        builder.Append("data: {\"choices\":[],\"usage\":{\"prompt_tokens\":30,\"completion_tokens\":7,\"total_tokens\":37}}\n\n");
        builder.Append("data: [DONE]\n\n");
        return builder.ToString();
    }

    [Fact]
    public async Task Chat_IsRoutedToTheProvider_WhenTheCloudSwitchIsOnAndAKeyExists()
    {
        using var rig = new Rig(FakeOpenAIHandler.WithDefaults());

        var chunks = await rig.ChatAsync("Say hello");

        Assert.Equal("Hello from Pillion", string.Concat(chunks.Select(c => c.Text)));
        Assert.All(chunks, c => Assert.Equal("cloudgw", c.ProviderId));
        Assert.All(chunks, c => Assert.Equal("gpt-6.1-sol", c.ModelId));
        Assert.True(chunks[^1].IsFinal);
        Assert.Null(chunks[^1].Error);
        Assert.Equal(21, chunks[^1].PromptTokens);
        Assert.Equal(3, chunks[^1].CompletionTokens);

        var request = Assert.Single(rig.Handler.RequestsTo("v1/chat/completions"));
        Assert.Equal("Bearer " + OpenAICompatibleProviderTests.Key, request.Authorization);
        Assert.Equal("gpt-6.1-sol", request.Body!["model"]!.GetValue<string>());
        var record = Assert.Single(rig.OutboundLog.GetRecent());
        Assert.False(record.IsLocal);
        Assert.Equal("cloudgw", record.ProviderId);
        Assert.Equal(OutboundOutcome.Succeeded, record.Outcome);
    }

    [Fact]
    public async Task Chat_WithAnExplicitModel_UsesIt()
    {
        using var rig = new Rig(FakeOpenAIHandler.WithDefaults());

        var chunks = await rig.ChatAsync("Say hello", "cloudgw/gpt-6-astra");

        Assert.All(chunks, c => Assert.Equal("gpt-6-astra", c.ModelId));
        Assert.Equal("gpt-6-astra", rig.Handler.RequestsTo("v1/chat/completions").Single().Body!["model"]!.GetValue<string>());
    }

    [Fact]
    public async Task Provider_WithoutAKey_IsHiddenFromRouting_AndNothingIsSent()
    {
        using var rig = new Rig(FakeOpenAIHandler.WithDefaults(), key: null);

        await Assert.ThrowsAsync<PillionRpcException>(() => rig.ChatAsync("Say hello"));

        var snapshot = rig.Registry.GetSnapshot();
        Assert.Empty(snapshot.Providers);
        Assert.Contains("No API key", Assert.Single(snapshot.Hidden).Reason, StringComparison.Ordinal);
        Assert.Empty(rig.Handler.Requests);
        var test = await rig.Gateway.TestProviderAsync("cloudgw", CancellationToken.None);
        Assert.False(test.IsSuccess);
        Assert.Contains("No API key", test.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Provider_AppearsAsSoonAsTheKeyIsStored()
    {
        using var rig = new Rig(FakeOpenAIHandler.WithDefaults(), key: null);
        await Assert.ThrowsAsync<PillionRpcException>(() => rig.ChatAsync("Say hello"));

        rig.Secrets.Set("cloudgw", OpenAICompatibleProviderTests.Key);
        rig.Source.Update(rig.Settings);
        var chunks = await rig.ChatAsync("Say hello");

        Assert.Equal("Hello from Pillion", string.Concat(chunks.Select(c => c.Text)));
    }

    [Fact]
    public async Task Chat_WithTheCloudSwitchOff_IsRefusedByPolicy_AndNothingIsSent()
    {
        using var rig = new Rig(FakeOpenAIHandler.WithDefaults(), configure: settings => settings.CloudSwitches.Chat = false);

        var ex = await Assert.ThrowsAsync<PillionRpcException>(() => rig.ChatAsync("Say hello"));

        Assert.Equal(PillionErrorCode.PolicyDenied, ex.Error.Code);
        Assert.Empty(rig.Handler.Requests);
    }

    [Fact]
    public async Task Chat_ALoopbackServerWithoutAKey_IsRoutedAsLocal_WithNoAuthorizationHeader()
    {
        using var rig = new Rig(
            FakeOpenAIHandler.WithDefaults(),
            key: null,
            endpoint: "http://localhost:1234/v1",
            configure: settings => settings.Providers[0].Models = ["qwen2.5-coder-7b-instruct"]);

        var chunks = await rig.ChatAsync("Say hello");

        Assert.All(chunks, c => Assert.Equal("qwen2.5-coder-7b-instruct", c.ModelId));
        var request = Assert.Single(rig.Handler.RequestsTo("v1/chat/completions"));
        Assert.Null(request.Authorization);
        Assert.True(Assert.Single(rig.OutboundLog.GetRecent()).IsLocal);
    }

    [Fact]
    public async Task Redaction_ACloudBoundRequest_SendsPlaceholders_AndTheReplyIsRestored()
    {
        var secret = TestSecrets.AwsAccessKey();
        var handler = FakeOpenAIHandler.WithDefaults();
        handler.On("POST v1/chat/completions", request =>
        {
            // The server echoes what it was sent, split in the middle of the placeholder.
            var sent = request.Body!["messages"]!.AsArray().Last()!["content"]!.GetValue<string>();
            var cut = sent.IndexOf("<redacted:", StringComparison.Ordinal) + 8;
            return FakeOpenAIHandler.Respond(HttpStatusCode.OK, Sse("You sent: ", sent[..cut], sent[cut..]), "text/event-stream");
        });
        using var rig = new Rig(handler);

        var chunks = await rig.ChatAsync($"my key is {secret} ok");

        var request = Assert.Single(handler.RequestsTo("v1/chat/completions"));
        var wire = request.Body!["messages"]!.AsArray().Last()!["content"]!.GetValue<string>();
        Assert.DoesNotContain(secret, request.Body.ToJsonString(), StringComparison.Ordinal);
        Assert.Equal("my key is <redacted:aws-access-key> ok", wire);
        Assert.Equal($"You sent: my key is {secret} ok", string.Concat(chunks.Select(c => c.Text)));
        Assert.Equal(1, Assert.Single(rig.OutboundLog.GetRecent()).RedactionCount);
    }

    [Fact]
    public async Task Chat_A429ThenSuccess_IsRetriedByTheGateway()
    {
        var handler = FakeOpenAIHandler.WithDefaults();
        var calls = 0;
        handler.On("POST v1/chat/completions", _ => Interlocked.Increment(ref calls) == 1
            ? FakeOpenAIHandler.Respond(HttpStatusCode.TooManyRequests, FakeOpenAIHandler.Fixture("error-429.json"))
            : FakeOpenAIHandler.Respond(HttpStatusCode.OK, FakeOpenAIHandler.Fixture("chat-stream.sse"), "text/event-stream"));
        using var rig = new Rig(handler);

        var chunks = await rig.ChatAsync("Say hello");

        Assert.Equal("Hello from Pillion", string.Concat(chunks.Select(c => c.Text)));
        Assert.Equal(2, handler.RequestsTo("v1/chat/completions").Count());
        Assert.Null(chunks[^1].Error);
    }

    [Fact]
    public async Task Chat_AlwaysRateLimited_FailsAfterTheConfiguredRetries()
    {
        var handler = new FakeOpenAIHandler();
        handler.Error("POST v1/chat/completions", HttpStatusCode.ServiceUnavailable, "overloaded", "text/plain");
        using var rig = new Rig(handler);

        var ex = await Assert.ThrowsAsync<PillionRpcException>(() => rig.ChatAsync("Say hello"));

        Assert.Equal(PillionErrorCode.ProviderUnavailable, ex.Error.Code);
        // The gateway tries the Balanced model and then the Fast one; each gets one try and two retries.
        var byModel = handler.RequestsTo("v1/chat/completions").GroupBy(r => r.Body!["model"]!.GetValue<string>()).ToList();
        Assert.Equal(["gpt-6.1-sol", "gpt-6-luna"], byModel.Select(g => g.Key));
        Assert.All(byModel, g => Assert.Equal(3, g.Count()));
        Assert.All(rig.OutboundLog.GetRecent(), r => Assert.Equal(OutboundOutcome.Failed, r.Outcome));
    }

    [Fact]
    public async Task Chat_ARejectedKey_IsNotRetried_AndTheMessageDoesNotContainTheKey()
    {
        var handler = new FakeOpenAIHandler();
        handler.Error("POST v1/chat/completions", HttpStatusCode.Unauthorized, FakeOpenAIHandler.Fixture("error-401.json"));
        using var rig = new Rig(handler);

        var ex = await Assert.ThrowsAsync<PillionRpcException>(() => rig.ChatAsync("Say hello"));

        Assert.Equal(PillionErrorCode.ProviderUnavailable, ex.Error.Code);
        Assert.Contains("rejected the API key", ex.Error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(OpenAICompatibleProviderTests.Key, ex.Error.Message, StringComparison.Ordinal);

        // Never retried on the same model; the gateway still offers the Fast model as the next candidate.
        Assert.Equal(["gpt-6.1-sol", "gpt-6-luna"], handler.Requests.Select(r => r.Body!["model"]!.GetValue<string>()));
    }

    [Fact]
    public async Task Chat_ContextLengthError_ReachesTheClientAsContextOverflow()
    {
        var handler = new FakeOpenAIHandler();
        handler.Error("POST v1/chat/completions", HttpStatusCode.BadRequest, FakeOpenAIHandler.Fixture("error-context-length.json"));
        using var rig = new Rig(handler);

        var ex = await Assert.ThrowsAsync<PillionRpcException>(() => rig.ChatAsync("Say hello"));

        Assert.Equal(PillionErrorCode.ContextOverflow, ex.Error.Code);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Chat_AnUnknownModel_ReachesTheClientAsModelNotInstalled()
    {
        var handler = new FakeOpenAIHandler();
        handler.Error("POST v1/chat/completions", HttpStatusCode.NotFound, FakeOpenAIHandler.Fixture("error-model-not-found.json"));
        using var rig = new Rig(handler);

        var ex = await Assert.ThrowsAsync<PillionRpcException>(() => rig.ChatAsync("Say hello", "cloudgw/gpt-9-imaginary"));

        Assert.Equal(PillionErrorCode.ModelNotInstalled, ex.Error.Code);
    }

    [Fact]
    public async Task ListModels_ThroughTheGateway_NamesTheProviderOnEveryModel()
    {
        using var rig = new Rig(FakeOpenAIHandler.WithDefaults());

        var models = await rig.Gateway.ListModelsAsync(CancellationToken.None);

        Assert.Contains(models, m => m.Id == "cloudgw/gpt-6.1-sol" && m.ProviderId == "cloudgw" && !m.IsLocal && m.IsAvailable);
        Assert.Contains(models, m => m.Id == "cloudgw/text-embedding-3-small" && m.Capabilities == ModelCapabilities.Embeddings);
    }

    [Fact]
    public async Task TestProvider_ThroughTheGateway_ReportsTheProbe()
    {
        using var rig = new Rig(FakeOpenAIHandler.WithDefaults());

        var result = await rig.Gateway.TestProviderAsync("cloudgw", CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Contains("accepted", result.Message, StringComparison.Ordinal);
    }
}
