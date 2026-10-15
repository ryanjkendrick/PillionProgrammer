using Pillion.LocalStore.Index;

namespace Pillion.LocalStore.Tests.Index;

/// <summary>The question options of lexical search (work item 2.11): no prefix match, and terms too common to score left out.</summary>
public sealed class LexicalSearchOptionsTests : IAsyncLifetime, IDisposable
{
    private readonly TempDirectory _temp = new();
    private IndexStorage _storage = null!;
    private long _file;
    private List<long> _ids = null!;

    public async Task InitializeAsync()
    {
        _storage = await IndexTestSupport.OpenAsync(_temp);
        _file = await _storage.AddFileAsync("src/a.cs");

        // Of 10 chunks: "common" is in 6 and "shared" in 5 (half or more), "middle" in 4 and "words" in 3 (fewer than half),
        // "rare" and "commonplace" in one each.
        _ids = [.. await _storage.Chunks.ReplaceFileChunksAsync(_file, [
            IndexTestSupport.Chunk("common rare", 1),
            IndexTestSupport.Chunk("common middle shared", 2),
            IndexTestSupport.Chunk("common middle shared", 3),
            IndexTestSupport.Chunk("common middle shared", 4),
            IndexTestSupport.Chunk("common middle shared", 5),
            IndexTestSupport.Chunk("common shared", 6),
            IndexTestSupport.Chunk("commonplace", 7),
            IndexTestSupport.Chunk("other words", 8),
            IndexTestSupport.Chunk("more words", 9),
            IndexTestSupport.Chunk("last words", 10),
        ])];
    }

    public async Task DisposeAsync() => await _storage.DisposeAsync();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void TermExpressions_AreWhatTheMatchQueryJoins()
    {
        Assert.Equal(["\"order\"", "(\"getordertotal\" * OR \"get order total\" *)"], IdentifierSplitter.BuildTermExpressions("order getOrderTotal"));
        Assert.Equal(["\"order\"", "\"total\""], IdentifierSplitter.BuildTermExpressions("order total order", prefixLastToken: false));
        Assert.Empty(IdentifierSplitter.BuildTermExpressions("?!"));
        Assert.Null(IdentifierSplitter.BuildMatchQuery("?!"));
    }

    [Fact]
    public async Task WithoutThePrefixOption_TheLastTokenMatchesWholeOnly()
    {
        var prefixed = await Ids("common", LexicalSearchOptions.Default);
        var whole = await Ids("common", new LexicalSearchOptions { PrefixLastToken = false });

        Assert.Contains(_ids[6], prefixed); // commonplace
        Assert.DoesNotContain(_ids[6], whole);
        Assert.Equal(6, whole.Count);
    }

    [Fact]
    public async Task ATermInHalfTheChunksOrMore_IsLeftOut_AndTheRareTermsDecide()
    {
        var all = await Ids("common rare middle", new LexicalSearchOptions { PrefixLastToken = false });
        var skipped = await Ids("common rare middle", LexicalSearchOptions.Question);

        Assert.Equal(6, all.Count);
        Assert.Equal([_ids[0], _ids[1], _ids[2], _ids[3], _ids[4]], skipped.Order());
        Assert.Equal(_ids[0], skipped[0]); // "rare" has the highest idf
    }

    [Fact]
    public async Task WhenEveryTermIsCommon_TheRarestIsKept()
    {
        var both = await Ids("common shared", LexicalSearchOptions.Question);
        var one = await Ids("common words", LexicalSearchOptions.Question);

        Assert.Equal([_ids[1], _ids[2], _ids[3], _ids[4], _ids[5]], both.Order()); // "shared" (5) is rarer than "common" (6)
        Assert.Equal([_ids[7], _ids[8], _ids[9]], one.Order()); // "words" scores, so "common" goes
    }

    [Fact]
    public async Task ASingleTerm_IsNeverLeftOut()
    {
        Assert.Equal(6, (await Ids("common", LexicalSearchOptions.Question)).Count);
    }

    [Fact]
    public async Task TheCountsAreDroppedOnAWrite_SoATermThatBecameCommonIsLeftOut()
    {
        Assert.Contains(_ids[7], await Ids("rare other", LexicalSearchOptions.Question));

        var other = await _storage.AddFileAsync("src/b.cs");
        await _storage.Chunks.ReplaceFileChunksAsync(other, [.. Enumerable.Range(1, 12).Select(i => IndexTestSupport.Chunk("other", i))]);

        // 13 of 22 chunks now hold "other": only "rare" is searched.
        Assert.Equal([_ids[0]], await Ids("rare other", LexicalSearchOptions.Question));
    }

    private async Task<List<long>> Ids(string query, LexicalSearchOptions options) =>
        [.. (await _storage.Chunks.SearchLexicalAsync(query, 50, options)).Select(h => h.ChunkId)];
}
