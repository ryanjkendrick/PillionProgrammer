using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using Pillion.VisualStudio.Core;
using Pillion.VisualStudio.Core.Markdown;
using Xunit.Abstractions;

namespace Pillion.VisualStudio.Tests;

public sealed class ChatMarkdownTests
{
    private readonly ITestOutputHelper _output;

    public ChatMarkdownTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void Parse_MapsEachMarkdownConstructToABlock()
    {
        var message = ChatMarkdownParser.Parse(
            "# Title\r\n\r\nSome **bold**, *italic*, ~~gone~~ and `code`.\n\n"
            + "- one\n- two\n\n3. three\n4. four\n\n"
            + "> quoted\n\n---\n\n"
            + "| A | B |\n| :-- | --: |\n| 1 | 2 |\n\n"
            + "```cs\nvar x = 1;\n```\n\n    indented();\n");

        Assert.Equal(
            [MarkdownBlockKind.Heading, MarkdownBlockKind.Paragraph, MarkdownBlockKind.List, MarkdownBlockKind.List, MarkdownBlockKind.Quote,
             MarkdownBlockKind.Rule, MarkdownBlockKind.Table, MarkdownBlockKind.Code, MarkdownBlockKind.Code],
            message.Blocks.Select(b => b.Kind));

        var heading = Assert.IsType<MarkdownHeading>(message.Blocks[0]);
        Assert.Equal(1, heading.Level);
        Assert.Equal("Title", heading.PlainText);

        var paragraph = Assert.IsType<MarkdownParagraph>(message.Blocks[1]);
        Assert.Equal(
            ["Text(Some )", "Strong(bold)", "Text(, )", "Emphasis(italic)", "Text(, )", "Strikethrough(gone)", "Text( and )", "Code(code)", "Text(.)"],
            paragraph.Inlines.Select(i => i.ToString()));

        var bullets = Assert.IsType<MarkdownList>(message.Blocks[2]);
        Assert.False(bullets.IsOrdered);
        Assert.Equal(["\u2022", "\u2022"], bullets.Items.Select(i => i.Marker));
        Assert.Equal("two", Assert.IsType<MarkdownParagraph>(bullets.Items[1].Blocks.Single()).PlainText);

        var numbers = Assert.IsType<MarkdownList>(message.Blocks[3]);
        Assert.True(numbers.IsOrdered);
        Assert.Equal(3, numbers.Start);
        Assert.Equal(["3.", "4."], numbers.Items.Select(i => i.Marker));

        Assert.Equal("quoted", Assert.IsType<MarkdownParagraph>(Assert.IsType<MarkdownQuote>(message.Blocks[4]).Blocks.Single()).PlainText);

        var table = Assert.IsType<MarkdownTable>(message.Blocks[6]);
        Assert.Equal(1, table.HeaderRows);
        Assert.Equal(2, table.ColumnCount);
        Assert.Equal([MarkdownColumnAlignment.Left, MarkdownColumnAlignment.Right], table.Alignments);
        Assert.Equal("2", MarkdownInline.ToPlainText(table.Rows[1][1]));

        var fenced = Assert.IsType<MarkdownCodeBlock>(message.Blocks[7]);
        Assert.Equal("var x = 1;", fenced.Code);
        Assert.Same(CodeLanguage.CSharp, fenced.Language);
        Assert.True(fenced.IsClosed);

        var indented = Assert.IsType<MarkdownCodeBlock>(message.Blocks[8]);
        Assert.Equal("indented();", indented.Code);
        Assert.Same(CodeLanguage.PlainText, indented.Language);
    }

    [Fact]
    public void Parse_KeepsCodeInsideListItemsAsCodeBlocks()
    {
        var message = ChatMarkdownParser.Parse("1. Run this:\n\n   ```powershell\n   Get-Date\n   ```\n2. Done.");

        var list = Assert.IsType<MarkdownList>(Assert.Single(message.Blocks));
        var code = Assert.IsType<MarkdownCodeBlock>(list.Items[0].Blocks[1]);
        Assert.Equal("Get-Date", code.Code);
        Assert.Same(CodeLanguage.PowerShell, code.Language);
    }

    [Fact]
    public void Parse_AnUnclosedFenceIsCodeToTheEndWhileItStreams()
    {
        var message = ChatMarkdownParser.Parse("Here:\n```python\nprint(1)\nprint(2");

        var code = Assert.IsType<MarkdownCodeBlock>(message.Blocks[message.Blocks.Count - 1]);
        Assert.Equal("print(1)\nprint(2", code.Code);
        Assert.False(code.IsClosed);
        Assert.Same(CodeLanguage.Python, code.Language);
    }

    [Fact]
    public void Parse_NeverInterpretsRawHtml()
    {
        var message = ChatMarkdownParser.Parse("<script>alert(1)</script>\n\nText with <b>tags</b>.");

        Assert.All(message.Blocks, b => Assert.IsType<MarkdownParagraph>(b));
        Assert.Equal("<script>alert(1)</script>", ((MarkdownParagraph)message.Blocks[0]).PlainText);
        Assert.Equal("Text with <b>tags</b>.", ((MarkdownParagraph)message.Blocks[1]).PlainText);
    }

    [Fact]
    public void Images_AreOnlyTheirAltTextAndCarryNoOpenableAddress()
    {
        var message = ChatMarkdownParser.Parse("See ![the *diagram*](https://example.com/d.png \"title\") and ![](http://x/y.png).");

        var inlines = Assert.IsType<MarkdownParagraph>(Assert.Single(message.Blocks)).Inlines;
        var images = inlines.Where(i => i.Kind == MarkdownInlineKind.Image).ToList();
        Assert.Equal(2, images.Count);
        Assert.Equal("the diagram", images[0].Text);
        Assert.Equal("https://example.com/d.png", images[0].Url);
        Assert.Null(images[0].SafeUri);
        Assert.Equal(string.Empty, images[1].Text);
        Assert.DoesNotContain(inlines, i => i.Kind == MarkdownInlineKind.Link);
        Assert.Equal("See the diagram and .", MarkdownInline.ToPlainText(inlines));
    }

    [Fact]
    public void Links_OnlyHttpHttpsAndMailtoAreOpenable()
    {
        var message = ChatMarkdownParser.Parse(
            "[web](https://example.com/a?b=c) [plain](http://example.com) [mail](mailto:dev@example.com) "
            + "[js](javascript:alert(1)) [file](file:///c:/windows/win.ini) [rel](docs/readme.md) [vb](vbscript:msgbox) "
            + "[data](data:text/html;base64,PHA+) [unc](\\\\server\\share) https://bare.example.com/x and dev@example.org");

        var links = Assert.IsType<MarkdownParagraph>(Assert.Single(message.Blocks)).Inlines
            .Where(i => i.Kind == MarkdownInlineKind.Link)
            .ToDictionary(i => MarkdownInline.ToPlainText(i.Children), i => i.SafeUri?.AbsoluteUri);

        Assert.Equal("https://example.com/a?b=c", links["web"]);
        Assert.Equal("http://example.com/", links["plain"]);
        Assert.Equal("mailto:dev@example.com", links["mail"]);
        Assert.Null(links["js"]);
        Assert.Null(links["file"]);
        Assert.Null(links["rel"]);
        Assert.Null(links["vb"]);
        Assert.Null(links["data"]);
        Assert.Equal("https://bare.example.com/x", links["https://bare.example.com/x"]);
        Assert.DoesNotContain("dev@example.org", links.Keys); // A bare address stays text.
    }

    [Theory]
    [InlineData("https://example.com", true)]
    [InlineData("HTTP://EXAMPLE.COM/path", true)]
    [InlineData("mailto:a@b.c", true)]
    [InlineData("  https://example.com  ", true)]
    [InlineData("https://exa mple.com", false)]
    [InlineData("https://example.com/\u0007", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("JavaScript:alert(1)", false)]
    [InlineData("file:///c:/x", false)]
    [InlineData("c:\\windows\\system32", false)]
    [InlineData("\\\\server\\share", false)]
    [InlineData("ftp://example.com", false)]
    [InlineData("ms-settings:privacy", false)]
    [InlineData("/relative", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void LinkSafety_AllowsOnlyWebAndMailAddresses(string? url, bool safe)
    {
        Assert.Equal(safe, LinkSafety.TryGetSafeUri(url, out var uri));
        Assert.Equal(safe, uri is not null);
    }

    [Fact]
    public void LinkSafety_DescribeShowsTheAsciiFormOfAnInternationalHost()
    {
        Assert.True(LinkSafety.TryGetSafeUri("https://b\u00fccher.example/x", out var uri));

        var text = LinkSafety.Describe(uri!);

        Assert.Contains("xn--bcher-kva.example", text, StringComparison.Ordinal);
        Assert.Equal("https://example.com/", LinkSafety.Describe(new Uri("https://example.com")));
    }

    [Theory]
    [InlineData("csharp", "csharp", "C#", "CSharp")]
    [InlineData("cs", "csharp", "C#", "CSharp")]
    [InlineData("C#", "csharp", "C#", "CSharp")]
    [InlineData("vb", "vb", "Visual Basic", "Basic")]
    [InlineData("fsharp", "fsharp", "F#", "F#")]
    [InlineData("ts", "typescript", "TypeScript", "TypeScript")]
    [InlineData("js", "javascript", "JavaScript", "JavaScript")]
    [InlineData("json", "json", "JSON", "JSON")]
    [InlineData("xml", "xml", "XML", "XML")]
    [InlineData("html", "html", "HTML", "HTMLX")]
    [InlineData("css", "css", "CSS", "CSS")]
    [InlineData("python", "python", "Python", "Python")]
    [InlineData("powershell", "powershell", "PowerShell", "PowerShell")]
    [InlineData("bash", "bash", "Bash", null)]
    [InlineData("sql", "sql", "SQL", "SQL Server Tools")]
    [InlineData("markdown", "markdown", "Markdown", "markdown")]
    [InlineData("{.python}", "python", "Python", "Python")]
    [InlineData("language-js", "javascript", "JavaScript", "JavaScript")]
    [InlineData("csharp title=\"x.cs\"", "csharp", "C#", "CSharp")]
    public void CodeLanguage_MapsCommonFenceTags(string info, string id, string name, string? contentType)
    {
        var language = CodeLanguage.FromFenceInfo(info);

        Assert.Equal(id, language.Id);
        Assert.Equal(name, language.NameFor(info));
        Assert.Equal(contentType, language.ContentTypes.Count == 0 ? null : language.ContentTypes[0]);
        Assert.False(language.IsPlainText);
        Assert.StartsWith(".", language.FileExtension, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("", "Plain text")]
    [InlineData("rust", "rust")]
    [InlineData("text", "Plain text")]
    public void CodeLanguage_UnknownOrMissingTagsArePlainText(string info, string name)
    {
        var language = CodeLanguage.FromFenceInfo(info);

        Assert.Same(CodeLanguage.PlainText, language);
        Assert.Equal(name, language.NameFor(info));
        Assert.Equal([CodeLanguage.PlainTextContentType], language.ContentTypes);
    }

    [Fact]
    public void RenderPlan_WhileStreamingOnlyTheTailBlockIsUpdated()
    {
        var before = ChatMarkdownParser.Parse("# Title\n\nFirst paragraph.\n\nSecond para");
        var after = ChatMarkdownParser.Parse("# Title\n\nFirst paragraph.\n\nSecond paragraph grows");

        var plan = MarkdownRenderPlan.Compare(before, after);

        Assert.Equal(new MarkdownRenderPlan(2, updateFirstInPlace: true, 3, 3), plan);
        Assert.Equal(0, plan.RenderedCount);
    }

    [Fact]
    public void RenderPlan_ANewBlockAfterTheTailIsAdded()
    {
        var before = ChatMarkdownParser.Parse("Intro.\n\n```csharp\nvar a = 1;\n```");
        var after = ChatMarkdownParser.Parse("Intro.\n\n```csharp\nvar a = 1;\n```\n\nThen this.");

        var plan = MarkdownRenderPlan.Compare(before, after);

        Assert.Equal(2, plan.FirstChanged);
        Assert.False(plan.UpdateFirstInPlace);
        Assert.Equal(1, plan.RenderedCount);
    }

    [Fact]
    public void RenderPlan_AGrowingCodeBlockIsUpdatedInPlaceUnlessItsLanguageChanges()
    {
        var before = ChatMarkdownParser.Parse("```csharp\nvar a");
        var grown = ChatMarkdownParser.Parse("```csharp\nvar a = 1;\nvar b");
        var closed = ChatMarkdownParser.Parse("```csharp\nvar a = 1;\nvar b = 2;\n```");
        var otherLanguage = ChatMarkdownParser.Parse("```python\nvar a");

        Assert.Equal(new MarkdownRenderPlan(0, true, 1, 1), MarkdownRenderPlan.Compare(before, grown));
        Assert.Equal(new MarkdownRenderPlan(0, true, 1, 1), MarkdownRenderPlan.Compare(grown, closed));
        Assert.Equal(new MarkdownRenderPlan(0, false, 1, 1), MarkdownRenderPlan.Compare(before, otherLanguage));
    }

    [Fact]
    public void RenderPlan_AParagraphThatBecomesATableOrHeadingIsReplaced()
    {
        var before = ChatMarkdownParser.Parse("Intro.\n\n| A | B |");
        var table = ChatMarkdownParser.Parse("Intro.\n\n| A | B |\n| - | - |");
        var setext = ChatMarkdownParser.Parse("Intro.\n\nTitle\n=====");

        Assert.Equal(new MarkdownRenderPlan(1, false, 2, 2), MarkdownRenderPlan.Compare(before, table));
        Assert.Equal(new MarkdownRenderPlan(1, false, 2, 2), MarkdownRenderPlan.Compare(ChatMarkdownParser.Parse("Intro.\n\nTitle"), setext));
    }

    [Fact]
    public void RenderPlan_AListThatGainsAnItemIsUpdatedInPlaceAndOnlyItsLastItemChanges()
    {
        var before = ChatMarkdownParser.Parse("- one\n- two\n- thr");
        var after = ChatMarkdownParser.Parse("- one\n- two\n- three\n- fo");

        var plan = MarkdownRenderPlan.Compare(before, after);
        Assert.Equal(new MarkdownRenderPlan(0, true, 1, 1), plan);

        var items = MarkdownRenderPlan.Compare(before.Blocks[0].Children, after.Blocks[0].Children);
        Assert.Equal(new MarkdownRenderPlan(2, true, 3, 4), items);
        Assert.Equal(1, items.RenderedCount);
    }

    [Fact]
    public void RenderPlan_UnchangedTextChangesNothingAndANewDefinitionRerendersEverything()
    {
        var text = "See [the docs][d].\n\nMore.";
        Assert.True(MarkdownRenderPlan.Compare(ChatMarkdownParser.Parse(text), ChatMarkdownParser.Parse(text)).IsUnchanged);

        var withDefinition = ChatMarkdownParser.Parse(text + "\n\n[d]: https://example.com");
        var plan = MarkdownRenderPlan.Compare(ChatMarkdownParser.Parse(text), withDefinition);

        Assert.Equal(0, plan.FirstChanged);
        Assert.Equal(MarkdownInlineKind.Link, ((MarkdownParagraph)withDefinition.Blocks[0]).Inlines[1].Kind);
    }

    [Fact]
    public void Streaming_ParsingEveryPrefixNeverThrowsAndEndsWithTheFullParse()
    {
        var reply = LongReply();
        MarkdownMessage previous = MarkdownMessage.Empty;
        for (var length = 1; length <= reply.Length; length += 7)
        {
            var next = ChatMarkdownParser.Parse(reply.Substring(0, length));
            var plan = MarkdownRenderPlan.Compare(previous, next);
            Assert.InRange(plan.FirstChanged, 0, Math.Min(previous.Blocks.Count, next.Blocks.Count));
            previous = next;
        }

        var full = ChatMarkdownParser.Parse(reply);
        Assert.True(MarkdownRenderPlan.Compare(ChatMarkdownParser.Parse(reply), full).IsUnchanged);
        Assert.Contains(full.Blocks, b => b.Kind == MarkdownBlockKind.Table);
    }

    /// <summary>
    /// The incremental-render budget's model half: parsing the text so far and planning the update, per chunk, for a
    /// 2,000-token reply streamed in the fake host's 14-character chunks (each one treated as its own flush, the worst
    /// case; the view model batches chunks every 50 ms).
    /// </summary>
    [Fact]
    public void Streaming_ParseAndPlanPerChunkStaysWellUnderTwoMillisecondsForA2000TokenReply()
    {
        var reply = LongReply();
        Assert.InRange(reply.Length, 7900, 9000);

        // Warm up the pipeline and the JIT.
        for (var i = 0; i < 3; i++)
        {
            MarkdownRenderPlan.Compare(ChatMarkdownParser.Parse(reply.Substring(0, reply.Length / 2)), ChatMarkdownParser.Parse(reply));
        }

        var timings = new List<double>();
        var previous = MarkdownMessage.Empty;
        var watch = new Stopwatch();
        for (var length = 14; length < reply.Length + 14; length += 14)
        {
            var text = reply.Substring(0, Math.Min(length, reply.Length));
            watch.Restart();
            var next = ChatMarkdownParser.Parse(text);
            _ = MarkdownRenderPlan.Compare(previous, next);
            watch.Stop();
            timings.Add(watch.Elapsed.TotalMilliseconds);
            previous = next;
        }

        timings.Sort();
        var median = timings[timings.Count / 2];
        var p95 = timings[(int)(timings.Count * 0.95)];
        _output.WriteLine(string.Format(
            CultureInfo.InvariantCulture,
            "{0} chunks over {1} characters: median {2:0.000} ms, p95 {3:0.000} ms, max {4:0.000} ms, mean {5:0.000} ms",
            timings.Count,
            reply.Length,
            median,
            p95,
            timings[timings.Count - 1],
            timings.Average()));
        Assert.True(median < 2.0, "median parse and plan per chunk " + median.ToString("0.000", CultureInfo.InvariantCulture) + " ms");
    }

    [Fact]
    public void Turn_ParsesItsTextLazilyAndAgainAfterEachAppend()
    {
        var turn = ChatTurnViewModel.ForAssistant(ModelOption.Build([TestModels.Local], ChatModelPolicy.Default).Single());
        var changed = new List<string?>();
        turn.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        turn.Append("Hello *wor");
        var first = turn.Markdown;
        Assert.Same(first, turn.Markdown);
        turn.Append("ld*");

        Assert.Contains(nameof(ChatTurnViewModel.Markdown), changed);
        Assert.NotSame(first, turn.Markdown);
        Assert.Equal("Emphasis(world)", ((MarkdownParagraph)turn.Markdown.Blocks[0]).Inlines[1].ToString());
    }

    [Fact]
    public void ChatStrings_EveryStringResolvesFromTheResourceFile()
    {
        var properties = typeof(ChatStrings).GetProperties().Where(p => p.PropertyType == typeof(string)).ToList();

        Assert.True(properties.Count > 30);
        foreach (var property in properties)
        {
            var value = (string)property.GetValue(null)!;
            Assert.False(string.IsNullOrWhiteSpace(value), property.Name);
            Assert.NotEqual(property.Name, value);
        }

        Assert.Equal("C# code block", ChatStrings.Format(ChatStrings.CodeBlockNameFormat, "C#"));
    }

    /// <summary>The same shape of reply as the fake host's <c>/long</c> trigger: about 2,000 tokens of mixed Markdown.</summary>
    internal static string LongReply()
    {
        var builder = new StringBuilder("# A long answer from Fake Local Coder\n\n");
        for (var section = 1; builder.Length < 8000; section++)
        {
            var n = section.ToString(CultureInfo.InvariantCulture);
            builder.Append("## Part ").Append(n).Append("\n\n")
                .Append("This part explains step ").Append(n).Append(" in some detail, with **strong text**, *emphasis*, ")
                .Append("`InlineCode").Append(n).Append("()` and a [reference](https://example.com/docs/part-").Append(n)
                .Append("). The paragraph is long enough to wrap across several lines in a narrow tool window, which is ")
                .Append("what a real model's answer usually looks like.\n\n")
                .Append("- First point of part ").Append(n).Append("\n- Second point, with `code`\n- Third point\n\n")
                .Append("```csharp\npublic sealed class Part").Append(n).Append("\n{\n    public int Value { get; } = ")
                .Append(n).Append(";\n\n    public override string ToString() => \"Part ").Append(n).Append("\";\n}\n```\n\n");
            if (section % 3 == 0)
            {
                builder.Append("| Name | Value |\n| --- | ---: |\n| Part ").Append(n).Append(" | ").Append(n).Append(" |\n| Total | ")
                    .Append((section * 10).ToString(CultureInfo.InvariantCulture)).Append(" |\n\n");
            }
        }

        builder.Append("That is the end of the long answer.");
        return builder.ToString();
    }
}
