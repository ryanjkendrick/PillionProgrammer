using System.Globalization;

namespace Pillion.LocalStore.Index;

/// <summary>
/// Index schema version 4 (work item 3.2, issue #103, design section 12): the secret scanner runs on every chunk's text
/// before it is stored, and <c>chunks.redacted</c> (0 or 1) records which chunks had something replaced by an index
/// placeholder (<c>&lt;redacted-index:kind&gt;</c>), so the Index window can show the count per root without reading the
/// chunk text. It chains after <see cref="IndexSymbolsSchema.Migration"/> (version 3), which
/// <see cref="IndexStorageSchema.After"/> already includes.
/// </summary>
/// <remarks>
/// The migration only adds a column with a default and a partial index over it, so <c>min_reader_version</c> stays 2: a
/// version 2 or 3 build reads and writes the table correctly (its inserts leave the column at 0). Chunks stored before this
/// version were not scanned; the chunker version was bumped at the same time (<c>ContextEngineVersions.ChunkerVersion</c>),
/// so every index is built again under the new identity and the old one retires (issue #104).
/// </remarks>
public static class IndexRedactionSchema
{
    /// <summary>The schema version this migration produces.</summary>
    public const int Version = 4;

    /// <summary>The reader version recorded with it (unchanged from version 2).</summary>
    public const int MinReaderVersion = 2;

    /// <summary>
    /// The first chunker version whose chunks were scanned before they were stored (<c>ContextEngineVersions.ChunkerVersion</c>
    /// was bumped to it with this schema). An index whose <c>state.json</c> records a lower version holds the text of its
    /// files as they were, secrets included: the host never adopts it as a root's previous index, retrieval's read-only
    /// catalog never opens it, and the pipeline deletes it once the root's current index completes (PR 121 review).
    /// </summary>
    public const string FirstScannedChunkerVersion = "2";

    /// <summary>Tells whether an index built by the given chunker version had its chunks scanned for secrets before they were stored.</summary>
    /// <param name="chunkerVersion">The chunker version as <c>state.json</c> or the index <c>meta</c> row records it (an integer as text).</param>
    /// <returns><see langword="true"/> for <see cref="FirstScannedChunkerVersion"/> and later; <see langword="false"/> for an earlier, missing or unreadable version.</returns>
    public static bool IsScannedChunkerVersion(string? chunkerVersion) =>
        int.TryParse(chunkerVersion, NumberStyles.None, CultureInfo.InvariantCulture, out var version)
        && version >= int.Parse(FirstScannedChunkerVersion, CultureInfo.InvariantCulture);

    /// <summary>Version 4: the <c>redacted</c> flag on <c>chunks</c> and a partial index that makes counting the flagged rows cheap.</summary>
    internal const string Version4Sql = """
        ALTER TABLE chunks ADD COLUMN redacted INTEGER NOT NULL DEFAULT 0 CHECK (redacted IN (0, 1));
        CREATE INDEX ix_chunks_redacted ON chunks (redacted) WHERE redacted = 1;
        """;

    /// <summary>Gets migration 4: append it after index schema version 3.</summary>
    public static StoreMigration Migration { get; } =
        StoreMigration.Sql(Version, MinReaderVersion, "chunks.redacted: the secret scanner's mark on a stored chunk", Version4Sql);
}
