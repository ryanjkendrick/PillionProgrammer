# Pillion.Indexing

Repository discovery, hashing and index ownership for the code index (design document sections 7 and 10, work item
2.3). It decides which files of a repository are indexed and why the others are not, hashes them, keeps the `files`
table of `index.db`, and elects the one host that owns each index. Chunking, embeddings, storage of chunks and the
pipeline that ties them together are later work items (2.4 to 2.9). Behind the `indexing` feature flag (see the
repository README, Feature flags). Pillion Host's indexing pipeline (item 2.9, `src/Pillion.Host/Indexing`) uses it.

## Layout of an index

```
%LOCALAPPDATA%\PillionProgrammer\index\<index-id>\
  index.db        encrypted SQLite (see src/Pillion.LocalStore/README.md), WAL
  key.bin         the database key, DPAPI-protected for the current user, user-only ACL (one key per index)
  writer.lock     JSON: process id, process start time, pipe name, acquisition time, token
  state.json      JSON: root path, embedding model, chunker version, schema version, created, last opened, last full sweep
```

`<index-id>` is `IndexIdentity.Id`: 32 lower-case hex digits, the first 16 bytes of SHA-256 over the text
`pillion-index-v1`, the normalised root, the embedding model id and the chunker version, joined with U+001F. The
normalised root is the full path, lower-cased, with `\` separators and no trailing separator (links and 8.3 short names
are not resolved). Change the root, the model or the chunker version and the index is a different one in a different
folder; the old one goes away through retention.

`state.json` is plain text on purpose (paths and versions, never code) so retention and the Index window can list
indexes without opening a database. Only the index's owner writes it (atomically: temporary file, then a replacing
move).

## Repository roots

`RepositoryRoots.Resolve(paths)` takes what the extension relays (solution file, project files, an opened folder) and
returns the roots to index. For each path it walks up to the nearest folder holding `.git`:

- a `.git` directory: a normal repository (`RepositoryRootKind.GitRepository`);
- a `.git` file with `gitdir: <path>`, absolute or relative, forward or back slashes: a linked worktree when the path
  runs through `worktrees/`, a submodule when through `modules/`, otherwise a separate git dir. `RepositoryRoot.GitDirectory`
  is the real git directory (where `HEAD` lives), for the watcher of item 2.10;
- none: the opened folder (`Folder`). Several folders without git collapse into the outermost one, so a solution folder
  holds its projects.

Projects under one repository give one root; a solution spanning two repositories gives two. Pure path logic, no git
library.

## What is indexed

`RepositoryScanner.ScanAsync(root)` walks the tree and yields one `ScanEntry` per file, and one per directory it
excluded as a whole (it does not enter them), each with a `FileDecision`: indexed, or the `ExclusionReason` with the rule
that did it. The directory walk is sequential and lazy; the content checks run on up to
`ScanOptions.MaxDegreeOfParallelism` threads (default half the logical processors, at least one). Cancelling or leaving
the `await foreach` stops the walk.

Rules, in the order they are applied to a file (the first that fires is the reason):

| # | Rule | Reason |
| --- | --- | --- |
| 1 | Reparse point (symlink, junction): never followed, files and directories alike | `ReparsePoint` |
| 2 | Secret-bearing name: `.env`, `.env.*`, `*.pfx`, `*.pem`, `*.key`, `*.p12`, `secrets.json`, `appsettings.*.json`, `*.publishsettings`, `id_rsa`, `id_dsa`, `id_ecdsa`, `id_ed25519`, `*.ppk`, `*.jks`, `*.keystore`, `.netrc`, `.pgpass` | `SecretBearing` |
| 3 | `.gitignore` at every level (deeper files override shallower ones; `!` negation; anchoring; directory-only; `**`) | `GitIgnore` |
| 4 | `.pillionignore` at the root, same syntax; it comes after `.gitignore`, so its `!` rules re-include what `.gitignore` ignored | `PillionIgnore` |
| 5 | Built-in names, files or folders, at any depth: `bin`, `obj`, `node_modules`, `.git`, `.vs`. `packages` only for a folder at the repository root that looks like a NuGet packages folder (a `repositories.config`, or a `*.nupkg` one or two levels down); any other `packages` is source. A `!` rule in the root `.pillionignore` (`!packages/`, `!bin/`) opts back in to a built-in name, except `.git`; `indexing.exclude` and the secret list cannot be opted back in to | `BuiltIn` |
| 6 | The `indexing.exclude` setting (and repository policy `exclude`), same syntax relative to the root | `UserExclude` |
| 7 | Generated names `*.g.cs`, `*.g.i.cs`, `*.designer.cs`, `*.generated.cs` (and the `.vb` forms) | `GeneratedFile` |
| 8 | Minified `*.min.js`, `*.min.css` | `MinifiedFile` |
| 9 | Lock files: `package-lock.json`, `npm-shrinkwrap.json`, `yarn.lock`, `pnpm-lock.yaml`, `packages.lock.json`, `composer.lock`, `Cargo.lock`, `poetry.lock`, `Pipfile.lock`, `Gemfile.lock`, `go.sum`, `bun.lockb` | `LockFile` |
| 10 | Larger than 1 MB (`ScanOptions.MaxFileBytes`) | `TooLarge` |
| 11 | Auto-generated header in the first 2 KB: `<auto-generated`, `<autogenerated`, `@generated`, the protocol buffer notice, or Go's `Code generated ... DO NOT EDIT` | `GeneratedHeader` |
| 12 | A NUL byte in the first 8 KB (a UTF-16 file with a byte order mark is text) | `Binary` |
| 13 | Cannot be opened (locked, denied, vanished) | `Unreadable` |

A directory is excluded as a whole by 1, 3, 4, 5 and 6, or as `NestedRepository` when it holds its own `.git` (another
repository, indexed under its own root; the root itself may of course have one). Names match without regard to case.

Notes:

- The secret check comes first on purpose: a `.env` that is also gitignored (the usual case) is still reported as
  secret-bearing, so the Index window lists it. Secret-bearing files are never opened, never hashed, never stored.
- Rules 1 to 10 use the name and the directory entry only. Excluded files are never opened (tests lock them to prove it).
- Language is detected from the extension (`Languages.Detect`): `csharp`, `vb`, `razor`, `typescript`, `javascript`,
  `python`, `c`, `cpp` (headers too: a C++ grammar parses C headers), `go`, `rust`, `java`, `sql`, `html`, `css`, `other`.
- Gitignore syntax is Pillion's own implementation of the gitwildmatch subset (`Discovery/Ignore`), compiled to string
  comparisons or non-backtracking regular expressions, so a hostile pattern cannot slow matching down. Not supported:
  `.git/info/exclude`, the global excludes file, `core.ignorecase=false`.

## Hashing and sweeps

`FileHasher.Hash(path)` streams a file through `XxHash64` (`System.IO.Hashing`) with a pooled 64 KB buffer and returns
the hash, the bytes read and the modified time from the same pass. The time is read from the open handle before the first
byte, so a file written during the pass keeps the older time and is hashed again next time.

`RepositorySweeper.SweepAsync(root, storedRows, partitions[, progress])` compares the tree with the stored `files` rows and returns a
`SweepResult` with disjoint sets: `Added`, `Changed` (hash differs; keeps the row id), `Touched` (same hash, but size, time,
language, partition or path casing differ), `Unchanged` (size and time match: the file is not opened at all, and not even
content-checked) and `Deleted` (no indexed file left: removed, renamed, or newly excluded), plus `Excluded`, `SecretFiles`
and `Unreadable` (a locked file keeps its stored row). Nothing is written; the pipeline stores added and changed files
after chunking them, `FilesRepository.CommitAsync(sweep)` applies the whole sweep in one transaction. A branch switch is
this sweep, not a rebuild.

`ScanOptions.TaskScheduler` runs the content checks and the hashing on a given scheduler (the pipeline's below-normal
threads) instead of the thread pool; the overload with an `IProgress<SweepProgress>` reports the entries seen and the files
hashed every 256 entries, at the first hash and at the end (the pipeline turns it into its Discovering and Hashing phases).
`FilesRepository.Upsert(connection, transaction, records)` and `FilesRepository.Delete(connection, transaction, paths)` write
inside a transaction the caller holds, so the pipeline stores a file's row with its chunks and symbols at once.

A row stored within 2 seconds of its file's modified time is hashed again even if size and time match (git's "racily
clean" rule), so a file written twice inside the time resolution cannot keep a stale hash.

`PartitionResolver` chooses `files.partition`: the deepest project folder containing the file (project names come from
the extension), else the top-level folder, else `(root)`.

## Schema

`IndexSchema.Schema` (`Storage/IndexSchema.cs`) is the `StoreSchema` of `index.db`. Version 1 is `files` on top of the
`meta` table every Local Store database has:

| Column | Notes |
| --- | --- |
| `id` | integer primary key; a changed file keeps it, so `chunks.file_id` stays valid |
| `path` | relative to the root, `/` separators, unique without regard to case |
| `language`, `partition` | see above |
| `hash` | xxHash64 as the signed 64-bit integer with the same bits |
| `size`, `mtime` | bytes; Unix milliseconds UTC |
| `indexed_at` | Unix milliseconds UTC, set by the repository on every store |

`meta` also holds `embedding_model`, `chunker_version` and `root_path` (`IndexMetaKeys`), written by the owner.

**Version 2 is the index storage of item 2.8**: `IndexSchema.Schema` is `IndexStorageSchema.After(version 1)`, so one
`index.db` carries `files` and then `chunks`, `chunks_fts`, `chunk_embeddings`, `vec_float32` (development only),
`vec_generation`, `symbols` and `symbol_refs` (see the Index storage section of the Local Store README). Version 2
raises `min_reader_version` to 2. Open the chunk, vector and symbol APIs over an open index with
`IndexStorage.Create(indexStore.Database, ownsDatabase: false)`; on a reader the database is read-only, so they only
search. Deleting a `files` row removes the file's chunks, FTS5 rows, vectors and symbols by cascade and trigger. Later
migrations append after version 2; leave the earlier SQL alone.

## Ownership

`IndexStoreFactory.OpenAsync(new IndexRequest(root, modelId, chunkerVersion, hostPipeName))` returns an `IndexStore`:

1. Take `writer.lock` (`WriterLock.TryAcquire`). The file is written to a temporary name and moved into place without
   overwriting, so exactly one process wins and nobody reads half a lock.
2. Winner (`IndexRole.Writer`): opens `index.db` read-write (creating the key and the database, migrating the schema),
   writes the `meta` identity rows and `state.json`. `IndexStore.Writer` is its own endpoint.
3. Everyone else (`IndexRole.Reader`): opens `index.db` read-only (`EncryptedDatabaseOptions.ReadOnly`: nothing created,
   journal mode and schema untouched, writes throw) and gets `IndexStore.Writer`, an `IndexWriterEndpoint` with the
   owner's pipe name. Item 2.9 does not forward to it yet (a reader re-sweeps when it becomes the writer; see the host
   README, Readers). While the owner has not finished creating or migrating the database the reader retries (20 times, 250 ms apart by default), electing again each round, then gives up with the
   `LocalStoreException` that stopped it.
4. A lock is stale when its process id does not exist, or exists but started more than 2 seconds away from the recorded
   start time (the id was reused), or the file cannot be parsed. A stale lock is reclaimed under a short-lived guard file
   (`writer.lock.reclaim`, deleted by the OS when its handle closes, even after a crash), re-reading the lock under the
   guard, so two hosts cannot each delete the other's fresh lock. A lock that cannot be read right now (a scanner has it
   open) is never treated as stale.
5. Disposing the writer records the last-opened time, closes the database and deletes the lock, but only if the file still
   carries its own token: a host that was wrongly presumed dead does not delete its successor's lock.
6. `IndexStore.GetLiveWriter()` lets a reader check that the owner is still alive; when it is not, opening the index
   again through the factory reclaims the lock and promotes the caller to writer.

Both `features.indexing` and `features.localStore` must be on; otherwise opening fails with `LocalStoreException`
(`FeatureDisabled`) before anything is created.

## Retention

`IndexRetention.SweepAsync(dryRun, retentionDays?)` deletes index folders whose repository root no longer exists, and
those whose last use is older than `indexing.retentionDays` (30 by default; zero or less turns the age rule off). Last
use is the latest of `state.json`'s last-opened time and the modification times of `index.db`, its WAL and the lock. Never
deleted: an index whose lock belongs to a live process; a root on a drive or share that is not available right now (the
root is then "unknown", not missing); anything in `index\` whose name is not a 32-digit hex id. With `features.indexing`
off the sweep does nothing. `GetUsage()` lists every index with its root, model, chunker, disk use and last use for the
Index window. `Delete(indexId)` deletes one folder on request (`Deleted`, `NotFound`, `InUse` while a live host holds its
lock, or `Failed` when a file in it is open elsewhere).

## Tests

`tests/Pillion.Indexing.Tests`: gitignore semantics, every exclusion rule with its reason, content checks, languages,
roots (including worktree and submodule `.git` files), hashing, `files` repository, sweeps (added, changed, touched,
unchanged without opening files, deleted, racy rows, locked files), identity, the lock election in one process (24
threads, stale-lock races) and across real processes (the test assembly started as a helper), index stores with two
hosts, retention, and a 20,000-file throughput test that logs its timings.
