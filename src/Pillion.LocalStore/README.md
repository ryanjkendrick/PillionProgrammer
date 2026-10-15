# Pillion.LocalStore

Pillion Host's encrypted SQLite databases (design document section 10, work item 2.1). A separate library so the phase 2
index can share it. Behind the `localStore` feature flag (see the repository README, Feature flags).

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

`conversations.db` schema version 1 (`Conversations/ConversationsSchema.cs`); times are Unix milliseconds, UTC:

| Table | Columns |
| --- | --- |
| `conversations` | id, title, created, updated, model |
| `messages` | id, conversation\_id (cascade delete), role, content, model, provider, tokens\_in, tokens\_out, cost, created |
| `pins` | conversation\_id (cascade delete), kind, target, token\_count |
| `outbound_log` | id, time, request\_id, provider, model, task\_class, locality (`local` or `cloud`), prompt\_tokens, prompt\_tokens\_counted, completion\_tokens, cache\_hit, first\_token\_ms, duration\_ms, outcome, error\_code, redactions, context\_items, context\_trimmed, spans |
| `meta` | key, value: `schema_version`, `min_reader_version`, `store`, `written_by`, `created_utc` |

`outbound_log` holds every field of the gateway's in-memory `OutboundRecord` (enums as their names), plus `spans`
(files and line spans as JSON, design section 10) for when the gateway records them. `IConversationRepository` is the
typed API over these tables; nothing in the host uses it yet (work item 2.2 does).

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
    `PillionRpcException`, code `PolicyDenied`, message naming both versions); the file is not touched.
  - `schema_version` newer but `min_reader_version` still within this build: opened as is, without migrating.
  - `schema_version` older: the file and its WAL are copied to `<file>.v<old>.bak` (and `.bak-wal`), then every missing
    migration runs in the same transaction, which also writes the new versions. A failure rolls everything back
    (`MigrationFailed`) and leaves the file at its old version.
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

`LocalStoreException` derives from `PillionRpcException`, so it can cross the RPC boundary unchanged. The contract has
no storage error code yet, so every kind uses `PolicyDenied`; `Kind` (`FeatureDisabled`, `CipherUnavailable`,
`KeyMissing`, `KeyUnreadable`, `KeyMismatch`, `NewerSchema`, `MigrationFailed`) carries the precise reason. Messages are
written for the user and never contain key material; `Details` holds the file path.

## Native library

`SQLitePCLRaw.bundle_e_sqlite3mc` ships `e_sqlite3mc.dll` (SQLite3 Multiple Ciphers 2.1.0, SQLite 3.49.1) for each
runtime; the self-contained win-x64 host publish copies the win-x64 one beside `Pillion.Host.exe`.
`Microsoft.Data.Sqlite.Core` (not `Microsoft.Data.Sqlite`) is used so that no plain `e_sqlite3` is ever loaded. Do not
add another SQLitePCLRaw bundle to the host: the provider is process-wide.
