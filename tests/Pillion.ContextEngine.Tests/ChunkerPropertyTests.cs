using System.Text;
using CsCheck;
using Pillion.ContextEngine.Chunking;

namespace Pillion.ContextEngine.Tests;

/// <summary>
/// Property-based tests (design section 15): for random and damaged inputs in every language, chunks cover every
/// line exactly once (at least once, with the configured overlap, for windows), line ranges are inside the file, every
/// chunk is within the cap after splitting, token counts are exact, breadcrumbs are well formed, and nothing throws.
/// </summary>
public sealed class ChunkerPropertyTests
{
    private const long Iterations = 200;

    private static readonly string[] s_extensions =
    [
        ".cs", ".vb", ".razor", ".cshtml", ".ts", ".tsx", ".js", ".py", ".c", ".cpp", ".h", ".go", ".rs", ".java",
        ".sql", ".html", ".css", ".txt", ".md",
    ];

    private static readonly string[] s_tokens =
    [
        "class", "struct", "namespace", "void", "def", "fn", "func", "impl", "package", "import", "using", "public",
        "return", "if", "else", "for", "BEGIN", "END", "GO", "SELECT", "CASE", "End Class", "Sub", "Function",
        "{", "}", "(", ")", "[", "]", "<", ">", ";", ":", ",", ".", "=", "=>", "->", "\"", "'", "`", "$\"", "@\"", "\"\"\"",
        "//", "/*", "*/", "--", "#", "#include", "#ifdef", "#endif", "///", "'''", "@code", "@{", "@functions", "@*", "*@",
        "<div>", "</div>", "<script>", "</script>", "<!--", "-->", "@media", "x", "Name", "value_1", "getValue", "42",
        "3.14", "é", "日本語", "😀", "\t", "    ", "\u00a0",
    ];

    private static readonly string[] s_breaks = ["\n", "\r\n", "\r", "\u2028", "\u0085"];

    private static readonly string[] s_fixtures =
    [
        "csharp/Greeter.cs", "vb/Greeter.vb", "typescript/shapes.ts", "javascript/app.js", "python/inventory.py",
        "c/list.c", "cpp/matrix.hpp", "go/server.go", "rust/geometry.rs", "java/OrderService.java", "sql/schema.sql",
        "html/index.html", "css/site.css", "razor/Counter.razor", "razor/Index.cshtml",
    ];

    /// <summary>Lines of code-like tokens with mixed line breaks, the occasional very long line, any extension.</summary>
    private static readonly Gen<(string Path, string Text, int Cap)> s_randomFiles =
        Gen.Select(
            Gen.OneOfConst(s_extensions),
            Gen.Select(Gen.OneOfConst(s_tokens).Array[0, 14], Gen.OneOfConst(s_breaks), Gen.Int[0, 30])
                .Select((tokens, lineBreak, longLine) => string.Join(longLine == 0 ? "" : " ", longLine == 0 ? Enumerable.Repeat(string.Join(" ", tokens), 60) : tokens) + lineBreak)
                .Array[0, 90],
            Gen.Int[ChunkOptions.MinimumMaxTokens, 600],
            (extension, lines, cap) => ("dir/sub/file" + extension, string.Concat(lines), cap));

    /// <summary>A real fixture with random edits: characters deleted, duplicated or replaced by structural tokens.</summary>
    private static readonly Gen<(string Path, string Text, int Cap)> s_damagedFixtures =
        Gen.Select(
            Gen.OneOfConst(s_fixtures),
            Gen.Select(Gen.Int[0, 1_000_000].Select(i => i / 1_000_000.0), Gen.Int[0, 3], Gen.OneOfConst(s_tokens)).Array[0, 25],
            Gen.Int[ChunkOptions.MinimumMaxTokens, 600],
            (fixture, edits, cap) =>
            {
                var text = new StringBuilder(Fixture.Read(fixture));
                foreach (var (where, operation, token) in edits)
                {
                    if (text.Length == 0)
                    {
                        break;
                    }

                    var position = (int)(where * (text.Length - 1));
                    var length = Math.Min(1 + (int)(where * 40), text.Length - position);
                    switch (operation)
                    {
                        case 0:
                            text.Remove(position, length);
                            break;
                        case 1:
                            text.Insert(position, text.ToString(position, length));
                            break;
                        default:
                            text.Insert(position, token);
                            break;
                    }
                }

                return ("src/" + Path.GetFileName(fixture), text.ToString(), cap);
            });

    [Fact]
    public void Random_files_satisfy_the_chunk_invariants() =>
        s_randomFiles.Sample(Check, iter: Iterations, print: Print);

    [Fact]
    public void Damaged_fixtures_satisfy_the_chunk_invariants() =>
        s_damagedFixtures.Sample(Check, iter: Iterations, print: Print);

    [Fact]
    public void Intact_fixtures_satisfy_the_invariants_at_any_cap() =>
        Gen.Select(Gen.OneOfConst(s_fixtures), Gen.Int[ChunkOptions.MinimumMaxTokens, 300])
            .Sample((fixture, cap) => Check(("src/" + Path.GetFileName(fixture), Fixture.Read(fixture), cap)), iter: Iterations);

    [Fact]
    public void Windows_overlap_by_the_configured_lines()
    {
        Gen.Select(Gen.Int[1, 400], Gen.Int[2, 80], Gen.Int[0, 79])
            .Where((_, height, overlap) => overlap < height)
            .Sample((lines, height, overlap) =>
            {
                var text = string.Join("\n", Enumerable.Range(1, lines).Select(i => "l" + i));
                var options = ChunkOptions.Default with { WindowLines = height, WindowOverlap = overlap, MaxTokens = 100_000 };
                var chunks = new WindowChunker().Chunk("w.txt", text, options);

                Assert.Null(ChunkInvariants.Check("w.txt", text, chunks, options));
                Assert.Equal(1, chunks[0].StartLine);
                Assert.Equal(lines, chunks[^1].EndLine);
                for (var i = 1; i < chunks.Count; i++)
                {
                    Assert.Equal(chunks[i - 1].EndLine - overlap + 1, chunks[i].StartLine);
                    Assert.Equal(height, chunks[i - 1].EndLine - chunks[i - 1].StartLine + 1);
                }
            }, iter: Iterations);
    }

    private static void Check((string Path, string Text, int Cap) input)
    {
        var options = ChunkOptions.Default with { MaxTokens = input.Cap };
        var chunks = new FileChunker().Chunk(input.Path, input.Text, options);
        var failure = ChunkInvariants.Check(input.Path, input.Text, chunks, options);
        Assert.True(failure is null, failure + Environment.NewLine + ChunkInvariants.Describe(chunks));
    }

    private static string Print((string Path, string Text, int Cap) input) =>
        $"{input.Path} cap {input.Cap}:{Environment.NewLine}{input.Text}";
}
