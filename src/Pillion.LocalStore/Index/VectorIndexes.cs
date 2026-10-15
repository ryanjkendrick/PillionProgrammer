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
    private protected override IReadOnlyList<VectorHit> Search(SqliteConnection connection, ReadOnlySpan<sbyte> query, int k, ChunkFilter filter, CancellationToken cancellationToken)
    {
        if (!Sql.TableExists(connection, null, IndexStorageSchema.VectorTable) || VectorStorage.IsStale(connection, null))
        {
            return BruteForceVectorIndex.Scan(connection, query, k, filter, cancellationToken);
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
/// The brute-force backend: reads every int8 vector of <c>chunk_embeddings</c> that passes the filter and computes the
/// cosine distance in managed code (AVX2 or 128-bit SIMD), keeping the best <c>k</c> in a heap. Used when sqlite-vec
/// cannot be loaded; it gives the same ranking as sqlite-vec because it computes the distance the same way.
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
        var heap = new PriorityQueue<long, (float Distance, long Id)>(k + 1, WorstFirst.Instance);
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

            var id = reader.GetInt64(0);
            Int8Vectors.DotAndNorm(query, System.Runtime.InteropServices.MemoryMarshal.Cast<byte, sbyte>(buffer), out var dot, out var norm);
            var distance = Int8Vectors.CosineDistance(dot, queryNorm, norm);
            if (heap.Count < k)
            {
                heap.Enqueue(id, (distance, id));
            }
            else if (heap.TryPeek(out _, out var worst) && WorstFirst.Instance.Compare((distance, id), worst) > 0)
            {
                heap.DequeueEnqueue(id, (distance, id));
            }
        }

        var hits = new List<VectorHit>(heap.Count);
        while (heap.TryDequeue(out var id, out var priority))
        {
            hits.Add(new VectorHit(id, priority.Distance));
        }

        hits.Reverse();
        return hits;
    }

    /// <inheritdoc/>
    private protected override IReadOnlyList<VectorHit> Search(SqliteConnection connection, ReadOnlySpan<sbyte> query, int k, ChunkFilter filter, CancellationToken cancellationToken) =>
        Scan(connection, query, k, filter, cancellationToken);

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
