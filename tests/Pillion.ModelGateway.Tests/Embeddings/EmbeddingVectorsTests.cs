namespace Pillion.ModelGateway.Tests.Embeddings;

/// <summary>Unit normalisation and int8 quantisation (design section 7, Embed).</summary>
public class EmbeddingVectorsTests
{
    private static readonly float[] ThreeFourZero = [0.6f, 0.8f, 0f];
    private static readonly float[] ThreeFourZeroZero = [0.6f, 0.8f, 0f, 0f];
    private static readonly float[] RawThreeFour = [3f, 4f, 0f, 0f];
    private static readonly float[] OneTwoThree = [1f, 2f, 3f];
    private static readonly sbyte[] OneTwoThreeInt8 = [1, 2, 3];
    private static readonly sbyte[] ThreeFourQuantized = [95, 127, 0, 0];

    private static float[] Random(int seed, int dimensions)
    {
        var random = new Random(seed);
        var vector = new float[dimensions];
        for (var i = 0; i < dimensions; i++)
        {
            // Roughly Gaussian like a real embedding (sum of uniforms), centred on zero.
            vector[i] = (float)((random.NextDouble() + random.NextDouble() + random.NextDouble() - 1.5) * 0.2);
        }

        return vector;
    }

    private static double LengthOf(ReadOnlySpan<float> vector)
    {
        double sum = 0;
        foreach (var x in vector)
        {
            sum += (double)x * x;
        }

        return Math.Sqrt(sum);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(384)]
    [InlineData(768)]
    [InlineData(1024)]
    [InlineData(3072)]
    public void Normalize_GivesUnitLength_AndReturnsTheOriginalLength(int dimensions)
    {
        var vector = Random(dimensions, dimensions);
        var before = LengthOf(vector);

        var returned = EmbeddingVectors.Normalize(vector);

        Assert.Equal(before, returned, 4);
        Assert.Equal(1.0, LengthOf(vector), 5);
    }

    [Fact]
    public void Normalize_KeepsTheDirection()
    {
        float[] vector = [3f, 4f, 0f];

        var length = EmbeddingVectors.Normalize(vector);

        Assert.Equal(5f, length);
        Assert.Equal(ThreeFourZero, vector);
    }

    [Fact]
    public void Normalize_LeavesAZeroVectorAlone()
    {
        float[] vector = [0f, 0f, 0f];

        Assert.Equal(0f, EmbeddingVectors.Normalize(vector));
        Assert.All(vector, x => Assert.Equal(0f, x));
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void Normalize_RejectsAnyValueThatIsNotANumber(float bad)
    {
        var vector = new[] { 1f, bad, 2f };

        Assert.Throws<ArgumentException>(() => EmbeddingVectors.Normalize(vector));
    }

    [Theory]
    [InlineData(384)]
    [InlineData(1024)]
    public void Quantize_RoundTripStaysWithinHalfAScaleStep_AndTheScaleIsRecorded(int dimensions)
    {
        var vector = Random(7, dimensions);
        EmbeddingVectors.Normalize(vector);
        var max = vector.Max(Math.Abs);

        var processed = EmbeddingVectors.Process(vector, keepFloat32: true);

        // The scale is the largest magnitude over 127, so the largest component lands on +-127.
        Assert.Equal(max / 127f, processed.Scale, 7);
        Assert.Contains(processed.Quantized, q => Math.Abs((int)q) == 127);
        Assert.All(processed.Quantized, q => Assert.InRange((int)q, -127, 127));

        var restored = new float[dimensions];
        EmbeddingVectors.Dequantize(processed.Quantized, processed.Scale, restored);
        for (var i = 0; i < dimensions; i++)
        {
            Assert.True(Math.Abs(restored[i] - vector[i]) <= (processed.Scale / 2) + 1e-7f, $"component {i}: {vector[i]} became {restored[i]}");
        }

        // Quantisation noise is small against a unit vector: the cosine to the original stays above 0.9999.
        Assert.True(EmbeddingVectors.Cosine(restored, vector) > 0.9999f);
    }

    [Fact]
    public void Quantize_OfAZeroVector_IsAllZeroWithAZeroScale()
    {
        var quantized = new sbyte[] { 5, 5, 5 };

        EmbeddingVectors.Quantize(new float[3], quantized, out var scale);

        Assert.Equal(0f, scale);
        Assert.All(quantized, q => Assert.Equal(0, q));
    }

    [Fact]
    public void Quantize_RejectsADestinationThatIsTooShort()
    {
        Assert.Throws<ArgumentException>(() => EmbeddingVectors.Quantize(new float[4], new sbyte[3], out _));
    }

    [Fact]
    public void Process_NormalisesFirst_DoesNotTouchTheInput_AndKeepsFloat32OnlyWhenAsked()
    {
        float[] raw = [3f, 4f, 0f, 0f];

        var withFloat = EmbeddingVectors.Process(raw, keepFloat32: true);
        var withoutFloat = EmbeddingVectors.Process(raw, keepFloat32: false);

        Assert.Equal(RawThreeFour, raw);
        Assert.Equal(ThreeFourZeroZero, withFloat.Normalized);
        Assert.Null(withoutFloat.Normalized);
        Assert.Equal(4, withFloat.Dimensions);
        Assert.Equal(withFloat.Quantized, withoutFloat.Quantized);

        // The 0.8 component is the largest: scale 0.8 / 127, so it is 127 and the 0.6 component rounds to 95.
        Assert.Equal(ThreeFourQuantized, withFloat.Quantized);
        Assert.Equal(0.8f / 127f, withFloat.Scale, 7);
    }

    [Fact]
    public void Process_RejectsAnEmptyVector_AndANonFiniteOne()
    {
        Assert.Throws<ArgumentException>(() => EmbeddingVectors.Process([], keepFloat32: false));
        Assert.Throws<ArgumentException>(() => EmbeddingVectors.Process([1f, float.NaN], keepFloat32: false));
    }

    [Fact]
    public void Cosine_OfInt8Vectors_TracksTheFloatCosine()
    {
        var a = Random(1, 1024);
        var b = a.Select((x, i) => x + (Random(2, 1024)[i] * 0.5f)).ToArray(); // Similar, not equal.
        var unrelated = Random(3, 1024);

        var pa = EmbeddingVectors.Process(a, keepFloat32: true);
        var pb = EmbeddingVectors.Process(b, keepFloat32: true);
        var pu = EmbeddingVectors.Process(unrelated, keepFloat32: true);

        var floatSimilar = EmbeddingVectors.Cosine(pa.Normalized!, pb.Normalized!);
        var int8Similar = EmbeddingVectors.Cosine(pa.Quantized, pb.Quantized);
        var floatUnrelated = EmbeddingVectors.Cosine(pa.Normalized!, pu.Normalized!);
        var int8Unrelated = EmbeddingVectors.Cosine(pa.Quantized, pu.Quantized);

        Assert.Equal(floatSimilar, int8Similar, 2);
        Assert.Equal(floatUnrelated, int8Unrelated, 2);
        Assert.True(int8Similar > int8Unrelated + 0.3f);
    }

    [Fact]
    public void Cosine_IsZeroForAZeroVector_AndRejectsDifferentLengths()
    {
        Assert.Equal(0f, EmbeddingVectors.Cosine(new float[3], OneTwoThree));
        Assert.Equal(0f, EmbeddingVectors.Cosine(new sbyte[3], OneTwoThreeInt8));
        Assert.Throws<ArgumentException>(() => EmbeddingVectors.Cosine(new float[3], new float[4]));
        Assert.Throws<ArgumentException>(() => EmbeddingVectors.Cosine(new sbyte[3], new sbyte[4]));
    }

    [Fact]
    public void Quantization_PreservesTheRanking_OfNeighbours()
    {
        // 200 candidates around a query: the nearest by int8 dot product is the nearest by float cosine for nearly all queries.
        var dimensions = 384;
        var candidates = Enumerable.Range(0, 200).Select(i => EmbeddingVectors.Process(Random(100 + i, dimensions), keepFloat32: true)).ToArray();
        var agree = 0;
        for (var q = 0; q < 50; q++)
        {
            var query = EmbeddingVectors.Process(candidates[q].Normalized!.Select((x, i) => x + (Random(900 + q, dimensions)[i] * 0.3f)).ToArray(), keepFloat32: true);
            var byFloat = Enumerable.Range(0, candidates.Length).MaxBy(i => EmbeddingVectors.Cosine(query.Normalized!, candidates[i].Normalized!));
            var byInt8 = Enumerable.Range(0, candidates.Length).MaxBy(i => EmbeddingVectors.Cosine(query.Quantized, candidates[i].Quantized));
            if (byFloat == byInt8)
            {
                agree++;
            }
        }

        Assert.True(agree >= 49, $"{agree} of 50 queries agree");
    }
}
