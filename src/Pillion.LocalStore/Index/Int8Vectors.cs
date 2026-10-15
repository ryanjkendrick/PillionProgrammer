using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Pillion.LocalStore.Index;

/// <summary>int8 vector arithmetic for the brute-force backend, vectorised with SIMD where the CPU has it.</summary>
internal static class Int8Vectors
{
    /// <summary>Computes the dot product of two vectors and the squared norm of the second, exactly, in one pass.</summary>
    /// <param name="query">The query vector.</param>
    /// <param name="candidate">The candidate vector, the same length.</param>
    /// <param name="dot">The dot product.</param>
    /// <param name="candidateNorm">The sum of the squares of <paramref name="candidate"/>.</param>
    public static void DotAndNorm(ReadOnlySpan<sbyte> query, ReadOnlySpan<sbyte> candidate, out int dot, out int candidateNorm)
    {
        var length = Math.Min(query.Length, candidate.Length);
        var i = 0;
        var d = 0;
        var n = 0;
        ref var q = ref MemoryMarshal.GetReference(query);
        ref var c = ref MemoryMarshal.GetReference(candidate);

        if (Avx2.IsSupported && length >= 32)
        {
            var accumulatedDot = Vector256<int>.Zero;
            var accumulatedNorm = Vector256<int>.Zero;
            for (; i <= length - 32; i += 32)
            {
                // Sign-extend 16 bytes at a time to shorts, then multiply-add adjacent pairs into ints (vpmaddwd).
                var q0 = Avx2.ConvertToVector256Int16(Vector128.LoadUnsafe(ref q, (nuint)i));
                var q1 = Avx2.ConvertToVector256Int16(Vector128.LoadUnsafe(ref q, (nuint)(i + 16)));
                var c0 = Avx2.ConvertToVector256Int16(Vector128.LoadUnsafe(ref c, (nuint)i));
                var c1 = Avx2.ConvertToVector256Int16(Vector128.LoadUnsafe(ref c, (nuint)(i + 16)));
                accumulatedDot = Avx2.Add(accumulatedDot, Avx2.Add(Avx2.MultiplyAddAdjacent(q0, c0), Avx2.MultiplyAddAdjacent(q1, c1)));
                accumulatedNorm = Avx2.Add(accumulatedNorm, Avx2.Add(Avx2.MultiplyAddAdjacent(c0, c0), Avx2.MultiplyAddAdjacent(c1, c1)));
            }

            d = Vector256.Sum(accumulatedDot);
            n = Vector256.Sum(accumulatedNorm);
        }
        else if (Vector128.IsHardwareAccelerated && length >= 16)
        {
            var accumulatedDot = Vector128<int>.Zero;
            var accumulatedNorm = Vector128<int>.Zero;
            for (; i <= length - 16; i += 16)
            {
                var (q0, q1) = Vector128.Widen(Vector128.LoadUnsafe(ref q, (nuint)i));
                var (c0, c1) = Vector128.Widen(Vector128.LoadUnsafe(ref c, (nuint)i));
                accumulatedDot += WidenSum(q0 * c0) + WidenSum(q1 * c1);
                accumulatedNorm += WidenSum(c0 * c0) + WidenSum(c1 * c1);
            }

            d = Vector128.Sum(accumulatedDot);
            n = Vector128.Sum(accumulatedNorm);
        }

        for (; i < length; i++)
        {
            int a = Unsafe.Add(ref q, i);
            int b = Unsafe.Add(ref c, i);
            d += a * b;
            n += b * b;
        }

        dot = d;
        candidateNorm = n;
    }

    /// <summary>The sum of the squares of a vector.</summary>
    /// <param name="vector">The vector.</param>
    /// <returns>The squared norm.</returns>
    public static int SquaredNorm(ReadOnlySpan<sbyte> vector)
    {
        DotAndNorm(vector, vector, out _, out var norm);
        return norm;
    }

    /// <summary>
    /// The cosine distance as sqlite-vec computes it for int8 vectors (<c>distance_cosine_int8</c>: exact integer sums,
    /// <c>1 - dot / (sqrt(|a|²) * sqrt(|b|²))</c> in double, returned as a float), so both backends rank alike. A zero
    /// vector is at distance 1 from everything.
    /// </summary>
    /// <param name="dot">The dot product.</param>
    /// <param name="queryNorm">The squared norm of the query.</param>
    /// <param name="candidateNorm">The squared norm of the candidate.</param>
    /// <returns>The distance.</returns>
    public static float CosineDistance(int dot, int queryNorm, int candidateNorm) =>
        queryNorm == 0 || candidateNorm == 0
            ? 1f
            : (float)(1.0 - (dot / (Math.Sqrt(queryNorm) * Math.Sqrt(candidateNorm))));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<int> WidenSum(Vector128<short> products)
    {
        var (low, high) = Vector128.Widen(products);
        return low + high;
    }
}
