using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Pillion.Common;
using Pillion.Common.Secrets;
using Pillion.Common.Settings;
using Pillion.ContextEngine.Retrieval;
using Pillion.Contracts;
using Pillion.Host.Context.Retrieval;
using Pillion.Host.Tests.TestSupport;
using Pillion.ModelGateway;
using AiChatMessage = Microsoft.Extensions.AI.ChatMessage;
using AiChatRole = Microsoft.Extensions.AI.ChatRole;

namespace Pillion.Host.Tests;

/// <summary>
/// Context assembly v2 in Pillion Host (work item 2.12): the chat sources' queries and focus files, the autocomplete snippet
/// cache per member, the inline edit context, and a chat over the real pipe on a real encrypted index whose scripted model
/// cites a chunk.
/// </summary>
public sealed class ContextAssemblyV2Tests : IAsyncDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

    private readonly TempDirectory _data = new();
    private readonly TempDirectory _repo = new();
    private readonly RetrievalTests.Flags _flags = new();
    private readonly ScriptedProvider _provider = new();

    public ValueTask DisposeAsync()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        _data.Dispose();
        _repo.Dispose();
        return ValueTask.CompletedTask;
    }

    [Fact]
    public void TheChatQuery_IsTheLatestMessagePlusThePinnedSelectionsIdentifiers_AndTheFocusIsTheCurrentAndPinnedFiles()
    {
        var request = new ChatRequest
        {
            Messages = [new Contracts.ChatMessage { Role = Contracts.ChatRole.User, Content = "old" }, new Contracts.ChatMessage { Role = Contracts.ChatRole.Assistant, Content = "a" }, new Contracts.ChatMessage { Role = Contracts.ChatRole.User, Content = " Why does it fail? " }],
            CurrentFile = @"C:\repo\src\Program.cs",
            Pins =
            [
                new MentionResolution { IsResolved = true, Kind = MentionKinds.Selection, FilePath = @"C:\repo\src\Orders.cs", Content = "var total = orderService.ComputeTotal(order); return total;" },
                new MentionResolution { IsResolved = true, Kind = MentionKinds.File, FilePath = @"C:\repo\src\Program.cs", Content = "x" },
            ],
        };

        Assert.Equal("Why does it fail? total orderService ComputeTotal order", RetrievalContextSource.Query(request));
        Assert.Equal([@"C:\repo\src\Program.cs", @"C:\repo\src\Orders.cs"], RetrievalContextSource.FocusFiles(request));
        Assert.Equal(RetrievalContextSource.DefaultMaxChunks, RetrievalContextSource.MaxChunks(request));
        request.MaxRetrievedChunks = 50;
        Assert.Equal(RetrievalContextSource.MaxChunksLimit, RetrievalContextSource.MaxChunks(request));
    }

    [Fact]
    public async Task TheChatSource_AsksForAssemblyOnlyWithRetrievalOnAndAnIndex()
    {
        var retrieval = new FakeRetrieval();
        var source = new RetrievalContextSource(new CodeContextRetriever(retrieval, new EmptyCatalog(), _flags));
        var request = new ChatRequest { Messages = [new Contracts.ChatMessage { Role = Contracts.ChatRole.User, Content = "Where is Restock?" }] };

        Assert.False(await source.RequestsAssemblyAsync(request, CancellationToken.None)); // no index open
        Assert.Null(await source.GetAsync(request.With(r => r.RetrievalMode = RetrievalMode.Off), Target(), CancellationToken.None));
        _flags.Disabled.Add(FeatureNames.Retrieval);
        Assert.Null(await source.GetAsync(request, Target(), CancellationToken.None));
        Assert.Equal(0, retrieval.Calls);
    }

    [Fact]
    public async Task TheChatSource_GivesTheChunksBestFirst_WithTheInstructionToCite_WithoutPinnedFiles_AndRetrievesOncePerRequest()
    {
        var retrieval = new FakeRetrieval { Hits = [Hit(1, "src/A.cs"), Hit(2, "src/Pinned.cs"), Hit(3, "src/B.cs")] };
        var source = new RetrievalContextSource(new CodeContextRetriever(retrieval, new EmptyCatalog(), _flags));
        var request = new ChatRequest
        {
            RequestId = "r1",
            Messages = [new Contracts.ChatMessage { Role = Contracts.ChatRole.User, Content = "How?" }],
            Pins = [new MentionResolution { IsResolved = true, Kind = MentionKinds.File, FilePath = @"C:\repo\src\Pinned.cs", Content = "x" }],
            MaxRetrievedChunks = 5,
        };

        var first = await source.GetAsync(request, Target(), CancellationToken.None);
        var again = await source.GetAsync(request, Target(), CancellationToken.None); // a fallback model

        Assert.NotNull(first);
        Assert.Contains("cite", first.Text, StringComparison.Ordinal);
        Assert.Equal(["src/A.cs", "src/B.cs"], first.Chunks.Select(c => c.Path));
        Assert.Equal(@"C:\repo\src\A.cs", first.Chunks[0].FilePath);
        Assert.Equal("0123456789abcdef0123456789abcdef:1", first.Chunks[0].Key);
        Assert.Equal(first.Chunks.Select(c => c.Key), again!.Chunks.Select(c => c.Key));
        Assert.Equal(1, retrieval.Calls);
        var query = Assert.Single(retrieval.Queries);
        Assert.Equal((5, true, RetrievalSearchMode.Hybrid), (query.MaxResults, query.IncludeText, query.Mode));
        Assert.Equal([@"C:\repo\src\Pinned.cs"], query.FocusFiles);
    }

    [Fact]
    public async Task AutocompleteSnippets_AreSearchedOncePerMember_AndReusedForEveryKeystrokeInIt()
    {
        var time = new ManualTimeProvider();
        var retrieval = new FakeRetrieval { Hits = [Hit(1, "src/Calc.cs"), Hit(2, "src/A.cs"), Hit(3, "src/B.cs"), Hit(4, "src/C.cs"), Hit(5, "src/D.cs")] };
        var provider = new AutocompleteSnippetProvider(new CodeContextRetriever(retrieval, new EmptyCatalog(), _flags), time);

        var first = await provider.GetSnippetsAsync(Completion("return order.", "decimal ComputeTotal(Order order)"), CancellationToken.None);
        var second = await provider.GetSnippetsAsync(Completion("return order.Li", "decimal  ComputeTotal(Order order)"), CancellationToken.None);
        time.Advance(TimeSpan.FromSeconds(10));
        var third = await provider.GetSnippetsAsync(Completion("return order.Lines.", "decimal ComputeTotal(Order order)"), CancellationToken.None);

        // Three snippets from other files (the current file's own chunk is left out), the same list every time.
        Assert.Equal(["src/A.cs", "src/B.cs", "src/C.cs"], first.Select(s => s.Path));
        Assert.Same(first, second);
        Assert.Same(first, third);
        Assert.Equal(1, retrieval.Calls);
        Assert.Equal((1, 2), (provider.Searches, provider.Reuses));
        var query = Assert.Single(retrieval.Queries);
        Assert.Equal(AutocompleteSnippetProvider.TimeBudget, query.TimeBudget);
        Assert.StartsWith("decimal ComputeTotal(Order order) ComputeTotal Order order", query.Text, StringComparison.Ordinal);

        // Leaving the member searches again, and so does the same member after 30 s.
        await provider.GetSnippetsAsync(Completion("_levels[", "void Restock(string product)"), CancellationToken.None);
        Assert.Equal(2, retrieval.Calls);
        await provider.GetSnippetsAsync(Completion("_levels[p", "void Restock(string product)"), CancellationToken.None);
        Assert.Equal(2, retrieval.Calls);
        time.Advance(AutocompleteSnippetProvider.ReuseTime);
        await provider.GetSnippetsAsync(Completion("_levels[pr", "void Restock(string product)"), CancellationToken.None);
        Assert.Equal(3, retrieval.Calls);
    }

    [Fact]
    public async Task ADegradedSnippetSearch_GivesNoSnippets_AndRetrievalOffGivesNone()
    {
        var retrieval = new FakeRetrieval { Hits = [Hit(2, "src/A.cs")], Degraded = true };
        var provider = new AutocompleteSnippetProvider(new CodeContextRetriever(retrieval, new EmptyCatalog(), _flags));

        Assert.Empty(await provider.GetSnippetsAsync(Completion("x", "void M()"), CancellationToken.None));

        _flags.Disabled.Add(FeatureNames.Retrieval);
        Assert.Empty(await provider.GetSnippetsAsync(Completion("x", "void N()"), CancellationToken.None));
        Assert.Equal(1, retrieval.Calls);
    }

    [Fact]
    public async Task TheInlineEditContext_LeavesOutWhatThePromptAlreadyShows_AndKeepsFour()
    {
        var retrieval = new FakeRetrieval
        {
            Hits = [Hit(1, "src/Calc.cs", start: 12, end: 14), Hit(2, "src/Calc.cs", start: 200, end: 210), Hit(3, "src/A.cs"), Hit(4, "src/B.cs"), Hit(5, "src/C.cs"), Hit(6, "src/D.cs")],
        };
        var context = new InlineEditRetrieval(new CodeContextRetriever(retrieval, new EmptyCatalog(), _flags));
        var request = new InlineEditRequest
        {
            FilePath = @"C:\repo\src\Calc.cs",
            ScopeText = "int Add(int a, int b) => a + b;\n",
            ScopeStartLine = 10,
            TextBefore = "class Calc\n{\n",
            TextAfter = "}\n",
            Instruction = "use checked arithmetic",
        };

        var related = await context.GetRelatedAsync(request, CancellationToken.None);

        Assert.Equal(["src/Calc.cs", "src/A.cs", "src/B.cs", "src/C.cs"], related.Select(c => c.Path));
        Assert.Equal(200, related[0].StartLine);
        Assert.Equal("use checked arithmetic Add", Assert.Single(retrieval.Queries).Text);
    }

    [Fact]
    public async Task OverThePipe_AChatOnARealIndex_PutsTheChunksInThePrompt_AndTheModelsCitationComesBackOnTheFinalChunk()
    {
        var indexId = await TestIndex.BuildAsync(new PillionPaths(_data.Path), _repo.Path);
        var restock = TestIndex.LineOf("src/Inventory/StockRoom.cs", "public void Restock");
        _provider.Reply = $"Stock is added in `src/Inventory/StockRoom.cs:{restock}` when a delivery arrives.";
        await using var host = await StartAsync();
        await using var client = await host.ConnectAndHandshakeAsync();

        var chunks = new List<ChatChunk>();
        await foreach (var chunk in client.Attach<IModelService>().ChatAsync(Chat("c1", "Where do we Restock a product in the StockRoom?"), CancellationToken.None))
        {
            chunks.Add(chunk);
        }

        var prompt = Assert.Single(_provider.Received);
        var system = prompt[0].Text;
        Assert.Contains("## Retrieved code", system, StringComparison.Ordinal);
        Assert.Contains("### src/Inventory/StockRoom.cs:", system, StringComparison.Ordinal);
        Assert.Contains("public void Restock", system, StringComparison.Ordinal);
        var citation = Assert.Single(chunks.Single(c => c.IsFinal).Citations);
        Assert.Equal(Path.Combine(_repo.Path, "src", "Inventory", "StockRoom.cs"), citation.FilePath);
        Assert.Equal((restock - 1, restock - 1), (citation.StartLine, citation.EndLine));
        Assert.StartsWith(indexId + ":", citation.ChunkId, StringComparison.Ordinal);
        Assert.EndsWith("> Restock", citation.Breadcrumb, StringComparison.Ordinal);
        Assert.True(citation.ReplyOffset > 0);

        // The Context Inspector shows the chunks with their keys; removing one leaves it out.
        var diagnostics = client.Attach<IDiagnosticsService>();
        var report = await diagnostics.PreviewContextAsync(Chat("p1", "Where do we Restock a product in the StockRoom?"), CancellationToken.None).WaitAsync(Wait);
        Assert.True(report.IsAssembled);
        var retrieved = report.Items.Where(i => i.Kind == "retrieved").ToList();
        Assert.NotEmpty(retrieved);
        Assert.All(retrieved, i => Assert.StartsWith(indexId + ":", i.ContextKey, StringComparison.Ordinal));
        Assert.All(retrieved, i => Assert.True(i.Tokens > 0));
        Assert.Contains(retrieved, i => i.Name.StartsWith("src/Inventory/StockRoom.cs:", StringComparison.Ordinal));
        var removed = retrieved[0].ContextKey;
        var after = await diagnostics.PreviewContextAsync(Chat("p2", "Where do we Restock a product in the StockRoom?").With(r => r.ExcludedContextKeys = [removed]), CancellationToken.None).WaitAsync(Wait);
        Assert.DoesNotContain(after.Items, i => i.ContextKey == removed);
        Assert.Equal(retrieved.Count - 1, after.Items.Count(i => i.Kind == "retrieved"));
    }

    [Fact]
    public async Task OverThePipe_AReplyThatCitesNothing_GetsTheImplicitSet_AndRetrievalOffSendsAPlainRequest()
    {
        await TestIndex.BuildAsync(new PillionPaths(_data.Path), _repo.Path);
        _provider.Reply = "Somewhere in the inventory code.";
        await using var host = await StartAsync();
        await using var client = await host.ConnectAndHandshakeAsync();
        var models = client.Attach<IModelService>();

        var implicitChunks = await CollectAsync(models.ChatAsync(Chat("c1", "Where do we Restock a product?"), CancellationToken.None));
        var offChunks = await CollectAsync(models.ChatAsync(Chat("c2", "Where do we Restock a product?").With(r => r.RetrievalMode = RetrievalMode.Off), CancellationToken.None));

        var implicitSet = implicitChunks.Single(c => c.IsFinal).Citations;
        Assert.NotEmpty(implicitSet);
        Assert.All(implicitSet, c => Assert.Equal(-1, c.ReplyOffset));
        Assert.Empty(offChunks.Single(c => c.IsFinal).Citations);
        Assert.DoesNotContain("Retrieved code", _provider.Received.Last()[0].Text, StringComparison.Ordinal);
        var records = host.Services.GetRequiredService<IOutboundLog>().GetRecent();
        Assert.Equal(0, records.Single(r => r.RequestId == "c2").ContextItems); // a plain request, sent as it is
    }

    private static async Task<List<ChatChunk>> CollectAsync(IAsyncEnumerable<ChatChunk> stream)
    {
        var list = new List<ChatChunk>();
        await foreach (var chunk in stream)
        {
            list.Add(chunk);
        }

        return list;
    }

    private static ChatRequest Chat(string id, string text) => new()
    {
        RequestId = id,
        Profile = Profile.Auto,
        Messages = [new Contracts.ChatMessage { Role = Contracts.ChatRole.User, Content = text }],
    };

    private static AssemblyTarget Target() => new("m", 32768, true, "s", []);

    private static CompletionRequest Completion(string prefix, string declaration) => new()
    {
        RequestId = "a",
        Prefix = "class Calc\n{\n    " + prefix,
        Suffix = "\n}\n",
        FilePath = @"C:\repo\src\Calc.cs",
        LanguageId = "csharp",
        EnclosingDeclaration = declaration,
        TaskClass = TaskClass.Autocomplete,
        OpenDocuments = [@"C:\repo\src\A.cs"],
    };

    private static RetrievalHit Hit(int id, string path, int start = 1, int end = 9) => new()
    {
        IndexId = "0123456789abcdef0123456789abcdef",
        ChunkId = id,
        FilePath = @"C:\repo\" + path.Replace('/', '\\'),
        RelativePath = path,
        StartLine = start,
        EndLine = end,
        Breadcrumb = path + " > T > M" + id,
        Kind = "member",
        Snippet = "snippet " + id,
        Text = "int M" + id + "() => " + id + ";",
        Score = 1.0 / id,
    };

    private Task<InProcessHost> StartAsync() => InProcessHost.StartAsync(services =>
    {
        var settings = new PillionSettings();
        settings.Providers.Add(new ProviderSettings { Id = "fake", Kind = ProviderKind.Mistral, Models = ["fake-chat:1b"] });
        services.Replace(ServiceDescriptor.Singleton(new PillionPaths(_data.Path)));
        services.Replace(ServiceDescriptor.Singleton<ISecretStore>(new InMemorySecretStore()));
        services.Replace(ServiceDescriptor.Singleton<IFeatureFlags>(_flags));
        services.Replace(ServiceDescriptor.Singleton<IGatewaySettingsSource>(new StaticGatewaySettingsSource(settings)));
        services.AddSingleton<IModelProviderFactory>(new ScriptedFactory(_provider));
        services.AddSingleton(RetrievalOptions.Default with { TimeBudget = TimeSpan.FromSeconds(10) }); // correctness, not latency
        var inventory = new ModelInventory();
        inventory.Update("fake", _provider.ListModelsAsync(CancellationToken.None).Result);
        services.Replace(ServiceDescriptor.Singleton(inventory));
    });

    /// <summary>Answers every search with canned hits and records the queries.</summary>
    private sealed class FakeRetrieval : IRetrievalService
    {
        public IReadOnlyList<RetrievalHit> Hits { get; init; } = [];

        public bool Degraded { get; init; }

        public int Calls;

        public ConcurrentQueue<RetrievalQuery> Queries { get; } = new();

        public Task<SearchResponse> SearchDetailedAsync(SearchRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<SearchResult[]> SearchAsync(SearchRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<RetrievalResponse> RetrieveAsync(RetrievalQuery query, IReadOnlyList<string> roots, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            Queries.Enqueue(query);
            return Task.FromResult(new RetrievalResponse { Hits = [.. Hits.Take(query.MaxResults)], Degraded = Degraded, TotalMs = 1 });
        }
    }

    private sealed class EmptyCatalog : IIndexCatalog
    {
        public Task<IReadOnlyList<CatalogIndex>> GetIndexesAsync(IReadOnlyList<string> roots, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<CatalogIndex>>([]);
    }

    private sealed class ScriptedFactory(ScriptedProvider provider) : IModelProviderFactory
    {
        public ProviderKind Kind => ProviderKind.Mistral;

        public bool RequiresApiKey(ProviderSettings settings) => false;

        public IModelProvider Create(ProviderSettings settings, ProviderCreationContext context) => provider;
    }

    /// <summary>A local chat model that records each prompt and answers with <see cref="Reply"/> in a few pieces.</summary>
    private sealed class ScriptedProvider : IModelProvider
    {
        public ConcurrentQueue<List<AiChatMessage>> Received { get; } = new();

        public string Reply { get; set; } = "ok";

        public string Id => "fake";

        public ProviderKind Kind => ProviderKind.Mistral;

        public string DisplayName => "Scripted";

        public bool IsLocal => true;

        public Task<IReadOnlyList<ProviderModel>> ListModelsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ProviderModel>>(
                [new ProviderModel("fake", "fake-chat:1b", "Scripted chat", new ModelCapabilityDescriptor { ContextWindow = 16384, IsLocal = true }, "sha256:abc")]);

        public IChatClient GetChatClient(string modelId) => new Client(this);

        public IFimClient? GetFimClient(string modelId) => null;

        public Task<ProviderProbeResult> ProbeAsync(CancellationToken cancellationToken) => Task.FromResult(new ProviderProbeResult(true, "1", "fine"));

        public void Dispose()
        {
        }

        private sealed class Client(ScriptedProvider owner) : IChatClient
        {
            public Task<ChatResponse> GetResponseAsync(IEnumerable<AiChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
                throw new NotSupportedException();

            public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
                IEnumerable<AiChatMessage> messages,
                ChatOptions? options = null,
                [EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                owner.Received.Enqueue([.. messages]);
                var reply = owner.Reply;
                for (var i = 0; i < reply.Length; i += 7)
                {
                    await Task.Yield();
                    yield return new ChatResponseUpdate(AiChatRole.Assistant, reply.Substring(i, Math.Min(7, reply.Length - i)));
                }
            }

            public object? GetService(Type serviceType, object? serviceKey = null) => null;

            public void Dispose()
            {
            }
        }
    }
}

internal static class ChatRequestHostTestExtensions
{
    /// <summary>Applies a change to a request and returns it.</summary>
    public static ChatRequest With(this ChatRequest request, Action<ChatRequest> change)
    {
        change(request);
        return request;
    }
}
