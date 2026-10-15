using Microsoft.Data.Sqlite;

namespace Pillion.LocalStore.Index;

/// <summary>The writes every backend shares (they all store vectors in <c>chunk_embeddings</c>); backends differ only in how they search.</summary>
public abstract class VectorIndexBase : IVectorIndex
{
    private protected VectorIndexBase(VectorStorage storage)
    {
        Storage = storage;
    }

    /// <inheritdoc/>
    public abstract string Backend { get; }

    private protected VectorStorage Storage { get; }

    /// <inheritdoc/>
    /// <remarks>
    /// The in-memory copy of the default backend takes the vector incrementally after the commit (work item 2.10) when it
    /// was current, instead of being reloaded whole by the next search.
    /// </remarks>
    public async Task UpsertAsync(long chunkId, ReadOnlyMemory<sbyte> vector, VectorMetadata metadata, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        var delta = await Storage.Database.WriteAsync(
            (connection, transaction, _) =>
            {
                var before = VectorCache.ReadGeneration(connection, transaction);
                using var session = VectorStorage.BeginWrite(connection, transaction);
                session.Upsert(chunkId, vector.Span, metadata.Language, metadata.Partition);
                var after = VectorCache.ReadGeneration(connection, transaction);
                return Task.FromResult(after - before == 1
                    ? new VectorDelta { Before = before, After = after, Added = [new AddedVector(chunkId, vector.ToArray(), metadata.Language, metadata.Partition)] }
                    : null);
            },
            cancellationToken).ConfigureAwait(false);
        if (delta is not null)
        {
            Storage.ApplyDelta(delta);
        }
    }

    /// <inheritdoc/>
    public Task<int> DeleteByFileAsync(long fileId, CancellationToken cancellationToken = default) =>
        Storage.Database.WriteAsync(
            (connection, transaction, _) =>
            {
                using var session = VectorStorage.BeginWrite(connection, transaction);
                return Task.FromResult(session.DeleteByFile(fileId));
            },
            cancellationToken);

    /// <inheritdoc/>
    public Task<IReadOnlyList<VectorHit>> SearchAsync(ReadOnlyMemory<sbyte> query, int k, ChunkFilter? filter = null, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(k, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(k, IVectorIndex.MaxK);
        if (query.IsEmpty)
        {
            throw new ArgumentException("The query vector is empty.", nameof(query));
        }

        return Storage.Database.ReadAsync(
            (connection, token) =>
            {
                var dims = VectorStorage.ReadDimensions(connection, null);
                if (dims is null)
                {
                    return Task.FromResult<IReadOnlyList<VectorHit>>([]);
                }

                if (dims.Value != query.Length)
                {
                    throw new ArgumentException($"The index stores {dims}-dimension vectors; the query has {query.Length}.", nameof(query));
                }

                return Task.FromResult(Search(connection, query.Span, k, filter ?? ChunkFilter.None, token));
            },
            cancellationToken);
    }

    /// <summary>Runs the search on a reader connection.</summary>
    private protected abstract IReadOnlyList<VectorHit> Search(SqliteConnection connection, ReadOnlySpan<sbyte> query, int k, ChunkFilter filter, CancellationToken cancellationToken);
}

/// <summary>
/// The default backend (<see cref="VectorBackendPreference.InMemory"/>): brute force over an in-memory copy of the int8
/// vectors while they fit <see cref="IndexStorageOptions.VectorCacheMaxBytes"/> (a few milliseconds for 100,000 chunks of
/// 384 dimensions), otherwise the scan of <c>chunk_embeddings</c> that <see cref="TableScanVectorIndex"/> does. The copy
/// holds vectors only, never chunk text, is never written to disk, and is reloaded when the <c>vec_generation</c> counter
/// moves. Both paths rank alike.
/// </summary>
public sealed class AutoVectorIndex : VectorIndexBase
{
    internal AutoVectorIndex(VectorStorage storage)
        : base(storage)
    {
    }

    /// <summary>The backend name.</summary>
    public const string Name = "in-memory";

    /// <inheritdoc/>
    public override string Backend => Name;

    /// <inheritdoc/>
    private protected override IReadOnlyList<VectorHit> Search(SqliteConnection connection, ReadOnlySpan<sbyte> query, int k, ChunkFilter filter, CancellationToken cancellationToken)
    {
        var cache = Storage.GetCache(connection, query.Length);
        return cache is not null
            ? cache.Search(query, k, filter, cancellationToken)
            : TableScanVectorIndex.Scan(connection, query, k, filter, cancellationToken);
    }
}

/// <summary>
/// The table-scan backend (<see cref="VectorBackendPreference.TableScan"/>): reads every int8 vector of
/// <c>chunk_embeddings</c> that passes the filter and computes the cosine distance in managed code (AVX2 or 128-bit
/// SIMD), keeping the best <c>k</c> in a heap. It holds no copy of the vectors, at the cost of reading and decrypting the
/// table on every search.
/// </summary>
public sealed class TableScanVectorIndex : VectorIndexBase
{
    internal TableScanVectorIndex(VectorStorage storage)
        : base(storage)
    {
    }

    /// <summary>The backend name.</summary>
    public const string Name = "table-scan";

    /// <inheritdoc/>
    public override string Backend => Name;

    /// <summary>Scans <c>chunk_embeddings</c> on a connection.</summary>
    internal static IReadOnlyList<VectorHit> Scan(SqliteConnection connection, ReadOnlySpan<sbyte> query, int k, ChunkFilter filter, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        var clauses = new List<string>();
        Sql.AddInFilter(command, clauses, "partition", filter.Partitions, "p");
        Sql.AddInFilter(command, clauses, "language", filter.Languages, "l");
        command.CommandText = clauses.Count == 0
            ? "SELECT chunk_id, embedding FROM chunk_embeddings"
            : $"SELECT chunk_id, embedding FROM chunk_embeddings WHERE {string.Join(" AND ", clauses)}";

        var queryNorm = Int8Vectors.SquaredNorm(query);
        var buffer = new byte[query.Length];
        var heap = new TopK(k);
        var rows = 0;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if ((++rows & 4095) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var length = reader.GetBytes(1, 0, buffer, 0, buffer.Length);
            if (length != query.Length)
            {
                continue;
            }

            Int8Vectors.DotAndNorm(query, System.Runtime.InteropServices.MemoryMarshal.Cast<byte, sbyte>(buffer), out var dot, out var norm);
            heap.Offer(reader.GetInt64(0), Int8Vectors.CosineDistance(dot, queryNorm, norm));
        }

        return heap.ToHits();
    }

    /// <inheritdoc/>
    private protected override IReadOnlyList<VectorHit> Search(SqliteConnection connection, ReadOnlySpan<sbyte> query, int k, ChunkFilter filter, CancellationToken cancellationToken) =>
        Scan(connection, query, k, filter, cancellationToken);
}

/// <summary>The best <c>k</c> hits seen so far: a heap with the worst (largest distance, then largest id) on top.</summary>
internal sealed class TopK
{
    private readonly int _k;
    private readonly PriorityQueue<long, (float Distance, long Id)> _heap;

    public TopK(int k)
    {
        _k = k;
        _heap = new PriorityQueue<long, (float Distance, long Id)>(k + 1, WorstFirst.Instance);
    }

    public void Offer(long id, float distance)
    {
        if (_heap.Count < _k)
        {
            _heap.Enqueue(id, (distance, id));
        }
        else if (_heap.TryPeek(out _, out var worst) && WorstFirst.Instance.Compare((distance, id), worst) > 0)
        {
            _heap.DequeueEnqueue(id, (distance, id));
        }
    }

    public List<VectorHit> ToHits()
    {
        var hits = new List<VectorHit>(_heap.Count);
        while (_heap.TryDequeue(out var id, out var priority))
        {
            hits.Add(new VectorHit(id, priority.Distance));
        }

        hits.Reverse();
        return hits;
    }

    /// <summary>Orders the heap so the worst hit (largest distance, then largest id) is at the top.</summary>
    private sealed class WorstFirst : IComparer<(float Distance, long Id)>
    {
        public static readonly WorstFirst Instance = new();

        public int Compare((float Distance, long Id) x, (float Distance, long Id) y)
        {
            var byDistance = y.Distance.CompareTo(x.Distance);
            return byDistance != 0 ? byDistance : y.Id.CompareTo(x.Id);
        }
    }
}
