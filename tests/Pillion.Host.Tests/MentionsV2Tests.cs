using Pillion.Common;
using Pillion.Common.Settings;
using Pillion.Contracts;
using Pillion.Host.Context;
using Pillion.Host.Context.Mentions;
using Pillion.Host.Context.Retrieval;
using Pillion.Host.Tests.TestSupport;
using Pillion.Host.WorkspaceQueries;
using Pillion.ModelGateway;

namespace Pillion.Host.Tests;

/// <summary>
/// Mentions v2 (work item 2.13) over a real, encrypted index of a small repository built from the Context Engine's fixtures:
/// <c>@file</c> and <c>@folder</c> from the index, <c>@symbol</c>, <c>@docs</c> and <c>@solution</c>, the secret-file rules, and
/// the IDE kinds (<c>@output</c>, <c>@build</c>, <c>@tests</c>) that fit the payload the VSIX sends.
/// </summary>
public sealed class MentionsV2Tests : IAsyncLifetime, IDisposable
{
    private readonly TempDirectory _data = new();
    private readonly TempDirectory _repo = new();
    private readonly PillionSettings _settings = new();
    private readonly List<ReadOnlyIndexCatalog> _catalogs = [];
    private string _indexId = string.Empty;

    public async Task InitializeAsync() => (_indexId, _) = await MentionTestIndex.BuildAsync(new PillionPaths(_data.Path), _repo.Path);

    public async Task DisposeAsync()
    {
        foreach (var catalog in _catalogs)
        {
            await catalog.DisposeAsync();
        }

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    public void Dispose()
    {
        _data.Dispose();
        _repo.Dispose();
    }

    // ---- @file ----

    [Fact]
    public async Task File_ByFuzzyName_ResolvesFromTheIndex_WithoutAClientFileList()
    {
        var result = await ResolveAsync(Request("@file:shapes"));

        Assert.True(result.IsResolved, result.Reason);
        Assert.Equal(MentionKinds.File, result.Kind);
        Assert.Equal(RepoPath("typescript/shapes.ts"), result.FilePath);
        Assert.Contains("export class Circle", result.Content, StringComparison.Ordinal);
        var citation = Assert.Single(result.Citations);
        Assert.Equal((RepoPath("typescript/shapes.ts"), 0, 37), (citation.FilePath, citation.StartLine, citation.EndLine));
        Assert.Equal(-1, citation.ReplyOffset);
    }

    [Fact]
    public async Task File_TheIndexIsTheList_AFileItLeftOutIsNotMatchedByName_ButItsExactPathResolves()
    {
        File.WriteAllText(RepoPath("src/App/Unindexed.cs"), "class Unindexed { }\n");

        var byName = await ResolveAsync(Request("@file:Unindexed"));
        var byPath = await ResolveAsync(Request("@file:src/App/Unindexed.cs"));

        Assert.False(byName.IsResolved);
        Assert.Contains("No file matches", byName.Reason, StringComparison.Ordinal);
        Assert.True(byPath.IsResolved, byPath.Reason);
    }

    [Fact]
    public async Task File_AnAmbiguousName_ListsTheIndexedCandidates()
    {
        var result = await ResolveAsync(Request("@file:Greeter"));

        Assert.False(result.IsResolved);
        Assert.Equal([RepoPath("vb/Greeter.vb"), RepoPath("csharp/Greeter.cs")], result.Candidates.OrderBy(c => c.Length).ThenByDescending(c => c, StringComparer.Ordinal));
    }

    [Fact]
    public async Task File_TheSecretFileTheIndexLeftOut_StillNeedsApproval_AndIsSensitiveOnceApproved()
    {
        var refused = await ResolveAsync(Request("@file:.env"));
        var approved = await ResolveAsync(Request("@file:.env", approve: true));

        Assert.False(refused.IsResolved);
        Assert.True(refused.RequiresApproval);
        Assert.Equal(RepoPath(".env"), refused.FilePath);
        Assert.Null(refused.Content);
        Assert.True(approved.IsResolved, approved.Reason);
        Assert.True(approved.IsSensitive);
    }

    [Fact]
    public async Task File_WithoutAnIndex_TheClientListAnswersAsInPhaseOne()
    {
        var service = new ContextRpcService(TokenCounter.Default, new StaticGatewaySettingsSource(_settings));
        var request = Request("@file:shapes");
        request.CandidateFiles = [RepoPath("typescript/shapes.ts"), RepoPath("csharp/Greeter.cs")];

        var result = await service.ResolveMentionAsync(request, CancellationToken.None);

        Assert.True(result.IsResolved, result.Reason);
        Assert.Equal(RepoPath("typescript/shapes.ts"), result.FilePath);
    }

    // ---- @folder ----

    [Fact]
    public async Task Folder_IsOutlinedFromTheIndex_WithFirstChunkSummariesAndTheRepoMapsSymbols()
    {
        var result = await ResolveAsync(Request("@folder:csharp"));

        Assert.True(result.IsResolved, result.Reason);
        Assert.Equal(MentionKinds.Folder, result.Kind);
        Assert.Equal("csharp/", result.DisplayName);
        Assert.Contains("1 indexed files", result.Content, StringComparison.Ordinal);
        Assert.Contains("Greeter.cs: ", result.Content, StringComparison.Ordinal);
        Assert.Contains("class Greeter", result.Content, StringComparison.Ordinal);
        Assert.Contains("Greet(string name)", result.Content, StringComparison.Ordinal);
        var citation = Assert.Single(result.Citations);
        Assert.Equal(RepoPath("csharp/Greeter.cs"), citation.FilePath);
        Assert.StartsWith(_indexId + ":", citation.ChunkId, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Folder_TheRoot_ListsTheIndexedFiles_ButNeverTheSecretFile()
    {
        var result = await ResolveAsync(Request("@folder:" + _repo.Path));

        Assert.True(result.IsResolved, result.Reason);
        Assert.Contains("README.md", result.Content, StringComparison.Ordinal);
        Assert.Contains("src/App/Program.cs", result.Content, StringComparison.Ordinal);
        Assert.DoesNotContain(".env", result.Content, StringComparison.Ordinal);
    }

    // ---- @symbol ----

    [Fact]
    public async Task Symbol_ANameInTwoLanguages_IsAmbiguous_AndACandidatePinsThatDefinitionWithItsReferences()
    {
        var ambiguous = await ResolveAsync(Request("@symbol:Greet"));

        Assert.False(ambiguous.IsResolved);
        Assert.Equal(MentionKinds.Symbol, ambiguous.Kind);
        Assert.Equal(2, ambiguous.Candidates.Length);
        var csharp = Assert.Single(ambiguous.Candidates, c => c.Contains("Greeter.cs", StringComparison.Ordinal));
        Assert.StartsWith("Fixtures.Sample.Greeter.Greet#" + RepoPath("csharp/Greeter.cs") + ":", csharp, StringComparison.Ordinal);

        var result = await ResolveAsync(Request("@symbol:" + csharp));

        Assert.True(result.IsResolved, result.Reason);
        Assert.Equal("Greet", result.DisplayName);
        Assert.Equal(RepoPath("csharp/Greeter.cs"), result.FilePath);
        Assert.Contains("method Fixtures.Sample.Greeter.Greet in csharp/Greeter.cs", result.Content, StringComparison.Ordinal);
        Assert.Contains("public string Greet(string name)", result.Content, StringComparison.Ordinal);
        Assert.Contains("approximate, from the index", result.Content, StringComparison.Ordinal);
        Assert.Contains("src/App/Program.cs:11", result.Content, StringComparison.Ordinal); // greeter.Greet("Ann")
        Assert.Contains("csharp/Greeter.cs:37 in Fixtures.Sample.Greeter.GreetAll", result.Content, StringComparison.Ordinal);
        Assert.Contains(result.Citations, c => c.FilePath == RepoPath("csharp/Greeter.cs") && c.ChunkId.StartsWith(_indexId + ":", StringComparison.Ordinal));
        Assert.Contains(result.Citations, c => c.FilePath == RepoPath("src/App/Program.cs") && c.StartLine == 10 && c.EndLine == 10);
    }

    [Fact]
    public async Task Symbol_ATypeAndItsConstructor_PinTheType()
    {
        var result = await ResolveAsync(Request("@symbol:OrderService"));

        Assert.True(result.IsResolved, result.Reason);
        Assert.Contains("class com.example.orders.OrderService", result.Content, StringComparison.Ordinal);
        Assert.Contains("public void place(String order)", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Symbol_AFuzzyPrefix_ResolvesWhenUnique()
    {
        var result = await ResolveAsync(Request("@symbol:GreetA"));

        Assert.True(result.IsResolved, result.Reason);
        Assert.Equal("GreetAll", result.DisplayName);
    }

    [Fact]
    public async Task Symbol_TheQualifierPinsTheKind_SoABareNameMatchingAFileIsReadAsASymbol()
    {
        // "Program" names the file src/App/Program.cs; with the symbol qualifier the class answers instead.
        var request = Request("@Program");
        request.Qualifier = MentionKinds.Symbol;

        var result = await ResolveAsync(request);

        Assert.True(result.IsResolved, result.Reason);
        Assert.Equal(MentionKinds.Symbol, result.Kind);
        Assert.Contains("class Shop.App.Program", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Symbol_ABareNameWithNoFile_ResolvesAsASymbol()
    {
        var result = await ResolveAsync(Request("@restock"));

        Assert.True(result.IsResolved, result.Reason);
        Assert.Equal(MentionKinds.Symbol, result.Kind);
        Assert.Contains("def restock(self, amount)", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Symbol_ReferencesComeFromTheWorkspaceQueries_WhenTheyAnswer()
    {
        var queries = new FakeQueries(new WorkspaceQueryResult(
            true,
            [new WorkspaceSite(RepoPath("src/App/Program.cs"), 10, "System.Console.WriteLine(greeter.Greet(\"Ann\"));", string.Empty, false)],
            false,
            WorkspaceQuerySource.Roslyn,
            null,
            TimeSpan.FromMilliseconds(5)));

        var result = await ResolveAsync(Request("@symbol:GreetAll"), queries);

        Assert.True(result.IsResolved, result.Reason);
        Assert.Contains("References: 1 in 1 file (from Roslyn)", result.Content, StringComparison.Ordinal);
        var asked = Assert.Single(queries.Queries);
        Assert.Equal(("GreetAll", RepoPath("csharp/Greeter.cs")), (asked.Name, asked.FilePath));
        Assert.Equal(ContextRpcService.MaxSymbolReferences, asked.MaxResults);
    }

    [Fact]
    public async Task Symbol_AReferenceInAnExcludedFile_IsListedWithoutItsSnippet()
    {
        _settings.Indexing.Exclude = ["src/App/**"];
        var csharp = (await ResolveAsync(Request("@symbol:Greet"))).Candidates.Single(c => c.Contains("Greeter.cs", StringComparison.Ordinal));

        var result = await ResolveAsync(Request("@symbol:" + csharp));

        Assert.True(result.IsResolved, result.Reason);
        var line = Assert.Single(result.Content!.Split('\n'), l => l.Contains("src/App/Program.cs:", StringComparison.Ordinal));
        Assert.Equal("  src/App/Program.cs:11 in Shop.App.Program.Main", line); // no ": greeter.Greet(...)" snippet
    }

    [Fact]
    public async Task Symbol_WithoutAnIndex_SaysTheIndexIsNeeded()
    {
        var service = new ContextRpcService(TokenCounter.Default, new StaticGatewaySettingsSource(_settings));

        var result = await service.ResolveMentionAsync(Request("@symbol:Greet"), CancellationToken.None);

        Assert.False(result.IsResolved);
        Assert.Contains("no index is open", result.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Greet", "Greet", "", 0)]
    [InlineData("A.B.Greet#C:\\repo\\a.cs:12", "A.B.Greet", "C:\\repo\\a.cs", 12)]
    [InlineData("Greet#src/a.cs", "Greet", "src/a.cs", 0)]
    public void SymbolValues_SplitIntoNamePathAndLine(string value, string name, string path, int line) =>
        Assert.Equal((name, path, line), ContextRpcService.ParseSymbolValue(value));

    // ---- @docs ----

    [Fact]
    public async Task Docs_ByName_PinsTheDocumentsChunksInOrder_UntilTheLimit()
    {
        var whole = await ResolveAsync(Request("@docs:guide", maxTokens: 20000));
        var cut = await ResolveAsync(Request("@docs:guide", maxTokens: 400));

        Assert.True(whole.IsResolved, whole.Reason);
        Assert.Equal(MentionKinds.Docs, whole.Kind);
        Assert.Equal("guide.md", whole.DisplayName);
        Assert.False(whole.Truncated);
        Assert.Contains("Paragraph 12.24", whole.Content, StringComparison.Ordinal);
        Assert.True(whole.Citations.Length > 1);
        Assert.All(whole.Citations, c => Assert.StartsWith(_indexId + ":", c.ChunkId, StringComparison.Ordinal));
        Assert.Equal(whole.Citations.OrderBy(c => c.StartLine).Select(c => c.StartLine), whole.Citations.Select(c => c.StartLine));

        Assert.True(cut.IsResolved, cut.Reason);
        Assert.True(cut.Truncated);
        Assert.True(cut.TokenCount <= 400);
        Assert.Contains("# Developer guide", cut.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("Paragraph 12.24", cut.Content, StringComparison.Ordinal);
        Assert.True(cut.Citations.Length < whole.Citations.Length);
    }

    [Fact]
    public async Task Docs_Bare_ListsTheMarkdownAndTextDocuments()
    {
        var result = await ResolveAsync(Request("@docs"));

        Assert.True(result.IsResolved, result.Reason);
        Assert.Contains("README.md: # Shop sample", result.Content, StringComparison.Ordinal);
        Assert.Contains("docs/guide.md: # Developer guide", result.Content, StringComparison.Ordinal);
        Assert.Contains("text/notes.txt", result.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("shapes.ts", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Docs_ARegisteredFolder_IsSearchedToo_AndItsSecretFilesNeedApproval()
    {
        using var handbook = new TempDirectory();
        File.WriteAllText(Path.Combine(handbook.Path, "onboarding.md"), "# Onboarding\n\nWelcome to the team.\n");
        Directory.CreateDirectory(Path.Combine(handbook.Path, ".ssh"));
        File.WriteAllText(Path.Combine(handbook.Path, ".ssh", "notes.md"), "# Keys\n");
        _settings.Mentions.DocsFolders = [handbook.Path];

        var result = await ResolveAsync(Request("@docs:onboarding"));
        var secret = await ResolveAsync(Request("@docs:" + Path.Combine(handbook.Path, ".ssh", "notes.md")));

        Assert.True(result.IsResolved, result.Reason);
        Assert.Contains("Welcome to the team.", result.Content, StringComparison.Ordinal);
        var citation = Assert.Single(result.Citations);
        Assert.Equal(string.Empty, citation.ChunkId); // read from disk, not the index
        Assert.True(secret.RequiresApproval, secret.Reason);
    }

    // ---- @solution ----

    [Fact]
    public async Task Solution_RendersTheRepoMap_WithinTheCap()
    {
        var full = await ResolveAsync(Request("@solution"));
        var capped = await ResolveAsync(Request("@solution", maxTokens: 300));

        Assert.True(full.IsResolved, full.Reason);
        Assert.Equal(MentionKinds.Solution, full.Kind);
        Assert.StartsWith("Repo map: ", full.DisplayName, StringComparison.Ordinal);
        Assert.Contains("Repo map of " + _repo.Path, full.Content, StringComparison.Ordinal);
        Assert.Contains("Greeter", full.Content, StringComparison.Ordinal);
        Assert.True(full.TokenCount <= ContextRpcService.DefaultSolutionMaxTokens);
        Assert.True(capped.TokenCount <= 300);
        Assert.True(capped.Truncated);
    }

    // ---- @output, @build and @tests ----

    [Fact]
    public async Task Output_KeepsTheEndOfThePaneNamedByTheArgument()
    {
        var payload = string.Join("\n", Enumerable.Range(1, 2000).Select(i => $"Debug line {i} with some words to fill it"));
        var request = Request("@output:debug", payload: payload, maxTokens: 200);
        request.Arguments = ["debug"];

        var result = await ResolveAsync(request);

        Assert.True(result.IsResolved, result.Reason);
        Assert.Equal("Output: Debug", result.DisplayName);
        Assert.True(result.Truncated);
        Assert.True(result.TokenCount <= 200);
        Assert.StartsWith("[... ", result.Content, StringComparison.Ordinal);
        Assert.EndsWith("Debug line 2000 with some words to fill it", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Build_IsNamedByItsOutcome_AndCitesTheDiagnostics()
    {
        var payload = "Build started...\n1>C:\\src\\Shop\\Order.cs(12,5): error CS1002: ; expected [C:\\src\\Shop\\Shop.csproj]\n"
            + "1>C:\\src\\Shop\\Order.cs(30,1): warning CS0168: unused\n========== Build: 0 succeeded, 1 failed ==========\nBuild failed.\n";

        var result = await ResolveAsync(Request("@build", payload: payload));

        Assert.True(result.IsResolved, result.Reason);
        Assert.Equal("Last build (failed)", result.DisplayName);
        Assert.Equal([("C:\\src\\Shop\\Order.cs", 11), ("C:\\src\\Shop\\Order.cs", 29)], result.Citations.Select(c => (c.FilePath, c.StartLine)));
    }

    [Fact]
    public async Task Tests_CiteTheStackFrames()
    {
        var payload = "Failed OrderTests.Totals [12 ms]\n  Error Message:\n   Assert.Equal() Failure\n  Stack Trace:\n     at OrderTests.Totals() in C:\\src\\Shop.Tests\\OrderTests.cs:line 42\n";

        var result = await ResolveAsync(Request("@tests", payload: payload));

        Assert.True(result.IsResolved, result.Reason);
        Assert.Equal("Tests", result.DisplayName);
        var citation = Assert.Single(result.Citations);
        Assert.Equal(("C:\\src\\Shop.Tests\\OrderTests.cs", 41), (citation.FilePath, citation.StartLine));
    }

    [Theory]
    [InlineData("@output")]
    [InlineData("@build")]
    [InlineData("@tests")]
    public async Task TheIdeKinds_WithoutAPayload_AreUnresolvedWithAReason(string mention)
    {
        var result = await ResolveAsync(Request(mention));

        Assert.False(result.IsResolved);
        Assert.NotEqual(string.Empty, result.Reason);
    }

    [Fact]
    public void TheIndexRootAndAWorkspaceRoot_AreRelatedWhenOneHoldsTheOther()
    {
        Assert.True(MentionIndexReader.IsRelated(@"C:\repo", @"C:\repo\src"));
        Assert.True(MentionIndexReader.IsRelated(@"C:\repo\src", @"C:\repo"));
        Assert.True(MentionIndexReader.IsRelated(@"C:\repo\", @"c:\REPO"));
        Assert.False(MentionIndexReader.IsRelated(@"C:\repo", @"C:\repository"));
    }

    private string RepoPath(string relative) => Path.Combine(_repo.Path, relative.Replace('/', Path.DirectorySeparatorChar));

    private MentionRequest Request(string mention, bool approve = false, int maxTokens = 0, string? payload = null) => new()
    {
        Mention = mention,
        WorkspaceRoots = [_repo.Path],
        ApprovedSensitive = approve,
        MaxTokens = maxTokens,
        Payload = payload,
    };

    private Task<MentionResolution> ResolveAsync(MentionRequest request, IWorkspaceQueries? queries = null)
    {
        var catalog = new ReadOnlyIndexCatalog(new PillionPaths(_data.Path), new TestIndex.AllOn());
        _catalogs.Add(catalog);
        var service = new ContextRpcService(TokenCounter.Default, new StaticGatewaySettingsSource(_settings), catalog: new WorkspaceCatalog(catalog, _repo.Path), queries: queries);
        return service.ResolveMentionAsync(request, CancellationToken.None);
    }

    private sealed class FakeQueries(WorkspaceQueryResult result) : IWorkspaceQueries
    {
        public List<WorkspaceSymbolQuery> Queries { get; } = [];

        public Task<WorkspaceQueryResult> FindReferencesAsync(WorkspaceSymbolQuery query, CancellationToken cancellationToken)
        {
            Queries.Add(query);
            return Task.FromResult(result);
        }

        public Task<WorkspaceQueryResult> CallersOfAsync(WorkspaceSymbolQuery query, CancellationToken cancellationToken) => Task.FromResult(result);
    }
}
