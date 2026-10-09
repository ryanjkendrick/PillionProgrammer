using Pillion.ModelGateway.Editing;

namespace Pillion.ModelGateway.Tests.Editing;

public sealed class EditBlockApplierTests
{
    private const string Code = "public int Add(int a, int b)\n{\n    var sum = a + b;   \n    return sum;\n}\n";

    /// <summary>The matcher table: SEARCH text, expected kind, expected first line (or -1), expected occurrences.</summary>
    public static TheoryData<string, EditMatchKind, int, int> Table => new()
    {
        { "    var sum = a + b;   \n    return sum;", EditMatchKind.Exact, 2, 1 },
        { "    var sum = a + b;\n    return sum;", EditMatchKind.TrailingWhitespace, 2, 1 },
        { "var sum = a + b;\nreturn sum;", EditMatchKind.Indentation, 2, 1 },
        { "\t\tvar sum = a + b;\n\t\treturn sum;  ", EditMatchKind.Indentation, 2, 1 },
        { "    var total = a + b;", EditMatchKind.NotFound, -1, 0 },
        { "{", EditMatchKind.Exact, 1, 1 },
        { "    return sum;\n}\n\n", EditMatchKind.Exact, 3, 1 },
    };

    [Theory]
    [MemberData(nameof(Table))]
    public void Find_UsesTheStrictestLevelThatMatches(string search, EditMatchKind kind, int line, int occurrences)
    {
        var match = EditBlockApplier.Find(EditText.SplitLines(Code), search.Split('\n'), ["x"]);

        Assert.Equal(kind, match.Kind);
        Assert.Equal(occurrences, match.Occurrences);
        if (line >= 0)
        {
            Assert.Equal(line, match.Line);
        }
    }

    [Fact]
    public void Find_ReportsAmbiguity_WithTheNumberOfPlaces()
    {
        var lines = EditText.SplitLines("a();\nb();\na();\n");

        var match = EditBlockApplier.Find(lines, ["a();"], ["c();"]);

        Assert.Equal(EditMatchKind.Ambiguous, match.Kind);
        Assert.Equal(2, match.Occurrences);
        Assert.False(match.IsMatch);
    }

    [Fact]
    public void Find_ReportsAmbiguity_AtTheLooserLevel_WhenTheStricterFindsNothing()
    {
        var lines = EditText.SplitLines("    a();\n        a();\n");

        var match = EditBlockApplier.Find(lines, ["a();"], ["b();"]);

        Assert.Equal(EditMatchKind.Ambiguous, match.Kind);
        Assert.Equal(2, match.Occurrences);
    }

    [Fact]
    public void Indentation_ReindentsTheReplacement_ToTheCode()
    {
        var lines = EditText.SplitLines("class C\n{\n        void M()\n        {\n        }\n}\n");

        var match = EditBlockApplier.Find(lines, ["void M()", "{", "}"], ["void M()", "{", "    Run();", "}", "", "  // less"]);

        Assert.Equal(EditMatchKind.Indentation, match.Kind);
        Assert.Equal(
            ["        void M()", "        {", "            Run();", "        }", string.Empty, "          // less"],
            match.Replacement);
    }

    [Fact]
    public void Indentation_CanAlsoRemoveIndentation()
    {
        var lines = EditText.SplitLines("x();\n");

        var match = EditBlockApplier.Find(lines, ["        x();"], ["        y();", "            z();"]);

        Assert.Equal(EditMatchKind.Indentation, match.Kind);
        Assert.Equal(["y();", "    z();"], match.Replacement);
    }

    [Fact]
    public void BlankEdgeLines_AreDropped_WhenTheBlockDoesNotMatchWithThem()
    {
        var lines = EditText.SplitLines("a();\nb();\n");

        var match = EditBlockApplier.Find(lines, [string.Empty, "a();", string.Empty, string.Empty], [string.Empty, "c();", string.Empty]);

        Assert.True(match.IsMatch);
        Assert.Equal(0, match.Line);
        Assert.Equal(1, match.LineCount);
        Assert.Equal(["c();"], match.Replacement);
    }

    [Fact]
    public void EmptySearch_IsRejected_UnlessTheCodeIsBlank()
    {
        Assert.Equal(EditMatchKind.EmptySearch, EditBlockApplier.Find(EditText.SplitLines("a();"), [string.Empty], ["b();"]).Kind);

        var blank = EditBlockApplier.Find(EditText.SplitLines("\n"), [], ["b();"]);
        Assert.True(blank.IsMatch);
        Assert.Equal(["b();"], blank.Replacement);
    }

    [Fact]
    public void Apply_RunsBlocksInOrder_EachAgainstThePreviousResult()
    {
        var blocks = new[]
        {
            new EditBlock(1, ["    var sum = a + b;   "], ["    var sum = checked(a + b);"]),
            new EditBlock(2, ["    var sum = checked(a + b);", "    return sum;"], ["    return checked(a + b);"]),
        };

        var result = EditBlockApplier.Apply(Code.Replace("\n", "\r\n", StringComparison.Ordinal), blocks);

        Assert.True(result.Succeeded);
        Assert.Equal("public int Add(int a, int b)\n{\n    return checked(a + b);\n}\n", result.Text);
        Assert.Equal(3, result.LastEditEndLine);
        Assert.Equal([EditMatchKind.Exact, EditMatchKind.Exact], result.Matches.Select(m => m.Kind));
    }

    [Fact]
    public void Apply_ReportsEveryFailure_AndAppliesTheRest()
    {
        var blocks = new[]
        {
            new EditBlock(1, ["    return nothing;"], ["x"]),
            new EditBlock(2, ["{"], ["{ // start"]),
            new EditBlock(3, ["}"], ["} // end"]),
        };

        var result = EditBlockApplier.Apply("{\n}\n{\n}", blocks);

        Assert.False(result.Succeeded);
        Assert.Collection(
            result.Failures,
            f =>
            {
                Assert.Equal(1, f.Block.Index);
                Assert.Equal(EditMatchKind.NotFound, f.Match.Kind);
                Assert.Contains("do not match", f.Describe(), StringComparison.Ordinal);
            },
            f =>
            {
                Assert.Equal(2, f.Block.Index);
                Assert.Equal(EditMatchKind.Ambiguous, f.Match.Kind);
                Assert.Contains("match 2 places", f.Describe(), StringComparison.Ordinal);
            },
            f => Assert.Equal(3, f.Block.Index));
    }

    [Fact]
    public void Apply_KeepsATextWithoutATrailingNewline_WithoutOne()
    {
        var result = EditBlockApplier.Apply("a\nb", [new EditBlock(1, ["b"], ["c"])]);

        Assert.Equal("a\nc", result.Text);
    }
}

public sealed class EditTextTests
{
    [Theory]
    [InlineData("a\r\nb", "\r\n")]
    [InlineData("a\nb", "\n")]
    public void DetectNewline_UsesTheFirstTextWithALineBreak(string text, string expected)
    {
        Assert.Equal(expected, EditText.DetectNewline(string.Empty, null, text, "x\ny"));
    }

    [Fact]
    public void Fence_IsLongerThanAnyBacktickRunInTheContent()
    {
        Assert.Equal("```", EditText.Fence("int x;"));
        Assert.Equal("````", EditText.Fence("/// ```"));
        Assert.Equal("``````", EditText.Fence("`````"));
    }

    [Fact]
    public void FindIntroducedPlaceholders_IgnoresTokensAlreadyInTheCode()
    {
        var found = EditText.FindIntroducedPlaceholders(
            "var a = \"<redacted:aws-access-key>\"; var b = \"<redacted:github-token#2>\";",
            "var a = \"<redacted:aws-access-key>\";",
            null);

        Assert.Equal(["<redacted:github-token#2>"], found);
    }

    [Fact]
    public void NumberLines_PadsTheNumbers_AndDropsTheEmptyLastLine()
    {
        Assert.Equal(" 9| a\n10|     b", EditText.NumberLines("a\n    b\n", 9));
    }
}
