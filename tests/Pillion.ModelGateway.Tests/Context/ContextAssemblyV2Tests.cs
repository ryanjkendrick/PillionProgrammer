using System.Text;
using Pillion.Contracts;
using Pillion.ModelGateway.Tests.TestSupport;

namespace Pillion.ModelGateway.Tests.Context;

/// <summary>
/// Context assembly v2 (work item 2.12): retrieved chunks placed one by one by fused score under the per-chunk and slot caps,
/// the repository map after them in priority but before the pins in the prompt, removal by key, the retrieval switch, and
/// the citation set the gateway sends on the final chunk.
/// </summary>
public class ContextAssemblyV2Tests
{
    private const string SystemPrompt = "You are a test assistant.";

    private static readonly QuarterCounter Counter = new();

    // Window 20,000, reserve 4,000, system prompt 7 tokens: budget 15,993; retrieved cap 3,198, per chunk 1,279, map 1,599.
    private static AssemblyTarget Target(int window = 20_000, params string[] excludes) => new("test-model", window, true, SystemPrompt, excludes);

    private static ChatRequest Request(string query = "How are totals computed?") => new()
    {
        RequestId = "r1",
        ConversationId = "c1",
        Messages = [new ChatMessage { Role = ChatRole.User, Content = query }],
    };

    private static string Text(int tokens, string label = "line")
    {
        var builder = new StringBuilder();
        var line = 0;
        while (builder.Length < tokens * 4)
        {
            builder.Append(System.Globalization.CultureInfo.InvariantCulture, $"{label}{line++:D5} ").Append('x', 30).Append('\n');
        }

        return builder.ToString(0, tokens * 4);
    }

    private static ContextChunk Chunk(int id, int tokens, string path = "src/Orders/OrderService.cs", int start = 10, int? end = null, double score = 1) =>
        new($"idx:{id}", path, @"C:\repo\" + path.Replace('/', '\\'), start, end ?? start + 9, Text(tokens, "c" + id + "_"), path + " > Orders > OrderService > M" + id, score);

    [Fact]
    public async Task RetrievedChunks_ArePlacedOneByOne_WithTheirSpans_AndTheInstructionToCite()
    {
        var source = new ChunkSource(Chunk(1, 100, start: 9, end: 12), Chunk(2, 100, "src/Inventory/StockRoom.cs", 3, 8));
        var assembler = new ContextAssembler(Counter, sources: [source]);
        var request = Request().With(r => r.CurrentFile = @"C:\repo\src\Program.cs").With(r => r.CurrentFileRegion = "var total = service.ComputeTotal(order);");

        var assembled = await assembler.AssembleAsync(request, Target(), null, CancellationToken.None);

        var system = assembled.Messages[0].Content!;
        var current = system.IndexOf("## Current file", StringComparison.Ordinal);
        var intro = system.IndexOf(ChunkSource.Intro, StringComparison.Ordinal);
        var first = system.IndexOf("### src/Orders/OrderService.cs:9-12\n```", StringComparison.Ordinal);
        var second = system.IndexOf("### src/Inventory/StockRoom.cs:3-8\n```", StringComparison.Ordinal);
        Assert.True(current >= 0 && current < intro && intro < first && first < second, system);
        Assert.Equal(["idx:1", "idx:2"], assembled.SentChunks.Select(c => c.Key));
        var items = assembled.Report.Items.Where(i => i.Kind == ContextItemKind.RetrievedChunks).ToList();
        Assert.Equal(["src/Orders/OrderService.cs:9-12", "src/Inventory/StockRoom.cs:3-8"], items.Select(i => i.Name));
        Assert.Equal(["idx:1", "idx:2"], items.Select(i => i.Key));
        Assert.All(items, i => Assert.False(i.Truncated || i.Dropped));
    }

    [Fact]
    public async Task APerChunkCap_CutsABigChunk_AndTheSlotCap_DropsWhatNoLongerFits()
    {
        // 1,279 per chunk; 3,198 for the slot: the first is cut to its cap, the second and third fit, the fourth does not.
        var source = new ChunkSource(Chunk(1, 2000), Chunk(2, 900, start: 30), Chunk(3, 900, start: 50), Chunk(4, 900, start: 70));
        var assembler = new ContextAssembler(Counter, sources: [source]);

        var assembled = await assembler.AssembleAsync(Request(), Target(), null, CancellationToken.None);

        var items = assembled.Report.Items.Where(i => i.Kind == ContextItemKind.RetrievedChunks).ToList();
        Assert.True(items[0].Truncated);
        Assert.InRange(items[0].Tokens, 1000, 1279);
        Assert.Contains("per-chunk cap", items[0].Note, StringComparison.Ordinal);
        Assert.False(items[1].Truncated || items[1].Dropped);
        Assert.True(items[3].Dropped || items[3].Truncated);
        var section = assembled.Sections.Single(s => s.Kind == PromptSectionKind.RetrievedChunks);
        Assert.InRange(section.Tokens, 1, 3198 + 8);
        Assert.Equal(items.Count(i => !i.Dropped), assembled.SentChunks.Count);
    }

    [Fact]
    public async Task RetrievedChunks_ComeBeforeTheRepoMap_InPriority_WhateverTheRegistrationOrder()
    {
        // A window so small that only one of them fits: the chunks win.
        var map = new MapSource(Text(300, "map"));
        var chunks = new ChunkSource(Chunk(1, 300));
        var assembler = new ContextAssembler(Counter, new ContextAssemblerOptions { RetrievedCapFraction = 1, RetrievedChunkCapFraction = 1, RepoMapCapFraction = 1, OutputReserveTokens = 0 }, [map, chunks]);

        var assembled = await assembler.AssembleAsync(Request(), Target(window: 450), null, CancellationToken.None);

        Assert.Single(assembled.SentChunks);
        var mapItem = Assert.Single(assembled.Report.Items, i => i.Kind == ContextItemKind.RepoMap);
        Assert.True(mapItem.Dropped || mapItem.Truncated);
        Assert.Equal("repoMap", mapItem.Key);
    }

    [Fact]
    public async Task TheRepoMap_GoesBeforeThePins_InThePrompt_UnderItsCap()
    {
        var assembler = new ContextAssembler(Counter, sources: [new MapSource(Text(3000, "map")), new ChunkSource(Chunk(1, 50))]);
        var request = Request().With(r => r.Pins = [new MentionResolution { Mention = "@file:A.cs", IsResolved = true, Kind = "file", DisplayName = "A.cs", FilePath = @"C:\repo\A.cs", Content = "class A { }" }]);

        var assembled = await assembler.AssembleAsync(request, Target(), null, CancellationToken.None);

        var system = assembled.Messages[0].Content!;
        Assert.True(system.IndexOf("## Repository map", StringComparison.Ordinal) < system.IndexOf("## Pinned context", StringComparison.Ordinal));
        Assert.True(system.IndexOf("## Pinned context", StringComparison.Ordinal) < system.IndexOf(ChunkSource.Intro, StringComparison.Ordinal));
        var map = assembled.Report.Items.Single(i => i.Kind == ContextItemKind.RepoMap);
        Assert.True(map.Truncated);
        Assert.InRange(map.Tokens, 1, 1599);
        Assert.Equal(
            [ContextItemKind.SystemPrompt, ContextItemKind.RepoMap, ContextItemKind.Pin, ContextItemKind.RetrievedChunks, ContextItemKind.Query],
            assembled.Report.Items.Select(i => i.Kind));
    }

    [Fact]
    public async Task ExcludedKeys_LeaveOutAChunk_AndTheRepoMap()
    {
        var assembler = new ContextAssembler(Counter, sources: [new ChunkSource(Chunk(1, 50), Chunk(2, 50, start: 40)), new MapSource("src/A.cs\n  class A")]);
        var request = Request().With(r => r.ExcludedContextKeys = ["idx:1", "repoMap"]);

        var assembled = await assembler.AssembleAsync(request, Target(), null, CancellationToken.None);

        Assert.Equal(["idx:2"], assembled.SentChunks.Select(c => c.Key));
        Assert.DoesNotContain(assembled.Report.Items, i => i.Key == "idx:1" || i.Kind == ContextItemKind.RepoMap);
        Assert.DoesNotContain("## Repository map", assembled.Messages[0].Content!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RetrievalOff_SkipsTheRetrievedChunks()
    {
        var assembler = new ContextAssembler(Counter, sources: [new ChunkSource(Chunk(1, 50))]);
        var request = Request().With(r => r.RetrievalMode = RetrievalMode.Off).With(r => r.ProjectRules = "Be brief.");

        var assembled = await assembler.AssembleAsync(request, Target(), null, CancellationToken.None);

        Assert.Empty(assembled.SentChunks);
        Assert.DoesNotContain(assembled.Report.Items, i => i.Kind == ContextItemKind.RetrievedChunks);
    }

    [Fact]
    public async Task AChunkOfAnExcludedFile_IsDropped_AndNotSent()
    {
        var assembler = new ContextAssembler(Counter, sources: [new ChunkSource(Chunk(1, 50, "secrets/keys.cs"), Chunk(2, 50))]);

        var assembled = await assembler.AssembleAsync(Request(), Target(20_000, "secrets/**"), null, CancellationToken.None);

        Assert.Equal(["idx:2"], assembled.SentChunks.Select(c => c.Key));
        var dropped = assembled.Report.Items.Single(i => i.Key == "idx:1");
        Assert.True(dropped.Dropped);
        Assert.Contains("excluded", dropped.Note, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ShouldAssembleAsync_AsksTheSources_ForAPlainRequest_UnlessRetrievalIsOff()
    {
        var quiet = new ContextAssembler(Counter, sources: [new ChunkSource(Chunk(1, 10)) { Requests = false }]);
        var eager = new ContextAssembler(Counter, sources: [new ChunkSource(Chunk(1, 10)) { Requests = true }]);

        Assert.False(await quiet.ShouldAssembleAsync(Request(), CancellationToken.None));
        Assert.True(await eager.ShouldAssembleAsync(Request(), CancellationToken.None));
        Assert.False(await eager.ShouldAssembleAsync(Request().With(r => r.RetrievalMode = RetrievalMode.Off), CancellationToken.None));
        Assert.True(await quiet.ShouldAssembleAsync(Request().With(r => r.ProjectRules = "x"), CancellationToken.None));
    }

    [Fact]
    public async Task TheGateway_AssemblesAPlainChat_WhenRetrievalHasCode_AndSendsTheCitedSpanOnTheFinalChunk()
    {
        var source = new ChunkSource(Chunk(1, 40, start: 9, end: 12), Chunk(2, 40, "src/Inventory/StockRoom.cs", 3, 8)) { Requests = true };
        using var harness = new GatewayHarness(withCloud: false, assembler: new ContextAssembler(Counter, sources: [source]));
        harness.Local!.Chat = (_, _, ct) => FakeProvider.Pieces(ct, "Totals are summed in ", "`src/Orders/OrderService.cs:10-11`", " with tax.");

        var chunks = await harness.Gateway.ChatAsync(GatewayHarness.ChatRequest("How are totals computed?"), TaskClass.Chat, CancellationToken.None).CollectAsync();

        var sent = Assert.Single(harness.Local.ReceivedChats);
        Assert.Contains("### src/Orders/OrderService.cs:9-12", sent[0].Text, StringComparison.Ordinal);
        Assert.All(chunks.Where(c => !c.IsFinal), c => Assert.Empty(c.Citations));
        var citation = Assert.Single(chunks.Single(c => c.IsFinal).Citations);
        Assert.Equal((@"C:\repo\src\Orders\OrderService.cs", 9, 10, "idx:1"), (citation.FilePath, citation.StartLine, citation.EndLine, citation.ChunkId));
        var reply = string.Concat(chunks.Select(c => c.Text));
        Assert.Equal(reply.IndexOf("` with", StringComparison.Ordinal), citation.ReplyOffset);
    }

    [Fact]
    public async Task TheGateway_SendsTheImplicitSet_WhenTheReplyCitesNothing_AndNoneWithoutRetrievedCode()
    {
        var source = new ChunkSource(Chunk(1, 40), Chunk(2, 40, start: 40)) { Requests = true };
        using var harness = new GatewayHarness(withCloud: false, assembler: new ContextAssembler(Counter, sources: [source]));
        harness.Local!.Chat = (_, _, ct) => FakeProvider.Pieces(ct, "They are summed with tax.");

        var withCode = await harness.Gateway.ChatAsync(GatewayHarness.ChatRequest("How?"), TaskClass.Chat, CancellationToken.None).CollectAsync();
        var off = await harness.Gateway.ChatAsync(GatewayHarness.ChatRequest("How?", requestId: "r2").With(r => r.RetrievalMode = RetrievalMode.Off), TaskClass.Chat, CancellationToken.None).CollectAsync();

        var implicitSet = withCode.Single(c => c.IsFinal).Citations;
        Assert.Equal(["idx:1", "idx:2"], implicitSet.Select(c => c.ChunkId));
        Assert.All(implicitSet, c => Assert.Equal(-1, c.ReplyOffset));
        Assert.Empty(off.Single(c => c.IsFinal).Citations);
    }

    [Fact]
    public async Task AnInlineEditRequest_IsNeverAssembledForRetrieval()
    {
        var source = new ChunkSource(Chunk(1, 40)) { Requests = true };
        using var harness = new GatewayHarness(withCloud: false, assembler: new ContextAssembler(Counter, sources: [source]));

        var chunks = await harness.Gateway.ChatAsync(GatewayHarness.ChatRequest("Rename it."), TaskClass.InlineEdit, CancellationToken.None).CollectAsync();

        Assert.DoesNotContain("###", string.Concat(Assert.Single(harness.Local!.ReceivedChats).Select(m => m.Text)), StringComparison.Ordinal);
        Assert.Empty(chunks.Single(c => c.IsFinal).Citations);
    }

    internal sealed class ChunkSource(params ContextChunk[] chunks) : IContextSource
    {
        public const string Intro = "## Retrieved code\nCite what you use.";

        public bool Requests { get; init; }

        public ContextSlot Slot => ContextSlot.RetrievedChunks;

        public Task<ContextContribution?> GetAsync(ChatRequest request, AssemblyTarget target, CancellationToken cancellationToken) =>
            Task.FromResult<ContextContribution?>(new ContextContribution("Retrieved code", Intro) { Chunks = chunks });

        public ValueTask<bool> RequestsAssemblyAsync(ChatRequest request, CancellationToken cancellationToken) => ValueTask.FromResult(Requests);
    }

    private sealed class MapSource(string text) : IContextSource
    {
        public ContextSlot Slot => ContextSlot.RepoMap;

        public Task<ContextContribution?> GetAsync(ChatRequest request, AssemblyTarget target, CancellationToken cancellationToken) =>
            Task.FromResult<ContextContribution?>(new ContextContribution("Repository map", text) { Key = "repoMap" });
    }
}

/// <summary>Citation extraction (work item 2.12): the explicit forms, the implicit set, and none.</summary>
public class CitationExtractorTests
{
    private static readonly ContextChunk Orders = new("idx:1", "src/Orders/OrderService.cs", @"C:\repo\src\Orders\OrderService.cs", 9, 20, "code", "src/Orders/OrderService.cs > OrderService", 0.5);
    private static readonly ContextChunk Stock = new("idx:2", "src/Inventory/StockRoom.cs", @"C:\repo\src\Inventory\StockRoom.cs", 3, 8, "code", "src/Inventory/StockRoom.cs > StockRoom", 0.4);
    private static readonly ContextChunk[] Sent = [Orders, Stock];

    [Theory]
    [InlineData("See src/Orders/OrderService.cs:12.", 11, 11)]
    [InlineData("See `src/Orders/OrderService.cs:12-15`.", 11, 14)]
    [InlineData("See OrderService.cs:12 for it.", 11, 11)]
    [InlineData("See [OrderService.cs:12-30](x)", 11, 19)] // clamped to the chunk's end, line 20
    [InlineData(@"See C:\repo\src\Orders\OrderService.cs:9.", 8, 8)]
    [InlineData("See src/Orders/OrderService.cs#L12-L13.", 11, 12)]
    [InlineData("See Orders/OrderService.cs:12–14.", 11, 13)]
    [InlineData("See ./src/Orders/OrderService.cs:12:5.", 11, 11)]
    [InlineData("See src\\Orders\\OrderService.cs:5-10.", 8, 9)] // starts before the chunk, overlaps it
    public void AnExplicitReference_IntoASentChunk_IsCitedWithTheLinesItNames(string reply, int start, int end)
    {
        var citation = Assert.Single(CitationExtractor.Extract(reply, Sent));

        Assert.Equal((Orders.FilePath, start, end, "idx:1", Orders.Breadcrumb), (citation.FilePath, citation.StartLine, citation.EndLine, citation.ChunkId, citation.Breadcrumb));
        Assert.True(citation.ReplyOffset > 0 && citation.ReplyOffset <= reply.Length);
    }

    [Fact]
    public void SeveralReferences_AreCitedInReplyOrder_WithoutRepeats_AndTheOffsetFollowsEach()
    {
        const string reply = "First StockRoom.cs:4, then OrderService.cs:10 and again StockRoom.cs:4.";

        var citations = CitationExtractor.Extract(reply, Sent);

        Assert.Equal(["idx:2", "idx:1"], citations.Select(c => c.ChunkId));
        Assert.Equal(reply.IndexOf(", then", StringComparison.Ordinal), citations[0].ReplyOffset);
        Assert.Equal(reply.IndexOf(" and again", StringComparison.Ordinal), citations[1].ReplyOffset);
    }

    [Theory]
    [InlineData("Nothing cited here.")]
    [InlineData("See OrderService.cs:200.")] // outside every span
    [InlineData("See Program.cs:12.")] // a file that was not sent
    public void AReplyThatNamesNoSentChunk_GetsTheImplicitSet(string reply)
    {
        var citations = CitationExtractor.Extract(reply, Sent);

        Assert.Equal(["idx:1", "idx:2"], citations.Select(c => c.ChunkId));
        Assert.All(citations, c => Assert.Equal(-1, c.ReplyOffset));
        Assert.Equal((8, 19), (citations[0].StartLine, citations[0].EndLine)); // the whole chunk, zero-based
    }

    [Fact]
    public void WithoutSentChunks_ThereAreNoCitations() =>
        Assert.Empty(CitationExtractor.Extract("See OrderService.cs:12.", []));
}
