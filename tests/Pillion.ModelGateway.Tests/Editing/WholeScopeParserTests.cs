using Pillion.Contracts;
using Pillion.ModelGateway.Editing;

namespace Pillion.ModelGateway.Tests.Editing;

public sealed class WholeScopeParserTests
{
    private const string Scope = "    public int Add(int a, int b)\n    {\n        return a + b;\n    }\n";
    private const string Rewritten = "    public int Add(int a, int b)\n    {\n        return checked(a + b);\n    }\n";

    [Theory]
    [InlineData("", 0)]
    [InlineData("a", 1)]
    [InlineData("a\n", 1)]
    [InlineData("a\nb", 2)]
    [InlineData("a\nb\n", 2)]
    [InlineData("a\r\nb\r\n", 2)]
    [InlineData("\n", 1)]
    [InlineData("a\n\n", 2)]
    public void CountLines_CountsTheLinesAnEditorShows(string text, int expected)
    {
        Assert.Equal(expected, WholeScopeParser.CountLines(text));
    }

    [Fact]
    public void PlainCode_IsTheResult()
    {
        var reply = WholeScopeParser.Parse(Rewritten, Scope);

        Assert.Equal(Rewritten, reply.Code);
        Assert.Equal(string.Empty, reply.Explanation);
        Assert.Null(reply.Problem);
    }

    [Fact]
    public void AFencedBlock_IsUnwrapped_AndTheProseBecomesTheExplanation()
    {
        var reply = WholeScopeParser.Parse("Here is the change:\n```csharp\n" + Rewritten + "```\nIt now throws on overflow.", Scope);

        Assert.Equal(Rewritten, reply.Code);
        Assert.Equal("Here is the change:\nIt now throws on overflow.", reply.Explanation);
    }

    [Fact]
    public void AFenceOpenedButNeverClosed_StillGivesTheCode()
    {
        var reply = WholeScopeParser.Parse("```csharp\n" + Rewritten, Scope);

        Assert.Equal(Rewritten, reply.Code);
    }

    [Fact]
    public void LineNumbers_OnEveryLine_AreStripped()
    {
        var numbered = EditText.NumberLines(Rewritten, 9);

        var reply = WholeScopeParser.Parse(numbered + "\n", Scope);

        Assert.Equal(Rewritten, reply.Code);
    }

    [Fact]
    public void LineNumbers_InsideAFence_AreStripped()
    {
        var reply = WholeScopeParser.Parse("```\n" + EditText.NumberLines(Rewritten, 9) + "\n```", Scope);

        Assert.Equal(Rewritten, reply.Code);
    }

    [Fact]
    public void LineNumbers_OnSomeLinesOnly_AreKept()
    {
        // A line of the code that happens to start like a number is code; only a reply numbered throughout is stripped.
        var reply = WholeScopeParser.Parse("    var x = 1;\n12| not a prefix of every line\n", "    var x = 1;\n");

        Assert.Equal("    var x = 1;\n12| not a prefix of every line\n", reply.Code);
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void LineEndings_OfTheReply_AreReadAsLf(string replyNewline)
    {
        var reply = WholeScopeParser.Parse(Rewritten.Replace("\n", replyNewline, StringComparison.Ordinal), Scope);

        Assert.Equal(Rewritten, reply.Code);
        var fenced = WholeScopeParser.Parse("```csharp" + replyNewline + Rewritten.Replace("\n", replyNewline, StringComparison.Ordinal) + "```" + replyNewline, Scope);
        Assert.Equal(Rewritten, fenced.Code);
    }

    [Fact]
    public void TheTrailingNewline_FollowsTheCodeBeingReplaced()
    {
        Assert.Equal(Rewritten, WholeScopeParser.Parse(Rewritten.TrimEnd('\n'), Scope).Code);
        Assert.Equal(Rewritten.TrimEnd('\n'), WholeScopeParser.Parse(Rewritten + "\n\n", Scope.TrimEnd('\n')).Code);
    }

    [Fact]
    public void BlankLinesAroundTheCode_AreDropped_ButIndentationIsKept()
    {
        var reply = WholeScopeParser.Parse("\n\n" + Rewritten + "\n\n", Scope);

        Assert.Equal(Rewritten, reply.Code);
        Assert.StartsWith("    public", reply.Code, StringComparison.Ordinal);
    }

    [Fact]
    public void CodeThatHasFencesOfItsOwn_IsNotUnwrapped()
    {
        var markdown = "```csharp\nvar x = 1;\n```\n";

        var reply = WholeScopeParser.Parse("```csharp\nvar x = 2;\n```\n", markdown);

        Assert.Equal("```csharp\nvar x = 2;\n```\n", reply.Code);
    }

    [Fact]
    public void ProseThenCode_InAScopeWithFences_IsTakenAsItIs()
    {
        // A docstring that holds a fenced example: the fence lines belong to the code, so a reply is not unwrapped.
        var scope = "def f():\n    \"\"\"\n    ```python\n    run()\n    ```\n    \"\"\"\n";

        var reply = WholeScopeParser.Parse(scope.Replace("run", "go", StringComparison.Ordinal), scope);

        Assert.Equal(scope.Replace("run", "go", StringComparison.Ordinal), reply.Code);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  \n\n ")]
    [InlineData("```csharp\n```")]
    [InlineData("```csharp\n   \n```\nNothing to change.")]
    public void AnEmptyReply_HasNoCode(string text)
    {
        var reply = WholeScopeParser.Parse(text, Scope);

        Assert.Null(reply.Code);
        Assert.NotNull(reply.Problem);
    }

    [Fact]
    public void AReplyInBlockFormat_IsNotCode()
    {
        var reply = WholeScopeParser.Parse("<<<<<<< SEARCH\n        return a + b;\n=======\n        return checked(a + b);\n>>>>>>> REPLACE\n", Scope);

        Assert.Null(reply.Code);
        Assert.Contains("edit-block format", reply.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Preview_GivesTheCompleteLinesSoFar_WithoutTheUnfinishedOne()
    {
        Assert.Null(WholeScopeParser.Preview("    public int Ad", Scope, out _));
        Assert.Equal("    public int Add(int a, int b)", WholeScopeParser.Preview("    public int Add(int a, int b)\n    {", Scope, out _));
        Assert.Equal("    public int Add(int a, int b)\n    {", WholeScopeParser.Preview("    public int Add(int a, int b)\n    {\n", Scope, out _));
    }

    [Fact]
    public void Preview_SkipsAnOpeningFence_AndStopsAtTheClosingOne()
    {
        Assert.Null(WholeScopeParser.Preview("```csharp\n", Scope, out _));
        Assert.Equal("int x;", WholeScopeParser.Preview("```csharp\nint x;\n", Scope, out _));
        Assert.Equal("int x;", WholeScopeParser.Preview("```csharp\nint x;\n```\nDone.\n", Scope, out _));
    }

    [Fact]
    public void Preview_StripsLineNumbers_AndHoldsBackTrailingBlankLines()
    {
        Assert.Equal("int x;", WholeScopeParser.Preview("9| int x;\n10| \n", Scope, out _));
        Assert.Equal("int x;\n\nint y;", WholeScopeParser.Preview("9| int x;\n10|\n11| int y;\n12|\n", Scope, out _));
    }

    [Fact]
    public void Preview_OfABlockFormatReply_Stops()
    {
        var preview = WholeScopeParser.Preview("<<<<<<< SEARCH\n        return a + b;\n", Scope, out var stop);

        Assert.Null(preview);
        Assert.True(stop);
    }

    [Fact]
    public void WholeScopePrompt_AsksForPlainCode()
    {
        var system = InlineEditTemplates.Default.SystemPrompt(InlineEditCommand.None, "C#", wholeScope: true);

        Assert.Contains("complete rewritten code", system, StringComparison.Ordinal);
        Assert.Contains("no Markdown fences", system, StringComparison.Ordinal);
        Assert.DoesNotContain("SEARCH", system, StringComparison.Ordinal);
    }
}
