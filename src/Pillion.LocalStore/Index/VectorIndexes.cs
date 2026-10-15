using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Pillion.LocalStore.Index;

/// <summary>The writes both backends share (they store vectors the same way).</summary>
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
    public Task UpsertAsync(long chunkId, ReadOnlyMemory<sbyte> vector, VectorMetadata metadata, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        return Storage.Database.WriteAsync(
            (connection, transaction, _) =>
            {
                using var session = Storage.BeginWrite(connection, transaction);
                session.Upsert(chunkId, vector.Span, metadata.Language, metadata.Partition, isNew: false);
                return Task.CompletedTask;
            },
            cancellationToken);
    }

    /// <inheritdoc/>
    public Task<int> DeleteByFileAsync(long fileId, CancellationToken cancellationToken = default) =>
        Storage.Database.WriteAsync(
            (connection, transaction, _) =>
            {
                using var session = Storage.BeginWrite(connection, transaction);
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
/// The sqlite-vec backend: a KNN query on the <c>vec0</c> table <c>chunk_vectors</c> (<c>embedding MATCH ... AND k = ...</c>)
/// with the filters as <c>IN</c> conditions on its <c>language</c> and <c>partition</c> metadata columns. Needs the
/// extension on every connection; while the table is missing or stale (a host without the extension changed vectors)
/// it answers with the brute-force scan instead.
/// </summary>
public sealed class SqliteVecIndex : VectorIndexBase
{
    internal SqliteVecIndex(VectorStorage storage)
        : base(storage)
    {
        if (!storage.ExtensionLoaded)
        {
            throw new InvalidOperationException("The sqlite-vec backend needs the extension loaded on the database's connections.");
        }
    }

    /// <summary>The backend name.</summary>
    public const string Name = "sqlite-vec";

    /// <inheritdoc/>
    public override string Backend => Name;

    /// <inheritdoc/>
    private protected override IReadOnlyList<VectorHit> Search(SqliteConnection connection, ReadOnlySpan<sbyte> query, int k, ChunkFilter filter, CancellationToken cancellationToken) =>
        Knn(connection, query, k, filter, cancellationToken) ?? BruteForceVectorIndex.CachedOrScan(Storage, connection, query, k, filter, cancellationToken);

    /// <summary>Runs the KNN query on <c>chunk_vectors</c>, or returns <see langword="null"/> while the table is missing or stale.</summary>
    internal static List<VectorHit>? Knn(SqliteConnection connection, ReadOnlySpan<sbyte> query, int k, ChunkFilter filter, CancellationToken cancellationToken)
    {
        if (!Sql.TableExists(connection, null, IndexStorageSchema.VectorTable) || VectorStorage.IsStale(connection, null))
        {
            return null;
        }

        using var command = connection.CreateCommand();
        var clauses = new List<string> { "embedding MATCH vec_int8($query)", "k = $k" };
        command.Parameters.AddWithValue("$query", Sql.ToBlob(query));
        command.Parameters.AddWithValue("$k", k);
        Sql.AddInFilter(command, clauses, "partition", filter.Partitions, "p");
        Sql.AddInFilter(command, clauses, "language", filter.Languages, "l");
        command.CommandText = string.Create(
            CultureInfo.InvariantCulture,
            $"SELECT chunk_id, distance FROM {IndexStorageSchema.VectorTable} WHERE {string.Join(" AND ", clauses)} ORDER BY distance");

        var hits = new List<VectorHit>(k);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            hits.Add(new VectorHit(reader.GetInt64(0), reader.GetDouble(1)));
        }

        return hits;
    }
}

/// <summary>
/// The default backend (<see cref="VectorBackendPreference.Auto"/>): the brute-force search over the in-memory copy of the
/// vectors while they fit <see cref="IndexStorageOptions.VectorCacheMaxBytes"/> (the fastest path: a few milliseconds for
/// 100,000 chunks of 384 dimensions), otherwise the sqlite-vec KNN query when the extension is loaded and its table is up
/// to date, otherwise the brute-force scan of the table. Every path ranks alike.
/// </summary>
public sealed class AutoVectorIndex : VectorIndexBase
{
    internal AutoVectorIndex(VectorStorage storage)
        : base(storage)
    {
    }

    /// <summary>The backend name.</summary>
    public const string Name = "auto";

    /// <inheritdoc/>
    public override string Backend => Name;

    /// <inheritdoc/>
    private protected override IReadOnlyList<VectorHit> Search(SqliteConnection connection, ReadOnlySpan<sbyte> query, int k, ChunkFilter filter, CancellationToken cancellationToken)
    {
        var cache = Storage.GetCache(connection, query.Length);
        if (cache is not null)
        {
            return cache.Search(query, k, filter, cancellationToken);
        }

        return (Storage.ExtensionLoaded ? SqliteVecIndex.Knn(connection, query, k, filter, cancellationToken) : null)
            ?? BruteForceVectorIndex.Scan(connection, query, k, filter, cancellationToken);
    }
}

/// <summary>
/// The brute-force backend: reads every int8 vector of <c>chunk_embeddings</c> that passes the filter and computes the
/// cosine distance in managed code (AVX2 or 128-bit SIMD), keeping the best <c>k</c> in a heap. While the vectors fit
/// <see cref="IndexStorageOptions.VectorCacheMaxBytes"/> it scans an in-memory copy of them instead of the table, reloaded
/// when the <c>vec_generation</c> counter moves. Used when sqlite-vec cannot be loaded; it gives the same ranking as
/// sqlite-vec because it computes the distance the same way.
/// </summary>
public sealed class BruteForceVectorIndex : VectorIndexBase
{
    internal BruteForceVectorIndex(VectorStorage storage)
        : base(storage)
    {
    }

    /// <summary>The backend name.</summary>
    public const string Name = "brute-force";

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

    /// <summary>Searches the in-memory copy of the vectors when it fits the cache limit, otherwise scans the table.</summary>
    internal static IReadOnlyList<VectorHit> CachedOrScan(VectorStorage storage, SqliteConnection connection, ReadOnlySpan<sbyte> query, int k, ChunkFilter filter, CancellationToken cancellationToken)
    {
        var cache = storage.GetCache(connection, query.Length);
        return cache is not null
            ? cache.Search(query, k, filter, cancellationToken)
            : Scan(connection, query, k, filter, cancellationToken);
    }
    /// <inheritdoc/>
    private protected override IReadOnlyList<VectorHit> Search(SqliteConnection connection, ReadOnlySpan<sbyte> query, int k, ChunkFilter filter, CancellationToken cancellationToken) =>
        CachedOrScan(Storage, connection, query, k, filter, cancellationToken);

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
