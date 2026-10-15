using Pillion.ContextEngine.Retrieval;
using Pillion.LocalStore.Index;

namespace Pillion.ContextEngine.Tests.Retrieval;

public sealed class SpanExpanderTests
{
    [Fact]
    public void AHit_GrowsByOneNeighbourOnEachSide_WhenTheyFit()
    {
        var chunks = Consecutive(5, tokens: 100);

        Assert.Equal((1, 3), SpanExpander.Expand(chunks, 2, maxTokens: 300));
        Assert.Equal((1, 2), SpanExpander.Expand(chunks, 2, maxTokens: 200)); // only the one before fits
        Assert.Equal((2, 2), SpanExpander.Expand(chunks, 2, maxTokens: 199));
        Assert.Equal((2, 2), SpanExpander.Expand(chunks, 2, maxTokens: 10)); // the hit itself is always kept
        Assert.Equal((0, 1), SpanExpander.Expand(chunks, 0, maxTokens: 1000)); // nothing before the first chunk
        Assert.Equal((3, 4), SpanExpander.Expand(chunks, 4, maxTokens: 1000));
    }

    [Fact]
    public void APartOfASplitMember_GrowsToTheWholeMember_ThenToItsNeighbours()
    {
        List<ChunkRecord> chunks =
        [
            Chunk(1, 1, 10, "a.cs > N > T > Before", 50),
            Chunk(2, 11, 20, "a.cs > N > T > Big (part 1 of 3)", 100),
            Chunk(3, 21, 30, "a.cs > N > T > Big (part 2 of 3)", 100),
            Chunk(4, 31, 40, "a.cs > N > T > Big (part 3 of 3)", 100),
            Chunk(5, 41, 50, "a.cs > N > T > After", 50),
            Chunk(6, 51, 60, "a.cs > N > T > Far", 50),
        ];

        Assert.Equal((0, 4), SpanExpander.Expand(chunks, 2, maxTokens: 400));
        Assert.Equal((1, 3), SpanExpander.Expand(chunks, 2, maxTokens: 300)); // the member, no room for neighbours
        Assert.Equal((2, 3), SpanExpander.Expand(chunks, 2, maxTokens: 200)); // the nearest part first (after, then before)
        Assert.Equal((0, 4), SpanExpander.Expand(chunks, 1, maxTokens: 400)); // from the first part
    }

    [Fact]
    public void PartsOfAnotherMember_AreNotPulledIn()
    {
        List<ChunkRecord> chunks =
        [
            Chunk(1, 1, 10, "a.cs > N > T > Other (part 2 of 2)", 100),
            Chunk(2, 11, 20, "a.cs > N > T > Big (part 1 of 2)", 100),
            Chunk(3, 21, 30, "a.cs > N > T > Big (part 2 of 2)", 100),
        ];

        // Big's parts first (200 tokens); Other's part is only a neighbour, and does not fit 250.
        Assert.Equal((1, 2), SpanExpander.Expand(chunks, 1, maxTokens: 250));
        Assert.Equal((0, 2), SpanExpander.Expand(chunks, 1, maxTokens: 300));
    }

    [Fact]
    public void Place_DropsAHitWhoseChunkIsAlreadyShown()
    {
        var chunks = Consecutive(5);

        Assert.Null(SpanExpander.Place(chunks, 1, 3, 2, [(6, 20)])); // chunk 2 is lines 11-15, inside 6-20
        Assert.Null(SpanExpander.Place(chunks, 2, 2, 2, [(1, 12), (13, 15)])); // covered by two spans together
    }

    [Fact]
    public void Place_TrimsNeighboursThatOverlapAnAcceptedSpan()
    {
        var chunks = Consecutive(6); // lines 1-5, 6-10, 11-15, 16-20, 21-25, 26-30

        var placed = SpanExpander.Place(chunks, 1, 3, 2, [(1, 10)]);

        Assert.Equal(new PlacedSpan(2, 3, 11, 20), placed);
        Assert.Equal(new PlacedSpan(1, 3, 6, 20), SpanExpander.Place(chunks, 1, 3, 2, [(26, 30)])); // no overlap: unchanged
    }

    [Fact]
    public void Place_ClipsTheLinesOverlappingWindowChunksShare()
    {
        // Window chunks of 60 lines with 10 lines of overlap.
        List<ChunkRecord> windows = [Chunk(1, 1, 60, "w.txt", 100, "window"), Chunk(2, 51, 110, "w.txt", 100, "window")];

        var placed = SpanExpander.Place(windows, 1, 1, 1, [(1, 60)]);

        Assert.Equal(new PlacedSpan(1, 1, 61, 110), placed);
    }

    [Fact]
    public void BuildText_TakesEachLineOnce_AcrossOverlappingWindows()
    {
        List<ChunkRecord> windows =
        [
            new(1, 1, 1, 4, "window", "w.txt", "l1\nl2\nl3\nl4", 10),
            new(2, 1, 3, 6, "window", "w.txt", "l3\nl4\nl5\nl6", 10),
        ];

        Assert.Equal("l1\nl2\nl3\nl4\nl5\nl6", SpanExpander.BuildText(windows, new PlacedSpan(0, 1, 1, 6)));
        Assert.Equal("l2\nl3\nl4\nl5", SpanExpander.BuildText(windows, new PlacedSpan(0, 1, 2, 5)));
        Assert.Equal("l5\nl6", SpanExpander.BuildText(windows, new PlacedSpan(1, 1, 5, 6)));
    }

    [Fact]
    public void BuildText_KeepsEmptyLines_AndDropsCarriageReturns()
    {
        List<ChunkRecord> chunks = [new(1, 1, 1, 3, "member", "a.cs", "a\r\n\r\nc", 3), new(2, 1, 4, 4, "member", "a.cs", "d", 1)];

        Assert.Equal("a\n\nc\nd", SpanExpander.BuildText(chunks, new PlacedSpan(0, 1, 1, 4)));
    }

    [Fact]
    public void Snippet_CutsLongTextAtALineBreak()
    {
        var text = string.Join('\n', Enumerable.Range(1, 100).Select(i => $"line number {i}"));

        var snippet = SpanExpander.Snippet(text, 100);

        Assert.True(snippet.Length <= 104, snippet);
        Assert.EndsWith("\n...", snippet, StringComparison.Ordinal);
        Assert.StartsWith("line number 1\n", snippet, StringComparison.Ordinal);
        Assert.Equal("short", SpanExpander.Snippet("short", 100));
    }

    private static List<ChunkRecord> Consecutive(int count, int tokens = 50) =>
        [.. Enumerable.Range(0, count).Select(i => Chunk(i + 1, (i * 5) + 1, (i * 5) + 5, $"a.cs > N > T > M{i}", tokens))];

    private static ChunkRecord Chunk(long id, int start, int end, string breadcrumb, int tokens, string kind = "member") =>
        new(id, 1, start, end, kind, breadcrumb, string.Join('\n', Enumerable.Range(start, end - start + 1).Select(l => "line " + l)), tokens);
}
