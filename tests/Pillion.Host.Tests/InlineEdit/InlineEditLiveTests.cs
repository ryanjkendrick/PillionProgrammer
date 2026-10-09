using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Pillion.Common.Secrets;
using Pillion.Common.Settings;
using Pillion.Contracts;
using Pillion.Host.InlineEdit;
using Pillion.ModelGateway;
using Pillion.ModelGateway.Editing;
using Pillion.ModelGateway.Providers.Ollama;
using Xunit.Abstractions;
using static Pillion.Host.Tests.InlineEdit.InlineEditTestSupport;

namespace Pillion.Host.Tests.InlineEdit;

/// <summary>A fact that needs the local Ollama server with a given model installed.</summary>
public sealed class LiveInlineEditFactAttribute : FactAttribute
{
    public LiveInlineEditFactAttribute(string model)
    {
        var models = InlineEditLiveTests.InstalledModels();
        if (models is null)
        {
            Skip = $"Ollama is not answering at {InlineEditLiveTests.Endpoint}api/version.";
        }
        else if (!models.Contains(model, StringComparer.OrdinalIgnoreCase))
        {
            Skip = $"{model} is not installed.";
        }
        else if (InlineEditLiveTests.OtherLoadedModel(model) is { } other)
        {
            // On the 8 GB development machine Ollama waits indefinitely for room rather than evicting the resident model.
            Skip = $"{model} is not loaded and {other} is; on this machine Ollama waits for room instead of swapping them.";
        }
    }
}

/// <summary>
/// Inline edits against the local Ollama server through the real gateway and engine (Gate 1 row: first line of the
/// preview within 2 s p50, 5 s p95). Skipped when Ollama does not answer or the model is not installed. The latencies
/// are written to the test output: <c>dotnet test --filter Category=Live --logger "console;verbosity=detailed"</c>.
/// </summary>
[Trait("Category", "Live")]
public sealed class InlineEditLiveTests(ITestOutputHelper output)
{
    public const string Endpoint = "http://localhost:11434/";
    public const string Coder = "qwen2.5-coder:7b";
    public const string Gemma = "gemma4:e2b";

    private const int Runs = 5;
    private static readonly TimeSpan Budget = TimeSpan.FromMinutes(5);

    public static IReadOnlyList<string>? InstalledModels()
    {
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(Endpoint), Timeout = TimeSpan.FromSeconds(3) };
            using var tags = JsonDocument.Parse(http.GetStringAsync("api/tags").GetAwaiter().GetResult());
            return tags.RootElement.GetProperty("models").EnumerateArray().Select(m => m.GetProperty("name").GetString()!).ToList();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException)
        {
            return null;
        }
    }

    /// <summary>Finds another loaded model when <paramref name="model"/> itself is not loaded.</summary>
    public static string? OtherLoadedModel(string model)
    {
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(Endpoint), Timeout = TimeSpan.FromSeconds(3) };
            using var ps = JsonDocument.Parse(http.GetStringAsync("api/ps").GetAwaiter().GetResult());
            var loaded = ps.RootElement.GetProperty("models").EnumerateArray()
                .Select(m => m.GetProperty("name").GetString()!)
                .ToList();
            return loaded.Contains(model, StringComparer.OrdinalIgnoreCase) ? null : loaded.FirstOrDefault();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }

    [LiveInlineEditFact(Coder)]
    public Task Coder_ExtractsTheValidation_IntoCodeThatParses() => ExtractAsync(Coder);

    [LiveInlineEditFact(Coder)]
    public Task Coder_Doc_AddsADocComment() => DocAsync(Coder);

    [LiveInlineEditFact(Gemma)]
    public Task Gemma_ExtractsTheValidation_IntoCodeThatParses() => ExtractAsync(Gemma);

    [LiveInlineEditFact(Gemma)]
    public Task Gemma_Doc_AddsADocComment() => DocAsync(Gemma);

    private async Task ExtractAsync(string model)
    {
        using var live = new LiveEngine(model);
        await WarmUpAsync(live);
        var timings = new List<(double First, double Total)>();
        for (var i = 0; i < Runs; i++)
        {
            var (proposal, final, first, total) = await RunAsync(live, Request());
            timings.Add((first, total));
            output.WriteLine($"Run {i + 1}: first text {first:F0} ms, total {total:F0} ms, rejected blocks {final.RejectedBlocks}, warning {final.Warning ?? "none"}");
            output.WriteLine(proposal);

            Assert.Empty(RoslynSyntaxValidator.Instance.GetErrors(Before + proposal + After, "csharp", CancellationToken.None));
            Assert.NotEqual(Scope, proposal);
            Assert.Matches(@"void \w+\(Order \w+\)[\s\S]*void \w+\(Order \w+\)", proposal);
        }

        Report(model, "extract", timings);
    }

    private async Task DocAsync(string model)
    {
        using var live = new LiveEngine(model);
        await WarmUpAsync(live);
        var timings = new List<(double First, double Total)>();
        for (var i = 0; i < Runs; i++)
        {
            var (proposal, final, first, total) = await RunAsync(live, Request(string.Empty, InlineEditCommand.Doc));
            timings.Add((first, total));
            output.WriteLine($"Run {i + 1}: first text {first:F0} ms, total {total:F0} ms, rejected blocks {final.RejectedBlocks}");
            output.WriteLine(proposal);

            Assert.Contains("/// <summary>", proposal, StringComparison.Ordinal);
            Assert.Empty(RoslynSyntaxValidator.Instance.GetErrors(Before + proposal + After, "csharp", CancellationToken.None));
            Assert.Contains("_store.Add(order);", proposal, StringComparison.Ordinal);
        }

        Report(model, "/doc", timings);
    }

    private static async Task WarmUpAsync(LiveEngine live)
    {
        // Load the model with the Local-strong context size so no measured run includes the load.
        using var cts = new CancellationTokenSource(Budget);
        await foreach (var _ in live.Gateway.ChatAsync(
            new ChatRequest { RequestId = "warm-up", Messages = [new ChatMessage { Role = ChatRole.User, Content = "Say OK." }], Profile = Profile.Auto },
            TaskClass.InlineEdit,
            cts.Token))
        {
        }
    }

    private async Task<(string Proposal, InlineEditChunk Final, double FirstMs, double TotalMs)> RunAsync(LiveEngine live, InlineEditRequest request)
    {
        using var cts = new CancellationTokenSource(Budget);
        var watch = Stopwatch.StartNew();
        double? first = null;
        var chunks = new List<InlineEditChunk>();
        live.Recorder.Replies.Clear();
        try
        {
            await foreach (var chunk in live.Engine.RunAsync(request, cts.Token))
            {
                if (first is null && chunk.Text.Length > 0)
                {
                    first = watch.Elapsed.TotalMilliseconds;
                }

                chunks.Add(chunk);
            }
        }
        catch (PillionRpcException)
        {
            foreach (var reply in live.Recorder.Replies)
            {
                output.WriteLine("--- model reply ---");
                output.WriteLine(reply);
            }

            throw;
        }

        var total = watch.Elapsed.TotalMilliseconds;
        return (Assemble(chunks), chunks[^1], first ?? total, total);
    }

    private void Report(string model, string scenario, List<(double First, double Total)> timings)
    {
        static double Percentile(IEnumerable<double> values, double p)
        {
            var sorted = values.Order().ToArray();
            return sorted[(int)Math.Min(sorted.Length - 1, Math.Ceiling(p * sorted.Length) - 1)];
        }

        output.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"{model} {scenario}: first text p50 {Percentile(timings.Select(t => t.First), 0.5):F0} ms, max {timings.Max(t => t.First):F0} ms; total p50 {Percentile(timings.Select(t => t.Total), 0.5):F0} ms, max {timings.Max(t => t.Total):F0} ms ({timings.Count} warm runs)"));
    }

    private sealed class LiveEngine : IDisposable
    {
        private readonly ProviderRegistry _registry;

        public LiveEngine(string model)
        {
            var settings = new PillionSettings();
            // On the 8 GB machine the 7B coder is resident for autocomplete at num_ctx 4096 with keep_alive -1, and Ollama
            // waits indefinitely for room to load a second instance at Local-strong's default 8192. Chat shares the
            // resident model on that tier (design section 6), so the test sends the same context size.
            using var numCtx = JsonDocument.Parse("""{ "localStrong": 4096 }""");
            settings.Providers.Add(new ProviderSettings
            {
                Id = "ollama",
                Kind = ProviderKind.Ollama,
                Endpoint = Endpoint,
                Models = [model],
                ExtensionData = new Dictionary<string, JsonElement> { ["numCtx"] = numCtx.RootElement.Clone() },
            });
            settings.Routing.Profiles[Profile.LocalStrong] = "ollama/" + model;
            var options = new ModelGatewayOptions();
            _registry = new ProviderRegistry(new StaticGatewaySettingsSource(settings), new InMemorySecretStore(), [new OllamaProviderFactory()], ModelCatalog.Default);
            var inventory = new ModelInventory();
            var breakers = new CircuitBreakerRegistry(options);
            var router = new ModelRouter(_registry, ModelCatalog.Default, inventory, new AlwaysReachable(), breakers, new NoCostCaps());
            Gateway = new ModelGateway.ModelGateway(_registry, router, inventory, ModelCatalog.Default, PromptTemplates.Default, new AutocompleteCache(options), breakers, new ConcurrencyLimiter(options), new InMemoryOutboundLog(options), new AlwaysReachable(), new NoCostCaps(), new NoProgressSink(), options);
            Gateway.ListModelsAsync(CancellationToken.None).GetAwaiter().GetResult();
            Recorder = new RecordingGateway(Gateway);
            Engine = new InlineEditEngine(Recorder, new InlineEditPromptBuilder(), RoslynSyntaxValidator.Instance, new InlineEditConversations());
        }

        public ModelGateway.ModelGateway Gateway { get; }

        public InlineEditEngine Engine { get; }

        public RecordingGateway Recorder { get; }

        public void Dispose() => _registry.Dispose();
    }

    /// <summary>Passes calls to the real gateway and keeps each reply's text, to show it when a run fails.</summary>
    private sealed class RecordingGateway(IModelGateway inner) : IModelGateway
    {
        public List<string> Replies { get; } = [];

        public async IAsyncEnumerable<ChatChunk> ChatAsync(ChatRequest request, TaskClass taskClass, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var text = new System.Text.StringBuilder();
            await foreach (var chunk in inner.ChatAsync(request, taskClass, cancellationToken))
            {
                text.Append(chunk.Text);
                if (chunk.IsFinal)
                {
                    Replies.Add(text.ToString());
                }

                yield return chunk;
            }
        }

        public IAsyncEnumerable<CompletionChunk> CompleteAsync(CompletionRequest request, CancellationToken cancellationToken) => inner.CompleteAsync(request, cancellationToken);

        public Task<IReadOnlyList<ModelDescriptor>> ListModelsAsync(CancellationToken cancellationToken) => inner.ListModelsAsync(cancellationToken);

        public Task<ProviderTestResult> TestProviderAsync(string providerId, CancellationToken cancellationToken) => inner.TestProviderAsync(providerId, cancellationToken);
    }
}
