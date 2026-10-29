using Pillion.Common.Settings;
using Pillion.ContextEngine.Chunking;
using Pillion.Host.Indexing;
using Pillion.LocalStore.Index;

namespace Pillion.Host.Tests.Indexing;

/// <summary>
/// The truncated-embedding experiment (work item 3.4, issue #90): <c>indexing.embeddingTokenCap</c> cuts what the model sees
/// of a long chunk, never what the index stores, and the cap is part of what the vectors were made with.
/// </summary>
public sealed class EmbeddingTokenCapTests : IDisposable
{
    private static readonly TokenCounter Counter = TokenCounters.ApproximateWordPiece;
    private readonly IndexingWorkspace _workspace = new();

    public void Dispose() => _workspace.Dispose();

    [Theory]
    [InlineData(0, 512, 0)]
    [InlineData(-5, 512, 0)]
    [InlineData(256, 512, 256)]
    [InlineData(8, 512, EmbeddingInput.MinimumCap)]
    [InlineData(512, 512, 0)]
    [InlineData(600, 512, 0)]
    public void TheEffectiveCap_IsOffAtZero_RaisedToTheMinimum_AndOffAtOrAboveTheChunkCap(int configured, int chunkMax, int expected) =>
        Assert.Equal(expected, EmbeddingInput.EffectiveCap(configured, chunkMax));

    [Fact]
    public void AChunkWithinTheCap_IsEmbeddedWhole()
    {
        var chunk = MakeChunk("src/A.cs > N > T > M", "public void M() { }");
        Assert.Equal(chunk.EmbeddingText, EmbeddingInput.For(chunk, 64, Counter));
        Assert.Equal(chunk.EmbeddingText, EmbeddingInput.For(chunk, 0, Counter));
    }

    [Fact]
    public void ALongChunk_IsCutToTheCap_BreadcrumbFirst_OnAWhiteSpaceBoundary()
    {
        var text = string.Join("\n", Enumerable.Range(0, 80).Select(i => $"var value{i} = Compute(alpha{i}, beta{i});"));
        var chunk = MakeChunk("src/Long.cs > Namespace > Worker > Run", text);
        Assert.True(chunk.TokenCount > 64);

        var input = EmbeddingInput.For(chunk, 64, Counter);

        Assert.StartsWith("src/Long.cs > Namespace > Worker > Run\n", input, StringComparison.Ordinal);
        Assert.InRange(Counter(input), 1, 64);
        Assert.True(input.Length < chunk.EmbeddingText.Length);
        Assert.StartsWith(input, chunk.EmbeddingText, StringComparison.Ordinal); // a prefix: the leading text
        Assert.False(char.IsWhiteSpace(input[^1]));
        Assert.True(input.Length == chunk.EmbeddingText.Length || char.IsWhiteSpace(chunk.EmbeddingText[input.Length]), "the cut falls on a white-space boundary");

        // The longest prefix that fits: one more word would go over.
        var nextSpace = chunk.EmbeddingText.IndexOfAny([' ', '\n'], input.Length + 1);
        Assert.True(nextSpace < 0 || Counter(chunk.EmbeddingText.AsSpan(0, nextSpace)) > 64);
    }

    [Fact]
    public void WhenTheBreadcrumbAloneFillsTheCap_TheBreadcrumbIsEmbeddedWhole()
    {
        var breadcrumb = "src/" + string.Join("/", Enumerable.Range(0, 20).Select(i => $"folder{i}")) + "/File.cs > A > B > C";
        var chunk = MakeChunk(breadcrumb, "line one\nline two\nline three");
        Assert.Equal(breadcrumb, EmbeddingInput.For(chunk, EmbeddingInput.MinimumCap, Counter));
    }

    [Fact]
    public async Task UnderACap_TheModelSeesAtMostTheCap_WhileTheStoredTextAndTokenCountStayComplete()
    {
        Fixtures.Copy("shop", _workspace.Repository);
        var embedder = new FakeEmbedder();
        var settings = new PillionSettings();
        settings.Indexing.EmbeddingTokenCap = 64;
        await using var harness = new IndexingHarness(_workspace.Data, embedder, settings: settings);
        await harness.StartAsync(_workspace.Repository);
        await harness.WaitForIdleAsync();

        Assert.NotEmpty(embedder.Texts);
        Assert.All(embedder.Texts, text => Assert.InRange(Counter(text), 1, 64));

        var longChunks = 0;
        foreach (var file in await harness.Root.Store.Files.GetAllAsync())
        {
            foreach (var chunk in await harness.Root.Storage.Chunks.GetChunksByFileAsync(file.Id))
            {
                // What is stored is the whole chunk, counted whole.
                Assert.Equal(Counter(chunk.Breadcrumb + "\n" + chunk.Text), chunk.TokenCount);
                if (chunk.TokenCount > 64)
                {
                    longChunks++;
                    Assert.DoesNotContain(chunk.Breadcrumb + "\n" + chunk.Text, embedder.Texts);
                    Assert.Contains(EmbeddingInput.For(chunk.Breadcrumb, chunk.Text, chunk.TokenCount, 64, Counter), embedder.Texts);
                }
            }
        }

        Assert.True(longChunks > 0, "the fixture has chunks over the cap");
        Assert.Equal(longChunks, harness.Service.LastRunStatistics!.ChunksTruncatedForEmbedding);
        Assert.Equal(harness.Service.LastRunStatistics.Chunks, embedder.ChunksEmbedded);

        // The lexical entry holds the whole text: a word from the tail of a long chunk is found.
        var tail = await TailWordOfALongChunkAsync(harness.Root.Storage, 64);
        Assert.NotEmpty(await harness.Root.Storage.Chunks.SearchLexicalAsync(tail, 5));

        // The cap is part of the index identity rows.
        Assert.Equal("64", await ReadMetaAsync(harness.Root.Storage, IndexMetaKeysExtra.EmbeddingTokenCap));
    }

    [Fact]
    public async Task WithTheCapOff_EveryChunkIsEmbeddedWhole_AndTheIndexRecordsZero()
    {
        Fixtures.Copy("shop", _workspace.Repository);
        var embedder = new FakeEmbedder();
        await using var harness = new IndexingHarness(_workspace.Data, embedder);
        await harness.StartAsync(_workspace.Repository);
        await harness.WaitForIdleAsync();

        Assert.Equal(0, harness.Service.LastRunStatistics!.ChunksTruncatedForEmbedding);
        Assert.Equal("0", await ReadMetaAsync(harness.Root.Storage, IndexMetaKeysExtra.EmbeddingTokenCap));
        Assert.Contains(embedder.Texts, text => Counter(text) > 64);
    }

    [Fact]
    public async Task ChangingTheCap_ReEmbedsTheIndex_AndTheSameCapDoesNot()
    {
        Fixtures.Copy("shop", _workspace.Repository);
        long chunks;
        await using (var first = new IndexingHarness(_workspace.Data, new FakeEmbedder()))
        {
            await first.StartAsync(_workspace.Repository);
            await first.WaitForIdleAsync();
            chunks = first.Service.LastRunStatistics!.Chunks;
            Assert.Equal("0", await ReadMetaAsync(first.Root.Storage, IndexMetaKeysExtra.EmbeddingTokenCap));
        }

        // Another host opens the same index with a cap: the vectors were made from other inputs, so every chunk is embedded again.
        var capped = new FakeEmbedder();
        var settings = new PillionSettings();
        settings.Indexing.EmbeddingTokenCap = 64;
        await using (var second = new IndexingHarness(_workspace.Data, capped, settings: settings))
        {
            await second.StartAsync(_workspace.Repository);
            await WaitForFullRunAsync(second);
            Assert.Equal(chunks, capped.ChunksEmbedded);
            Assert.Equal("64", await ReadMetaAsync(second.Root.Storage, IndexMetaKeysExtra.EmbeddingTokenCap));
        }

        // The same cap again: nothing to re-embed.
        var same = new FakeEmbedder();
        var sameSettings = new PillionSettings();
        sameSettings.Indexing.EmbeddingTokenCap = 64;
        await using (var third = new IndexingHarness(_workspace.Data, same, settings: sameSettings))
        {
            await third.StartAsync(_workspace.Repository);
            await WaitForFullRunAsync(third);
            Assert.Equal(0, same.ChunksEmbedded);
            Assert.Equal("64", await ReadMetaAsync(third.Root.Storage, IndexMetaKeysExtra.EmbeddingTokenCap));

            // A settings change while the host runs queues a run, which rebuilds with the new cap.
            var changed = new PillionSettings();
            changed.Indexing.EmbeddingTokenCap = 128;
            var runs = third.Service.FullRuns;
            third.Settings.Update(changed);
            await third.WaitForAsync(s => third.Service.FullRuns > runs && s.Phase == Pillion.Contracts.IndexPhase.Idle, what: "the run after the cap changed");
            Assert.Equal(chunks, same.ChunksEmbedded);
            Assert.Equal("128", await ReadMetaAsync(third.Root.Storage, IndexMetaKeysExtra.EmbeddingTokenCap));
        }
    }

    [Fact]
    public async Task AnIndexBuiltBeforeTheOption_CountsAsCapOff_AndIsNotRebuilt()
    {
        Fixtures.Copy("shop", _workspace.Repository);
        await using (var first = new IndexingHarness(_workspace.Data, new FakeEmbedder()))
        {
            await first.StartAsync(_workspace.Repository);
            await first.WaitForIdleAsync();
            await first.Root.Storage.WriteAsync(
                (session, _) =>
                {
                    using var command = session.Connection.CreateCommand();
                    command.Transaction = session.Transaction;
                    command.CommandText = "DELETE FROM meta WHERE key = $key";
                    command.Parameters.AddWithValue("$key", IndexMetaKeysExtra.EmbeddingTokenCap);
                    command.ExecuteNonQuery();
                    return true;
                },
                CancellationToken.None);
        }

        var embedder = new FakeEmbedder();
        await using var second = new IndexingHarness(_workspace.Data, embedder);
        await second.StartAsync(_workspace.Repository);
        await WaitForFullRunAsync(second);
        Assert.Equal(0, embedder.ChunksEmbedded);
        Assert.Equal("0", await ReadMetaAsync(second.Root.Storage, IndexMetaKeysExtra.EmbeddingTokenCap));
    }

    /// <summary>
    /// Waits for the first full run of a host over an index another host built: <c>state.json</c> already carries a full
    /// sweep, so an idle wait would return before the run starts.
    /// </summary>
    private static Task<Pillion.Contracts.IndexStatus> WaitForFullRunAsync(IndexingHarness harness) =>
        harness.WaitForAsync(s => harness.Service.FullRuns >= 1 && s.Phase == Pillion.Contracts.IndexPhase.Idle, what: "the first full run of this host");

    private static Chunk MakeChunk(string breadcrumb, string text) => new()
    {
        FilePath = "src/A.cs",
        StartLine = 1,
        EndLine = text.Count(c => c == '\n') + 1,
        Kind = ChunkKind.Member,
        Breadcrumb = breadcrumb,
        Text = text,
        TokenCount = Counter(breadcrumb + "\n" + text),
    };

    /// <summary>A word from the last line of a chunk over the cap, which the model never saw.</summary>
    private static async Task<string> TailWordOfALongChunkAsync(IndexStorage storage, int cap)
    {
        foreach (var file in await storage.Chunks.ListFilesAsync())
        {
            foreach (var chunk in await storage.Chunks.GetChunksByFileAsync(file.FileId))
            {
                if (chunk.TokenCount <= cap)
                {
                    continue;
                }

                var seen = EmbeddingInput.For(chunk.Breadcrumb, chunk.Text, chunk.TokenCount, cap, Counter);
                var unseen = chunk.Text[(seen.Length - chunk.Breadcrumb.Length - 1)..];
                var word = unseen.Split([' ', '\n', '\r', '\t', '(', ')', ';', '{', '}', '.', ',', '"'], StringSplitOptions.RemoveEmptyEntries)
                    .FirstOrDefault(w => w.Length >= 6 && w.All(char.IsLetter));
                if (word is not null)
                {
                    return word;
                }
            }
        }

        throw new InvalidOperationException("No long chunk with a searchable word past the cap.");
    }

    private static async Task<string?> ReadMetaAsync(IndexStorage storage, string key) =>
        await storage.Database.ReadAsync(
            (connection, _) =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT value FROM meta WHERE key = $key";
                command.Parameters.AddWithValue("$key", key);
                return Task.FromResult(command.ExecuteScalar() as string);
            });
}
