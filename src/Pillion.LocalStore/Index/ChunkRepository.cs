using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Pillion.LocalStore.Index;

/// <summary>The <see cref="IChunkRepository"/> over an index database at schema version 2 or later.</summary>
public sealed class ChunkRepository : IChunkRepository
{
    private const string ChunkColumns = "id, file_id, start_line, end_line, kind, breadcrumb, text, token_count";

    private readonly VectorStorage _vectors;
    private readonly double _textWeight;
    private readonly double _identifiersWeight;

    internal ChunkRepository(VectorStorage vectors, double textWeight, double identifiersWeight)
    {
        _vectors = vectors;
        _textWeight = textWeight;
        _identifiersWeight = identifiersWeight;
    }

    private EncryptedDatabase Database => _vectors.Database;

    /// <summary>Builds the FTS5 <c>text</c> column: the breadcrumb, a line break, then the chunk text as is.</summary>
    /// <param name="breadcrumb">The breadcrumb.</param>
    /// <param name="text">The chunk text.</param>
    /// <returns>The indexed text.</returns>
    public static string IndexedText(string? breadcrumb, string text) =>
        string.IsNullOrEmpty(breadcrumb) ? text : breadcrumb + "\n" + text;

    /// <inheritdoc/>
    public async Task<IReadOnlyList<long>> ReplaceFileChunksAsync(long fileId, IReadOnlyList<NewChunk> chunks, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chunks);
        var result = await ReplaceFilesChunksAsync([new FileChunks(fileId, chunks)], cancellationToken).ConfigureAwait(false);
        return result[0];
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<IReadOnlyList<long>>> ReplaceFilesChunksAsync(IReadOnlyList<FileChunks> files, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(files);
        foreach (var file in files)
        {
            ArgumentNullException.ThrowIfNull(file);
            ArgumentNullException.ThrowIfNull(file.Chunks);
            foreach (var chunk in file.Chunks)
            {
                ArgumentNullException.ThrowIfNull(chunk);
                ArgumentNullException.ThrowIfNull(chunk.Text);
                ArgumentException.ThrowIfNullOrEmpty(chunk.Kind);
            }
        }

        return Database.WriteAsync<IReadOnlyList<IReadOnlyList<long>>>(
            (connection, transaction, token) =>
            {
                using var vectors = _vectors.BeginWrite(connection, transaction);
                using var writer = new ChunkWriter(connection, transaction);
                var results = new List<IReadOnlyList<long>>(files.Count);
                foreach (var file in files)
                {
                    token.ThrowIfCancellationRequested();
                    var (language, partition) = writer.ReadFile(file.FileId)
                        ?? throw new KeyNotFoundException($"There is no file with id {file.FileId} in the index.");

                    // The delete trigger removes the FTS5 rows; the cascade removes the vectors; vec_deleted remembers the
                    // chunk_vectors rows, dropped below.
                    writer.DeleteChunks(file.FileId);
                    var ids = new long[file.Chunks.Count];
                    for (var i = 0; i < file.Chunks.Count; i++)
                    {
                        var chunk = file.Chunks[i];
                        var id = writer.InsertChunk(file.FileId, chunk);
                        ids[i] = id;
                        if (!chunk.Embedding.IsEmpty)
                        {
                            vectors.Upsert(id, chunk.Embedding.Span, language, partition, isNew: true);
                        }

                        if (!chunk.EmbeddingFloat32.IsEmpty)
                        {
                            vectors.UpsertFloat32(id, chunk.EmbeddingFloat32.Span);
                        }
                    }

                    results.Add(ids);
                }

                vectors.DrainDeleted();
                return Task.FromResult<IReadOnlyList<IReadOnlyList<long>>>(results);
            },
            cancellationToken);
    }

    /// <inheritdoc/>
    public Task<int> DeleteFileChunksAsync(long fileId, CancellationToken cancellationToken = default) =>
        Database.WriteAsync(
            (connection, transaction, _) =>
            {
                using var vectors = _vectors.BeginWrite(connection, transaction);
                var deleted = Sql.Execute(connection, transaction, "DELETE FROM chunks WHERE file_id = $file", ("$file", fileId));
                vectors.DrainDeleted();
                return Task.FromResult(deleted);
            },
            cancellationToken);

    /// <inheritdoc/>
    public Task<int> UpsertEmbeddingsAsync(IReadOnlyList<ChunkEmbedding> embeddings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(embeddings);
        return Database.WriteAsync(
            (connection, transaction, token) =>
            {
                using var vectors = _vectors.BeginWrite(connection, transaction);
                using var lookup = Sql.Command(
                    connection,
                    transaction,
                    "SELECT f.language, f.partition FROM chunks c JOIN files f ON f.id = c.file_id WHERE c.id = $id",
                    ("$id", 0L));
                var stored = 0;
                foreach (var embedding in embeddings)
                {
                    token.ThrowIfCancellationRequested();
                    ArgumentNullException.ThrowIfNull(embedding);
                    lookup.Parameters[0].Value = embedding.ChunkId;
                    string? language;
                    string? partition;
                    using (var reader = lookup.ExecuteReader())
                    {
                        if (!reader.Read())
                        {
                            continue;
                        }

                        language = reader.IsDBNull(0) ? null : reader.GetString(0);
                        partition = reader.IsDBNull(1) ? null : reader.GetString(1);
                    }

                    vectors.Upsert(embedding.ChunkId, embedding.Vector.Span, language, partition, isNew: false);
                    if (!embedding.VectorFloat32.IsEmpty)
                    {
                        vectors.UpsertFloat32(embedding.ChunkId, embedding.VectorFloat32.Span);
                    }

                    stored++;
                }

                return Task.FromResult(stored);
            },
            cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<ChunkRecord?> GetChunkAsync(long chunkId, CancellationToken cancellationToken = default)
    {
        var rows = await QueryAsync($"SELECT {ChunkColumns} FROM chunks WHERE id = $id", ReadChunk, cancellationToken, ("$id", chunkId)).ConfigureAwait(false);
        return rows.Count == 0 ? null : rows[0];
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<ChunkRecord>> GetChunksAsync(IReadOnlyList<long> chunkIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chunkIds);
        if (chunkIds.Count == 0)
        {
            return [];
        }

        var json = new StringBuilder("[");
        for (var i = 0; i < chunkIds.Count; i++)
        {
            json.Append(i == 0 ? string.Empty : ",").Append(chunkIds[i].ToString(CultureInfo.InvariantCulture));
        }

        json.Append(']');
        var rows = await QueryAsync(
            $"SELECT {ChunkColumns} FROM chunks WHERE id IN (SELECT value FROM json_each($ids))",
            ReadChunk,
            cancellationToken,
            ("$ids", json.ToString())).ConfigureAwait(false);

        var byId = rows.ToDictionary(r => r.Id);
        var ordered = new List<ChunkRecord>(rows.Count);
        foreach (var id in chunkIds)
        {
            if (byId.Remove(id, out var row))
            {
                ordered.Add(row);
            }
        }

        return ordered;
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<ChunkRecord>> GetChunksByFileAsync(long fileId, CancellationToken cancellationToken = default) =>
        QueryAsync($"SELECT {ChunkColumns} FROM chunks WHERE file_id = $file ORDER BY start_line, id", ReadChunk, cancellationToken, ("$file", fileId));

    /// <inheritdoc/>
    public Task<long> CountChunksAsync(CancellationToken cancellationToken = default) =>
        Database.ReadAsync((connection, _) => Task.FromResult(Sql.ScalarLong(connection, null, "SELECT count(*) FROM chunks")), cancellationToken);

    /// <inheritdoc/>
    public Task<IReadOnlyList<FileChunkSummary>> ListFilesAsync(CancellationToken cancellationToken = default) =>
        QueryAsync(
            """
            SELECT f.id, f.path, count(c.id), coalesce(sum(c.token_count), 0), count(e.chunk_id)
            FROM files f
            JOIN chunks c ON c.file_id = f.id
            LEFT JOIN chunk_embeddings e ON e.chunk_id = c.id
            GROUP BY f.id
            ORDER BY f.path
            """,
            reader => new FileChunkSummary(reader.GetInt64(0), reader.GetString(1), reader.GetInt32(2), reader.GetInt64(3), reader.GetInt32(4)),
            cancellationToken);

    /// <inheritdoc/>
    public Task<IndexSize> GetSizeAsync(CancellationToken cancellationToken = default) =>
        Database.ReadAsync(
            (connection, _) =>
            {
                var pages = Sql.ScalarLong(connection, null, "PRAGMA page_count");
                var pageSize = Sql.ScalarLong(connection, null, "PRAGMA page_size");
                var free = Sql.ScalarLong(connection, null, "PRAGMA freelist_count");
                var wal = new FileInfo(Database.DatabasePath + "-wal");
                return Task.FromResult(new IndexSize(pages, pageSize, free, wal.Exists ? wal.Length : 0));
            },
            cancellationToken);

    /// <inheritdoc/>
    public Task<IReadOnlyList<LexicalHit>> SearchLexicalAsync(string query, int k, ChunkFilter? filter = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentOutOfRangeException.ThrowIfLessThan(k, 1);
        var match = IdentifierSplitter.BuildMatchQuery(query);
        if (match is null)
        {
            return Task.FromResult<IReadOnlyList<LexicalHit>>([]);
        }

        filter ??= ChunkFilter.None;
        return Database.ReadAsync<IReadOnlyList<LexicalHit>>(
            (connection, token) =>
            {
                using var command = connection.CreateCommand();
                command.Parameters.AddWithValue("$match", match);
                command.Parameters.AddWithValue("$k", k);
                command.Parameters.AddWithValue("$wt", _textWeight);
                command.Parameters.AddWithValue("$wi", _identifiersWeight);
                if (filter.IsEmpty)
                {
                    command.CommandText =
                        "SELECT rowid, bm25(chunks_fts, $wt, $wi) AS score FROM chunks_fts WHERE chunks_fts MATCH $match ORDER BY score LIMIT $k";
                }
                else
                {
                    var clauses = new List<string> { "chunks_fts MATCH $match" };
                    Sql.AddInFilter(command, clauses, "f.partition", filter.Partitions, "p");
                    Sql.AddInFilter(command, clauses, "f.language", filter.Languages, "l");
                    command.CommandText =
                        "SELECT chunks_fts.rowid, bm25(chunks_fts, $wt, $wi) AS score FROM chunks_fts "
                        + "JOIN chunks c ON c.id = chunks_fts.rowid JOIN files f ON f.id = c.file_id "
                        + $"WHERE {string.Join(" AND ", clauses)} ORDER BY score LIMIT $k";
                }

                var hits = new List<LexicalHit>(k);
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    token.ThrowIfCancellationRequested();
                    hits.Add(new LexicalHit(reader.GetInt64(0), -reader.GetDouble(1)));
                }

                return Task.FromResult<IReadOnlyList<LexicalHit>>(hits);
            },
            cancellationToken);
    }

    private static ChunkRecord ReadChunk(SqliteDataReader reader) => new(
        reader.GetInt64(0),
        reader.GetInt64(1),
        reader.GetInt32(2),
        reader.GetInt32(3),
        reader.GetString(4),
        reader.GetString(5),
        reader.GetString(6),
        reader.GetInt32(7));

    private Task<IReadOnlyList<T>> QueryAsync<T>(string sql, Func<SqliteDataReader, T> read, CancellationToken cancellationToken, params (string Name, object? Value)[] parameters) =>
        Database.ReadAsync<IReadOnlyList<T>>(
            (connection, token) =>
            {
                using var command = Sql.Command(connection, null, sql, parameters);
                using var reader = command.ExecuteReader();
                var rows = new List<T>();
                while (reader.Read())
                {
                    token.ThrowIfCancellationRequested();
                    rows.Add(read(reader));
                }

                return Task.FromResult<IReadOnlyList<T>>(rows);
            },
            cancellationToken);

    /// <summary>Prepared statements for writing chunks within one transaction.</summary>
    private sealed class ChunkWriter : IDisposable
    {
        private readonly SqliteCommand _readFile;
        private readonly SqliteCommand _deleteChunks;
        private readonly SqliteCommand _insertChunk;
        private readonly SqliteCommand _insertFts;

        public ChunkWriter(SqliteConnection connection, SqliteTransaction transaction)
        {
            _readFile = Sql.Command(connection, transaction, "SELECT language, partition FROM files WHERE id = $file", ("$file", 0L));
            _deleteChunks = Sql.Command(connection, transaction, "DELETE FROM chunks WHERE file_id = $file", ("$file", 0L));
            _insertChunk = Sql.Command(
                connection,
                transaction,
                """
                INSERT INTO chunks (file_id, start_line, end_line, kind, breadcrumb, text, token_count)
                VALUES ($file, $start, $end, $kind, $breadcrumb, $text, $tokens) RETURNING id
                """,
                ("$file", 0L),
                ("$start", 0),
                ("$end", 0),
                ("$kind", string.Empty),
                ("$breadcrumb", string.Empty),
                ("$text", string.Empty),
                ("$tokens", 0));
            _insertFts = Sql.Command(
                connection,
                transaction,
                "INSERT INTO chunks_fts (rowid, text, identifiers) VALUES ($id, $text, $identifiers)",
                ("$id", 0L),
                ("$text", string.Empty),
                ("$identifiers", string.Empty));
        }

        public (string? Language, string? Partition)? ReadFile(long fileId)
        {
            _readFile.Parameters[0].Value = fileId;
            using var reader = _readFile.ExecuteReader();
            if (!reader.Read())
            {
                return null;
            }

            return (reader.IsDBNull(0) ? null : reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1));
        }

        public void DeleteChunks(long fileId)
        {
            _deleteChunks.Parameters[0].Value = fileId;
            _deleteChunks.ExecuteNonQuery();
        }

        public long InsertChunk(long fileId, NewChunk chunk)
        {
            var parameters = _insertChunk.Parameters;
            parameters[0].Value = fileId;
            parameters[1].Value = chunk.StartLine;
            parameters[2].Value = chunk.EndLine;
            parameters[3].Value = chunk.Kind;
            parameters[4].Value = chunk.Breadcrumb ?? string.Empty;
            parameters[5].Value = chunk.Text;
            parameters[6].Value = chunk.TokenCount;
            var id = Convert.ToInt64(_insertChunk.ExecuteScalar(), CultureInfo.InvariantCulture);

            var indexed = IndexedText(chunk.Breadcrumb, chunk.Text);
            _insertFts.Parameters[0].Value = id;
            _insertFts.Parameters[1].Value = indexed;
            _insertFts.Parameters[2].Value = IdentifierSplitter.BuildIdentifiersColumn(indexed);
            _insertFts.ExecuteNonQuery();
            return id;
        }

        public void Dispose()
        {
            _readFile.Dispose();
            _deleteChunks.Dispose();
            _insertChunk.Dispose();
            _insertFts.Dispose();
        }
    }
}
