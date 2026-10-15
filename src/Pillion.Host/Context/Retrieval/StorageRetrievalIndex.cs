using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using Pillion.ContextEngine.Retrieval;
using Pillion.ContextEngine.Symbols;
using Pillion.Indexing.Storage;
using Pillion.LocalStore.Index;

namespace Pillion.Host.Context.Retrieval;

/// <summary>
/// Adapts an open index (<see cref="CatalogIndex"/>) to what retrieval reads: the chunk, vector and symbol storage, the
/// <c>files</c> table and the embedding model in <c>meta</c>. Made per search, so the model and vector size are current.
/// </summary>
internal sealed class StorageRetrievalIndex : IRetrievalIndex, IRetrievalFileNameLookup
{
    private const int MaxFilesPerName = 20;
    private readonly CatalogIndex _index;

    private StorageRetrievalIndex(CatalogIndex index, string? model, int? dimensions)
    {
        _index = index;
        EmbeddingModelId = model;
        VectorDimensions = dimensions;
    }

    /// <inheritdoc/>
    public string IndexId => _index.IndexId;

    /// <inheritdoc/>
    public string RootPath => _index.RootPath;

    /// <inheritdoc/>
    public string? EmbeddingModelId { get; }

    /// <inheritdoc/>
    public int? VectorDimensions { get; }

    /// <inheritdoc/>
    public IChunkRepository Chunks => _index.Storage.Chunks;

    /// <inheritdoc/>
    public IVectorIndex Vectors => _index.Storage.Vectors;

    /// <inheritdoc/>
    public ISymbolRepository Symbols => _index.Storage.Symbols;

    /// <inheritdoc/>
    public SymbolGraph? Graph => _index.Graph;

    /// <summary>Reads the index's embedding model and vector size and makes the adapter.</summary>
    /// <param name="index">The open index.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The adapter.</returns>
    public static async Task<StorageRetrievalIndex> CreateAsync(CatalogIndex index, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(index);
        var meta = await index.Storage.Database.ReadAsync(
            (connection, token) =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT key, value FROM meta WHERE key IN ($model, $dims)";
                command.Parameters.AddWithValue("$model", IndexMetaKeys.EmbeddingModel);
                command.Parameters.AddWithValue("$dims", IndexStorageSchema.MetaKeys.VectorDimensions);
                var values = new Dictionary<string, string>(StringComparer.Ordinal);
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    values[reader.GetString(0)] = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
                }

                return Task.FromResult(values);
            },
            cancellationToken).ConfigureAwait(false);

        var model = meta.TryGetValue(IndexMetaKeys.EmbeddingModel, out var m) && m.Length > 0 ? m : null;
        int? dimensions = meta.TryGetValue(IndexStorageSchema.MetaKeys.VectorDimensions, out var d)
            && int.TryParse(d, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > 0 ? parsed : null;
        return new StorageRetrievalIndex(index, model, dimensions);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyDictionary<long, RetrievalFile>> GetFilesAsync(IReadOnlyCollection<long> fileIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fileIds);
        if (fileIds.Count == 0)
        {
            return new Dictionary<long, RetrievalFile>();
        }

        var files = await QueryFilesAsync(
            "SELECT id, path, language, partition FROM files WHERE id IN (SELECT value FROM json_each($ids))",
            [("$ids", "[" + string.Join(',', fileIds.Select(i => i.ToString(CultureInfo.InvariantCulture))) + "]")],
            cancellationToken).ConfigureAwait(false);
        return files.ToDictionary(f => f.Id);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyDictionary<string, RetrievalFile>> FindFilesAsync(IReadOnlyCollection<string> paths, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var result = new Dictionary<string, RetrievalFile>(StringComparer.OrdinalIgnoreCase);
        if (paths.Count == 0)
        {
            return result;
        }

        var files = await QueryFilesAsync(
            "SELECT id, path, language, partition FROM files WHERE path COLLATE NOCASE IN (SELECT value FROM json_each($paths))",
            [("$paths", JsonArray(paths))],
            cancellationToken).ConfigureAwait(false);
        foreach (var path in paths)
        {
            if (files.FirstOrDefault(f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase)) is { } file)
            {
                result[path] = file;
            }
        }

        return result;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyDictionary<string, IReadOnlyList<long>>> FindFilesByNameAsync(IReadOnlyCollection<string> names, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(names);
        var result = new Dictionary<string, IReadOnlyList<long>>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in names.Where(n => n.Length > 0))
        {
            var files = await QueryFilesAsync(
                "SELECT id, path, language, partition FROM files WHERE path = $name COLLATE NOCASE OR path LIKE $suffix ESCAPE '\\' ORDER BY length(path), path LIMIT $limit",
                [("$name", name), ("$suffix", "%/" + EscapeLike(name)), ("$limit", MaxFilesPerName)],
                cancellationToken).ConfigureAwait(false);
            if (files.Count > 0)
            {
                result[name] = [.. files.Select(f => f.Id)];
            }
        }

        return result;
    }

    private Task<List<RetrievalFile>> QueryFilesAsync(string sql, (string Name, object Value)[] parameters, CancellationToken cancellationToken) =>
        _index.Storage.Database.ReadAsync(
            (connection, token) =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = sql;
                foreach (var (name, value) in parameters)
                {
                    command.Parameters.AddWithValue(name, value);
                }

                var files = new List<RetrievalFile>();
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    token.ThrowIfCancellationRequested();
                    files.Add(new RetrievalFile(reader.GetInt64(0), reader.GetString(1), ReadText(reader, 2), ReadText(reader, 3)));
                }

                return Task.FromResult(files);
            },
            cancellationToken);

    private static string ReadText(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? string.Empty : reader.GetString(ordinal);

    private static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal);

    private static string JsonArray(IEnumerable<string> values)
    {
        var builder = new StringBuilder("[");
        var first = true;
        foreach (var value in values)
        {
            builder.Append(first ? string.Empty : ",").Append(System.Text.Json.JsonSerializer.Serialize(value));
            first = false;
        }

        return builder.Append(']').ToString();
    }
}
