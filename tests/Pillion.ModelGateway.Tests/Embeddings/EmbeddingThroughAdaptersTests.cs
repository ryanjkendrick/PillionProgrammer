using System.Net;
using Pillion.Common.Secrets;
using Pillion.Common.Settings;
using Pillion.Contracts;
using Pillion.ModelGateway.Providers.Gemini;
using Pillion.ModelGateway.Providers.Ollama;
using Pillion.ModelGateway.Tests.Ollama;
using Pillion.ModelGateway.Tests.Providers.Cloud;
using Pillion.ModelGateway.Tests.Providers.Gemini;

namespace Pillion.ModelGateway.Tests.Embeddings;

/// <summary>The gateway wired to the real adapters over fake HTTP: registry, router, resilience, Outbound log and the wire format together.</summary>
public class EmbeddingThroughAdaptersTests
{
    private static async Task<(ModelGateway Gateway, InMemoryOutboundLog Log, ProviderRegistry Registry)> OllamaGatewayAsync(FakeOllamaHandler handler, Action<PillionSettings>? configure = null)
    {
        var settings = new PillionSettings();
        settings.Providers.Add(new ProviderSettings { Id = "ollama", Kind = ProviderKind.Ollama });
        configure?.Invoke(settings);
        var options = new ModelGatewayOptions { RetryBaseDelay = TimeSpan.FromMilliseconds(1), RetryMaxDelay = TimeSpan.FromMilliseconds(5) };
        var registry = new ProviderRegistry(new StaticGatewaySettingsSource(settings), new InMemorySecretStore(), [new OllamaProviderFactory(() => handler)], ModelCatalog.Default);
        var inventory = new ModelInventory();
        var breakers = new CircuitBreakerRegistry(options);
        var log = new InMemoryOutboundLog(options);
        var router = new ModelRouter(registry, ModelCatalog.Default, inventory, new AlwaysReachable(), breakers, new NoCostCaps());
        var gateway = new ModelGateway(registry, router, inventory, ModelCatalog.Default, PromptTemplates.Default, new AutocompleteCache(options), breakers, new ConcurrencyLimiter(options), log, new AlwaysReachable(), new NoCostCaps(), new NoProgressSink(), options);
        await gateway.ListModelsAsync(CancellationToken.None);
        return (gateway, log, registry);
    }

    [Fact]
    public async Task Ollama_TheGatewayPicksTheInstalledEmbeddingModel_AndReturnsItsVectors()
    {
        var handler = FakeOllamaHandler.WithDefaults();
        var (gateway, log, registry) = await OllamaGatewayAsync(handler);
        using var _ = registry;

        var result = await gateway.EmbedAsync(new EmbeddingRequest { RequestId = "idx", Inputs = ["hello world", "int Add(int a, int b) => a + b;"], IncludeFloat32 = true }, CancellationToken.None);

        Assert.True(result.IsComplete);
        Assert.Equal(new EmbeddingModelInfo("ollama", "qwen3-embedding:0.6b", Dimensions: 6, TokenizerFamily: "qwen3-bpe", MaxInputTokens: 32768), result.Model);
        Assert.Equal(2, result.Vectors.Count);
        Assert.All(result.Vectors, v => Assert.Equal(1.0, Math.Sqrt(v!.Normalized!.Sum(x => (double)x * x)), 5));

        // The recorded vectors point the way the model said: the largest component of the first is the fourth, with a minus sign.
        var first = result.Vectors[0]!;
        Assert.Equal(127, Math.Abs(first.Quantized[3]));
        Assert.True(first.Quantized[3] < 0);

        var body = handler.BodiesFor("api/embed").Single();
        Assert.Equal("qwen3-embedding:0.6b", body["model"]!.GetValue<string>());
        var record = Assert.Single(log.GetRecent());
        Assert.Equal((TaskClass.Embedding, true, OutboundOutcome.Succeeded, "idx/b1"), (record.TaskClass, record.IsLocal, record.Outcome, record.RequestId));
    }

    [Fact]
    public async Task Ollama_AQueryGetsTheModelsInstruction_AndADocumentDoesNot()
    {
        var handler = FakeOllamaHandler.WithDefaults();
        handler.On("POST api/embed", (_, body) => FakeOllamaHandler.Respond(HttpStatusCode.OK, $$"""{"model":"qwen3-embedding:0.6b","embeddings":[{{string.Join(",", body!["input"]!.AsArray().Select(_ => "[1,2,3]"))}}]}"""));
        var (gateway, _, registry) = await OllamaGatewayAsync(handler);
        using var _ = registry;

        await gateway.EmbedAsync(new EmbeddingRequest { Inputs = ["where is the retry policy"], Purpose = EmbeddingPurpose.Query }, CancellationToken.None);
        await gateway.EmbedAsync(new EmbeddingRequest { Inputs = ["class RetryPolicy {}"] }, CancellationToken.None);

        var sent = handler.BodiesFor("api/embed").Select(b => b["input"]![0]!.GetValue<string>()).ToList();
        Assert.StartsWith("Instruct: ", sent[0], StringComparison.Ordinal);
        Assert.EndsWith("where is the retry policy", sent[0], StringComparison.Ordinal);
        Assert.Equal("class RetryPolicy {}", sent[1]);
    }

    [Fact]
    public async Task Ollama_WhenNoEmbeddingModelIsInstalled_TheErrorSaysHowToGetOne()
    {
        var handler = FakeOllamaHandler.WithDefaults();
        handler.Json("GET api/tags", """{"models":[{"name":"gemma4:e2b","model":"gemma4:e2b","digest":"d1"}]}""");
        var (gateway, _, registry) = await OllamaGatewayAsync(handler);
        using var _ = registry;

        var ex = await Assert.ThrowsAsync<PillionRpcException>(() => gateway.EmbedAsync(new EmbeddingRequest { Inputs = ["x"] }, CancellationToken.None));

        Assert.Equal(PillionErrorCode.ModelNotInstalled, ex.Error.Code);
        Assert.Contains("not installed", ex.Message, StringComparison.Ordinal);
        Assert.Empty(handler.BodiesFor("api/embed"));
    }

    [Fact]
    public async Task Ollama_AContextOverflowInOneBatch_IsReportedAsAFailure_NotAsALostIndex()
    {
        var handler = FakeOllamaHandler.WithDefaults();
        var calls = 0;
        handler.On("POST api/embed", (_, body) =>
            ++calls == 2
                ? FakeOllamaHandler.Respond(HttpStatusCode.BadRequest, FakeOllamaHandler.Fixture("error-embed-too-long.json"))
                : FakeOllamaHandler.Respond(HttpStatusCode.OK, $$"""{"model":"qwen3-embedding:0.6b","embeddings":[{{string.Join(",", body!["input"]!.AsArray().Select(_ => "[1,2,3]"))}}],"prompt_eval_count":1}"""));
        var (gateway, log, registry) = await OllamaGatewayAsync(handler);
        using var _ = registry;

        var result = await gateway.EmbedAsync(new EmbeddingRequest { Inputs = [.. Enumerable.Range(0, 40).Select(i => $"chunk {i}")], BatchSize = 20 }, CancellationToken.None);

        var failure = Assert.Single(result.Failures);
        Assert.Equal(PillionErrorCode.ContextOverflow, failure.Error.Code);
        Assert.Equal((20, 20), (failure.Start, failure.Count));
        Assert.All(result.Vectors.Take(20), v => Assert.NotNull(v));
        Assert.All(result.Vectors.Skip(20), v => Assert.Null(v));
        Assert.Equal([OutboundOutcome.Succeeded, OutboundOutcome.Failed], log.GetRecent().Select(r => r.Outcome));
    }

    [Fact]
    public async Task Ollama_AnEndpointThatIsNotThisMachine_IsACloudBoundary_SoTheSwitchApplies()
    {
        var handler = FakeOllamaHandler.WithDefaults();
        var (gateway, log, registry) = await OllamaGatewayAsync(handler, s => s.Providers[0].Endpoint = "http://gpu-box.example:11434");
        using var _ = registry;

        // A remote Ollama server is treated as cloud: it is not a local candidate, and with the Embedding switch off a cloud profile is refused too.
        var auto = await Assert.ThrowsAsync<PillionRpcException>(() => gateway.EmbedAsync(new EmbeddingRequest { Inputs = ["x"] }, CancellationToken.None));
        var cloudProfile = await Assert.ThrowsAsync<PillionRpcException>(() => gateway.EmbedAsync(new EmbeddingRequest { Inputs = ["x"], Profile = Profile.Fast }, CancellationToken.None));

        Assert.Equal(PillionErrorCode.PolicyDenied, auto.Error.Code);
        Assert.Contains("cloud switch for Embedding is off", auto.Message, StringComparison.Ordinal);
        Assert.Equal(PillionErrorCode.PolicyDenied, cloudProfile.Error.Code);
        Assert.Empty(handler.BodiesFor("api/embed"));
        Assert.Empty(log.GetRecent());
    }

    // Gemini through the gateway (issue #61).
    [Fact]
    public async Task Gemini_ThroughTheGateway_WithTheSwitchOn_UsesBatchEmbedContents_AndIsRecordedAsCloud()
    {
        var handler = GeminiProviderTests.Defaults()
            .Serve("POST v1beta/models/gemini-embedding-001:batchEmbedContents", "Gemini", "batch-embed-contents.json");
        using var rig = new CloudRig(
            handler,
            GeminiProviderTests.Settings("gem"),
            (secrets, h) => new GeminiProviderFactory(secrets, () => h),
            configure: s =>
            {
                s.CloudSwitches.Embedding = true;
                s.Routing.Tasks[TaskClass.Embedding] = Profile.Fast;
            });
        await rig.Gateway.ListModelsAsync(CancellationToken.None);

        var result = await rig.Gateway.EmbedAsync(new EmbeddingRequest { RequestId = "g1", Inputs = ["a", "b", "c"] }, CancellationToken.None);

        Assert.True(result.IsComplete);
        Assert.Equal(new EmbeddingModelInfo("gem", "gemini-embedding-001", Dimensions: 6, TokenizerFamily: "gemini", MaxInputTokens: 2048), result.Model);
        var record = Assert.Single(rig.OutboundLog.GetRecent());
        Assert.False(record.IsLocal);
        Assert.Equal(TaskClass.Embedding, record.TaskClass);
        Assert.Equal("g1/b1", record.RequestId);
        Assert.Equal("RETRIEVAL_DOCUMENT", rig.Handler.RequestsTo("v1beta/models/gemini-embedding-001:batchEmbedContents").Single().Body!["requests"]![0]!["taskType"]!.GetValue<string>());
    }

    [Fact]
    public async Task Gemini_ThroughTheGateway_WithTheSwitchOff_SendsNothing()
    {
        var handler = GeminiProviderTests.Defaults()
            .Serve("POST v1beta/models/gemini-embedding-001:batchEmbedContents", "Gemini", "batch-embed-contents.json");
        using var rig = new CloudRig(
            handler,
            GeminiProviderTests.Settings("gem"),
            (secrets, h) => new GeminiProviderFactory(secrets, () => h),
            configure: s => s.Routing.Tasks[TaskClass.Embedding] = Profile.Fast);
        await rig.Gateway.ListModelsAsync(CancellationToken.None);
        handler.Requests.Clear();

        var ex = await Assert.ThrowsAsync<PillionRpcException>(() => rig.Gateway.EmbedAsync(new EmbeddingRequest { Inputs = ["a", "b", "c"] }, CancellationToken.None));
        var explicitModel = await Assert.ThrowsAsync<PillionRpcException>(() => rig.Gateway.EmbedAsync(new EmbeddingRequest { Inputs = ["a"], ModelId = "gem/gemini-embedding-001" }, CancellationToken.None));

        Assert.Equal(PillionErrorCode.PolicyDenied, ex.Error.Code);
        Assert.Equal(PillionErrorCode.PolicyDenied, explicitModel.Error.Code);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Gemini_ThroughTheGateway_UnderLocalOnly_SendsNothing_EvenWithTheSwitchOn()
    {
        var handler = GeminiProviderTests.Defaults()
            .Serve("POST v1beta/models/gemini-embedding-001:batchEmbedContents", "Gemini", "batch-embed-contents.json");
        using var rig = new CloudRig(
            handler,
            GeminiProviderTests.Settings("gem"),
            (secrets, h) => new GeminiProviderFactory(secrets, () => h),
            configure: s =>
            {
                s.CloudSwitches.Embedding = true;
                s.Privacy.LocalOnly = true;
            });

        var ex = await Assert.ThrowsAsync<PillionRpcException>(() => rig.Gateway.EmbedAsync(new EmbeddingRequest { Inputs = ["a"], ModelId = "gem/gemini-embedding-001" }, CancellationToken.None));

        Assert.Equal(PillionErrorCode.PolicyDenied, ex.Error.Code);
        Assert.Empty(handler.Requests);
    }
}
