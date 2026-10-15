namespace Pillion.ModelGateway;

/// <summary>
/// Vector post-processing for the index (design section 7, Embed): unit normalisation, so that a dot product is a cosine
/// similarity, then symmetric int8 quantisation with one scale per vector. The float32 vector is kept only for callers that
/// measure the recall lost to quantisation.
/// </summary>
public static class EmbeddingVectors
{
    /// <summary>The largest magnitude of a quantised component. -128 is never used, so the range is symmetric.</summary>
    public const int Int8Max = 127;

    /// <summary>Scales a vector to unit length in place.</summary>
    /// <param name="vector">The vector. An all-zero vector is left as it is.</param>
    /// <returns>The length the vector had before.</returns>
    /// <exception cref="ArgumentException">A component is NaN or infinite.</exception>
    public static float Normalize(Span<float> vector)
    {
        double sum = 0;
        foreach (var x in vector)
        {
            if (!float.IsFinite(x))
            {
                throw new ArgumentException("The vector has a component that is not a finite number.", nameof(vector));
            }

            sum += (double)x * x;
        }

        var length = Math.Sqrt(sum);
        if (length == 0)
        {
            return 0;
        }

        for (var i = 0; i < vector.Length; i++)
        {
            vector[i] = (float)(vector[i] / length);
        }

        return (float)length;
    }

    /// <summary>Quantises a vector to int8: <c>q = round(x / scale)</c> with <c>scale = max|x| / 127</c>.</summary>
    /// <param name="vector">The vector (normally already unit length).</param>
    /// <param name="destination">Receives the quantised components; at least as long as <paramref name="vector"/>.</param>
    /// <param name="scale">Receives the scale (0 for an all-zero vector, whose components are all 0).</param>
    /// <exception cref="ArgumentException"><paramref name="destination"/> is too short.</exception>
    public static void Quantize(ReadOnlySpan<float> vector, Span<sbyte> destination, out float scale)
    {
        if (destination.Length < vector.Length)
        {
            throw new ArgumentException("The destination is shorter than the vector.", nameof(destination));
        }

        var max = 0f;
        foreach (var x in vector)
        {
            max = Math.Max(max, Math.Abs(x));
        }

        scale = max / Int8Max;
        if (scale == 0)
        {
            destination[..vector.Length].Clear();
            return;
        }

        for (var i = 0; i < vector.Length; i++)
        {
            destination[i] = (sbyte)Math.Clamp((int)MathF.Round(vector[i] / scale), -Int8Max, Int8Max);
        }
    }

    /// <summary>Rebuilds approximate float32 components from a quantised vector: <c>x = q * scale</c>.</summary>
    /// <param name="quantized">The quantised components.</param>
    /// <param name="scale">The vector's scale.</param>
    /// <param name="destination">Receives the components; at least as long as <paramref name="quantized"/>.</param>
    public static void Dequantize(ReadOnlySpan<sbyte> quantized, float scale, Span<float> destination)
    {
        if (destination.Length < quantized.Length)
        {
            throw new ArgumentException("The destination is shorter than the vector.", nameof(destination));
        }

        for (var i = 0; i < quantized.Length; i++)
        {
            destination[i] = quantized[i] * scale;
        }
    }

    /// <summary>Normalises a provider's vector and quantises it. The input is not modified.</summary>
    /// <param name="raw">The vector as the provider returned it.</param>
    /// <param name="keepFloat32">Whether the result carries the unit-normalised float32 vector.</param>
    /// <returns>The processed vector.</returns>
    /// <exception cref="ArgumentException">The vector is empty or has a component that is not a finite number.</exception>
    public static EmbeddedVector Process(ReadOnlySpan<float> raw, bool keepFloat32)
    {
        if (raw.IsEmpty)
        {
            throw new ArgumentException("The vector is empty.", nameof(raw));
        }

        var unit = raw.ToArray();
        Normalize(unit);
        var quantized = new sbyte[unit.Length];
        Quantize(unit, quantized, out var scale);
        return new EmbeddedVector(quantized, scale, keepFloat32 ? unit : null);
    }

    /// <summary>Gets the cosine similarity of two float32 vectors (the dot product when both are unit length).</summary>
    /// <param name="a">The first vector.</param>
    /// <param name="b">The second vector, of the same length.</param>
    /// <returns>A value in -1 to 1; 0 when either vector is all zero.</returns>
    public static float Cosine(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        if (a.Length != b.Length)
        {
            throw new ArgumentException("The vectors differ in length.", nameof(b));
        }

        double dot = 0, normA = 0, normB = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += (double)a[i] * b[i];
            normA += (double)a[i] * a[i];
            normB += (double)b[i] * b[i];
        }

        return normA == 0 || normB == 0 ? 0 : (float)(dot / Math.Sqrt(normA * normB));
    }

    /// <summary>Gets the cosine similarity of two quantised vectors from their int8 components (the scales cancel).</summary>
    /// <param name="a">The first vector.</param>
    /// <param name="b">The second vector, of the same length.</param>
    /// <returns>A value in -1 to 1; 0 when either vector is all zero.</returns>
    public static float Cosine(ReadOnlySpan<sbyte> a, ReadOnlySpan<sbyte> b)
    {
        if (a.Length != b.Length)
        {
            throw new ArgumentException("The vectors differ in length.", nameof(b));
        }

        long dot = 0, normA = 0, normB = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }

        return normA == 0 || normB == 0 ? 0 : (float)(dot / Math.Sqrt((double)normA * normB));
    }
}
