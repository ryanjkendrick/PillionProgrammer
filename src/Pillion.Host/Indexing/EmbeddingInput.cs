using Pillion.ContextEngine.Chunking;

namespace Pillion.Host.Indexing;

/// <summary>
/// The text a chunk hands to the embedding model under the <c>indexing.embeddingTokenCap</c> experiment (work item 3.4,
/// issue #90): the whole embedding text when the chunk fits the cap, otherwise the breadcrumb first and then as much of the
/// leading text as fits. The breadcrumb carries the symbol names, so it is never cut; the text is cut at a white-space
/// boundary. Only the model's input changes: the stored text, the lexical entry and <c>token_count</c> stay complete.
/// </summary>
internal static class EmbeddingInput
{
    /// <summary>The smallest cap that is applied; a smaller positive value is raised to it (room for a breadcrumb and a line).</summary>
    public const int MinimumCap = 16;

    /// <summary>
    /// Normalises the configured cap: zero or less is off (0); a cap at or above the chunk cap is off too, since no chunk
    /// exceeds it; a small positive cap is raised to <see cref="MinimumCap"/>. The result is what the index records.
    /// </summary>
    /// <param name="configured">The <c>indexing.embeddingTokenCap</c> setting.</param>
    /// <param name="chunkMaxTokens">The chunk cap of the index, in the model's tokens.</param>
    /// <returns>The effective cap, or 0 for off.</returns>
    public static int EffectiveCap(int configured, int chunkMaxTokens)
    {
        if (configured <= 0 || configured >= chunkMaxTokens)
        {
            return 0;
        }

        return Math.Max(MinimumCap, configured);
    }

    /// <summary>Builds the model input of a chunk.</summary>
    /// <param name="chunk">The chunk.</param>
    /// <param name="cap">The effective cap from <see cref="EffectiveCap"/>; 0 for off.</param>
    /// <param name="counter">The model's token counter.</param>
    /// <returns>The text to embed.</returns>
    public static string For(Chunk chunk, int cap, TokenCounter counter)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        return For(chunk.Breadcrumb, chunk.Text, chunk.TokenCount, cap, counter);
    }

    /// <summary>Builds the model input of a chunk from its stored parts.</summary>
    /// <param name="breadcrumb">The breadcrumb.</param>
    /// <param name="text">The chunk text.</param>
    /// <param name="tokenCount">The token count of the breadcrumb, a line break and the text, in the model's tokens.</param>
    /// <param name="cap">The effective cap; 0 for off.</param>
    /// <param name="counter">The model's token counter.</param>
    /// <returns>The text to embed: the full embedding text when it fits, otherwise the breadcrumb and the leading text that fit.</returns>
    public static string For(string breadcrumb, string text, int tokenCount, int cap, TokenCounter counter)
    {
        ArgumentNullException.ThrowIfNull(breadcrumb);
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(counter);
        var full = breadcrumb + "\n" + text;
        if (cap <= 0 || tokenCount <= cap)
        {
            return full;
        }

        var head = breadcrumb + "\n";
        if (counter(head) >= cap)
        {
            // The breadcrumb alone fills the cap: it is embedded whole and the text is left out.
            return breadcrumb;
        }

        // The longest prefix of the text that fits, by binary search over its length (token counts grow with the text).
        int low = 0, high = text.Length;
        while (low < high)
        {
            var middle = low + ((high - low + 1) / 2);
            if (counter(full.AsSpan(0, head.Length + middle)) <= cap)
            {
                low = middle;
            }
            else
            {
                high = middle - 1;
            }
        }

        // Cut at the last white space before the limit so no word is split, and make sure the result still fits.
        var length = low;
        while (length > 0)
        {
            var boundary = text.LastIndexOfAny(['\n', '\r', ' ', '\t'], length - 1);
            var cut = boundary > 0 ? boundary : length;
            var candidate = text.AsSpan(0, cut).TrimEnd();
            if (candidate.Length == 0)
            {
                break;
            }

            if (counter(string.Concat(head, candidate)) <= cap)
            {
                return string.Concat(head, candidate);
            }

            length = candidate.Length - 1;
        }

        return breadcrumb;
    }
}
