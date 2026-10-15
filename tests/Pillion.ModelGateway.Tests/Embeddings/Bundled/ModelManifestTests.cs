using System.Text.RegularExpressions;
using Pillion.Common.Settings;
using Pillion.ModelGateway.Providers.Bundled;

namespace Pillion.ModelGateway.Tests.Embeddings.Bundled;

/// <summary>The model manifest compiled into the gateway: it parses, every URL is https on a pinned commit, and it agrees with the catalog.</summary>
public partial class ModelManifestTests
{
    private const string ValidFile = """{ "role": "model", "path": "m.onnx", "url": "https://example.test/m.onnx", "sha256": "0000000000000000000000000000000000000000000000000000000000000000", "size": 1 }""";
    private const string ValidVocab = """{ "role": "vocab", "path": "vocab.txt", "url": "https://example.test/vocab.txt", "sha256": "1111111111111111111111111111111111111111111111111111111111111111", "size": 1 }""";

    [Fact]
    public void TheEmbeddedManifest_Parses_AndListsTheThreeModels()
    {
        var manifest = ModelManifest.Default;

        Assert.Equal([TestModels.Bge, TestModels.E5, TestModels.Jina], manifest.Models.Select(m => m.Id));
        Assert.Same(manifest.Models[0], manifest.Find("BGE-small-en-v1.5"));
    }

    [Fact]
    public void EveryUrl_IsHttpsOnHuggingFace_AtAPinnedCommit()
    {
        foreach (var file in ModelManifest.Default.Models.SelectMany(m => m.Files))
        {
            Assert.Equal(Uri.UriSchemeHttps, file.Url.Scheme);
            Assert.Equal("huggingface.co", file.Url.Host);
            Assert.Matches(PinnedResolveUrl(), file.Url.AbsolutePath);
            Assert.Matches("^[0-9a-f]{64}$", file.Sha256);
            Assert.True(file.Size > 0);
            Assert.DoesNotContain("..", file.Path, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void OnlyTheBundledDefault_IsBundled_AndItFitsTheVsixBudget()
    {
        var bundled = Assert.Single(ModelManifest.Default.Models, m => m.Bundled);

        Assert.Equal(TestModels.Bge, bundled.Id);
        Assert.Equal("MIT", bundled.License);
        Assert.Equal(EmbeddingPooling.Cls, bundled.Pooling);
        Assert.Equal(384, bundled.Dimensions);
        Assert.Equal(512, bundled.MaxTokens);
        Assert.Equal(ModelManifest.BertWordPiece, bundled.Tokenizer);

        // Design section 16 budgets 35 MB for the bundled model; the optional FP16 graph is not bundled.
        Assert.InRange(bundled.RequiredFiles.Sum(f => f.Size), 30_000_000, 36_700_160);
        Assert.True(bundled.Find(ModelFileRole.ModelFp16)!.Optional);
    }

    [Fact]
    public void TheOnRequestModels_CarryTheirLicencesPoolingAndTokenizers()
    {
        var e5 = ModelManifest.Default.Find(TestModels.E5)!;
        var jina = ModelManifest.Default.Find(TestModels.Jina)!;

        Assert.Equal(("MIT", EmbeddingPooling.Mean, ModelManifest.XlmRobertaSentencePiece, 384, 512), (e5.License, e5.Pooling, e5.Tokenizer, e5.Dimensions, e5.MaxTokens));
        Assert.Equal(("Apache-2.0", EmbeddingPooling.Mean, ModelManifest.RobertaBpe, 768, 8192), (jina.License, jina.Pooling, jina.Tokenizer, jina.Dimensions, jina.MaxTokens));
        Assert.All([e5, jina], m => Assert.True(m.Available && !m.Bundled));
    }

    [Fact]
    public void EveryManifestModel_IsAnEmbeddingFamilyOfTheCatalog_WithTheSameFigures()
    {
        var options = new BundledModelOptions();
        foreach (var model in ModelManifest.Default.Models)
        {
            var entry = ModelCatalog.Default.GetEmbeddingInfo(model.Id);
            Assert.NotNull(entry);
            Assert.Equal(model.Dimensions, entry.Dimensions);
            Assert.Equal(model.Tokenizer, entry.TokenizerFamily);
            Assert.Equal(Math.Min(model.MaxTokens, options.MaxSequenceTokens), entry.MaxInputTokens);
            Assert.True(ModelCatalog.Default.GetCapabilities(model.Id, isLocal: true).Embeddings);
        }

        Assert.Equal(ModelManifest.Default.Models.Where(m => m.Available).Select(m => m.Id), ModelCatalog.Default.GetDefaultEmbeddingModels(ProviderKind.Bundled));
    }

    [Theory]
    [InlineData("http://example.test/m.onnx", "not an https URL")]
    [InlineData("ftp://example.test/m.onnx", "not an https URL")]
    [InlineData("https://user:pass@example.test/m.onnx", "not an https URL")]
    public void AFileUrlThatIsNotPlainHttps_IsRejected(string url, string expected)
    {
        var ex = Assert.Throws<FormatException>(() => ModelManifest.Parse(Manifest(ValidFile.Replace("https://example.test/m.onnx", url, StringComparison.Ordinal))));

        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\"sha256\": \"0000000000000000000000000000000000000000000000000000000000000000\"", "\"sha256\": \"abc\"")]
    [InlineData("\"path\": \"m.onnx\"", "\"path\": \"../m.onnx\"")]
    [InlineData("\"path\": \"m.onnx\"", "\"path\": \"C:/m.onnx\"")]
    [InlineData("\"size\": 1", "\"size\": 0")]
    [InlineData("\"role\": \"model\"", "\"role\": \"weights\"")]
    public void AnInvalidFileEntry_IsRejected(string from, string to) =>
        Assert.Throws<FormatException>(() => ModelManifest.Parse(Manifest(ValidFile.Replace(from, to, StringComparison.Ordinal))));

    [Fact]
    public void AModelWithoutItsTokenizerFile_OrListedTwice_IsRejected()
    {
        Assert.Throws<FormatException>(() => ModelManifest.Parse(Manifest(ValidFile, includeVocab: false)));
        var model = ModelJson("dup", ValidFile + "," + ValidVocab);
        Assert.Throws<FormatException>(() => ModelManifest.Parse($$"""{ "version": 1, "models": [ {{model}}, {{model}} ] }"""));
    }

    [Fact]
    public void AnotherVersion_IsRejected() =>
        Assert.Throws<FormatException>(() => ModelManifest.Parse("""{ "version": 2, "models": [] }"""));

    private static string Manifest(string file, bool includeVocab = true) =>
        $$"""{ "version": 1, "models": [ {{ModelJson("test-model", includeVocab ? file + "," + ValidVocab : file)}} ] }""";

    private static string ModelJson(string id, string files) =>
        $$"""{ "id": "{{id}}", "dimensions": 4, "maxTokens": 16, "pooling": "cls", "tokenizer": "bert-wordpiece", "files": [ {{files}} ] }""";

    [GeneratedRegex("^/[^/]+/[^/]+/resolve/[0-9a-f]{40}/")]
    private static partial Regex PinnedResolveUrl();
}
