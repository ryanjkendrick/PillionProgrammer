namespace Pillion.LocalStore.Index;

/// <summary>
/// Index schema version 2 (work item 2.8, design section 10, Index schema): the chunk, lexical, vector and symbol tables
/// of <c>index.db</c>. Version 1 (the <c>files</c> table and the index identity in <c>meta</c>) belongs to repository
/// discovery (work item 2.3); this migration chains after it and needs only its <c>files (id, language, partition)</c>.
/// </summary>
/// <remarks>
/// <para>
/// Chaining: the index schema is <c>new StoreSchema("index", [version1, IndexStorageSchema.Migration])</c>, which is
/// what <see cref="After"/> returns. Later index migrations append after this one.
/// </para>
/// <para>
/// The int8 vectors live in the plain table <c>chunk_embeddings</c>; the vector dimension is recorded in
/// <see cref="MetaKeys.VectorDimensions"/> (by the embedding step, or by the first vector stored). Search runs in managed
/// code (<see cref="IVectorIndex"/>); no SQLite extension is needed.
/// </para>
/// <para>Tables:</para>
/// <list type="bullet">
/// <item><c>chunks</c>: id (never reused, so a stale id held elsewhere can never name another chunk), file_id (cascade from <c>files</c>), start_line, end_line, kind, breadcrumb, text, token_count. The
/// chunk text lives only here, in the encrypted database.</item>
/// <item><c>chunks_fts</c>: contentless FTS5 (<c>contentless_delete=1</c>) keyed by the chunk id (its rowid), with two columns:
/// <c>text</c> (breadcrumb, a line break, then the chunk text as is) and <c>identifiers</c> (every identifier of both split on
/// camelCase, PascalCase, snake_case and digit boundaries, lower-cased, written at insert time by
/// <see cref="IdentifierSplitter"/>). The stock <c>unicode61</c> tokenizer with <c>tokenchars '_'</c> keeps
/// <c>get_order_total</c> whole in <c>text</c>. A trigger removes a chunk's row when the chunk is deleted.</item>
/// <item><c>chunk_embeddings</c>: chunk_id (cascade from <c>chunks</c>), language, partition, embedding (int8, one byte per dimension).</item>
/// <item><c>vec_float32</c>: <b>development only, dropped before release</b> (phase 2 decision of 2026-10-15): chunk_id and the
/// float32 vector, kept for the int8 recall-loss measurement (work item 2.16).</item>
/// <item><c>vec_generation</c>: one counter that triggers on <c>chunk_embeddings</c> bump on every change (cascades included), so
/// the in-memory copy of the vectors knows when to reload.</item>
/// <item><c>symbols</c>: id, file_id, name, kind, container, start_line, end_line, signature (approximate, work item 2.5).</item>
/// <item><c>symbol_refs</c>: from_symbol_id (null at file scope), to_symbol_id, file_id, line, kind (approximate, name-based).</item>
/// </list>
/// </remarks>
public static class IndexStorageSchema
{
    /// <summary>The database kind recorded in <c>meta</c> as <c>store</c>.</summary>
    public const string Name = "index";

    /// <summary>The schema version this migration produces.</summary>
    public const int Version = 2;

    /// <summary>
    /// The reader version recorded with it. Raised to 2: a version 1 build does not know the chunk tables, so it must not
    /// write a version 2 database.
    /// </summary>
    public const int MinReaderVersion = 2;


    /// <summary>Version 2: chunks, FTS5, vectors and symbols.</summary>
    internal const string Version2Sql = """
        CREATE TABLE chunks (
            id          INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
            file_id     INTEGER NOT NULL REFERENCES files (id) ON DELETE CASCADE,
            start_line  INTEGER NOT NULL,
            end_line    INTEGER NOT NULL,
            kind        TEXT    NOT NULL,
            breadcrumb  TEXT    NOT NULL DEFAULT '',
            text        TEXT    NOT NULL,
            token_count INTEGER NOT NULL DEFAULT 0
        ) STRICT;
        CREATE INDEX ix_chunks_file ON chunks (file_id, start_line);

        CREATE VIRTUAL TABLE chunks_fts USING fts5 (
            text,
            identifiers,
            content = '',
            contentless_delete = 1,
            tokenize = "unicode61 tokenchars '_'"
        );

        CREATE TABLE chunk_embeddings (
            chunk_id  INTEGER NOT NULL PRIMARY KEY REFERENCES chunks (id) ON DELETE CASCADE,
            language  TEXT    NOT NULL DEFAULT '',
            partition TEXT    NOT NULL DEFAULT '',
            embedding BLOB    NOT NULL
        ) STRICT;

        -- DEVELOPMENT ONLY (phase 2): float32 vectors for the int8 recall-loss measurement. Drop before release.
        CREATE TABLE vec_float32 (
            chunk_id  INTEGER NOT NULL PRIMARY KEY REFERENCES chunks (id) ON DELETE CASCADE,
            embedding BLOB    NOT NULL
        ) STRICT;


        -- Bumped by every change to chunk_embeddings, through any path (cascades included), so an in-memory copy of the
        -- vectors knows when it is out of date.
        CREATE TABLE vec_generation (
            id    INTEGER NOT NULL PRIMARY KEY CHECK (id = 1),
            value INTEGER NOT NULL
        ) STRICT;
        INSERT INTO vec_generation (id, value) VALUES (1, 0);
        CREATE TRIGGER chunk_embeddings_after_insert AFTER INSERT ON chunk_embeddings BEGIN
            UPDATE vec_generation SET value = value + 1 WHERE id = 1;
        END;
        CREATE TRIGGER chunk_embeddings_after_update AFTER UPDATE ON chunk_embeddings BEGIN
            UPDATE vec_generation SET value = value + 1 WHERE id = 1;
        END;
        CREATE TRIGGER chunk_embeddings_after_delete AFTER DELETE ON chunk_embeddings BEGIN
            UPDATE vec_generation SET value = value + 1 WHERE id = 1;
        END;

        CREATE TRIGGER chunks_after_delete AFTER DELETE ON chunks BEGIN
            DELETE FROM chunks_fts WHERE rowid = old.id;
        END;

        CREATE TABLE symbols (
            id         INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
            file_id    INTEGER NOT NULL REFERENCES files (id) ON DELETE CASCADE,
            name       TEXT    NOT NULL,
            kind       TEXT    NOT NULL,
            container  TEXT    NULL,
            start_line INTEGER NOT NULL,
            end_line   INTEGER NOT NULL,
            signature  TEXT    NULL
        ) STRICT;
        CREATE INDEX ix_symbols_file ON symbols (file_id);
        CREATE INDEX ix_symbols_name ON symbols (name COLLATE NOCASE);

        CREATE TABLE symbol_refs (
            from_symbol_id INTEGER NULL     REFERENCES symbols (id) ON DELETE CASCADE,
            to_symbol_id   INTEGER NOT NULL REFERENCES symbols (id) ON DELETE CASCADE,
            file_id        INTEGER NOT NULL REFERENCES files (id) ON DELETE CASCADE,
            line           INTEGER NOT NULL,
            kind           TEXT    NOT NULL
        ) STRICT;
        CREATE INDEX ix_symbol_refs_file ON symbol_refs (file_id);
        CREATE INDEX ix_symbol_refs_from ON symbol_refs (from_symbol_id);
        CREATE INDEX ix_symbol_refs_to ON symbol_refs (to_symbol_id);
        """;

    /// <summary>Gets migration 2: append it after index schema version 1.</summary>
    public static StoreMigration Migration { get; } =
        StoreMigration.Sql(Version, MinReaderVersion, "chunks, FTS5 lexical index, vectors and symbols", Version2Sql);

    /// <summary>Builds the index schema from version 1 (work item 2.3's <c>files</c> and identity) followed by this migration.</summary>
    /// <param name="version1">Migration 1 of the index schema; it must create <c>files</c> with an integer <c>id</c> primary key.</param>
    /// <returns>The schema <c>index</c> at version 2.</returns>
    public static StoreSchema After(StoreMigration version1)
    {
        ArgumentNullException.ThrowIfNull(version1);
        return new StoreSchema(Name, [version1, Migration]);
    }

    /// <summary>The <c>meta</c> keys this schema reads and writes, beside the store's own.</summary>
    public static class MetaKeys
    {
        /// <summary>The vector dimension of the index (written by the embedding step, or by the first vector stored).</summary>
        public const string VectorDimensions = "vector_dimensions";

    }
}
