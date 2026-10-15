using Pillion.Contracts;
using Pillion.ModelGateway.Tests.TestSupport;
using static Pillion.ModelGateway.Tests.Autocomplete.PromptBudgetTests;

namespace Pillion.ModelGateway.Tests.Autocomplete;

/// <summary>
/// Retrieved snippets in the autocomplete prompt (work item 2.12): at most three, as terse comment lines before the header
/// and the prefix, trimmed first when the budget is tight, and byte-identical from keystroke to keystroke in one member so
/// the prompt prefix stays stable and the cache keeps serving type-through hits.
/// </summary>
public sealed class AutocompleteSnippetTests
{
    private static readonly EstimatingCounter PerChar = new("test", CharsPerToken: 1, DigitTokens: 1, SafetyMargin: 0);

    private static readonly AutocompleteSnippet Total = new("src/Orders/OrderService.cs", 9, 12, "    public decimal ComputeTotal(Order order)\n    {\n        return order.Lines.Sum(l => l.Price);\n    }\n");
    private static readonly AutocompleteSnippet Restock = new("src/Inventory/StockRoom.cs", 8, 8, "public void Restock(string product, int amount) { }");
    private static readonly AutocompleteSnippet Pick = new("src/Inventory/StockRoom.cs", 14, 15, "public bool Pick(string product, int amount)\n{ return true; }");
    private static readonly AutocompleteSnippet Extra = new("src/Extra.cs", 1, 1, "class Extra { }");

    [Fact]
    public void Snippets_AreCommentLines_BeforeTheHeaderAndThePrefix_WithTheCommonIndentRemoved()
    {
        var request = Request(prefix: "var total = ", suffix: ";\n", caretLine: 0);

        var prompt = AutocompletePromptBuilder.Build(request, 2048, PerChar, snippets: [Total, Restock]);

        Assert.Equal(
            "// from: OrderService.cs:9-12\n"
            + "// public decimal ComputeTotal(Order order)\n"
            + "// {\n"
            + "//     return order.Lines.Sum(l => l.Price);\n"
            + "// }\n"
            + "// from: StockRoom.cs:8\n"
            + "// public void Restock(string product, int amount) { }\n",
            prompt.Snippets);
        Assert.Equal(2, prompt.SnippetCount);
        Assert.Equal(prompt.Snippets + prompt.Header + prompt.Prefix, prompt.PromptPrefix);
        Assert.StartsWith("// file: Calc.cs\n", prompt.Header, StringComparison.Ordinal);
        Assert.Equal(prompt.SnippetTokens + prompt.HeaderTokens + prompt.PrefixTokens + prompt.SuffixTokens + AutocompleteBudget.ServerTemplateReserveTokens, prompt.PromptTokens);
    }

    [Fact]
    public void AtMostThreeSnippets_AreShown()
    {
        var prompt = AutocompletePromptBuilder.Build(Request("x = ", ";", 0), 2048, PerChar, snippets: [Total, Restock, Pick, Extra]);

        Assert.Equal(3, prompt.SnippetCount);
        Assert.DoesNotContain("Extra", prompt.Snippets, StringComparison.Ordinal);
    }

    [Fact]
    public void Snippets_UseTheLanguagesCommentSyntax_AndCannotCloseTheComment()
    {
        var python = Request("x = ", "\n", 0);
        python.LanguageId = "python";
        python.FilePath = @"C:\src\report.py";
        var css = Request("a { ", "}\n", 0);
        css.LanguageId = "css";
        css.FilePath = @"C:\src\site.css";

        var py = AutocompletePromptBuilder.Build(python, 2048, PerChar, snippets: [new AutocompleteSnippet("scripts/sales.py", 1, 2, "def total(sales):\n    return sum(sales)")]);
        var styles = AutocompletePromptBuilder.Build(css, 2048, PerChar, snippets: [new AutocompleteSnippet("site/base.css", 3, 3, "b { color: red; } /* note */")]);

        Assert.StartsWith("# from: sales.py:1-2\n# def total(sales):\n#     return sum(sales)\n", py.Snippets, StringComparison.Ordinal);
        Assert.Equal("/* from: base.css:3 */\n/* b { color: red; } /* note * / */\n", styles.Snippets);
    }

    [Fact]
    public void AModelWithoutTheHeader_GetsNoSnippets_AndNeitherDoesALanguageWithoutComments()
    {
        var noHeader = AutocompletePromptBuilder.Build(Request("x = ", ";", 0), 2048, PerChar, includeHeader: false, snippets: [Total]);
        var json = Request("{ \"a\": ", "}", 0);
        json.LanguageId = "json";

        Assert.Equal(string.Empty, noHeader.Snippets);
        Assert.Equal(string.Empty, AutocompletePromptBuilder.Build(json, 2048, PerChar, snippets: [Total]).Snippets);
    }

    [Fact]
    public void UnderATightBudget_SnippetsAreTrimmedFirst_ThePrefixAndSuffixKeepTheirShares_AndTheBudgetHolds()
    {
        var request = Request(prefix: Lines(0, 400) + "    return ", suffix: "\n" + Lines(401, 200), caretLine: 400);
        var big = Enumerable.Range(0, 3).Select(i => new AutocompleteSnippet($"src/Big{i}.cs", 1, 40, string.Join('\n', Enumerable.Range(0, 40).Select(l => $"var value{l} = Compute{l}(input);")))).ToList();

        var without = AutocompletePromptBuilder.Build(request, 1024, PerChar);
        var with = AutocompletePromptBuilder.Build(request, 1024, PerChar, snippets: big);

        var available = 1024 - AutocompleteBudget.ServerTemplateReserveTokens;
        Assert.True(with.PromptTokens <= 1024, $"{with.PromptTokens} tokens");
        Assert.InRange(with.SnippetTokens, 1, (int)(available * AutocompleteBudget.SnippetMaximumShare));
        Assert.True(with.SnippetCount < 3); // whole snippets dropped from the end, then lines
        Assert.True(with.SuffixTokens >= (int)(available * AutocompleteBudget.SuffixMinimumShare)); // the suffix keeps its floor
        Assert.EndsWith("    return ", with.Prefix, StringComparison.Ordinal); // and the prefix the caret line
        Assert.True(with.PrefixTokens <= without.PrefixTokens); // the snippets' room comes out of what the prefix would get beyond its share
        Assert.True(with.PrefixTokens + with.SuffixTokens >= (int)(available * (AutocompleteBudget.PrefixMinimumShare + AutocompleteBudget.SuffixMinimumShare)) - (AutocompletePromptBuilder.AnchorLines * 14));
    }

    [Fact]
    public void WhenTheFloorsAndHeaderLeaveNoRoom_ThereAreNoSnippets()
    {
        var request = Request(prefix: Lines(0, 400) + "    return ", suffix: "\n" + Lines(401, 200), caretLine: 400);
        request.EnclosingDeclaration = new string('d', 200);
        request.OpenDocuments = [.. Enumerable.Range(0, 8).Select(i => $@"C:\src\OpenDocumentWithALongName{i}.cs")];

        var prompt = AutocompletePromptBuilder.Build(request, 120, PerChar, snippets: [Total]);

        Assert.True(prompt.PromptTokens <= 120);
        Assert.Equal(0, prompt.SnippetTokens);
    }

    [Fact]
    public void SuccessiveKeystrokesInOneMember_KeepThePromptPrefix_OnlyTheTailGrows()
    {
        const string typed = "order.Lines.Sum(";
        AutocompletePrompt? previous = null;
        for (var i = 0; i <= typed.Length; i++)
        {
            var request = Request(prefix: Lines(0, 400) + "    return " + typed[..i], suffix: "\n" + Lines(401, 200), caretLine: 400);
            request.EnclosingDeclaration = "public decimal ComputeTotal(Order order)";
            var prompt = AutocompletePromptBuilder.Build(request, 2048, PerChar, snippets: [Total, Restock]);

            if (previous is not null)
            {
                Assert.Equal(previous.Snippets, prompt.Snippets);
                Assert.Equal(previous.Header, prompt.Header);
                Assert.StartsWith(previous.PromptPrefix, prompt.PromptPrefix, StringComparison.Ordinal);
            }

            previous = prompt;
        }

        Assert.Equal(2, previous!.SnippetCount);
    }

    [Fact]
    public async Task TheGateway_SendsTheSameSnippetBlock_ForEveryKeystrokeInAMember_AndTheCacheServesTypeThrough()
    {
        var source = new MemberSnippets();
        using var harness = new GatewayHarness(withCloud: false, snippets: source);
        harness.Settings.Routing.Profiles[Profile.LocalFast] = "local/qwen2.5-coder:14b"; // a family with the comment header
        harness.Reload();
        harness.ListAll();
        harness.Local!.Fim = (_, ct) => FakeProvider.Pieces(ct, "Sum(l => l.Price);");

        CompletionRequest Key(string prefix, string member, string id)
        {
            var request = GatewayHarness.CompletionRequest(prefix: prefix, suffix: "\n}", requestId: id);
            request.FilePath = @"C:\src\Calc.cs";
            request.EnclosingDeclaration = member;
            return request;
        }

        const string total = "decimal ComputeTotal(Order order)";
        await harness.Gateway.CompleteAsync(Key("return order.Lines.", total, "k1"), CancellationToken.None).CollectAsync();
        var typed = await harness.Gateway.CompleteAsync(Key("return order.Lines.Su", total, "k2"), CancellationToken.None).CollectAsync();
        var more = await harness.Gateway.CompleteAsync(Key("return order.Lines.Sum(", total, "k3"), CancellationToken.None).CollectAsync();

        // Another member: other snippets, so the completion cached under the first member's snippets is not reused.
        await harness.Gateway.CompleteAsync(Key("return order.Lines.Su", "void Restock(string product)", "k4"), CancellationToken.None).CollectAsync();

        Assert.Equal("m(l => l.Price);", Assert.Single(typed).Text);
        Assert.Equal("l => l.Price);", Assert.Single(more).Text);
        var fims = harness.Local.ReceivedFims.ToList();
        Assert.Equal(2, fims.Count);
        Assert.StartsWith("// from: OrderService.cs:9-12\n", fims[0].Prefix, StringComparison.Ordinal);
        Assert.StartsWith("// from: StockRoom.cs:8\n", fims[1].Prefix, StringComparison.Ordinal);

        var counters = harness.Gateway.Counters.Snapshot();
        Assert.Equal(4, counters.Requests);
        Assert.Equal(2, counters.TypeThroughHits);
        Assert.Equal(2, counters.CacheMisses);
        Assert.Equal(0.5, counters.HitRate);
        Assert.Equal(4, source.Calls);
    }

    [Fact]
    public async Task AFailingSnippetSource_DoesNotFailTheCompletion()
    {
        using var harness = new GatewayHarness(withCloud: false, snippets: new FailingSnippets());

        var chunks = await harness.Gateway.CompleteAsync(GatewayHarness.CompletionRequest(), CancellationToken.None).CollectAsync();

        Assert.NotEmpty(chunks);
        Assert.DoesNotContain("from:", Assert.Single(harness.Local!.ReceivedFims).Prefix, StringComparison.Ordinal);
    }

    [Fact]
    public void TheFingerprint_IsEmptyWithoutSnippets_AndFollowsTheirContent()
    {
        Assert.Equal(string.Empty, AutocompleteSnippets.Fingerprint([]));
        Assert.Equal(AutocompleteSnippets.Fingerprint([Total, Restock]), AutocompleteSnippets.Fingerprint([Total, Restock]));
        Assert.NotEqual(AutocompleteSnippets.Fingerprint([Total, Restock]), AutocompleteSnippets.Fingerprint([Restock, Total]));
        Assert.NotEqual(AutocompleteCache.CreateKey("p", "m", "x", "y", false), AutocompleteCache.CreateKey("p", "m", "x", "y", false, "abc"));
    }

    /// <summary>Returns one snippet set per enclosing member, as the host's provider does.</summary>
    private sealed class MemberSnippets : IAutocompleteSnippetSource
    {
        public int Calls;

        public ValueTask<IReadOnlyList<AutocompleteSnippet>> GetSnippetsAsync(CompletionRequest request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            IReadOnlyList<AutocompleteSnippet> result = request.EnclosingDeclaration?.Contains("Restock", StringComparison.Ordinal) == true ? [Restock] : [Total];
            return ValueTask.FromResult(result);
        }
    }

    private sealed class FailingSnippets : IAutocompleteSnippetSource
    {
        public ValueTask<IReadOnlyList<AutocompleteSnippet>> GetSnippetsAsync(CompletionRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("index gone");
    }
}
