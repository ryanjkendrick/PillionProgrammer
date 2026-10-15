using Pillion.ModelGateway.Providers.Bundled;

namespace Pillion.ModelGateway.Tests.Embeddings.Bundled;

/// <summary>
/// The bundled model's WordPiece tokenizer against the Python tokenizer: the ids for each reference string were produced by
/// the Hugging Face tokenizers library (0.22.1) from the export's tokenizer.json. Uses the vocab.txt the build placed in the
/// output; no network.
/// </summary>
public class BundledTokenizerTests
{
    private static readonly int[][] BgeReferenceIds =
    [
        [101, 7592, 2088, 1012, 102],
        [101, 2270, 2004, 6038, 2278, 4708, 1026, 7861, 8270, 4667, 6072, 11314, 1028, 7861, 8270, 3022, 6038, 2278, 1006, 7861, 8270, 4667, 2890, 15500, 5227, 1010, 16990, 18715, 2368, 16990, 18715, 2368, 1007, 102],
        [101, 13366, 11968, 3366, 1035, 9530, 8873, 2290, 1006, 4130, 1024, 2358, 2099, 1007, 1011, 1028, 4487, 6593, 1024, 2709, 1046, 3385, 1012, 7170, 1006, 2330, 1006, 4130, 1007, 1007, 102],
        [101, 7668, 15743, 13746, 19169, 102],
        [101, 100, 100, 1745, 100, 2965, 7592, 2088, 102],
        [101, 2065, 1006, 1060, 999, 1027, 19701, 1004, 1004, 1061, 1028, 1027, 2184, 1007, 1063, 2709, 1060, 1029, 1012, 3643, 1029, 1029, 1014, 1025, 1065, 102],
        [101, 2131, 11039, 25856, 6072, 26029, 3366, 16044, 7488, 1035, 2553, 1035, 2171, 17710, 3676, 2497, 1011, 2553, 1011, 2171, 102],
    ];

    private static readonly Lazy<EmbeddingTokenizer> Bge = new(() =>
    {
        using var runtime = TestModels.Runtime(TestModels.RequireBuildOutput());
        return (EmbeddingTokenizer)runtime.GetTokenCounter(TestModels.Bge)!;
    });

    public static TheoryData<int> ReferenceCases() => [.. Enumerable.Range(0, TestModels.ReferenceTexts.Length)];

    [Theory]
    [MemberData(nameof(ReferenceCases))]
    public void Bge_EncodesLikeThePythonTokenizer(int index)
    {
        var text = TestModels.ReferenceTexts[index];

        Assert.Equal(BgeReferenceIds[index], Bge.Value.Encode(text));
        Assert.Equal(BgeReferenceIds[index].Length, Bge.Value.CountTokens(text));
    }

    [Fact]
    public void Bge_SplitsAsciiSymbolsAndUnderscores_WhichMicrosoftMLTokenizers200DropsOrJoins()
    {
        // '<', '=' and '>' are tokens 1026, 1027 and 1028, '_' is 1035; the stock BertTokenizer drops the first three and glues '_' into the word.
        var ids = Bge.Value.Encode("a<=b_c");

        Assert.Equal([101, 1037, 1026, 1027, 1038, 1035, 1039, 102], ids);
    }

    [Fact]
    public void Bge_ReportsItsFamilyLimitAndSpecialTokens()
    {
        Assert.Equal("bert-wordpiece", Bge.Value.TokenizerFamily);
        Assert.Equal(512, Bge.Value.MaxTokens);
        Assert.Equal(101, Bge.Value.BeginId);
        Assert.Equal(102, Bge.Value.EndId);
        Assert.Equal(0, Bge.Value.PadId);
        Assert.Equal(2, Bge.Value.CountTokens(string.Empty));
    }

    [Fact]
    public void Bge_TruncatesTheContentToTheModelsLimit_KeepingTheEndToken()
    {
        var text = string.Join(' ', Enumerable.Repeat("public void ProcessOrder(Order order) { _repository.Save(order); }", 60));

        var ids = Bge.Value.Encode(text);

        Assert.True(Bge.Value.CountTokens(text) > 1000);
        Assert.Equal(512, ids.Length);
        Assert.Equal(101, ids[0]);
        Assert.Equal(102, ids[^1]);
        Assert.Equal(Bge.Value.Encode(text[..200])[1..10], ids[1..10]);
    }

    [Fact]
    public void TheRuntimesTokenCounter_IsTheTokenizerTheModelUses()
    {
        using var runtime = TestModels.Runtime(TestModels.RequireBuildOutput());

        var counter = runtime.GetTokenCounter(TestModels.Bge);

        Assert.NotNull(counter);
        Assert.Equal(34, counter.CountTokens(TestModels.ReferenceTexts[1]));
        Assert.Null(runtime.GetTokenCounter(TestModels.E5 + "-not-in-the-manifest"));
    }
}
