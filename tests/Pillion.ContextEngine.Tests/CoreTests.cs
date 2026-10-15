using System.Text;
using Pillion.ContextEngine.Chunking;
using Pillion.ContextEngine.Chunking.Core;

namespace Pillion.ContextEngine.Tests;

public sealed class CoreTests
{
    [Fact]
    public void Window_chunker_uses_60_lines_with_10_of_overlap_and_the_path_as_breadcrumb()
    {
        var text = string.Join("\n", Enumerable.Range(1, 200).Select(i => $"line {i}"));

        var chunks = new WindowChunker().Chunk("docs/notes.md", text, ChunkOptions.Default);

        ChunkInvariants.Assert("docs/notes.md", text, chunks, ChunkOptions.Default);
        Assert.Equal([(1, 60), (51, 110), (101, 160), (151, 200)], chunks.Select(c => (c.StartLine, c.EndLine)));
        Assert.All(chunks, c => Assert.Equal("docs/notes.md", c.Breadcrumb));
        Assert.All(chunks, c => Assert.Equal(ChunkKind.Window, c.Kind));
    }

    [Fact]
    public void Unknown_extensions_use_windows()
    {
        var chunks = Fixture.Chunk("text/notes.txt");

        Assert.Equal("src/notes.txt", Assert.Single(chunks).Breadcrumb);
        Assert.Equal(LanguageIds.Text, chunks[0].Language);
        Assert.Equal(LanguageIds.Text, new FileChunker().LanguageOf("README"));
        Assert.Equal(LanguageIds.Cpp, new FileChunker().LanguageOf(@"C:\src\Thing.H"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \n\t\r\n  ")]
    public void Blank_text_has_no_chunks(string text)
    {
        Assert.Empty(new FileChunker().Chunk("a.cs", text, ChunkOptions.Default));
        Assert.Empty(new FileChunker().Chunk("a.txt", text, ChunkOptions.Default));
    }

    [Fact]
    public void Line_breaks_of_every_kind_number_lines_like_the_editor()
    {
        var document = new SourceDocument("a", "one\r\ntwo\rthree\nfour\u2028five\u0085six\n");

        Assert.Equal(6, document.LineCount);
        Assert.Equal("three", document.LineSpan(3).ToString());
        Assert.Equal("six", document.LineSpan(6).ToString());
        Assert.Equal(2, document.LineOfOffset(5));
        Assert.Equal(1, document.LineOfOffset(4)); // the \n of the first \r\n
    }

    [Fact]
    public void A_single_line_over_the_cap_is_truncated_but_keeps_its_line()
    {
        var text = "short\n" + new string('x', 20_000) + " " + string.Concat(Enumerable.Repeat("word ", 5_000)) + "\nend";
        var options = ChunkOptions.Default with { MaxTokens = 64 };

        var chunks = new WindowChunker().Chunk("big.txt", text, options);

        ChunkInvariants.Assert("big.txt", text, chunks, options);
        var truncated = Assert.Single(chunks, c => c.IsTruncated);
        Assert.Equal(2, truncated.StartLine);
        Assert.Equal(2, truncated.EndLine);
    }

    [Fact]
    public void Long_paths_are_shortened_to_fit_the_breadcrumb_budget()
    {
        var path = string.Join("/", Enumerable.Repeat("a-very-long-folder-name-that-goes-on", 30)) + "/File.cs";
        const string Text = "class C { void M() { } }";
        var options = ChunkOptions.Default with { MaxTokens = 64 };

        var chunks = new FileChunker().Chunk(path, Text, options);

        ChunkInvariants.Assert(path, Text, chunks, options);
        Assert.StartsWith("File.cs > C", Assert.Single(chunks).Breadcrumb, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("a.cs > N > C > M", true)]
    [InlineData("a.cs > N > C > M (part 2 of 3)", true)]
    [InlineData("a.cs", true)]
    [InlineData("a.cs >  > M", false)]
    [InlineData("a.cs > M (part 4 of 3)", false)]
    [InlineData("a.cs\n> M", false)]
    [InlineData("", false)]
    public void Breadcrumb_format_is_checked(string breadcrumb, bool wellFormed) =>
        Assert.Equal(wellFormed, Breadcrumbs.IsWellFormed(breadcrumb));

    [Fact]
    public void Breadcrumb_segments_never_contain_the_separator()
    {
        var breadcrumb = Breadcrumbs.Format("a b > c.cs", ["operator >", "x\n  > y", "  ", "List<T>"]);

        Assert.True(Breadcrumbs.IsWellFormed(breadcrumb), breadcrumb);
        Assert.True(Breadcrumbs.TryParse(breadcrumb, out var segments, out _, out _));
        Assert.Equal(["a b> c.cs", "operator>", "x> y", "List<T>"], segments);
    }

    [Fact]
    public void Approximate_counter_counts_words_punctuation_and_special_tokens()
    {
        var count = TokenCounters.ApproximateWordPiece;

        Assert.Equal(2, count(""));
        Assert.Equal(3, count("abc"));
        Assert.Equal(2 + 6, count("a.b(c)"));
        Assert.Equal(2 + 4, count("GetCustomer")); // 11 characters: 1 + (11 - 4 + 2) / 3 = 4
        Assert.Equal(count("one two") + count("three") - 2, count("one two\nthree"));
    }

    [Fact]
    public void WordPiece_vocabulary_counter_uses_the_bert_tokenizer()
    {
        var vocabulary = string.Join("\n", "[PAD]", "[UNK]", "[CLS]", "[SEP]", "[MASK]", "get", "##customer", "by", "##id", "(", ")", "x");
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(vocabulary));

        var count = TokenCounters.FromWordPieceVocabulary(stream);

        var empty = count("");
        Assert.Equal(empty + 4, count("getCustomer byId"));
        Assert.Equal(empty + 3, count("(x)"));
        Assert.Equal(empty + 1, count("unknownword"));
    }

    [Fact]
    public void Chunking_with_the_wordpiece_counter_respects_the_cap()
    {
        var vocabulary = string.Join("\n", ["[PAD]", "[UNK]", "[CLS]", "[SEP]", "[MASK]", .. "abcdefghijklmnopqrstuvwxyz".Select(c => c.ToString()), .. "abcdefghijklmnopqrstuvwxyz".Select(c => "##" + c), "{", "}", "(", ")", ";", "=", "."]);
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(vocabulary));
        var options = ChunkOptions.Default with { TokenCounter = TokenCounters.FromWordPieceVocabulary(stream), MaxTokens = 48 };
        var text = Fixture.Read("csharp/Greeter.cs");

        var chunks = new FileChunker().Chunk("Greeter.cs", text, options);

        ChunkInvariants.Assert("Greeter.cs", text, chunks, options);
    }

    [Fact]
    public void Options_are_validated()
    {
        var chunker = new FileChunker();

        Assert.Throws<ArgumentOutOfRangeException>(() => chunker.Chunk("a.cs", "x", ChunkOptions.Default with { MaxTokens = 8 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => chunker.Chunk("a.cs", "x", ChunkOptions.Default with { WindowOverlap = 60 }));
    }

    [Fact]
    public void A_throwing_fallback_callback_does_not_stop_chunking()
    {
        var options = ChunkOptions.Default with { OnFallback = _ => throw new InvalidOperationException("boom") };
        var text = string.Join("\n", Enumerable.Repeat(")))) (((( ;;;; class class", 100));

        var chunks = new FileChunker().Chunk("a.cs", text, options);

        Assert.NotEmpty(chunks);
    }
}
