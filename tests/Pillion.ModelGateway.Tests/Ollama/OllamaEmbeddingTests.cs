using System.Net;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Pillion.Common.Hardware;
using Pillion.Common.Settings;
using Pillion.Contracts;
using Pillion.ModelGateway.Providers.Ollama;
using Pillion.ModelGateway.Tests.TestSupport;

namespace Pillion.ModelGateway.Tests.Ollama;

/// <summary>The Ollama embedding adapter (<c>/api/embed</c>) against recorded responses, and how it shares the GPU with the other models.</summary>
public class OllamaEmbeddingTests
{
    private const string Qwen = "qwen3-embedding:0.6b";
    private const string Coder = "qwen2.5-coder:7b";

    private static readonly string[] Pair = ["hello world", "int Add(int a, int b) => a + b;"];

    private static OllamaProvider Create(FakeOllamaHandler handler, string? extensionJson = null)
    {
        var settings = new ProviderSettings { Id = "ollama", Kind = ProviderKind.Ollama };
        if (extensionJson is not null)
        {
            settings.ExtensionData = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(extensionJson);
        }

        return (OllamaProvider)new OllamaProviderFactory(() => handler).Create(
            settings,
            new ProviderCreationContext(null, ModelCatalog.Default, NullLoggerFactory.Instance, null));
    }

    private static EmbeddingGenerationOptions With(params (string Key, object Value)[] properties)
    {
        var options = new EmbeddingGenerationOptions { AdditionalProperties = [] };
        foreach (var (key, value) in properties)
        {
            options.AdditionalProperties[key] = value;
        }

        return options;
    }

    // The wire format.
    [Fact]
    public async Task Generate_PostsTheBatchToApiEmbed_AndReadsOneVectorPerText()
    {
        var handler = FakeOllamaHandler.WithDefaults();
        using var provider = Create(handler);

        var result = await provider.GetEmbeddingGenerator(Qwen)!.GenerateAsync(Pair, cancellationToken: CancellationToken.None);

        Assert.Equal(2, result.Count);
        Assert.Equal(6, result[0].Vector.Length);
        Assert.Equal(-0.015692139f, result[0].Vector.Span[0]);
        Assert.Equal(0.020066675f, result[1].Vector.Span[5]);
        Assert.All(result, e => Assert.Equal(Qwen, e.ModelId));
        Assert.Equal(17, result.Usage!.InputTokenCount);

        Assert.Equal(["api/version", "api/ps", "api/embed"], handler.Requests.Select(r => r.Path));
        var body = handler.BodiesFor("api/embed").Single();
        Assert.Equal(Qwen, body["model"]!.GetValue<string>());
        Assert.Equal(Pair, body["input"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Null(body["dimensions"]);
    }

    [Fact]
    public async Task Generate_AlwaysSendsASmallContext_AndTheEmbeddingKeepAlive()
    {
        var handler = FakeOllamaHandler.WithDefaults();
        using var provider = Create(handler);

        await provider.GetEmbeddingGenerator(Qwen)!.GenerateAsync(Pair);

        var body = handler.BodiesFor("api/embed").Single();
        Assert.Equal(2048, body["options"]!["num_ctx"]!.GetValue<int>());
        Assert.Null(body["options"]!["num_gpu"]); // The GPU is Ollama's choice unless the residency rules say the CPU.
        Assert.Equal("5m", body["keep_alive"]!.GetValue<string>());
    }

    [Fact]
    public async Task Generate_ALongTextRaisesTheContext_ToThePowerOfTwoThatHoldsIt()
    {
        var handler = FakeOllamaHandler.WithDefaults();
        using var provider = Create(handler);

        await provider.GetEmbeddingGenerator(Qwen)!.GenerateAsync([new string('x', 12_000)]); // About 3,000 tokens.

        Assert.Equal(4096, handler.BodiesFor("api/embed").Single()["options"]!["num_ctx"]!.GetValue<int>());
    }

    [Fact]
    public async Task Generate_KeepAliveAndContext_CanBeOverriddenPerRequest_AndTheProviderEntryChangesTheDefault()
    {
        var handler = FakeOllamaHandler.WithDefaults();
        using var provider = Create(handler, """{ "embeddingKeepAlive": "1h" }""");
        using var other = Create(handler);

        await provider.GetEmbeddingGenerator(Qwen)!.GenerateAsync(Pair);
        await other.GetEmbeddingGenerator(Qwen)!.GenerateAsync(Pair, With((GatewayOptionKeys.KeepAlive, "-1"), (GatewayOptionKeys.ContextSize, 8192)));

        var bodies = handler.BodiesFor("api/embed").ToList();
        Assert.Equal("1h", bodies[0]["keep_alive"]!.GetValue<string>());
        Assert.Equal(-1, bodies[1]["keep_alive"]!.GetValue<int>());
        Assert.Equal(8192, bodies[1]["options"]!["num_ctx"]!.GetValue<int>());
    }

    [Fact]
    public async Task Generate_DimensionsAndAnExplicitModel_GoIntoTheRequest()
    {
        var handler = FakeOllamaHandler.WithDefaults();
        using var provider = Create(handler);

        await provider.GetEmbeddingGenerator("another-embedder")!.GenerateAsync(Pair, new EmbeddingGenerationOptions { ModelId = Qwen, Dimensions = 256 });

        var body = handler.BodiesFor("api/embed").Single();
        Assert.Equal(Qwen, body["model"]!.GetValue<string>());
        Assert.Equal(256, body["dimensions"]!.GetValue<int>());
    }

    [Fact]
    public async Task Generate_WithNoTexts_SendsNothing()
    {
        var handler = FakeOllamaHandler.WithDefaults();
        using var provider = Create(handler);

        var result = await provider.GetEmbeddingGenerator(Qwen)!.GenerateAsync([]);

        Assert.Empty(result);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task GetEmbeddingGenerator_IsNullForAModelKnownNotToEmbed()
    {
        using var provider = Create(FakeOllamaHandler.WithDefaults());
        await provider.ListModelsAsync(CancellationToken.None);

        Assert.NotNull(provider.GetEmbeddingGenerator(Qwen));
        Assert.Null(provider.GetEmbeddingGenerator("gemma4:e2b"));
        Assert.Null(provider.GetEmbeddingGenerator("qwen2.5-coder:1.5b"));
        Assert.NotNull(provider.GetEmbeddingGenerator("a-model-not-listed-yet"));
        Assert.Throws<ArgumentException>(() => provider.GetEmbeddingGenerator(" "));
    }

    // Errors.
    [Fact]
    public async Task Generate_AModelThatIsNotInstalled_IsModelNotInstalled_WithThePullCommand()
    {
        var handler = FakeOllamaHandler.WithDefaults();
        handler.On("POST api/embed", (_, _) => FakeOllamaHandler.Respond(HttpStatusCode.NotFound, FakeOllamaHandler.Fixture("error-model-not-found.json")));
        using var provider = Create(handler);

        var ex = await Assert.ThrowsAsync<ProviderException>(() => provider.GetEmbeddingGenerator("nomic-embed-text")!.GenerateAsync(Pair));

        Assert.Equal(PillionErrorCode.ModelNotInstalled, ex.Code);
        Assert.Contains("ollama pull nomic-embed-text", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Generate_ATextBeyondTheContext_IsContextOverflow()
    {
        var handler = FakeOllamaHandler.WithDefaults();
        handler.On("POST api/embed", (_, _) => FakeOllamaHandler.Respond(HttpStatusCode.BadRequest, FakeOllamaHandler.Fixture("error-embed-too-long.json")));
        using var provider = Create(handler);

        var ex = await Assert.ThrowsAsync<ProviderException>(() => provider.GetEmbeddingGenerator(Qwen)!.GenerateAsync(Pair));

        Assert.Equal(PillionErrorCode.ContextOverflow, ex.Code);
        Assert.False(ex.IsProviderFault);
    }

    [Fact]
    public async Task Generate_WhenOllamaIsNotRunning_SaysSo()
    {
        var handler = FakeOllamaHandler.WithDefaults();
        handler.RefuseConnections = true;
        using var provider = Create(handler);

        var ex = await Assert.ThrowsAsync<ProviderException>(() => provider.GetEmbeddingGenerator(Qwen)!.GenerateAsync(Pair));

        Assert.Contains("not running", ex.Message, StringComparison.Ordinal);
        Assert.True(ex.IsProviderFault);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"embeddings":"nope"}""")]
    [InlineData("""{"embeddings":[["a"]]}""")]
    public async Task Generate_AnUnreadableAnswer_IsAProviderError(string body)
    {
        var handler = FakeOllamaHandler.WithDefaults();
        handler.Json("POST api/embed", body);
        using var provider = Create(handler);

        var ex = await Assert.ThrowsAsync<ProviderException>(() => provider.GetEmbeddingGenerator(Qwen)!.GenerateAsync(Pair));

        Assert.Equal(PillionErrorCode.ProviderUnavailable, ex.Code);
    }

    // Sharing the GPU.
    private sealed class Rig : IDisposable
    {
        public Rig(GpuTier tier, string? extensionJson = null)
        {
            Server = new FakeOllamaServer(Clock);
            var settings = new ProviderSettings { Id = "ollama", Kind = ProviderKind.Ollama };
            if (extensionJson is not null)
            {
                settings.ExtensionData = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(extensionJson);
            }

            var runtime = new ProviderRuntime(Progress, () => tier, _ => null, Clock, Map);
            Provider = (OllamaProvider)new OllamaProviderFactory(() => Server.Handler).Create(
                settings,
                new ProviderCreationContext(null, ModelCatalog.Default, NullLoggerFactory.Instance, Coder, runtime));
        }

        public ManualClock Clock { get; } = new();

        public RecordingProgressSink Progress { get; } = new();

        public LocalResidencyMap Map { get; } = new();

        public FakeOllamaServer Server { get; }

        public OllamaProvider Provider { get; }

        public Task<GeneratedEmbeddings<Embedding<float>>> EmbedAsync(string model = Qwen, params string[] texts) =>
            Provider.GetEmbeddingGenerator(model)!.GenerateAsync(texts.Length == 0 ? Pair : texts, With((GatewayOptionKeys.RequestId, "req-1")));

        public void Dispose() => Provider.Dispose();
    }

    [Fact]
    public async Task OnAnEmptyGpu_TheEmbeddingModelLoadsThereAtASmallContext()
    {
        using var rig = new Rig(GpuTier.Gpu8GB);

        await rig.EmbedAsync();

        Assert.Equal(["embed@2048/5m"], rig.Server.Transcript());
        var runner = Assert.Single(rig.Server.Runners);
        Assert.Equal((Qwen, 2048, false), (runner.Model, runner.Context, runner.OnCpu));
    }

    [Fact]
    public async Task OnThe8GbTier_WithTheCompletionModelResidentAtItsRecommendedContext_TheEmbeddingModelStillFitsOnTheGpu()
    {
        using var rig = new Rig(GpuTier.Gpu8GB);
        rig.Server.Load(Coder, 4096, "-1"); // 4,524 MiB of the 7,168 the tier offers; the embedding model needs 2,154.

        await rig.EmbedAsync();

        Assert.Equal(["embed@2048/5m"], rig.Server.Transcript());
        Assert.Equal([Coder, Qwen], rig.Server.Runners.Select(r => r.Model));
        Assert.All(rig.Server.Runners, r => Assert.False(r.OnCpu));
    }

    [Fact]
    public async Task WhenTheResidentModelsLeaveNoRoom_TheEmbeddingModelRunsOnTheCpu_AndNothingIsEvicted()
    {
        using var rig = new Rig(GpuTier.Gpu8GB);
        rig.Server.Load(Coder, 16384, "-1"); // 5,196 MiB: 1,972 MiB are left and the embedding model needs 2,154.

        await rig.EmbedAsync();
        await rig.EmbedAsync();

        // Both requests ask for the CPU; the coder model is never asked to unload and keeps its pinned configuration.
        Assert.Equal(["embed@2048/5m+cpu", "embed@2048/5m+cpu"], rig.Server.Transcript());
        var coder = Assert.Single(rig.Server.Runners, r => r.Model == Coder);
        Assert.Equal((16384, "-1", false), (coder.Context, coder.KeepAlive, coder.OnCpu));
        Assert.True(Assert.Single(rig.Server.Runners, r => r.Model == Qwen).OnCpu);
        Assert.Contains(rig.Progress.Reports, p => p.Message.Contains("runs on the CPU", StringComparison.Ordinal) && p.RequestId == "req-1");
    }

    [Theory]
    [InlineData(GpuTier.Gpu12GB, 4096)]
    [InlineData(GpuTier.Gpu16GB, 4096)]
    [InlineData(GpuTier.Gpu24GBPlus, 8192)]
    public async Task OnALargerTier_TheSameResidentCompletionModelLeavesRoom_AndTheCatalogsContextForTheTierIsUsed(GpuTier tier, int context)
    {
        using var rig = new Rig(tier);
        rig.Server.Load(Coder, 16384, "-1");

        await rig.EmbedAsync();

        Assert.Equal([$"embed@{context}/5m"], rig.Server.Transcript());
    }

    [Fact]
    public async Task OnACpuOnlyMachine_ThereIsNoGpuToProtect_SoTheRequestIsSentAsItIs()
    {
        using var rig = new Rig(GpuTier.CpuOnly);
        rig.Server.Load(Coder, 16384, "-1");

        await rig.EmbedAsync();

        Assert.Equal(["embed@2048/5m"], rig.Server.Transcript());
    }

    [Fact]
    public async Task AModelTheCatalogCannotSizeIsKeptOffTheGpu_WhileAnotherModelIsResident_ButNotOtherwise()
    {
        using var alone = new Rig(GpuTier.Gpu8GB);
        await alone.EmbedAsync("mxbai-embed-large");
        using var shared = new Rig(GpuTier.Gpu8GB);
        shared.Server.Load(Coder, 4096, "-1");
        await shared.EmbedAsync("mxbai-embed-large");

        Assert.Equal(["embed@2048/5m"], alone.Server.Transcript());
        Assert.Equal(["embed@2048/5m+cpu"], shared.Server.Transcript());
    }

    [Fact]
    public async Task AResidentEmbeddingModelThatIsBigEnough_IsReused_WhateverContextItHas()
    {
        using var rig = new Rig(GpuTier.Gpu8GB);
        rig.Server.Load(Qwen, 8192, "5m");

        await rig.EmbedAsync();

        Assert.Equal(["embed@8192/5m"], rig.Server.Transcript());
        Assert.Single(rig.Server.Runners);
    }

    [Fact]
    public async Task AResidentEmbeddingModelThatIsTooSmall_IsUnloadedThroughApiEmbed_AndLoadedAgainAtTheNeededSize()
    {
        using var rig = new Rig(GpuTier.Gpu8GB);
        rig.Server.Load(Qwen, 2048, "5m");

        await rig.EmbedAsync(Qwen, new string('x', 12_000));

        Assert.Equal(["unload-embed", "embed@4096/5m"], rig.Server.Transcript());
        var runner = Assert.Single(rig.Server.Runners);
        Assert.Equal(4096, runner.Context);
    }

    [Fact]
    public async Task OneResidentConfigurationPerModel_ConcurrentBatchesDoNotLoadItTwice()
    {
        using var rig = new Rig(GpuTier.Gpu8GB);

        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => rig.EmbedAsync()));

        Assert.Single(rig.Server.Runners);
        Assert.All(rig.Server.Transcript(), line => Assert.Equal("embed@2048/5m", line));
    }

    [Fact]
    public async Task TheEmbeddingModel_AppearsInTheResidencyMap_ForTheStatusBar()
    {
        using var rig = new Rig(GpuTier.Gpu8GB);
        rig.Server.Load(Coder, 4096, "-1");
        await rig.EmbedAsync();

        var resident = await rig.Map.RefreshAsync(force: true, CancellationToken.None);

        Assert.Contains(rig.Map.GetSnapshot().Models, m => m.ModelId == Qwen);
        Assert.Contains(rig.Map.GetSnapshot().Models, m => m.ModelId == Coder && m.IsPinned);
        Assert.NotNull(resident);
    }
}
