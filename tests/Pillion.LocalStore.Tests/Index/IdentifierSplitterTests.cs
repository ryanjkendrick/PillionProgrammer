using Pillion.LocalStore.Index;

namespace Pillion.LocalStore.Tests.Index;

public sealed class IdentifierSplitterTests
{
    [Theory]
    [InlineData("getOrderTotal", "get order total")]
    [InlineData("GetOrderTotal", "get order total")]
    [InlineData("get_order_total", "get order total")]
    [InlineData("GET_ORDER_TOTAL", "get order total")]
    [InlineData("__init__", "init")]
    [InlineData("_privateField", "private field")]
    [InlineData("HTTPServer", "http server")]
    [InlineData("XMLHttpRequest", "xml http request")]
    [InlineData("IOError", "io error")]
    [InlineData("utf8Decode", "utf 8 decode")]
    [InlineData("Base64Encode", "base 64 encode")]
    [InlineData("x86_64", "x 86 64")]
    [InlineData("v2", "v 2")]
    [InlineData("order", "order")]
    [InlineData("ORDER", "order")]
    [InlineData("Ünïcødé", "ünïcødé")]
    [InlineData("straßeName", "straße name")]
    [InlineData("_", "")]
    [InlineData("42", "42")]
    public void Split_BreaksIdentifiersOnCaseUnderscoreAndDigitBoundaries(string identifier, string expected)
    {
        Assert.Equal(expected, string.Join(' ', IdentifierSplitter.Split(identifier)));
    }

    [Fact]
    public void TheIdentifiersColumn_HoldsTheSplitPartsOfEveryCompoundToken_InOrder()
    {
        var column = IdentifierSplitter.BuildIdentifiersColumn("public decimal GetOrderTotal(int order_id) { return _cache[order_id]; }");

        Assert.Equal("get order total order id cache order id", column);
    }

    [Fact]
    public void TheIdentifiersColumn_LeavesPlainWordsToTheTextColumn()
    {
        Assert.Equal(string.Empty, IdentifierSplitter.BuildIdentifiersColumn("return the order // 42 times"));
        Assert.Equal(string.Empty, IdentifierSplitter.BuildIdentifiersColumn("ORDER Order")); // unicode61 folds case in the text column
    }

    [Fact]
    public void TheMatchQuery_MatchesEachTokenWholeOrSplit_WithTheLastAsAPrefix()
    {
        Assert.Equal("(\"getordertotal\" OR \"get order total\")", IdentifierSplitter.BuildMatchQuery("getOrderTotal", prefixLastToken: false));
        Assert.Equal("(\"getordertotal\" * OR \"get order total\" *)", IdentifierSplitter.BuildMatchQuery("getOrderTotal"));
        Assert.Equal("\"order\" OR \"total\" *", IdentifierSplitter.BuildMatchQuery("order total"));
        Assert.Equal("(\"get_order_total\" * OR \"get order total\" *)", IdentifierSplitter.BuildMatchQuery("get_order_total"));
    }

    [Fact]
    public void TheMatchQuery_DropsFts5Syntax_AndRepeatedTokens()
    {
        Assert.Equal("\"name\" OR \"near\" OR \"or\" OR \"x\"", IdentifierSplitter.BuildMatchQuery("\"name\" NEAR(or) OR x*", prefixLastToken: true)!.Replace(" *", string.Empty, StringComparison.Ordinal));
        Assert.Equal("\"order\" OR \"total\"", IdentifierSplitter.BuildMatchQuery("order total order total", prefixLastToken: false));
        Assert.Null(IdentifierSplitter.BuildMatchQuery("  (*) -- \"\" "));
    }

    [Fact]
    public void TheMatchQuery_DoesNotPrefixASingleCharacter()
    {
        Assert.Equal("\"order\" OR \"x\"", IdentifierSplitter.BuildMatchQuery("order x"));
    }
}
