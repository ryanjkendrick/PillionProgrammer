using System.Text;
using CsCheck;
using Pillion.ContextEngine.Symbols;

namespace Pillion.ContextEngine.Tests.Symbols;

/// <summary>
/// Property-based tests (design section 15): for random and damaged inputs in every language, extraction never throws,
/// every definition's and reference's lines are inside the file, and the graph and the map built from the results are
/// consistent and within budget.
/// </summary>
public sealed class SymbolPropertyTests
{
    private const long Iterations = 200;

    private static readonly string[] s_extensions =
    [
        ".cs", ".vb", ".razor", ".cshtml", ".ts", ".tsx", ".js", ".py", ".c", ".cpp", ".h", ".go", ".rs", ".java",
        ".sql", ".html", ".css", ".txt",
    ];

    private static readonly string[] s_tokens =
    [
        "class", "struct", "interface", "enum", "namespace", "module", "void", "def", "fn", "func", "impl", "trait",
        "package", "import", "using", "from", "public", "static", "new", "extends", "implements", "return", "if",
        "CREATE TABLE", "CREATE PROCEDURE", "FROM", "JOIN", "UPDATE", "INSERT INTO", "EXEC", "BEGIN", "END", "GO",
        "End Class", "Sub", "Function", "Inherits", "@code", "@{", "@inherits", "<Counter />", "#include", "#define",
        "{", "}", "(", ")", "[", "]", "<", ">", "::", ";", ":", ",", ".", "=", "=>", "->", "\"", "'", "`", "//", "/*",
        "*/", "--", "#", "<div id=\"x\" class=\"a b\">", "</div>", ".card", "#top", "Order", "Order.Total", "self",
        "this", "Foo()", "x.y()", "42", "é", "日本語", "😀", "\t", "    ", "\u00a0",
    ];

    private static readonly string[] s_breaks = ["\n", "\r\n", "\r", "\u2028", "\u0085"];

    private static readonly string[] s_fixtures =
    [
        "csharp/Greeter.cs", "vb/Greeter.vb", "typescript/shapes.ts", "javascript/app.js", "python/inventory.py",
        "c/list.c", "cpp/matrix.hpp", "go/server.go", "rust/geometry.rs", "java/OrderService.java", "sql/schema.sql",
        "html/index.html", "css/site.css", "razor/Counter.razor", "razor/Index.cshtml",
    ];

    private static readonly Gen<(string Path, string Text)> s_randomFiles =
        Gen.Select(
            Gen.OneOfConst(s_extensions),
            Gen.Select(Gen.OneOfConst(s_tokens).Array[0, 14], Gen.OneOfConst(s_breaks))
                .Select((tokens, lineBreak) => string.Join(" ", tokens) + lineBreak)
                .Array[0, 80],
            (extension, lines) => ("dir/sub/file" + extension, string.Concat(lines)));

    private static readonly Gen<(string Path, string Text)> s_damagedFixtures =
        Gen.Select(
            Gen.OneOfConst(s_fixtures),
            Gen.Select(Gen.Int[0, 1_000_000].Select(i => i / 1_000_000.0), Gen.Int[0, 3], Gen.OneOfConst(s_tokens)).Array[0, 25],
            (fixture, edits) =>
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

                return ("src/" + Path.GetFileName(fixture), text.ToString());
            });

    [Fact]
    public void Extraction_of_random_files_never_throws_and_stays_inside_the_file() =>
        s_randomFiles.Sample(Check, iter: Iterations, print: Print);

    [Fact]
    public void Extraction_of_damaged_fixtures_never_throws_and_stays_inside_the_file() =>
        s_damagedFixtures.Sample(Check, iter: Iterations, print: Print);

    [Fact]
    public void Graphs_and_maps_from_random_repositories_are_consistent_and_within_budget() =>
        Gen.Select(s_damagedFixtures.Array[1, 6], s_randomFiles.Array[0, 4], Gen.Int[0, 600])
            .Sample((damaged, random, budget) =>
            {
                var builder = new SymbolGraphBuilder();
                var index = 0;
                foreach (var (path, text) in damaged.Concat(random))
                {
                    // Unique paths, spread over two partitions.
                    builder.AddFile(new FileSymbolExtractor().Extract($"p{index % 2}/{index++}/{Path.GetFileName(path)}", text));
                }

                var graph = builder.Build();
                var ids = graph.Definitions.Select(d => d.Id).ToHashSet();
                Assert.Equal(graph.Definitions.Count, ids.Count);
                Assert.All(graph.Edges, edge =>
                {
                    Assert.Contains(edge.ToSymbolId, ids);
                    Assert.True(edge.FromSymbolId is null || ids.Contains(edge.FromSymbolId.Value));
                    Assert.InRange(edge.CandidateCount, 1, SymbolGraphOptions.Default.MaxCandidates);
                });
                var statistics = graph.Statistics;
                Assert.Equal(statistics.References, statistics.Resolved + statistics.Ambiguous + statistics.TooAmbiguous + statistics.Unresolved);

                var map = RepoMapRenderer.Render(graph, RepoMapOptions.Default with { MaxTokens = budget });
                Assert.True(map.TokenCount <= budget);
                Assert.Equal(map.Text, RepoMapRenderer.Render(graph, RepoMapOptions.Default with { MaxTokens = budget }).Text);
            }, iter: 60);

    private static void Check((string Path, string Text) input)
    {
        var failures = new List<SymbolExtractionFailure>();
        var extraction = new FileSymbolExtractor().Extract(input.Path, input.Text, new SymbolExtractionOptions { OnFailure = failures.Add });
        Assert.Equal(input.Path, extraction.FilePath);
        Assert.True(extraction.IsComplete || failures.Count == 1, "An incomplete extraction must report one failure.");
        var failure = SymbolFixture.Check(input.Path, input.Text, extraction);
        Assert.True(failure is null, failure + Environment.NewLine + SymbolFixture.Describe(extraction));
        Assert.All(extraction.Definitions, d => Assert.True(d.IsApproximate));
        Assert.Equal(new FileSymbolExtractor().LanguageOf(input.Path), extraction.Language);
    }

    private static string Print((string Path, string Text) input) => $"{input.Path}:{Environment.NewLine}{input.Text}";
}
