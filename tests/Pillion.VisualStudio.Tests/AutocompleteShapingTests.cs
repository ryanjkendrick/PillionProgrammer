using System.Linq;
using Pillion.VisualStudio.Core.Autocomplete;

namespace Pillion.VisualStudio.Tests;

public sealed class MultiLineDecisionTests
{
    [Theory]
    // Opens a block.
    [InlineData("csharp", "        if (x > 0) {", "", null, false, MultiLineReason.OpensBlock)]
    [InlineData("typescript", "function add(a: number, b: number) {", "", null, false, MultiLineReason.OpensBlock)]
    [InlineData("python", "    for item in items:", "", null, false, MultiLineReason.OpensBlock)]
    [InlineData("vb", "        If x > 0 Then", "", null, false, MultiLineReason.OpensBlock)]
    // Empty line inside a body.
    [InlineData("csharp", "        ", "", "    {", false, MultiLineReason.EmptyLineInBody)]
    [InlineData("csharp", "        ", "", "        var x = 1;", false, MultiLineReason.EmptyLineInBody)]
    [InlineData("python", "    ", "", "    total = 0", false, MultiLineReason.EmptyLineInBody)]
    // Follows a declaration signature.
    [InlineData("csharp", "    public static int Min(int[] numbers)", "", null, false, MultiLineReason.AfterDeclaration)]
    [InlineData("csharp", "", "", "    public static int Min(int[] numbers)", false, MultiLineReason.AfterDeclaration)]
    [InlineData("python", "def area(radius):", "", null, false, MultiLineReason.OpensBlock)]
    [InlineData("python", "", "", "def area(radius):", false, MultiLineReason.AfterDeclaration)]
    // Previous suggestion accepted at a line end.
    [InlineData("csharp", "        var total = Sum(numbers);", "", null, true, MultiLineReason.AfterAcceptAtLineEnd)]
    // Single-line.
    [InlineData("csharp", "        var total = ", "", null, false, MultiLineReason.Default)]
    [InlineData("csharp", "        Console.WriteLine(", ");", null, false, MultiLineReason.MidLine)]
    [InlineData("csharp", "        if (x > 0) {", "return;", null, true, MultiLineReason.MidLine)]
    [InlineData("csharp", "", "", "using System;", false, MultiLineReason.Default)]
    [InlineData("plaintext", "    ", "", "some text", false, MultiLineReason.Default)]
    public void Decide_FollowsTheCompletionShapingTable(string language, string before, string after, string? previous, bool acceptedAtLineEnd, MultiLineReason expected)
    {
        var reason = MultiLineDecision.Decide(new MultiLineInput
        {
            LanguageId = language,
            LineBeforeCaret = before,
            LineAfterCaret = after,
            PreviousNonBlankLine = previous,
            PreviousAcceptedAtLineEnd = acceptedAtLineEnd,
        });

        Assert.Equal(expected, reason);
    }

    [Fact]
    public void Degraded_IsAlwaysSingleLine()
    {
        var reason = MultiLineDecision.Decide(new MultiLineInput { LanguageId = "csharp", LineBeforeCaret = "    void M() {", Degraded = true, ForceMultiLine = true });

        Assert.Equal(MultiLineReason.Degraded, reason);
        Assert.False(MultiLineDecision.IsMultiLine(reason));
    }

    [Fact]
    public void ClosingBraceAfterTheCaret_StillAllowsMultiLine()
    {
        var reason = MultiLineDecision.Decide(new MultiLineInput { LanguageId = "csharp", LineBeforeCaret = "    void M() {", LineAfterCaret = "}" });

        Assert.Equal(MultiLineReason.OpensBlock, reason);
    }
}

public sealed class CompletionShaperTests
{
    [Fact]
    public void SingleLine_CutsAtTheFirstLineBreak()
    {
        var context = new ShapingContext { LanguageId = "csharp", LineBeforeCaret = "        var x = " };
        var shaper = new CompletionShaper(context);

        Assert.False(shaper.Append("Math.Min(a,"));
        Assert.True(shaper.Append(" b);\n        return x;"));
        Assert.Equal(CompletionStopReason.SingleLine, shaper.StopReason);
        Assert.Equal("Math.Min(a, b);", shaper.Complete());
    }

    [Fact]
    public void SingleLine_DropsATailThatRepeatsTheTextRightOfTheCaret()
    {
        var context = new ShapingContext { LanguageId = "csharp", LineBeforeCaret = "Console.WriteLine(", LineAfterCaret = ");" };

        Assert.Equal("total", CompletionShaper.Shape(context, "total);"));
    }

    [Fact]
    public void MultiLine_StopsAtTheEndOfTheEnclosingBlock_WhenTheClosingBraceIsAlreadyThere()
    {
        var context = new ShapingContext
        {
            LanguageId = "csharp",
            MultiLine = true,
            LineBeforeCaret = "    {",
            LinesBelow = ["    }", "}"],
        };

        var text = CompletionShaper.Shape(context, "\n        var x = 1;\n        return x;\n    }\n\n    void Other() { }\n");

        Assert.Equal("\n        var x = 1;\n        return x;", text);
    }

    [Fact]
    public void MultiLine_KeepsTheClosingBrace_WhenTheDocumentHasNone()
    {
        var context = new ShapingContext { LanguageId = "csharp", MultiLine = true, LineBeforeCaret = "    void M() {", LinesBelow = ["", "    void N()"] };
        var shaper = new CompletionShaper(context);
        shaper.Append("\n        Run();\n    }\n    void Extra() {}\n");

        Assert.Equal(CompletionStopReason.BlockEnd, shaper.StopReason);
        Assert.Equal("\n        Run();\n    }", shaper.Complete());
    }

    [Fact]
    public void MultiLine_BracesInStringsAndCommentsDoNotCount()
    {
        var context = new ShapingContext { LanguageId = "csharp", MultiLine = true, LineBeforeCaret = "    {", LinesBelow = ["    }"] };

        var text = CompletionShaper.Shape(context, "\n        var s = \"}\"; // }\n        var c = '}'; /* } */\n        return s;\n    }\n");

        Assert.Equal("\n        var s = \"}\"; // }\n        var c = '}'; /* } */\n        return s;", text);
    }

    [Fact]
    public void AfterDeclaration_StopsAfterTheBodyItOpened()
    {
        var context = new ShapingContext
        {
            LanguageId = "csharp",
            MultiLine = true,
            StopAfterFirstBlock = true,
            LineBeforeCaret = "    public static int Min(int[] numbers)",
            LinesBelow = ["}"],
        };

        var text = CompletionShaper.Shape(context, "\n    {\n        if (numbers.Length == 0) { return 0; }\n        return numbers.Min();\n    }\n\n    public static int Max()\n    {\n    }\n");

        Assert.Equal("\n    {\n        if (numbers.Length == 0) { return 0; }\n        return numbers.Min();\n    }", text);
    }

    [Fact]
    public void Python_StopsAtADedentOutOfTheBlock()
    {
        var context = new ShapingContext { LanguageId = "python", MultiLine = true, LineBeforeCaret = "def area(radius):" };
        var shaper = new CompletionShaper(context);
        shaper.Append("\n    pi = 3.14159\n\n    return pi * radius ** 2\n\ndef perimeter(radius):\n    return 0\n");

        Assert.Equal(CompletionStopReason.Dedent, shaper.StopReason);
        Assert.Equal("\n    pi = 3.14159\n\n    return pi * radius ** 2", shaper.Complete());
    }

    [Fact]
    public void Python_InsideABody_StopsWhenTheOutputLeavesTheCaretsIndentation()
    {
        var context = new ShapingContext { LanguageId = "python", MultiLine = true, LineBeforeCaret = "        " };

        var text = CompletionShaper.Shape(context, "total = 0\n        for x in xs:\n            total += x\n    return total\n");

        Assert.Equal("total = 0\n        for x in xs:\n            total += x", text);
    }

    [Fact]
    public void MultiLine_StopsAtThirtyLines()
    {
        var context = new ShapingContext { LanguageId = "plaintext", MultiLine = true };
        var shaper = new CompletionShaper(context);
        var input = string.Join("\n", Enumerable.Range(1, 50).Select(i => "line " + i)) + "\n";

        Assert.True(shaper.Append(input));
        Assert.Equal(CompletionStopReason.MaxLines, shaper.StopReason);
        Assert.Equal(30, shaper.Complete().Split('\n').Length);
    }

    [Fact]
    public void MultiLine_StopsAtTheFirstLineThatDuplicatesTheCodeBelow()
    {
        var context = new ShapingContext
        {
            LanguageId = "csharp",
            MultiLine = true,
            LineBeforeCaret = "        ",
            LinesBelow = ["        return total;", "    }"],
        };
        var shaper = new CompletionShaper(context);
        shaper.Append("total += x;\n        Log(total);\n        return total;\n    }\n");

        Assert.Equal(CompletionStopReason.Duplicate, shaper.StopReason);
        Assert.Equal("total += x;\n        Log(total);", shaper.Complete());
    }

    [Fact]
    public void MultiLine_WhenTheFirstLineDuplicatesTheLineBelow_NothingIsLeft()
    {
        var context = new ShapingContext { LanguageId = "csharp", MultiLine = true, LineBeforeCaret = "        var x = ", LinesBelow = ["        var x = 1;"] };

        Assert.Equal(string.Empty, CompletionShaper.Shape(context, "1;\n"));
    }

    [Fact]
    public void CarriageReturnsAreNormalisedAndTrailingBlankLinesDropped()
    {
        var context = new ShapingContext { LanguageId = "csharp", MultiLine = true, LineBeforeCaret = "        " };

        Assert.Equal("a();\r\n        b();".Replace("\r", string.Empty), CompletionShaper.Shape(context, "a();\r\n        b();\r\n\r\n"));
    }

    [Fact]
    public void PartialText_HoldsOnlyCompleteLines()
    {
        var shaper = new CompletionShaper(new ShapingContext { LanguageId = "csharp", MultiLine = true, LineBeforeCaret = "        " });

        shaper.Append("first();\n        sec");
        Assert.Equal("first();", shaper.PartialText);
        Assert.Equal(1, shaper.CompleteContentLines);
    }

    [Theory]
    [InlineData("\n    {\n        return 1;\n    }", "\n    {")]
    [InlineData("a();\nb();", "a();")]
    [InlineData("a();", null)]
    [InlineData("", null)]
    public void FirstLineVariant_IsTheTextUpToTheFirstLineWithContent(string text, string? expected)
    {
        Assert.Equal(expected, CompletionShaper.FirstLineVariant(text));
    }

    [Theory]
    [InlineData("```csharp")]
    [InlineData("```")]
    [InlineData("```typescript")]
    [InlineData("  ~~~python")]
    public void FenceLines_AreRecognised(string line) => Assert.True(CompletionShaper.IsFenceLine(line));

    [Theory]
    [InlineData("var s = \"```\";")]
    [InlineData("`x`")]
    [InlineData("```csharp {")]
    public void OtherLines_AreNotFences(string line) => Assert.False(CompletionShaper.IsFenceLine(line));

    [Fact]
    public void SingleLine_Gate1Fence_IsNeverShown()
    {
        // The 7B's ghost text at Gate 1: "```csharp" (tools/gate1/out/*/scenarios/autocomplete-strong-VS18.json).
        var shaper = new CompletionShaper(new ShapingContext { LanguageId = "csharp", LineBeforeCaret = "    " });

        shaper.Append("``");
        Assert.Null(shaper.DisplayText);
        shaper.Append("`csharp");
        Assert.Null(shaper.DisplayText);
        Assert.True(shaper.Append("\n"));

        Assert.Equal(CompletionStopReason.Fence, shaper.StopReason);
        Assert.Equal(string.Empty, shaper.Complete());
        Assert.Null(shaper.DisplayText);
    }

    [Fact]
    public void SingleLine_FenceTheStreamEndsOn_IsNeverShown()
    {
        var shaper = new CompletionShaper(new ShapingContext { LanguageId = "csharp", LineBeforeCaret = "    " });

        shaper.Append("```csharp");

        Assert.Equal(string.Empty, shaper.Complete());
    }

    [Fact]
    public void MultiLine_LeadingFenceIsSkipped_ALaterOneEndsTheSuggestion()
    {
        var context = new ShapingContext { LanguageId = "python", MultiLine = true, LineBeforeCaret = "    " };

        Assert.Equal("    return 1", CompletionShaper.Shape(context, "```python\n    return 1\n```\nThis returns one.\n"));
    }

    [Fact]
    public void DisplayText_SingleLine_IsTheTextSoFar()
    {
        var shaper = new CompletionShaper(new ShapingContext { LanguageId = "csharp", LineBeforeCaret = "        var x = " });

        Assert.Null(shaper.DisplayText);
        shaper.Append(" ");
        Assert.Null(shaper.DisplayText);
        shaper.Append("Math");
        Assert.Equal(" Math", shaper.DisplayText);
        shaper.Append(".Max(a, b);");
        Assert.Equal(" Math.Max(a, b);", shaper.DisplayText);
    }

    [Fact]
    public void DisplayText_SingleLine_HoldsWhatCouldBeTheTextRightOfTheCaret()
    {
        var shaper = new CompletionShaper(new ShapingContext { LanguageId = "csharp", LineBeforeCaret = "        Foo(", LineAfterCaret = ");" });

        shaper.Append("a, b)");
        Assert.Equal("a, b", shaper.DisplayText);
        shaper.Append(" + c");
        Assert.Equal("a, b) + c", shaper.DisplayText);
    }

    [Fact]
    public void DisplayText_MultiLine_IsTheCompleteLines()
    {
        var shaper = new CompletionShaper(new ShapingContext { LanguageId = "csharp", MultiLine = true, LineBeforeCaret = "        " });

        shaper.Append("first();\n        sec");
        Assert.Equal("first();", shaper.DisplayText);
    }
}
