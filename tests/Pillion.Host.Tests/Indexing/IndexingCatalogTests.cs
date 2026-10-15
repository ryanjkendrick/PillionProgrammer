using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Pillion.Common;
using Pillion.Common.Secrets;
using Pillion.Common.Settings;
using Pillion.ContextEngine.Retrieval;
using Pillion.Contracts;
using Pillion.Host.Context.Retrieval;
using Pillion.Host.Indexing;
using Pillion.Host.Tests.TestSupport;
using Pillion.Host.WorkspaceQueries;
using Pillion.ModelGateway;
using Pillion.ModelGateway.Providers.Bundled;
using Xunit.Abstractions;

namespace Pillion.Host.Tests.Indexing;

/// <summary>
/// The pipeline's open indexes serve retrieval (work item 2.11's catalog) and the workspace queries' fallback (2.14's symbol
/// graphs), so searches read the index the pipeline writes; and the query-embedding time through the bundled model.
/// </summary>
public sealed class IndexingCatalogTests(ITestOutputHelper output) : IDisposable
{
    private readonly IndexingWorkspace _workspace = new();

    public void Dispose() => _workspace.Dispose();

    [Fact]
    public async Task TheComposedHost_UsesTheLiveCatalogForRetrievalAndTheSymbolGraphs()
    {
        using var data = new TempDirectory();
        await using var host = await InProcessHost.StartAsync(services =>
        {
            services.Replace(ServiceDescriptor.Singleton(new PillionPaths(data.Path)));
            services.Replace(ServiceDescriptor.Singleton<ISecretStore>(new InMemorySecretStore()));
        });

        Assert.IsType<LiveIndexCatalog>(host.Services.GetRequiredService<IIndexCatalog>());
        Assert.IsType<LiveIndexCatalog>(host.Services.GetRequiredService<ISymbolGraphProvider>());
    }

    [Fact]
    public async Task Retrieval_SearchesTheIndexesThePipelineHasOpen_AndTheGraphsServeTheFallback()
    {
        Fixtures.Copy("shop", _workspace.Repository);
        var embedder = new FakeEmbedder();
        await using var harness = new IndexingHarness(_workspace.Data, embedder);
        await harness.StartAsync(_workspace.Repository);
        await harness.WaitForIdleAsync();

        var catalog = new LiveIndexCatalog(harness.Service, harness.Flags);
        var index = Assert.Single(await catalog.GetIndexesAsync([], CancellationToken.None));
        Assert.Same(harness.Root.Storage, index.Storage);
        Assert.NotNull(index.Graph);
        Assert.Single(await catalog.GetIndexesAsync([_workspace.Repository + Path.DirectorySeparatorChar], CancellationToken.None));
        Assert.Empty(await catalog.GetIndexesAsync([Path.Combine(_workspace.Repository, "elsewhere")], CancellationToken.None));

        var graphs = await catalog.GetGraphsAsync(CancellationToken.None);
        var graph = Assert.Single(graphs);
        Assert.NotEmpty(graph.Graph.FindDefinitions("PaymentGateway.Charge"));

        var retrieval = new RetrievalService(catalog, harness.Flags, new FakeQueryEmbedder(), new RetrievalOptions { TimeBudget = TimeSpan.FromSeconds(30) });
        var response = await retrieval.SearchDetailedAsync(new SearchRequest { Query = "how does the order service charge the card", MaxResults = 5 }, CancellationToken.None);
        Assert.NotEmpty(response.Results);
        Assert.Contains(response.Results, r => r.FilePath.EndsWith("PaymentGateway.cs", StringComparison.Ordinal) || r.FilePath.EndsWith("OrderService.cs", StringComparison.Ordinal));

        harness.Flags.Disabled[FeatureNames.Indexing] = true;
        Assert.Empty(await catalog.GetIndexesAsync([], CancellationToken.None));
        Assert.Empty(await catalog.GetGraphsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task OpeningAnIndex_WarmsItsVectorCacheOffTheQueryPath()
    {
        Fixtures.Copy("shop", _workspace.Repository);
        await using (var first = new IndexingHarness(_workspace.Data))
        {
            await first.StartAsync(_workspace.Repository);
            await first.WaitForIdleAsync();
        }

        // A new session: the index is opened again and warmed before anyone searches.
        await using var second = new IndexingHarness(_workspace.Data);
        await second.StartAsync(_workspace.Repository);
        await second.WaitForAsync(s => s.Roots.Length == 1, what: "the index to open");
        var deadline = Stopwatch.StartNew();
        while (second.Root.Storage.VectorCacheBytes == 0)
        {
            Assert.True(deadline.Elapsed < TimeSpan.FromSeconds(30), "The vector cache was not warmed.");
            await Task.Delay(20);
        }
    }

    [Fact]
    [Trait("Category", "Model")]
    public async Task QueryEmbedding_ThroughTheBundledModel_IsMeasured()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IGatewaySettingsSource>(new StaticGatewaySettingsSource(new PillionSettings()));
        services.AddSingleton<ISecretStore>(new InMemorySecretStore());
        services.AddSingleton(new BundledModelOptions
        {
            InstallModelsDirectory = Path.Combine(AppContext.BaseDirectory, "models"),
            UserModelsDirectory = null,
            ExecutionProvider = BundledExecutionProvider.Cpu,
            IdleUnloadAfter = TimeSpan.Zero,
        });
        services.AddPillionModelGateway();
        await using var provider = services.BuildServiceProvider();
        var embedder = new GatewayQueryEmbedder(provider.GetRequiredService<IModelGateway>());
        string[] questions =
        [
            "how is the customer's card charged?", "where are orders validated", "OrderService.Place", "which class talks to the card processor",
            "low stock report threshold", "total area of the shapes", "how do refunds work", "what does the inventory count",
        ];

        // The first call loads the model; it is reported apart.
        var load = Stopwatch.StartNew();
        await embedder.EmbedQueryAsync("warm up", "bundled/bge-small-en-v1.5", null, CancellationToken.None);
        load.Stop();
        var times = new List<double>();
        for (var round = 0; round < 5; round++)
        {
            foreach (var question in questions)
            {
                var started = Stopwatch.GetTimestamp();
                var vector = await embedder.EmbedQueryAsync(question, "bundled/bge-small-en-v1.5", null, CancellationToken.None);
                times.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                Assert.Equal(384, vector.Vector.Length);
            }
        }

        times.Sort();
        var text = string.Create(CultureInfo.InvariantCulture, $"Query embedding through the gateway and the bundled model on the CPU: first call (model load) {load.Elapsed.TotalMilliseconds:0} ms; then {times.Count} queries p50 {times[times.Count / 2]:0.0} ms, p95 {times[(int)(times.Count * 0.95)]:0.0} ms, max {times[^1]:0.0} ms.");
        output.WriteLine(text);
        Console.WriteLine(text);
    }

    private sealed class FakeQueryEmbedder : IQueryEmbedder
    {
        public Task<QueryEmbedding> EmbedQueryAsync(string text, string? modelId, int? dimensions, CancellationToken cancellationToken) =>
            Task.FromResult(new QueryEmbedding(FakeEmbedder.Vector(text).Quantized, modelId ?? "fake/hashed-words"));
    }
}
