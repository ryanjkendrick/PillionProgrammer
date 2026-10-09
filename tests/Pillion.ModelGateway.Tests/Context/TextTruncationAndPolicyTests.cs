using System.Text;

namespace Pillion.ModelGateway.Tests.Context;

/// <summary>A token counter whose count is exact and easy to reason about: one token per four characters, rounded up.</summary>
internal sealed class QuarterCounter : ITokenCounter, IModelTokenCounter
{
    public string Family => "test-quarter";

    public bool IsExact => true;

    public int Count(string text) => string.IsNullOrEmpty(text) ? 0 : (text.Length + 3) / 4;

    public IModelTokenCounter ForModel(string modelId) => this;
}

public class TextTruncationTests
{
    private static readonly QuarterCounter Counter = new();

    private static string Lines(int count, int width = 40)
    {
        var builder = new StringBuilder();
        for (var i = 0; i < count; i++)
        {
            builder.Append("line ").Append(i.ToString("D4", System.Globalization.CultureInfo.InvariantCulture)).Append(' ').Append('x', width - 10).Append('\n');
        }

        return builder.ToString();
    }

    [Fact]
    public void TextThatFits_IsReturnedUnchanged()
    {
        var text = Lines(5);

        var result = TextTruncation.KeepHead(text, 1000, Counter);

        Assert.Equal(text, result.Text);
        Assert.False(result.Truncated);
        Assert.Equal(Counter.Count(text), result.Tokens);
    }

    [Fact]
    public void KeepHead_CutsOnALineBoundary_AndMarksTheElision()
    {
        var text = Lines(100);

        var result = TextTruncation.KeepHead(text, 100, Counter);

        Assert.True(result.Truncated);
        Assert.InRange(result.Tokens, 1, 100);
        Assert.Equal(Counter.Count(result.Text), result.Tokens);
        Assert.StartsWith("line 0000 ", result.Text, StringComparison.Ordinal);
        var lines = result.Text.Split('\n');
        Assert.Matches(@"^\[\.\.\. \d+ lines omitted \.\.\.\]$", lines[^1]);
        Assert.All(lines[..^1], l => Assert.StartsWith("line ", l, StringComparison.Ordinal));
        Assert.All(lines[..^1], l => Assert.Equal(40, l.Length));
    }

    [Fact]
    public void KeepHeadAndTail_KeepsBothEnds_WithTheElisionBetween()
    {
        var text = Lines(100);

        var result = TextTruncation.KeepHeadAndTail(text, 200, Counter);

        Assert.True(result.Truncated);
        Assert.InRange(result.Tokens, 100, 200);
        Assert.StartsWith("line 0000 ", result.Text, StringComparison.Ordinal);
        Assert.EndsWith("line 0099 " + new string('x', 30) + "\n", result.Text.TrimEnd() + "\n", StringComparison.Ordinal);
        Assert.Matches(@"\[\.\.\. \d+ lines omitted \.\.\.\]", result.Text);
        var marker = result.Text.Split('\n').Single(l => l.StartsWith("[...", StringComparison.Ordinal));
        var omitted = int.Parse(marker.Split(' ')[1], System.Globalization.CultureInfo.InvariantCulture);
        var kept = result.Text.Split('\n').Count(l => l.StartsWith("line ", StringComparison.Ordinal));
        Assert.Equal(100, omitted + kept);
    }

    [Fact]
    public void KeepMiddle_KeepsTheCentre_WithMarkersAtBothCutEnds()
    {
        var text = Lines(101);

        var result = TextTruncation.KeepMiddle(text, 150, Counter);

        Assert.True(result.Truncated);
        Assert.InRange(result.Tokens, 1, 150);
        var lines = result.Text.Split('\n');
        Assert.StartsWith("[...", lines[0], StringComparison.Ordinal);
        Assert.StartsWith("[...", lines[^1], StringComparison.Ordinal);
        Assert.Contains("line 0050 ", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("line 0000 ", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("line 0100 ", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void ABudgetOfZero_YieldsNothing()
    {
        var result = TextTruncation.KeepHead(Lines(10), 0, Counter);

        Assert.Equal(string.Empty, result.Text);
        Assert.True(result.Truncated);
    }

    [Fact]
    public void ASingleVeryLongLine_IsCutInsideTheLine()
    {
        var text = new string('a', 4000);

        var result = TextTruncation.KeepHead(text, 100, Counter);

        Assert.True(result.Truncated);
        Assert.InRange(result.Tokens, 1, 100);
    }

    [Fact]
    public void WithARealTokenizer_TheResultFitsTheBudget()
    {
        var counter = TokenCounter.Default.ForModel("gpt-4o");
        var text = string.Concat(Enumerable.Range(0, 400).Select(i => $"public int Method{i}(string name, int count) => name.Length + count * {i};\n"));

        var result = TextTruncation.KeepHeadAndTail(text, 500, counter);

        Assert.True(result.Truncated);
        Assert.InRange(result.Tokens, 300, 500);
        Assert.Equal(counter.Count(result.Text), result.Tokens);
    }
}

public class FileAccessPolicyTests
{
    [Theory]
    [InlineData(@"C:\src\app\.env")]
    [InlineData(@"C:\src\app\.env.production")]
    [InlineData(@"C:\src\app\.ENV.local")]
    [InlineData(@"C:\certs\site.pfx")]
    [InlineData(@"C:\certs\site.PEM")]
    [InlineData(@"C:\certs\id.key")]
    [InlineData(@"C:\src\app\secrets.json")]
    [InlineData(@"C:\Users\me\.ssh\config")]
    [InlineData(@"C:\Users\me\.aws\whatever.txt")]
    [InlineData(@"C:\home\.netrc")]
    [InlineData("/home/me/id_ed25519")]
    public void SecretFiles_NeedApproval(string path)
    {
        var decision = FileAccessPolicy.Default.Check(path);

        Assert.Equal(FileAccessVerdict.SecretFile, decision.Verdict);
        Assert.False(decision.IsAllowed);
        Assert.False(string.IsNullOrEmpty(decision.Reason));
    }

    [Theory]
    [InlineData(@"C:\src\app\.env.example")]
    [InlineData(@"C:\src\app\.env.sample")]
    [InlineData(@"C:\src\app\Program.cs")]
    [InlineData(@"C:\src\app\appsettings.json")]
    [InlineData(@"C:\src\app\environment.cs")]
    [InlineData(@"C:\src\app\README.md")]
    public void OrdinaryFiles_AreAllowed(string path)
    {
        Assert.True(FileAccessPolicy.Default.Check(path).IsAllowed);
    }

    [Theory]
    [InlineData("""{ "ConnectionStrings": { "Db": "Server=x" } }""", true)]
    [InlineData("""{ "Smtp": { "Password": "x" } }""", true)]
    [InlineData("""{ "Logging": { "Level": "Debug" } }""", false)]
    public void AppSettingsVariants_AreSecretOnlyWhenTheContentMentionsConnectionStringsOrPasswords(string content, bool secret)
    {
        var decision = FileAccessPolicy.Default.Check(@"C:\src\app\appsettings.Production.json", () => content);

        Assert.Equal(secret, !decision.IsAllowed);
    }

    [Fact]
    public void AnAppSettingsVariant_WithoutContentToInspect_IsTreatedAsSecret()
    {
        Assert.False(FileAccessPolicy.Default.Check(@"C:\src\app\appsettings.Development.json").IsAllowed);
    }

    [Theory]
    [InlineData("*.generated.cs", @"C:\src\Foo.generated.cs", true)]
    [InlineData("*.generated.cs", @"C:\src\Foo.cs", false)]
    [InlineData("**/bin/**", @"C:\src\app\bin\Debug\app.dll", true)]
    [InlineData("**/bin/**", @"C:\src\app\binary\app.dll", false)]
    [InlineData("node_modules", @"C:\src\web\node_modules\lib\index.js", true)]
    [InlineData("secrets/**", @"C:\src\secrets\keys.txt", true)]
    [InlineData("secrets/**", @"C:\src\notsecrets\keys.txt", false)]
    [InlineData("src/legacy/**", @"C:\repo\src\legacy\Old.cs", true)]
    [InlineData("src/legacy/**", @"C:\repo\src\modern\New.cs", false)]
    [InlineData("docs/*.md", @"C:\repo\docs\a.md", true)]
    [InlineData("docs/*.md", @"C:\repo\docs\sub\a.md", false)]
    [InlineData("data?.csv", @"C:\repo\data1.csv", true)]
    [InlineData("data?.csv", @"C:\repo\data12.csv", false)]
    public void ExcludeGlobs_MatchPathsAnywhereInTheTree(string glob, string path, bool excluded)
    {
        var policy = new FileAccessPolicy([glob]);

        var decision = policy.Check(path);

        Assert.Equal(excluded, !decision.IsAllowed);
        if (excluded)
        {
            Assert.Equal(FileAccessVerdict.ExcludedByGlob, decision.Verdict);
            Assert.Contains(glob, decision.Reason, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void BlankAndNegatedGlobs_AreIgnored()
    {
        var policy = new FileAccessPolicy(["", "  ", "# comment", "!keep.cs"]);

        Assert.True(policy.Check(@"C:\src\keep.cs").IsAllowed);
    }
}
