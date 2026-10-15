using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pillion.Indexing.Ownership;

/// <summary>
/// The content of <c>state.json</c> (design section 10): the plain-text summary of an index that is readable without the
/// database key, so retention and the Index window can list indexes cheaply. It holds paths and versions, never code.
/// The index's owner is the only writer.
/// </summary>
/// <param name="Version">The format version of this file (1).</param>
/// <param name="RootPath">The repository root the index covers, as given when it was created.</param>
/// <param name="EmbeddingModel">The embedding model id.</param>
/// <param name="ChunkerVersion">The chunker version.</param>
/// <param name="SchemaVersion">The schema version of <c>index.db</c> when its owner last opened it.</param>
/// <param name="CreatedUtc">When the index was created.</param>
/// <param name="LastOpenedUtc">When its owner last opened or closed it; retention measures unused days from here.</param>
/// <param name="LastFullSweepUtc">When the last full hash sweep finished, or <see langword="null"/> if none has.</param>
public sealed record IndexState(
    int Version,
    string RootPath,
    string EmbeddingModel,
    string ChunkerVersion,
    int SchemaVersion,
    DateTimeOffset CreatedUtc,
    DateTimeOffset LastOpenedUtc,
    DateTimeOffset? LastFullSweepUtc)
{
    /// <summary>The format version this build writes.</summary>
    public const int CurrentVersion = 1;
}

/// <summary>Reads and writes <c>state.json</c>. Writes are atomic: a temporary file, then a replacing move.</summary>
public static class IndexStateFile
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Reads a state file.</summary>
    /// <param name="path">The <c>state.json</c> path.</param>
    /// <returns>The state, or <see langword="null"/> if the file is missing, unreadable or not a state file.</returns>
    public static IndexState? Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var state = JsonSerializer.Deserialize<IndexState>(stream, Json);
            return state is { Version: >= 1 } && !string.IsNullOrEmpty(state.RootPath) ? state : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Writes a state file atomically.</summary>
    /// <param name="path">The <c>state.json</c> path; its folder must exist.</param>
    /// <param name="state">The state to write.</param>
    public static void Write(string path, IndexState state)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(state);

        var temp = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, state, Json);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }
}
