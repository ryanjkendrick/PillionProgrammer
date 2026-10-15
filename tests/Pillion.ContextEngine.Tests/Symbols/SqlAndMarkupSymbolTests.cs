using Pillion.ContextEngine.Symbols;

namespace Pillion.ContextEngine.Tests.Symbols;

public sealed class SqlAndMarkupSymbolTests
{
    [Fact]
    public void Sql_fixture_tables_procedures_and_table_references()
    {
        var symbols = SymbolFixture.Extract("sql/schema.sql");

        var orders = SymbolFixture.Definition(symbols, "dbo > Orders", SymbolKind.Table);
        Assert.Equal((2, 5), (orders.StartLine, orders.EndLine));
        Assert.Equal("CREATE TABLE dbo.Orders", orders.Signature);
        var procedure = SymbolFixture.Definition(symbols, "dbo > GetOrders", SymbolKind.Procedure);
        Assert.Equal("CREATE PROCEDURE dbo.GetOrders @Customer NVARCHAR(100)", procedure.Signature);

        var inProcedure = symbols.References.Single(r => r.Line == 14);
        Assert.Equal(("Orders", "dbo", SymbolReferenceKind.Use, "dbo > GetOrders"), (inProcedure.Name, inProcedure.Qualifier, inProcedure.Kind, inProcedure.FromContainer));
        Assert.Equal(5, symbols.References.Count(r => r.Name == "Orders"));
    }

    [Fact]
    public void Sql_joins_updates_deletes_comma_lists_exec_and_quoted_names()
    {
        const string Source = """
            CREATE OR ALTER VIEW [sales].[OpenOrders] AS
            SELECT o.Id FROM dbo.Orders o JOIN "dbo"."Customers" c ON c.Id = o.CustomerId;

            -- FROM Commented
            UPDATE Inventory SET Count = 0 WHERE Name = 'FROM Quoted';
            DELETE FROM AuditLog;
            SELECT * FROM Regions r, Countries;
            EXEC dbo.Recalculate @Force = 1;
            CREATE FUNCTION dbo.Total(@Id INT) RETURNS INT AS BEGIN RETURN 1 END;
            """;
        var symbols = SymbolFixture.Extract("src/views.sql", Source);

        SymbolFixture.Definition(symbols, "sales > OpenOrders", SymbolKind.View);
        Assert.Equal("CREATE FUNCTION dbo.Total(@Id INT) RETURNS INT", SymbolFixture.Definition(symbols, "dbo > Total", SymbolKind.Function).Signature);
        Assert.Equal("sales > OpenOrders", SymbolFixture.Reference(symbols, "Orders", SymbolReferenceKind.Use, "dbo").FromContainer);
        SymbolFixture.Reference(symbols, "Customers", SymbolReferenceKind.Use, "dbo");
        SymbolFixture.Reference(symbols, "Inventory", SymbolReferenceKind.Use);
        SymbolFixture.Reference(symbols, "AuditLog", SymbolReferenceKind.Use);
        SymbolFixture.Reference(symbols, "Regions", SymbolReferenceKind.Use);
        SymbolFixture.Reference(symbols, "Countries", SymbolReferenceKind.Use);
        SymbolFixture.Reference(symbols, "Recalculate", SymbolReferenceKind.Call, "dbo");
        Assert.DoesNotContain(symbols.References, r => r.Name is "Commented" or "Quoted" or "SET" or "o");
    }

    [Fact]
    public void Css_classes_are_definitions_and_ids_are_uses()
    {
        var symbols = SymbolFixture.Extract("css/site.css");

        Assert.Equal(2, symbols.Definitions.Count(d => d is { Name: ".card", Kind: SymbolKind.Selector }));
        var title = SymbolFixture.Definition(symbols, ".title", SymbolKind.Selector);
        Assert.Equal((10, 12), (title.StartLine, title.EndLine));
        Assert.Equal(".card > .title", title.Signature);
        SymbolFixture.Definition(symbols, ".nav", SymbolKind.Selector);

        var withId = SymbolFixture.Extract("src/a.css", "#top .menu { color: red; }\n");
        Assert.Equal("#top .menu", SymbolFixture.Reference(withId, "#top", SymbolReferenceKind.Use).FromContainer);
        SymbolFixture.Definition(withId, ".menu", SymbolKind.Selector);
    }

    [Fact]
    public void Html_ids_are_definitions_and_classes_are_uses()
    {
        var symbols = SymbolFixture.Extract("html/index.html");

        var top = SymbolFixture.Definition(symbols, "#top", SymbolKind.ElementId);
        Assert.Equal((11, 13), (top.StartLine, top.EndLine));
        Assert.Equal("<header id=\"top\">", top.Signature);
        SymbolFixture.Reference(symbols, ".content", SymbolReferenceKind.Use);
        SymbolFixture.Reference(symbols, ".wide", SymbolReferenceKind.Use);
    }

    [Fact]
    public void Html_classes_resolve_to_css_rules_and_css_ids_to_html_elements()
    {
        var graph = new SymbolGraphBuilder();
        graph.AddFile(SymbolFixture.Extract("web/index.html", "<div id=\"top\" class=\"card\">Hi</div>\n"));
        graph.AddFile(SymbolFixture.Extract("web/site.css", ".card { margin: 0; }\n#top { color: red; }\n"));

        var built = graph.Build();

        var card = Assert.Single(built.FindDefinitions(".card"));
        Assert.Equal("web/index.html", Assert.Single(built.FindReferences(card.Id)).FilePath);
        var top = Assert.Single(built.FindDefinitions("#top"));
        Assert.Equal("web/site.css", Assert.Single(built.FindReferences(top.Id)).FilePath);
    }
}
