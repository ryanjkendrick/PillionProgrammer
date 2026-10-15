using System.Diagnostics;
using System.Globalization;
using Microsoft.ML.OnnxRuntime;
using Pillion.ModelGateway.Providers.Bundled;
using Xunit.Abstractions;

namespace Pillion.ModelGateway.Tests.Embeddings.Bundled;

/// <summary>
/// The on-request models (multilingual E5 and Jina code): downloaded from huggingface.co through
/// <see cref="ModelDownloadService"/> into %TEMP%\pillion-model-tests (about 280 MB the first time), then tokenised and run.
/// Live because they need the network; the reference token ids come from the Hugging Face tokenizers library (0.22.1).
/// </summary>
[Trait("Category", "Live")]
public class OnRequestModelLiveTests(ITestOutputHelper output)
{
    private static readonly int[][] E5ReferenceIds =
    [
        [0, 35378, 8999, 5, 2],
        [0, 3835, 10, 12654, 238, 9598, 92, 16093, 34139, 372, 59725, 4332, 7, 7136, 2740, 2775, 13482, 284, 12654, 238, 132, 34139, 372, 59725, 4332, 75616, 50336, 4, 80392, 57860, 7763, 1098, 126136, 1363, 7763, 1098, 16, 2],
        [0, 8, 420, 366, 184, 454, 153837, 132, 128405, 12, 7704, 16, 33079, 6, 61669, 12, 30646, 1647, 1681, 5, 63033, 132, 59636, 132, 128405, 16, 16, 2],
        [0, 61427, 24, 9392, 272, 233482, 1659, 2],
        [0, 6, 124084, 3221, 26950, 33600, 31, 8999, 2],
        [0, 2174, 15, 425, 711, 1369, 56574, 619, 1230, 113, 977, 1369, 209, 16, 10666, 30646, 1022, 32, 5, 856, 50770, 24610, 757, 74, 51912, 2],
        [0, 2046, 18544, 23658, 94399, 13, 114211, 16854, 350, 454, 58437, 454, 11627, 311, 17105, 9, 58437, 9, 11627, 2],
    ];

    private static readonly int[][] JinaReferenceIds =
    [
        [0, 10564, 7550, 18, 2],
        [0, 1040, 5401, 5868, 32, 53863, 1575, 34, 25663, 4221, 12, 53863, 1417, 1478, 16, 26510, 19980, 13, 2],
        [0, 406, 3240, 67, 1128, 12, 1111, 30, 1321, 13, 1882, 6757, 30, 287, 437, 3905, 18, 970, 12, 1798, 12, 1111, 478, 2],
        [0, 39, 1326, 2521, 9604, 15764, 661, 24091, 1155, 2521, 48167, 2],
        [0, 12552, 19692, 47406, 32039, 6196, 26056, 7550, 2],
        [0, 311, 301, 92, 786, 787, 742, 711, 1921, 1528, 13, 297, 437, 927, 16137, 840, 14234, 325, 31, 332, 2],
        [0, 372, 5623, 1909, 1455, 7990, 1268, 67, 1259, 67, 507, 12874, 24473, 17, 1259, 17, 507, 2],
    ];

    [Fact]
    public async Task WithTheOptionalFp16Graph_DirectMLRunsIt_AndAgreesWithTheCpuInt8Graph()
    {
        if (!OrtEnv.Instance().GetAvailableProviders().Contains("DmlExecutionProvider") || !OperatingSystem.IsWindows())
        {
            output.WriteLine("This build has no DirectML; nothing to measure.");
            return;
        }

        await new ModelDownloadService(ModelManifest.Default, TestModels.DownloadRoot).DownloadAsync(TestModels.Bge, includeOptional: true);
        using var cpu = TestModels.Runtime(TestModels.DownloadRoot);
        using var gpu = TestModels.Runtime(TestModels.DownloadRoot, BundledExecutionProvider.DirectML);
        Assert.True(gpu.Locate(TestModels.Bge)!.HasFp16);
        var batch = Enumerable.Range(0, 32).Select(i => i.ToString(CultureInfo.InvariantCulture) + " " + string.Join(' ', Enumerable.Repeat("public void ProcessOrder(Order order) { _repository.Save(order); }", 80))).ToArray();

        var expected = cpu.Embed(TestModels.Bge, batch[..4], CancellationToken.None).Vectors;
        var actual = gpu.Embed(TestModels.Bge, batch[..4], CancellationToken.None).Vectors;
        Assert.Equal(LoadedExecutionProvider.DirectML, gpu.ExecutionProviderOf(TestModels.Bge));
        for (var i = 0; i < expected.Length; i++)
        {
            Assert.InRange(EmbeddingVectors.Cosine(expected[i], actual[i]), 0.99, 1.0001);
        }

        var watch = Stopwatch.StartNew();
        for (var round = 0; round < 3; round++)
        {
            gpu.Embed(TestModels.Bge, batch, CancellationToken.None);
        }

        var perBatch = watch.Elapsed.TotalMilliseconds / 3;
        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"DirectML with model_fp16.onnx: {32 / (perBatch / 1000):F1} chunks/s for 512-token chunks in batches of 32 ({perBatch:F0} ms a batch)."));
    }

    [Fact]
    public async Task E5_TokenisesLikeThePythonTokenizer_AndRanksAMatchingPassageFirst()
    {
        using var runtime = TestModels.Runtime(await TestModels.EnsureAsync(TestModels.E5));
        var tokenizer = (EmbeddingTokenizer)runtime.GetTokenCounter(TestModels.E5)!;
        for (var i = 0; i < TestModels.ReferenceTexts.Length; i++)
        {
            Assert.Equal(E5ReferenceIds[i], tokenizer.Encode(TestModels.ReferenceTexts[i]));
        }

        // The model card's prefixes (the gateway adds them from the catalog).
        var (vectors, _) = runtime.Embed(
            TestModels.E5,
            ["query: wie lese ich eine Datei Zeile für Zeile", "passage: File.ReadLines liest eine Textdatei Zeile für Zeile.", "passage: The recipe needs two cups of flour."],
            CancellationToken.None);
        var near = EmbeddingVectors.Cosine(vectors[0], vectors[1]);
        var far = EmbeddingVectors.Cosine(vectors[0], vectors[2]);
        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"e5: matching passage {near:F4}, unrelated {far:F4}"));
        Assert.True(near > far);
        Assert.All(vectors, v => Assert.Equal(384, v.Length));
    }

    [Fact]
    public async Task Jina_TokenisesLikeThePythonTokenizer_RunsOnStockOnnxRuntime_AndRanksMatchingCodeFirst()
    {
        using var runtime = TestModels.Runtime(await TestModels.EnsureAsync(TestModels.Jina));
        var tokenizer = (EmbeddingTokenizer)runtime.GetTokenCounter(TestModels.Jina)!;
        for (var i = 0; i < TestModels.ReferenceTexts.Length; i++)
        {
            Assert.Equal(JinaReferenceIds[i], tokenizer.Encode(TestModels.ReferenceTexts[i]));
        }

        Assert.Equal(2048, tokenizer.MaxTokens);
        var watch = Stopwatch.StartNew();
        var (vectors, _) = runtime.Embed(
            TestModels.Jina,
            ["read a text file line by line", "foreach (var line in File.ReadLines(path)) { Console.WriteLine(line); }", "def bake(flour, eggs): return Cake(flour, eggs)"],
            CancellationToken.None);
        var near = EmbeddingVectors.Cosine(vectors[0], vectors[1]);
        var far = EmbeddingVectors.Cosine(vectors[0], vectors[2]);
        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"jina: matching code {near:F4}, unrelated code {far:F4}; first call (load and run) {watch.ElapsedMilliseconds} ms on {runtime.ExecutionProviderOf(TestModels.Jina)}"));
        Assert.True(near > far);
        Assert.All(vectors, v => Assert.Equal(768, v.Length));
    }
}
