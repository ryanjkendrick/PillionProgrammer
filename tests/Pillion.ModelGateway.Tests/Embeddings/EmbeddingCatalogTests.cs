using Pillion.Common.Settings;

namespace Pillion.ModelGateway.Tests.Embeddings;

/// <summary>The embedding entries of the model catalog: dimensions, input limits, tokenizer families, licences, prefixes and per-kind preferences.</summary>
public class EmbeddingCatalogTests
{
    private const string Minimal = """{ "families": [ { "match": "*" } ] }""";

    [Theory]
    [InlineData("qwen3-embedding:0.6b", 1024, 32768, "qwen3-bpe")]
    [InlineData("qwen3-embedding:0.6b-q8_0", 1024, 32768, "qwen3-bpe")]
    [InlineData("nomic-embed-text", 768, 8192, "bert-wordpiece")]
    [InlineData("nomic-embed-text:latest", 768, 8192, "bert-wordpiece")]
    [InlineData("text-embedding-3-small", 1536, 8191, "openai-cl100k")]
    [InlineData("text-embedding-3-large", 3072, 8191, "openai-cl100k")]
    [InlineData("text-embedding-ada-002", 1536, 8191, "openai-cl100k")]
    [InlineData("gemini-embedding-001", 3072, 2048, "gemini")]
    [InlineData("mistral-embed", 1024, 8192, "mistral")]
    [InlineData("library/qwen3-embedding:0.6b", 1024, 32768, "qwen3-bpe")]
    public void TheCatalogDescribesTheEmbeddingModels(string modelId, int dimensions, int maxInputTokens, string tokenizer)
    {
        var entry = ModelCatalog.Default.GetEmbeddingInfo(modelId);

        Assert.NotNull(entry);
        Assert.Equal(dimensions, entry.Dimensions);
        Assert.Equal(maxInputTokens, entry.MaxInputTokens);
        Assert.Equal(tokenizer, entry.TokenizerFamily);
        Assert.False(string.IsNullOrWhiteSpace(entry.License), "a licence note");
        Assert.False(string.IsNullOrWhiteSpace(entry.Note), "where the figures come from");
        Assert.True(ModelCatalog.Default.GetCapabilities(modelId, isLocal: false).Embeddings);
        Assert.False(ModelCatalog.Default.GetCapabilities(modelId, isLocal: false).Chat);
    }

    [Fact]
    public void QwenEmbeddingsOtherThanTheMeasuredSize_LeaveTheDimensionsToTheFirstResponse()
    {
        var entry = ModelCatalog.Default.GetEmbeddingInfo("qwen3-embedding:8b");

        Assert.NotNull(entry);
        Assert.Equal(0, entry.Dimensions);
        Assert.Equal("qwen3-bpe", entry.TokenizerFamily);
    }

    [Theory]
    [InlineData("qwen2.5-coder:7b")]
    [InlineData("gemma4:e2b")]
    [InlineData("claude-sonnet-5-5")]
    [InlineData("gpt-6.1-sol")]
    [InlineData("an-unknown-model")]
    public void ModelsThatAreNotDescribedAsEmbeddingModels_HaveNoEmbeddingEntry(string modelId)
    {
        Assert.Null(ModelCatalog.Default.GetEmbeddingInfo(modelId));
    }

    [Fact]
    public void OnlyTheModelsTheSizeIsKnownFor_CanBeShortened_AndOnlyNomicAndQwenHavePrefixes()
    {
        Assert.True(ModelCatalog.Default.GetEmbeddingInfo("text-embedding-3-small")!.SupportsDimensions);
        Assert.True(ModelCatalog.Default.GetEmbeddingInfo("gemini-embedding-001")!.SupportsDimensions);
        Assert.False(ModelCatalog.Default.GetEmbeddingInfo("text-embedding-ada-002")!.SupportsDimensions);
        Assert.False(ModelCatalog.Default.GetEmbeddingInfo("mistral-embed")!.SupportsDimensions);

        var nomic = ModelCatalog.Default.GetEmbeddingInfo("nomic-embed-text")!;
        Assert.Equal("search_document: ", nomic.DocumentPrefix);
        Assert.Equal("search_query: ", nomic.QueryPrefix);
        var qwen = ModelCatalog.Default.GetEmbeddingInfo("qwen3-embedding:0.6b")!;
        Assert.Equal(string.Empty, qwen.DocumentPrefix);
        Assert.StartsWith("Instruct: ", qwen.QueryPrefix, StringComparison.Ordinal);
        Assert.Equal(string.Empty, ModelCatalog.Default.GetEmbeddingInfo("text-embedding-3-small")!.QueryPrefix);
    }

    [Fact]
    public void TheQwenEmbeddingResidencyIsMeasured_AndSmallContextsAreRecommended()
    {
        var residency = ModelCatalog.Default.GetResidency("qwen3-embedding:0.6b");

        Assert.NotNull(residency);

        // Measured on Ollama 0.40.4: 2,152 MiB at num_ctx 2,048 (the model's whole 32,768-token window takes 5,801 MiB).
        Assert.InRange(residency.EstimateMiB(2048), 2100, 2200);
        Assert.InRange(residency.EstimateMiB(32768), 5400, 5900);
        Assert.Equal(2048, residency.RecommendedContextFor(Common.Hardware.GpuTier.Gpu8GB, 0));
    }

    [Fact]
    public void ThePreferredEmbeddingModels_AreListedPerProviderKind_InOrder()
    {
        Assert.Equal(["qwen3-embedding:0.6b", "nomic-embed-text"], ModelCatalog.Default.GetDefaultEmbeddingModels(ProviderKind.Ollama));
        Assert.Equal(["text-embedding-3-small"], ModelCatalog.Default.GetDefaultEmbeddingModels(ProviderKind.OpenAi));
        Assert.Equal(["gemini-embedding-001"], ModelCatalog.Default.GetDefaultEmbeddingModels(ProviderKind.Gemini));
        Assert.Equal(["mistral-embed"], ModelCatalog.Default.GetDefaultEmbeddingModels(ProviderKind.Mistral));
        Assert.Empty(ModelCatalog.Default.GetDefaultEmbeddingModels(ProviderKind.Anthropic));
        Assert.Empty(ModelCatalog.Default.GetDefaultEmbeddingModels(ProviderKind.AzureOpenAi));
    }

    [Fact]
    public void EveryPreferredModel_IsDescribedByTheCatalog_AsAnEmbeddingModel()
    {
        foreach (var kind in Enum.GetValues<ProviderKind>())
        {
            foreach (var model in ModelCatalog.Default.GetDefaultEmbeddingModels(kind))
            {
                Assert.NotNull(ModelCatalog.Default.GetEmbeddingInfo(model));
                Assert.True(ModelCatalog.Default.GetCapabilities(model, isLocal: false).Embeddings, model);
            }
        }
    }

    [Fact]
    public void TheCatalogsOwnPrefixes_DoNotChangeWhatTheEmbeddingFamiliesAreForChat()
    {
        // The existing capability facts still hold: embedding families are not chat models and have no completion limit.
        var capabilities = ModelCatalog.Default.GetCapabilities("qwen3-embedding:0.6b", isLocal: true);

        Assert.Equal(32768, capabilities.ContextWindow);
        Assert.Equal(0, capabilities.MaxOutputTokens);
        Assert.True(capabilities.IsLocal);
        Assert.Equal(0m, capabilities.InputCostPerMillionTokens);
    }

    // Parsing.
    [Fact]
    public void Parse_ReadsAnEmbeddingEntry_AndTheDefaultsList()
    {
        var catalog = ModelCatalog.Parse("""
            {
              "embeddingDefaults": { "ollama": [ "acme-embed", " " ] },
              "families": [
                { "match": "acme-embed*", "chat": false, "embeddings": true, "embedding": { "dimensions": 512, "maxInputTokens": 1024, "tokenizerFamily": "acme", "license": "MIT", "documentPrefix": "doc: " } },
                { "match": "*" }
              ]
            }
            """);

        var entry = catalog.GetEmbeddingInfo("acme-embed-v2");
        Assert.Equal(new EmbeddingModelEntry(512, 1024, "acme", "MIT", false, "doc: ", string.Empty, null), entry);
        Assert.Equal(["acme-embed"], catalog.GetDefaultEmbeddingModels(ProviderKind.Ollama));
    }

    [Fact]
    public void Parse_AnEntryWithoutATokenizer_IsUnknown()
    {
        var catalog = ModelCatalog.Parse("""{ "families": [ { "match": "x", "embedding": { "maxInputTokens": 10 } }, { "match": "*" } ] }""");

        Assert.Equal(EmbeddingModelInfo.UnknownTokenizer, catalog.GetEmbeddingInfo("x")!.TokenizerFamily);
        Assert.Equal(0, catalog.GetEmbeddingInfo("x")!.Dimensions);
    }

    [Theory]
    [InlineData("""{ "families": [ { "match": "x", "embedding": { "dimensions": 8 } }, { "match": "*" } ] }""")]
    [InlineData("""{ "families": [ { "match": "x", "embedding": { "dimensions": -1, "maxInputTokens": 8 } }, { "match": "*" } ] }""")]
    [InlineData("""{ "embeddingDefaults": { "nonsense": [ "a" ] }, "families": [ { "match": "*" } ] }""")]
    public void Parse_RejectsABadEmbeddingEntry(string json)
    {
        Assert.Throws<FormatException>(() => ModelCatalog.Parse(json));
    }

    [Fact]
    public void Parse_AnEmptyCatalog_HasNoEmbeddingInformation()
    {
        var catalog = ModelCatalog.Parse(Minimal);

        Assert.Null(catalog.GetEmbeddingInfo("anything"));
        Assert.Empty(catalog.GetDefaultEmbeddingModels(ProviderKind.Ollama));
    }
}
