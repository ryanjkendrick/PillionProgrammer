using Pillion.ModelGateway.Editing;

namespace Pillion.ModelGateway.Tests.Editing;

public sealed class EditBlockParserTests
{
    private const string TwoBlocks = """
        <<<<<<< SEARCH
            int x = 1;
        =======
            long x = 1;
        >>>>>>> REPLACE
        <<<<<<< SEARCH
            return x;
        =======
            return checked(x);
        >>>>>>> REPLACE
        Widened x and checked the result.
        """;

    [Fact]
    public void WellFormedBlocks_AreParsedInOrder_WithTheProseAsExplanation()
    {
        var result = EditBlockParser.Parse(TwoBlocks);

        Assert.Empty(result.Malformed);
        Assert.Null(result.Partial);
        Assert.Collection(
            result.Blocks,
            b =>
            {
                Assert.Equal(1, b.Index);
                Assert.Equal(["    int x = 1;"], b.SearchLines);
                Assert.Equal(["    long x = 1;"], b.ReplaceLines);
            },
            b =>
            {
                Assert.Equal(2, b.Index);
                Assert.Equal(["    return x;"], b.SearchLines);
                Assert.Equal(["    return checked(x);"], b.ReplaceLines);
            });
        Assert.Equal("Widened x and checked the result.", result.Explanation);
        Assert.False(result.HasFenceOutsideBlocks);
    }

    [Fact]
    public void CrlfAndLf_ParseTheSame()
    {
        var lf = EditBlockParser.Parse(TwoBlocks.Replace("\r\n", "\n", StringComparison.Ordinal));
        var crlf = EditBlockParser.Parse(TwoBlocks.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal));

        Assert.Equal(lf.Blocks.Count, crlf.Blocks.Count);
        for (var i = 0; i < lf.Blocks.Count; i++)
        {
            Assert.Equal(lf.Blocks[i].SearchLines, crlf.Blocks[i].SearchLines);
            Assert.Equal(lf.Blocks[i].ReplaceLines, crlf.Blocks[i].ReplaceLines);
        }

        Assert.DoesNotContain(crlf.Blocks.SelectMany(b => b.SearchLines.Concat(b.ReplaceLines)), l => l.Contains('\r', StringComparison.Ordinal));
    }

    [Fact]
    public void FencesAroundBlocks_AreIgnored_AndFencesInsideBlocks_AreContent()
    {
        var reply = """
            Here you go:
            ```csharp
            <<<<<<< SEARCH
            /// Example:
            /// ```
            /// Run();
            /// ```
            =======
            /// Example:
            /// ```csharp
            /// Run();
            /// ```
            >>>>>>> REPLACE
            ```
            """;

        var result = EditBlockParser.Parse(reply);

        var block = Assert.Single(result.Blocks);
        Assert.Equal(["/// Example:", "/// ```", "/// Run();", "/// ```"], block.SearchLines);
        Assert.Equal(["/// Example:", "/// ```csharp", "/// Run();", "/// ```"], block.ReplaceLines);
        Assert.Equal("Here you go:", result.Explanation);
        Assert.True(result.HasFenceOutsideBlocks);
        Assert.Empty(result.Malformed);
    }

    [Fact]
    public void BareFencesInsideABlock_DoNotEndIt()
    {
        var reply = "<<<<<<< SEARCH\n```\nold\n```\n=======\n```\nnew\n```\n>>>>>>> REPLACE\n";

        var block = Assert.Single(EditBlockParser.Parse(reply).Blocks);

        Assert.Equal(["```", "old", "```"], block.SearchLines);
        Assert.Equal(["```", "new", "```"], block.ReplaceLines);
    }

    [Theory]
    [InlineData("<<<<<<< SEARCH\na\n>>>>>>> REPLACE\n", "no '=======' line")]
    [InlineData("<<<<<<< SEARCH\na\n=======\nb\n", "not closed")]
    [InlineData("<<<<<<< SEARCH\na\n<<<<<<< SEARCH\nb\n=======\nc\n>>>>>>> REPLACE\n", "before the next '<<<<<<< SEARCH'")]
    [InlineData("<<<<<<< SEARCH\na\n=======\nb\n<<<<<<< SEARCH\nc\n=======\nd\n>>>>>>> REPLACE\n", "no '>>>>>>> REPLACE' line before")]
    [InlineData("a\n=======\nb\n>>>>>>> REPLACE\n", "without a '<<<<<<< SEARCH' line")]
    public void MalformedBlocks_AreReportedWithAReason(string reply, string reason)
    {
        var result = EditBlockParser.Parse(reply);

        var malformed = Assert.Single(result.Malformed);
        Assert.Contains(reason, malformed.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AMalformedBlock_DoesNotHideTheWellFormedOnesAfterIt()
    {
        var result = EditBlockParser.Parse("<<<<<<< SEARCH\na\n<<<<<<< SEARCH\nb\n=======\nc\n>>>>>>> REPLACE\n");

        Assert.Single(result.Malformed);
        var block = Assert.Single(result.Blocks);
        Assert.Equal(["b"], block.SearchLines);
        Assert.Equal(2, block.Index);
    }

    [Theory]
    [InlineData("<<<<<<<SEARCH", "=======", ">>>>>>>REPLACE")]
    [InlineData("<<<<<<< search", "=======", ">>>>>>> replace")]
    [InlineData("  <<<<<<< SEARCH  ", "  =======", ">>>>>>> REPLACE   ")]
    [InlineData("<<<<<", "=====", ">>>>>")]
    public void MarkerVariants_AreAccepted(string start, string divider, string end)
    {
        var block = Assert.Single(EditBlockParser.Parse($"{start}\nold\n{divider}\nnew\n{end}").Blocks);

        Assert.Equal(["old"], block.SearchLines);
        Assert.Equal(["new"], block.ReplaceLines);
    }

    [Fact]
    public void ADividerLineInsideReplace_IsContent()
    {
        var block = Assert.Single(EditBlockParser.Parse("<<<<<<< SEARCH\nTitle\n=======\nTitle\n=======\n>>>>>>> REPLACE").Blocks);

        Assert.Equal(["Title", "======="], block.ReplaceLines);
    }

    [Fact]
    public void EmptyReplace_MeansDelete()
    {
        var block = Assert.Single(EditBlockParser.Parse("<<<<<<< SEARCH\nConsole.WriteLine();\n=======\n>>>>>>> REPLACE").Blocks);

        Assert.Empty(block.ReplaceLines);
    }

    [Fact]
    public void LineNumbersCopiedFromThePrompt_AreStripped_WhenEveryLineHasThem()
    {
        var reply = "<<<<<<< SEARCH\n 9|     int x = 1;\n10|     return x;\n=======\n 9|     long x = 1;\n10|     return x;\n>>>>>>> REPLACE";

        var block = Assert.Single(EditBlockParser.Parse(reply).Blocks);

        Assert.Equal(["    int x = 1;", "    return x;"], block.SearchLines);
        Assert.Equal(["    long x = 1;", "    return x;"], block.ReplaceLines);
    }

    [Fact]
    public void CodeThatOnlySometimesLooksNumbered_IsLeftAlone()
    {
        var reply = "<<<<<<< SEARCH\n1| a\nb\n=======\nc\n>>>>>>> REPLACE";

        var block = Assert.Single(EditBlockParser.Parse(reply).Blocks);

        Assert.Equal(["1| a", "b"], block.SearchLines);
    }

    [Fact]
    public void CodeWithoutBlocks_IsReportedAsAFenceOutsideBlocks()
    {
        var result = EditBlockParser.Parse("```csharp\nint x = 2;\n```\n");

        Assert.Empty(result.Blocks);
        Assert.Empty(result.Malformed);
        Assert.True(result.HasFenceOutsideBlocks);
        Assert.Equal("int x = 2;", result.Explanation);
    }

    [Fact]
    public void WhileStreaming_AnOpenReplace_IsPartial_WithItsCompleteLinesOnly()
    {
        var result = EditBlockParser.Parse("<<<<<<< SEARCH\na\n=======\nb1\nb2\nb3-unfinish", isComplete: false);

        Assert.Empty(result.Malformed);
        Assert.Empty(result.Blocks);
        Assert.NotNull(result.Partial);
        Assert.Equal(["a"], result.Partial.SearchLines);
        Assert.Equal(["b1", "b2"], result.Partial.ReplaceLines);
    }

    [Fact]
    public void WhileStreaming_AnOpenSearch_IsNeitherPartialNorMalformed()
    {
        var result = EditBlockParser.Parse("<<<<<<< SEARCH\na\n", isComplete: false);

        Assert.Empty(result.Malformed);
        Assert.Null(result.Partial);
    }
}

public sealed class FencedCodeParserTests
{
    [Fact]
    public void ExtractsTheCode_AndTheProse()
    {
        var (blocks, prose) = FencedCodeParser.Parse("```csharp\n[Fact]\npublic void T() { }\n```\nPut it in CalcTests.cs.");

        var block = Assert.Single(blocks);
        Assert.Equal("csharp", block.Info);
        Assert.Equal("[Fact]\npublic void T() { }", block.Code);
        Assert.True(block.IsClosed);
        Assert.Equal("Put it in CalcTests.cs.", prose);
    }

    [Fact]
    public void ALongerFence_CanContainAShorterOne()
    {
        var (blocks, _) = FencedCodeParser.Parse("````\na\n```\nb\n```\n````\n");

        Assert.Equal("a\n```\nb\n```", Assert.Single(blocks).Code);
    }

    [Fact]
    public void WhileStreaming_AnOpenFence_IsReportedOpen_WithCompleteLinesOnly()
    {
        var (blocks, _) = FencedCodeParser.Parse("```\nline1\nline2\nli", isComplete: false);

        var block = Assert.Single(blocks);
        Assert.False(block.IsClosed);
        Assert.Equal("line1\nline2", block.Code);
    }
}
