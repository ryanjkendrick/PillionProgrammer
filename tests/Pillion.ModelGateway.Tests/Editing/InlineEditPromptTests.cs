using Pillion.Contracts;
using Pillion.ModelGateway.Editing;

namespace Pillion.ModelGateway.Tests.Editing;

public sealed class InlineEditPromptTests
{
    private static readonly InlineEditPromptBuilder Builder = new();

    [Fact]
    public void TheShippedTemplates_Load_AndDocumentTheBlockFormat()
    {
        var system = InlineEditTemplates.Default.SystemPrompt(InlineEditCommand.None, "C#");

        Assert.Contains("<<<<<<< SEARCH", system, StringComparison.Ordinal);
        Assert.Contains("=======", system, StringComparison.Ordinal);
        Assert.Contains(">>>>>>> REPLACE", system, StringComparison.Ordinal);
        Assert.Contains("C# file", system, StringComparison.Ordinal);
        Assert.Contains("<redacted:kind>", system, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEdit_ShowsContextAndNumberedCode_InStableOrder()
    {
        var messages = Builder.Build(Input(InlineEditCommand.None, "extract the validation into a method"));

        Assert.Equal(2, messages.Count);
        Assert.Equal(ChatRole.System, messages[0].Role);
        Assert.Equal(ChatRole.User, messages[1].Role);
        var user = messages[1].Content;
        Assert.StartsWith("Language: C#\nFile: Calc.cs", user, StringComparison.Ordinal);
        Assert.Contains("lines 11 to 12 of the file", user, StringComparison.Ordinal);
        Assert.Contains("11|     public int Add(int a, int b)\n12|         => a + b;", user, StringComparison.Ordinal);
        Assert.Contains("```csharp\npublic class Calc\n{\n```", user, StringComparison.Ordinal);
        Assert.Contains("Instruction: extract the validation into a method", user, StringComparison.Ordinal);
        Assert.True(
            user.IndexOf("Code before", StringComparison.Ordinal) < user.IndexOf("Code to edit", StringComparison.Ordinal) &&
            user.IndexOf("Code to edit", StringComparison.Ordinal) < user.IndexOf("Code after", StringComparison.Ordinal) &&
            user.IndexOf("Code after", StringComparison.Ordinal) < user.IndexOf("Task:", StringComparison.Ordinal));
        Assert.DoesNotContain(@"C:\src", user, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(InlineEditCommand.Doc, "edit blocks", "Add XML documentation comments (///")]
    [InlineData(InlineEditCommand.Fix, "edit blocks", "Fix the problems the diagnostics report")]
    [InlineData(InlineEditCommand.Test, "Markdown code block", "Write a unit test")]
    [InlineData(InlineEditCommand.Explain, "Explain the code to edit", "Explain the code to edit.")]
    public void SlashCommands_ChooseTheirSystemPromptAndTask(InlineEditCommand command, string inSystem, string inTask)
    {
        var messages = Builder.Build(Input(command, string.Empty));

        Assert.Contains(inSystem, messages[0].Content, StringComparison.Ordinal);
        Assert.Contains("Task: " + inTask, messages[1].Content, StringComparison.Ordinal);
        Assert.DoesNotContain("Instruction:", messages[1].Content, StringComparison.Ordinal);
    }

    [Fact]
    public void Doc_UsesTheVisualBasicCommentStyle_ForVb()
    {
        var messages = Builder.Build(Input(InlineEditCommand.Doc, string.Empty) with { LanguageId = "vb" });

        Assert.Contains("'''", messages[1].Content, StringComparison.Ordinal);
        Assert.Contains("Visual Basic", messages[0].Content, StringComparison.Ordinal);
    }

    [Fact]
    public void Fix_ListsTheDiagnostics_WithOneBasedLines()
    {
        var input = Input(InlineEditCommand.Fix, string.Empty) with
        {
            Diagnostics = [new InlineEditDiagnostic { Line = 11, Severity = DiagnosticSeverity.Error, Code = "CS0103", Message = "The name 'c' does not exist in the current context" }],
        };

        var user = Builder.Build(input)[1].Content;

        Assert.Contains("- line 12: error CS0103: The name 'c' does not exist in the current context", user, StringComparison.Ordinal);
    }

    [Fact]
    public void Refinement_ShowsTheProposal_AndTheEarlierInstructions()
    {
        var input = Input(InlineEditCommand.None, "also rename it to Sum") with
        {
            Code = "    public int Add(int a, int b)\n        => checked(a + b);\n",
            IsRefinement = true,
            History = ["use checked arithmetic"],
        };

        var user = Builder.Build(input)[1].Content;

        Assert.Contains("your earlier proposal for lines 11 to 12", user, StringComparison.Ordinal);
        Assert.Contains("do not start again from the original code", user, StringComparison.Ordinal);
        Assert.Contains("12|         => checked(a + b);", user, StringComparison.Ordinal);
        Assert.Contains("Earlier instructions for this code, oldest first:\n1. use checked arithmetic", user, StringComparison.Ordinal);
        Assert.Contains("Instruction: also rename it to Sum", user, StringComparison.Ordinal);
    }

    [Fact]
    public void Context_IsCappedToWholeLinesNearestTheScope()
    {
        var before = string.Concat(Enumerable.Range(0, 100).Select(i => $"// before {i}\n"));
        var after = string.Concat(Enumerable.Range(0, 100).Select(i => $"// after {i}\n"));
        var builder = new InlineEditPromptBuilder(contextCharacters: 60);

        var user = builder.Build(Input(InlineEditCommand.None, "x") with { Before = before, After = after })[1].Content;

        Assert.Contains("// before 99", user, StringComparison.Ordinal);
        Assert.DoesNotContain("// before 90\n", user, StringComparison.Ordinal);
        Assert.Contains("// after 0", user, StringComparison.Ordinal);
        Assert.DoesNotContain("// after 9\n", user, StringComparison.Ordinal);
        Assert.StartsWith("// before ", user.Split("```csharp\n")[1], StringComparison.Ordinal);
    }

    [Fact]
    public void CodeContainingFences_GetsALongerFence()
    {
        var user = Builder.Build(Input(InlineEditCommand.None, "x") with { Code = "/// ```\n/// Run();\n/// ```\n" })[1].Content;

        Assert.Contains("````csharp\n11| /// ```", user, StringComparison.Ordinal);
    }

    [Fact]
    public void Corrections_NameTheProblem()
    {
        var failure = new EditBlockFailure(new EditBlock(2, ["a"], ["b"]), new EditMatch(EditMatchKind.Ambiguous, 0, 1, 3, []));

        Assert.Contains("Block 2: its SEARCH lines match 3 places", Builder.Unmatched([failure]), StringComparison.Ordinal);
        Assert.Contains("Block 1: it is not closed", Builder.Malformed([new MalformedEditBlock(1, "it is not closed")]), StringComparison.Ordinal);
        Assert.Contains("<redacted:aws-access-key#9>", Builder.Placeholders(["<redacted:aws-access-key#9>"]), StringComparison.Ordinal);
        var syntax = Builder.Syntax(["line 12: CS1002: ; expected"], "int x = 1\n", 12, "csharp");
        Assert.Contains("- line 12: CS1002: ; expected", syntax, StringComparison.Ordinal);
        Assert.Contains("```csharp\n12| int x = 1\n```", syntax, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(InlineEditCommand.None)]
    [InlineData(InlineEditCommand.Doc)]
    [InlineData(InlineEditCommand.Fix)]
    public void WholeScope_AsksForTheCompleteRewrittenCode_NotBlocks(InlineEditCommand command)
    {
        var messages = Builder.Build(Input(command, "extract it") with { WholeScope = true });

        Assert.Contains("complete rewritten code", messages[0].Content, StringComparison.Ordinal);
        Assert.DoesNotContain("<<<<<<< SEARCH", messages[0].Content, StringComparison.Ordinal);
        var user = messages[1].Content;
        Assert.Contains("11|     public int Add(int a, int b)\n12|         => a + b;", user, StringComparison.Ordinal);
        Assert.EndsWith("Reply with only the complete rewritten code to edit: plain code, no Markdown fence, no line numbers, no explanation.", user, StringComparison.Ordinal);
        Assert.DoesNotContain("SEARCH", user, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(InlineEditCommand.Test)]
    [InlineData(InlineEditCommand.Explain)]
    public void WholeScope_IsIgnored_ForTestAndExplain(InlineEditCommand command)
    {
        var plain = Builder.Build(Input(command, string.Empty));
        var whole = Builder.Build(Input(command, string.Empty) with { WholeScope = true });

        Assert.Equal(plain[0].Content, whole[0].Content);
        Assert.Equal(plain[1].Content, whole[1].Content);
    }

    [Fact]
    public void WholeScope_Refinement_ShowsTheProposal_AndAsksForTheRevisedCode()
    {
        var input = Input(InlineEditCommand.None, "also rename it to Sum") with
        {
            Code = "    public int Add(int a, int b)\n        => checked(a + b);\n",
            IsRefinement = true,
            History = ["use checked arithmetic"],
            WholeScope = true,
        };

        var user = Builder.Build(input)[1].Content;

        Assert.Contains("your earlier proposal for lines 11 to 12", user, StringComparison.Ordinal);
        Assert.Contains("12|         => checked(a + b);", user, StringComparison.Ordinal);
        Assert.Contains("Earlier instructions for this code, oldest first:\n1. use checked arithmetic", user, StringComparison.Ordinal);
        Assert.EndsWith("Reply with only the complete revised code (your earlier proposal with the change made): plain code, no Markdown fence, no line numbers, no explanation.", user, StringComparison.Ordinal);
    }

    [Fact]
    public void WholeScopeCorrections_AskForCompleteCode_NotEditBlocks()
    {
        var syntax = Builder.Syntax(["line 12: CS1002: ; expected"], "int x = 1\n", 12, "csharp", wholeScope: true);
        Assert.Contains("Your rewrite introduced syntax errors", syntax, StringComparison.Ordinal);
        Assert.Contains("```csharp\n12| int x = 1\n```", syntax, StringComparison.Ordinal);
        Assert.Contains("complete corrected code", syntax, StringComparison.Ordinal);

        var doc = Builder.DocOnly(["'public Order Place(Order order)'"], wholeScope: true);
        Assert.Contains("Your rewrite changed code", doc, StringComparison.Ordinal);
        Assert.Contains("'public Order Place(Order order)'", doc, StringComparison.Ordinal);

        var placeholders = Builder.Placeholders(["<redacted:aws-access-key#9>"], wholeScope: true);
        Assert.Contains("Your rewrite contains <redacted:aws-access-key#9>", placeholders, StringComparison.Ordinal);

        Assert.Contains("no edit blocks", Builder.NoWholeCode(), StringComparison.Ordinal);
        Assert.DoesNotContain("SEARCH", syntax + doc + placeholders + Builder.NoWholeCode(), StringComparison.Ordinal);
    }

    [Fact]
    public void Render_IsSinglePass_AndLeavesUnknownPlaceholders()
    {
        Assert.Equal("a {text} b {other}", InlineEditTemplates.Render("a {value} b {other}", ("value", "{text}"), ("text", "X")));
    }

    [Fact]
    public void Parse_RejectsADocumentWithoutARequiredEntry()
    {
        Assert.Throws<FormatException>(() => InlineEditTemplates.Parse("""{ "system": { "edit": "x" } }"""));
    }

    [Fact]
    public void RetrievedCode_IsShownAfterTheHeader_WithItsSpans_AtMostFourChunks()
    {
        var related = Enumerable.Range(1, 6)
            .Select(i => new ContextChunk($"idx:{i}", $"src/Math/Helper{i}.cs", $@"C:\repo\src\Math\Helper{i}.cs", i * 10, (i * 10) + 2, $"static int Helper{i}(int x) => x + {i};", "b", 1.0 / i))
            .ToList();

        var user = Builder.Build(Input(InlineEditCommand.None, "use the helper") with { Related = related })[1].Content;

        Assert.Contains("Related code from elsewhere in the repository", user, StringComparison.Ordinal);
        Assert.Contains("src/Math/Helper1.cs:10-12\n```csharp\nstatic int Helper1(int x) => x + 1;\n```", user, StringComparison.Ordinal);
        Assert.Contains("Helper4.cs:40-42", user, StringComparison.Ordinal);
        Assert.DoesNotContain("Helper5.cs", user, StringComparison.Ordinal);
        Assert.True(user.IndexOf("File: Calc.cs", StringComparison.Ordinal) < user.IndexOf("Related code", StringComparison.Ordinal));
        Assert.True(user.IndexOf("Related code", StringComparison.Ordinal) < user.IndexOf("Code before", StringComparison.Ordinal));
        Assert.DoesNotContain(@"C:\repo", user, StringComparison.Ordinal);

        Assert.DoesNotContain("Related code", Builder.Build(Input(InlineEditCommand.None, "x"))[1].Content, StringComparison.Ordinal);
    }

    private static InlineEditPromptInput Input(InlineEditCommand command, string instruction) => new(
        "csharp",
        "Calc.cs",
        command,
        instruction,
        "    public int Add(int a, int b)\n        => a + b;\n",
        11,
        IsRefinement: false,
        "public class Calc\n{\n",
        "}\n",
        [],
        []);
}
