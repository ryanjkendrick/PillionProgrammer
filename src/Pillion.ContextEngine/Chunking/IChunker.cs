namespace Pillion.ContextEngine.Chunking;

/// <summary>Splits one file into chunks for the index.</summary>
/// <remarks>
/// Implementations never throw because of the content of <c>text</c>: malformed code falls back to the window chunker.
/// They are safe to call from several threads at once.
/// </remarks>
public interface IChunker
{
    /// <summary>Chunks one file.</summary>
    /// <param name="path">The file's path as it should appear in breadcrumbs (usually relative to the repository root).</param>
    /// <param name="text">The file's text.</param>
    /// <param name="options">The token cap, the token counter and the window settings.</param>
    /// <returns>The chunks in line order; empty when the text is empty or only white space.</returns>
    IReadOnlyList<Chunk> Chunk(string path, string text, ChunkOptions options);
}
