namespace Pillion.ModelGateway.Tests.Autocomplete;

/// <summary>
/// The gateway's prose and fence handling (Gate 1 open issues 1 and 2). The samples are the ghost text the Gate 1 typing
/// scenario recorded (<c>firstNonMatchingProposals</c> in <c>tools/gate1/out/*/ide-VS*/scenarios/autocomplete-*.json</c>,
/// runs of 2026-10-10 and 2026-10-11) and the replies of the prompt-quality measurement.
/// </summary>
public sealed class CompletionContentFilterTests
{
    private static readonly string[] FencedPieces = ["``", "`csharp\n", "        Console", ".WriteLine(1);\n", "        return;"];

    /// <summary>Every distinct prose opening the 1.5B model showed as ghost text at Gate 1.</summary>
    public static TheoryData<string> Gate1ProseSamples { get; } = new()
    {
        "It looks like you're encountering an issue with a file named `TypeFast.cs` located in the specified directory. The recent edits indicate that the file has been modified at three different times, but the exact changes are not provided in the snippet. However, based on the context, it seems like there might be a syntax",
        "It looks like you're encountering an issue with the `TypeFast.cs` file, specifically related to the closing curly braces (`}`). The error message indicates that there are multiple closing curly braces in a row, which can cause syntax errors in your code.",
        "It looks like you're encountering an issue with the code in `TypeFast.cs` at line 38. The error message indicates that there is a missing closing brace. To resolve this issue, you need to ensure that every opening brace `{` has a corresponding closing brace `}`.",
        "It looks like you're encountering an issue with the code in `TypeFast.cs` at line 40, which is closing a block. The error message indicates that the block is not properly closed, which could be causing the IDE to have trouble parsing the code.",
        "It looks like you're encountering an issue with the code in the file `TypeFast.cs`. The recent edits indicate that the code is incomplete or contains syntax errors. To provide a more accurate response, I would need to see the complete code around the lines you mentioned. However, based on the information provided, here are",
        "It looks like you're working on a C# project and encountering an issue with the `TypeFast.cs` file. The error message indicates that there is a mismatch in the closing curly braces (`}`) in the file. This can happen for several reasons, such as a syntax error or a misplaced closing brace.",
        "It looks like you're working on a C# project and have a file named `TypeFast.cs`. The file appears to be part of a larger project that deals with type-related operations. However, the code snippet provided is incomplete and lacks context.",
        "It looks like you're working on a C# project, and you're encountering an issue with the `TypeFast.cs` file. The error message indicates that there are unmatched closing curly braces (`}`) in the file. This can happen for several reasons, such as missing braces in control structures, incorrect indentation, or",
        "It looks like you're working on a file named `WarmTypeFast.cs` and have some recent edits to it. The code snippet you provided is incomplete, but it seems to be related to a type system or a similar concept in programming. Here's a more complete example of what the file might look like:",
        "It looks like you're working on a type-safe collection in C#. The code snippet you provided is a part of a larger file, and it seems to be related to a class named `TypeFast`.",
        "It looks like you're working on a type-safe library called `TypeFast` and you've encountered a problem with the code snippet you provided.",
        "It looks like you're working with a C# codebase, specifically in the context of a type-safe library or framework.",
        "It looks like you're working with a C# file named `TypeFast.cs` and encountering some issues with the code. The recent edits indicate that the code is incomplete or contains syntax errors.",
        "It looks like you're working with a C# file named `WarmTypeFast.cs`, and there are recent edits to the file. The recent edits are located at the specified paths. Here's a brief overview of the file and its contents:",
        "It looks like you're working with a code file named `TypeFast.cs` and encountering issues with the closing curly braces (`}`).",
        "It looks like you're working with a code file named `WarmTypeFast.cs`, and there are recent edits to it. The recent edits are located at the specified paths. Here's a brief overview of what the code might look like and some general information about the file:",

        // From the prompt-quality measurement (empty suffix, Gate 1 header), both models.
        " It looks like you're working on a checkout system for an e-commerce application. The `Checkout` class is responsible for",
        "It looks like you're working on a Python script named `stats.py` that involves calculating the mean of a list of values.",

        // Other usual chat openings.
        "Here's the completed method:",
        "Sure, here is the implementation of the method.",
        "To fix this, you need to close the brace.",
        "This code defines a class that stores the orders.",
    };

    /// <summary>Completions that are code (or legitimately start like a word) and must pass untouched.</summary>
    public static TheoryData<string> CodeSamples { get; } = new()
    {
        "_orders.Add(order);",
        "0m;",
        "init; }",
        "p.Age >= 18",
        "ArgumentNullException(nameof(order));",
        "value * 2;",
        "await readFile(path, 'utf-8');",
        "name: string;",
        " values.reduce((a, b) => a + b, 0);",
        "Error(`HTTP error! status: ${response.status}`);",
        "u.name",
        "sum(values) / len(values)",
        "self.balance = balance",
        "x for x in values if x % 2 == 0]",
        "lines = f.readlines()",
        "        int max = values[0];\n        for (int i = 1; i < values.Length; i++)",
        "Console.WriteLine(\"Hello\");",
        "Math.Max(a, b);",
        "List<int> numbers = new();",
        "Task.CompletedTask;",
        "True",
        "None",
        "Dim total As Integer = 0",
        "If x Then Return",
        "End Sub",
        "Return result",
        "return a if b else c",
        "SELECT name FROM users WHERE id = 1",
        "Assert.Equal(expected, actual);",
        "\n    {\n        return numbers.Min();\n    }",
        "throw new InvalidOperationException(\"The order is empty.\");",
    };

    [Theory]
    [MemberData(nameof(Gate1ProseSamples))]
    public void Prose_IsRecognised_FromItsOpeningWords(string sample)
    {
        Assert.Equal(CompletionContentVerdict.Prose, CompletionContentFilter.ClassifyStart(sample, lineComplete: true));

        // As a stream: decided after a few words, long before the sentence ends.
        var filter = new CompletionContentFilter("public static class TypeFast\n{\n    ", "csharp");
        var shown = string.Empty;
        var consumed = 0;
        foreach (var piece in Pieces(sample))
        {
            shown += filter.Push(piece);
            consumed += piece.Length;
            if (filter.IsDropped)
            {
                break;
            }
        }

        Assert.True(filter.IsDropped);
        Assert.Equal(CompletionContentVerdict.Prose, filter.Verdict);
        Assert.Equal(string.Empty, shown + filter.Flush());
        Assert.True(consumed <= 40, $"decided after {consumed} characters");
    }

    [Theory]
    [MemberData(nameof(CodeSamples))]
    public void Code_PassesUnchanged(string sample)
    {
        Assert.Equal(CompletionContentVerdict.Code, CompletionContentFilter.ClassifyStart(sample, lineComplete: true));

        var filter = new CompletionContentFilter("    var x = ", "csharp");
        var shown = string.Concat(Pieces(sample).Select(filter.Push)) + filter.Flush();

        Assert.False(filter.IsDropped);
        Assert.False(filter.FenceStripped);
        Assert.Equal(sample, shown);
    }

    [Theory]
    [InlineData("Math.Max(a, b);", 6)]
    [InlineData("_orders.Add(order);", 1)]
    [InlineData("0m;", 1)]
    [InlineData("numbers.Min();", 1)]
    public void Code_IsReleasedWithinAFewCharacters(string sample, int heldAtMost)
    {
        var filter = new CompletionContentFilter("    return ", "csharp");
        var consumed = 0;
        foreach (var piece in sample.Select(c => c.ToString()))
        {
            consumed++;
            if (filter.Push(piece).Length > 0)
            {
                break;
            }
        }

        Assert.True(consumed <= heldAtMost, $"held for {consumed} characters");
    }

    [Theory]
    [InlineData("```csharp")]
    [InlineData("```")]
    [InlineData("  ```typescript")]
    [InlineData("~~~python")]
    [InlineData("```c++")]
    [InlineData("```c#")]
    public void FenceLines_AreRecognised(string line) => Assert.True(CompletionContentFilter.IsFenceLine(line));

    [Theory]
    [InlineData("var s = ```;")]
    [InlineData("`code`")]
    [InlineData("``")]
    [InlineData("```csharp { }")]
    public void OtherLines_AreNotFences(string line) => Assert.False(CompletionContentFilter.IsFenceLine(line));

    [Fact]
    public void Gate1Fence_Alone_ShowsNothing()
    {
        // The 7B's Gate 1 ghost text: "```csharp" and nothing else (the single-line newline stop ended it there).
        var filter = new CompletionContentFilter("public static class TypeFast\n{\n    ", "csharp");

        var shown = filter.Push("```") + filter.Push("csharp") + filter.Flush();

        Assert.Equal(string.Empty, shown);
        Assert.True(filter.FenceStripped);
        Assert.False(filter.IsDropped);
    }

    [Fact]
    public void LeadingFence_AtABlankCaretLine_IsStripped_AndTheCodeKept()
    {
        var filter = new CompletionContentFilter("    void Run()\n    {\n        ", "csharp");

        var shown = string.Concat(FencedPieces.Select(filter.Push)) + filter.Flush();

        Assert.True(filter.FenceStripped);
        Assert.Equal("Console.WriteLine(1);\n        return;", shown);
    }

    [Fact]
    public void LeadingFence_RestatingTheCaretLine_KeepsOnlyTheContinuation()
    {
        // From the measurement (7B, empty suffix): the fenced code quotes the caret line from its start.
        var filter = new CompletionContentFilter("    public static int Twice(int value)\n    {\n        return ", "csharp");

        var shown = filter.Push(" ```csharp\n        return value * 2;\n    }\n}") + filter.Flush();

        Assert.Equal("value * 2;\n    }\n}", shown);
    }

    [Fact]
    public void LeadingFence_ThenCodeThatDoesNotContinueTheCaretLine_IsDropped()
    {
        var filter = new CompletionContentFilter("        var total = ", "csharp");

        var shown = filter.Push("```csharp\nusing System;\n\npublic class Cart\n") + filter.Flush();

        Assert.Equal(string.Empty, shown);
        Assert.Equal(CompletionContentVerdict.Restated, filter.Verdict);
    }

    [Fact]
    public void LeadingFence_ThenTheHeaderRestated_IsDropped()
    {
        // From the measurement (1.5B, empty suffix, Gate 1 header): "```python\n# File: store.py\n# Open files: ...".
        const string Prompt = "# file: store.py\n# see: Helpers.py, Models.py\nimport json\n\n\ndef load(path):\n";
        var filter = new CompletionContentFilter("import json\n\n\ndef load(path):\n", "python", Prompt);

        var shown = filter.Push(" ```python\n# file: store.py\n# see: Helpers.py, Models.py\n") + filter.Flush();

        Assert.Equal(string.Empty, shown);
        Assert.Equal(CompletionContentVerdict.Restated, filter.Verdict);
    }

    [Fact]
    public void LeadingFence_ThenProse_IsDropped()
    {
        var filter = new CompletionContentFilter("    ", "csharp");

        var shown = filter.Push("```\nIt looks like you're missing a brace.") + filter.Flush();

        Assert.Equal(string.Empty, shown);
        Assert.Equal(CompletionContentVerdict.Prose, filter.Verdict);
    }

    [Theory]
    [InlineData("    // Returns the ", "csharp")]
    [InlineData("    /// <summary>Gets the ", "csharp")]
    [InlineData("    # Compute the ", "python")]
    [InlineData("    x = 1  # the ", "python")]
    [InlineData("    var message = \"The ", "csharp")]
    [InlineData("    print('It ", "python")]
    [InlineData("/*\n * It ", "csharp")]
    [InlineData("def f():\n    \"\"\"Return the ", "python")]
    [InlineData("    ' Gets the ", "vb")]
    [InlineData("# Title\n\nIt ", "markdown")]
    [InlineData("Some notes ", "plaintext")]
    public void Prose_IsKept_WhereItBelongs(string prefix, string languageId)
    {
        Assert.True(CompletionContentFilter.IsProseAllowedAt(prefix, languageId));

        var filter = new CompletionContentFilter(prefix, languageId);
        var shown = filter.Push("It looks like you're") + filter.Push(" here.") + filter.Flush();

        Assert.Equal("It looks like you're here.", shown);
    }

    [Theory]
    [InlineData("    return ", "csharp")]
    [InlineData("    var url = \"http://x\"; ", "csharp")]
    [InlineData("public class A\n{\n    ", "csharp")]
    [InlineData("def f():\n    ", "python")]
    public void Prose_IsFiltered_InCode(string prefix, string languageId) =>
        Assert.False(CompletionContentFilter.IsProseAllowedAt(prefix, languageId));

    [Fact]
    public void ShortCompletion_EndingTheStreamUndecided_IsCode()
    {
        var filter = new CompletionContentFilter("    return ", "csharp");

        var shown = filter.Push("Total") + filter.Flush();

        Assert.Equal("Total", shown);
        Assert.Equal(CompletionContentVerdict.Code, filter.Verdict);
    }

    /// <summary>Splits a sample the way a model streams it: a few characters at a time, breaking after spaces.</summary>
    private static IEnumerable<string> Pieces(string text)
    {
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == ' ' || i - start >= 4)
            {
                yield return text[start..(i + 1)];
                start = i + 1;
            }
        }

        if (start < text.Length)
        {
            yield return text[start..];
        }
    }
}
