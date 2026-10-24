using Microsoft.Data.Sqlite;

namespace Pillion.LocalStore.Index;

/// <summary>
/// An in-memory copy of the int8 vectors of <c>chunk_embeddings</c> for the default backend: one contiguous array,
/// the squared norms precomputed, and the language and partition of each row as small codes for filtering. It is tied to
/// the <c>vec_generation</c> counter it was read at and replaced as a whole when the counter moves. It holds vectors
/// only, never chunk text, and is never written to disk.
/// </summary>
internal sealed class VectorCache
{
    private readonly long[] _ids;
    private readonly sbyte[] _vectors;
    private readonly int[] _norms;
    private readonly ushort[] _partitions;
    private readonly ushort[] _languages;
    private readonly Dictionary<string, ushort> _partitionCodes;
    private readonly Dictionary<string, ushort> _languageCodes;

    private VectorCache(long generation, int dimensions, long[] ids, sbyte[] vectors, int[] norms, ushort[] partitions, ushort[] languages, Dictionary<string, ushort> partitionCodes, Dictionary<string, ushort> languageCodes)
    {
        Generation = generation;
        Dimensions = dimensions;
        _ids = ids;
        _vectors = vectors;
        _norms = norms;
        _partitions = partitions;
        _languages = languages;
        _partitionCodes = partitionCodes;
        _languageCodes = languageCodes;
    }

    /// <summary>Gets the <c>vec_generation</c> value the copy was read at.</summary>
    public long Generation { get; }

    /// <summary>Gets the vector dimension.</summary>
    public int Dimensions { get; }

    /// <summary>Gets the number of vectors.</summary>
    public int Count => _ids.Length;

    /// <summary>Gets the approximate memory the copy holds, in bytes.</summary>
    public long Bytes => _vectors.LongLength + (_ids.LongLength * (sizeof(long) + sizeof(int) + (2 * sizeof(ushort))));

    /// <summary>Reads the current generation.</summary>
    public static long ReadGeneration(SqliteConnection connection, SqliteTransaction? transaction) =>
        Sql.ScalarLong(connection, transaction, "SELECT value FROM vec_generation WHERE id = 1");

    /// <summary>Reads every vector of the given dimension, with the generation, in one read transaction.</summary>
    public static VectorCache Load(SqliteConnection connection, int dimensions, int expectedCount)
    {
        using var transaction = connection.BeginTransaction(deferred: true);
        var generation = ReadGeneration(connection, transaction);
        var ids = new List<long>(expectedCount);
        var vectors = new sbyte[(long)Math.Max(expectedCount, 1) * dimensions];
        var norms = new List<int>(expectedCount);
        var partitions = new List<ushort>(expectedCount);
        var languages = new List<ushort>(expectedCount);
        var partitionCodes = new Dictionary<string, ushort>(StringComparer.Ordinal);
        var languageCodes = new Dictionary<string, ushort>(StringComparer.Ordinal);
        var buffer = new byte[dimensions];

        using (var command = Sql.Command(connection, transaction, "SELECT chunk_id, language, partition, embedding FROM chunk_embeddings"))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                if (reader.GetBytes(3, 0, null, 0, 0) != dimensions)
                {
                    continue;
                }

                var row = ids.Count;
                if ((long)(row + 1) * dimensions > vectors.LongLength)
                {
                    Array.Resize(ref vectors, (int)Math.Min((long)Array.MaxLength, Math.Max(vectors.LongLength * 2, (long)(row + 1) * dimensions)));
                }

                reader.GetBytes(3, 0, buffer, 0, dimensions);
                var vector = vectors.AsSpan(row * dimensions, dimensions);
                System.Runtime.InteropServices.MemoryMarshal.Cast<byte, sbyte>(buffer).CopyTo(vector);
                ids.Add(reader.GetInt64(0));
                norms.Add(Int8Vectors.SquaredNorm(vector));
                languages.Add(Code(languageCodes, reader.GetString(1)));
                partitions.Add(Code(partitionCodes, reader.GetString(2)));
            }
        }

        transaction.Commit();
        if (vectors.LongLength != (long)ids.Count * dimensions)
        {
            Array.Resize(ref vectors, ids.Count * dimensions);
        }

        return new VectorCache(generation, dimensions, [.. ids], vectors, [.. norms], [.. partitions], [.. languages], partitionCodes, languageCodes);
    }

    /// <summary>
    /// Builds the copy that a committed change leads to (work item 2.10): the rows of <see cref="VectorDelta.Removed"/> and
    /// of every re-stored chunk dropped, the new vectors appended, tied to <see cref="VectorDelta.After"/>. One pass over the
    /// arrays (a few milliseconds for 100,000 chunks), against reading and decrypting the whole table again.
    /// </summary>
    /// <param name="delta">What the transaction changed; its <see cref="VectorDelta.Before"/> must be this copy's generation.</param>
    /// <param name="maxBytes">The cap; a copy that would outgrow it is not built.</param>
    /// <returns>The new copy, or <see langword="null"/> when it cannot be built (another generation, another dimension, over the cap).</returns>
    public VectorCache? Apply(VectorDelta delta, long maxBytes)
    {
        if (delta.Before != Generation || delta.Added.Any(a => a.Vector.Length != Dimensions))
        {
            return null;
        }

        var dropped = new HashSet<long>(delta.Removed);
        foreach (var added in delta.Added)
        {
            dropped.Add(added.ChunkId);
        }

        var keep = 0;
        for (var row = 0; row < _ids.Length; row++)
        {
            if (!dropped.Contains(_ids[row]))
            {
                keep++;
            }
        }

        var count = keep + delta.Added.Count;
        if ((long)count * Dimensions > maxBytes)
        {
            return null;
        }

        var dims = Dimensions;
        var ids = new long[count];
        var vectors = new sbyte[(long)count * dims];
        var norms = new int[count];
        var partitions = new ushort[count];
        var languages = new ushort[count];
        var partitionCodes = new Dictionary<string, ushort>(_partitionCodes, StringComparer.Ordinal);
        var languageCodes = new Dictionary<string, ushort>(_languageCodes, StringComparer.Ordinal);
        var next = 0;
        for (var row = 0; row < _ids.Length; row++)
        {
            if (dropped.Contains(_ids[row]))
            {
                continue;
            }

            ids[next] = _ids[row];
            Array.Copy(_vectors, (long)row * dims, vectors, (long)next * dims, dims);
            norms[next] = _norms[row];
            partitions[next] = _partitions[row];
            languages[next] = _languages[row];
            next++;
        }

        foreach (var added in delta.Added)
        {
            ids[next] = added.ChunkId;
            var target = vectors.AsSpan(next * dims, dims);
            added.Vector.Span.CopyTo(target);
            norms[next] = Int8Vectors.SquaredNorm(target);
            languages[next] = Code(languageCodes, added.Language ?? string.Empty);
            partitions[next] = Code(partitionCodes, added.Partition ?? string.Empty);
            next++;
        }

        return new VectorCache(delta.After, dims, ids, vectors, norms, partitions, languages, partitionCodes, languageCodes);
    }

    /// <summary>Finds the nearest vectors, as <see cref="TableScanVectorIndex.Scan"/> does over the table.</summary>
    public IReadOnlyList<VectorHit> Search(ReadOnlySpan<sbyte> query, int k, ChunkFilter filter, CancellationToken cancellationToken)
    {
        var allowedPartitions = Allowed(_partitionCodes, filter.Partitions);
        var allowedLanguages = Allowed(_languageCodes, filter.Languages);
        if ((allowedPartitions is not null && Array.IndexOf(allowedPartitions, true) < 0) || (allowedLanguages is not null && Array.IndexOf(allowedLanguages, true) < 0))
        {
            return [];
        }

        var queryNorm = Int8Vectors.SquaredNorm(query);
        var heap = new TopK(k);
        var dims = Dimensions;
        for (var row = 0; row < _ids.Length; row++)
        {
            if ((row & 16383) == 16383)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            if ((allowedPartitions is not null && !allowedPartitions[_partitions[row]]) || (allowedLanguages is not null && !allowedLanguages[_languages[row]]))
            {
                continue;
            }

            var dot = Int8Vectors.Dot(query, new ReadOnlySpan<sbyte>(_vectors, row * dims, dims));
            heap.Offer(_ids[row], Int8Vectors.CosineDistance(dot, queryNorm, _norms[row]));
        }

        return heap.ToHits();
    }

    private static bool[]? Allowed(Dictionary<string, ushort> codes, IReadOnlyList<string>? values)
    {
        if (values is null || values.Count == 0)
        {
            return null;
        }

        var allowed = new bool[codes.Count];
        foreach (var value in values)
        {
            if (codes.TryGetValue(value ?? string.Empty, out var code))
            {
                allowed[code] = true;
            }
        }

        return allowed;
    }

    private static ushort Code(Dictionary<string, ushort> codes, string value)
    {
        if (!codes.TryGetValue(value, out var code))
        {
            if (codes.Count >= ushort.MaxValue)
            {
                throw new InvalidOperationException("More than 65,535 distinct partitions or languages; the vector cache cannot code them.");
            }

            code = (ushort)codes.Count;
            codes.Add(value, code);
        }

        return code;
    }
}
