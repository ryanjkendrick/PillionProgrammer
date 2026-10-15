using LibGit2Sharp;
using Pillion.Common.Settings;
using Pillion.Contracts;
using Pillion.Host.Context;
using Pillion.Host.Context.Mentions;
using Pillion.Host.Tests.TestSupport;
using Pillion.ModelGateway;

namespace Pillion.Host.Tests;

/// <summary>
/// <c>@git</c> (work item 2.13) over a temporary repository: the working-tree diff, the staged changes, the last commits,
/// redaction of a planted secret, secret files listed without their changes, the cap, and paths outside the repository.
/// </summary>
public sealed class GitMentionTests : IDisposable
{
    // A well-formed but made-up AWS access key id (not the documentation example, which the scanner allows).
    private const string PlantedKey = "AKIAZ7Q3XK2M5TPLW4RD";

    private readonly TempDirectory _repo = new();
    private readonly Signature _author = new("Pillion Tests", "tests@example.invalid", new DateTimeOffset(2026, 10, 15, 9, 0, 0, TimeSpan.Zero));

    public GitMentionTests()
    {
        Repository.Init(_repo.Path);
        Write("src/Order.cs", "class Order\n{\n    int Total;\n}\n");
        Write("README.md", "# Shop\n");
        Commit("Add the order");
        Write("src/Order.cs", "class Order\n{\n    int Total;\n    int Tax;\n}\n");
        Commit("Add tax");
    }

    public void Dispose()
    {
        // Git writes its objects read-only; clear that so the folder can be deleted.
        foreach (var file in Directory.EnumerateFiles(_repo.Path, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        _repo.Dispose();
    }

    [Fact]
    public async Task Default_IsTheWorkingTreeDiff_WithCitationsOfTheChangedLines()
    {
        Write("src/Order.cs", "class Order\n{\n    int Total;\n    int Tax;\n    int Discount;\n}\n");

        var result = await ResolveAsync(Request("@git"));

        Assert.True(result.IsResolved, result.Reason);
        Assert.Equal(MentionKinds.Git, result.Kind);
        Assert.Equal("Git: working tree", result.DisplayName);
        Assert.Contains("Working-tree changes (not staged)", result.Content, StringComparison.Ordinal);
        Assert.Contains("+    int Discount;", result.Content, StringComparison.Ordinal);
        Assert.False(result.Truncated);
        var citation = Assert.Single(result.Citations);
        Assert.Equal(Path.Combine(_repo.Path, "src", "Order.cs"), citation.FilePath);
        Assert.InRange(4, citation.StartLine, citation.EndLine); // the new line is zero-based line 4
    }

    [Fact]
    public async Task Staged_ShowsOnlyTheStagedChanges()
    {
        Write("src/Order.cs", "class Order\n{\n    int Total;\n    int Tax;\n    int Staged;\n}\n");
        using (var repository = new Repository(_repo.Path))
        {
            Commands.Stage(repository, "src/Order.cs");
        }

        Write("README.md", "# Shop\n\nUnstaged line.\n");

        var byArgument = await ResolveAsync(Request("@git", "staged"));
        var byValue = await ResolveAsync(Request("@git:staged"));

        Assert.True(byArgument.IsResolved, byArgument.Reason);
        Assert.Equal("Git: staged", byArgument.DisplayName);
        Assert.Contains("+    int Staged;", byArgument.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("Unstaged line.", byArgument.Content, StringComparison.Ordinal);
        Assert.Equal(byArgument.Content, byValue.Content);
    }

    [Fact]
    public async Task Log_ListsTheLastNCommits_NewestFirst_WithTheirPatches()
    {
        var one = await ResolveAsync(Request("@git", "log", "1"));
        var both = await ResolveAsync(Request("@git:log:5"));

        Assert.True(one.IsResolved, one.Reason);
        Assert.Equal("Git: last 1 commits", one.DisplayName);
        Assert.Contains("    Add tax", one.Content, StringComparison.Ordinal);
        Assert.Contains("+    int Tax;", one.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("Add the order", one.Content, StringComparison.Ordinal);
        Assert.Contains("Author: Pillion Tests", one.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("tests@example.invalid", one.Content, StringComparison.Ordinal);

        Assert.True(both.IsResolved, both.Reason);
        Assert.Equal("Git: last 2 commits", both.DisplayName);
        Assert.True(both.Content!.IndexOf("Add tax", StringComparison.Ordinal) < both.Content.IndexOf("Add the order", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ARevisionRange_ListsItsCommits()
    {
        var result = await ResolveAsync(Request("@git", "HEAD~1..HEAD"));

        Assert.True(result.IsResolved, result.Reason);
        Assert.Contains("Add tax", result.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("Add the order", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task APlantedSecretInTheDiff_IsRedacted()
    {
        Write("src/Settings.cs", $"class Settings\n{{\n    const string AwsKey = \"{PlantedKey}\";\n}}\n");

        var result = await ResolveAsync(Request("@git"));

        Assert.True(result.IsResolved, result.Reason);
        Assert.Contains("src/Settings.cs", result.Content, StringComparison.Ordinal);
        Assert.DoesNotContain(PlantedKey, result.Content, StringComparison.Ordinal);
        Assert.Contains("<redacted:", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASecretFile_IsListedWithoutItsChanges()
    {
        Write(".env", "DB_PASSWORD=hunter2-but-longer\n");

        var result = await ResolveAsync(Request("@git"));

        Assert.True(result.IsResolved, result.Reason);
        Assert.Contains("diff --git a/.env b/.env", result.Content, StringComparison.Ordinal);
        Assert.Contains("[changes to this file are not shown: It looks like a secret file", result.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ALongDiff_IsCutToTheTokenLimit_AndMarkedTruncated()
    {
        Write("src/Big.cs", string.Join("\n", Enumerable.Range(0, 3000).Select(i => $"// line {i} of a long generated file")) + "\n");

        var result = await ResolveAsync(Request("@git", maxTokens: 300));

        Assert.True(result.IsResolved, result.Reason);
        Assert.True(result.Truncated);
        Assert.True(result.TokenCount <= 300);
    }

    [Fact]
    public async Task APathOutsideTheRepository_IsRefused()
    {
        using var elsewhere = new TempDirectory();

        var result = await ResolveAsync(Request("@git", "diff", elsewhere.Path));

        Assert.False(result.IsResolved);
        Assert.Contains("outside the repository", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task APathInsideTheRepository_LimitsTheDiff()
    {
        Write("src/Order.cs", "class Order\n{\n    int Total;\n    int Tax;\n    int Discount;\n}\n");
        Write("README.md", "# Shop\n\nChanged readme.\n");

        var result = await ResolveAsync(Request("@git", "diff", "src"));

        Assert.True(result.IsResolved, result.Reason);
        Assert.Equal("Git: working tree (src)", result.DisplayName);
        Assert.Contains("int Discount", result.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("Changed readme", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFolderOutsideAnyRepository_IsUnresolvedWithAReason()
    {
        using var plain = new TempDirectory();
        var request = Request("@git");
        request.WorkspaceRoots = [plain.Path];

        var result = await ResolveAsync(request);

        Assert.False(result.IsResolved);
        Assert.Contains("not in a git repository", result.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(new string[0], "", "Diff", 10, "", "")]
    [InlineData(new[] { "staged" }, "", "Staged", 10, "", "")]
    [InlineData(new string[0], "staged", "Staged", 10, "", "")]
    [InlineData(new[] { "log", "5" }, "", "Log", 5, "", "")]
    [InlineData(new string[0], "log 3", "Log", 3, "", "")]
    [InlineData(new string[0], "log:7", "Log", 7, "", "")]
    [InlineData(new[] { "log", "500" }, "", "Log", 50, "", "")]
    [InlineData(new[] { "HEAD~3..HEAD" }, "", "Range", 10, "HEAD~3..HEAD", "")]
    [InlineData(new string[0], "src/Api", "Diff", 10, "", "src/Api")]
    [InlineData(new string[0], "staged:src/Api", "Staged", 10, "", "src/Api")]
    public void Arguments_AreReadFromTheRequestOrTheValue(string[] arguments, string value, string form, int count, string range, string path)
    {
        var request = GitMentions.Parse(arguments, value);

        Assert.Equal((Enum.Parse<GitForm>(form), count, range), (request.Form, request.Count, request.Range));
        Assert.Equal(path.Length == 0 ? [] : [path], request.Paths);
    }

    private static Task<MentionResolution> ResolveAsync(MentionRequest request) =>
        new ContextRpcService(TokenCounter.Default, new StaticGatewaySettingsSource(new PillionSettings())).ResolveMentionAsync(request, CancellationToken.None);

    private MentionRequest Request(string mention, params string[] arguments) => new()
    {
        Mention = mention,
        WorkspaceRoots = [_repo.Path],
        Arguments = arguments,
    };

    private MentionRequest Request(string mention, int maxTokens) => new()
    {
        Mention = mention,
        WorkspaceRoots = [_repo.Path],
        MaxTokens = maxTokens,
    };

    private void Write(string relative, string text)
    {
        var path = Path.Combine(_repo.Path, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private void Commit(string message)
    {
        using var repository = new Repository(_repo.Path);
        Commands.Stage(repository, "*");
        repository.Commit(message, _author, _author);
    }
}
