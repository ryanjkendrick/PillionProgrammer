using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Pillion.Common;
using Pillion.Common.Secrets;
using Pillion.Common.Settings;
using Pillion.ContextEngine.Retrieval;
using Pillion.Contracts;
using Pillion.Host.Context.Retrieval;
using Pillion.Host.Tests.TestSupport;
using Pillion.Indexing.Storage;
using Pillion.ModelGateway;
using StreamJsonRpc;
using Xunit.Abstractions;

namespace Pillion.Host.Tests;

/// <summary>
/// Hybrid retrieval in Pillion Host (work item 2.11): the read-only index catalog, the mapping to the contract, the
/// <c>retrieval</c> flag, and SearchAsync and SearchDetailedAsync over the real pipe on a real index folder.
/// </summary>
public sealed class RetrievalTests : IAsyncDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

    private readonly TempDirectory _data = new();
    private readonly TempDirectory _repo = new();
    private readonly Flags _flags = new();
    private readonly List<IAsyncDisposable> _catalogs = [];

    public async ValueTask DisposeAsync()
    {
        foreach (var catalog in _catalogs)
        {
            await catalog.DisposeAsync();
        }

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        _data.Dispose();
        _repo.Dispose();
    }

    [Fact]
    public void AHit_MapsToZeroBasedLinesAndTheOpaqueChunkId()
    {
        var result = RetrievalService.ToResult(new RetrievalHit
        {
            IndexId = "0123456789abcdef0123456789abcdef",
            ChunkId = 42,
            FilePath = @"C:\repo\src\a.cs",
            RelativePath = "src/a.cs",
            StartLine = 10,
            EndLine = 20,
            Breadcrumb = "src/a.cs > N > T > M",
            Kind = "member",
            Partition = "src",
            Language = "csharp",
            Snippet = "snippet",
            Text = "text",
            LexicalRank = 2,
            VectorRank = 3,
            FusedScore = 0.03,
            Score = 0.05,
        });

        Assert.Equal((9, 19), (result.StartLine, result.EndLine));
        Assert.Equal("0123456789abcdef0123456789abcdef:42", result.ChunkId);
        Assert.Equal((2, 3), (result.LexicalRank, result.VectorRank));
        Assert.Equal((0.03, 0.05), (result.FusedScore, result.Score));
        Assert.Equal(("src/a.cs > N > T > M", "member", "src", "csharp", "snippet", "text"), (result.Breadcrumb, result.Kind, result.Partition, result.Language, result.Snippet, result.Text));
        Assert.Equal(@"C:\repo\src\a.cs", result.FilePath);
    }

    [Theory]
    [InlineData(SearchMode.Hybrid, RetrievalSearchMode.Hybrid)]
    [InlineData(SearchMode.Lexical, RetrievalSearchMode.Lexical)]
    [InlineData(SearchMode.Vector, RetrievalSearchMode.Vector)]
    [InlineData(SearchMode.Symbol, RetrievalSearchMode.Symbol)]
    public void ARequest_MapsToTheRetrieverQuery(SearchMode mode, RetrievalSearchMode expected)
    {
        var query = RetrievalService.ToQuery(new SearchRequest
        {
            Query = "q",
            MaxResults = 7,
            Mode = mode,
            Partitions = ["src"],
            Languages = ["csharp"],
            MaxPerFile = 2,
            IncludeText = true,
            FocusFiles = [@"C:\repo\a.cs"],
        });

        Assert.Equal(expected, query.Mode);
        Assert.Equal(("q", 7, 2, true), (query.Text, query.MaxResults, query.MaxPerFile, query.IncludeText));
        Assert.Equal(["src"], query.Partitions);
        Assert.Equal(["csharp"], query.Languages);
        Assert.Equal([@"C:\repo\a.cs"], query.FocusFiles);
        Assert.Null(query.TimeBudget);
    }

    [Fact]
    public void TheEmbeddingModelMetaKey_IsTheOneIndexingWrites() =>
        Assert.Equal(IndexMetaKeys.EmbeddingModel, StorageRetrievalIndex.EmbeddingModelMetaKey);

    [Fact]
    public async Task WithTheRetrievalFlagOff_SearchesAreRefusedNamingTheFlag()
    {
        _flags.Disabled.Add(FeatureNames.Retrieval);
        var service = new RetrievalService(Catalog(), _flags);

        var detailed = await Assert.ThrowsAsync<PillionRpcException>(() => service.SearchDetailedAsync(new SearchRequest { Query = "x" }, CancellationToken.None));
        var plain = await Assert.ThrowsAsync<PillionRpcException>(() => service.SearchAsync(new SearchRequest { Query = "x" }, CancellationToken.None));

        Assert.Equal(PillionErrorCode.PolicyDenied, detailed.Error.Code);
        Assert.Contains("feature retrieval", detailed.Error.Message, StringComparison.Ordinal);
        Assert.Equal(PillionErrorCode.PolicyDenied, plain.Error.Code);
    }

    [Fact]
    public async Task TheReadOnlyCatalog_OpensTheIndexOfARoot_AndTheSearchFindsTheCode()
    {
        var indexId = await TestIndex.BuildAsync(new PillionPaths(_data.Path), _repo.Path);
        var service = new RetrievalService(Catalog(), _flags);

        var response = await service.SearchDetailedAsync(new SearchRequest { Query = "where do we Restock a product", IncludeText = true, Roots = [_repo.Path] }, CancellationToken.None);

        Assert.False(response.Degraded); // the index has no vectors, so there is no vector leg to miss
        var top = response.Results[0];
        Assert.Equal(Path.Combine(_repo.Path, "src", "Inventory", "StockRoom.cs"), top.FilePath);
        Assert.EndsWith("> Restock", top.Breadcrumb, StringComparison.Ordinal);
        Assert.StartsWith(indexId + ":", top.ChunkId, StringComparison.Ordinal);
        Assert.True(RetrievalChunkIds.TryParse(top.ChunkId, out _, out _));
        var restockLine = TestIndex.LineOf("src/Inventory/StockRoom.cs", "public void Restock") - 1; // zero-based
        Assert.InRange(restockLine, top.StartLine, top.EndLine);
        Assert.Contains("public void Restock", top.Text, StringComparison.Ordinal);
        Assert.True(top.LexicalRank > 0);
        Assert.Equal(0, top.VectorRank);
        Assert.Equal("csharp", top.Language);
        Assert.Equal("src", top.Partition);
        Assert.True(response.TotalMs >= response.FusionMs);

        var plain = await service.SearchAsync(new SearchRequest { Query = "where do we Restock a product", Roots = [_repo.Path] }, CancellationToken.None);
        Assert.Equal(response.Results.Select(r => r.ChunkId), plain.Select(r => r.ChunkId));
        Assert.All(plain, r => Assert.Null(r.Text));
    }

    [Fact]
    public async Task TheCatalog_ServesEveryIndexWithoutRoots_NoneForAnotherRoot_AndNothingWithIndexingOff()
    {
        await TestIndex.BuildAsync(new PillionPaths(_data.Path), _repo.Path);
        using var elsewhere = new TempDirectory();
        var catalog = Catalog();

        Assert.Single(await catalog.GetIndexesAsync([], CancellationToken.None));
        Assert.Single(await catalog.GetIndexesAsync([_repo.Path.ToUpperInvariant() + "\\"], CancellationToken.None)); // normalised like the index identity
        Assert.Empty(await catalog.GetIndexesAsync([elsewhere.Path], CancellationToken.None));
        Assert.Same((await catalog.GetIndexesAsync([], CancellationToken.None))[0].Storage, (await catalog.GetIndexesAsync([], CancellationToken.None))[0].Storage);

        _flags.Disabled.Add(FeatureNames.Indexing);
        Assert.Empty(await catalog.GetIndexesAsync([], CancellationToken.None));
    }

    [Fact]
    public async Task WithoutAnyIndex_TheSearchAnswersEmpty()
    {
        var service = new RetrievalService(Catalog(), _flags);

        var response = await service.SearchDetailedAsync(new SearchRequest { Query = "anything" }, CancellationToken.None);

        Assert.Empty(response.Results);
        Assert.False(response.Degraded);
    }

    [Fact]
    public async Task OverThePipe_SearchDetailedAndSearch_AnswerFromTheIndexFolder()
    {
        await TestIndex.BuildAsync(new PillionPaths(_data.Path), _repo.Path);
        await using var host = await StartAsync();
        await using var client = await host.ConnectAndHandshakeAsync();
        var context = client.Attach<IContextService>();

        var detailed = await context.SearchDetailedAsync(new SearchRequest { Query = "ComputeTotal of an order", Mode = SearchMode.Lexical, MaxResults = 3 }, CancellationToken.None).WaitAsync(Wait);
        var plain = await context.SearchAsync(new SearchRequest { Query = "ComputeTotal of an order", MaxResults = 3 }, CancellationToken.None).WaitAsync(Wait);

        Assert.InRange(detailed.Results.Length, 1, 3);
        Assert.EndsWith("OrderService.cs", detailed.Results[0].FilePath, StringComparison.Ordinal);
        Assert.EndsWith("> ComputeTotal", detailed.Results[0].Breadcrumb, StringComparison.Ordinal);
        Assert.Equal(0, detailed.Results[0].VectorRank);
        Assert.True(detailed.TotalMs > 0);
        Assert.EndsWith("OrderService.cs", plain[0].FilePath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OverThePipe_WithRetrievalOff_TheSearchIsPolicyDenied()
    {
        _flags.Disabled.Add(FeatureNames.Retrieval);
        await using var host = await StartAsync();
        await using var client = await host.ConnectAndHandshakeAsync();

        var ex = await Assert.ThrowsAsync<RemoteInvocationException>(() =>
            client.Attach<IContextService>().SearchDetailedAsync(new SearchRequest { Query = "x" }, CancellationToken.None).WaitAsync(Wait));

        Assert.Equal(PillionErrorCode.PolicyDenied, Assert.IsType<PillionError>(ex.DeserializedErrorData).Code);
    }

    private ReadOnlyIndexCatalog Catalog()
    {
        var catalog = new ReadOnlyIndexCatalog(new PillionPaths(_data.Path), _flags);
        _catalogs.Add(catalog);
        return catalog;
    }

    private Task<InProcessHost> StartAsync() => InProcessHost.StartAsync(services =>
    {
        services.Replace(ServiceDescriptor.Singleton(new PillionPaths(_data.Path)));
        services.Replace(ServiceDescriptor.Singleton<ISecretStore>(new InMemorySecretStore()));
        services.Replace(ServiceDescriptor.Singleton<IFeatureFlags>(_flags));
    });

    internal sealed class Flags : IFeatureFlags
    {
        public event EventHandler? Changed
        {
            add { }
            remove { }
        }

        public ConcurrentBag<string> Disabled { get; } = [];

        public bool IsEnabled(string feature) => !Disabled.Contains(feature);
    }
}

/// <summary>
/// Hybrid retrieval end to end with the bundled ONNX model (bge-small-en-v1.5, from the build output): the index is
/// embedded through the host's own Model Gateway, and the question goes over the pipe and is embedded with the index's
/// model. Marked <c>Category=Model</c>: the model files are needed.
/// </summary>
[Trait("Category", "Model")]
public sealed class RetrievalModelTests(ITestOutputHelper output) : IAsyncDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(60);

    private readonly TempDirectory _data = new();
    private readonly TempDirectory _repo = new();

    public ValueTask DisposeAsync()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        _data.Dispose();
        _repo.Dispose();
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task AQuestion_IsEmbeddedWithTheIndexModel_AndFindsTheCodeBySimilarity()
    {
        var flags = new RetrievalTests.Flags();
        await using var host = await InProcessHost.StartAsync(services =>
        {
            services.Replace(ServiceDescriptor.Singleton(new PillionPaths(_data.Path)));
            services.Replace(ServiceDescriptor.Singleton<ISecretStore>(new InMemorySecretStore()));
            services.Replace(ServiceDescriptor.Singleton<IFeatureFlags>(flags));

            // Correctness, not latency: a shared machine can take longer than the 300 ms default to embed one query on the
            // CPU, which would (by design) skip the vector leg. The timings are logged; the benchmark measures the budget.
            services.AddSingleton(RetrievalOptions.Default with { TimeBudget = TimeSpan.FromSeconds(10) });
        });
        var gateway = host.Services.GetRequiredService<IModelGateway>();

        // The pipeline's half: embed the chunks as documents with the bundled model, then index them with its id.
        var probe = await gateway.EmbedAsync(new EmbeddingRequest { Inputs = ["probe"], Purpose = EmbeddingPurpose.Document }, CancellationToken.None).WaitAsync(Wait);
        var model = probe.Model!;
        Assert.Equal("bundled", model.ProviderId);
        await TestIndex.BuildAsync(new PillionPaths(_data.Path), _repo.Path, model.QualifiedId, async texts =>
        {
            var result = await gateway.EmbedAsync(new EmbeddingRequest { Inputs = texts, Purpose = EmbeddingPurpose.Document, ModelId = model.QualifiedId }, CancellationToken.None);
            Assert.True(result.IsComplete);
            return [.. result.Vectors.Select(v => v!.Quantized)];
        });

        await using var client = await host.ConnectAndHandshakeAsync();
        var context = client.Attach<IContextService>();

        // The first search after the index opens pays for reader connections and the vector cache load; logged.
        var cold = await context.SearchDetailedAsync(new SearchRequest { Query = "warm up the index", MaxResults = 3 }, CancellationToken.None).WaitAsync(Wait);
        output.WriteLine($"first search: degraded {cold.Degraded}; lexical {cold.LexicalMs:F1} ms, vector {cold.VectorMs:F1} ms, total {cold.TotalMs:F1} ms");
        var embedTimes = new List<double>();
        for (var i = 0; i < 5; i++)
        {
            var embedWatch = Stopwatch.StartNew();
            await gateway.EmbedAsync(new EmbeddingRequest { Inputs = ["how many units are on the shelves " + i], Purpose = EmbeddingPurpose.Query, ModelId = model.QualifiedId }, CancellationToken.None);
            embedTimes.Add(embedWatch.Elapsed.TotalMilliseconds);
        }

        output.WriteLine($"one query embedded by the gateway (bundled model, CPU) in {string.Join(", ", embedTimes.Select(t => t.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)))} ms");

        // Vector leg alone, no shared words with the code: similarity has to do it.
        var vector = await context.SearchDetailedAsync(new SearchRequest { Query = "increase the inventory quantity after a delivery", Mode = SearchMode.Vector, MaxResults = 3 }, CancellationToken.None).WaitAsync(Wait);
        var watch = Stopwatch.StartNew();
        var hybrid = await context.SearchDetailedAsync(new SearchRequest { Query = "how are the totals of an order computed with tax?", MaxResults = 3 }, CancellationToken.None).WaitAsync(Wait);
        watch.Stop();

        output.WriteLine($"vector: {string.Join(" | ", vector.Results.Select(r => $"{r.Breadcrumb} V{r.VectorRank}"))}; vector {vector.VectorMs:F1} ms, total {vector.TotalMs:F1} ms");
        output.WriteLine($"hybrid: {string.Join(" | ", hybrid.Results.Select(r => $"{r.Breadcrumb} L{r.LexicalRank} V{r.VectorRank}"))}; lexical {hybrid.LexicalMs:F1} ms, vector (embedding included) {hybrid.VectorMs:F1} ms, fusion {hybrid.FusionMs:F1} ms, total {hybrid.TotalMs:F1} ms, round trip {watch.Elapsed.TotalMilliseconds:F1} ms");

        Assert.False(vector.Degraded);
        Assert.EndsWith("StockRoom.cs", vector.Results[0].FilePath, StringComparison.Ordinal);
        Assert.Equal(1, vector.Results[0].VectorRank);
        Assert.False(hybrid.Degraded);
        Assert.EndsWith("OrderService.cs", hybrid.Results[0].FilePath, StringComparison.Ordinal);
        var computeTotal = Assert.Single(hybrid.Results.Take(2), r => r.Breadcrumb.EndsWith("> ComputeTotal", StringComparison.Ordinal));
        Assert.True(computeTotal.VectorRank > 0);
        Assert.True(computeTotal.LexicalRank > 0);
    }
}
