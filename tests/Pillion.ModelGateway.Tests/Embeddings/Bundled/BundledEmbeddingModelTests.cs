using System.Diagnostics;
using System.Globalization;
using Microsoft.ML.OnnxRuntime;
using Pillion.Contracts;
using Pillion.ModelGateway.Providers.Bundled;
using Pillion.ModelGateway.Tests.TestSupport;
using Xunit.Abstractions;

namespace Pillion.ModelGateway.Tests.Embeddings.Bundled;

/// <summary>
/// The bundled model itself (bge-small-en-v1.5, int8): real inference on ONNX Runtime with the files the build placed in the
/// output (or, failing that, downloaded through <see cref="ModelDownloadService"/> into a temp folder). Marked
/// <c>Category=Model</c>: no service is needed, but the model files are.
/// </summary>
[Trait("Category", "Model")]
public class BundledEmbeddingModelTests(ITestOutputHelper output)
{
    private const string Question = "How do I read a file line by line in C#?";
    private const string NearDuplicate = "What is the way to read each line of a text file in C#?";
    private const string Code = "foreach (var line in File.ReadLines(path)) { Console.WriteLine(line); }";
    private const string Unrelated = "The recipe needs two cups of flour and an egg.";

    private static readonly string LongChunk = string.Join(
        ' ',
        Enumerable.Repeat("public void ProcessOrder(Order order) { if (order is null) throw new ArgumentNullException(nameof(order)); _repository.Save(order); }", 60));

    private static bool DirectMLBuilt => OrtEnv.Instance().GetAvailableProviders().Contains("DmlExecutionProvider");

    [Fact]
    public async Task NearDuplicates_AreCloserThanUnrelatedText_AndTheInt8RoundTripKeepsTheOrder()
    {
        using var runtime = TestModels.Runtime(await TestModels.EnsureAsync(TestModels.Bge));

        var (vectors, tokens) = runtime.Embed(TestModels.Bge, [Question, NearDuplicate, Code, Unrelated], CancellationToken.None);

        Assert.All(vectors, v => Assert.Equal(384, v.Length));
        Assert.All(vectors, v => Assert.InRange(Math.Sqrt(v.Sum(x => (double)x * x)), 0.999, 1.001));
        Assert.True(tokens > 40);
        var near = EmbeddingVectors.Cosine(vectors[0], vectors[1]);
        var code = EmbeddingVectors.Cosine(vectors[0], vectors[2]);
        var far = EmbeddingVectors.Cosine(vectors[0], vectors[3]);
        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"float32 cosine: near-duplicate {near:F4}, code {code:F4}, unrelated {far:F4}"));
        Assert.True(near > far + 0.2, $"near {near} far {far}");
        Assert.True(code > far, $"code {code} far {far}");

        // The index stores int8 vectors (work item 2.6): the ordering survives quantisation and each cosine moves very little.
        var q = vectors.Select(v => EmbeddingVectors.Process(v, keepFloat32: false)).ToArray();
        var nearQ = EmbeddingVectors.Cosine(q[0].Quantized, q[1].Quantized);
        var codeQ = EmbeddingVectors.Cosine(q[0].Quantized, q[2].Quantized);
        var farQ = EmbeddingVectors.Cosine(q[0].Quantized, q[3].Quantized);
        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"int8 cosine: near-duplicate {nearQ:F4}, code {codeQ:F4}, unrelated {farQ:F4}"));
        Assert.True(nearQ > codeQ && codeQ > farQ);
        Assert.InRange(Math.Abs(nearQ - near), 0, 0.01);
        Assert.InRange(Math.Abs(farQ - far), 0, 0.01);
    }

    [Fact]
    public async Task TheGateway_EmbedsWithTheBundledModel_WhenLocalOnlyIsOn_AndDescribesItForTheIndex()
    {
        using var runtime = TestModels.Runtime(await TestModels.EnsureAsync(TestModels.Bge));
        using var harness = new GatewayHarness(extraFactories: [new BundledProviderFactory(runtime)]);
        harness.Settings.Privacy.LocalOnly = true;
        harness.Reload();
        await harness.Gateway.ListModelsAsync(CancellationToken.None);

        var result = await harness.Gateway.EmbedAsync(
            new EmbeddingRequest { Inputs = [Question, NearDuplicate, Unrelated, LongChunk], IncludeFloat32 = true },
            CancellationToken.None);

        Assert.True(result.IsComplete);
        Assert.Equal(new EmbeddingModelInfo("bundled", TestModels.Bge, 384, "bert-wordpiece", 512), result.Model);
        Assert.Equal(1, result.Batches);
        Assert.All(result.Vectors, v => Assert.Equal(384, v!.Dimensions));
        var near = EmbeddingVectors.Cosine(result.Vectors[0]!.Quantized, result.Vectors[1]!.Quantized);
        var far = EmbeddingVectors.Cosine(result.Vectors[0]!.Quantized, result.Vectors[2]!.Quantized);
        Assert.True(near > far);
        Assert.Contains(harness.OutboundLog.GetRecent(), r => r.ProviderId == "bundled" && r.IsLocal && r.TaskClass == TaskClass.Embedding && r.Outcome == OutboundOutcome.Succeeded);
    }

    [Fact]
    public async Task AQuery_GetsTheModelCardsInstruction_SoItsVectorDiffersFromTheSameTextAsADocument()
    {
        using var runtime = TestModels.Runtime(await TestModels.EnsureAsync(TestModels.Bge));
        using var harness = new GatewayHarness(withCloud: false, extraFactories: [new BundledProviderFactory(runtime)]);

        var document = await harness.Gateway.EmbedAsync(new EmbeddingRequest { Inputs = [Question], Purpose = EmbeddingPurpose.Document, IncludeFloat32 = true }, CancellationToken.None);
        var query = await harness.Gateway.EmbedAsync(new EmbeddingRequest { Inputs = [Question], Purpose = EmbeddingPurpose.Query, IncludeFloat32 = true }, CancellationToken.None);

        var cosine = EmbeddingVectors.Cosine(document.Vectors[0]!.Normalized!, query.Vectors[0]!.Normalized!);
        Assert.InRange(cosine, 0.7, 0.999);
    }

    [Fact]
    public async Task TheProvider_ListsOnlyVerifiedModels_AsLocalEmbeddingModelsThatTruncate()
    {
        using var runtime = TestModels.Runtime(await TestModels.EnsureAsync(TestModels.Bge));
        var provider = new BundledEmbeddingProvider(new Common.Settings.ProviderSettings { Id = "bundled", Kind = Common.Settings.ProviderKind.Bundled }, runtime);

        var models = await provider.ListModelsAsync(CancellationToken.None);
        var probe = await provider.ProbeAsync(CancellationToken.None);

        var bge = Assert.Single(models, m => m.ModelId == TestModels.Bge);
        Assert.True(bge.Capabilities.Embeddings);
        Assert.False(bge.Capabilities.Chat);
        Assert.True(bge.Capabilities.IsLocal);
        Assert.Equal(BundledEmbeddingProvider.TruncatingContextWindow, bge.Capabilities.ContextWindow);
        Assert.Equal("sha256:" + ModelManifest.Default.Find(TestModels.Bge)!.Find(ModelFileRole.Model)!.Sha256, bge.Digest);
        Assert.True(probe.IsReachable);
        Assert.NotNull(provider.GetEmbeddingGenerator(TestModels.Bge));
        Assert.Null(provider.GetEmbeddingGenerator("no-such-model"));
        Assert.Null(provider.GetFimClient(TestModels.Bge));
        var chat = await Assert.ThrowsAsync<ProviderException>(() => provider.GetChatClient(TestModels.Bge).GetResponseAsync([]));
        Assert.Equal(PillionErrorCode.ModelNotInstalled, chat.Code);
    }

    [Fact]
    public async Task ALongInput_IsTruncatedToTheModelsLimit_NotRejected()
    {
        using var runtime = TestModels.Runtime(await TestModels.EnsureAsync(TestModels.Bge));
        var counter = runtime.GetTokenCounter(TestModels.Bge)!;

        var (vectors, tokens) = runtime.Embed(TestModels.Bge, [LongChunk], CancellationToken.None);

        Assert.True(counter.CountTokens(LongChunk) > 512);
        Assert.Equal(512, tokens);
        Assert.Equal(384, vectors[0].Length);
    }

    [Fact]
    public async Task ACancelledCall_StopsWithOperationCanceled()
    {
        using var runtime = TestModels.Runtime(await TestModels.EnsureAsync(TestModels.Bge));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        Assert.ThrowsAny<OperationCanceledException>(() => runtime.Embed(TestModels.Bge, [Question], cts.Token));
    }

    [Fact]
    public async Task AnIdleModel_IsReleased_AndLoadsAgainOnTheNextCall()
    {
        using var runtime = TestModels.Runtime(await TestModels.EnsureAsync(TestModels.Bge), configure: o => o.IdleUnloadAfter = TimeSpan.FromMilliseconds(200));

        runtime.Embed(TestModels.Bge, [Question], CancellationToken.None);
        Assert.Equal(LoadedExecutionProvider.Cpu, runtime.ExecutionProviderOf(TestModels.Bge));

        var waited = Stopwatch.StartNew();
        while (runtime.ExecutionProviderOf(TestModels.Bge) is not null && waited.Elapsed < TimeSpan.FromSeconds(10))
        {
            await Task.Delay(50);
        }

        Assert.Null(runtime.ExecutionProviderOf(TestModels.Bge));
        Assert.Single(runtime.Embed(TestModels.Bge, [Question], CancellationToken.None).Vectors);
    }

    [Fact]
    public async Task DirectML_AgreesWithTheCpu_OrFallsBackToTheCpu()
    {
        var root = await TestModels.EnsureAsync(TestModels.Bge);
        using var cpu = TestModels.Runtime(root);
        using var gpu = TestModels.Runtime(root, BundledExecutionProvider.DirectML);

        var expected = cpu.Embed(TestModels.Bge, [Question, Code, Unrelated], CancellationToken.None).Vectors;
        var actual = gpu.Embed(TestModels.Bge, [Question, Code, Unrelated], CancellationToken.None).Vectors;

        var provider = gpu.ExecutionProviderOf(TestModels.Bge);
        output.WriteLine($"DirectML in this build: {DirectMLBuilt}; the model runs on {provider}.");
        // DirectML needs a hardware adapter; without one (a CI runner) the runtime falls back to the CPU, as the name says.
        if (DirectMLBuilt && OperatingSystem.IsWindows() && new Pillion.Common.Hardware.DxgiProbe().Probe() is { Count: > 0 })
        {
            Assert.Equal(LoadedExecutionProvider.DirectML, provider);
        }

        for (var i = 0; i < expected.Length; i++)
        {
            Assert.InRange(EmbeddingVectors.Cosine(expected[i], actual[i]), 0.99, 1.0001);
        }
    }

    [Fact]
    public async Task Throughput_OfFullBatches_IsLogged_AndWithinAGenerousBound()
    {
        var root = await TestModels.EnsureAsync(TestModels.Bge);
        var batch = Enumerable.Range(0, 32).Select(i => i.ToString(CultureInfo.InvariantCulture) + " " + LongChunk).ToArray();

        var cpu = Measure(root, BundledExecutionProvider.Cpu, batch);
        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"CPU ({new BundledModelOptions().IntraOpThreads} threads): {cpu.ChunksPerSecond:F1} chunks/s for 512-token chunks in batches of 32 ({cpu.Milliseconds:F0} ms a batch)."));
        Assert.True(cpu.ChunksPerSecond >= 0.25, $"{cpu.ChunksPerSecond} chunks/s");

        if (DirectMLBuilt && OperatingSystem.IsWindows())
        {
            var gpu = Measure(root, BundledExecutionProvider.DirectML, batch);
            output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{gpu.Provider}: {gpu.ChunksPerSecond:F1} chunks/s ({gpu.Milliseconds:F0} ms a batch), {gpu.ChunksPerSecond / cpu.ChunksPerSecond:F1} times the CPU."));
        }
    }

    private static (double ChunksPerSecond, double Milliseconds, LoadedExecutionProvider? Provider) Measure(string root, BundledExecutionProvider executionProvider, string[] batch)
    {
        using var runtime = TestModels.Runtime(root, executionProvider);
        runtime.Embed(TestModels.Bge, batch[..4], CancellationToken.None); // load and warm up
        const int Rounds = 3;
        var watch = Stopwatch.StartNew();
        for (var i = 0; i < Rounds; i++)
        {
            var (_, tokens) = runtime.Embed(TestModels.Bge, batch, CancellationToken.None);
            Assert.Equal(32 * 512, tokens);
        }

        var perBatch = watch.Elapsed.TotalMilliseconds / Rounds;
        return (32 / (perBatch / 1000), perBatch, runtime.ExecutionProviderOf(TestModels.Bge));
    }
}
