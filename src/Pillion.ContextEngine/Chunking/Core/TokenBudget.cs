namespace Pillion.ContextEngine.Chunking.Core;

/// <summary>
/// Token arithmetic for one document: the counter, the per-text overhead (special tokens, measured as the count of an
/// empty string), and every line's token count once, with prefix sums, so the size of any line range can be estimated
/// in constant time. Estimates assume counts add up across line breaks, which holds for WordPiece and the approximate
/// counter; every emitted chunk is still checked with a real count.
/// </summary>
internal sealed class TokenBudget
{
    private readonly TokenCounter _counter;
    private readonly long[] _prefix;

    public TokenBudget(SourceDocument document, ChunkOptions options)
    {
        Document = document;
        Options = options;
        _counter = options.TokenCounter;
        Overhead = Math.Max(0, _counter(ReadOnlySpan<char>.Empty));

        _prefix = new long[document.LineCount + 1];
        for (var line = 1; line <= document.LineCount; line++)
        {
            var tokens = Math.Max(0, _counter(document.LineSpan(line)) - Overhead);
            _prefix[line] = _prefix[line - 1] + tokens;
        }
    }

    public SourceDocument Document { get; }

    public ChunkOptions Options { get; }

    public int MaxTokens => Options.MaxTokens;

    /// <summary>Tokens every counted text carries regardless of content (for BERT, <c>[CLS]</c> and <c>[SEP]</c>).</summary>
    public int Overhead { get; }

    /// <summary>The real token count of a text.</summary>
    public int Count(ReadOnlySpan<char> text) => _counter(text);

    /// <summary>The token count of a text without the overhead.</summary>
    public int CountContent(string text) => Math.Max(0, _counter(text) - Overhead);

    /// <summary>The tokens of one line, without the overhead.</summary>
    public long LineTokens(int line) => _prefix[line] - _prefix[line - 1];

    /// <summary>The tokens of a line range, without the overhead.</summary>
    public long RangeTokens(int startLine, int endLine) => _prefix[endLine] - _prefix[startLine - 1];
}
