using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Pillion.Common.Settings;
using Pillion.ContextEngine.Symbols;
using Pillion.Contracts;
using Pillion.Host.Rpc;
using Pillion.Host.Tests.TestSupport;
using Pillion.Host.WorkspaceQueries;

namespace Pillion.Host.Tests.WorkspaceQueries;

/// <summary>
/// The host's workspace queries (work item 2.14): Visual Studio answers C# and Visual Basic; a refusal, a timeout, an older
/// extension, the flag, a missing connection or another language send the query to the index's approximate edges.
/// </summary>
public sealed class WorkspaceQueriesTests : IDisposable
{
    private const string OrderService = """
        namespace Shop.Orders
        {
            public class OrderService
            {
                public void Place(int quantity)
                {
                }
            }
        }
        """;

    private const string Checkout = """
        namespace Shop
        {
            public class Checkout
            {
                public void Run()
                {
                    var orders = new Shop.Orders.OrderService();
                    orders.Place(1);
                }

                public void Retry() => new Shop.Orders.OrderService().Place(2);
            }
        }
        """;

    private const string App = """
        export function placeOrder(quantity: number): void {
        }

        export function submit(): void {
            placeOrder(3);
        }
        """;

    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private readonly TempDirectory _root = new();
    private readonly FakeVsix _vsix = new();
    private readonly FakeFlags _flags = new();
    private readonly IndexedSymbolGraph _graph;

    public WorkspaceQueriesTests()
    {
        _graph = BuildGraph(_root.Path, ("src/Orders/OrderService.cs", OrderService), ("src/Checkout.cs", Checkout), ("web/app.ts", App));
    }

    private string OrderServicePath => Path.Combine(_root.Path, "src", "Orders", "OrderService.cs");

    private string CheckoutPath => Path.Combine(_root.Path, "src", "Checkout.cs");

    public void Dispose() => _root.Dispose();

    [Fact]
    public async Task CallersOf_CSharp_IsAnsweredByRoslyn_NotApproximate()
    {
        _vsix.Service.Callers = new CallersOfResponse
        {
            IsFound = true,
            Callers = [new CallSite { FilePath = CheckoutPath, Line = 7, Snippet = "orders.Place(1);", CallerName = "Shop.Checkout.Run" }],
        };
        var queries = Create();

        var result = await queries.CallersOfAsync(Query("Place", "Shop.Orders.OrderService"), CancellationToken.None);

        Assert.Equal(WorkspaceQuerySource.Roslyn, result.Source);
        Assert.True(result.IsFound);
        Assert.False(result.IsApproximate);
        Assert.False(result.Truncated);
        Assert.Null(result.FallbackReason);
        var site = Assert.Single(result.Sites);
        Assert.Equal((CheckoutPath, 7, "orders.Place(1);", "Shop.Checkout.Run", false), (site.FilePath, site.Line, site.Snippet, site.CallerName, site.IsApproximate));
        var request = Assert.Single(_vsix.Service.CallersRequests);
        Assert.Equal(("Place", "Shop.Orders.OrderService", HostWorkspaceQueries.DefaultMaxResults), (request.SymbolName, request.Container, request.MaxResults));
    }

    [Fact]
    public async Task CallersOf_WhenVisualStudioRefuses_FallsBackToTheIndex_Approximate()
    {
        _vsix.Service.Throw = new PillionRpcException(new PillionError { Code = PillionErrorCode.Cancelled, Message = "Another workspace query is in flight." });
        var queries = Create();

        var result = await queries.CallersOfAsync(Query("Place", "Shop.Orders > OrderService"), CancellationToken.None);

        Assert.Equal(WorkspaceQuerySource.Index, result.Source);
        Assert.True(result.IsFound);
        Assert.True(result.IsApproximate);
        Assert.Contains("Cancelled", result.FallbackReason, StringComparison.Ordinal);
        Assert.Equal([7, 10], result.Sites.Select(s => s.Line));
        Assert.All(result.Sites, s => Assert.True(s.IsApproximate));
        Assert.All(result.Sites, s => Assert.Equal(CheckoutPath, s.FilePath));
        Assert.Equal(["orders.Place(1);", "public void Retry() => new Shop.Orders.OrderService().Place(2);"], result.Sites.Select(s => s.Snippet));
        Assert.Equal(["Shop.Checkout.Run", "Shop.Checkout.Retry"], result.Sites.Select(s => s.CallerName));
    }

    [Fact]
    public async Task CallersOf_WhenVisualStudioDoesNotAnswerInTime_FallsBackWithinTheTimeout()
    {
        _vsix.Service.Hang = true;
        var queries = Create(callTimeout: TimeSpan.FromMilliseconds(300));

        var result = await queries.CallersOfAsync(Query("Place"), CancellationToken.None).WaitAsync(Wait);

        Assert.Equal(WorkspaceQuerySource.Index, result.Source);
        Assert.Contains("in time", result.FallbackReason, StringComparison.Ordinal);
        Assert.True(result.Elapsed < TimeSpan.FromSeconds(5));
        Assert.Equal(2, result.Sites.Count);
    }

    [Fact]
    public async Task CallersOf_WhenRoslynDoesNotFindTheSymbol_TheIndexAnswers()
    {
        _vsix.Service.Callers = new CallersOfResponse { IsFound = false };
        var queries = Create();

        var result = await queries.CallersOfAsync(Query("Place"), CancellationToken.None);

        Assert.Equal(WorkspaceQuerySource.Index, result.Source);
        Assert.Equal("Roslyn did not find the symbol", result.FallbackReason);
    }

    [Fact]
    public async Task CallersOf_AnotherLanguage_GoesStraightToTheIndex()
    {
        var queries = Create();

        var result = await queries.CallersOfAsync(Query("placeOrder") with { FilePath = Path.Combine(_root.Path, "web", "app.ts"), Line = 0 }, CancellationToken.None);

        Assert.Empty(_vsix.Service.CallersRequests);
        Assert.Equal(WorkspaceQuerySource.Index, result.Source);
        Assert.Equal("the language is not C# or Visual Basic", result.FallbackReason);
        var site = Assert.Single(result.Sites);
        Assert.Equal((4, "placeOrder(3);", true), (site.Line, site.Snippet, site.IsApproximate));
    }

    [Fact]
    public async Task FlagOff_DoesNotAskVisualStudio()
    {
        _flags.Off.Add(FeatureNames.WorkspaceQueries);
        var queries = Create();

        var callers = await queries.CallersOfAsync(Query("Place"), CancellationToken.None);
        var references = await queries.FindReferencesAsync(Query("Place") with { FilePath = OrderServicePath, Line = 4 }, CancellationToken.None);

        Assert.Empty(_vsix.Service.CallersRequests);
        Assert.Empty(_vsix.Service.ReferenceRequests);
        Assert.Equal("features.workspaceQueries is off", callers.FallbackReason);
        Assert.Equal(WorkspaceQuerySource.Index, references.Source);
        Assert.True(references.IsApproximate);
    }

    [Fact]
    public async Task NotConnected_TheIndexAnswers()
    {
        _vsix.Connected = false;
        var queries = Create();

        var result = await queries.CallersOfAsync(Query("Place"), CancellationToken.None);

        Assert.Equal("Visual Studio is not connected", result.FallbackReason);
        Assert.Equal(WorkspaceQuerySource.Index, result.Source);
    }

    [Fact]
    public async Task CallersOf_TruncatedByTheBudget_IsCompletedFromTheIndex_AndStaysTruncated()
    {
        _vsix.Service.Callers = new CallersOfResponse
        {
            IsFound = true,
            Truncated = true,
            Callers = [new CallSite { FilePath = CheckoutPath, Line = 7, Snippet = "orders.Place(1);", CallerName = "Shop.Checkout.Run" }],
        };
        var queries = Create();

        var result = await queries.CallersOfAsync(Query("Place"), CancellationToken.None);

        Assert.Equal(WorkspaceQuerySource.Roslyn, result.Source);
        Assert.True(result.Truncated);
        Assert.True(result.IsApproximate);
        Assert.Equal([(7, false), (10, true)], result.Sites.Select(s => (s.Line, s.IsApproximate)));
    }

    [Fact]
    public async Task OverRealRpc_AnOlderExtensionWithoutCallersOf_FallsBack()
    {
        var graph = _graph;
        await using var host = await InProcessHost.StartAsync(s => s.AddSingleton<ISymbolGraphProvider>(new FixedGraphs(graph)));
        await using var client = await host.ConnectAndHandshakeAsync(rpc => rpc.AddLocalRpcTarget(new ContractOnePointTwoTarget(), null));
        var queries = host.Services.GetRequiredService<IWorkspaceQueries>();

        var result = await queries.CallersOfAsync(Query("Place"), CancellationToken.None);

        Assert.Equal(WorkspaceQuerySource.Index, result.Source);
        Assert.Contains("older extension", result.FallbackReason, StringComparison.Ordinal);
        Assert.Equal(2, result.Sites.Count);
    }

    [Fact]
    public async Task UnknownSymbol_WithNoAnswerAnywhere_IsNotFound()
    {
        _vsix.Connected = false;
        var queries = Create(graphs: new EmptySymbolGraphProvider());

        var result = await queries.CallersOfAsync(Query("Place"), CancellationToken.None);

        Assert.False(result.IsFound);
        Assert.Equal(WorkspaceQuerySource.None, result.Source);
        Assert.Empty(result.Sites);
        Assert.Contains("no index is available", result.FallbackReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResultCap_CutsTheIndexAnswer_AndMarksItTruncated()
    {
        _vsix.Connected = false;
        var queries = Create();

        var result = await queries.CallersOfAsync(Query("Place") with { MaxResults = 1 }, CancellationToken.None);

        Assert.True(result.Truncated);
        Assert.Single(result.Sites);
    }

    [Fact]
    public async Task FindReferences_WithFileAndLine_AsksAtTheNamesColumn_AndReadsSnippets()
    {
        _vsix.Service.References = new FindReferencesResponse { References = [new SourceLocation { FilePath = CheckoutPath, Line = 7, Column = 23 }] };
        var queries = Create();

        var result = await queries.FindReferencesAsync(Query("Place") with { FilePath = OrderServicePath, Line = 4 }, CancellationToken.None);

        var asked = Assert.Single(_vsix.Service.ReferenceRequests).Location;
        Assert.Equal((OrderServicePath, 4, "        public void Place(int quantity)".IndexOf("Place", StringComparison.Ordinal)), (asked.FilePath, asked.Line, asked.Column));
        Assert.Equal(WorkspaceQuerySource.Roslyn, result.Source);
        Assert.False(result.Truncated);
        var site = Assert.Single(result.Sites);
        Assert.Equal((CheckoutPath, 7, "orders.Place(1);", false), (site.FilePath, site.Line, site.Snippet, site.IsApproximate));
    }

    [Fact]
    public async Task FindReferences_WithoutAFile_AsksAtTheIndexsDeclaration()
    {
        _vsix.Service.References = new FindReferencesResponse { References = [new SourceLocation { FilePath = CheckoutPath, Line = 7, Column = 23 }] };
        var queries = Create();

        var result = await queries.FindReferencesAsync(Query("Place", "OrderService"), CancellationToken.None);

        var asked = Assert.Single(_vsix.Service.ReferenceRequests).Location;
        Assert.Equal((OrderServicePath, 4), (asked.FilePath, asked.Line));
        Assert.Equal(WorkspaceQuerySource.Roslyn, result.Source);
    }

    [Fact]
    public async Task FindReferences_ThatTookTheWholeBudget_IsTreatedAsPartial_AndCompletedFromTheIndex()
    {
        _vsix.Service.References = new FindReferencesResponse { References = [new SourceLocation { FilePath = CheckoutPath, Line = 7, Column = 23 }] };
        _vsix.Service.Delay = TimeSpan.FromMilliseconds(250);
        var queries = Create(budget: TimeSpan.FromMilliseconds(200));

        var result = await queries.FindReferencesAsync(Query("Place") with { FilePath = OrderServicePath, Line = 4 }, CancellationToken.None);

        Assert.True(result.Truncated);
        Assert.Equal(WorkspaceQuerySource.Roslyn, result.Source);
        Assert.Contains(result.Sites, s => s.Line == 7 && !s.IsApproximate);
        Assert.Contains(result.Sites, s => s.Line == 10 && s.IsApproximate);
    }

    [Fact]
    public async Task FindReferences_RoslynFindsNone_TheIndexAnswers()
    {
        _vsix.Service.References = new FindReferencesResponse();
        var queries = Create();

        var result = await queries.FindReferencesAsync(Query("Place") with { FilePath = OrderServicePath, Line = 4 }, CancellationToken.None);

        Assert.Equal(WorkspaceQuerySource.Index, result.Source);
        Assert.Equal("Roslyn found no references", result.FallbackReason);
        Assert.Equal([7, 10], result.Sites.Select(s => s.Line));
    }

    [Fact]
    public async Task CallerCancellation_Propagates()
    {
        _vsix.Service.Hang = true;
        var queries = Create();
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queries.CallersOfAsync(Query("Place"), cancel.Token));
    }

    [Fact]
    public async Task OverRealRpc_ARefusalFromTheExtension_FallsBackToTheIndex()
    {
        var target = new FakeWorkspaceQueryService
        {
            Throw = new PillionRpcException(new PillionError { Code = PillionErrorCode.Cancelled, Message = "Too many workspace queries: at most five a second." }),
        };
        var graph = _graph;
        await using var host = await InProcessHost.StartAsync(s => s.AddSingleton<ISymbolGraphProvider>(new FixedGraphs(graph)));
        await using var client = await host.ConnectAndHandshakeAsync(rpc => rpc.AddLocalRpcTarget<IWorkspaceQueryService>(target, null));
        var queries = host.Services.GetRequiredService<IWorkspaceQueries>();

        var refused = await queries.CallersOfAsync(Query("Place"), CancellationToken.None);
        target.Throw = null;
        target.Callers = new CallersOfResponse { IsFound = true, Callers = [new CallSite { FilePath = CheckoutPath, Line = 7, Snippet = "orders.Place(1);" }] };
        var answered = await queries.CallersOfAsync(Query("Place"), CancellationToken.None);

        Assert.Equal(WorkspaceQuerySource.Index, refused.Source);
        Assert.Contains("five a second", refused.FallbackReason, StringComparison.Ordinal);
        Assert.Equal(2, refused.Sites.Count);
        Assert.Equal(WorkspaceQuerySource.Roslyn, answered.Source);
        Assert.Equal(7, Assert.Single(answered.Sites).Line);
    }

    [Theory]
    [InlineData("a.cs", true)]
    [InlineData("b.VB", true)]
    [InlineData("c.ts", false)]
    [InlineData("d.razor", false)]
    [InlineData("", false)]
    public void IsRoslynLanguage_CSharpAndVisualBasicOnly(string path, bool expected) =>
        Assert.Equal(expected, HostWorkspaceQueries.IsRoslynLanguage(path));

    [Theory]
    [InlineData("Shop.Orders > OrderService<T>", "Shop.Orders.OrderService")]
    [InlineData("global::Shop.Orders.OrderService", "Shop.Orders.OrderService")]
    [InlineData("linalg::Matrix", "linalg.Matrix")]
    [InlineData("Outer(Of T).Inner", "Outer.Inner")]
    [InlineData("  ", "")]
    public void NormalizeContainer_MakesADottedName(string container, string expected) =>
        Assert.Equal(expected, SymbolGraphFallback.NormalizeContainer(container));

    private static WorkspaceSymbolQuery Query(string name, string container = "") => new() { Name = name, Container = container };

    private static IndexedSymbolGraph BuildGraph(string root, params (string Path, string Text)[] files)
    {
        var builder = new SymbolGraphBuilder();
        var extractor = new FileSymbolExtractor();
        foreach (var (path, text) in files)
        {
            var full = Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, text);
            builder.AddFile(extractor.Extract(path, text));
        }

        return new IndexedSymbolGraph(root, builder.Build());
    }

    private HostWorkspaceQueries Create(TimeSpan? callTimeout = null, TimeSpan? budget = null, ISymbolGraphProvider? graphs = null) =>
        new(_vsix, _flags, graphs ?? new FixedGraphs(_graph), NullLogger<HostWorkspaceQueries>.Instance, callTimeout ?? TimeSpan.FromSeconds(3), budget ?? TimeSpan.FromSeconds(2));

    /// <summary>A contract 1.2 extension: it answers find-references but has no callers-of method.</summary>
    private sealed class ContractOnePointTwoTarget
    {
#pragma warning disable CA1822 // An RPC target method: StreamJsonRpc binds instance methods.
        public Task<FindReferencesResponse> FindReferencesAsync(FindReferencesRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new FindReferencesResponse());
#pragma warning restore CA1822
    }

    private sealed class FixedGraphs(params IndexedSymbolGraph[] graphs) : ISymbolGraphProvider
    {
        public ValueTask<IReadOnlyList<IndexedSymbolGraph>> GetGraphsAsync(CancellationToken cancellationToken) => ValueTask.FromResult<IReadOnlyList<IndexedSymbolGraph>>(graphs);
    }

    private sealed class FakeFlags : IFeatureFlags
    {
        public event EventHandler? Changed
        {
            add { }
            remove { }
        }

        public HashSet<string> Off { get; } = new(StringComparer.OrdinalIgnoreCase);

        public bool IsEnabled(string feature) => !Off.Contains(feature);
    }

    private sealed class FakeVsix : IVsixConnection
    {
        public event EventHandler? ConnectionChanged
        {
            add { }
            remove { }
        }

        public bool Connected { get; set; } = true;

        public FakeWorkspaceQueryService Service { get; } = new();

        public bool IsConnected => Connected;

        public IEditorService GetEditorService() => throw RpcErrors.NotConnected();

        public IApprovalService GetApprovalService() => throw RpcErrors.NotConnected();

        public IWorkspaceQueryService GetWorkspaceQueryService() => Connected ? Service : throw RpcErrors.NotConnected();
    }

    /// <summary>The extension's side, canned.</summary>
    internal sealed class FakeWorkspaceQueryService : IWorkspaceQueryService
    {
        public CallersOfResponse Callers { get; set; } = new();

        public FindReferencesResponse References { get; set; } = new();

        public Exception? Throw { get; set; }

        public bool Hang { get; set; }

        public TimeSpan Delay { get; set; }

        public List<CallersOfRequest> CallersRequests { get; } = [];

        public List<FindReferencesRequest> ReferenceRequests { get; } = [];

        public async Task<CallersOfResponse> CallersOfAsync(CallersOfRequest request, CancellationToken cancellationToken)
        {
            CallersRequests.Add(request);
            await RespondAsync(cancellationToken);
            return Callers;
        }

        public async Task<FindReferencesResponse> FindReferencesAsync(FindReferencesRequest request, CancellationToken cancellationToken)
        {
            ReferenceRequests.Add(request);
            await RespondAsync(cancellationToken);
            return References;
        }

        public Task<DiagnosticsResponse> GetDiagnosticsAsync(DiagnosticsRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<SymbolResponse> GetSymbolAsync(SymbolRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<RenameSymbolResponse> RenameSymbolAsync(RenameSymbolRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<RunTestsResponse> RunTestsAsync(RunTestsRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();

        private async Task RespondAsync(CancellationToken cancellationToken)
        {
            if (Hang)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            if (Delay > TimeSpan.Zero)
            {
                await Task.Delay(Delay, cancellationToken);
            }

            if (Throw is { } exception)
            {
                throw exception;
            }
        }
    }
}
