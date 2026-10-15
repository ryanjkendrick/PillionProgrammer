using System.Globalization;
using Microsoft.ML.Tokenizers;

namespace Pillion.ContextEngine.Chunking;

/// <summary>Counts the tokens an embedding model would see for a piece of text, including any special tokens.</summary>
/// <param name="text">The text.</param>
/// <returns>The token count.</returns>
public delegate int TokenCounter(ReadOnlySpan<char> text);

/// <summary>Ready-made <see cref="TokenCounter"/>s.</summary>
public static class TokenCounters
{
    /// <summary>
    /// A deterministic approximation of BERT WordPiece counting, used when the embedding model's vocabulary is not at
    /// hand (tests, and until the bundled model lands in work item 2.7). It follows BERT's basic tokenisation (white
    /// space splits words, every punctuation or symbol character and every CJK character is a token of its own), then
    /// charges a word of up to four characters one token and one more for every three characters after that, which is
    /// at or above what a WordPiece vocabulary produces for identifiers and code. Two tokens are added for
    /// <c>[CLS]</c> and <c>[SEP]</c>. Counts are additive across line breaks, which the chunkers rely on only for
    /// estimates; every chunk is still checked with a real count.
    /// </summary>
    public static TokenCounter ApproximateWordPiece { get; } = CountApproximateWordPiece;

    /// <summary>Counts with a Microsoft.ML.Tokenizers tokenizer, such as the embedding model's own.</summary>
    /// <param name="tokenizer">The tokenizer; it must be safe to call from several threads.</param>
    /// <returns>A counter that calls <see cref="Tokenizer.CountTokens(ReadOnlySpan{char}, bool, bool)"/>.</returns>
    public static TokenCounter FromTokenizer(Tokenizer tokenizer)
    {
        ArgumentNullException.ThrowIfNull(tokenizer);
        return text => tokenizer.CountTokens(text);
    }

    /// <summary>
    /// Counts with a BERT WordPiece tokenizer built from a <c>vocab.txt</c> (one token per line), the format the bundled
    /// <c>bge-small-en-v1.5</c> model ships its vocabulary in.
    /// </summary>
    /// <param name="vocabulary">The vocabulary stream; read to the end, not disposed.</param>
    /// <param name="lowerCase">Whether the model lower-cases its input (true for uncased models such as bge-small).</param>
    /// <returns>The counter.</returns>
    public static TokenCounter FromWordPieceVocabulary(Stream vocabulary, bool lowerCase = true)
    {
        ArgumentNullException.ThrowIfNull(vocabulary);
        var tokenizer = BertTokenizer.Create(vocabulary, new BertOptions { LowerCaseBeforeTokenization = lowerCase });
        return FromTokenizer(tokenizer);
    }

    private static int CountApproximateWordPiece(ReadOnlySpan<char> text)
    {
        var tokens = 2; // [CLS] and [SEP]
        var word = 0;
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c))
            {
                tokens += WordTokens(word);
                word = 0;
            }
            else if (IsStandalone(c))
            {
                tokens += WordTokens(word) + 1;
                word = 0;
            }
            else
            {
                word++;
            }
        }

        return tokens + WordTokens(word);
    }

    private static int WordTokens(int length) => length == 0 ? 0 : length <= 4 ? 1 : 1 + ((length - 4 + 2) / 3);

    private static bool IsStandalone(char c)
    {
        if (c < 128)
        {
            return !char.IsAsciiLetterOrDigit(c);
        }

        if (c is >= '一' and <= '鿿' or >= '㐀' and <= '䶿' or >= '豈' and <= '﫿')
        {
            return true;
        }

        return CharUnicodeInfo.GetUnicodeCategory(c) switch
        {
            UnicodeCategory.ConnectorPunctuation or UnicodeCategory.DashPunctuation or UnicodeCategory.OpenPunctuation
                or UnicodeCategory.ClosePunctuation or UnicodeCategory.InitialQuotePunctuation
                or UnicodeCategory.FinalQuotePunctuation or UnicodeCategory.OtherPunctuation
                or UnicodeCategory.MathSymbol or UnicodeCategory.CurrencySymbol or UnicodeCategory.ModifierSymbol
                or UnicodeCategory.OtherSymbol or UnicodeCategory.Surrogate => true,
            _ => false,
        };
    }
}
