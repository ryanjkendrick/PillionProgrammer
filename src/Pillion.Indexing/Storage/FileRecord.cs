namespace Pillion.Indexing.Storage;

/// <summary>One row of the <c>files</c> table: an indexed file, identified by its path relative to the repository root.</summary>
/// <param name="Id">The row id (0 for a record that has not been stored yet). A changed file keeps its id.</param>
/// <param name="Path">The path relative to the root with <c>/</c> separators.</param>
/// <param name="Language">The language identifier (see <c>Languages</c>).</param>
/// <param name="Partition">The project name, or the top-level folder, the file belongs to (used for path scoping).</param>
/// <param name="Hash">The xxHash64 of the content.</param>
/// <param name="Size">The size in bytes when the hash was taken.</param>
/// <param name="ModifiedUnixMilliseconds">The last write time (UTC, Unix milliseconds) when the hash was taken.</param>
/// <param name="IndexedAtUnixMilliseconds">When the row was last stored (UTC, Unix milliseconds); set by the repository.</param>
public sealed record FileRecord(
    long Id,
    string Path,
    string Language,
    string Partition,
    ulong Hash,
    long Size,
    long ModifiedUnixMilliseconds,
    long IndexedAtUnixMilliseconds);
