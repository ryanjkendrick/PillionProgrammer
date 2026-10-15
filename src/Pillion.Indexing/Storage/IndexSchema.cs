using Pillion.LocalStore;

namespace Pillion.Indexing.Storage;

/// <summary>
/// The schema of <c>index.db</c> (design section 10, Index schema). Times are Unix milliseconds in UTC.
/// </summary>
/// <remarks>
/// Version 1 holds <c>files</c> and uses the <c>meta</c> table that every Local Store database has (<c>schema_version</c>,
/// <c>min_reader_version</c>, plus the index keys in <see cref="IndexMetaKeys"/>). Work item 2.8 appends migration 2 to
/// <see cref="Schema"/> with <c>chunks</c>, <c>chunks_fts</c>, <c>chunk_vectors</c> and the symbol tables, and 2.5 the
/// symbol tables; each keeps <c>MinReaderVersion</c> at 1 when version 1 code can still read the file (a new table does
/// not change <c>files</c>). <c>files.id</c> is what <c>chunks.file_id</c> will reference, and a changed file keeps its id
/// (the repository upserts by path), so its chunks can be replaced in place.
/// </remarks>
public static class IndexSchema
{
    /// <summary>The database kind recorded in <c>meta</c> as <c>store</c>.</summary>
    public const string Name = "index";

    /// <summary>Version 1: the <c>files</c> table. Paths compare without regard to case, as the file system does.</summary>
    internal const string Version1 = """
        CREATE TABLE files (
            id          INTEGER NOT NULL PRIMARY KEY,
            path        TEXT    NOT NULL COLLATE NOCASE UNIQUE,
            language    TEXT    NOT NULL,
            partition   TEXT    NOT NULL,
            hash        INTEGER NOT NULL,
            size        INTEGER NOT NULL,
            mtime       INTEGER NOT NULL,
            indexed_at  INTEGER NOT NULL
        ) STRICT;
        CREATE INDEX ix_files_partition ON files (partition);
        """;

    /// <summary>Gets the schema: every migration of <c>index.db</c>, in order.</summary>
    public static StoreSchema Schema { get; } = new(
        Name,
        [
            StoreMigration.Sql(1, 1, "files", Version1),
        ]);
}

/// <summary>The index-specific keys written to the <c>meta</c> table when an index is created.</summary>
public static class IndexMetaKeys
{
    /// <summary>The embedding model id the index was built for (part of the index identity).</summary>
    public const string EmbeddingModel = "embedding_model";

    /// <summary>The chunker version the index was built with (part of the index identity).</summary>
    public const string ChunkerVersion = "chunker_version";

    /// <summary>The repository root the index belongs to, as given when it was created.</summary>
    public const string RootPath = "root_path";
}
