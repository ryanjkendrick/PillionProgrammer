using Pillion.ContextEngine.Retrieval;

namespace Pillion.ContextEngine.Tests.Retrieval;

public sealed class QueryAnalyzerTests
{
    [Fact]
    public void Identifiers_AreSplitOnCamelCaseSnakeCaseAcronymsAndDigits()
    {
        var analysis = QueryAnalyzer.Analyze("How does getOrderTotal use snake_case_name and HTTPServer2?");

        Assert.Equal(["How", "does", "getOrderTotal", "snake_case_name", "and", "HTTPServer2"], analysis.Identifiers.Where(i => i != "use"));
        Assert.Equal(["get", "order", "total", "snake", "case", "name", "http", "server", "2"], analysis.Terms);
        Assert.Equal("getOrderTotal snake_case_name HTTPServer2", analysis.LexicalQuery);
        Assert.Empty(analysis.FileNames);
    }

    [Fact]
    public void StopWordsAndShortPlainWords_AreNotSymbolCandidates_ButCodeShapedTokensAre()
    {
        var analysis = QueryAnalyzer.Analyze("Where is the Item restock in it and what is IO or x2 or id");

        Assert.Equal(["Item", "restock", "IO", "x2"], analysis.SymbolCandidates);
        Assert.DoesNotContain("Where", analysis.SymbolCandidates);
        Assert.Equal("Item restock IO x2 id", analysis.LexicalQuery);
    }

    [Theory]
    [InlineData("Where is Greeter.cs used?", "Greeter.cs")]
    [InlineData("open src/Api/Program.cs please", "src/Api/Program.cs")]
    [InlineData(@"what about C:\repo\web\app.ts", "C:/repo/web/app.ts")]
    [InlineData("the src/Orders folder", "src/Orders")]
    [InlineData("see `inventory.py`.", "inventory.py")]
    [InlineData("README.md: the install section", "README.md")]
    public void FileNames_AreDetectedByExtensionOrPathSeparator(string query, string expected)
    {
        var analysis = QueryAnalyzer.Analyze(query);

        Assert.Equal([expected], analysis.FileNames);
    }

    [Theory]
    [InlineData("OrderService.Place is called from where")]
    [InlineData("e.g. it works.")]
    [InlineData("see https://example.com/a.html for more")]
    [InlineData("version 1.2.3 of the library")]
    public void DottedNamesThatAreNotFiles_AreNotFileNames(string query)
    {
        Assert.Empty(QueryAnalyzer.Analyze(query).FileNames);
    }

    [Fact]
    public void AQualifiedName_IsOneIdentifier_AndItsPartsAreTermsToo()
    {
        var analysis = QueryAnalyzer.Analyze("who calls OrderService.Place or Point::new");

        Assert.Contains("OrderService.Place", analysis.Identifiers);
        Assert.Contains("Point::new", analysis.Identifiers);
        Assert.Contains("OrderService.Place", analysis.SymbolCandidates);
        Assert.Contains("Place", analysis.SymbolCandidates);
        Assert.Equal(["order", "service", "place", "point", "new"], analysis.Terms);
    }

    [Fact]
    public void AFileName_SearchesLexicallyByItsStemAndFolders()
    {
        var analysis = QueryAnalyzer.Analyze("what does src/Orders/OrderService.java do");

        Assert.Equal(["src/Orders/OrderService.java"], analysis.FileNames);
        Assert.Equal("src Orders OrderService", analysis.LexicalQuery);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("?! -- ...")]
    [InlineData("how is it and what")]
    public void AQueryWithoutMeaningfulTokens_GivesEmptyLists(string query)
    {
        var analysis = QueryAnalyzer.Analyze(query);

        Assert.Empty(analysis.Terms);
        Assert.Empty(analysis.FileNames);
        Assert.Empty(analysis.SymbolCandidates);
        Assert.Equal(string.Empty, analysis.LexicalQuery);
    }

    [Fact]
    public void RepeatedTokens_AppearOnce()
    {
        var analysis = QueryAnalyzer.Analyze("order Order ORDER order_total");

        Assert.Equal(["order", "total"], analysis.Terms);
        Assert.Equal("order order_total", analysis.LexicalQuery);
    }
}
