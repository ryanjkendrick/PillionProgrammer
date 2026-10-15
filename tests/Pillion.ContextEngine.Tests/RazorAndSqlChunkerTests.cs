using Pillion.ContextEngine.Chunking;
using Pillion.ContextEngine.Chunking.Razor;
using Pillion.ContextEngine.Chunking.Sql;

namespace Pillion.ContextEngine.Tests;

public sealed class RazorAndSqlChunkerTests
{
    [Fact]
    public void Razor_code_block_members_are_chunked_as_csharp_and_markup_as_html()
    {
        var chunks = Fixture.Chunk("razor/Counter.razor");

        var increment = Fixture.Single(chunks, "src/Counter.razor > Counter > IncrementCount");
        Assert.Equal(ChunkKind.Member, increment.Kind);
        Assert.Equal(LanguageIds.Razor, increment.Language);
        Assert.Contains("/// <summary>Adds one.</summary>", increment.Text, StringComparison.Ordinal);
        Assert.Contains("currentCount++;", increment.Text, StringComparison.Ordinal);
        Fixture.Single(chunks, "src/Counter.razor > Counter > OnInitialized");
        Fixture.Single(chunks, "src/Counter.razor > Counter > currentCount");

        var markup = chunks[0];
        Assert.Equal(ChunkKind.Markup, markup.Kind);
        Assert.Contains("<h1>Counter</h1>", markup.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("IncrementCount()", markup.Text.Replace("@onclick=\"IncrementCount\"", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
    }

    [Fact]
    public void Cshtml_statement_blocks_functions_and_markup()
    {
        var chunks = Fixture.Chunk("razor/Index.cshtml");

        var block = Fixture.Single(chunks, "src/Index.cshtml > Index > @{ }");
        Assert.Contains("ViewData[\"Title\"] = \"Home\";", block.Text, StringComparison.Ordinal);
        Fixture.Single(chunks, "src/Index.cshtml > Index > Shout");
        Assert.Contains(chunks, c => c.Kind == ChunkKind.Markup && c.Text.Contains("<h1 class=\"display-4\">", StringComparison.Ordinal));
    }

    [Fact]
    public void Razor_scanner_skips_braces_inside_strings_comments_and_interpolations()
    {
        const string Text = "@code {\n  var a = \"}\"; // }\n  var b = $\"{x} {{ }} {(y ? \"}\" : \"{\")}\";\n  /* } */ var c = '}';\n  var d = @\"\"\"}\"\"\";\n}\n<p>after</p>";

        var block = Assert.Single(RazorScanner.Scan(Text));

        Assert.Equal(RazorBlockKind.Members, block.Kind);
        Assert.Equal(Text.LastIndexOf('}'), block.Close);
    }

    [Fact]
    public void Razor_with_an_unterminated_block_does_not_throw()
    {
        const string Text = "<p>x</p>\n@code {\n  void M() {\n";

        var chunks = new RazorChunker().Chunk("A.razor", Text, ChunkOptions.Default);

        ChunkInvariants.Assert("A.razor", Text, chunks, ChunkOptions.Default);
    }

    [Fact]
    public void Sql_statements_end_at_top_level_semicolons_and_go_and_procedures_stay_whole()
    {
        var chunks = Fixture.Chunk("sql/schema.sql");

        var table = Fixture.Single(chunks, "src/schema.sql > CREATE TABLE dbo.Orders");
        Assert.StartsWith("-- Orders schema.", table.Text, StringComparison.Ordinal);
        Assert.Contains("GO", table.Text, StringComparison.Ordinal);
        var procedure = Fixture.Single(chunks, "src/schema.sql > CREATE PROCEDURE dbo.GetOrders");
        Assert.Contains("SET NOCOUNT ON;", procedure.Text, StringComparison.Ordinal);
        Assert.Contains("END", procedure.Text, StringComparison.Ordinal);
        Assert.All(chunks, c => Assert.Equal(ChunkKind.Statement, c.Kind));

        var inserts = chunks.Single(c => c.Breadcrumb.Contains("INSERT INTO dbo.Orders, INSERT", StringComparison.Ordinal));
        Assert.Contains("'Ann; Smith'", inserts.Text, StringComparison.Ordinal);
        Assert.Contains("VALUES (3, 'Cy')", inserts.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Sql_lexer_handles_quotes_comments_and_dollar_quoting()
    {
        const string Text = "SELECT 'a;b', \"c;d\", [e;f] FROM t; -- x; y\nCREATE FUNCTION f() RETURNS int AS $body$ BEGIN; RETURN 1; END; $body$ LANGUAGE plpgsql;\n/* ; */ SELECT 2";

        var statements = SqlChunker.Statements(Text);

        Assert.Equal(3, statements.Count);
        Assert.EndsWith("FROM t;", Text[statements[0].Start..statements[0].End], StringComparison.Ordinal);
        Assert.EndsWith("plpgsql;", Text[statements[1].Start..statements[1].End], StringComparison.Ordinal);
        Assert.Equal("SELECT 2", Text[statements[2].Start..statements[2].End]);
    }
}
