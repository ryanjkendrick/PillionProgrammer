using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Pillion.Common.Hardware;
using Pillion.Common.Settings;
using Pillion.Contracts;
using Pillion.ModelGateway.Providers.Ollama;
using Pillion.ModelGateway.Tests.Autocomplete;
using Pillion.ModelGateway.Tests.TestSupport;
using Xunit.Abstractions;

namespace Pillion.ModelGateway.Tests.Ollama;

/// <summary>A fact that needs an installed Ollama model.</summary>
public sealed class LiveOllamaModelFactAttribute : FactAttribute
{
    public LiveOllamaModelFactAttribute(string model)
    {
        if (!LiveOllama.IsReachable)
        {
            Skip = $"Ollama is not answering at {LiveOllama.Endpoint}api/version.";
        }
        else if (!AutocompleteLiveTests.IsInstalled(model))
        {
            Skip = $"{model} is not installed.";
        }
    }
}

/// <summary>
/// The autocomplete service against the local Ollama server (design sections 6, 7 and 13). Skipped when Ollama does not
/// answer or the model is not installed. The latency tests write their figures to the test output; run them with
/// <c>dotnet test --filter Category=Live --logger "console;verbosity=detailed"</c>.
/// </summary>
[Trait("Category", "Live")]
public sealed class AutocompleteLiveTests(ITestOutputHelper output)
{
    public const string Small = "qwen2.5-coder:1.5b";
    public const string Large = "qwen2.5-coder:7b";

    private static readonly TimeSpan Budget = TimeSpan.FromMinutes(5);

    // The caret sits in the middle of a statement in the second of two C# files joined into one long document, so the
    // 2,048-token budget is exercised; the "typing" continues with the file's real text.
    private const string Typed = "vertices[(i + 1) % vertices.Count];";

    public static bool IsInstalled(string model)
    {
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(LiveOllama.Endpoint), Timeout = TimeSpan.FromSeconds(3) };
            using var tags = JsonDocument.Parse(http.GetStringAsync("api/tags").GetAwaiter().GetResult());
            return tags.RootElement.GetProperty("models").EnumerateArray().Any(m => string.Equals(m.GetProperty("name").GetString(), model, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return false;
        }
    }

    [LiveOllamaModelFact(Small)]
    public async Task CSharpPrefixAndSuffix_YieldAPlausibleCompletion()
    {
        using var live = new LiveGateway(Small, GpuTierSetting.Auto);
        using var cts = new CancellationTokenSource(Budget);
        var request = Request("public static class MathUtil\n{\n    public static int Add(int a, int b)\n    {\n        return ", ";\n    }\n}\n", "c1");

        var chunks = await live.Gateway.CompleteAsync(request, cts.Token).CollectAsync(cts.Token);

        var text = string.Concat(chunks.Select(c => c.Text));
        output.WriteLine($"Completion: [{text}]");
        Assert.Contains("a", text, StringComparison.Ordinal);
        Assert.Contains("b", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", text, StringComparison.Ordinal);
        var record = Assert.Single(live.Log.GetRecent());
        Assert.Equal(OutboundOutcome.Succeeded, record.Outcome);
        Assert.True(record.IsLocal);
        Assert.Equal(chunks[^1].PromptTokens, record.EstimatedPromptTokens);
    }

    [LiveOllamaModelFact(Small)]
    public async Task MultiLine_ProducesSeveralLines()
    {
        using var live = new LiveGateway(Small, GpuTierSetting.Auto);
        using var cts = new CancellationTokenSource(Budget);
        var request = Request(
            "using System.Collections.Generic;\n\npublic static class Stats\n{\n    /// <summary>Returns the arithmetic mean of the values, or 0 for an empty list.</summary>\n    public static double Mean(IReadOnlyList<double> values)\n    {\n",
            "    }\n}\n",
            "m1");
        request.AllowMultiLine = true;

        var chunks = await live.Gateway.CompleteAsync(request, cts.Token).CollectAsync(cts.Token);

        var text = string.Concat(chunks.Select(c => c.Text));
        output.WriteLine($"Completion:\n{text}");
        Assert.True(text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length >= 2, text);
        Assert.True(text.Split('\n').Length <= AutocompleteBudget.MultiLineMaxLines);
    }

    [LiveOllamaModelFact(Small)]
    public async Task SecondKeystroke_EvaluatesLessOfThePrompt_BecauseThePrefixIsCached()
    {
        // The prompt the gateway would send, posted to /api/generate exactly as the Ollama adapter does, so the response's
        // prompt_eval_duration can be read. A unique marker at the top makes the first request a cold prompt.
        using var http = new HttpClient { BaseAddress = new Uri(LiveOllama.Endpoint), Timeout = Budget };
        var (prefix, suffix) = Document();
        var marker = $"// run {Guid.NewGuid():N}\n";
        var counter = TokenCounter.Default.ForModel(Small);

        // Load the model first so neither measured request includes the load.
        await GenerateAsync(http, Small, "int x = ", ";", numGpu: null);

        var first = AutocompletePromptBuilder.Build(Request(marker + prefix, suffix, "k1"), AutocompleteBudget.GpuPromptTokens, counter);
        var second = AutocompletePromptBuilder.Build(Request(marker + prefix + Typed[..1], suffix, "k2"), AutocompleteBudget.GpuPromptTokens, counter);
        Assert.StartsWith(first.PromptPrefix, second.PromptPrefix, StringComparison.Ordinal);

        var cold = await GenerateAsync(http, Small, first.PromptPrefix, first.Suffix, numGpu: null);
        var warm = await GenerateAsync(http, Small, second.PromptPrefix, second.Suffix, numGpu: null);

        output.WriteLine($"Budgeted prompt: {first.PromptTokens} estimated tokens; Ollama counted {cold.PromptEvalCount}.");
        output.WriteLine($"First request: prompt_eval_count {cold.PromptEvalCount}, prompt_eval_duration {cold.PromptEvalMs:F1} ms");
        output.WriteLine($"Second request (one more character): prompt_eval_count {warm.PromptEvalCount}, prompt_eval_duration {warm.PromptEvalMs:F1} ms");
        Assert.True(cold.PromptEvalCount <= AutocompleteBudget.GpuPromptTokens, $"{cold.PromptEvalCount} tokens");
        Assert.True(warm.PromptEvalMs < cold.PromptEvalMs, $"{warm.PromptEvalMs} ms is not below {cold.PromptEvalMs} ms");
    }

    [LiveOllamaModelFact(Small)]
    public Task Latency_Small_OnTheGpu() => MeasureAsync(Small, GpuTierSetting.Auto, "local 1.5B as this machine runs it (GPU, 2,048-token budget)");

    [LiveOllamaModelFact(Small)]
    public Task Latency_Small_WithTheCpuBudget() => MeasureAsync(Small, GpuTierSetting.Cpu, "local 1.5B with the CPU budget (1,024 tokens; the model still runs where Ollama put it)");

    [LiveOllamaModelFact(Large)]
    public async Task Latency_Large_OnTheGpu()
    {
        await MeasureAsync(Large, GpuTierSetting.Auto, "local 7B on the GPU (2,048-token budget)");

        using var http = new HttpClient { BaseAddress = new Uri(LiveOllama.Endpoint), Timeout = TimeSpan.FromSeconds(10) };
        output.WriteLine("api/ps after the run: " + await http.GetStringAsync("api/ps"));
    }

    [LiveOllamaModelFact(Small)]
    public async Task Latency_Small_OnTheCpu_Direct()
    {
        // The Gate 1 row for the 1.5B model is CPU-only. The adapter cannot force the CPU, so this posts the gateway's
        // 1,024-token prompt to /api/generate with num_gpu 0 (Ollama reloads the model on the CPU for it).
        using var http = new HttpClient { BaseAddress = new Uri(LiveOllama.Endpoint), Timeout = Budget };
        var (prefix, suffix) = Document();
        var counter = TokenCounter.Default.ForModel(Small);
        await GenerateAsync(http, Small, "int x = ", ";", numGpu: 0);

        var first = new List<double>();
        var total = new List<double>();
        for (var i = 0; i <= 10; i++)
        {
            var prompt = AutocompletePromptBuilder.Build(Request(prefix + Typed[..(i % Typed.Length)], suffix, $"cpu{i}"), AutocompleteBudget.CpuPromptTokens, counter);
            var result = await GenerateStreamingAsync(http, Small, prompt.PromptPrefix, prompt.Suffix, numGpu: 0);
            if (i > 0)
            {
                first.Add(result.FirstTokenMs);
                total.Add(result.TotalMs);
            }
        }

        Report("local 1.5B forced onto the CPU (num_gpu 0, 1,024-token budget)", first, total, "600 ms / 1,500 ms");
        await UnloadAsync(http, Small);
    }

    private async Task MeasureAsync(string model, GpuTierSetting tier, string label)
    {
        using var live = new LiveGateway(model, tier);
        using var cts = new CancellationTokenSource(Budget);
        var (prefix, suffix) = Document();

        // Warm up: load the model (keep_alive -1) and fill its prompt cache.
        await live.Gateway.CompleteAsync(Request(prefix, suffix, "warm"), cts.Token).CollectAsync(cts.Token);

        // Ten keystrokes that each miss the cache (it is cleared), so every figure is a model request.
        var first = new List<double>();
        var total = new List<double>();
        var tokens = new List<int>();
        for (var i = 1; i <= 10; i++)
        {
            live.Cache.Clear();
            var watch = Stopwatch.StartNew();
            double? firstMs = null;
            await foreach (var chunk in live.Gateway.CompleteAsync(Request(prefix + Typed[..i], suffix, $"k{i}"), cts.Token).WithCancellation(cts.Token))
            {
                firstMs ??= watch.Elapsed.TotalMilliseconds;
                if (chunk.IsFinal)
                {
                    tokens.Add(chunk.PromptTokens);
                }
            }

            first.Add(firstMs ?? watch.Elapsed.TotalMilliseconds);
            total.Add(watch.Elapsed.TotalMilliseconds);
        }

        Report($"{label}, prompt {tokens.Min()}-{tokens.Max()} estimated tokens", first, total, model == Large ? "300 ms / 800 ms" : "600 ms / 1,500 ms");
        Assert.All(tokens, t => Assert.True(t <= (tier == GpuTierSetting.Cpu ? AutocompleteBudget.CpuPromptTokens : AutocompleteBudget.GpuPromptTokens)));

        // Steady typing with the cache on: how many keystrokes the cache serves.
        live.Cache.Clear();
        var before = live.Gateway.Counters.Snapshot();
        for (var i = 1; i <= Typed.Length; i++)
        {
            await live.Gateway.CompleteAsync(Request(prefix + Typed[..i], suffix, $"t{i}"), cts.Token).CollectAsync(cts.Token);
        }

        var after = live.Gateway.Counters.Snapshot();
        var hits = (after.CacheHits - before.CacheHits) + (after.TypeThroughHits - before.TypeThroughHits);
        var lookups = hits + (after.CacheMisses - before.CacheMisses);
        output.WriteLine($"Steady typing of {Typed.Length} characters: {hits} of {lookups} served from the cache ({hits / (double)lookups:P0}).");
        output.WriteLine($"Measured prompt-evaluation rate: {live.Gateway.PromptEvaluationRates.RateOf("ollama/" + model):F0} tokens/s (from time to first token).");
    }

    private void Report(string label, List<double> first, List<double> total, string target)
    {
        output.WriteLine($"{label}: first token p50 {Percentile(first, 50):F0} ms, p95 {Percentile(first, 95):F0} ms; total p50 {Percentile(total, 50):F0} ms, p95 {Percentile(total, 95):F0} ms (Gate 1 target p50/p95 {target}).");
        output.WriteLine("  first token ms: " + string.Join(", ", first.Select(v => v.ToString("F0", CultureInfo.InvariantCulture))));
        output.WriteLine("  total ms:       " + string.Join(", ", total.Select(v => v.ToString("F0", CultureInfo.InvariantCulture))));
    }

    private static double Percentile(List<double> values, double percentile)
    {
        // Nearest-rank percentile.
        var sorted = values.Order().ToList();
        var rank = (int)Math.Ceiling(percentile / 100 * sorted.Count);
        return sorted[Math.Clamp(rank - 1, 0, sorted.Count - 1)];
    }

    private static (string Prefix, string Suffix) Document()
    {
        var text = TokenCounterTests.ReadCorpus("InventoryService.cs") + "\n" + TokenCounterTests.ReadCorpus("GeometryTests.cs");
        const string Caret = "            var b = ";
        var at = text.IndexOf(Caret, StringComparison.Ordinal) + Caret.Length;
        var suffix = text[at..];
        return (text[..at], suffix[(suffix.IndexOf('\n', StringComparison.Ordinal))..]);
    }

    private static CompletionRequest Request(string prefix, string suffix, string id) => new()
    {
        RequestId = id,
        Prefix = prefix,
        Suffix = suffix,
        LanguageId = "csharp",
        FilePath = @"C:\src\Geometry.cs",
        CaretLine = prefix.Count(c => c == '\n'),
        OpenDocuments = [@"C:\src\InventoryService.cs", @"C:\src\Polygon.cs"],
        TaskClass = TaskClass.Autocomplete,
    };

    private static JsonObject Body(string model, string prefix, string suffix, int? numGpu, bool stream)
    {
        var options = new JsonObject { ["num_ctx"] = 4096, ["num_predict"] = 64, ["temperature"] = 0.2, ["stop"] = new JsonArray("\n") };
        if (numGpu is { } gpu)
        {
            options["num_gpu"] = gpu;
        }

        return new JsonObject
        {
            ["model"] = model,
            ["prompt"] = prefix,
            ["suffix"] = suffix,
            ["raw"] = false,
            ["stream"] = stream,
            ["keep_alive"] = -1,
            ["options"] = options,
        };
    }

    private static async Task<(int PromptEvalCount, double PromptEvalMs)> GenerateAsync(HttpClient http, string model, string prefix, string suffix, int? numGpu)
    {
        using var response = await http.PostAsJsonAsync("api/generate", Body(model, prefix, suffix, numGpu, stream: false));
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;
        return (
            root.TryGetProperty("prompt_eval_count", out var count) ? count.GetInt32() : 0,
            root.TryGetProperty("prompt_eval_duration", out var duration) ? duration.GetInt64() / 1e6 : 0);
    }

    private static async Task<(double FirstTokenMs, double TotalMs)> GenerateStreamingAsync(HttpClient http, string model, string prefix, string suffix, int? numGpu)
    {
        var watch = Stopwatch.StartNew();
        using var message = new HttpRequestMessage(HttpMethod.Post, "api/generate") { Content = JsonContent.Create(Body(model, prefix, suffix, numGpu, stream: true)) };
        using var response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());
        double? first = null;
        while (await reader.ReadLineAsync() is { } line)
        {
            using var json = JsonDocument.Parse(line);
            if (json.RootElement.TryGetProperty("response", out var piece) && piece.GetString() is { Length: > 0 })
            {
                first ??= watch.Elapsed.TotalMilliseconds;
            }
        }

        return (first ?? watch.Elapsed.TotalMilliseconds, watch.Elapsed.TotalMilliseconds);
    }

    private static async Task UnloadAsync(HttpClient http, string model)
    {
        using var response = await http.PostAsJsonAsync("api/generate", new JsonObject { ["model"] = model, ["keep_alive"] = 0 });
    }

    /// <summary>A gateway over the real Ollama adapter, with the Local-fast profile pinned to one model.</summary>
    private sealed class LiveGateway : IDisposable
    {
        private readonly ProviderRegistry _registry;

        public LiveGateway(string model, GpuTierSetting tier)
        {
            var settings = new PillionSettings();
            settings.Providers.Add(new ProviderSettings { Id = "ollama", Kind = ProviderKind.Ollama, Endpoint = LiveOllama.Endpoint });
            settings.Routing.Profiles[Profile.LocalFast] = "ollama/" + model;
            settings.Models.GpuTier = tier;
            var options = new ModelGatewayOptions();
            _registry = new ProviderRegistry(new StaticGatewaySettingsSource(settings), new Common.Secrets.InMemorySecretStore(), [new OllamaProviderFactory()], ModelCatalog.Default);
            var inventory = new ModelInventory();
            var breakers = new CircuitBreakerRegistry(options);
            Log = new InMemoryOutboundLog(options);
            Cache = new AutocompleteCache(options);
            var router = new ModelRouter(_registry, ModelCatalog.Default, inventory, new AlwaysReachable(), breakers, new NoCostCaps());
            Gateway = new ModelGateway(_registry, router, inventory, ModelCatalog.Default, PromptTemplates.Default, Cache, breakers, new ConcurrencyLimiter(options), Log, new AlwaysReachable(), new NoCostCaps(), new NoProgressSink(), options);
            Gateway.ListModelsAsync(CancellationToken.None).GetAwaiter().GetResult();
        }

        public ModelGateway Gateway { get; }

        public InMemoryOutboundLog Log { get; }

        public AutocompleteCache Cache { get; }

        public void Dispose() => _registry.Dispose();
    }
}
