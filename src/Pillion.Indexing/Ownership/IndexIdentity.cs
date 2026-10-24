using System.Security.Cryptography;
using System.Text;
using Pillion.Common;

namespace Pillion.Indexing.Ownership;

/// <summary>
/// What an index is keyed by (design section 10, Index identity and ownership): the repository root, the embedding model
/// and the chunker version. Change any of the three and the index is a different one, built from scratch in its own
/// folder; the old one is removed by retention.
/// </summary>
public sealed class IndexIdentity : IEquatable<IndexIdentity>
{
    /// <summary>Initializes a new instance of the <see cref="IndexIdentity"/> class.</summary>
    /// <param name="rootPath">The repository root folder.</param>
    /// <param name="embeddingModelId">The embedding model id, for example <c>bge-small-en-v1.5-int8</c>.</param>
    /// <param name="chunkerVersion">The chunker version, bumped whenever chunk boundaries change.</param>
    public IndexIdentity(string rootPath, string embeddingModelId, string chunkerVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(embeddingModelId);
        ArgumentException.ThrowIfNullOrWhiteSpace(chunkerVersion);

        RootPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
        NormalizedRoot = NormalizeRoot(rootPath);
        EmbeddingModelId = embeddingModelId.Trim();
        ChunkerVersion = chunkerVersion.Trim();
        Id = ComputeId(NormalizedRoot, EmbeddingModelId, ChunkerVersion);
    }

    /// <summary>Gets the root folder as given (full path, no trailing separator), for display.</summary>
    public string RootPath { get; }

    /// <summary>Gets the root in its normalised form: full path, lower case, no trailing separator, <c>\</c> separators.</summary>
    public string NormalizedRoot { get; }

    /// <summary>Gets the embedding model id.</summary>
    public string EmbeddingModelId { get; }

    /// <summary>Gets the chunker version.</summary>
    public string ChunkerVersion { get; }

    /// <summary>Gets the index id: 32 lower-case hex digits, a stable hash of the normalised root, the model id and the chunker version. It names the index folder.</summary>
    public string Id { get; }

    /// <summary>
    /// Normalises a root path so that the same folder always gives the same text: the full path, upper-/lower-case
    /// folded (Windows paths do not distinguish case), with <c>\</c> separators and no trailing separator. Links and 8.3
    /// short names are not resolved.
    /// </summary>
    /// <param name="rootPath">A folder path.</param>
    /// <returns>The normalised path.</returns>
    public static string NormalizeRoot(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
        return full.Replace('/', '\\').ToLowerInvariant();
    }

    /// <summary>Computes the index id for the three parts.</summary>
    /// <param name="normalizedRoot">The root from <see cref="NormalizeRoot"/>.</param>
    /// <param name="embeddingModelId">The embedding model id.</param>
    /// <param name="chunkerVersion">The chunker version.</param>
    /// <returns>32 lower-case hex digits.</returns>
    public static string ComputeId(string normalizedRoot, string embeddingModelId, string chunkerVersion)
    {
        // The unit separator cannot occur in a path or a version, so the parts cannot run into each other.
        var text = string.Join('\u001F', "pillion-index-v1", normalizedRoot, embeddingModelId, chunkerVersion);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)).AsSpan(0, 16));
    }

    /// <inheritdoc/>
    public bool Equals(IndexIdentity? other) => other is not null && string.Equals(Id, other.Id, StringComparison.Ordinal);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => Equals(obj as IndexIdentity);

    /// <inheritdoc/>
    public override int GetHashCode() => Id.GetHashCode(StringComparison.Ordinal);

    /// <inheritdoc/>
    public override string ToString() => $"{Id} ({RootPath}, {EmbeddingModelId}, chunker {ChunkerVersion})";
}

/// <summary>The files of one index folder (<c>%LOCALAPPDATA%\PillionProgrammer\index\&lt;index-id&gt;\</c>, design section 10).</summary>
public sealed class IndexFolder
{
    /// <summary>The encrypted database.</summary>
    public const string DatabaseFileName = "index.db";

    /// <summary>The DPAPI-protected database key.</summary>
    public const string KeyFileName = "key.bin";

    /// <summary>The file that names the host that owns the index.</summary>
    public const string LockFileName = "writer.lock";

    /// <summary>The plain-text state: embedding model, chunker version, schema version, last full sweep.</summary>
    public const string StateFileName = "state.json";

    /// <summary>Initializes a new instance of the <see cref="IndexFolder"/> class.</summary>
    /// <param name="directory">The index folder.</param>
    public IndexFolder(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        Directory = Path.GetFullPath(directory);
    }

    /// <summary>Gets the folder.</summary>
    public string Directory { get; }

    /// <summary>Gets the index id: the folder's name.</summary>
    public string Id => Path.GetFileName(Directory);

    /// <summary>Gets the path of <c>index.db</c>.</summary>
    public string DatabaseFile => Path.Combine(Directory, DatabaseFileName);

    /// <summary>Gets the path of <c>key.bin</c>.</summary>
    public string KeyFile => Path.Combine(Directory, KeyFileName);

    /// <summary>Gets the path of <c>writer.lock</c>.</summary>
    public string LockFile => Path.Combine(Directory, LockFileName);

    /// <summary>Gets the path of <c>state.json</c>.</summary>
    public string StateFile => Path.Combine(Directory, StateFileName);

    /// <summary>Gets the folder of an identity under the data root.</summary>
    /// <param name="paths">The data layout.</param>
    /// <param name="identity">The index identity.</param>
    /// <returns>The folder, which may not exist yet.</returns>
    public static IndexFolder For(PillionPaths paths, IndexIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(identity);
        return new IndexFolder(Path.Combine(paths.IndexDirectory, identity.Id));
    }

    /// <summary>Adds up the size of every file in the folder: the database and its WAL, the key, the state, backups.</summary>
    /// <returns>The disk use in bytes; 0 when the folder does not exist.</returns>
    public long GetDiskUsageBytes()
    {
        if (!System.IO.Directory.Exists(Directory))
        {
            return 0;
        }

        long total = 0;
        foreach (var file in new DirectoryInfo(Directory).EnumerateFiles("*", SearchOption.AllDirectories))
        {
            try
            {
                total += file.Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The file vanished while counting.
            }
        }

        return total;
    }
}
