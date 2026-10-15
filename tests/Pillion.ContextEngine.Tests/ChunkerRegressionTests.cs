using System.Diagnostics;
using Pillion.ContextEngine.Chunking;

namespace Pillion.ContextEngine.Tests;

/// <summary>Inputs the property tests found that a chunker handles badly, kept so the fix can be checked.</summary>
public sealed class ChunkerRegressionTests
{
    /// <summary>
    /// An 8 KB file of random code-like tokens from <see cref="ChunkerPropertyTests"/>' generator (found by work item 2.17
    /// when <c>Random_files_satisfy_the_chunk_invariants</c> took 4 minutes in one of five full runs): parsed as <c>.tsx</c>
    /// the tree-sitter TSX grammar takes about six minutes on the reference machine, while the same text as <c>.ts</c> takes
    /// 40 ms. Issue #101: every tree-sitter parse is capped (<see cref="ChunkOptions.ParseTimeout"/>, 2 s by default), after
    /// which the file is chunked by windows and the fallback is reported.
    /// </summary>
    [Fact]
    public void Tsx_RandomTokens_AreCutOffAtTheParseCap_AndChunkedByWindows()
    {
        var text = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Regressions", "tsx-slow-parse.txt"));
        var fallbacks = new List<ChunkerFallback>();
        var options = ChunkOptions.Default with { MaxTokens = 117, OnFallback = fallbacks.Add };
        Assert.Equal(TimeSpan.FromSeconds(2), options.ParseTimeout);

        var watch = Stopwatch.StartNew();
        var chunks = new FileChunker().Chunk("dir/sub/file.tsx", text, options);
        watch.Stop();

        ChunkInvariants.Assert("dir/sub/file.tsx", text, chunks, options);
        var fallback = Assert.Single(fallbacks);
        Assert.Equal("dir/sub/file.tsx", fallback.Path);
        Assert.Contains("longer than 2 s", fallback.Reason, StringComparison.Ordinal);
        Assert.True(watch.Elapsed >= TimeSpan.FromSeconds(2), $"chunked in {watch.Elapsed.TotalSeconds:0.00} s, before the cap");
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"chunked in {watch.Elapsed.TotalSeconds:0.00} s; the cap is 2 s");
    }

    /// <summary>The cap is configurable, applies to every tree-sitter language, and a parse inside it is untouched.</summary>
    [Fact]
    public void TheParseCap_IsConfigurable_AndAParseInsideItIsUntouched()
    {
        var text = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Regressions", "tsx-slow-parse.txt"));
        var fallbacks = new List<ChunkerFallback>();

        var watch = Stopwatch.StartNew();
        var chunks = new FileChunker().Chunk("file.tsx", text, ChunkOptions.Default with { ParseTimeout = TimeSpan.FromMilliseconds(200), OnFallback = fallbacks.Add });
        watch.Stop();

        Assert.NotEmpty(chunks);
        Assert.Contains("longer than 0.2 s", Assert.Single(fallbacks).Reason, StringComparison.Ordinal);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3), $"chunked in {watch.Elapsed.TotalSeconds:0.00} s; the cap is 0.2 s");

        // The same text as TypeScript parses well inside the cap: the syntactic chunker's result, no fallback.
        fallbacks.Clear();
        var typescript = new FileChunker().Chunk("file.ts", text, ChunkOptions.Default with { OnFallback = fallbacks.Add });
        Assert.DoesNotContain(fallbacks, f => f.Reason.Contains("longer than", StringComparison.Ordinal));
        Assert.Equal(new FileChunker().Chunk("file.ts", text, ChunkOptions.Default with { ParseTimeout = Timeout.InfiniteTimeSpan }), typescript);
    }

    /// <summary>Symbol extraction uses the same cap: past it the extraction is empty, marked incomplete, and the failure reported.</summary>
    [Fact]
    public void Tsx_RandomTokens_SymbolExtractionIsCutOffAtTheParseCap()
    {
        var text = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Regressions", "tsx-slow-parse.txt"));
        var failures = new List<Pillion.ContextEngine.Symbols.SymbolExtractionFailure>();
        var options = new Pillion.ContextEngine.Symbols.SymbolExtractionOptions { OnFailure = failures.Add };
        Assert.Equal(TimeSpan.FromSeconds(2), options.ParseTimeout);

        var watch = Stopwatch.StartNew();
        var extraction = new Pillion.ContextEngine.Symbols.FileSymbolExtractor().Extract("dir/sub/file.tsx", text, options);
        watch.Stop();

        Assert.False(extraction.IsComplete);
        Assert.Empty(extraction.Definitions);
        Assert.Contains("longer than 2 s", Assert.Single(failures).Reason, StringComparison.Ordinal);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"extracted in {watch.Elapsed.TotalSeconds:0.00} s; the cap is 2 s");
    }
}
