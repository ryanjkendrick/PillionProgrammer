using System.Runtime.CompilerServices;
using System.Text.Json;
using Pillion.Common.Hardware;
using Pillion.Common.Settings;
using Pillion.Contracts;
using Pillion.ModelGateway.Tests.TestSupport;

namespace Pillion.ModelGateway.Tests.Autocomplete;

public sealed class AutocompleteGatewayTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Ollama_SmallModel_GetsTheCodeAlone_NoNewlineStop_AndTheGpuBudget()
    {
        using var harness = new GatewayHarness(withCloud: false);
        var request = PromptBudgetTests.Request("int Add(int a, int b) => ", ";\n", caretLine: 0);
        request.OpenDocuments = ["Program.cs"];
        request.StopSequences = ["// stop"];

        var chunks = await harness.Gateway.CompleteAsync(request, CancellationToken.None).CollectAsync().WaitAsync(Wait);

        // qwen2.5-coder:1.5b's catalog family sets fimHeader false: no header at all.
        var fim = Assert.Single(harness.Local!.ReceivedFims);
        Assert.Equal("int Add(int a, int b) => ", fim.Prefix);
        Assert.Equal(";\n", fim.Suffix);
        Assert.Equal(64, fim.MaxTokens);
        Assert.Equal(Profile.LocalFast, fim.Profile);
        Assert.Null(fim.ContextSize); // num_ctx stays fixed per profile in the adapter

        // The single-line newline is not sent to the provider (a leading fence line would end the completion); the
        // gateway's shaper ends the line instead.
        Assert.DoesNotContain("\n", fim.StopSequences);
        Assert.Equal("// stop", fim.StopSequences[0]);
        Assert.Contains("<|fim_prefix|>", fim.StopSequences); // the Qwen2.5-Coder family's stops
        Assert.Contains("\n```", fim.StopSequences); // and its closing-fence stop
        Assert.Equal("a + b", string.Concat(chunks.Select(c => c.Text)));

        var final = chunks[^1];
        Assert.True(final.IsFinal);
        var counted = TokenCounter.Default.ForModel("qwen2.5-coder:1.5b").Count(fim.Prefix) + TokenCounter.Default.ForModel("qwen2.5-coder:1.5b").Count(fim.Suffix) + AutocompleteBudget.ServerTemplateReserveTokens;
        Assert.InRange(final.PromptTokens, counted, counted + 2); // the header and the prefix are rounded up separately
    }

    [Fact]
    public async Task LargerModel_GetsTheTerseCommentHeader()
    {
        using var harness = new GatewayHarness(withCloud: false);
        harness.Settings.Routing.Profiles[Profile.LocalFast] = "local/qwen2.5-coder:14b";
        harness.Reload();
        harness.ListAll();
        var request = PromptBudgetTests.Request("int Add(int a, int b) => ", ";\n", caretLine: 0);
        request.OpenDocuments = [@"C:\src\Program.cs"];
        request.RecentEdits = [@"C:\src\Program.cs:8: Run(args);"];

        await harness.Gateway.CompleteAsync(request, CancellationToken.None).CollectAsync().WaitAsync(Wait);

        var fim = Assert.Single(harness.Local!.ReceivedFims);
        Assert.Equal("// file: Calc.cs\n// see: Program.cs\n// edit: Program.cs:8: Run(args);\nint Add(int a, int b) => ", fim.Prefix);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LeadingFence_IsStripped_AndTheCodeAfterItShown(bool multiLine)
    {
        using var harness = new GatewayHarness(withCloud: false);
        harness.Local!.Fim = (_, ct) => FakeProvider.Pieces(ct, "``", "`csharp\n", "    return a + b;", "\n```\n", "more");
        var request = GatewayHarness.CompletionRequest(prefix: "int Add(int a, int b)\n{\n    ", suffix: "\n}\n");
        request.AllowMultiLine = multiLine;

        var chunks = await harness.Gateway.CompleteAsync(request, CancellationToken.None).CollectAsync().WaitAsync(Wait);

        // The caret stands after the indentation already, so the quoted line's own indentation goes; the closing fence
        // ends the completion.
        var text = string.Concat(chunks.Select(c => c.Text));
        Assert.Equal("return a + b;", text);
        Assert.DoesNotContain("```", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FencedRestatementOfTheCaretLine_KeepsOnlyTheContinuation()
    {
        // Sample from the Gate 1 measurement (qwen2.5-coder:7b, empty suffix): "```csharp\n        return value * 2;\n    }\n}".
        using var harness = new GatewayHarness(withCloud: false);
        harness.Local!.Fim = (_, ct) => FakeProvider.Pieces(ct, " ```csharp\n", "        return value * 2;\n", "    }\n}");
        var request = GatewayHarness.CompletionRequest(prefix: "public static int Twice(int value)\n    {\n        return ", suffix: string.Empty);

        var chunks = await harness.Gateway.CompleteAsync(request, CancellationToken.None).CollectAsync().WaitAsync(Wait);

        Assert.Equal("value * 2;", string.Concat(chunks.Select(c => c.Text)));
    }

    [Fact]
    public async Task Prose_IsDropped_AndNotCached()
    {
        // A real 1.5B reply from the Gate 1 typing scenario (autocomplete-fast-VS18.json, run 3).
        using var harness = new GatewayHarness(withCloud: false);
        harness.Local!.Fim = (_, ct) => FakeProvider.Pieces(ct, "It", " looks", " like", " you're", " encountering an issue with the `TypeFast.cs` file.");
        var request = GatewayHarness.CompletionRequest(prefix: "namespace Gate;\n\npublic static class TypeFast\n{\n    ", suffix: string.Empty);

        var chunks = await harness.Gateway.CompleteAsync(request, CancellationToken.None).CollectAsync().WaitAsync(Wait);

        Assert.Equal(string.Empty, string.Concat(chunks.Select(c => c.Text)));
        Assert.True(chunks[^1].IsFinal);
        Assert.Equal(0, harness.Local.InFlight); // the stream was closed at once
        Assert.Equal(CacheHitKind.None, harness.Cache.Lookup("local", "qwen2.5-coder:1.5b", false, request.Prefix!, request.Suffix!, out _));
    }

    [Fact]
    public async Task ProseInAComment_IsKept()
    {
        using var harness = new GatewayHarness(withCloud: false);
        harness.Local!.Fim = (_, ct) => FakeProvider.Pieces(ct, "It looks like the total of the items in the cart.");
        var request = GatewayHarness.CompletionRequest(prefix: "    // Returns the total. ", suffix: "\n");

        var chunks = await harness.Gateway.CompleteAsync(request, CancellationToken.None).CollectAsync().WaitAsync(Wait);

        Assert.Equal("It looks like the total of the items in the cart.", string.Concat(chunks.Select(c => c.Text)));
    }

    [Fact]
    public async Task SingleLine_StopsAtTheFirstNewline_EvenWhenTheProviderIgnoresTheStop()
    {
        using var harness = new GatewayHarness(withCloud: false);
        harness.Local!.Fim = (_, ct) => FakeProvider.Pieces(ct, "a + b;", "\n", "    more();");

        var chunks = await harness.Gateway.CompleteAsync(GatewayHarness.CompletionRequest(), CancellationToken.None).CollectAsync().WaitAsync(Wait);

        Assert.Equal("a + b;", string.Concat(chunks.Select(c => c.Text)));
        Assert.Equal(0, harness.Local.InFlight); // the stream was closed early
    }

    [Fact]
    public async Task MultiLine_AllowsSeveralLines_UpTo30_AndAsksForMoreTokens()
    {
        using var harness = new GatewayHarness(withCloud: false);
        harness.Local!.Fim = (_, ct) => FakeProvider.Pieces(ct, [.. Enumerable.Range(1, 35).Select(i => $"    line{i};\n")]);
        var request = GatewayHarness.CompletionRequest(prefix: "void Run()\n{\n", suffix: "}\n");
        request.AllowMultiLine = true;

        var chunks = await harness.Gateway.CompleteAsync(request, CancellationToken.None).CollectAsync().WaitAsync(Wait);

        var text = string.Concat(chunks.Select(c => c.Text));
        Assert.Equal(30, text.Split('\n').Length);
        var fim = Assert.Single(harness.Local.ReceivedFims);
        Assert.Equal(256, fim.MaxTokens);
        Assert.DoesNotContain("\n", fim.StopSequences);
    }

    [Theory]
    [InlineData(GpuTier.CpuOnly, 1024)]
    [InlineData(GpuTier.Gpu8GB, 2048)]
    public async Task LocalBudget_FollowsTheGpuTier(GpuTier tier, int budget)
    {
        using var harness = new GatewayHarness(withCloud: false, gpuTier: tier);
        var request = GatewayHarness.CompletionRequest(prefix: PromptBudgetTests.Lines(0, 2000) + "x = ", suffix: ";\n" + PromptBudgetTests.Lines(2001, 2000));
        request.CaretLine = 2000;

        var chunks = await harness.Gateway.CompleteAsync(request, CancellationToken.None).CollectAsync().WaitAsync(Wait);

        var promptTokens = chunks[^1].PromptTokens;
        Assert.InRange(promptTokens, budget * 0.85, budget);
        var record = Assert.Single(harness.OutboundLog.GetRecent());
        Assert.Equal(promptTokens, record.EstimatedPromptTokens);
        Assert.False(record.PromptTokensCounted); // Qwen is estimated
    }

    [Fact]
    public async Task GpuTierSetting_OverridesDetection()
    {
        using var harness = new GatewayHarness(withCloud: false, gpuTier: GpuTier.Gpu24GBPlus);
        harness.Settings.Models.GpuTier = GpuTierSetting.Cpu;
        harness.Reload();
        var request = GatewayHarness.CompletionRequest(prefix: PromptBudgetTests.Lines(0, 2000) + "x = ");
        request.CaretLine = 2000;

        var chunks = await harness.Gateway.CompleteAsync(request, CancellationToken.None).CollectAsync().WaitAsync(Wait);

        Assert.InRange(chunks[^1].PromptTokens, 800, AutocompleteBudget.CpuPromptTokens);
        Assert.Equal(new GpuInfo(GpuTier.CpuOnly, null, null, "setting"), harness.Gateway.GetGpuInfo());
    }

    [Fact]
    public async Task Cloud_UsesTheGpuBudget_AndCountsExactlyWithATokenizer()
    {
        using var harness = new GatewayHarness(withLocal: false, gpuTier: GpuTier.CpuOnly);
        harness.Settings.CloudSwitches.Autocomplete = true;
        harness.Settings.Routing.Profiles[Profile.Fast] = "cloud/gpt-4o-mini";
        harness.Cloud!.Models.Add(harness.Cloud.Model("gpt-4o-mini", fim: true));
        harness.Settings.Providers.First(p => p.Id == "cloud").Models.Add("gpt-4o-mini");
        harness.Reload();
        harness.ListAll();
        var request = GatewayHarness.CompletionRequest(prefix: PromptBudgetTests.Lines(0, 3000) + "x = ");
        request.CaretLine = 3000;

        var chunks = await harness.Gateway.CompleteAsync(request, CancellationToken.None).CollectAsync().WaitAsync(Wait);

        Assert.InRange(chunks[^1].PromptTokens, 1500, AutocompleteBudget.GpuPromptTokens);
        var record = Assert.Single(harness.OutboundLog.GetRecent());
        Assert.True(record.PromptTokensCounted);
        Assert.False(record.IsLocal);
        Assert.Equal("gpt-4o-mini", record.ModelId);
    }

    [Fact]
    public async Task ExplicitFimTemplate_IsRenderedForARawServer_WithNoSuffix()
    {
        using var harness = new GatewayHarness(withCloud: false, withLocal: false);
        var raw = new FakeProvider("llamacpp", isLocal: true, ProviderKind.OpenAiCompatible);
        raw.Models = [raw.Model("starcoder2:3b", fim: true)];
        harness.CloudFactory.Providers[raw.Id] = raw;
        harness.Settings.Providers.Add(new ProviderSettings
        {
            Id = "llamacpp",
            Kind = ProviderKind.OpenAiCompatible,
            Endpoint = "http://localhost:8080",
            Models = ["starcoder2:3b"],
            ExtensionData = new Dictionary<string, JsonElement> { ["fimTemplate"] = JsonSerializer.SerializeToElement("explicit") },
        });
        harness.Settings.Routing.Profiles[Profile.LocalFast] = "llamacpp/starcoder2:3b";
        harness.Reload();
        harness.ListAll();
        harness.Inventory.Update(raw.Id, raw.Models);

        await harness.Gateway.CompleteAsync(PromptBudgetTests.Request("x = ", ";", 0), CancellationToken.None).CollectAsync().WaitAsync(Wait);

        var fim = Assert.Single(raw.ReceivedFims);
        Assert.Equal("<fim_prefix>// file: Calc.cs\nx = <fim_suffix>;<fim_middle>", fim.Prefix);
        Assert.Equal(string.Empty, fim.Suffix);
        Assert.Contains("<file_sep>", fim.StopSequences);
    }

    [Fact]
    public void ExplicitFimTokens_AreNeverUsedForOllama()
    {
        var ollama = new ProviderSettings { Kind = ProviderKind.Ollama, ExtensionData = new Dictionary<string, JsonElement> { ["fimTemplate"] = JsonSerializer.SerializeToElement("explicit") } };
        var server = new ProviderSettings { Kind = ProviderKind.OpenAiCompatible };

        Assert.False(ModelGateway.UsesExplicitFimTokens(ollama));
        Assert.False(ModelGateway.UsesExplicitFimTokens(server));
        Assert.False(ModelGateway.UsesExplicitFimTokens(null));
    }

    [Fact]
    public async Task Cache_ExactAndTypeThroughHits_AreCountedAndRecorded()
    {
        using var harness = new GatewayHarness(withCloud: false);
        harness.Local!.Fim = (_, ct) => FakeProvider.Pieces(ct, "Add(first, second);");

        var miss = await harness.Gateway.CompleteAsync(GatewayHarness.CompletionRequest(prefix: "var x = calc.", suffix: "\n"), CancellationToken.None).CollectAsync();
        var exact = await harness.Gateway.CompleteAsync(GatewayHarness.CompletionRequest(prefix: "var x = calc.", suffix: "\n", requestId: "c2"), CancellationToken.None).CollectAsync();
        var typed = await harness.Gateway.CompleteAsync(GatewayHarness.CompletionRequest(prefix: "var x = calc.Add(", suffix: "\n", requestId: "c3"), CancellationToken.None).CollectAsync();
        var diverged = await harness.Gateway.CompleteAsync(GatewayHarness.CompletionRequest(prefix: "var x = calc.Sub", suffix: "\n", requestId: "c4"), CancellationToken.None).CollectAsync();

        Assert.Equal("Add(first, second);", Assert.Single(exact).Text);
        Assert.Equal("first, second);", Assert.Single(typed).Text);
        Assert.Equal(2, harness.Local.FimCalls);
        Assert.NotEmpty(miss);
        Assert.NotEmpty(diverged);

        var counters = harness.Gateway.Counters.Snapshot();
        Assert.Equal(4, counters.Requests);
        Assert.Equal(1, counters.CacheHits);
        Assert.Equal(1, counters.TypeThroughHits);
        Assert.Equal(2, counters.CacheMisses);
        Assert.Equal(2, counters.ModelRequests);
        Assert.Equal(0.5, counters.HitRate);

        var records = harness.OutboundLog.GetRecent();
        Assert.Equal([false, true, true, false], records.Select(r => r.CacheHit));
        Assert.All(records.Where(r => r.CacheHit), r => Assert.Equal(0, r.EstimatedPromptTokens));
    }

    [Fact]
    public void CacheKey_SeparatesModelsAndSingleFromMultiLine()
    {
        var single = AutocompleteCache.CreateKey("p", "m", "prefix", "suffix", multiLine: false);
        var multi = AutocompleteCache.CreateKey("p", "m", "prefix", "suffix", multiLine: true);

        Assert.NotEqual(single, multi);
        Assert.Equal(single, AutocompleteCache.CreateKey("p", "m", "prefix", "suffix"));
        Assert.NotEqual(single, AutocompleteCache.CreateKey("p", "other", "prefix", "suffix"));
    }

    [Fact]
    public void Cache_TypeThrough_NeedsTheTypedTextToStartTheCompletion_AndSomethingLeft()
    {
        var cache = new AutocompleteCache(new ModelGatewayOptions());
        cache.Set(AutocompleteCache.CreateKey("p", "m", "abc", "s", multiLine: false), "def");

        Assert.Equal(CacheHitKind.TypeThrough, cache.Lookup("p", "m", false, "abcd", "s", out var rest));
        Assert.Equal("ef", rest);
        Assert.Equal(CacheHitKind.None, cache.Lookup("p", "m", false, "abcdef", "s", out _)); // nothing left to show
        Assert.Equal(CacheHitKind.None, cache.Lookup("p", "m", false, "abcx", "s", out _));
        Assert.Equal(CacheHitKind.None, cache.Lookup("p", "m", false, "abcd", "other suffix", out _));
        Assert.Equal(CacheHitKind.None, cache.Lookup("p", "m", true, "abcd", "s", out _));
        Assert.Equal(new CacheCounts(0, 1, 4), cache.GetCounts());
    }

    [Fact]
    public async Task OutboundRecord_HasFirstTokenAndDuration()
    {
        using var harness = new GatewayHarness(withCloud: false);
        harness.Local!.Fim = Slow;

        await harness.Gateway.CompleteAsync(GatewayHarness.CompletionRequest(), CancellationToken.None).CollectAsync().WaitAsync(Wait);

        var record = Assert.Single(harness.OutboundLog.GetRecent());
        Assert.True(record.TimeToFirstToken >= TimeSpan.FromMilliseconds(40), $"{record.TimeToFirstToken}");
        Assert.True(record.Duration >= record.TimeToFirstToken);
        Assert.Equal(TokenCounter.Default.ForModel("qwen2.5-coder:1.5b").Count("ab"), record.CompletionTokens);
        Assert.False(record.CacheHit);
        Assert.True(record.IsLocal);

        static async IAsyncEnumerable<string> Slow(FimRequest request, [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Delay(50, ct);
            yield return "a";
            await Task.Delay(20, ct);
            yield return "b";
        }
    }

    [Fact]
    public void LocalBudget_HasA10SecondFloor()
    {
        var rates = new PromptEvaluationRates();
        var floor = new ModelGatewayOptions().LocalAutocompleteFirstTokenFloor;

        Assert.Equal(TimeSpan.FromSeconds(10), floor);
        Assert.Equal(floor, rates.BudgetFor("ollama/m", 500, floor));                    // 3 x 500 / 200 = 7.5 s
        Assert.Equal(TimeSpan.FromSeconds(15), rates.BudgetFor("ollama/m", 1000, floor)); // 3 x 1,000 / 200 = 15 s
    }

    [Fact]
    public void LocalBudget_UsesTheMeasuredRate()
    {
        var rates = new PromptEvaluationRates();
        var floor = TimeSpan.FromSeconds(10);

        Assert.Equal(TimeSpan.FromSeconds(30), rates.BudgetFor("ollama/m", 2000, floor)); // default 200 tokens/s, x3
        rates.Observe("ollama/m", 2000, TimeSpan.FromSeconds(1));                       // 2,000 tokens/s measured
        Assert.Equal(2000, rates.RateOf("ollama/m"));
        Assert.Equal(floor, rates.BudgetFor("ollama/m", 2000, floor));                  // 3 s predicted, floor wins
        for (var i = 0; i < 10; i++)
        {
            rates.Observe("ollama/m", 2000, TimeSpan.FromSeconds(20));                  // the model became slow (100 tokens/s)
        }

        Assert.True(rates.BudgetFor("ollama/m", 2000, floor) > floor);
        var before = rates.RateOf("ollama/m");
        rates.Observe("ollama/m", 10, TimeSpan.FromSeconds(100));                       // too small to measure
        Assert.Equal(before, rates.RateOf("ollama/m"));
    }

    [Fact]
    public async Task ColdLoad_ReportsProgress_InsteadOfFailing()
    {
        using var harness = new GatewayHarness(withCloud: false, configure: o =>
        {
            o.LocalProgressDelay = TimeSpan.FromMilliseconds(50);
            o.LocalAutocompleteFirstTokenFloor = TimeSpan.FromMilliseconds(150);
        });
        harness.Local!.Fim = ColdLoad;

        var chunks = await harness.Gateway.CompleteAsync(GatewayHarness.CompletionRequest(), CancellationToken.None).CollectAsync().WaitAsync(Wait);

        Assert.Equal("a + b", string.Concat(chunks.Select(c => c.Text)));
        Assert.Contains(harness.Progress.Reports, p => p.Kind == ModelProgressKind.WaitingForLocalModel);
        Assert.Contains(harness.Progress.Reports, p => p.Kind == ModelProgressKind.SlowerThanExpected);
        Assert.Equal(OutboundOutcome.Succeeded, Assert.Single(harness.OutboundLog.GetRecent()).Outcome);

        static async IAsyncEnumerable<string> ColdLoad(FimRequest request, [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Delay(800, ct);
            yield return "a + b";
        }
    }

    [Fact]
    public async Task Autocomplete_IsNeverRetried_AndHasOneSlotPerProvider()
    {
        using var harness = new GatewayHarness(withCloud: false);
        harness.Local!.Fim = (_, _) => throw FakeProvider.HttpError(503);

        await Assert.ThrowsAsync<PillionRpcException>(() => harness.Gateway.CompleteAsync(GatewayHarness.CompletionRequest(), CancellationToken.None).CollectAsync());

        // One attempt per candidate (the 1.5B model, then the 14B fallback), none repeated.
        Assert.Equal(2, harness.Local.FimCalls);
        Assert.Equal(2, harness.OutboundLog.GetRecent().Select(r => r.ModelId).Distinct().Count());
        Assert.Equal(1, harness.Limiter.LimitFor(ConcurrencyLane.Autocomplete));
        Assert.Equal(1, harness.Gateway.Counters.Snapshot().Failed);
    }

    [Fact]
    public async Task Cloud_HardLimit_DropsSilently_AndCountsTheDrop()
    {
        using var harness = new GatewayHarness(withLocal: false, configure: o => o.CloudAutocompleteTimeout = TimeSpan.FromMilliseconds(100));
        harness.Settings.CloudSwitches.Autocomplete = true;
        harness.Reload();
        harness.ListAll();
        harness.Cloud!.Fim = Hang;

        var chunks = await harness.Gateway.CompleteAsync(GatewayHarness.CompletionRequest(), CancellationToken.None).CollectAsync().WaitAsync(Wait);

        Assert.Empty(chunks);
        Assert.Equal(1, harness.Gateway.Counters.Snapshot().Dropped);
        Assert.Equal(OutboundOutcome.Dropped, Assert.Single(harness.OutboundLog.GetRecent()).Outcome);

        static async IAsyncEnumerable<string> Hang(FimRequest request, [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct);
            yield return "never";
        }
    }

    [Fact]
    public async Task HugeFile_IsRoutedAndBudgeted_InsteadOfOverflowingTheContext()
    {
        using var harness = new GatewayHarness(withCloud: false);
        var request = GatewayHarness.CompletionRequest(prefix: new string('x', 400_000) + "\nvar y = ", suffix: "\n" + new string('z', 400_000));

        var chunks = await harness.Gateway.CompleteAsync(request, CancellationToken.None).CollectAsync().WaitAsync(Wait);

        Assert.True(chunks[^1].PromptTokens <= AutocompleteBudget.GpuPromptTokens);
        Assert.EndsWith("var y = ", Assert.Single(harness.Local!.ReceivedFims).Prefix, StringComparison.Ordinal);
    }
}
