using System.Text;
using Pillion.Common.Settings;
using Pillion.Contracts;
using Pillion.Host.Context;
using Pillion.Host.Tests.TestSupport;
using Pillion.ModelGateway;

namespace Pillion.Host.Tests;

/// <summary>Mention resolution (work item 1.10): one test group per kind, fuzzy matching, exclusion and secret rules, truncation.</summary>
public sealed class ContextServiceTests : IDisposable
{
    private readonly TempDirectory _root = new();
    private readonly PillionSettings _settings = new();

    public ContextServiceTests()
    {
        Write("src/Program.cs", "using System;\n\nclass Program { static void Main() { } }\n");
        Write("src/Api/Customer.cs", "namespace Api;\npublic class Customer { }\n");
        Write("src/Domain/Customer.cs", "namespace Domain;\npublic class Customer { }\n");
        Write("src/Api/Orders.cs", "namespace Api;\npublic class Orders { }\n");
        Write("src/Api/OrderService.cs", "namespace Api;\npublic class OrderService { }\n");
        Write(".env", "API_KEY=abc123\n");
        Write("certs/site.pem", "-----BEGIN CERTIFICATE-----\nMIIB\n-----END CERTIFICATE-----\n");
        Write("config/appsettings.Production.json", "{ \"ConnectionStrings\": { \"Db\": \"Server=x;Password=y\" } }");
        Write("config/appsettings.Development.json", "{ \"Logging\": { \"LogLevel\": \"Debug\" } }");
        Write("config/appsettings.json", "{ \"ConnectionStrings\": { \"Db\": \"Server=x\" } }");
        Directory.CreateDirectory(Path("assets"));
        File.WriteAllBytes(Path("assets/logo.bin"), [1, 2, 0, 3, 4]);
    }

    public void Dispose() => _root.Dispose();

    private string Path(string relative) => System.IO.Path.Combine(_root.Path, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));

    private void Write(string relative, string content)
    {
        var path = Path(relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private ContextRpcService Service() => new(TokenCounter.Default, new StaticGatewaySettingsSource(_settings));

    private string[] AllFiles() => [.. Directory.EnumerateFiles(_root.Path, "*", SearchOption.AllDirectories)];

    private MentionRequest Request(string mention, string kind = "", bool approve = false, int maxTokens = 0, string[]? files = null, string? payload = null) => new()
    {
        Mention = mention,
        Kind = kind,
        WorkspaceRoots = [_root.Path],
        CandidateFiles = files ?? AllFiles(),
        Payload = payload,
        MaxTokens = maxTokens,
        ApprovedSensitive = approve,
    };

    private Task<MentionResolution> ResolveAsync(MentionRequest request) => Service().ResolveMentionAsync(request, CancellationToken.None);

    // ---- @file ----

    [Fact]
    public async Task File_ByAbsolutePath_ResolvesWithContentAndTokenCount()
    {
        var result = await ResolveAsync(Request("@file:" + Path("src/Program.cs")));

        Assert.True(result.IsResolved, result.Reason);
        Assert.Equal("file", result.Kind);
        Assert.Equal("Program.cs", result.DisplayName);
        Assert.Equal(Path("src/Program.cs"), result.FilePath);
        Assert.Equal("using System;\n\nclass Program { static void Main() { } }\n", result.Content);
        Assert.True(result.TokenCount > 5);
        Assert.False(result.Truncated);
        Assert.False(result.RequiresApproval);
        Assert.False(result.IsSensitive);
        Assert.Equal("@file:" + Path("src/Program.cs"), result.Mention);
        Assert.Empty(result.Candidates);
        Assert.Equal(string.Empty, result.Reason);
    }

    [Fact]
    public async Task File_ByRelativePath_ResolvesAgainstTheWorkspaceRoots()
    {
        var result = await ResolveAsync(Request("@file:src/Api/Orders.cs", files: []));

        Assert.True(result.IsResolved, result.Reason);
        Assert.Equal(Path("src/Api/Orders.cs"), result.FilePath);
    }

    [Theory]
    [InlineData("@Program.cs")]
    [InlineData("@program.cs")]
    [InlineData("@Program")]
    [InlineData("@file:Program")]
    [InlineData("@Prog")]
    public async Task File_ByFuzzyName_ResolvesWhenExactlyOneFileMatches(string mention)
    {
        var result = await ResolveAsync(Request(mention));

        Assert.True(result.IsResolved, result.Reason);
        Assert.Equal(Path("src/Program.cs"), result.FilePath);
        Assert.Equal("file", result.Kind);
    }

    [Fact]
    public async Task File_WithTwoFilesOfTheSameName_IsAmbiguous_AndReturnsTheCandidates()
    {
        var result = await ResolveAsync(Request("@Customer.cs"));

        Assert.False(result.IsResolved);
        Assert.Equal(2, result.Candidates.Length);
        Assert.Contains(Path("src/Api/Customer.cs"), result.Candidates);
        Assert.Contains(Path("src/Domain/Customer.cs"), result.Candidates);
        Assert.Contains("2 files match", result.Reason, StringComparison.Ordinal);
        Assert.Null(result.Content);
    }

    [Fact]
    public async Task File_APathSuffixSeparatesTwoFilesOfTheSameName()
    {
        var result = await ResolveAsync(Request("@file:Domain/Customer.cs"));

        Assert.True(result.IsResolved, result.Reason);
        Assert.Equal(Path("src/Domain/Customer.cs"), result.FilePath);
    }

    [Fact]
    public async Task File_TheBestTierWins_SoAnExactNameBeatsLongerNames()
    {
        // "Orders" is the stem of Orders.cs and a prefix of OrderService? No: "Orders" is not a prefix of "OrderService.cs".
        var result = await ResolveAsync(Request("@Orders"));

        Assert.True(result.IsResolved, result.Reason);
        Assert.Equal(Path("src/Api/Orders.cs"), result.FilePath);
    }

    [Fact]
    public async Task File_APrefixSharedByTwoFiles_IsAmbiguous()
    {
        var result = await ResolveAsync(Request("@Order"));

        Assert.False(result.IsResolved);
        Assert.Equal(2, result.Candidates.Length);
    }

    [Fact]
    public async Task File_ALooseLetterMatch_OffersCandidatesButNeverResolvesOnItsOwn()
    {
        var result = await ResolveAsync(Request("@OrdSrv"));

        Assert.False(result.IsResolved);
        Assert.Equal([Path("src/Api/OrderService.cs")], result.Candidates);
        Assert.Contains("closest", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task File_NoMatch_SaysSo()
    {
        var result = await ResolveAsync(Request("@file:Nothing.cs"));

        Assert.False(result.IsResolved);
        Assert.Empty(result.Candidates);
        Assert.Contains("No file matches 'Nothing.cs'", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task File_WithoutAFileListOrRoots_ExplainsWhy()
    {
        var request = new MentionRequest { Mention = "@file:Program.cs" };

        var result = await ResolveAsync(request);

        Assert.False(result.IsResolved);
        Assert.Contains("No file list or workspace folder", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task File_WithoutACandidateList_FallsBackToTheFilesUnderTheRoots()
    {
        var result = await ResolveAsync(Request("@Orders.cs", files: []));

        Assert.True(result.IsResolved, result.Reason);
        Assert.Equal(Path("src/Api/Orders.cs"), result.FilePath);
    }

    [Fact]
    public async Task File_OutsideTheWorkspace_IsRefused()
    {
        using var other = new TempDirectory();
        var outside = System.IO.Path.Combine(other.Path, "Outside.cs");
        File.WriteAllText(outside, "class Outside { }");

        var result = await ResolveAsync(Request("@file:" + outside, files: [Path("src/Program.cs")]));

        Assert.False(result.IsResolved);
        Assert.Contains("outside", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task File_ACandidateThatIsNotOnDisk_IsNotFound()
    {
        var ghost = Path("src/Ghost.cs");

        var result = await ResolveAsync(Request("@file:" + ghost, files: [ghost]));

        Assert.False(result.IsResolved);
        Assert.Contains("does not exist", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task File_ABinaryFile_IsNotResolved()
    {
        var result = await ResolveAsync(Request("@file:assets/logo.bin"));

        Assert.False(result.IsResolved);
        Assert.Contains("binary", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task File_TheKindFieldWinsOverTheMentionText()
    {
        var result = await ResolveAsync(Request("@Program.cs", kind: "file"));

        Assert.True(result.IsResolved, result.Reason);
    }

    [Fact]
    public async Task File_AQuotedNameWithSpaces_Resolves()
    {
        Write("src/My File.cs", "class My { }");

        var result = await ResolveAsync(Request("@file:\"My File.cs\""));

        Assert.True(result.IsResolved, result.Reason);
        Assert.Equal("My File.cs", result.DisplayName);
    }

    // ---- Exclusions and secret files ----

    [Theory]
    [InlineData(".env")]
    [InlineData("certs/site.pem")]
    [InlineData("config/appsettings.Production.json")]
    public async Task SecretFiles_AreRefusedUntilTheUserApproves(string relative)
    {
        var refused = await ResolveAsync(Request("@file:" + relative));

        Assert.False(refused.IsResolved);
        Assert.True(refused.RequiresApproval);
        Assert.Contains("secret", refused.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Null(refused.Content);
        Assert.Equal(Path(relative), refused.FilePath);

        var approved = await ResolveAsync(Request("@file:" + relative, approve: true));

        Assert.True(approved.IsResolved, approved.Reason);
        Assert.True(approved.IsSensitive);
        Assert.False(approved.RequiresApproval);
        Assert.False(string.IsNullOrEmpty(approved.Content));
    }

    [Fact]
    public async Task AppSettingsWithoutConnectionStringsOrPasswords_IsOrdinary()
    {
        var result = await ResolveAsync(Request("@file:config/appsettings.Development.json"));

        Assert.True(result.IsResolved, result.Reason);
        Assert.False(result.IsSensitive);
    }

    [Fact]
    public async Task PlainAppSettings_IsOrdinary()
    {
        var result = await ResolveAsync(Request("@file:config/appsettings.json"));

        Assert.True(result.IsResolved, result.Reason);
        Assert.False(result.IsSensitive);
    }

    [Fact]
    public async Task TheExcludeGlobs_AreHonoured_AndApprovalOverridesThem()
    {
        _settings.Indexing.Exclude.Add("src/Domain/**");

        var refused = await ResolveAsync(Request("@file:src/Domain/Customer.cs"));
        var approved = await ResolveAsync(Request("@file:src/Domain/Customer.cs", approve: true));

        Assert.False(refused.IsResolved);
        Assert.True(refused.RequiresApproval);
        Assert.Contains("src/Domain/**", refused.Reason, StringComparison.Ordinal);
        Assert.True(approved.IsResolved);
        Assert.True(approved.IsSensitive);
    }

    // ---- Truncation ----

    [Fact]
    public async Task LongContent_IsTruncatedOnALineBoundary_WithTheCountAndTheFlag()
    {
        var builder = new StringBuilder();
        for (var i = 0; i < 2000; i++)
        {
            builder.Append("    public int Property").Append(i).Append(" { get; set; }\n");
        }

        Write("src/Big.cs", builder.ToString());

        var result = await ResolveAsync(Request("@file:src/Big.cs", maxTokens: 500));

        Assert.True(result.IsResolved);
        Assert.True(result.Truncated);
        Assert.InRange(result.TokenCount, 300, 500);
        Assert.Equal(TokenCounter.Default.ForModel("unknown").Count(result.Content!), result.TokenCount);
        var lines = result.Content!.Split('\n');
        Assert.Matches(@"^\[\.\.\. \d+ lines omitted \.\.\.\]$", lines[^1]);
        Assert.All(lines[..^1], l => Assert.Matches(@"^    public int Property\d+ \{ get; set; \}$", l));
    }

    [Fact]
    public async Task TheDefaultLimit_AppliesWhenTheRequestSetsNone()
    {
        Write("src/Huge.cs", string.Concat(Enumerable.Repeat("var x = 1234567890; // some text to fill the line up\n", 5000)));

        var result = await ResolveAsync(Request("@file:src/Huge.cs"));

        Assert.True(result.Truncated);
        Assert.InRange(result.TokenCount, 1, ContextRpcService.DefaultMaxTokens);
    }

    [Fact]
    public async Task AFileOverTheReadLimit_IsMarkedTruncatedEvenWhenTheTokenLimitIsLarge()
    {
        Write("src/Giant.txt", string.Concat(Enumerable.Repeat("0123456789\n", 150_000)));

        var result = await ResolveAsync(Request("@file:src/Giant.txt", maxTokens: 10_000_000));

        Assert.True(result.IsResolved, result.Reason);
        Assert.True(result.Truncated);
    }

    // ---- @folder ----

    [Fact]
    public async Task Folder_ResolvesToAnOutline_WithSizesAndFirstLines()
    {
        var result = await ResolveAsync(Request("@folder:src/Api"));

        Assert.True(result.IsResolved, result.Reason);
        Assert.Equal("folder", result.Kind);
        Assert.Equal("Api/", result.DisplayName);
        Assert.Equal(Path("src/Api"), result.FilePath);
        var content = result.Content!;
        Assert.Contains("3 files", content, StringComparison.Ordinal);
        Assert.Contains("Customer.cs (", content, StringComparison.Ordinal);
        Assert.Contains("): namespace Api;", content, StringComparison.Ordinal);
        Assert.Contains(" B)", content, StringComparison.Ordinal);
        Assert.Contains("OrderService.cs", content, StringComparison.Ordinal);
        Assert.True(result.TokenCount > 10);
    }

    [Fact]
    public async Task Folder_LeavesOutSecretAndExcludedFiles_AndSaysHowMany()
    {
        _settings.Indexing.Exclude.Add("**/appsettings.Development.json");

        var result = await ResolveAsync(Request("@folder:config"));

        Assert.True(result.IsResolved, result.Reason);
        Assert.DoesNotContain("Production", result.Content!, StringComparison.Ordinal);
        Assert.DoesNotContain("Development", result.Content!, StringComparison.Ordinal);
        Assert.Contains("excluded or secret files not listed", result.Content!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Folder_ByFuzzyName_ResolvesWhenUnique()
    {
        var result = await ResolveAsync(Request("@Domain"));

        Assert.True(result.IsResolved, result.Reason);
        Assert.Equal("folder", result.Kind);
        Assert.Equal(Path("src/Domain"), result.FilePath);
    }

    [Fact]
    public async Task Folder_AmbiguousNames_ReturnCandidates()
    {
        Write("tests/Api/ApiTests.cs", "class ApiTests { }");

        var result = await ResolveAsync(Request("@folder:Api"));

        Assert.False(result.IsResolved);
        Assert.Equal(2, result.Candidates.Length);
        Assert.Contains(Path("src/Api"), result.Candidates);
        Assert.Contains(Path("tests/Api"), result.Candidates);
    }

    [Fact]
    public async Task Folder_OutlineListsAtMostTwoHundredFiles()
    {
        for (var i = 0; i < 230; i++)
        {
            Write($"many/f{i:D3}.txt", "x");
        }

        var result = await ResolveAsync(Request("@folder:many", maxTokens: 100_000));

        Assert.True(result.IsResolved, result.Reason);
        Assert.Contains("230 files, first 200 listed", result.Content!, StringComparison.Ordinal);
        Assert.Equal(201, result.Content!.TrimEnd('\n').Split('\n').Length);
    }

    // ---- @selection, @clipboard, @errors ----

    [Theory]
    [InlineData("@selection", "selection", "Selection")]
    [InlineData("@clipboard", "clipboard", "Clipboard")]
    public async Task SelectionAndClipboard_UseThePayload(string mention, string kind, string display)
    {
        var result = await ResolveAsync(Request(mention, payload: "var x = 1;\nvar y = 2;"));

        Assert.True(result.IsResolved, result.Reason);
        Assert.Equal(kind, result.Kind);
        Assert.Equal(display, result.DisplayName);
        Assert.Equal("var x = 1;\nvar y = 2;", result.Content);
        Assert.True(result.TokenCount > 0);
        Assert.Null(result.FilePath);
    }

    [Theory]
    [InlineData("@selection", null)]
    [InlineData("@selection", "  \n ")]
    [InlineData("@clipboard", "")]
    [InlineData("@errors", null)]
    public async Task EmptyPayloads_AreUnresolved_WithAReason(string mention, string? payload)
    {
        var result = await ResolveAsync(Request(mention, payload: payload));

        Assert.False(result.IsResolved);
        Assert.False(string.IsNullOrEmpty(result.Reason));
    }

    [Fact]
    public async Task ALongSelection_IsTruncatedToTheLimit()
    {
        var payload = string.Concat(Enumerable.Range(0, 500).Select(i => $"line number {i} of the selection\n"));

        var result = await ResolveAsync(Request("@selection", payload: payload, maxTokens: 100));

        Assert.True(result.Truncated);
        Assert.InRange(result.TokenCount, 1, 100);
    }

    [Fact]
    public async Task Errors_AreFormattedFromJsonLines_ErrorsFirst()
    {
        var payload = string.Join(
            "\n",
            """{"severity":"warning","id":"CS0168","message":"The variable 'e' is declared but never used","file":"Foo.cs","line":12}""",
            """{"severity":"error","id":"CS0103","message":"The name 'x' does not exist","file":"Bar.cs","line":7}""",
            "plain text diagnostic",
            """{"severity":"Error","id":"CS1002","message":"; expected","file":"Bar.cs","line":9}""");

        var result = await ResolveAsync(Request("@errors", payload: payload));

        Assert.True(result.IsResolved, result.Reason);
        Assert.Equal("errors", result.Kind);
        Assert.Equal("Errors (2 errors, 1 warnings)", result.DisplayName);
        Assert.Equal(
            [
                "error CS0103: The name 'x' does not exist (Bar.cs:7)",
                "error CS1002: ; expected (Bar.cs:9)",
                "warning CS0168: The variable 'e' is declared but never used (Foo.cs:12)",
                "plain text diagnostic",
            ],
            result.Content!.Split('\n'));
    }

    // ---- Other kinds and the rest of the service ----

    [Theory]
    [InlineData("@symbol:OrderService")]
    [InlineData("@git")]
    [InlineData("@solution")]
    public async Task KindsOfLaterPhases_AreUnresolvedWithAReason(string mention)
    {
        var result = await ResolveAsync(Request(mention));

        Assert.False(result.IsResolved);
        Assert.Contains("not available", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ABareAt_AsksForAName()
    {
        var result = await ResolveAsync(Request("@"));

        Assert.False(result.IsResolved);
        Assert.Contains("name after @", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Search_ReturnsNothing_AndTheIndexReportsNothingIndexed_AndPausingIsHarmless()
    {
        var service = Service();

        var hits = await service.SearchAsync(new SearchRequest { Query = "customer", MaxResults = 5 }, CancellationToken.None);
        var status = await service.GetIndexStatusAsync(CancellationToken.None);
        await service.PauseIndexingAsync(true, CancellationToken.None);
        await service.PauseIndexingAsync(false, CancellationToken.None);

        Assert.Empty(hits);
        Assert.False(status.IsIndexing);
        Assert.False(status.IsPaused);
        Assert.Equal(0, status.IndexedFileCount);
        Assert.Equal(0, status.TotalFileCount);
        Assert.Null(status.LastUpdatedUtc);
    }

    [Fact]
    public async Task ACancelledToken_StopsAFileRead()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Service().ResolveMentionAsync(Request("@file:src/Program.cs"), cts.Token));
    }
}

public class MentionParserTests
{
    [Theory]
    [InlineData("@file:src/Program.cs", "", "file", "src/Program.cs")]
    [InlineData("file:src/Program.cs", "", "file", "src/Program.cs")]
    [InlineData("@folder:src/Api", "", "folder", "src/Api")]
    [InlineData("@Customer.cs", "", "", "Customer.cs")]
    [InlineData("@selection", "", "selection", "")]
    [InlineData("@ERRORS", "", "errors", "")]
    [InlineData("@clipboard", "", "clipboard", "")]
    [InlineData(@"@C:\src\Foo.cs", "", "", @"C:\src\Foo.cs")]
    [InlineData(@"@file:C:\src\Foo.cs", "", "file", @"C:\src\Foo.cs")]
    [InlineData("@file:\"My File.cs\"", "", "file", "My File.cs")]
    [InlineData("@Customer", "file", "file", "Customer")]
    [InlineData("@file:Customer", "folder", "folder", "Customer")]
    [InlineData("@symbol:OrderService", "", "symbol", "OrderService")]
    [InlineData("  @ file:x ", "", "", "file:x")]
    public void Parse_SplitsKindAndValue(string mention, string hint, string kind, string value)
    {
        var parsed = MentionParser.Parse(mention, hint);

        Assert.Equal(kind, parsed.Kind);
        Assert.Equal(value, parsed.Value);
    }
}

public class FileMatcherTests
{
    private static readonly string[] Files =
    [
        @"C:\r\src\Api\Customer.cs",
        @"C:\r\src\Domain\Customer.cs",
        @"C:\r\src\Api\CustomerService.cs",
        @"C:\r\src\Api\Orders.cs",
        @"C:\r\src\Api\OrderService.cs",
        @"C:\r\README.md",
    ];

    [Fact]
    public void ExactName_BeatsLongerNames_ButTwoExactNamesAreAmbiguous()
    {
        var match = FileMatcher.Match("Customer.cs", Files);

        Assert.Null(match.Single);
        Assert.Equal(2, match.Candidates.Count);
        Assert.DoesNotContain(@"C:\r\src\Api\CustomerService.cs", match.Candidates);
    }

    [Theory]
    [InlineData("orders", @"C:\r\src\Api\Orders.cs")]
    [InlineData("README", @"C:\r\README.md")]
    [InlineData("readme.md", @"C:\r\README.md")]
    [InlineData("Domain/Customer", @"C:\r\src\Domain\Customer.cs")]
    [InlineData("domain\\customer.cs", @"C:\r\src\Domain\Customer.cs")]
    [InlineData("CustomerS", @"C:\r\src\Api\CustomerService.cs")]
    public void UniqueMatches_Resolve(string query, string expected)
    {
        var match = FileMatcher.Match(query, Files);

        Assert.Equal(expected, match.Single);
    }

    [Fact]
    public void Candidates_AreCappedAtTen_ShortestPathFirst()
    {
        var many = Enumerable.Range(0, 25).Select(i => $@"C:\r\{new string('d', i)}\File.cs").ToArray();

        var match = FileMatcher.Match("File.cs", many);

        Assert.Null(match.Single);
        Assert.Equal(FileMatcher.MaxCandidates, match.Candidates.Count);
        Assert.Equal(many[0], match.Candidates[0]);
    }

    [Fact]
    public void NoMatch_IsEmpty()
    {
        Assert.True(FileMatcher.Match("zzz", Files).IsEmpty);
        Assert.True(FileMatcher.Match("   ", Files).IsEmpty);
    }
}
