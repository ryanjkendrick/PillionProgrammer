using Pillion.ContextEngine.Chunking;

namespace Pillion.ContextEngine.Tests;

/// <summary>Reads a fixture and chunks it with the default catalog.</summary>
internal static class Fixture
{
    public static string Read(string relative) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", relative));

    /// <summary>Chunks a fixture under the path <c>src/&lt;file name&gt;</c>, checks the invariants and fails on a fallback.</summary>
    public static IReadOnlyList<Chunk> Chunk(string relative, ChunkOptions? options = null)
    {
        var fallbacks = new List<ChunkerFallback>();
        options = (options ?? ChunkOptions.Default) with { OnFallback = fallbacks.Add };
        var path = "src/" + Path.GetFileName(relative);
        var text = Read(relative);
        var chunks = new FileChunker().Chunk(path, text, options);
        Assert.True(fallbacks.Count == 0, string.Join("; ", fallbacks.Select(f => f.Reason)));
        ChunkInvariants.Assert(path, text, chunks, options);
        return chunks;
    }

    public static Chunk Single(IReadOnlyList<Chunk> chunks, string breadcrumb)
    {
        var matches = chunks.Where(c => c.Breadcrumb == breadcrumb).ToList();
        Assert.True(matches.Count == 1, $"Expected one chunk '{breadcrumb}', found {matches.Count}:{Environment.NewLine}{ChunkInvariants.Describe(chunks)}");
        return matches[0];
    }
}
