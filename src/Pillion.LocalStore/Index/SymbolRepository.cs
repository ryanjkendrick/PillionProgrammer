using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Pillion.LocalStore.Index;

/// <summary>
/// The <c>symbols</c> and <c>symbol_refs</c> tables (design section 10): syntax-derived definitions and name-based
/// reference edges, all approximate (index schema version 3, <see cref="IndexSymbolsSchema"/>). This is storage only;
/// extraction, resolution and the repo map are work item 2.5 (Pillion.ContextEngine).
/// </summary>
public interface ISymbolRepository
{
    /// <summary>
    /// Replaces the symbols of a file. Symbols with a stable <see cref="NewSymbol.Id"/> are upserted: a symbol whose id
    /// is already stored keeps its row, and references from other files to it survive. Every other old symbol of the
    /// file goes, and with it (by cascade) every reference from or to it, including references recorded in other files,
    /// which the extractor re-resolves when it next visits those files. Symbols without an id get a new one.
    /// </summary>
    /// <param name="fileId">The file; it must exist.</param>
    /// <param name="symbols">The new symbols; stable ids must be unique within the list.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The new symbol ids, in order.</returns>
    Task<IReadOnlyList<long>> ReplaceFileSymbolsAsync(long fileId, IReadOnlyList<NewSymbol> symbols, CancellationToken cancellationToken = default);

    /// <summary>Replaces the references found in a file.</summary>
    /// <param name="fileId">The file the references are in.</param>
    /// <param name="references">The references; their <see cref="SymbolReference.FileId"/> is ignored (it is <paramref name="fileId"/>).</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task that completes when they are stored.</returns>
    Task ReplaceFileReferencesAsync(long fileId, IReadOnlyList<SymbolReference> references, CancellationToken cancellationToken = default);

    /// <summary>Gets a symbol.</summary>
    /// <param name="symbolId">The symbol id.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The symbol, or <see langword="null"/>.</returns>
    Task<SymbolRecord?> GetSymbolAsync(long symbolId, CancellationToken cancellationToken = default);

    /// <summary>Gets the symbols of a file by start line.</summary>
    /// <param name="fileId">The file.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The symbols.</returns>
    Task<IReadOnlyList<SymbolRecord>> GetSymbolsByFileAsync(long fileId, CancellationToken cancellationToken = default);

    /// <summary>Finds symbols by exact name, ignoring case.</summary>
    /// <param name="name">The name.</param>
    /// <param name="limit">The most to return.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The symbols.</returns>
    Task<IReadOnlyList<SymbolRecord>> FindSymbolsByNameAsync(string name, int limit = 50, CancellationToken cancellationToken = default);

    /// <summary>Gets the references found in a file.</summary>
    /// <param name="fileId">The file.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The references by line.</returns>
    Task<IReadOnlyList<SymbolReference>> GetReferencesInFileAsync(long fileId, CancellationToken cancellationToken = default);

    /// <summary>Gets the references to a symbol (its callers and users).</summary>
    /// <param name="symbolId">The referenced symbol.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The references.</returns>
    Task<IReadOnlyList<SymbolReference>> GetReferencesToAsync(long symbolId, CancellationToken cancellationToken = default);

    /// <summary>Gets every reference edge (for PageRank over the reference graph).</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The references.</returns>
    Task<IReadOnlyList<SymbolReference>> GetAllReferencesAsync(CancellationToken cancellationToken = default);

    /// <summary>Counts the symbols.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The count.</returns>
    Task<long> CountSymbolsAsync(CancellationToken cancellationToken = default);

    /// <summary>Gets the symbol rules version the stored symbols were built with (<see cref="IndexSymbolsSchema.MetaKeys.SymbolsVersion"/>).</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The version, or <see langword="null"/> when the symbols were never built (or the schema was migrated since).</returns>
    Task<string?> GetSymbolsVersionAsync(CancellationToken cancellationToken = default);

    /// <summary>Records the symbol rules version after the symbols have been (re)built, separately from the chunker version.</summary>
    /// <param name="version">The version (<c>ContextEngineVersions.SymbolsVersion</c>).</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task that completes when it is stored.</returns>
    Task SetSymbolsVersionAsync(string version, CancellationToken cancellationToken = default);
}

/// <summary>The <see cref="ISymbolRepository"/> over an index database at schema version 3 or later.</summary>
public sealed class SymbolRepository : ISymbolRepository
{
    private const string SymbolColumns = "id, file_id, name, kind, container, start_line, end_line, signature";
    private const string ReferenceColumns = "from_symbol_id, to_symbol_id, file_id, line, kind, ambiguous, candidates";
    private readonly EncryptedDatabase _database;

    /// <summary>Initializes a new instance of the <see cref="SymbolRepository"/> class.</summary>
    /// <param name="database">The open index database; the caller owns it.</param>
    public SymbolRepository(EncryptedDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        _database = database;
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<long>> ReplaceFileSymbolsAsync(long fileId, IReadOnlyList<NewSymbol> symbols, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(symbols);
        return _database.WriteAsync<IReadOnlyList<long>>(
            (connection, transaction, token) => Task.FromResult<IReadOnlyList<long>>(ReplaceSymbols(connection, transaction, fileId, symbols, token)),
            cancellationToken);
    }

    /// <summary>Replaces a file's symbols inside the caller's write transaction; see <see cref="ReplaceFileSymbolsAsync"/>.</summary>
    internal static long[] ReplaceSymbols(SqliteConnection connection, SqliteTransaction transaction, long fileId, IReadOnlyList<NewSymbol> symbols, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(symbols);

        // Keep the rows whose stable ids come back (their references from other files stay valid); drop the rest.
        var keep = new HashSet<long>();
        foreach (var symbol in symbols)
        {
            ArgumentNullException.ThrowIfNull(symbol);
            ArgumentException.ThrowIfNullOrEmpty(symbol.Name);
            if (symbol.Id is { } id)
            {
                ArgumentOutOfRangeException.ThrowIfNegativeOrZero(id, nameof(symbols));
                if (!keep.Add(id))
                {
                    throw new ArgumentException($"The symbol id {id} appears twice.", nameof(symbols));
                }
            }
        }

        DeleteOtherSymbols(connection, transaction, fileId, keep);
        using var insert = Sql.Command(
            connection,
            transaction,
            """
            INSERT INTO symbols (id, file_id, name, kind, container, start_line, end_line, signature)
            VALUES ($id, $file, $name, $kind, $container, $start, $end, $signature)
            ON CONFLICT (id) DO UPDATE SET
                file_id = excluded.file_id, name = excluded.name, kind = excluded.kind, container = excluded.container,
                start_line = excluded.start_line, end_line = excluded.end_line, signature = excluded.signature
            RETURNING id
            """,
            ("$id", null),
            ("$file", fileId),
            ("$name", string.Empty),
            ("$kind", string.Empty),
            ("$container", null),
            ("$start", 0),
            ("$end", 0),
            ("$signature", null));
        var ids = new long[symbols.Count];
        for (var i = 0; i < symbols.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            var symbol = symbols[i];
            insert.Parameters[0].Value = (object?)symbol.Id ?? DBNull.Value;
            insert.Parameters[2].Value = symbol.Name;
            insert.Parameters[3].Value = symbol.Kind ?? string.Empty;
            insert.Parameters[4].Value = (object?)symbol.Container ?? DBNull.Value;
            insert.Parameters[5].Value = symbol.StartLine;
            insert.Parameters[6].Value = symbol.EndLine;
            insert.Parameters[7].Value = (object?)symbol.Signature ?? DBNull.Value;
            ids[i] = Convert.ToInt64(insert.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        return ids;
    }

    /// <inheritdoc/>
    public Task ReplaceFileReferencesAsync(long fileId, IReadOnlyList<SymbolReference> references, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(references);
        return _database.WriteAsync(
            (connection, transaction, token) =>
            {
                ReplaceReferences(connection, transaction, fileId, references, token);
                return Task.CompletedTask;
            },
            cancellationToken);
    }

    /// <summary>Replaces the references found in a file inside the caller's write transaction; see <see cref="ReplaceFileReferencesAsync"/>.</summary>
    internal static void ReplaceReferences(SqliteConnection connection, SqliteTransaction transaction, long fileId, IReadOnlyList<SymbolReference> references, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(references);
        Sql.Execute(connection, transaction, "DELETE FROM symbol_refs WHERE file_id = $file", ("$file", fileId));
        using var insert = Sql.Command(
            connection,
            transaction,
            """
            INSERT INTO symbol_refs (from_symbol_id, to_symbol_id, file_id, line, kind, ambiguous, candidates)
            VALUES ($from, $to, $file, $line, $kind, $ambiguous, $candidates)
            """,
            ("$from", null),
            ("$to", 0L),
            ("$file", fileId),
            ("$line", 0),
            ("$kind", string.Empty),
            ("$ambiguous", 0),
            ("$candidates", 1));
        foreach (var reference in references)
        {
            token.ThrowIfCancellationRequested();
            ArgumentNullException.ThrowIfNull(reference);
            insert.Parameters[0].Value = (object?)reference.FromSymbolId ?? DBNull.Value;
            insert.Parameters[1].Value = reference.ToSymbolId;
            insert.Parameters[3].Value = reference.Line;
            insert.Parameters[4].Value = reference.Kind ?? string.Empty;
            insert.Parameters[5].Value = reference.Ambiguous ? 1 : 0;
            insert.Parameters[6].Value = Math.Max(1, reference.Candidates);
            insert.ExecuteNonQuery();
        }
    }

    /// <inheritdoc/>
    public async Task<SymbolRecord?> GetSymbolAsync(long symbolId, CancellationToken cancellationToken = default)
    {
        var rows = await QueryAsync($"SELECT {SymbolColumns} FROM symbols WHERE id = $id", ReadSymbol, cancellationToken, ("$id", symbolId)).ConfigureAwait(false);
        return rows.Count == 0 ? null : rows[0];
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<SymbolRecord>> GetSymbolsByFileAsync(long fileId, CancellationToken cancellationToken = default) =>
        QueryAsync($"SELECT {SymbolColumns} FROM symbols WHERE file_id = $file ORDER BY start_line, id", ReadSymbol, cancellationToken, ("$file", fileId));

    /// <inheritdoc/>
    public Task<IReadOnlyList<SymbolRecord>> FindSymbolsByNameAsync(string name, int limit = 50, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentOutOfRangeException.ThrowIfNegative(limit);
        return QueryAsync(
            $"SELECT {SymbolColumns} FROM symbols WHERE name = $name COLLATE NOCASE ORDER BY id LIMIT $limit",
            ReadSymbol,
            cancellationToken,
            ("$name", name),
            ("$limit", limit));
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<SymbolReference>> GetReferencesInFileAsync(long fileId, CancellationToken cancellationToken = default) =>
        QueryAsync($"SELECT {ReferenceColumns} FROM symbol_refs WHERE file_id = $file ORDER BY line, rowid", ReadReference, cancellationToken, ("$file", fileId));

    /// <inheritdoc/>
    public Task<IReadOnlyList<SymbolReference>> GetReferencesToAsync(long symbolId, CancellationToken cancellationToken = default) =>
        QueryAsync($"SELECT {ReferenceColumns} FROM symbol_refs WHERE to_symbol_id = $id ORDER BY file_id, line", ReadReference, cancellationToken, ("$id", symbolId));

    /// <inheritdoc/>
    public Task<IReadOnlyList<SymbolReference>> GetAllReferencesAsync(CancellationToken cancellationToken = default) =>
        QueryAsync($"SELECT {ReferenceColumns} FROM symbol_refs ORDER BY rowid", ReadReference, cancellationToken);

    /// <inheritdoc/>
    public Task<long> CountSymbolsAsync(CancellationToken cancellationToken = default) =>
        _database.ReadAsync((connection, _) => Task.FromResult(Sql.ScalarLong(connection, null, "SELECT count(*) FROM symbols")), cancellationToken);

    /// <inheritdoc/>
    public Task<string?> GetSymbolsVersionAsync(CancellationToken cancellationToken = default) =>
        _database.ReadAsync((connection, _) => Task.FromResult(Sql.ReadMeta(connection, null, IndexSymbolsSchema.MetaKeys.SymbolsVersion)), cancellationToken);

    /// <inheritdoc/>
    public Task SetSymbolsVersionAsync(string version, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        return _database.WriteAsync(
            (connection, transaction, _) =>
            {
                Sql.WriteMeta(connection, transaction, IndexSymbolsSchema.MetaKeys.SymbolsVersion, version);
                return Task.CompletedTask;
            },
            cancellationToken);
    }

    /// <summary>Deletes the file's symbols whose ids are not in <paramref name="keep"/> (cascading to their references).</summary>
    private static void DeleteOtherSymbols(SqliteConnection connection, SqliteTransaction transaction, long fileId, HashSet<long> keep)
    {
        if (keep.Count == 0)
        {
            Sql.Execute(connection, transaction, "DELETE FROM symbols WHERE file_id = $file", ("$file", fileId));
            return;
        }

        var stale = new List<long>();
        using (var select = Sql.Command(connection, transaction, "SELECT id FROM symbols WHERE file_id = $file", ("$file", fileId)))
        using (var reader = select.ExecuteReader())
        {
            while (reader.Read())
            {
                var id = reader.GetInt64(0);
                if (!keep.Contains(id))
                {
                    stale.Add(id);
                }
            }
        }

        using var delete = Sql.Command(connection, transaction, "DELETE FROM symbols WHERE id = $id", ("$id", 0L));
        foreach (var id in stale)
        {
            delete.Parameters[0].Value = id;
            delete.ExecuteNonQuery();
        }
    }

    private static SymbolRecord ReadSymbol(SqliteDataReader reader) => new(
        reader.GetInt64(0),
        reader.GetInt64(1),
        reader.GetString(2),
        reader.GetString(3),
        reader.IsDBNull(4) ? null : reader.GetString(4),
        reader.GetInt32(5),
        reader.GetInt32(6),
        reader.IsDBNull(7) ? null : reader.GetString(7));

    private static SymbolReference ReadReference(SqliteDataReader reader) => new(
        reader.IsDBNull(0) ? null : reader.GetInt64(0),
        reader.GetInt64(1),
        reader.GetInt64(2),
        reader.GetInt32(3),
        reader.GetString(4),
        reader.GetInt64(5) != 0,
        reader.GetInt32(6));

    private Task<IReadOnlyList<T>> QueryAsync<T>(string sql, Func<SqliteDataReader, T> read, CancellationToken cancellationToken, params (string Name, object? Value)[] parameters) =>
        _database.ReadAsync<IReadOnlyList<T>>(
            (connection, token) =>
            {
                using var command = Sql.Command(connection, null, sql, parameters);
                using var reader = command.ExecuteReader();
                var rows = new List<T>();
                while (reader.Read())
                {
                    token.ThrowIfCancellationRequested();
                    rows.Add(read(reader));
                }

                return Task.FromResult<IReadOnlyList<T>>(rows);
            },
            cancellationToken);
}
