using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using Pillion.Common.Secrets;
using Pillion.Common.Settings;
using Pillion.Contracts;
using Pillion.ModelGateway.Tests.Providers.Anthropic;
using Pillion.ModelGateway.Tests.Providers.AzureOpenAI;
using Pillion.ModelGateway.Tests.Providers.Cloud;
using Pillion.ModelGateway.Tests.Providers.Gemini;
using Pillion.ModelGateway.Tests.Providers.Mistral;
using Pillion.ModelGateway.Tests.Providers.OpenAICompatible;

namespace Pillion.ModelGateway.Tests.Embeddings;

/// <summary>
/// The OpenAI-compatible, Mistral, Azure OpenAI and Gemini embedding adapters against recorded wire shapes, and Anthropic's
/// absence of one. The response vectors are shortened to six components.
/// </summary>
public class EmbeddingAdapterTests
{
    private const string Key = "unit-test-key-0001";

    private static readonly string[] TwoTexts = ["first text", "second text"];

    private static InMemorySecretStore SecretsFor(string providerId)
    {
        var secrets = new InMemorySecretStore();
        secrets.Set(providerId, Key);
        return secrets;
    }

    private static EmbeddingGenerationOptions Purpose(EmbeddingPurpose purpose, int? dimensions = null) => new()
    {
        Dimensions = dimensions,
        AdditionalProperties = new AdditionalPropertiesDictionary { [GatewayOptionKeys.EmbeddingPurpose] = purpose },
    };

    // OpenAI-compatible (OpenAI, LM Studio, llama.cpp, vLLM, gateways).
    private static FakeOpenAIHandler OpenAIHandler()
    {
        var handler = FakeOpenAIHandler.WithDefaults();
        handler.Text("POST v1/embeddings", FakeOpenAIHandler.Fixture("embeddings.json"), "application/json");
        return handler;
    }

    [Fact]
    public async Task OpenAI_PostsTheBatchToEmbeddings_WithTheKey_AndReturnsTheVectorsInInputOrder()
    {
        var handler = OpenAIHandler();
        using var provider = OpenAICompatibleProviderTests.Create(handler, SecretsFor("gw"));

        var result = await provider.GetEmbeddingGenerator("text-embedding-3-small")!.GenerateAsync(TwoTexts);

        // The fixture lists index 1 before index 0, as some servers do; the result follows the input order.
        Assert.Equal(2, result.Count);
        Assert.Equal(-0.0111f, result[0].Vector.Span[0]);
        Assert.Equal(0.0123f, result[1].Vector.Span[0]);
        Assert.All(result, e => Assert.Equal(6, e.Vector.Length));
        Assert.Equal(9, result.Usage!.InputTokenCount);
        Assert.Equal(9, result.Usage.TotalTokenCount);

        var request = Assert.Single(handler.RequestsTo("v1/embeddings"));
        Assert.Equal("Bearer " + Key, request.Authorization);
        Assert.Equal("text-embedding-3-small", request.Body!["model"]!.GetValue<string>());
        Assert.Equal(TwoTexts, request.Body["input"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Null(request.Body["dimensions"]);
        Assert.Null(request.Body["encoding_format"]);
    }

    [Fact]
    public async Task OpenAI_Dimensions_AreSentOnlyWhenAsked()
    {
        var handler = OpenAIHandler();
        using var provider = OpenAICompatibleProviderTests.Create(handler, SecretsFor("gw"));
        var generator = provider.GetEmbeddingGenerator("text-embedding-3-small")!;

        await generator.GenerateAsync(TwoTexts, new EmbeddingGenerationOptions { Dimensions = 256 });
        await generator.GenerateAsync(TwoTexts);

        var bodies = handler.RequestsTo("v1/embeddings").Select(r => r.Body!).ToList();
        Assert.Equal(256, bodies[0]["dimensions"]!.GetValue<int>());
        Assert.Null(bodies[1]["dimensions"]);
    }

    [Fact]
    public async Task OpenAI_ALocalServer_NeedsNoKey_AndSendsNone()
    {
        var handler = OpenAIHandler();
        using var provider = OpenAICompatibleProviderTests.Create(handler, new InMemorySecretStore(), "http://localhost:1234/v1");

        await provider.GetEmbeddingGenerator("nomic-embed-text-v1.5")!.GenerateAsync(TwoTexts);

        Assert.True(provider.IsLocal);
        Assert.Null(Assert.Single(handler.RequestsTo("v1/embeddings")).Authorization);
    }

    [Fact]
    public async Task OpenAI_TheOpenAIKind_UsesTheSameAdapter()
    {
        var handler = OpenAIHandler();
        using var provider = OpenAICompatibleProviderTests.Create(handler, SecretsFor("gw"), "https://api.openai.com/v1", ProviderKind.OpenAi);

        var result = await provider.GetEmbeddingGenerator("text-embedding-3-small")!.GenerateAsync(TwoTexts);

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public async Task OpenAI_AModelThatDoesNotExist_IsModelNotInstalled()
    {
        var handler = OpenAIHandler();
        handler.Error("POST v1/embeddings", HttpStatusCode.NotFound, FakeOpenAIHandler.Fixture("error-model-not-found.json"));
        using var provider = OpenAICompatibleProviderTests.Create(handler, SecretsFor("gw"));

        var ex = await Assert.ThrowsAsync<ProviderException>(() => provider.GetEmbeddingGenerator("gpt-9-imaginary")!.GenerateAsync(TwoTexts));

        Assert.Equal(PillionErrorCode.ModelNotInstalled, ex.Code);
    }

    [Fact]
    public async Task OpenAI_AServerThatRejectsDimensions_SurfacesItsReason()
    {
        var handler = OpenAIHandler();
        handler.Error("POST v1/embeddings", HttpStatusCode.BadRequest, FakeOpenAIHandler.Fixture("error-embeddings-dimensions.json"));
        using var provider = OpenAICompatibleProviderTests.Create(handler, SecretsFor("gw"));

        var ex = await Assert.ThrowsAsync<ProviderException>(() => provider.GetEmbeddingGenerator("text-embedding-ada-002")!.GenerateAsync(TwoTexts, new EmbeddingGenerationOptions { Dimensions = 256 }));

        Assert.Contains("does not support specifying dimensions", ex.Message, StringComparison.Ordinal);
        Assert.False(ex.IsProviderFault);
    }

    [Fact]
    public async Task OpenAI_ARejectedKey_IsReportedWithoutEchoingIt()
    {
        var handler = OpenAIHandler();
        handler.Error("POST v1/embeddings", HttpStatusCode.Unauthorized, FakeOpenAIHandler.Fixture("error-401.json"));
        using var provider = OpenAICompatibleProviderTests.Create(handler, SecretsFor("gw"));

        var ex = await Assert.ThrowsAsync<ProviderException>(() => provider.GetEmbeddingGenerator("text-embedding-3-small")!.GenerateAsync(TwoTexts));

        Assert.Contains("rejected the API key", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Key, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpenAI_RateLimiting_IsRetryable()
    {
        var handler = OpenAIHandler();
        handler.Error("POST v1/embeddings", HttpStatusCode.TooManyRequests, FakeOpenAIHandler.Fixture("error-429.json"));
        using var provider = OpenAICompatibleProviderTests.Create(handler, SecretsFor("gw"));

        var ex = await Assert.ThrowsAsync<ProviderException>(() => provider.GetEmbeddingGenerator("text-embedding-3-small")!.GenerateAsync(TwoTexts));

        Assert.True(ex.IsRetryable);
    }

    [Theory]
    [InlineData("""{"data":[]}""")]
    [InlineData("""{"object":"list"}""")]
    [InlineData("""{"data":[{"index":0,"embedding":[1,2]},{"index":0,"embedding":[1,2]}]}""")]
    [InlineData("""{"data":[{"index":5,"embedding":[1,2]},{"index":0,"embedding":[1,2]}]}""")]
    [InlineData("""{"data":[{"index":0,"embedding":"AAAA"},{"index":1,"embedding":"AAAA"}]}""")]
    [InlineData("""{"data":[{"index":0},{"index":1}]}""")]
    public async Task OpenAI_AnUnreadableAnswer_IsAProviderError(string body)
    {
        var handler = OpenAIHandler();
        handler.Text("POST v1/embeddings", body, "application/json");
        using var provider = OpenAICompatibleProviderTests.Create(handler, SecretsFor("gw"));

        await Assert.ThrowsAsync<ProviderException>(() => provider.GetEmbeddingGenerator("text-embedding-3-small")!.GenerateAsync(TwoTexts));
    }

    [Fact]
    public async Task OpenAI_AnErrorInsideA200_IsAnError()
    {
        var handler = OpenAIHandler();
        handler.Text("POST v1/embeddings", """{"error":{"message":"overloaded","code":"503"}}""", "application/json");
        using var provider = OpenAICompatibleProviderTests.Create(handler, SecretsFor("gw"));

        await Assert.ThrowsAsync<ProviderException>(() => provider.GetEmbeddingGenerator("text-embedding-3-small")!.GenerateAsync(TwoTexts));
    }

    [Fact]
    public async Task OpenAI_NoTexts_SendNothing()
    {
        var handler = OpenAIHandler();
        using var provider = OpenAICompatibleProviderTests.Create(handler, SecretsFor("gw"));

        var result = await provider.GetEmbeddingGenerator("text-embedding-3-small")!.GenerateAsync([]);

        Assert.Empty(result);
        Assert.Empty(handler.Requests);
    }

    // Mistral.
    [Fact]
    public async Task Mistral_PostsToEmbeddings_WithTheBearerKey()
    {
        var handler = MistralProviderTests.Defaults().Serve("POST v1/embeddings", "Mistral", "embeddings.json");
        using var provider = MistralProviderTests.Create(handler, SecretsFor("mistral"));

        var result = await provider.GetEmbeddingGenerator("mistral-embed")!.GenerateAsync(TwoTexts);

        Assert.Equal(2, result.Count);
        Assert.Equal(0.0101f, result[0].Vector.Span[0]);
        Assert.Equal(-0.0707f, result[1].Vector.Span[0]);
        Assert.Equal(12, result.Usage!.InputTokenCount);
        var request = Assert.Single(handler.RequestsTo("v1/embeddings"));
        Assert.Equal("Bearer " + Key, request.Header("authorization"));
        Assert.Equal("mistral-embed", request.Body!["model"]!.GetValue<string>());
        Assert.Equal(TwoTexts, request.Body["input"]!.AsArray().Select(n => n!.GetValue<string>()));
    }

    [Fact]
    public async Task Mistral_ARejectedKey_IsReported()
    {
        var handler = MistralProviderTests.Defaults().Fail("POST v1/embeddings", HttpStatusCode.Unauthorized, "Mistral", "error-401.json");
        using var provider = MistralProviderTests.Create(handler, SecretsFor("mistral"));

        var ex = await Assert.ThrowsAsync<ProviderException>(() => provider.GetEmbeddingGenerator("mistral-embed")!.GenerateAsync(TwoTexts));

        Assert.DoesNotContain(Key, ex.Message, StringComparison.Ordinal);
    }

    // Azure OpenAI.
    [Fact]
    public async Task Azure_PostsToTheDeploymentsEmbeddingsPath_WithTheApiVersion_AndNamesNoModelInTheBody()
    {
        var handler = AzureOpenAIProviderTests.Defaults().Serve("POST openai/deployments/embed-prod/embeddings", "AzureOpenAI", "embeddings.json");
        using var provider = AzureOpenAIProviderTests.Create(handler, SecretsFor("azure"), null, null, AzureOpenAIProviderTests.Endpoint, "embed-prod");

        var result = await provider.GetEmbeddingGenerator("embed-prod")!.GenerateAsync(["only text"], new EmbeddingGenerationOptions { Dimensions = 512 });

        Assert.Single(result);
        Assert.Equal(0.0131f, result[0].Vector.Span[0]);
        var request = Assert.Single(handler.RequestsTo("openai/deployments/embed-prod/embeddings"));
        Assert.Equal("api-version=2024-10-21", request.Query);
        Assert.Equal(Key, request.Header("api-key"));
        Assert.Null(request.Body!["model"]);
        Assert.Equal(512, request.Body["dimensions"]!.GetValue<int>());
    }

    // Gemini (issue #61).
    private static FakeCloudHandler GeminiHandler() => GeminiProviderTests.Defaults()
        .Serve("POST v1beta/models/gemini-embedding-001:embedContent", "Gemini", "embed-content.json")
        .Serve("POST v1beta/models/gemini-embedding-001:batchEmbedContents", "Gemini", "batch-embed-contents.json");

    [Fact]
    public async Task Gemini_OneText_UsesEmbedContent()
    {
        var handler = GeminiHandler();
        using var provider = GeminiProviderTests.Create(handler, SecretsFor("gem"));

        var result = await provider.GetEmbeddingGenerator("gemini-embedding-001")!.GenerateAsync(["a single chunk"], Purpose(EmbeddingPurpose.Document));

        Assert.Single(result);
        Assert.Equal(0.0161f, result[0].Vector.Span[0]);
        Assert.Equal("gemini-embedding-001", result[0].ModelId);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("v1beta/models/gemini-embedding-001:embedContent", request.Path);
        Assert.Equal(Key, request.Header("x-goog-api-key"));
        Assert.Equal("models/gemini-embedding-001", request.Body!["model"]!.GetValue<string>());
        Assert.Equal("a single chunk", request.Body["content"]!["parts"]![0]!["text"]!.GetValue<string>());
        Assert.Equal("RETRIEVAL_DOCUMENT", request.Body["taskType"]!.GetValue<string>());
        Assert.Null(request.Body["outputDimensionality"]);
    }

    [Fact]
    public async Task Gemini_SeveralTexts_UseBatchEmbedContents_OneRequestPerText_InOrder()
    {
        var handler = GeminiHandler();
        using var provider = GeminiProviderTests.Create(handler, SecretsFor("gem"));

        var result = await provider.GetEmbeddingGenerator("models/gemini-embedding-001")!.GenerateAsync(["one", "two", "three"], Purpose(EmbeddingPurpose.Query, dimensions: 768));

        Assert.Equal(3, result.Count);
        Assert.Equal([0.0171f, -0.0181f, 0.0191f], result.Select(e => e.Vector.Span[0]));
        var request = Assert.Single(handler.Requests);
        Assert.Equal("v1beta/models/gemini-embedding-001:batchEmbedContents", request.Path);
        var requests = request.Body!["requests"]!.AsArray();
        Assert.Equal(["one", "two", "three"], requests.Select(r => r!["content"]!["parts"]![0]!["text"]!.GetValue<string>()));
        Assert.All(requests, r =>
        {
            Assert.Equal("models/gemini-embedding-001", r!["model"]!.GetValue<string>());
            Assert.Equal("RETRIEVAL_QUERY", r["taskType"]!.GetValue<string>());
            Assert.Equal(768, r["outputDimensionality"]!.GetValue<int>());
        });
    }

    [Fact]
    public async Task Gemini_WithoutAPurpose_SendsNoTaskType()
    {
        var handler = GeminiHandler();
        using var provider = GeminiProviderTests.Create(handler, SecretsFor("gem"));

        await provider.GetEmbeddingGenerator("gemini-embedding-001")!.GenerateAsync(["x"]);

        Assert.Null(handler.Requests.Single().Body!["taskType"]);
    }

    [Fact]
    public async Task Gemini_MoreThanTheApiLimit_IsSplitIntoRequestsOfAtMost100()
    {
        var handler = GeminiHandler();
        handler.On("POST v1beta/models/gemini-embedding-001:batchEmbedContents", request =>
        {
            var count = request.Body!["requests"]!.AsArray().Count;
            var embeddings = new JsonArray();
            for (var i = 0; i < count; i++)
            {
                embeddings.Add(new JsonObject { ["values"] = new JsonArray(1.0, 2.0, 3.0) });
            }

            return FakeCloudHandler.Respond(HttpStatusCode.OK, new JsonObject { ["embeddings"] = embeddings }.ToJsonString());
        });
        using var provider = GeminiProviderTests.Create(handler, SecretsFor("gem"));

        var result = await provider.GetEmbeddingGenerator("gemini-embedding-001")!.GenerateAsync(Enumerable.Range(0, 201).Select(i => $"text {i}"));

        // The last request carries one text, which goes to embedContent.
        Assert.Equal(201, result.Count);
        Assert.Equal(
            [100, 100, 1],
            handler.Requests.Select(r => r.Body!["requests"]?.AsArray().Count ?? 1));
        Assert.Equal("v1beta/models/gemini-embedding-001:embedContent", handler.Requests.Last().Path);
    }

    [Fact]
    public async Task Gemini_ABatchAnswerOfTheWrongSize_IsAProviderError()
    {
        var handler = GeminiHandler();
        using var provider = GeminiProviderTests.Create(handler, SecretsFor("gem"));

        // The fixture answers three vectors; two were asked for.
        await Assert.ThrowsAsync<ProviderException>(() => provider.GetEmbeddingGenerator("gemini-embedding-001")!.GenerateAsync(TwoTexts));
    }

    [Fact]
    public async Task Gemini_AnInvalidKey_AndRateLimiting_AreClassified()
    {
        var handler = GeminiHandler()
            .Fail("POST v1beta/models/gemini-embedding-001:embedContent", HttpStatusCode.BadRequest, "Gemini", "error-api-key-invalid.json")
            .Fail("POST v1beta/models/gemini-embedding-001:batchEmbedContents", HttpStatusCode.TooManyRequests, "Gemini", "error-429.json");
        using var provider = GeminiProviderTests.Create(handler, SecretsFor("gem"));
        var generator = provider.GetEmbeddingGenerator("gemini-embedding-001")!;

        var invalid = await Assert.ThrowsAsync<ProviderException>(() => generator.GenerateAsync(["x"]));
        var limited = await Assert.ThrowsAsync<ProviderException>(() => generator.GenerateAsync(TwoTexts));

        Assert.Contains("key", invalid.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Key, invalid.Message, StringComparison.Ordinal);
        Assert.True(limited.IsRetryable);
    }

    [Fact]
    public async Task Gemini_AnEmbeddingModelIsListedWithTheEmbeddingsCapability()
    {
        var handler = GeminiHandler();
        using var provider = GeminiProviderTests.Create(handler, SecretsFor("gem"));

        var models = await provider.ListModelsAsync(CancellationToken.None);

        var embedding = models.Single(m => m.ModelId == "gemini-embedding-001");
        Assert.True(embedding.Capabilities.Embeddings);
        Assert.False(embedding.Capabilities.Chat);
        Assert.NotNull(provider.GetEmbeddingGenerator(embedding.ModelId));
    }

    // Anthropic has no embeddings endpoint.
    [Fact]
    public async Task Anthropic_ReportsNoEmbeddings_AndOffersNoGenerator()
    {
        using var provider = AnthropicProviderTests.Create(AnthropicProviderTests.Defaults(), SecretsFor("claude"));

        var models = await provider.ListModelsAsync(CancellationToken.None);

        Assert.All(models, m => Assert.False(m.Capabilities.Embeddings, m.ModelId));
        Assert.All(models, m => Assert.Null(provider.GetEmbeddingGenerator(m.ModelId)));
        Assert.Null(provider.GetEmbeddingGenerator("text-embedding-3-small"));
        Assert.False(ModelCatalog.Default.GetCapabilities("claude-sonnet-5-5", isLocal: false).Embeddings);
    }
}
