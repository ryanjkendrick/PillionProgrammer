using System.Text;
using Pillion.Common.Hardware;
using Pillion.Contracts;

namespace Pillion.ModelGateway.Tests.Autocomplete;

public sealed class PromptBudgetTests
{
    // One token per character makes every share easy to check by hand.
    private static readonly EstimatingCounter PerChar = new("test", CharsPerToken: 1, DigitTokens: 1, SafetyMargin: 0);

    [Theory]
    [InlineData(true, GpuTier.CpuOnly, 1024)]
    [InlineData(true, GpuTier.Gpu8GB, 2048)]
    [InlineData(true, GpuTier.Gpu12GB, 2048)]
    [InlineData(true, GpuTier.Gpu24GBPlus, 2048)]
    [InlineData(false, GpuTier.CpuOnly, 2048)]
    [InlineData(false, GpuTier.Gpu16GB, 2048)]
    public void Budget_IsCpuOrGpu(bool isLocal, GpuTier tier, int expected) =>
        Assert.Equal(expected, AutocompleteBudget.PromptTokensFor(isLocal, tier));

    [Fact]
    public void EverythingFits_NothingIsTrimmed_AndTheHeaderIsIncluded()
    {
        var request = Request(prefix: "int Add(int a, int b)\n{\n    return ", suffix: "\n}\n", caretLine: 2);
        request.EnclosingDeclaration = "int Add(int a,  int b)";
        request.OpenDocuments = [@"C:\src\Zeta.cs", @"C:\src\Calc.cs", @"C:\src\alpha.cs"];
        request.RecentEdits = ["Zeta.cs:12 added Reset()", "Calc.cs:3 typed return"];

        var prompt = AutocompletePromptBuilder.Build(request, 2048, PerChar);

        Assert.Equal(request.Prefix, prompt.Prefix);
        Assert.Equal(request.Suffix, prompt.Suffix);
        Assert.Equal(0, prompt.PrefixLinesDropped);
        Assert.Equal(0, prompt.SuffixLinesDropped);

        // The declaration is already in the prefix, the current file's edit is left out, open files are sorted.
        Assert.Equal(
            "// file: Calc.cs\n// see: alpha.cs, Zeta.cs\n// edit: Zeta.cs:12 added Reset()\n",
            prompt.Header);
        Assert.Equal(["file", "openDocuments", "recentEdit"], prompt.HeaderItems);
        Assert.Equal(prompt.Header + prompt.Prefix, prompt.PromptPrefix);
        Assert.Equal(prompt.HeaderTokens + prompt.PrefixTokens + prompt.SuffixTokens + AutocompleteBudget.ServerTemplateReserveTokens, prompt.PromptTokens);
        Assert.False(prompt.IsExact);
    }

    [Fact]
    public void Declaration_IsInTheHeader_WhenTheKeptPrefixDoesNotShowIt()
    {
        var request = Request(prefix: Lines(0, 400) + "    return ", suffix: "\n}\n", caretLine: 400);
        request.EnclosingDeclaration = "public   int\n Add(int a, int b)";

        var prompt = AutocompletePromptBuilder.Build(request, 1024, PerChar);

        Assert.Contains("// in: public int Add(int a, int b)\n", prompt.Header, StringComparison.Ordinal);
        Assert.Contains("declaration", prompt.HeaderItems);
    }

    [Fact]
    public void Prefix_IsCutOnLineBoundaries_AtAnAnchorLine_KeepingTheCaretLine()
    {
        var prefix = Lines(0, 500) + "    var total = ";
        var prompt = AutocompletePromptBuilder.Build(Request(prefix, suffix: string.Empty, caretLine: 500), 1024, PerChar);

        Assert.EndsWith("    var total = ", prompt.Prefix, StringComparison.Ordinal);
        Assert.StartsWith("line", prompt.Prefix, StringComparison.Ordinal);
        var firstLine = int.Parse(prompt.Prefix.AsSpan(4, 4), System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(0, firstLine % AutocompletePromptBuilder.AnchorLines);
        Assert.Equal(firstLine, prompt.PrefixLinesDropped);
        Assert.True(prompt.PromptTokens <= 1024, $"{prompt.PromptTokens}");

        // The next anchor up would not have fitted.
        var earlier = prefix[prefix.IndexOf($"line{firstLine - AutocompletePromptBuilder.AnchorLines:D4}", StringComparison.Ordinal)..];
        Assert.True(prompt.HeaderTokens + PerChar.Count(earlier) + prompt.SuffixTokens + prompt.TemplateTokens > 1024);
    }

    [Fact]
    public void Suffix_IsTrimmedBeforeThePrefix()
    {
        // Both sides need far more than the budget: the suffix keeps only its minimum share (plus what the prefix's
        // line-aligned cut leaves over) and the prefix gets the rest.
        var request = Request(prefix: Lines(0, 600) + "x = ", suffix: "\n" + Lines(601, 600), caretLine: 600);
        var budget = 1024;
        var available = budget - AutocompleteBudget.ServerTemplateReserveTokens;

        var prompt = AutocompletePromptBuilder.Build(request, budget, PerChar);

        Assert.True(prompt.PromptTokens <= budget);
        Assert.True(prompt.SuffixTokens >= (int)(available * AutocompleteBudget.SuffixMinimumShare) - 10, $"suffix {prompt.SuffixTokens}");
        Assert.True(prompt.SuffixTokens < (int)(available * AutocompleteBudget.SuffixMinimumShare) + (AutocompletePromptBuilder.AnchorLines * 9), $"suffix {prompt.SuffixTokens}");
        Assert.True(prompt.PrefixTokens > available * AutocompleteBudget.PrefixMinimumShare, $"prefix {prompt.PrefixTokens}");
        Assert.StartsWith("\n", prompt.Suffix, StringComparison.Ordinal);
        Assert.EndsWith("\n", prompt.Suffix, StringComparison.Ordinal); // whole lines
        Assert.True(prompt.SuffixLinesDropped > 0);
    }

    [Fact]
    public void ShortPrefix_LeavesTheRestToTheSuffix()
    {
        var request = Request(prefix: "int x = ", suffix: "\n" + Lines(1, 600), caretLine: 0);

        var prompt = AutocompletePromptBuilder.Build(request, 1024, PerChar);

        Assert.Equal("int x = ", prompt.Prefix);
        Assert.True(prompt.SuffixTokens > 1024 * 0.7, $"suffix {prompt.SuffixTokens}");
        Assert.True(prompt.PromptTokens <= 1024);
    }

    [Theory]
    [InlineData(1024, 10, 10)]
    [InlineData(1024, 3000, 10)]
    [InlineData(1024, 10, 3000)]
    [InlineData(1024, 3000, 3000)]
    [InlineData(2048, 3000, 3000)]
    [InlineData(2048, 50, 50)]
    [InlineData(300, 3000, 3000)]
    public void PromptTokens_NeverExceedTheBudget(int budget, int prefixLines, int suffixLines)
    {
        var request = Request(prefix: Lines(0, prefixLines) + "    foo(", suffix: ");\n" + Lines(prefixLines + 1, suffixLines), caretLine: prefixLines);
        request.EnclosingDeclaration = "void Run()";
        request.OpenDocuments = ["a.cs", "b.cs"];
        request.RecentEdits = ["b.cs:1 x"];

        var prompt = AutocompletePromptBuilder.Build(request, budget, PerChar);

        Assert.True(prompt.PromptTokens <= budget, $"{prompt.PromptTokens} > {budget}");
        Assert.True(prompt.HeaderTokens <= (int)((budget - AutocompleteBudget.ServerTemplateReserveTokens) * AutocompleteBudget.HeaderMaximumShare));
        Assert.EndsWith("    foo(", prompt.Prefix, StringComparison.Ordinal);
    }

    [Fact]
    public void Header_IsLeftOut_WhenNothingFits()
    {
        var request = Request(prefix: Lines(0, 100) + "x", suffix: Lines(101, 100), caretLine: 100);
        request.FilePath = @"C:\a-very-long-directory-name\and-an-even-longer-file-name-that-will-not-fit-anywhere.cs";

        var prompt = AutocompletePromptBuilder.Build(request, 60, PerChar);

        Assert.Equal(string.Empty, prompt.Header);
        Assert.Empty(prompt.HeaderItems);
    }

    [Fact]
    public void Header_UsesTheLanguagesCommentSyntax_AndNoneForJson()
    {
        var python = Request("x = ", string.Empty, 0);
        python.LanguageId = "python";
        python.FilePath = "ledger.py";
        var json = Request("{ ", string.Empty, 0);
        json.LanguageId = "json";
        json.FilePath = "a.json";

        Assert.Equal("# file: ledger.py\n", AutocompletePromptBuilder.Build(python, 1024, PerChar).Header);
        Assert.Equal(string.Empty, AutocompletePromptBuilder.Build(json, 1024, PerChar).Header);
    }

    [Theory]
    [InlineData("csharp", @"C:\src\Calc.cs", "// file: Calc.cs\n// see: Bar.cs\n")]
    [InlineData("typescript", @"C:\web\src\user.ts", "// file: user.ts\n// see: Bar.cs\n")]
    [InlineData("python", @"C:\py\ledger.py", "# file: ledger.py\n# see: Bar.cs\n")]
    [InlineData("vb", @"C:\src\Calc.vb", "' file: Calc.vb\n' see: Bar.cs\n")]
    [InlineData("sql", @"C:\db\q.sql", "-- file: q.sql\n-- see: Bar.cs\n")]
    [InlineData("xml", @"C:\src\a.xml", "<!-- file: a.xml -->\n<!-- see: Bar.cs -->\n")]
    [InlineData("css", @"C:\web\a.css", "/* file: a.css */\n/* see: Bar.cs */\n")]
    public void Header_IsTerseCodeComments_InEachLanguagesSyntax(string languageId, string path, string expected)
    {
        var request = Request("x", string.Empty, 0);
        request.LanguageId = languageId;
        request.FilePath = path;
        request.OpenDocuments = [@"C:\elsewhere\Bar.cs"];

        Assert.Equal(expected, AutocompletePromptBuilder.Build(request, 2048, PerChar).Header);
    }

    [Fact]
    public void Header_HasNoSentencesAndNoDirectories_EvenForRecentEditsSentWithFullPaths()
    {
        // The edits come from the extension as "full path:line: text"; at Gate 1 the header repeated those paths in
        // sentences ("// Recent edit: C:\...") and the 1.5B model answered them in prose.
        var request = Request("int Twice(int v)\n{\n    return ", "\n}\n", caretLine: 2);
        request.FilePath = @"C:\Users\dev\repos\Gate\TypeFast.cs";
        request.OpenDocuments = [@"C:\Users\dev\repos\Gate\TypeFast.cs", @"C:\Users\dev\repos\Gate\WarmTypeFast.cs"];
        request.RecentEdits =
        [
            @"C:\Users\dev\repos\Gate\WarmTypeFast.cs:3: public static int Warm() => 1;",
            @"C:\Users\dev\repos\Gate\TypeFast.cs:2: {",
        ];

        var prompt = AutocompletePromptBuilder.Build(request, 2048, PerChar);

        // WarmTypeFast.cs is another file even though its name ends with the current one's.
        Assert.Equal("// file: TypeFast.cs\n// see: WarmTypeFast.cs\n// edit: WarmTypeFast.cs:3: public static int Warm() => 1;\n", prompt.Header);
        Assert.DoesNotContain(@"C:\", prompt.PromptPrefix, StringComparison.Ordinal);
        Assert.DoesNotContain("Recent", prompt.PromptPrefix, StringComparison.Ordinal);
        Assert.DoesNotContain("Open files", prompt.PromptPrefix, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(@"C:\src\Foo.cs:12: Reset();", "Foo.cs", "Foo.cs:12: Reset();")]
    [InlineData("/home/dev/app/main.py:3: main()", "main.py", "main.py:3: main()")]
    [InlineData("Zeta.cs:12 added Reset()", "", "Zeta.cs:12 added Reset()")]
    public void RecentEdits_AreShortenedToTheFileName(string edit, string name, string text)
    {
        Assert.Equal((name, text), AutocompletePromptBuilder.ShortenEdit(edit));
    }

    [Fact]
    public void WithoutTheHeader_ThePromptIsTheCodeAlone_AndThePrefixGetsTheRoom()
    {
        var request = Request(Lines(0, 400) + "    return ", "\n}\n", caretLine: 400);
        request.EnclosingDeclaration = "public int Add(int a, int b)";
        request.OpenDocuments = [@"C:\src\Zeta.cs"];
        request.RecentEdits = [@"C:\src\Zeta.cs:12: Reset();"];

        var with = AutocompletePromptBuilder.Build(request, 1024, PerChar);
        var without = AutocompletePromptBuilder.Build(request, 1024, PerChar, includeHeader: false);

        Assert.NotEmpty(with.Header);
        Assert.Equal(string.Empty, without.Header);
        Assert.Empty(without.HeaderItems);
        Assert.Equal(0, without.HeaderTokens);
        Assert.Equal(without.Prefix, without.PromptPrefix);
        Assert.True(without.Prefix.Length >= with.Prefix.Length);
        Assert.EndsWith("    return ", without.PromptPrefix, StringComparison.Ordinal);
    }

    [Fact]
    public void WithoutTheHeader_ThePrefixStillStartsOnAnAnchorLine()
    {
        // The cache-friendly layout does not depend on the header: the kept prefix starts on a line whose absolute
        // number is a multiple of 16, and a keystroke only appends to the prompt.
        var body = Lines(0, 400);
        var first = AutocompletePromptBuilder.Build(Request(body + "    x", "\n}\n", caretLine: 400), 1024, PerChar, includeHeader: false);
        var second = AutocompletePromptBuilder.Build(Request(body + "    xy", "\n}\n", caretLine: 400), 1024, PerChar, includeHeader: false);

        Assert.True(first.PrefixLinesDropped > 0);
        Assert.Equal(0, first.PrefixLinesDropped % AutocompletePromptBuilder.AnchorLines);
        Assert.StartsWith(first.PromptPrefix, second.PromptPrefix, StringComparison.Ordinal);
    }

    [Fact]
    public void ConsecutiveKeystrokes_ChangeOnlyTheTail()
    {
        // Steady typing on a long file: each prompt starts with the whole previous prompt prefix, so a resident model's
        // cached prompt is reused up to the caret.
        var body = Lines(0, 700);
        var typed = "        var result = calculator.Add(first, second);";
        string? previous = null;
        var reused = 0;
        for (var i = 1; i <= typed.Length; i++)
        {
            var request = Request(body + typed[..i], "\n    }\n}\n", caretLine: 700);
            request.EnclosingDeclaration = "void Compute()";
            request.OpenDocuments = ["Calculator.cs"];
            var prompt = AutocompletePromptBuilder.Build(request, 1024, PerChar).PromptPrefix;
            if (previous is not null && prompt.StartsWith(previous, StringComparison.Ordinal))
            {
                reused++;
            }

            previous = prompt;
        }

        // The start moves by one anchor step only when the prefix outgrows the budget; nearly every keystroke reuses.
        Assert.True(reused >= typed.Length - 3, $"{reused} of {typed.Length - 1}");
    }

    [Fact]
    public void ALongCaretLine_IsCutFromTheStart()
    {
        var line = new string('a', 5000) + "END";
        var prompt = AutocompletePromptBuilder.Build(Request(line, string.Empty, 0), 1024, PerChar);

        Assert.True(prompt.CaretLineCut);
        Assert.EndsWith("END", prompt.Prefix, StringComparison.Ordinal);
        Assert.True(prompt.PromptTokens <= 1024);
    }

    [Fact]
    public void ExplicitTemplate_FramingIsCounted()
    {
        var format = PromptTemplates.Default.FindFimFormat("starcoder2:3b")!;

        var prompt = AutocompletePromptBuilder.Build(Request("x = ", ";", 0), 1024, PerChar, format);

        Assert.Equal("<fim_prefix><fim_suffix><fim_middle>".Length, prompt.TemplateTokens);
    }

    [Fact]
    public void ExactCounter_IsReported()
    {
        var prompt = AutocompletePromptBuilder.Build(Request("x = ", ";", 0), 2048, TokenCounter.Default.ForModel("gpt-4o"));

        Assert.True(prompt.IsExact);
    }

    internal static CompletionRequest Request(string prefix, string suffix, int caretLine) => new()
    {
        RequestId = "r",
        Prefix = prefix,
        Suffix = suffix,
        CaretLine = caretLine,
        LanguageId = "csharp",
        FilePath = @"C:\src\Calc.cs",
        TaskClass = TaskClass.Autocomplete,
    };

    /// <summary>Lines "lineNNNN pad;\n" (14 characters each) numbered from <paramref name="first"/>.</summary>
    internal static string Lines(int first, int count)
    {
        var builder = new StringBuilder();
        for (var i = first; i < first + count; i++)
        {
            builder.Append("line").Append(i.ToString("D4", System.Globalization.CultureInfo.InvariantCulture)).Append(" pad;\n");
        }

        return builder.ToString();
    }
}
