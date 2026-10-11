using Pillion.Common.Redaction;
using Pillion.Common.Settings;
using Pillion.Contracts;

namespace Pillion.ModelGateway.Tests;

public class CatalogAndTemplateTests
{
    [Fact]
    public void Catalog_LoadsFromTheEmbeddedResource()
    {
        var catalog = ModelCatalog.Default;

        Assert.Equal("qwen2.5-coder:1.5b", catalog.GetDefaultModel(ProviderKind.Ollama, Profile.LocalFast));
        Assert.Equal("qwen2.5-coder:14b", catalog.GetDefaultModel(ProviderKind.Ollama, Profile.LocalStrong));
        Assert.Null(catalog.GetDefaultModel(ProviderKind.Ollama, Profile.Frontier));
        Assert.Equal("*", catalog.FamilyPatterns[^1]);
    }

    [Theory]
    [InlineData("qwen2.5-coder:1.5b", true, false, 32768)]
    [InlineData("qwen2.5-coder:7b-base-q4_K_M", true, false, 32768)]
    [InlineData("qwen2.5-coder:14b", true, false, 32768)]
    [InlineData("qwen2.5-coder:32b", true, false, 32768)]
    [InlineData("devstral:24b", false, false, 131072)]
    [InlineData("gemma4:e2b", false, true, 131072)]
    [InlineData("qwen3:8b", false, false, 40960)]
    [InlineData("hf.co/someone/unknown-model:Q4", false, false, 4096)]
    public void Catalog_MatchesFamilies(string modelId, bool fim, bool vision, int context)
    {
        var capabilities = ModelCatalog.Default.GetCapabilities(modelId, isLocal: true);

        Assert.Equal(fim, capabilities.Fim);
        Assert.Equal(vision, capabilities.Vision);
        Assert.Equal(context, capabilities.ContextWindow);
        Assert.True(capabilities.IsLocal);
        Assert.Equal(0m, capabilities.InputCostPerMillionTokens);
    }

    [Theory]
    [InlineData("qwen2.5-coder:0.5b", false)]
    [InlineData("qwen2.5-coder:1.5b", false)]
    [InlineData("qwen2.5-coder:1.5b-base", false)]
    [InlineData("qwen2.5-coder:7b", true)]
    [InlineData("qwen2.5-coder:14b", true)]
    [InlineData("codestral-latest", true)]
    [InlineData("hf.co/someone/unknown-model:Q4", true)]
    public void Catalog_FimHeader_IsOffForTheSmallCoderModels(string modelId, bool header) =>
        Assert.Equal(header, ModelCatalog.Default.GetCapabilities(modelId, isLocal: true).FimHeader);

    [Fact]
    public void Templates_QwenFamily_StopsAtAClosingFence() =>
        Assert.Contains("\n```", PromptTemplates.Default.FindFimFormat("qwen2.5-coder:7b")!.StopSequences);

    [Fact]
    public void Catalog_EmbeddingFamilies_AreNotChatModels()
    {
        var capabilities = ModelCatalog.Default.GetCapabilities("qwen3-embedding:0.6b", isLocal: true);

        Assert.True(capabilities.Embeddings);
        Assert.False(capabilities.Chat);
        Assert.Equal(ModelCapabilities.Embeddings, capabilities.ToFlags());
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{ "profiles": { "nonsense-kind": {} }, "families": [ { "match": "*" } ] }""")]
    [InlineData("""{ "profiles": { "ollama": { "Turbo": "x" } }, "families": [ { "match": "*" } ] }""")]
    [InlineData("""{ "profiles": {}, "families": [ { "match": "qwen*" } ] }""")]
    public void Catalog_InvalidDocuments_AreRejected(string json)
    {
        Assert.Throws<FormatException>(() => ModelCatalog.Parse(json));
    }

    [Fact]
    public void Catalog_ParsesKindNamesAndProfileNames()
    {
        var catalog = ModelCatalog.Parse("""
            {
              "profiles": { "openai-compatible": { "balanced": "gpt-x", "Local-Strong": "local-x" } },
              "families": [ { "match": "gpt-*", "contextWindow": 128000, "toolCalling": "native", "inputCostPerMillionTokens": 2.5 }, { "match": "*" } ]
            }
            """);

        Assert.Equal("gpt-x", catalog.GetDefaultModel(ProviderKind.OpenAiCompatible, Profile.Balanced));
        Assert.Equal("local-x", catalog.GetDefaultModel(ProviderKind.OpenAiCompatible, Profile.LocalStrong));
        var gpt = catalog.GetCapabilities("gpt-x", isLocal: false);
        Assert.Equal(128000, gpt.ContextWindow);
        Assert.Equal(ToolCallingSupport.Native, gpt.ToolCalling);
        Assert.Equal(2.5m, gpt.InputCostPerMillionTokens);
        Assert.False(gpt.IsLocal);
    }

    [Fact]
    public void Templates_LoadTheChatPromptAndFimFormats()
    {
        var templates = PromptTemplates.Default;

        Assert.Contains("Pillion", templates.ChatSystemPrompt, StringComparison.Ordinal);
        Assert.NotEmpty(templates.GetSystemPrompt("chatToolCapable"));
        Assert.Equal(
            ["qwen2.5-coder", "codellama", "starcoder2", "deepseek-coder", "codestral"],
            templates.FimFormats.Select(f => f.Family));
        Assert.True(templates.DefaultFimMaxTokens > 0);

        var qwen = templates.FindFimFormat("qwen2.5-coder:7b")!;
        Assert.Equal("<|fim_prefix|>int a = {suffix}<|fim_suffix|>;<|fim_middle|>", qwen.Render("int a = {suffix}", ";"));
        Assert.Contains("<|endoftext|>", qwen.StopSequences);
        Assert.Equal("[SUFFIX]}[PREFIX]if (x) {", templates.FindFimFormat("codestral:22b")!.Render("if (x) {", "}"));
        Assert.Null(templates.FindFimFormat("gemma4:e2b"));
    }

    [Fact]
    public void QualifiedModelId_SplitsOnlyAKnownProviderPrefix()
    {
        string[] providers = ["ollama", "openai"];

        Assert.Equal(("ollama", "qwen2.5-coder:7b"), QualifiedModelId.Parse("ollama/qwen2.5-coder:7b", providers));
        Assert.Equal(("ollama", "hf.co/org/model:Q4"), QualifiedModelId.Parse("Ollama/hf.co/org/model:Q4", providers));
        Assert.Equal(((string?)null, "hf.co/org/model:Q4"), QualifiedModelId.Parse("hf.co/org/model:Q4", providers));
        Assert.Equal("ollama/x", QualifiedModelId.Format("ollama", "x"));
    }

    [Fact]
    public void ProviderKinds_RoundTripSettingsNames()
    {
        Assert.Equal("openai-compatible", ProviderKinds.ToName(ProviderKind.OpenAiCompatible));
        Assert.True(ProviderKinds.TryParse("azure-openai", out var kind));
        Assert.Equal(ProviderKind.AzureOpenAi, kind);
        Assert.False(ProviderKinds.TryParse("nope", out _));
    }
}

public class StreamingPlaceholderRestorerTests
{
    private static readonly string Secret = TestSecrets.AwsAccessKey();

    [Fact]
    public void PlaceholderSplitAtEveryPosition_IsRestored()
    {
        using var session = new RedactionSession();
        var redacted = session.Redact($"key={Secret}");
        var placeholder = redacted["key=".Length..];
        var output = $"Before {placeholder} after <b>bold</b> < 3";

        for (var split = 1; split < output.Length; split++)
        {
            var restorer = new StreamingPlaceholderRestorer(session);
            var text = restorer.Push(output[..split]) + restorer.Push(output[split..]) + restorer.Flush();
            Assert.Equal($"Before {Secret} after <b>bold</b> < 3", text);
        }
    }

    [Fact]
    public void CharacterByCharacterStreaming_IsRestored()
    {
        using var session = new RedactionSession();
        session.Redact($"a {Secret} b");
        var restorer = new StreamingPlaceholderRestorer(session);

        var pieces = "x <redacted:aws-access-key> y".Select(c => restorer.Push(c.ToString())).ToList();
        pieces.Add(restorer.Flush());

        Assert.Equal($"x {Secret} y", string.Concat(pieces));
        Assert.DoesNotContain(pieces, p => p.Contains("<red", StringComparison.Ordinal));
    }

    [Fact]
    public void TextThatOnlyLooksLikeTheStartOfAPlaceholder_IsReleased()
    {
        using var session = new RedactionSession();
        var restorer = new StreamingPlaceholderRestorer(session);

        Assert.Equal("if (a ", restorer.Push("if (a <"));
        Assert.Equal("< b) {", restorer.Push(" b) {"));
        Assert.Equal("x ", restorer.Push("x <redacted:"));
        Assert.Equal("<redacted:\nnot a placeholder", restorer.Push("\nnot a placeholder"));
        Assert.Equal(string.Empty, restorer.Flush());
    }

    [Fact]
    public void UnknownPlaceholders_AreLeftAsTheyAre()
    {
        using var session = new RedactionSession();
        var restorer = new StreamingPlaceholderRestorer(session);

        Assert.Equal(string.Empty, restorer.Push("<redacted:jwt"));
        Assert.Equal("<redacted:jwt>!", restorer.Push(">!"));
    }

    [Fact]
    public void AnUnclosedPlaceholderAtTheEnd_IsFlushed()
    {
        using var session = new RedactionSession();
        var restorer = new StreamingPlaceholderRestorer(session);

        Assert.Equal("end ", restorer.Push("end <redacted:aws"));
        Assert.Equal("<redacted:aws", restorer.Flush());
    }
}
