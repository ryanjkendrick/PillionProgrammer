using System.Text;
using Pillion.ContextEngine.Chunking;
using Pillion.ContextEngine.Chunking.TreeSitter;

namespace Pillion.ContextEngine.Tests;

public sealed class TreeSitterChunkerTests
{
    [Fact]
    public void Every_shipped_grammar_loads_on_this_runtime()
    {
        foreach (var grammar in new[]
        {
            TreeSitterGrammars.TypeScript, TreeSitterGrammars.Tsx, TreeSitterGrammars.JavaScript, TreeSitterGrammars.Python,
            TreeSitterGrammars.C, TreeSitterGrammars.Cpp, TreeSitterGrammars.Go, TreeSitterGrammars.Rust,
            TreeSitterGrammars.Java, TreeSitterGrammars.Css, TreeSitterGrammars.Html,
        })
        {
            Assert.True(new TreeSitterChunker(grammar).IsAvailable, grammar.GrammarId);
        }
    }

    [Fact]
    public void TypeScript_classes_interfaces_enums_namespaces_and_arrow_functions()
    {
        var chunks = Fixture.Chunk("typescript/shapes.ts");

        var area = Fixture.Single(chunks, "src/shapes.ts > Circle > area");
        Assert.Equal(ChunkKind.Member, area.Kind);
        Assert.Contains("/** The area. */", area.Text, StringComparison.Ordinal);
        Assert.Equal(LanguageIds.TypeScript, area.Language);

        var header = Fixture.Single(chunks, "src/shapes.ts > Circle");
        Assert.Equal(ChunkKind.TypeHeader, header.Kind);
        Assert.Contains("/** A circle. */", header.Text, StringComparison.Ordinal);
        Assert.Contains("export class Circle implements Shape {", header.Text, StringComparison.Ordinal);

        Assert.Equal(ChunkKind.TypeHeader, Fixture.Single(chunks, "src/shapes.ts > Colour").Kind);
        Assert.Equal(ChunkKind.Member, Fixture.Single(chunks, "src/shapes.ts > describe").Kind);
        Assert.Equal(ChunkKind.Member, Fixture.Single(chunks, "src/shapes.ts > Geometry > unit").Kind);
        Fixture.Single(chunks, "src/shapes.ts > Shape > area");
        Assert.Equal(ChunkKind.Document, chunks[0].Kind);
        Assert.Equal(ChunkKind.Document, chunks[^1].Kind);
    }

    [Fact]
    public void JavaScript_functions_classes_and_fields()
    {
        var chunks = Fixture.Chunk("javascript/app.js");

        var router = Fixture.Single(chunks, "src/app.js > buildRouter");
        Assert.StartsWith("// Builds the router.", router.Text, StringComparison.Ordinal);
        Fixture.Single(chunks, "src/app.js > Store > add");
        Fixture.Single(chunks, "src/app.js > Store > items");
        Fixture.Single(chunks, "src/app.js > handler");
    }

    [Fact]
    public void Python_classes_keep_decorators_and_docstrings_in_the_header()
    {
        var chunks = Fixture.Chunk("python/inventory.py");

        var header = Fixture.Single(chunks, "src/inventory.py > Item");
        Assert.StartsWith("@dataclass", header.Text.TrimStart(), StringComparison.Ordinal);
        Assert.Contains("\"\"\"An item in stock.\"\"\"", header.Text, StringComparison.Ordinal);
        Assert.Contains("quantity: int = 0", header.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("def restock", header.Text, StringComparison.Ordinal);

        var empty = Fixture.Single(chunks, "src/inventory.py > Item > empty");
        Assert.StartsWith("    @property", empty.Text, StringComparison.Ordinal);
        Fixture.Single(chunks, "src/inventory.py > load");
    }

    [Fact]
    public void C_and_Cpp_functions_types_and_namespaces()
    {
        var c = Fixture.Chunk("c/list.c");
        Assert.Contains("/* Pushes a value. */", Fixture.Single(c, "src/list.c > list_push").Text, StringComparison.Ordinal);
        Fixture.Single(c, "src/list.c > list_free");
        Assert.Equal(ChunkKind.TypeHeader, Fixture.Single(c, "src/list.c > node").Kind);
        Fixture.Single(c, "src/list.c > list_t");

        var cpp = Fixture.Chunk("cpp/matrix.hpp");
        var header = Fixture.Single(cpp, "src/matrix.hpp > linalg > Matrix");
        Assert.Contains("template <typename T>", header.Text, StringComparison.Ordinal);
        Fixture.Single(cpp, "src/matrix.hpp > linalg > Matrix > at");
        Fixture.Single(cpp, "src/matrix.hpp > linalg > Matrix<T>::rows");
        Assert.All(cpp, chunk => Assert.Equal(LanguageIds.Cpp, chunk.Language));
    }

    [Fact]
    public void Go_package_joins_the_breadcrumb()
    {
        var chunks = Fixture.Chunk("go/server.go");

        Assert.StartsWith("// New creates a server.", Fixture.Single(chunks, "src/server.go > server > New").Text, StringComparison.Ordinal);
        Fixture.Single(chunks, "src/server.go > server > Start");
        Assert.Equal(ChunkKind.TypeHeader, Fixture.Single(chunks, "src/server.go > server > Server").Kind);
    }

    [Fact]
    public void Rust_attributes_lead_into_their_item_and_impl_blocks_name_the_type()
    {
        var chunks = Fixture.Chunk("rust/geometry.rs");

        var point = chunks.Single(c => c.Breadcrumb == "src/geometry.rs > Point" && c.Text.Contains("pub struct", StringComparison.Ordinal));
        Assert.Contains("#[derive(Debug, Clone, Copy)]", point.Text, StringComparison.Ordinal);
        Assert.Contains("/// The origin.", Fixture.Single(chunks, "src/geometry.rs > Point > origin").Text, StringComparison.Ordinal);
        Fixture.Single(chunks, "src/geometry.rs > Point > distance");
        Fixture.Single(chunks, "src/geometry.rs > Point > fmt");
        Fixture.Single(chunks, "src/geometry.rs > shapes > unit");
    }

    [Fact]
    public void Java_package_class_fields_and_methods()
    {
        var chunks = Fixture.Chunk("java/OrderService.java");
        const string Type = "src/OrderService.java > com.example.orders > OrderService";

        var header = Fixture.Single(chunks, Type);
        Assert.Contains("@Service", header.Text, StringComparison.Ordinal);
        Assert.Contains("Manages orders.", header.Text, StringComparison.Ordinal);
        var place = Fixture.Single(chunks, Type + " > place");
        Assert.Contains("@Transactional", place.Text, StringComparison.Ordinal);
        Fixture.Single(chunks, Type + " > orders, created");
        Fixture.Single(chunks, Type + " > OrderService");
    }

    [Fact]
    public void Css_rules_and_media_queries()
    {
        var chunks = Fixture.Chunk("css/site.css");

        Assert.Contains("/* Layout. */", Fixture.Single(chunks, "src/site.css > body, html").Text, StringComparison.Ordinal);
        Fixture.Single(chunks, "src/site.css > @media (max-width: 600px) > .card");
        Fixture.Single(chunks, "src/site.css > @media (max-width: 600px) > .nav");
        Fixture.Single(chunks, "src/site.css > @keyframes spin");
    }

    [Fact]
    public void Html_small_document_is_one_markup_chunk_and_large_elements_are_opened()
    {
        var small = Fixture.Chunk("html/index.html");
        Assert.Equal(ChunkKind.Markup, Fixture.Single(small, "src/index.html > html").Kind);

        var options = ChunkOptions.Default with { MaxTokens = 64 };
        var large = Fixture.Chunk("html/index.html", options);
        Fixture.Single(large, "src/index.html > html > head");
        Assert.Equal(ChunkKind.Markup, Fixture.Single(large, "src/index.html > html > body").Kind);
        Assert.True(large.Count >= 5, ChunkInvariants.Describe(large));

        // With room for the breadcrumb, sibling elements are named by tag, id and first class.
        var text = Fixture.Read("html/index.html");
        var named = new HtmlChunker().Chunk("i.html", text, ChunkOptions.Default with { MaxTokens = 72 });
        ChunkInvariants.Assert("i.html", text, named, ChunkOptions.Default with { MaxTokens = 72 });
        Assert.Contains(named, c => c.Breadcrumb.Contains("header#top", StringComparison.Ordinal));
        Assert.Contains(named, c => c.Breadcrumb.Contains("main.content", StringComparison.Ordinal));
    }

    [Fact]
    public void Html_long_lists_are_grouped_not_one_chunk_per_item()
    {
        var html = new StringBuilder("<ul id=\"items\">\n");
        for (var i = 0; i < 400; i++)
        {
            html.Append("  <li>Item ").Append(i).Append("</li>\n");
        }

        html.Append("</ul>\n");
        var text = html.ToString();

        var chunks = new HtmlChunker().Chunk("list.html", text, ChunkOptions.Default);

        ChunkInvariants.Assert("list.html", text, chunks, ChunkOptions.Default);
        Assert.InRange(chunks.Count, 3, 40);
    }

    [Fact]
    public void Function_over_the_cap_is_split_at_statements()
    {
        var source = new StringBuilder("def long_function(values):\n    total = 0\n");
        for (var i = 0; i < 150; i++)
        {
            source.Append("    if values[").Append(i).Append("] > 0:\n        total += values[").Append(i).Append("]\n");
        }

        source.Append("    return total\n");
        var text = source.ToString();
        var options = ChunkOptions.Default with { MaxTokens = 96 };

        var chunks = new TreeSitterChunker(TreeSitterGrammars.Python).Chunk("long.py", text, options);

        ChunkInvariants.Assert("long.py", text, chunks, options);
        Assert.True(chunks.Count > 5);
        Assert.All(chunks.Skip(1), c => Assert.StartsWith("    if values[", c.Text.Replace("    return", "    if values[", StringComparison.Ordinal), StringComparison.Ordinal));
        Assert.All(chunks, c => Assert.StartsWith("long.py > long_function (part ", c.Breadcrumb, StringComparison.Ordinal));
    }

    [Fact]
    public void Text_that_is_not_the_language_falls_back_to_windows()
    {
        var text = string.Join("\n", Enumerable.Range(0, 120).Select(i => $"}}}}}}}}}} ))) ]]] <<< @@@ {i} ''' \"\"\" ```"));
        var fallbacks = new List<ChunkerFallback>();
        var options = ChunkOptions.Default with { OnFallback = fallbacks.Add };

        var chunks = new TreeSitterChunker(TreeSitterGrammars.Python).Chunk("junk.py", text, options);

        ChunkInvariants.Assert("junk.py", text, chunks, options);
        Assert.All(chunks, c => Assert.Equal(ChunkKind.Window, c.Kind));
        Assert.Single(fallbacks);
    }

    [Fact]
    public void A_grammar_that_does_not_exist_falls_back_to_windows_without_throwing()
    {
        var missing = TreeSitterGrammars.Python with { Language = "nope", GrammarId = "NoSuchGrammar" };
        var fallbacks = new List<ChunkerFallback>();
        var options = ChunkOptions.Default with { OnFallback = fallbacks.Add };
        var chunker = new TreeSitterChunker(missing);

        var chunks = chunker.Chunk("a.nope", "line one\nline two\n", options);

        Assert.False(chunker.IsAvailable);
        Assert.Equal(ChunkKind.Window, Assert.Single(chunks).Kind);
        Assert.Contains("NoSuchGrammar", Assert.Single(fallbacks).Reason, StringComparison.Ordinal);
    }
}
