using System.Linq;
using Pillion.Contracts;
using Pillion.VisualStudio.Core.Autocomplete;
using Pillion.VisualStudio.Core.InlineChat;

namespace Pillion.VisualStudio.Tests;

/// <summary>Inline chat's rules: slash commands, the scope, the stream accumulator and the request (work items 1.6 and 1.7).</summary>
public sealed class InlineChatRulesTests
{
    private const string Code =
        "namespace Scratch;\r\n" +          // line 0
        "\r\n" +                            // line 1
        "public class Orders\r\n" +         // line 2
        "{\r\n" +                           // line 3
        "    public void Place(int n)\r\n" + // line 4
        "    {\r\n" +                       // line 5
        "        if (n < 0) throw new System.ArgumentException();\r\n" + // line 6
        "        Count += n;\r\n" +         // line 7
        "    }\r\n" +                       // line 8
        "\r\n" +                            // line 9
        "    public int Count { get; private set; }\r\n" + // line 10
        "}\r\n";                            // line 11, then the empty line 12

    private static readonly StringDocumentText Text = new(Code);

    [Theory]
    [InlineData("/doc", InlineEditCommand.Doc, "")]
    [InlineData("  /FIX  the null check ", InlineEditCommand.Fix, "the null check")]
    [InlineData("/test with xunit", InlineEditCommand.Test, "with xunit")]
    [InlineData("/explain", InlineEditCommand.Explain, "")]
    [InlineData("/document this", InlineEditCommand.None, "/document this")]
    [InlineData("extract the validation into a method", InlineEditCommand.None, "extract the validation into a method")]
    [InlineData("use /doc style", InlineEditCommand.None, "use /doc style")]
    [InlineData("/", InlineEditCommand.None, "/")]
    public void Parse_SplitsTheShortcutFromTheInstruction(string text, InlineEditCommand command, string instruction)
    {
        var parsed = InlineChatPrompt.Parse(text);

        Assert.Equal(command, parsed.Command);
        Assert.Equal(instruction, parsed.Instruction);
        Assert.False(parsed.IsEmpty);
    }

    [Fact]
    public void Parse_EmptyOrBlank_IsEmpty()
    {
        Assert.True(InlineChatPrompt.Parse(null).IsEmpty);
        Assert.True(InlineChatPrompt.Parse("   ").IsEmpty);
    }

    [Fact]
    public void Suggest_OffersMatchingShortcutsWhileTyping()
    {
        Assert.Equal(["/doc", "/test", "/fix", "/explain"], InlineChatPrompt.Suggest("/").Select(s => s.Name));
        Assert.Equal(["/doc"], InlineChatPrompt.Suggest("/d").Select(s => s.Name));
        Assert.Equal(["/explain"], InlineChatPrompt.Suggest("/EX").Select(s => s.Name));
        Assert.Empty(InlineChatPrompt.Suggest("/doc"));
        Assert.Empty(InlineChatPrompt.Suggest("/doc "));
        Assert.Empty(InlineChatPrompt.Suggest("/x"));
        Assert.Empty(InlineChatPrompt.Suggest("doc"));
        Assert.Empty(InlineChatPrompt.Suggest(string.Empty));
        Assert.Equal("/fix: fix the errors and warnings reported on these lines", InlineChatPrompt.Commands[2].AutomationName);
    }

    [Fact]
    public void Scope_Selection_IsExtendedToWholeLines()
    {
        var start = Code.IndexOf("if (n", System.StringComparison.Ordinal);
        var end = Code.IndexOf("+= n", System.StringComparison.Ordinal);

        var scope = InlineScopeSelector.Select(Text, end, start, caret: end);

        Assert.Equal(InlineScopeKind.Selection, scope.Kind);
        Assert.Equal(6, scope.StartLine);
        Assert.Equal(7, scope.EndLine);
        Assert.Equal(
            "        if (n < 0) throw new System.ArgumentException();\r\n        Count += n;",
            Code.Substring(scope.Start, scope.Length));
    }

    [Fact]
    public void Scope_SelectionOfWholeLines_DoesNotTakeTheNextLine()
    {
        var start = Text.GetLineStart(4);
        var end = Text.GetLineStart(9); // After line 8's line break, as a full-line selection ends.

        var scope = InlineScopeSelector.Select(Text, start, end, caret: end);

        Assert.Equal(4, scope.StartLine);
        Assert.Equal(8, scope.EndLine);
        Assert.EndsWith("    }", Code.Substring(scope.Start, scope.Length), System.StringComparison.Ordinal);
    }

    [Fact]
    public void Scope_NoSelection_UsesTheEnclosingMember()
    {
        var memberStart = Code.IndexOf("public void Place", System.StringComparison.Ordinal);
        var memberEnd = Code.IndexOf("    }\r\n\r\n", System.StringComparison.Ordinal) + 5;
        var caret = Code.IndexOf("Count += n", System.StringComparison.Ordinal);

        var scope = InlineScopeSelector.Select(Text, caret, caret, caret, memberStart, memberEnd);

        Assert.Equal(InlineScopeKind.Member, scope.Kind);
        Assert.Equal(4, scope.StartLine);
        Assert.Equal(8, scope.EndLine);
        Assert.Equal(Text.GetLineStart(4), scope.Start); // Whole lines: the indentation is part of the scope.
    }

    [Fact]
    public void Scope_NoSelectionAndNoMember_UsesTheCaretLine()
    {
        var caret = Code.IndexOf("get; private", System.StringComparison.Ordinal);

        var scope = InlineScopeSelector.Select(Text, caret, caret, caret);

        Assert.Equal(InlineScopeKind.Line, scope.Kind);
        Assert.Equal(10, scope.StartLine);
        Assert.Equal(10, scope.EndLine);
        Assert.Equal("    public int Count { get; private set; }", Code.Substring(scope.Start, scope.Length));
    }

    [Fact]
    public void Scope_MemberThatDoesNotContainTheCaret_FallsBackToTheLine()
    {
        var memberStart = Code.IndexOf("public void Place", System.StringComparison.Ordinal);
        var memberEnd = memberStart + 30;
        var caret = Code.IndexOf("get; private", System.StringComparison.Ordinal);

        var scope = InlineScopeSelector.Select(Text, caret, caret, caret, memberStart, memberEnd);

        Assert.Equal(InlineScopeKind.Line, scope.Kind);
    }

    [Fact]
    public void Scope_WhiteSpaceSelection_IsTreatedAsNoSelection()
    {
        var start = Text.GetLineStart(9);
        var caret = Code.IndexOf("Count += n", System.StringComparison.Ordinal);

        var scope = InlineScopeSelector.Select(Text, start, start + 2, caret);

        Assert.Equal(InlineScopeKind.Line, scope.Kind);
        Assert.Equal(7, scope.StartLine);
    }

    [Fact]
    public void Scope_EmptyDocument_IsTheOnlyLine()
    {
        var scope = InlineScopeSelector.Select(new StringDocumentText(string.Empty), 0, 0, 0);

        Assert.Equal(0, scope.Start);
        Assert.Equal(0, scope.Length);
        Assert.Equal(0, scope.StartLine);
    }

    [Fact]
    public void Accumulator_AppendsAndStartsAgainOnResetText()
    {
        var accumulator = new InlineProposalAccumulator("r1");

        Assert.True(accumulator.Add(new InlineEditChunk { RequestId = "r1", Text = "// wrong" }));
        Assert.False(accumulator.HasFirstLine);
        Assert.True(accumulator.Add(new InlineEditChunk { RequestId = "r1", Text = " line\n" }));
        Assert.True(accumulator.HasFirstLine);
        Assert.True(accumulator.Add(new InlineEditChunk { RequestId = "r1", Text = "first\n", ResetText = true, ModelId = "m", ProviderId = "p" }));
        Assert.True(accumulator.Add(new InlineEditChunk { Text = "second" })); // No id: accepted, as hosts may omit it.
        Assert.False(accumulator.Add(new InlineEditChunk { RequestId = "other", Text = "stale" }));

        Assert.Equal("first\nsecond", accumulator.Text);
        Assert.Equal(1, accumulator.Resets);
        Assert.Equal("m", accumulator.ModelId);
        Assert.Equal("p", accumulator.ProviderId);
        Assert.False(accumulator.IsFinal);

        Assert.True(accumulator.Add(new InlineEditChunk { RequestId = "r1", IsFinal = true, Explanation = "  why  ", Warning = "careful", RejectedBlocks = 2 }));
        Assert.True(accumulator.IsFinal);
        Assert.Equal("why", accumulator.Explanation);
        Assert.Equal("careful", accumulator.Warning);
        Assert.Equal(2, accumulator.RejectedBlocks);
        Assert.False(accumulator.Add(new InlineEditChunk { RequestId = "r1", Text = "after the end" }));
        Assert.Equal("first\nsecond", accumulator.Text);
    }

    [Fact]
    public void Accumulator_ResetWithEmptyText_ClearsEverything()
    {
        var accumulator = new InlineProposalAccumulator("r1");
        accumulator.Add(new InlineEditChunk { Text = "abc\n" });
        accumulator.Add(new InlineEditChunk { ResetText = true });

        Assert.Equal(string.Empty, accumulator.Text);
        Assert.False(accumulator.HasFirstLine);
    }

    [Fact]
    public void Build_FillsTheRequestFromTheScope()
    {
        var start = Code.IndexOf("if (n", System.StringComparison.Ordinal);
        var scope = InlineScopeSelector.Select(Text, start, start + 4, start);
        var document = new InlineChatDocument(@"C:\src\Orders.cs", "csharp", Text, scope);

        var request = InlineEditRequestBuilder.Build(
            document,
            InlineChatPrompt.Parse("/fix"),
            "r1",
            "c1",
            "ollama/qwen2.5-coder:7b",
            previousProposal: null,
            diagnostics:
            [
                new InlineEditDiagnostic { Line = 6, Code = "CS0103", Message = "inside", Severity = DiagnosticSeverity.Error },
                new InlineEditDiagnostic { Line = 10, Code = "CS0169", Message = "outside", Severity = DiagnosticSeverity.Warning },
            ]);

        Assert.Equal("r1", request.RequestId);
        Assert.Equal(@"C:\src\Orders.cs", request.FilePath);
        Assert.Equal("csharp", request.LanguageId);
        Assert.Equal("        if (n < 0) throw new System.ArgumentException();", request.ScopeText);
        Assert.Equal(6, request.ScopeStartLine);
        Assert.Equal(0, request.ScopeStartColumn);
        Assert.Equal(Code.Substring(0, scope.Start), request.TextBefore);
        Assert.Equal(Code.Substring(scope.End), request.TextAfter);
        Assert.Equal(InlineEditCommand.Fix, request.Command);
        Assert.Equal(string.Empty, request.Instruction);
        Assert.Equal("CS0103", Assert.Single(request.Diagnostics).Code);
        Assert.Equal(Profile.Auto, request.Profile);
        Assert.Equal("ollama/qwen2.5-coder:7b", request.ModelId);
        Assert.Equal("c1", request.ConversationId);
        Assert.Null(request.PreviousProposal);
    }

    [Fact]
    public void Build_CapsTheContextAtWholeLines()
    {
        var line = new string('x', 99) + "\n";
        var text = new StringDocumentText(string.Concat(Enumerable.Repeat(line, 200)) + "SCOPE\n" + string.Concat(Enumerable.Repeat(line, 200)));
        var caret = 200 * line.Length;
        var document = new InlineChatDocument("a.txt", "plaintext", text, InlineScopeSelector.Select(text, caret, caret, caret));

        var request = InlineEditRequestBuilder.Build(document, InlineChatPrompt.Parse("go"), "r", null, null, null, null);

        Assert.Equal("SCOPE", request.ScopeText);
        Assert.True(request.TextBefore.Length <= InlineEditRequestBuilder.MaxContextCharacters);
        Assert.True(request.TextBefore.Length >= InlineEditRequestBuilder.MaxContextCharacters - line.Length);
        Assert.StartsWith("x", request.TextBefore, System.StringComparison.Ordinal);
        Assert.EndsWith("\n", request.TextBefore, System.StringComparison.Ordinal);
        Assert.True(request.TextAfter.Length <= InlineEditRequestBuilder.MaxContextCharacters);
        Assert.StartsWith("\n", request.TextAfter, System.StringComparison.Ordinal);
        Assert.Null(request.ModelId);
        Assert.Empty(request.Diagnostics);
    }

    [Fact]
    public void History_KeepsTheLastTenPromptsNewestFirstWithoutDuplicates()
    {
        var history = new InlineChatHistory();
        for (var i = 0; i < 12; i++)
        {
            history.Add("prompt " + i);
        }

        history.Add("PROMPT 5");
        history.Add("  ");

        Assert.Equal(InlineChatHistory.MaxPrompts, history.Prompts.Count);
        Assert.Equal("PROMPT 5", history.Prompts[0]);
        Assert.Equal("prompt 11", history.Prompts[1]);
        Assert.DoesNotContain("prompt 5", history.Prompts);
        Assert.DoesNotContain("prompt 1", history.Prompts);
    }

    [Fact]
    public void FromSignals_KeepsThisFilesErrorsAndWarningsWithZeroBasedLines()
    {
        const string path = @"C:\src\Orders.cs";
        Pillion.VisualStudio.Core.Signals.DiagnosticInfo[] signals =
        [
            new(path, 7, 9, Pillion.VisualStudio.Core.Signals.IdeSeverity.Error, "CS0103", "Unknown name", "Orders", Pillion.VisualStudio.Core.Signals.DiagnosticSource.ErrorList),
            new(@"c:\SRC\orders.cs", 7, 9, Pillion.VisualStudio.Core.Signals.IdeSeverity.Error, "CS0103", "Unknown name", "Orders", Pillion.VisualStudio.Core.Signals.DiagnosticSource.Roslyn),
            new(path, 3, 1, Pillion.VisualStudio.Core.Signals.IdeSeverity.Warning, "CS0168", "Unused", "Orders", Pillion.VisualStudio.Core.Signals.DiagnosticSource.Roslyn),
            new(path, 5, 1, Pillion.VisualStudio.Core.Signals.IdeSeverity.Info, "IDE0005", "Unnecessary using", "Orders", Pillion.VisualStudio.Core.Signals.DiagnosticSource.Roslyn),
            new(@"C:\src\Other.cs", 2, 1, Pillion.VisualStudio.Core.Signals.IdeSeverity.Error, "CS1002", "; expected", "Orders", Pillion.VisualStudio.Core.Signals.DiagnosticSource.ErrorList),
            new(path, 0, 0, Pillion.VisualStudio.Core.Signals.IdeSeverity.Error, "CS5001", "No Main", "Orders", Pillion.VisualStudio.Core.Signals.DiagnosticSource.ErrorList),
        ];

        var diagnostics = InlineChatDiagnostics.FromSignals(signals, path);

        Assert.Equal([2, 6], diagnostics.Select(d => d.Line));
        Assert.Equal(["CS0168", "CS0103"], diagnostics.Select(d => d.Code));
        Assert.Equal(DiagnosticSeverity.Warning, diagnostics[0].Severity);
        Assert.Equal(DiagnosticSeverity.Error, diagnostics[1].Severity);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("{}", true)]
    [InlineData("{\"inlineChat\":true}", true)]
    [InlineData("{\"inlineChat\":false,\"chat\":true}", false)]
    [InlineData("{\"inlineChat\":\"no\"}", true)]
    public void ReadFlag_IsOffOnlyWhenTheHostSaysFalse(string? json, bool expected) =>
        Assert.Equal(expected, InlineChatAvailability.ReadFlag(json));
}
