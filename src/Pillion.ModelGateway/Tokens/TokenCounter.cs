using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.ML.Tokenizers;

namespace Pillion.ModelGateway;

/// <summary>
/// Counts tokens for a model (design section 7, Context assembly): exactly with Microsoft.ML.Tokenizers for the OpenAI
/// (tiktoken) and Llama 3 families, with a calibrated estimate and a 10% safety margin for the others. Shared by the
/// autocomplete prompt budget and context assembly.
/// </summary>
public interface ITokenCounter
{
    /// <summary>Gets the counter for a model.</summary>
    /// <param name="modelId">The model id, bare or qualified (<c>provider/model</c>); a namespace such as <c>library/</c> is ignored.</param>
    /// <returns>The counter for the model's family.</returns>
    IModelTokenCounter ForModel(string modelId);
}

/// <summary>Counts tokens for one model family.</summary>
public interface IModelTokenCounter
{
    /// <summary>Gets the family name, for example <c>qwen2.5-coder</c> or <c>openai-o200k</c>.</summary>
    string Family { get; }

    /// <summary>Gets a value indicating whether counts come from the family's tokenizer rather than an estimate.</summary>
    bool IsExact { get; }

    /// <summary>Counts the tokens of a text.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The count (an estimate rounds up and includes the safety margin).</returns>
    int Count(string text);
}

/// <summary>
/// The default <see cref="ITokenCounter"/>, driven by <c>Tokens/token-families.json</c>. Tokenizers are created on
/// first use and shared; counters are cached per model id. Thread-safe.
/// </summary>
public sealed class TokenCounter : ITokenCounter
{
    private static readonly ConcurrentDictionary<string, Lazy<Tokenizer>> Tokenizers = new(StringComparer.Ordinal);

    private readonly List<(Regex[] Patterns, IModelTokenCounter Counter)> _families;
    private readonly ConcurrentDictionary<string, IModelTokenCounter> _byModel = new(StringComparer.OrdinalIgnoreCase);

    private TokenCounter(List<(Regex[], IModelTokenCounter)> families, double safetyMargin)
    {
        _families = families;
        SafetyMargin = safetyMargin;
    }

    /// <summary>Gets the counter built from the families shipped in this assembly.</summary>
    public static TokenCounter Default { get; } = LoadEmbedded();

    /// <summary>Gets the safety margin added to estimates (0.10 is 10%).</summary>
    public double SafetyMargin { get; }

    /// <summary>Gets the families in match order, for diagnostics and tests.</summary>
    public IReadOnlyList<IModelTokenCounter> Families => _families.Select(f => f.Counter).ToList();

    /// <inheritdoc/>
    public IModelTokenCounter ForModel(string modelId)
    {
        ArgumentNullException.ThrowIfNull(modelId);
        return _byModel.GetOrAdd(modelId, id =>
        {
            var name = id[(id.LastIndexOf('/') + 1)..];
            foreach (var (patterns, counter) in _families)
            {
                if (patterns.Any(p => p.IsMatch(name)))
                {
                    return counter;
                }
            }

            return _families[^1].Counter;
        });
    }

    /// <summary>Parses a token-families document.</summary>
    /// <param name="json">The document.</param>
    /// <returns>The counter.</returns>
    /// <exception cref="FormatException">The document is not valid.</exception>
    public static TokenCounter Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var margin = root.GetProperty("safetyMargin").GetDouble();
            var families = new List<(Regex[], IModelTokenCounter)>();
            foreach (var entry in root.GetProperty("families").EnumerateArray())
            {
                var family = entry.GetProperty("family").GetString()!;
                var match = entry.GetProperty("match");
                var globs = match.ValueKind == JsonValueKind.Array ? match.EnumerateArray().Select(m => m.GetString()!).ToArray() : [match.GetString()!];
                var tokenizer = entry.GetProperty("tokenizer").GetString()!;
                IModelTokenCounter counter = tokenizer switch
                {
                    "o200k_base" or "cl100k_base" => new TiktokenCounter(family, tokenizer),
                    "estimate" => new EstimatingCounter(
                        family,
                        entry.GetProperty("charsPerToken").GetDouble(),
                        entry.TryGetProperty("digitTokens", out var digits) ? digits.GetDouble() : 0,
                        margin),
                    _ => throw new FormatException($"Unknown tokenizer '{tokenizer}' for family '{family}'."),
                };
                families.Add((globs.Select(ToRegex).ToArray(), counter));
            }

            if (families.Count == 0 || !families[^1].Item1.Any(r => r.IsMatch("anything")))
            {
                throw new FormatException("The last token family must match '*'.");
            }

            return new TokenCounter(families, margin);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new FormatException($"The token families document is not valid: {ex.Message}", ex);
        }
    }

    private static TokenCounter LoadEmbedded()
    {
        const string Name = "Pillion.ModelGateway.Tokens.token-families.json";
        using var stream = typeof(TokenCounter).Assembly.GetManifestResourceStream(Name)
            ?? throw new InvalidOperationException($"The embedded resource {Name} is missing.");
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    }

    private static Regex ToRegex(string glob) =>
        new("^" + Regex.Escape(glob).Replace("\\*", ".*", StringComparison.Ordinal) + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>An exact counter over a tiktoken encoding, created on first use and shared by every family that uses it.</summary>
    private sealed class TiktokenCounter(string family, string encoding) : IModelTokenCounter
    {
        private readonly Lazy<Tokenizer> _tokenizer = Tokenizers.GetOrAdd(
            encoding,
            name => new Lazy<Tokenizer>(() => TiktokenTokenizer.CreateForEncoding(name), LazyThreadSafetyMode.ExecutionAndPublication));

        public string Family => family;

        public bool IsExact => true;

        public int Count(string text) => string.IsNullOrEmpty(text) ? 0 : _tokenizer.Value.CountTokens(text);
    }
}

/// <summary>
/// A calibrated estimate: every decimal digit counts <c>digitTokens</c> tokens, every other character
/// <c>1 / charsPerToken</c>, and the total is raised by the safety margin and rounded up.
/// </summary>
/// <param name="Family">The family name.</param>
/// <param name="CharsPerToken">The measured non-digit characters per token.</param>
/// <param name="DigitTokens">The tokens per decimal digit (1 for tokenizers that split numbers into digits).</param>
/// <param name="SafetyMargin">The safety margin (0.10 is 10%).</param>
public sealed record EstimatingCounter(string Family, double CharsPerToken, double DigitTokens, double SafetyMargin) : IModelTokenCounter
{
    /// <inheritdoc/>
    public bool IsExact => false;

    /// <inheritdoc/>
    public int Count(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        var digits = 0;
        foreach (var c in text)
        {
            if (char.IsAsciiDigit(c))
            {
                digits++;
            }
        }

        var raw = ((text.Length - digits) / CharsPerToken) + (digits * DigitTokens);
        return (int)Math.Ceiling(raw * (1 + SafetyMargin));
    }
}
