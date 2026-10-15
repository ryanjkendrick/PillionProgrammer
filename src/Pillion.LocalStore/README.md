# Pillion.LocalStore

Pillion Host's encrypted SQLite databases (design document section 10, work item 2.1). A separate library so the phase 2
index can share it. Behind the `localStore` feature flag (see the repository README, Feature flags). The index storage
(`Index\`, work item 2.8) is library code used by the indexing pipeline under `features.indexing`; see Index storage below.

## Layout

```
%LOCALAPPDATA%\PillionProgrammer\
  conversations.db            encrypted SQLite (SQLite3 Multiple Ciphers, ChaCha20-Poly1305), WAL journal
  conversations.db-wal, -shm  SQLite's WAL and its index, while the database is open (the WAL is encrypted too)
  conversations.key.bin       the database key, DPAPI-protected for the current Windows user, user-only ACL
  conversations.db.v<n>.bak   a byte copy of the (still encrypted) file, made before migrating from schema <n>
  index\<index-id>\
    index.db, key.bin         phase 2: one database per folder, so its key is plain key.bin (the default)
```

`EncryptedDatabaseOptions.KeyFilePath` defaults to `key.bin` beside the database. The data root can hold several
databases, so the conversations database names its key after itself (`PillionPaths.ConversationsKeyFile`): every
database has its own key.

`conversations.db` schema version 2 (`Conversations/ConversationsSchema.cs`); times are Unix milliseconds, UTC:

| Table | Columns |
| --- | --- |
| `conversations` | id, title, created, updated, model |
| `messages` | id, conversation\_id (cascade delete), role, content, model, provider, tokens\_in, tokens\_out, cost, created, citations (JSON, schema 2), cancelled (schema 2) |
| `pins` | conversation\_id (cascade delete), kind, target, token\_count, ordinal (schema 2), display\_name (schema 2) |
| `outbound_log` | id, time, request\_id, provider, model, task\_class, locality (`local` or `cloud`), prompt\_tokens, prompt\_tokens\_counted, completion\_tokens, cache\_hit, first\_token\_ms, duration\_ms, outcome, error\_code, redactions, context\_items, context\_trimmed, spans |
| `meta` | key, value: `schema_version`, `min_reader_version`, `store`, `written_by`, `created_utc` |

`outbound_log` holds every field of the gateway's in-memory `OutboundRecord` (enums as their names), plus `spans`
(files and line spans as JSON, design section 10) for when the gateway records them. `IConversationRepository` is the
typed API over these tables; the host uses it for the conversation service, for saving chat turns and for the Outbound log
(work item 2.2, `src/Pillion.Host/Conversations`).

Schema 2 (work item 2.2) is additive, so `min_reader_version` stays 1 and a build that only knows schema 1 can still use the
file: a reply's `citations` (JSON, the `file:line` spans it cited), a `cancelled` flag for a reply stopped before it finished
(`content` is what had arrived), and the pins' `ordinal` (they come back in the order the Chat window shows them) and
`display_name`. Pins hold what to resolve again, never the content.

Retention (`ConversationRetention`): conversations and the Outbound log are kept until the user deletes them.
`DeleteAllAsync` empties every table, runs `VACUUM` so the freed pages go too, and deletes the migration backups.

## Keys

- Each database has a random 256-bit key (`RandomNumberGenerator`), created with the database. It is protected with
  `ProtectedData.Protect` (DPAPI, `CurrentUser` scope, Pillion-specific entropy) and written as `PPK` + format version
  byte `1` + the DPAPI blob. The key file is created with a user-only ACL: the current user owns it and has full
  control, and inheritance from the folder is removed. Several processes racing to create a key all end up with the one
  that reached the disk first (temp file, then a rename that does not overwrite).
- The key is applied with `PRAGMA hexkey = '<64 hex digits>'` immediately after `Open`, before any other statement, on
  every connection. SQLite3 Multiple Ciphers answers `ok`; anything else (a plain SQLite would ignore the pragma) stops
  the open, so a database is never created unencrypted. `SELECT count(*) FROM sqlite_schema` then proves the key fits.
  `EncryptedDatabaseOptions.ConnectionOpened` runs next, on every connection, before the other pragmas and any migration
  (for example to load a SQLite extension).
- A missing key for an existing database, a key file DPAPI refuses (another user or machine, or damage), and a key that
  does not open the file each fail with a `LocalStoreException` whose `Kind` says which. A new key is never made for an
  existing database.
- The key lives in pinned memory, cleared on dispose; it is never logged, never put in a connection string (pooling is
  off and connections are kept open by `EncryptedDatabase`) and never put in an exception.
- Keying a connection costs about 40 ms (SQLite3 Multiple Ciphers' key derivation), once per connection; the writer and
  up to `MaxReaders` (4) readers stay open for the life of the database.
- Temporary tables and sorts are kept in memory (`temp_store = MEMORY`) and freed pages are zeroed (`secure_delete`),
  so no plain copy of the data is written outside the database. Backups are byte copies of the encrypted file.

## Conventions

- `EncryptedDatabase.OpenAsync(options, schema)` opens or creates the file, switches it to WAL, and checks and migrates
  its schema. Writes go through `WriteAsync`: one writer connection per process, one write at a time, each in its own
  `BEGIN IMMEDIATE` transaction (rolled back if the delegate throws). Reads go through `ReadAsync` on pooled
  `query_only` connections that run alongside the writer. Another process (another Visual Studio) opens the same file
  with its own writer; SQLite's locks and the busy timeout (5 s) serialise them.
- `meta.schema_version` is the schema the file has; `meta.min_reader_version` is the oldest schema whose code can still
  use it. On open, inside `BEGIN IMMEDIATE`:
  - `min_reader_version` newer than this build's schema: refused with `LocalStoreErrorKind.NewerSchema` (a
    `PillionRpcException`, code `StorageUnavailable`, message naming both versions); the file is not touched.
  - `schema_version` newer but `min_reader_version` still within this build: opened as is, without migrating.
  - `schema_version` older: the file and its WAL are copied to `<file>.v<old>.bak` (and `.bak-wal`), then every missing
    migration runs in the same transaction, which also writes the new versions. A failure rolls everything back
    (`MigrationFailed`) and leaves the file at its old version.
- `EncryptedDatabaseOptions.ReadOnly` opens an existing database for a host that does not own it (the index's non-writers,
  see `src/Pillion.Indexing/README.md`): the file and its key must exist (`DatabaseMissing`, `KeyMissing`; a key is never
  created), the journal mode is left alone, no migration runs (an older schema is refused with `NeedsMigration`, a newer
  incompatible one with `NewerSchema`), reads work alongside the owner's writes, and `WriteAsync` and `VacuumAsync` throw
  `InvalidOperationException`. `IsReadOnly` tells which kind of database you hold.
- Every file carries a version: databases in `meta`, key files in their header byte, `settings.json` in its `version`.

## Adding a migration

1. Append a `StoreMigration` to the schema's list (for conversations, `ConversationsSchema.Schema`). Its `Version` is
   the next number; versions start at 1 and have no gaps.
2. Choose `MinReaderVersion`. Keep the previous value when older builds can ignore the change (a new table, a new
   nullable column, a new index): Visual Studio 2022 and 2026 with different extension versions can then keep sharing
   the file. Raise it to the new version when older builds would misread or corrupt the data (a renamed or dropped
   column, a changed meaning); older builds will then refuse the file with a clear message.
3. Use `StoreMigration.Sql(version, minReader, description, sql)` for a script, or the full constructor for code. Use
   the connection and transaction you are given; do not commit, and do not run `VACUUM` or change `journal_mode`.
4. Add a test that opens a database at the previous version, writes data, reopens with the new schema and checks the
   backup and the migrated data (see `MigrationTests`).

## Errors

`LocalStoreException` derives from `PillionRpcException`, so it can cross the RPC boundary unchanged. Every kind uses
the contract 1.3 code `StorageUnavailable`, except `FeatureDisabled`, which uses `PolicyDenied` like every other feature
flag that is off (before contract 1.3 every kind used `PolicyDenied`); `Kind` (`FeatureDisabled`, `CipherUnavailable`,
`KeyMissing`, `KeyUnreadable`, `KeyMismatch`, `NewerSchema`, `MigrationFailed`, `DatabaseMissing`, `NeedsMigration`) carries the precise reason. Messages are
written for the user and never contain key material; `Details` holds the file path.

## Native library

`SQLitePCLRaw.bundle_e_sqlite3mc` ships `e_sqlite3mc.dll` (SQLite3 Multiple Ciphers 2.1.0, SQLite 3.49.1) for each
runtime; the self-contained win-x64 host publish copies the win-x64 one beside `Pillion.Host.exe`.
`Microsoft.Data.Sqlite.Core` (not `Microsoft.Data.Sqlite`) is used so that no plain `e_sqlite3` is ever loaded. Do not
add another SQLitePCLRaw bundle to the host: the provider is process-wide.

## Index storage

`Index\` (work item 2.8, design sections 7 and 10) stores the chunks, the lexical index, the vectors and the symbols of
`index\<index-id>\index.db`. The chunk text lives only in this encrypted database. No SQLite extension is needed.

Schema. Index schema version 1 (the `files` table and the index identity) belongs to repository discovery (work item
2.3). `IndexStorageSchema.Migration` is version 2 and chains after it:
`new StoreSchema("index", [version1, IndexStorageSchema.Migration])`, or `IndexStorageSchema.After(version1)`. It needs
only `files (id, language, partition)` and raises `min_reader_version` to 2. Version 3 (`IndexSymbolsSchema`, work item
2.5) recreates the symbol tables with stable ids and ambiguity columns and keeps the reader version at 2;
`IndexStorageSchema.After(version1)` returns all three, and `IndexStorage` needs version 3. The tables are listed in
`Index/IndexStorageSchema.cs` and `Index/IndexSymbolsSchema.cs`:

| Table | Columns |
| --- | --- |
| `chunks` | id (never reused), file\_id (cascade from `files`), start\_line, end\_line, kind, breadcrumb, text, token\_count |
| `chunks_fts` (FTS5, contentless, `contentless_delete=1`) | rowid = chunk id; `text` (breadcrumb, line break, text as is) and `identifiers` (split identifiers) |
| `chunk_embeddings` | chunk\_id (cascade from `chunks`), language, partition, embedding (int8 blob) |
| `vec_float32` | chunk\_id, embedding (float32 blob). **Development only**, for the int8 recall-loss measurement; drop before release |
| `vec_generation` | one counter that every change to `chunk_embeddings` bumps (triggers, so cascades count too) |
| `symbols` | id (a plain `INTEGER PRIMARY KEY`: the extractor's stable id, or assigned), file\_id, name, kind, container, start\_line, end\_line, signature |
| `symbol_refs` | from\_symbol\_id (null at file scope), to\_symbol\_id, file\_id, line, kind, ambiguous (0/1), candidates (at least 1); cascades from both symbols |
| `meta` | adds `vector_dimensions` and `symbols_version` |

Lexical search. Identifier splitting happens at write time into the second FTS5 column (phase 2 decision of 2026-10-15),
with the stock `unicode61` tokenizer and `tokenchars '_'`, so `get_order_total` stays one token in `text` while
`identifiers` holds `get order total`. Splitting breaks on underscores, lower-to-upper case changes, the end of an
acronym (`HTTPServer`) and letter-digit changes, and lower-cases the parts (`IdentifierSplitter`). A query is split the
same way: each token matches whole or as the phrase of its parts, tokens are combined with `OR`, the last token is a
prefix, and FTS5 syntax in the query is dropped. Ranking is `bm25(chunks_fts, 1.0, 0.5)` (weights in
`IndexStorageOptions`); `LexicalHit.Score` is the negated value, so higher is better. A delete trigger on `chunks` removes
FTS5 rows, so deletes through any path (including a cascade from `files`) keep them in step.

Vectors. The int8 vectors live in `chunk_embeddings`; the first vector stored fixes `meta.vector_dimensions`.
`IVectorIndex` (`UpsertAsync`, `DeleteByFileAsync`, `SearchAsync(query, k, filter)` returning chunk id and cosine
distance) has two implementations over the same rows, chosen with `IndexStorageOptions.VectorBackend`; both compute the
distance in managed code with AVX2 or 128-bit SIMD and rank alike:

- `AutoVectorIndex` (`VectorBackendPreference.InMemory`, the default): brute force over an in-memory copy of the vectors
  while they fit `IndexStorageOptions.VectorCacheMaxBytes` (64 MiB, about 170,000 chunks of 384 dimensions), otherwise
  the table scan. The copy holds vectors only (never text), is never written to disk, and is reloaded when
  `vec_generation` moves (about 0.8 s per 100,000 chunks); `IndexStorage.WarmVectorCacheAsync` reloads it ahead of the
  next search, for the indexing pipeline to call after each batch.
- `TableScanVectorIndex` (`VectorBackendPreference.TableScan`): reads and decrypts `chunk_embeddings` on every search and
  keeps no copy.

Measured with the benchmark (`IndexPerformanceTests`, `Category=Benchmark`: 100,000 chunks, 384 dimensions, 329-byte
synthetic texts, k = 50) on the development machine: in-memory p50 3 ms, p95 3.4 ms; table scan p50 about 400 ms;
FTS5 p50 63 ms, p95 116 ms; about 1,210 bytes per chunk on disk (chunks about 440, FTS5 about 305, vectors about 455).

Beyond the in-memory cap. An HNSW or sqlite-vec backend can be added behind `IVectorIndex` for indexes larger than the
in-memory cap. A sqlite-vec 0.1.9 implementation (a pinned, hash-verified `vec0.dll` downloaded at build time, a `vec0`
table with `language` and `partition` metadata filters kept in step with `chunk_embeddings`) is in this branch's history
at commit `6a487f9` (branch `feature/index-storage`, work item 2.8). It was dropped for this phase because, with the pages
decrypted on every read, its KNN query took about 205 ms at 100,000 chunks, and its copy of the vectors cost about 450
bytes per chunk. `EncryptedDatabaseOptions.ConnectionOpened` is where such an extension would be loaded.

Symbols. `ISymbolRepository` stores and reads `symbols` and `symbol_refs`; extraction, resolution and the repo map are
work item 2.5 (Pillion.ContextEngine). Symbols written with their stable `NewSymbol.Id` (a hash of path, container, name,
kind and overload ordinal, with no line in it) are upserted when their file is replaced, so references from other files
to a symbol that is still there survive; the file's other symbols go, and with them (cascade) every reference from or to
them, which the extractor restores when it next visits the referring files. Symbols without an id get a new one each
time. An ambiguous reference is one row per candidate with `ambiguous = 1` and the candidate count.
`GetSymbolsVersionAsync`/`SetSymbolsVersionAsync` read and write `meta.symbols_version`, which the host records after
rebuilding the symbols, separately from `chunker_version`; the version 3 migration clears it, so the symbols of a
migrated index are rebuilt.
