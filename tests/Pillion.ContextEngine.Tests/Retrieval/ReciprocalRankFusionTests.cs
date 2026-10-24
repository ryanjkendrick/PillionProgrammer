using Pillion.ContextEngine.Retrieval;

namespace Pillion.ContextEngine.Tests.Retrieval;

public sealed class ReciprocalRankFusionTests
{
    private const double Precision = 1e-12;

    [Fact]
    public void Fuse_SumsOneOverKPlusRank_AcrossTheLists()
    {
        // Lexical a, b, c; vector c, a, d; symbol b.
        var scores = ReciprocalRankFusion.Fuse<string>([["a", "b", "c"], ["c", "a", "d"], ["b"]]);

        Assert.Equal((1.0 / 61) + (1.0 / 62), scores["a"], Precision); // 0.0325224...
        Assert.Equal((1.0 / 62) + (1.0 / 61), scores["b"], Precision); // the same as a: rank 2 and rank 1
        Assert.Equal((1.0 / 63) + (1.0 / 61), scores["c"], Precision); // 0.0322664...
        Assert.Equal(1.0 / 63, scores["d"], Precision);
        Assert.Equal(4, scores.Count);
    }

    [Fact]
    public void Fuse_HonoursK()
    {
        var scores = ReciprocalRankFusion.Fuse<int>([[7, 8]], k: 0);

        Assert.Equal(1.0, scores[7], Precision);
        Assert.Equal(0.5, scores[8], Precision);
    }

    [Fact]
    public void ARepeatedItem_CountsOnceAtItsBestRank_AndDoesNotShiftTheOthers()
    {
        var ranks = ReciprocalRankFusion.Ranks<string>(["a", "a", "b"]);
        var scores = ReciprocalRankFusion.Fuse<string>([["a", "a", "b"]]);

        Assert.Equal(1, ranks["a"]);
        Assert.Equal(2, ranks["b"]);
        Assert.Equal(1.0 / 61, scores["a"], Precision);
        Assert.Equal(1.0 / 62, scores["b"], Precision);
    }

    [Fact]
    public void EmptyLists_FuseToNothing_AndRankZeroContributesNothing()
    {
        Assert.Empty(ReciprocalRankFusion.Fuse<string>([[], []]));
        Assert.Equal(0, ReciprocalRankFusion.Contribution(0));
        Assert.Equal(2.0 / 65, ReciprocalRankFusion.Contribution(5, 60, 2.0), Precision);
        Assert.Throws<ArgumentOutOfRangeException>(() => ReciprocalRankFusion.Fuse<string>([["a"]], k: -1));
    }
}

public sealed class QueryVectorCacheTests
{
    [Fact]
    public void AVector_IsReusedWithinTheWindow_ForTheSameTextModelAndSize_Only()
    {
        var clock = new SteppingClock();
        var cache = new QueryVectorCache(TimeSpan.FromSeconds(60), 8, clock);
        var vector = new QueryEmbedding([1, 2, 3], "m");

        cache.Set("q", "m", 3, vector);

        Assert.True(cache.TryGet("q", "m", 3, out var hit));
        Assert.Same(vector, hit);
        Assert.False(cache.TryGet("Q", "m", 3, out _));
        Assert.False(cache.TryGet("q", "other", 3, out _));
        Assert.False(cache.TryGet("q", "m", 4, out _));

        clock.Advance(TimeSpan.FromSeconds(61));
        Assert.False(cache.TryGet("q", "m", 3, out _));
    }

    [Fact]
    public void TheOldestEntriesGo_WhenTheCacheIsFull()
    {
        var clock = new SteppingClock();
        var cache = new QueryVectorCache(TimeSpan.FromSeconds(60), 2, clock);
        for (var i = 0; i < 3; i++)
        {
            cache.Set("q" + i, "m", 1, new QueryEmbedding([1], "m"));
            clock.Advance(TimeSpan.FromSeconds(1));
        }

        Assert.Equal(2, cache.Count);
        Assert.False(cache.TryGet("q0", "m", 1, out _));
        Assert.True(cache.TryGet("q2", "m", 1, out _));
    }

    [Fact]
    public void AZeroLifetime_DisablesTheCache()
    {
        var cache = new QueryVectorCache(TimeSpan.Zero, 8);
        cache.Set("q", "m", 1, new QueryEmbedding([1], "m"));

        Assert.False(cache.TryGet("q", "m", 1, out _));
        Assert.Equal(0, cache.Count);
    }
}

public sealed class RetrievalChunkIdTests
{
    [Fact]
    public void AChunkId_RoundTrips()
    {
        var id = RetrievalChunkIds.Format("0123456789abcdef0123456789abcdef", 42);

        Assert.Equal("0123456789abcdef0123456789abcdef:42", id);
        Assert.True(RetrievalChunkIds.TryParse(id, out var index, out var chunk));
        Assert.Equal("0123456789abcdef0123456789abcdef", index);
        Assert.Equal(42, chunk);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData(":12")]
    [InlineData("abc:")]
    [InlineData("abc:-3")]
    [InlineData("abc:0")]
    [InlineData("abc:1x")]
    public void AMalformedChunkId_DoesNotParse(string? value)
    {
        Assert.False(RetrievalChunkIds.TryParse(value, out _, out var chunk));
        Assert.Equal(0, chunk);
    }
}
