using System.Diagnostics;
using Pillion.ContextEngine.Chunking;

namespace Pillion.ContextEngine.Tests;

/// <summary>Inputs the property tests found that a chunker handles badly, kept so the fix can be checked.</summary>
public sealed class ChunkerRegressionTests
{
    /// <summary>
    /// An 8 KB file of random code-like tokens from <see cref="ChunkerPropertyTests"/>' generator (found by work item 2.17
    /// when <c>Random_files_satisfy_the_chunk_invariants</c> took 4 minutes in one of five full runs): chunked as
    /// <c>.tsx</c> it takes about six minutes on the reference machine, at any cap, while the same text as <c>.ts</c> takes
    /// 40 ms and as <c>.txt</c> under 1 ms. A file like this in a repository would hold the indexing pipeline for minutes.
    /// See open issue 1 of docs/gate-2-report.md.
    /// </summary>
    [Fact(Skip = "Open: the TSX chunker takes minutes on this input (docs/gate-2-report.md, open issue 1)")]
    public void Tsx_RandomTokens_ChunkWithinTwoSeconds()
    {
        var text = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Regressions", "tsx-slow-parse.txt"));

        var watch = Stopwatch.StartNew();
        var chunks = new FileChunker().Chunk("dir/sub/file.tsx", text, ChunkOptions.Default with { MaxTokens = 117 });
        watch.Stop();

        ChunkInvariants.Assert("dir/sub/file.tsx", text, chunks, ChunkOptions.Default with { MaxTokens = 117 });
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), $"chunked in {watch.Elapsed.TotalSeconds:0.0} s");
    }
}
