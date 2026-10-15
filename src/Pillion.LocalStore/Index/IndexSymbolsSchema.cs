namespace Pillion.LocalStore.Index;

/// <summary>
/// Index schema version 3 (work item 2.5, design section 10): the symbol tables as the symbol extraction writes them.
/// It chains after <see cref="IndexStorageSchema.Migration"/> (version 2), which <see cref="IndexStorageSchema.After"/>
/// already includes.
/// </summary>
/// <remarks>
/// <para>
/// The symbol tables hold derived data, rebuilt whenever the stored <see cref="MetaKeys.SymbolsVersion"/> differs from
/// the build's (a version 2 database has none), so this migration recreates them rather than altering them:
/// </para>
/// <list type="bullet">
/// <item><c>symbols.id</c> becomes a plain <c>INTEGER PRIMARY KEY</c> (no <c>AUTOINCREMENT</c>), so the extractor's
/// stable 63-bit ids can be stored as the row ids and a file's symbols can be upserted in place: references from other
/// files to a symbol whose id did not change survive re-indexing its file. Rows inserted without an id still get one.</item>
/// <item><c>symbol_refs.from_symbol_id</c> stays nullable (a reference from file-level code has no source symbol).</item>
/// <item><c>symbol_refs.ambiguous</c> (0 or 1) and <c>symbol_refs.candidates</c> (at least 1): a name-based
/// reference that resolved to several definitions is stored as one row per candidate, each marked ambiguous with the
/// candidate count.</item>
/// </list>
/// <para>
/// The reader version stays 2: a version 2 build reads and writes these tables correctly (the new columns have
/// defaults, and inserting without an id still works).
/// </para>
/// </remarks>
public static class IndexSymbolsSchema
{
    /// <summary>The schema version this migration produces.</summary>
    public const int Version = 3;

    /// <summary>The reader version recorded with it (unchanged from version 2).</summary>
    public const int MinReaderVersion = 2;

    /// <summary>Version 3: the symbol tables with stable ids and ambiguity columns.</summary>
    internal const string Version3Sql = """
        DROP TABLE symbol_refs;
        DROP TABLE symbols;

        CREATE TABLE symbols (
            id         INTEGER NOT NULL PRIMARY KEY,
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
            kind           TEXT    NOT NULL,
            ambiguous      INTEGER NOT NULL DEFAULT 0 CHECK (ambiguous IN (0, 1)),
            candidates     INTEGER NOT NULL DEFAULT 1 CHECK (candidates >= 1)
        ) STRICT;
        CREATE INDEX ix_symbol_refs_file ON symbol_refs (file_id);
        CREATE INDEX ix_symbol_refs_from ON symbol_refs (from_symbol_id);
        CREATE INDEX ix_symbol_refs_to ON symbol_refs (to_symbol_id);

        DELETE FROM meta WHERE key = 'symbols_version';
        """;

    /// <summary>Gets migration 3: append it after index schema version 2.</summary>
    public static StoreMigration Migration { get; } =
        StoreMigration.Sql(Version, MinReaderVersion, "symbols with stable ids, nullable reference sources, ambiguity columns", Version3Sql);

    /// <summary>The <c>meta</c> keys of the symbol tables.</summary>
    public static class MetaKeys
    {
        /// <summary>
        /// The symbol rules version the stored symbols were built with (<c>ContextEngineVersions.SymbolsVersion</c>),
        /// written by the host after it rebuilds the symbol tables, separately from <c>chunker_version</c>. When it is
        /// missing or differs from the build's, the host rebuilds the symbols and references only.
        /// </summary>
        public const string SymbolsVersion = "symbols_version";
    }
}
