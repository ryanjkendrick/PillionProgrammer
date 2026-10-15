using Pillion.Eval.Golden;
using Pillion.Eval.Models;

namespace Pillion.Eval.Tests;

/// <summary>Parsing <c>queries.jsonl</c>, the breadcrumb symbol rules, model specs, and the golden sets checked into <c>eval/</c>.</summary>
public sealed class GoldenSetTests
{
    private static readonly string[] Kinds = ["where", "which", "symbol", "concept"];

    [Fact]
    public void AQueriesFile_ParsesEveryLine_SkippingBlankOnes()
    {
        const string text = """
            {"id":"q1","query":"where are orders placed","expectedFiles":["src/Orders/OrderService.cs"],"expectedSymbols":["OrderService.Place"],"kind":"where","notes":"n"}

            {"id":"q2","query":" which class charges cards ","expectedFiles":["src\\Payments\\PaymentGateway.cs","./src/Payments/Receipt.cs"]}
            """;

        var queries = GoldenSet.ParseQueries(text);

        Assert.Equal(2, queries.Count);
        Assert.Equal("q1", queries[0].Id);
        Assert.Equal("where are orders placed", queries[0].Query);
        Assert.Equal(["src/Orders/OrderService.cs"], queries[0].ExpectedFiles);
        Assert.Equal(["OrderService.Place"], queries[0].ExpectedSymbols);
        Assert.Equal(("where", "n"), (queries[0].Kind, queries[0].Notes));
        Assert.Equal("which class charges cards", queries[1].Query);
        Assert.Equal(["src/Payments/PaymentGateway.cs", "src/Payments/Receipt.cs"], queries[1].ExpectedFiles); // normalized
        Assert.Empty(queries[1].ExpectedSymbols);
        Assert.Null(queries[1].Kind);
    }

    [Theory]
    [InlineData("""{"query":"q","expectedFiles":["a.cs"]}""", "'id' is required")]
    [InlineData("""{"id":"x","expectedFiles":["a.cs"]}""", "'query' is required")]
    [InlineData("""{"id":"x","query":"q"}""", "'expectedFiles' must list")]
    [InlineData("""{"id":"x","query":"q","expectedFiles":[]}""", "'expectedFiles' must list")]
    [InlineData("""{"id":"x","query":"q","expectedFiles":["../outside.cs"]}""", "without '..'")]
    [InlineData("""{"id":"x","query":"q","expectedFiles":["C:\\repo\\a.cs"]}""", "without '..'")]
    [InlineData("""{"id":"x","query":"q","expectedFiles":["/a.cs"]}""", "without '..'")]
    [InlineData("""{"id":"x","query":"q","expectedFiles":["a.cs"],"expectedSymbols":[" "]}""", "is empty")]
    [InlineData("""{"id":"x","query":"q","expectedFile":["a.cs"]}""", "expectedFile")]
    [InlineData("""{"id":"x","query":"q",""", "queries.jsonl:1")]
    public void AMalformedLine_IsRejectedWithItsLineNumber(string line, string message)
    {
        var ex = Assert.Throws<GoldenSetException>(() => GoldenSet.ParseQueries(line));

        Assert.Contains(message, ex.Message, StringComparison.Ordinal);
        Assert.Contains("queries.jsonl:1", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ARepeatedId_OrAnEmptyFile_IsRejected()
    {
        var repeated = Assert.Throws<GoldenSetException>(() => GoldenSet.ParseQueries("""
            {"id":"x","query":"q","expectedFiles":["a.cs"]}
            {"id":"x","query":"r","expectedFiles":["b.cs"]}
            """));
        Assert.Contains("queries.jsonl:2", repeated.Message, StringComparison.Ordinal);
        Assert.Contains("used twice", repeated.Message, StringComparison.Ordinal);

        Assert.Throws<GoldenSetException>(() => GoldenSet.ParseQueries("\n\n"));
    }

    [Theory]
    [InlineData("src/Orders/OrderService.cs > Shop.Orders > OrderService > Place", "OrderService.Place", true)]
    [InlineData("src/Orders/OrderService.cs > Shop.Orders > OrderService > Place", "Orders.OrderService", true)]
    [InlineData("src/Orders/OrderService.cs > Shop.Orders > OrderService > Place", "OrderService", true)]
    [InlineData("src/Orders/OrderService.cs > Shop.Orders > OrderService > Place", "Place", true)]
    [InlineData("src/Orders/OrderService.cs > Shop.Orders > OrderService > Place", "place", false)]
    [InlineData("src/Orders/OrderService.cs > Shop.Orders > OrderService > Place", "OrderService.Charge", false)]
    [InlineData("src/Orders/OrderService.cs > Shop.Orders > OrderService", "OrderService.Place", false)]
    [InlineData("src/OrderService.cs", "OrderService", false)]
    [InlineData("src/Greeter.cs > Fixtures.Sample > Greeter<T> > GreetAll", "Greeter.GreetAll", true)]
    [InlineData("src/Stream.cs > N > StreamAttempt<T> > StartAsync (part 2 of 4)", "StreamAttempt.StartAsync", true)]
    [InlineData("src/geometry.rs > shapes > unit", "shapes::unit", true)]
    [InlineData("src/a.cs > N > Outer > Inner > Run", "Outer.Inner", true)]
    public void ABreadcrumb_ContainsASymbol_AsConsecutiveNames(string breadcrumb, string symbol, bool expected) =>
        Assert.Equal(expected, BreadcrumbSymbols.Contains(breadcrumb, symbol));

    [Theory]
    [InlineData("bundled/bge-small-en-v1.5", "bundled", "bge-small-en-v1.5")]
    [InlineData("ollama/qwen3-embedding:0.6b", "ollama", "qwen3-embedding:0.6b")]
    [InlineData("Ollama/nomic-embed-text", "ollama", "nomic-embed-text")]
    public void AModelSpec_NamesTheProviderAndTheModel(string text, string provider, string model)
    {
        var spec = ModelSpec.Parse(text);

        Assert.Equal((provider, model), (spec.Provider, spec.ModelId));
        Assert.Equal(provider + "/" + model, spec.QualifiedId);
        Assert.DoesNotContain(':', spec.FileSafeName);
    }

    [Theory]
    [InlineData("bge-small-en-v1.5")]
    [InlineData("openai/text-embedding-3-small")]
    [InlineData("bundled/")]
    public void AnUnknownModelSpec_IsAFormatError(string text) => Assert.Throws<FormatException>(() => ModelSpec.Parse(text));

    [Fact]
    public void TheCommandLine_ParsesTheRetrievalOptions()
    {
        var parsed = CommandLine.Parse(["retrieval", "--set", "a", "--set", "b", "--model", "bundled/bge-small-en-v1.5", "--modes", "Hybrid,vector", "--no-int8-loss", "--budget-ms", "500", "--ollama", "http://box:11434"]);
        var options = parsed.ToRetrievalOptions();

        Assert.Equal(["a", "b"], options.Sets);
        Assert.Equal("bundled/bge-small-en-v1.5", Assert.Single(options.Models).QualifiedId);
        Assert.Equal(["hybrid", "vector"], options.Modes);
        Assert.False(options.Int8Loss);
        Assert.Equal(TimeSpan.FromMilliseconds(500), options.Budget);
        Assert.Equal("http://box:11434/", options.OllamaEndpoint);
        Assert.True(options.Download);

        Assert.Throws<FormatException>(() => CommandLine.Parse(["retrieval", "--modes", "fuzzy"]));
        Assert.Throws<FormatException>(() => CommandLine.Parse(["retrieval", "--set"]));
        Assert.Throws<FormatException>(() => CommandLine.Parse(["retrieval", "--frobnicate"]));
        Assert.Throws<FormatException>(() => CommandLine.Parse(["retrieval", "--set", "a"]).ToRetrievalOptions());
    }

    [Theory]
    [InlineData("pillion-src", 60)]
    [InlineData("fixtures", 40)]
    public void TheCheckedInSets_ParseAndEveryExpectedFileIsInTheirCorpus(string name, int count)
    {
        // Renaming or moving a file a query expects fails here: update eval/retrieval/<set>/queries.jsonl with the change.
        var set = GoldenSet.Load(EvalPaths.Set(name));
        var corpus = Corpus.List(set);

        Assert.Equal(count, set.Queries.Count);
        Assert.Empty(Corpus.CheckExpectedFiles(set, corpus));
        Assert.All(set.Queries, q => Assert.Contains(q.Kind, Kinds));
        Assert.DoesNotContain(corpus, f => f.Split('/').Any(part => part is "bin" or "obj"));
    }
}

/// <summary>Finds the repository's golden sets from the test assembly.</summary>
internal static class EvalPaths
{
    public static string Set(string name) => Path.Combine(
        CommandLine.FindRepositoryRoot(AppContext.BaseDirectory) ?? throw new DirectoryNotFoundException("The repository root was not found above the test assembly."),
        "eval",
        "retrieval",
        name);
}
