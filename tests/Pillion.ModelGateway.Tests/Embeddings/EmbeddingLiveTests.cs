using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Pillion.Common.Settings;
using Pillion.Contracts;
using Pillion.ModelGateway.Providers.Ollama;
using Pillion.ModelGateway.Tests.Ollama;
using Xunit.Abstractions;

namespace Pillion.ModelGateway.Tests.Embeddings;

/// <summary>A fact that needs a running Ollama with <c>qwen3-embedding:0.6b</c> installed.</summary>
public sealed class LiveQwenEmbeddingFactAttribute : FactAttribute
{
    public LiveQwenEmbeddingFactAttribute()
    {
        if (!LiveOllama.IsReachable)
        {
            Skip = $"Ollama is not answering at {LiveOllama.Endpoint}api/version.";
        }
        else if (!LiveOllama.Has(EmbeddingLiveTests.Model))
        {
            Skip = $"{EmbeddingLiveTests.Model} is not installed.";
        }
    }
}

/// <summary>
/// Embeddings against the local Ollama and <c>qwen3-embedding:0.6b</c>. The tests leave other resident models alone and unload
/// only the embedding model at the end, so the machine is as they found it.
/// </summary>
[Trait("Category", "Live")]
public class EmbeddingLiveTests(ITestOutputHelper output)
{
    internal const string Model = "qwen3-embedding:0.6b";

    private static readonly TimeSpan Budget = TimeSpan.FromMinutes(5);

    private static OllamaProvider CreateProvider() => (OllamaProvider)new OllamaProviderFactory().Create(
        new ProviderSettings { Id = "ollama", Kind = ProviderKind.Ollama, Endpoint = LiveOllama.Endpoint },
        new ProviderCreationContext(null, ModelCatalog.Default, NullLoggerFactory.Instance, null));

    private static async Task<List<(string Name, int Context, long Vram)>> ResidentAsync()
    {
        using var http = new HttpClient { BaseAddress = new Uri(LiveOllama.Endpoint), Timeout = TimeSpan.FromSeconds(10) };
        using var document = JsonDocument.Parse(await http.GetStringAsync("api/ps"));
        return [.. document.RootElement.GetProperty("models").EnumerateArray().Select(m =>
            (m.GetProperty("name").GetString()!, m.TryGetProperty("context_length", out var c) ? c.GetInt32() : 0, m.GetProperty("size_vram").GetInt64()))];
    }

    private static async Task UnloadEmbeddingModelAsync()
    {
        using var http = new HttpClient { BaseAddress = new Uri(LiveOllama.Endpoint), Timeout = TimeSpan.FromSeconds(30) };
        using var content = new StringContent($$"""{"model":"{{Model}}","input":[],"keep_alive":0}""");
        using var response = await http.PostAsync("api/embed", content);
    }

    [LiveQwenEmbeddingFact]
    public async Task TheModelsDimensions_MatchTheCatalog_AndTheVectorsAreUnitLength()
    {
        using var provider = CreateProvider();
        using var cts = new CancellationTokenSource(Budget);
        try
        {
            var result = await provider.GetEmbeddingGenerator(Model)!.GenerateAsync(["hello world", "int Add(int a, int b) => a + b;"], cancellationToken: cts.Token);

            var catalog = ModelCatalog.Default.GetEmbeddingInfo(Model)!;
            Assert.Equal(2, result.Count);
            Assert.All(result, e => Assert.Equal(catalog.Dimensions, e.Vector.Length));
            Assert.All(result, e => Assert.Equal(1.0, Math.Sqrt(e.Vector.ToArray().Sum(x => (double)x * x)), 3));
            Assert.True(result.Usage!.InputTokenCount > 0);
            output.WriteLine($"{Model}: {result[0].Vector.Length} dimensions; the catalog says {catalog.Dimensions}.");
        }
        finally
        {
            await UnloadEmbeddingModelAsync();
        }
    }

    [LiveQwenEmbeddingFact]
    public async Task NearDuplicateSentences_AreMoreSimilarThanUnrelatedOnes_InFloat32AndInInt8()
    {
        using var provider = CreateProvider();
        using var cts = new CancellationTokenSource(Budget);
        string[] texts =
        [
            "How do I sort a list of integers in C#?",
            "What is the way to sort a list of ints in C-sharp?",
            "The weather in Paris is rainy and cold in November.",
            "int Add(int a, int b) => a + b;",
            "public int Sum(int x, int y) { return x + y; }",
            "SELECT name FROM users WHERE id = 5;",
        ];
        try
        {
            var generated = await provider.GetEmbeddingGenerator(Model)!.GenerateAsync(texts, cancellationToken: cts.Token);
            var vectors = generated.Select(e => EmbeddingVectors.Process(e.Vector.Span, keepFloat32: true)).ToArray();

            float Float(int a, int b) => EmbeddingVectors.Cosine(vectors[a].Normalized!, vectors[b].Normalized!);
            float Int8(int a, int b) => EmbeddingVectors.Cosine(vectors[a].Quantized, vectors[b].Quantized);

            output.WriteLine($"sort ~ sort (near duplicate): float {Float(0, 1):0.0000}, int8 {Int8(0, 1):0.0000}");
            output.WriteLine($"sort ~ weather (unrelated):   float {Float(0, 2):0.0000}, int8 {Int8(0, 2):0.0000}");
            output.WriteLine($"Add ~ Sum (near duplicate):   float {Float(3, 4):0.0000}, int8 {Int8(3, 4):0.0000}");
            output.WriteLine($"Add ~ SQL (unrelated):        float {Float(3, 5):0.0000}, int8 {Int8(3, 5):0.0000}");

            Assert.True(Float(0, 1) > Float(0, 2), "float: near duplicate above unrelated (prose)");
            Assert.True(Float(3, 4) > Float(3, 5), "float: near duplicate above unrelated (code)");
            Assert.True(Int8(0, 1) > Int8(0, 2), "int8: near duplicate above unrelated (prose)");
            Assert.True(Int8(3, 4) > Int8(3, 5), "int8: near duplicate above unrelated (code)");
            Assert.True(Float(0, 1) - Float(0, 2) > 0.1f, "a clear margin");

            // Quantisation barely moves the similarity.
            Assert.InRange(Int8(0, 1) - Float(0, 1), -0.01f, 0.01f);
            Assert.InRange(Int8(3, 5) - Float(3, 5), -0.01f, 0.01f);
        }
        finally
        {
            await UnloadEmbeddingModelAsync();
        }
    }

    [LiveQwenEmbeddingFact]
    public async Task TheGateway_EmbedsBatches_InOrder_RecordsThem_AndLeavesOtherResidentModelsAlone()
    {
        var before = await ResidentAsync();
        var settings = new PillionSettings();
        settings.Providers.Add(new ProviderSettings { Id = "ollama", Kind = ProviderKind.Ollama, Endpoint = LiveOllama.Endpoint });
        var options = new ModelGatewayOptions();
        using var registry = new ProviderRegistry(new StaticGatewaySettingsSource(settings), new Common.Secrets.InMemorySecretStore(), [new OllamaProviderFactory()], ModelCatalog.Default);
        var inventory = new ModelInventory();
        var breakers = new CircuitBreakerRegistry(options);
        var log = new InMemoryOutboundLog(options);
        var router = new ModelRouter(registry, ModelCatalog.Default, inventory, new AlwaysReachable(), breakers, new NoCostCaps());
        var gateway = new ModelGateway(registry, router, inventory, ModelCatalog.Default, PromptTemplates.Default, new AutocompleteCache(options), breakers, new ConcurrencyLimiter(options), log, new AlwaysReachable(), new NoCostCaps(), new NoProgressSink(), options);
        using var cts = new CancellationTokenSource(Budget);
        try
        {
            await gateway.ListModelsAsync(cts.Token);
            string[] inputs = [.. Enumerable.Range(0, 40).Select(i => $"public int Method{i}(int value) => value * {i + 2};")];
            var started = DateTime.UtcNow;

            var result = await gateway.EmbedAsync(new EmbeddingRequest { RequestId = "live", Inputs = inputs, ModelId = "ollama/" + Model, IncludeFloat32 = true }, cts.Token);

            output.WriteLine($"{inputs.Length} texts in {result.Batches} batches took {(DateTime.UtcNow - started).TotalMilliseconds:0} ms (including any model load).");
            Assert.True(result.IsComplete);
            Assert.Equal(2, result.Batches);
            Assert.Equal(new EmbeddingModelInfo("ollama", Model, 1024, "qwen3-bpe", 32768), result.Model);
            Assert.All(result.Vectors, v => Assert.Equal(1024, v!.Dimensions));

            // Order: the vector of text 33 (second batch) is the vector of that text on its own.
            var alone = await gateway.EmbedAsync(new EmbeddingRequest { Inputs = [inputs[33]], ModelId = "ollama/" + Model, IncludeFloat32 = true }, cts.Token);
            Assert.True(EmbeddingVectors.Cosine(result.Vectors[33]!.Normalized!, alone.Vectors[0]!.Normalized!) > 0.999f);
            Assert.True(EmbeddingVectors.Cosine(result.Vectors[33]!.Normalized!, result.Vectors[3]!.Normalized!) < 0.9999f);

            var records = log.GetRecent();
            Assert.Equal(3, records.Count);
            Assert.Equal(["live/b1", "live/b2"], records.Take(2).Select(r => r.RequestId));
            Assert.All(records, r => Assert.Equal((TaskClass.Embedding, true, OutboundOutcome.Succeeded), (r.TaskClass, r.IsLocal, r.Outcome)));

            // The embedding model sits at the small context and other models are still where they were.
            var after = await ResidentAsync();
            var embedding = Assert.Single(after, m => m.Name.StartsWith("qwen3-embedding", StringComparison.Ordinal));
            output.WriteLine($"resident: {embedding.Name} at num_ctx {embedding.Context}, {embedding.Vram / (1024.0 * 1024):0} MiB of VRAM");
            Assert.Equal(2048, embedding.Context);
            foreach (var other in before.Where(m => !m.Name.StartsWith("qwen3-embedding", StringComparison.Ordinal)))
            {
                Assert.Contains(after, m => m.Name == other.Name && m.Context == other.Context);
            }
        }
        finally
        {
            await UnloadEmbeddingModelAsync();
        }
    }

    [LiveQwenEmbeddingFact]
    public async Task AQuery_FindsTheMatchingPassage_ThroughTheCatalogsInstruction()
    {
        var settings = new PillionSettings();
        settings.Providers.Add(new ProviderSettings { Id = "ollama", Kind = ProviderKind.Ollama, Endpoint = LiveOllama.Endpoint });
        var options = new ModelGatewayOptions();
        using var registry = new ProviderRegistry(new StaticGatewaySettingsSource(settings), new Common.Secrets.InMemorySecretStore(), [new OllamaProviderFactory()], ModelCatalog.Default);
        var inventory = new ModelInventory();
        var breakers = new CircuitBreakerRegistry(options);
        var router = new ModelRouter(registry, ModelCatalog.Default, inventory, new AlwaysReachable(), breakers, new NoCostCaps());
        var gateway = new ModelGateway(registry, router, inventory, ModelCatalog.Default, PromptTemplates.Default, new AutocompleteCache(options), breakers, new ConcurrencyLimiter(options), new InMemoryOutboundLog(options), new AlwaysReachable(), new NoCostCaps(), new NoProgressSink(), options);
        using var cts = new CancellationTokenSource(Budget);
        string[] passages =
        [
            "public int Add(int a, int b) => a + b;",
            "public string ReadFile(string path) => File.ReadAllText(path);",
            "public void Retry(Action action, int attempts) { for (var i = 0; i < attempts; i++) { try { action(); return; } catch { } } }",
            "SELECT name, email FROM users WHERE active = 1 ORDER BY name;",
        ];
        try
        {
            await gateway.ListModelsAsync(cts.Token);

            // No model id: the catalog's preferred model for Ollama is routed, which is this one when it is the only embedding model installed.
            var documents = await gateway.EmbedAsync(new EmbeddingRequest { Inputs = passages, IncludeFloat32 = true }, cts.Token);
            var query = await gateway.EmbedAsync(new EmbeddingRequest { Inputs = ["function that adds two numbers"], Purpose = EmbeddingPurpose.Query, IncludeFloat32 = true }, cts.Token);

            var scores = documents.Vectors.Select(v => EmbeddingVectors.Cosine(query.Vectors[0]!.Normalized!, v!.Normalized!)).ToArray();
            output.WriteLine($"model {documents.Model!.QualifiedId}; scores: {string.Join(", ", scores.Select(s => s.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture)))}");
            Assert.Equal(0, Array.IndexOf(scores, scores.Max()));
        }
        finally
        {
            await UnloadEmbeddingModelAsync();
        }
    }
}
