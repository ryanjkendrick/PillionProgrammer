using System.Text.Json;
using Xunit.Abstractions;

namespace Pillion.ModelGateway.Tests.Autocomplete;

public sealed class TokenCounterTests(ITestOutputHelper output)
{
    internal static string CorpusDirectory => Path.Combine(AppContext.BaseDirectory, "Fixtures", "Tokens", "corpus");

    internal static string ReadCorpus(string name) => File.ReadAllText(Path.Combine(CorpusDirectory, name)).Replace("\r\n", "\n", StringComparison.Ordinal);

    [Theory]
    [InlineData("gpt-4o", "openai-o200k", true)]
    [InlineData("openai/gpt-6-luna", "openai-o200k", true)]
    [InlineData("o3-mini", "openai-o200k", true)]
    [InlineData("gpt-4-turbo", "openai-cl100k", true)]
    [InlineData("gpt-3.5-turbo", "openai-cl100k", true)]
    [InlineData("llama3.1:8b", "llama3", true)]
    [InlineData("meta-llama-3-8b-instruct", "llama3", true)]
    [InlineData("qwen2.5-coder:1.5b", "qwen2.5-coder", false)]
    [InlineData("ollama/qwen2.5-coder:7b", "qwen2.5-coder", false)]
    [InlineData("Qwen/Qwen2.5-Coder-7B", "qwen2.5-coder", false)]
    [InlineData("gemma4:e2b", "gemma", false)]
    [InlineData("codellama:7b-code", "codellama", false)]
    [InlineData("starcoder2:3b", "starcoder2", false)]
    [InlineData("deepseek-coder:6.7b-base", "deepseek-coder", false)]
    [InlineData("codestral-latest", "codestral", false)]
    [InlineData("some-new-model", "default", false)]
    public void ForModel_PicksTheFamily(string modelId, string family, bool exact)
    {
        var counter = TokenCounter.Default.ForModel(modelId);

        Assert.Equal(family, counter.Family);
        Assert.Equal(exact, counter.IsExact);
    }

    [Fact]
    public void Exact_Families_CountWithTiktoken()
    {
        var o200k = TokenCounter.Default.ForModel("gpt-4o");
        var cl100k = TokenCounter.Default.ForModel("gpt-4");

        // Known counts: "hello world" is two tokens in both encodings.
        Assert.Equal(2, o200k.Count("hello world"));
        Assert.Equal(2, cl100k.Count("hello world"));
        Assert.Equal(0, o200k.Count(string.Empty));
        Assert.Same(TokenCounter.Default.ForModel("gpt-4o"), o200k);
    }

    [Fact]
    public void Llama3_IsCountedWithCl100k()
    {
        var text = ReadCorpus("InventoryService.cs");

        Assert.Equal(TokenCounter.Default.ForModel("gpt-4").Count(text), TokenCounter.Default.ForModel("llama3.2:3b").Count(text));
    }

    [Fact]
    public void Estimate_CountsDigitsAndOtherCharacters_WithTheMargin()
    {
        var counter = new EstimatingCounter("test", CharsPerToken: 4, DigitTokens: 1, SafetyMargin: 0.10);

        Assert.Equal(0, counter.Count(string.Empty));
        Assert.Equal(3, counter.Count("abcdefgh"));          // 8 / 4 = 2, +10% = 2.2, rounded up
        Assert.Equal(6, counter.Count("abcd1234"));          // 4 / 4 + 4 digits = 5, +10% = 5.5, rounded up
    }

    [Fact]
    public void Calibration_EstimateStaysWithinBoundsOfTheMeasuredCounts()
    {
        using var measured = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Tokens", "measured-counts.json")));
        foreach (var file in measured.RootElement.GetProperty("files").EnumerateObject())
        {
            var text = ReadCorpus(file.Name);
            foreach (var model in file.Value.EnumerateObject())
            {
                var actual = model.Value.GetInt32();
                var estimate = TokenCounter.Default.ForModel(model.Name).Count(text);
                var ratio = estimate / (double)actual;
                output.WriteLine($"{file.Name} {model.Name}: {text.Length} chars, actual {actual}, estimate {estimate} ({ratio:P1}), cl100k {TokenCounter.Default.ForModel("gpt-4").Count(text)}, o200k {TokenCounter.Default.ForModel("gpt-4o").Count(text)}");

                // The estimate may undercount a digit-heavy file by a few percent; it never undercounts by more than 6% and
                // never overcounts by more than 35% on the corpus.
                Assert.InRange(ratio, 0.94, 1.35);
            }
        }
    }

    [Fact]
    public void Calibration_RecordedCharsPerToken_MatchTheCorpusAggregate()
    {
        using var measured = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Tokens", "measured-counts.json")));
        foreach (var model in new[] { "qwen2.5-coder:1.5b", "gemma4:e2b" })
        {
            long nonDigits = 0, digits = 0, tokens = 0;
            foreach (var file in measured.RootElement.GetProperty("files").EnumerateObject())
            {
                var text = ReadCorpus(file.Name);
                var d = text.Count(char.IsAsciiDigit);
                digits += d;
                nonDigits += text.Length - d;
                tokens += file.Value.GetProperty(model).GetInt32();
            }

            var measuredRatio = nonDigits / (double)(tokens - digits);
            var counter = Assert.IsType<EstimatingCounter>(TokenCounter.Default.ForModel(model));
            output.WriteLine($"{model}: {nonDigits} non-digit chars, {digits} digits, {tokens} tokens: {measuredRatio:F3} chars per token (recorded {counter.CharsPerToken})");
            Assert.Equal(measuredRatio, counter.CharsPerToken, 0.01);
            Assert.Equal(0.10, counter.SafetyMargin);
        }
    }

    [Fact]
    public void Parse_RejectsADocumentWithoutACatchAll()
    {
        Assert.Throws<FormatException>(() => TokenCounter.Parse("""{ "safetyMargin": 0.1, "families": [ { "family": "x", "match": "x*", "tokenizer": "estimate", "charsPerToken": 4 } ] }"""));
        Assert.Throws<FormatException>(() => TokenCounter.Parse("""{ "safetyMargin": 0.1, "families": [ { "family": "x", "match": "*", "tokenizer": "sentencepiece" } ] }"""));
    }
}
