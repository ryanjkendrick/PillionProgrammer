using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.ML.Tokenizers;

namespace Pillion.ModelGateway.Providers.Bundled;

/// <summary>
/// Counts tokens the way an embedding model reads them, so that the chunker can cap chunks in the model's own tokenizer
/// (design section 7, Parse and chunk: 512 WordPiece tokens for the bundled model).
/// </summary>
public interface IEmbeddingTokenCounter
{
    /// <summary>Gets the tokenizer family (<c>bert-wordpiece</c>, <c>xlmr-sentencepiece</c>, <c>roberta-bpe</c>).</summary>
    string TokenizerFamily { get; }

    /// <summary>Gets the most tokens the model reads from one text, the special tokens included; longer texts are truncated.</summary>
    int MaxTokens { get; }

    /// <summary>Counts the tokens of a text as the model would see it untruncated, the special tokens included.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The count; a text above <see cref="MaxTokens"/> is truncated when embedded.</returns>
    int CountTokens(string text);
}

/// <summary>
/// The tokenizer of an embedding model, built on Microsoft.ML.Tokenizers and checked against the Hugging Face tokenizer of
/// each model. Every supported family frames the text as <c>begin, content, end</c> (<c>[CLS] ... [SEP]</c> or
/// <c>&lt;s&gt; ... &lt;/s&gt;</c>) and truncates the content, keeping the end token, like the Python tokenizer with
/// <c>truncation=True</c>. Thread-safe.
/// </summary>
public abstract class EmbeddingTokenizer : IEmbeddingTokenCounter
{
    /// <summary>Initializes a new instance of the <see cref="EmbeddingTokenizer"/> class.</summary>
    /// <param name="maxTokens">The most tokens the model reads, special tokens included.</param>
    /// <param name="beginId">The id of the token before the content.</param>
    /// <param name="endId">The id of the token after the content.</param>
    /// <param name="padId">The id of the padding token.</param>
    protected EmbeddingTokenizer(int maxTokens, int beginId, int endId, int padId)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxTokens, 3);
        MaxTokens = maxTokens;
        BeginId = beginId;
        EndId = endId;
        PadId = padId;
    }

    /// <inheritdoc/>
    public abstract string TokenizerFamily { get; }

    /// <inheritdoc/>
    public int MaxTokens { get; }

    /// <summary>Gets the id of the token before the content.</summary>
    public int BeginId { get; }

    /// <summary>Gets the id of the token after the content.</summary>
    public int EndId { get; }

    /// <summary>Gets the id of the padding token.</summary>
    public int PadId { get; }

    /// <summary>Creates the tokenizer of a manifest model from its (verified) files.</summary>
    /// <param name="model">The model.</param>
    /// <param name="readFile">Reads a file of the model by role, or returns <see langword="null"/> when the model has none.</param>
    /// <param name="maxTokens">The most tokens to read, special tokens included; at most the model's own limit.</param>
    /// <returns>The tokenizer.</returns>
    /// <exception cref="InvalidOperationException">A file the tokenizer needs is missing or cannot be parsed.</exception>
    public static EmbeddingTokenizer Create(ManifestModel model, Func<ModelFileRole, byte[]?> readFile, int maxTokens)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(readFile);

        var limit = Math.Min(model.MaxTokens, maxTokens);
        try
        {
            return model.Tokenizer switch
            {
                ModelManifest.BertWordPiece => new WordPieceEmbeddingTokenizer(Need(readFile, ModelFileRole.Vocab, model), readFile(ModelFileRole.TokenizerConfig), limit),
                ModelManifest.XlmRobertaSentencePiece => new XlmRobertaEmbeddingTokenizer(Need(readFile, ModelFileRole.SentencePiece, model), limit),
                ModelManifest.RobertaBpe => new RobertaBpeEmbeddingTokenizer(Need(readFile, ModelFileRole.Tokenizer, model), limit),
                _ => throw new InvalidOperationException($"The tokenizer family '{model.Tokenizer}' of {model.Id} is not supported."),
            };
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or FormatException or KeyNotFoundException or InvalidDataException)
        {
            throw new InvalidOperationException($"The tokenizer of {model.Id} could not be loaded: {ex.Message}", ex);
        }
    }

    /// <inheritdoc/>
    public int CountTokens(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return EncodeContent(text).Count + 2;
    }

    /// <summary>Encodes a text for the model: the begin token, the content truncated to fit, the end token.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The token ids, at most <see cref="MaxTokens"/> of them.</returns>
    public int[] Encode(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var content = EncodeContent(text);
        var take = Math.Min(content.Count, MaxTokens - 2);
        var ids = new int[take + 2];
        ids[0] = BeginId;
        for (var i = 0; i < take; i++)
        {
            ids[i + 1] = content[i];
        }

        ids[^1] = EndId;
        return ids;
    }

    /// <summary>Encodes a text without special tokens and without truncation.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The token ids.</returns>
    protected abstract IReadOnlyList<int> EncodeContent(string text);

    private static byte[] Need(Func<ModelFileRole, byte[]?> readFile, ModelFileRole role, ManifestModel model) =>
        readFile(role) ?? throw new InvalidOperationException($"{model.Id} has no {role} file, which its tokenizer needs.");
}

/// <summary>
/// BERT WordPiece (BGE): Microsoft.ML.Tokenizers' <see cref="BertTokenizer"/> over the model's <c>vocab.txt</c>, with the
/// basic tokenisation of the Hugging Face <c>BertTokenizer</c>. Version 2.0.0 of Microsoft.ML.Tokenizers splits words from
/// punctuation with <c>\w+|\p{P}</c>, which drops ASCII symbols such as <c>&lt; = &gt;</c> and keeps <c>_</c> inside a
/// word, where the Python tokenizer splits every ASCII punctuation character (33-47, 58-64, 91-96, 123-126) and every
/// Unicode punctuation character into a token of its own; <see cref="BertPunctuationPreTokenizer"/> restores that rule, and
/// accents are stripped as the Python tokenizer does when it lower-cases.
/// </summary>
internal sealed class WordPieceEmbeddingTokenizer : EmbeddingTokenizer
{
    private readonly BertTokenizer _tokenizer;

    public WordPieceEmbeddingTokenizer(byte[] vocab, byte[]? tokenizerConfig, int maxTokens)
        : this(CreateTokenizer(vocab, ReadLowerCase(tokenizerConfig)), maxTokens)
    {
    }

    private WordPieceEmbeddingTokenizer(BertTokenizer tokenizer, int maxTokens)
        : base(maxTokens, tokenizer.ClassificationTokenId, tokenizer.SeparatorTokenId, tokenizer.PaddingTokenId)
    {
        _tokenizer = tokenizer;
    }

    public override string TokenizerFamily => ModelManifest.BertWordPiece;

    protected override IReadOnlyList<int> EncodeContent(string text) =>
        _tokenizer.EncodeToIds(text, false, true, true);

    private static BertTokenizer CreateTokenizer(byte[] vocab, bool lowerCase)
    {
        using var stream = new MemoryStream(vocab, writable: false);
        return BertTokenizer.Create(stream, new BertOptions
        {
            LowerCaseBeforeTokenization = lowerCase,

            // The Python tokenizer strips accents whenever it lower-cases and strip_accents is not set (BGE's case).
            RemoveNonSpacingMarks = lowerCase,
            PreTokenizer = BertPunctuationPreTokenizer.Instance,
        });
    }

    private static bool ReadLowerCase(byte[]? tokenizerConfig)
    {
        if (tokenizerConfig is null)
        {
            return true;
        }

        using var document = JsonDocument.Parse(tokenizerConfig);
        return !document.RootElement.TryGetProperty("do_lower_case", out var value) || value.ValueKind != JsonValueKind.False;
    }
}

/// <summary>
/// The word splitting of the Hugging Face BERT basic tokenizer: whitespace separates words, and every punctuation character
/// (ASCII 33-47, 58-64, 91-96 and 123-126, and the Unicode punctuation categories) is a word of its own.
/// </summary>
internal sealed class BertPunctuationPreTokenizer : PreTokenizer
{
    public static readonly BertPunctuationPreTokenizer Instance = new();

    public override IEnumerable<(int Offset, int Length)> PreTokenize(string text) => Split(text.AsSpan());

    public override IEnumerable<(int Offset, int Length)> PreTokenize(ReadOnlySpan<char> text) => Split(text);

    internal static bool IsPunctuation(char c) =>
        c is (>= '!' and <= '/') or (>= ':' and <= '@') or (>= '[' and <= '`') or (>= '{' and <= '~') ||
        CharUnicodeInfo.GetUnicodeCategory(c) is UnicodeCategory.ConnectorPunctuation or UnicodeCategory.DashPunctuation
            or UnicodeCategory.OpenPunctuation or UnicodeCategory.ClosePunctuation or UnicodeCategory.InitialQuotePunctuation
            or UnicodeCategory.FinalQuotePunctuation or UnicodeCategory.OtherPunctuation;

    private static List<(int Offset, int Length)> Split(ReadOnlySpan<char> text)
    {
        var words = new List<(int Offset, int Length)>();
        var start = -1;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c) || IsPunctuation(c))
            {
                if (start >= 0)
                {
                    words.Add((start, i - start));
                    start = -1;
                }

                if (!char.IsWhiteSpace(c))
                {
                    words.Add((i, 1));
                }
            }
            else if (start < 0)
            {
                start = i;
            }
        }

        if (start >= 0)
        {
            words.Add((start, text.Length - start));
        }

        return words;
    }
}

/// <summary>
/// XLM-RoBERTa SentencePiece (multilingual E5): Microsoft.ML.Tokenizers' <see cref="SentencePieceTokenizer"/> over
/// <c>sentencepiece.bpe.model</c> (a unigram model despite the name), with fairseq's id layout: <c>&lt;s&gt;</c> 0,
/// <c>&lt;pad&gt;</c> 1, <c>&lt;/s&gt;</c> 2, <c>&lt;unk&gt;</c> 3 and every SentencePiece id shifted up by one.
/// </summary>
internal sealed class XlmRobertaEmbeddingTokenizer : EmbeddingTokenizer
{
    private const int FairseqOffset = 1;
    private const int UnknownId = 3;
    private readonly SentencePieceTokenizer _tokenizer;

    public XlmRobertaEmbeddingTokenizer(byte[] model, int maxTokens)
        : base(maxTokens, beginId: 0, endId: 2, padId: 1)
    {
        using var stream = new MemoryStream(model, writable: false);
        _tokenizer = SentencePieceTokenizer.Create(stream, addBeginningOfSentence: false, addEndOfSentence: false);
    }

    public override string TokenizerFamily => ModelManifest.XlmRobertaSentencePiece;

    protected override IReadOnlyList<int> EncodeContent(string text)
    {
        var ids = _tokenizer.EncodeToIds(text, addBeginningOfSentence: false, addEndOfSentence: false);
        var shifted = new int[ids.Count];
        for (var i = 0; i < shifted.Length; i++)
        {
            shifted[i] = ids[i] == 0 ? UnknownId : ids[i] + FairseqOffset;
        }

        return shifted;
    }
}

/// <summary>
/// Byte-level BPE in the RoBERTa format (Jina v2 code): Microsoft.ML.Tokenizers' <see cref="BpeTokenizer"/> with the
/// vocabulary and merges of the model's <c>tokenizer.json</c> and the GPT-2 word-splitting expression, framed by
/// <c>&lt;s&gt;</c> and <c>&lt;/s&gt;</c>.
/// </summary>
internal sealed partial class RobertaBpeEmbeddingTokenizer : EmbeddingTokenizer
{
    private readonly BpeTokenizer _tokenizer;

    public RobertaBpeEmbeddingTokenizer(byte[] tokenizerJson, int maxTokens)
        : this(Load(tokenizerJson), maxTokens)
    {
    }

    private RobertaBpeEmbeddingTokenizer((BpeTokenizer Tokenizer, int Begin, int End, int Pad) loaded, int maxTokens)
        : base(maxTokens, loaded.Begin, loaded.End, loaded.Pad)
    {
        _tokenizer = loaded.Tokenizer;
    }

    public override string TokenizerFamily => ModelManifest.RobertaBpe;

    protected override IReadOnlyList<int> EncodeContent(string text) =>
        _tokenizer.EncodeToIds(text, considerPreTokenization: true, considerNormalization: true);

    [GeneratedRegex(@"'s|'t|'re|'ve|'m|'ll|'d| ?\p{L}+| ?\p{N}+| ?[^\s\p{L}\p{N}]+|\s+(?!\S)|\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Gpt2Words();

    private static (BpeTokenizer Tokenizer, int Begin, int End, int Pad) Load(byte[] tokenizerJson)
    {
        using var document = JsonDocument.Parse(tokenizerJson);
        var model = document.RootElement.GetProperty("model");
        var type = model.TryGetProperty("type", out var t) ? t.GetString() : null;
        if (!string.Equals(type, "BPE", StringComparison.OrdinalIgnoreCase))
        {
            throw new FormatException($"tokenizer.json describes a {type} model, not BPE.");
        }

        var vocabulary = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var entry in model.GetProperty("vocab").EnumerateObject())
        {
            vocabulary[entry.Name] = entry.Value.GetInt32();
        }

        var merges = new List<string>();
        foreach (var merge in model.GetProperty("merges").EnumerateArray())
        {
            merges.Add(merge.ValueKind == JsonValueKind.Array
                ? new StringBuilder(merge[0].GetString()).Append(' ').Append(merge[1].GetString()).ToString()
                : merge.GetString()!);
        }

        var tokenizer = BpeTokenizer.Create(new BpeOptions(vocabulary)
        {
            Merges = merges,
            ByteLevel = true,
            UnknownToken = "<unk>",
            PreTokenizer = new RegexPreTokenizer(Gpt2Words(), null),
        });
        return (tokenizer, vocabulary["<s>"], vocabulary["</s>"], vocabulary["<pad>"]);
    }
}
