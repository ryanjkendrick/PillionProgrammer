namespace Pillion.ModelGateway.Tests.Autocomplete;

public sealed class ShapingAndTemplateTests
{
    [Fact]
    public void SingleLine_EndsBeforeTheFirstNewline()
    {
        var shaper = new CompletionShaper(["\n"], maxLines: 1);

        var shown = shaper.Push("a + b;") + shaper.Push("\n    return") + shaper.Push(" c;");

        Assert.Equal("a + b;", shown);
        Assert.True(shaper.IsComplete);
        Assert.Equal(string.Empty, shaper.Flush());
    }

    [Fact]
    public void SingleLine_WithoutANewlineStop_IsStillCutByTheLineLimit()
    {
        var shaper = new CompletionShaper([], maxLines: 1);

        Assert.Equal("x = 1;", shaper.Push("x = 1;\ny = 2;"));
        Assert.True(shaper.IsComplete);
    }

    [Fact]
    public void MultiLine_AllowsUpTo30Lines()
    {
        var shaper = new CompletionShaper(["\n\n\n"], AutocompleteBudget.MultiLineMaxLines);
        var input = string.Concat(Enumerable.Range(1, 40).Select(i => $"line {i}\n"));

        var shown = string.Concat(input.Chunk(7).Select(c => shaper.Push(new string(c)))) + shaper.Flush();

        var lines = shown.Split('\n');
        Assert.Equal(30, lines.Length);
        Assert.Equal("line 30", lines[^1]);
        Assert.True(shaper.IsComplete);
    }

    [Fact]
    public void MultiLine_ShortCompletion_FlushesEverything()
    {
        var shaper = new CompletionShaper(["\n\n\n"], AutocompleteBudget.MultiLineMaxLines);

        var shown = shaper.Push("if (x)\n{\n") + shaper.Push("    y();\n}\n\n") + shaper.Flush();

        Assert.Equal("if (x)\n{\n    y();\n}\n\n", shown);
        Assert.False(shaper.IsComplete);
    }

    [Fact]
    public void StopSequence_SplitAcrossPieces_IsHeldBackAndRemoved()
    {
        var shaper = new CompletionShaper(["<|endoftext|>", "// END"], maxLines: 30);

        var first = shaper.Push("return x; <|endo");
        var second = shaper.Push("ftext|> trailing");

        Assert.Equal("return x; ", first);
        Assert.Equal(string.Empty, second);
        Assert.Equal("return x; ", shaper.Text);
        Assert.True(shaper.IsComplete);
    }

    [Fact]
    public void HeldBackText_ThatIsNotAStop_IsReleased()
    {
        var shaper = new CompletionShaper(["<|endoftext|>"], maxLines: 30);

        Assert.Equal("a ", shaper.Push("a <|"));
        Assert.Equal("<|b", shaper.Push("b"));
        Assert.Equal(string.Empty, shaper.Flush());
    }

    [Theory]
    [InlineData("qwen2.5-coder:7b", "qwen2.5-coder", "<|fim_prefix|>P<|fim_suffix|>S<|fim_middle|>")]
    [InlineData("Qwen/Qwen2.5-Coder-1.5B", "qwen2.5-coder", "<|fim_prefix|>P<|fim_suffix|>S<|fim_middle|>")]
    [InlineData("codellama:7b-code", "codellama", "<PRE> P <SUF>S <MID>")]
    [InlineData("starcoder2:3b", "starcoder2", "<fim_prefix>P<fim_suffix>S<fim_middle>")]
    [InlineData("deepseek-coder:6.7b-base", "deepseek-coder", "<｜fim▁begin｜>P<｜fim▁hole｜>S<｜fim▁end｜>")]
    [InlineData("codestral-latest", "codestral", "[SUFFIX]S[PREFIX]P")]
    public void FimFormat_PerFamily_LaysOutTheTokens(string modelId, string family, string expected)
    {
        var format = PromptTemplates.Default.FindFimFormat(modelId);

        Assert.NotNull(format);
        Assert.Equal(family, format.Family);
        Assert.Equal(expected, format.Render("P", "S"));
        Assert.NotEmpty(format.StopSequences);
    }

    [Fact]
    public void FimFormat_UnknownFamily_HasNone() => Assert.Null(PromptTemplates.Default.FindFimFormat("gemma4:e2b"));

    [Fact]
    public void Templates_HaveSingleAndMultiLineDefaults()
    {
        Assert.Equal(64, PromptTemplates.Default.SingleLineMaxTokens);
        Assert.Equal(256, PromptTemplates.Default.MultiLineMaxTokens);
        Assert.Equal(128, PromptTemplates.Default.DefaultFimMaxTokens);
    }
}
