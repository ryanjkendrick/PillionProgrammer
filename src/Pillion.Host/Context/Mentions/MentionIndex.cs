using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Pillion.ContextEngine.Chunking;
using Pillion.ContextEngine.Symbols;
using Pillion.Host.Context.Retrieval;
using Pillion.LocalStore;
using Pillion.LocalStore.Index;

namespace Pillion.Host.Context.Mentions;

/// <summary>A file of an index's <c>files</c> table.</summary>
/// <param name="Id">The file id (<c>files.id</c>).</param>
/// <param name="RelativePath">The path relative to the root, with forward slashes, as stored.</param>
/// <param name="FullPath">The full path on disk.</param>
/// <param name="Language">The language the indexer gave it (<c>csharp</c>, <c>other</c>, ...).</param>
internal sealed record IndexedFile(long Id, string RelativePath, string FullPath, string Language);

/// <summary>
/// What the mentions read from one open index (work item 2.13): its file list, its symbol graph and its chunks. The graph is
/// the catalog's own when it keeps one; otherwise it is built from the stored <c>symbols</c> and <c>symbol_refs</c> rows (the
/// stored edges are turned back into name references and resolved again, which gives the same approximate edges) and cached
/// until the index changes.
/// </summary>
internal sealed class MentionIndexView
{
    private readonly Dictionary<string, IndexedFile> _byRelative;
    private readonly Dictionary<long, IndexedFile> _byId;

    /// <summary>Initializes a new instance of the <see cref="MentionIndexView"/> class.</summary>
    /// <param name="index">The catalog entry.</param>
    /// <param name="files">The file list.</param>
    /// <param name="graph">The symbol graph (paths relative to the root).</param>
    /// <param name="storedIds">Maps a graph definition id to its stored symbol id, when they differ.</param>
    public MentionIndexView(CatalogIndex index, IReadOnlyList<IndexedFile> files, SymbolGraph graph, IReadOnlyDictionary<long, long> storedIds)
    {
        Index = index;
        Files = files;
        Graph = graph;
        StoredIds = storedIds;
        _byRelative = files.GroupBy(f => f.RelativePath, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        _byId = files.ToDictionary(f => f.Id);
    }

    /// <summary>Gets the catalog entry (id, root and storage).</summary>
    public CatalogIndex Index { get; }

    /// <summary>Gets the index id.</summary>
    public string IndexId => Index.IndexId;

    /// <summary>Gets the repository root.</summary>
    public string RootPath => Index.RootPath;

    /// <summary>Gets the files of the index, ordered by path.</summary>
    public IReadOnlyList<IndexedFile> Files { get; }

    /// <summary>Gets the symbol graph; its paths are relative to <see cref="RootPath"/>.</summary>
    public SymbolGraph Graph { get; }

    /// <summary>Gets the stored symbol id of each graph definition whose id differs from it.</summary>
    public IReadOnlyDictionary<long, long> StoredIds { get; }

    /// <summary>Finds a file by its path relative to the root.</summary>
    /// <param name="relativePath">The relative path (either slash).</param>
    /// <returns>The file, or <see langword="null"/>.</returns>
    public IndexedFile? FindRelative(string relativePath) =>
        _byRelative.TryGetValue(relativePath.Replace('\\', '/'), out var file) ? file : null;

    /// <summary>Finds a file by its full path.</summary>
    /// <param name="fullPath">The full path.</param>
    /// <returns>The file, or <see langword="null"/> when it is not under the root or not indexed.</returns>
    public IndexedFile? FindFull(string fullPath)
    {
        var root = RootPath.Replace('/', '\\').TrimEnd('\\') + "\\";
        var path = fullPath.Replace('/', '\\');
        return path.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? FindRelative(path[root.Length..]) : null;
    }

    /// <summary>Finds a file by id.</summary>
    /// <param name="id">The file id.</param>
    /// <returns>The file, or <see langword="null"/>.</returns>
    public IndexedFile? FindId(long id) => _byId.TryGetValue(id, out var file) ? file : null;

    /// <summary>The full path of a path relative to the root.</summary>
    /// <param name="relativePath">The relative path.</param>
    /// <returns>The full path.</returns>
    public string FullPath(string relativePath) => Path.GetFullPath(Path.Combine(RootPath, relativePath.Replace('/', Path.DirectorySeparatorChar)));

    /// <summary>Reads a file's chunks in line order.</summary>
    /// <param name="fileId">The file.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The chunks.</returns>
    public async Task<IReadOnlyList<ChunkRecord>> GetChunksAsync(long fileId, CancellationToken cancellationToken) =>
        [.. (await Index.Storage.Chunks.GetChunksByFileAsync(fileId, cancellationToken).ConfigureAwait(false)).OrderBy(c => c.StartLine).ThenBy(c => c.Id)];
}

/// <summary>
/// Opens the index views the mentions need, through the retrieval catalog (work item 2.11), and keeps each one until its
/// index changes (a different file or symbol count, or a newer <c>indexed_at</c>). Thread-safe.
/// </summary>
internal sealed partial class MentionIndexReader
{
    private readonly IIndexCatalog? _catalog;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, (string Stamp, MentionIndexView View)> _cache = new(StringComparer.Ordinal);

    /// <summary>Initializes a new instance of the <see cref="MentionIndexReader"/> class.</summary>
    /// <param name="catalog">The catalog of open indexes, or <see langword="null"/> when the host has none (no index: the phase 1 paths answer).</param>
    /// <param name="logger">The logger.</param>
    public MentionIndexReader(IIndexCatalog? catalog, ILogger? logger = null)
    {
        _catalog = catalog;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>Gets a value indicating whether there is a catalog at all.</summary>
    public bool HasCatalog => _catalog is not null;

    /// <summary>Gets the views of the indexes that cover the roots.</summary>
    /// <param name="roots">The workspace roots; an index whose root is one of them, contains one, or lies inside one is used. Empty means every index the catalog serves: those of the open workspace's roots.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The views; empty when nothing is indexed (or the catalog is off).</returns>
    public async Task<IReadOnlyList<MentionIndexView>> GetAsync(IReadOnlyList<string> roots, CancellationToken cancellationToken)
    {
        if (_catalog is null)
        {
            return [];
        }

        IReadOnlyList<CatalogIndex> indexes;
        try
        {
            // The workspace's indexes (the catalog serves no others), then keep those related to the roots: a solution's folder can sit inside the
            // repository root that was indexed, or hold several indexed repositories.
            indexes = await _catalog.GetIndexesAsync([], cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or LocalStoreException or SqliteException)
        {
            LogCatalogFailed(_logger, ex.GetType().Name);
            return [];
        }

        var wanted = roots.Where(r => !string.IsNullOrWhiteSpace(r)).ToList();
        var related = indexes.Where(i => wanted.Count == 0 || wanted.Any(r => IsRelated(i.RootPath, r))).ToList();
        var views = new List<MentionIndexView>(related.Count);
        foreach (var index in related)
        {
            try
            {
                views.Add(await GetViewAsync(index, cancellationToken).ConfigureAwait(false));
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or LocalStoreException or SqliteException or ObjectDisposedException)
            {
                LogViewFailed(_logger, index.IndexId, ex.GetType().Name);
            }
        }

        return views;
    }

    /// <summary>Determines whether an index root and a workspace root overlap (one is the other or contains it).</summary>
    /// <param name="indexRoot">The index root.</param>
    /// <param name="workspaceRoot">The workspace root.</param>
    /// <returns><see langword="true"/> when they overlap.</returns>
    internal static bool IsRelated(string indexRoot, string workspaceRoot)
    {
        var a = indexRoot.Replace('/', '\\').TrimEnd('\\') + "\\";
        var b = workspaceRoot.Replace('/', '\\').TrimEnd('\\') + "\\";
        return a.StartsWith(b, StringComparison.OrdinalIgnoreCase) || b.StartsWith(a, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<MentionIndexView> GetViewAsync(CatalogIndex index, CancellationToken cancellationToken)
    {
        var stamp = await ReadStampAsync(index.Storage, cancellationToken).ConfigureAwait(false) + (index.Graph is null ? string.Empty : ":g" + index.Graph.GetHashCode());
        lock (_gate)
        {
            if (_cache.TryGetValue(index.IndexId, out var cached) && cached.Stamp == stamp && ReferenceEquals(cached.View.Index.Storage, index.Storage))
            {
                return cached.View;
            }
        }

        // Two requests may build the same view at once after a change; the second simply replaces the first.
        var view = await BuildAsync(index, cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            _cache[index.IndexId] = (stamp, view);
        }

        return view;
    }

    private static Task<string> ReadStampAsync(IndexStorage storage, CancellationToken cancellationToken) =>
        storage.Database.ReadAsync(
            (connection, token) =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT (SELECT count(*) FROM files), (SELECT coalesce(max(indexed_at), 0) FROM files), (SELECT count(*) FROM symbols), (SELECT count(*) FROM symbol_refs)";
                using var reader = command.ExecuteReader();
                reader.Read();
                return Task.FromResult(string.Join(':', reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3)));
            },
            cancellationToken);

    private static async Task<MentionIndexView> BuildAsync(CatalogIndex index, CancellationToken cancellationToken)
    {
        var files = await index.Storage.Database.ReadAsync(
            (connection, token) =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT id, path, language FROM files ORDER BY path";
                var list = new List<IndexedFile>();
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    token.ThrowIfCancellationRequested();
                    var relative = reader.GetString(1).Replace('\\', '/');
                    list.Add(new IndexedFile(reader.GetInt64(0), relative, FullPathOf(index.RootPath, relative), reader.IsDBNull(2) ? string.Empty : reader.GetString(2)));
                }

                return Task.FromResult(list);
            },
            cancellationToken).ConfigureAwait(false);

        if (index.Graph is { } graph)
        {
            return new MentionIndexView(index, files, graph, new Dictionary<long, long>());
        }

        var (built, storedIds) = await BuildGraphAsync(index.Storage, files, cancellationToken).ConfigureAwait(false);
        return new MentionIndexView(index, files, built, storedIds);
    }

    private static string FullPathOf(string root, string relative)
    {
        try
        {
            return Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Path.Combine(root, relative);
        }
    }

    /// <summary>Builds a graph from the stored symbols: definitions as stored, and the stored edges turned back into name references.</summary>
    private static async Task<(SymbolGraph Graph, Dictionary<long, long> StoredIds)> BuildGraphAsync(IndexStorage storage, IReadOnlyList<IndexedFile> files, CancellationToken cancellationToken)
    {
        var pathById = files.ToDictionary(f => f.Id, f => f.RelativePath);
        var (symbols, references) = await storage.Database.ReadAsync(
            (connection, token) =>
            {
                var symbolList = new List<SymbolRecord>();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT id, file_id, name, kind, container, start_line, end_line, signature FROM symbols ORDER BY file_id, start_line, id";
                    using var reader = command.ExecuteReader();
                    while (reader.Read())
                    {
                        token.ThrowIfCancellationRequested();
                        symbolList.Add(new SymbolRecord(
                            reader.GetInt64(0),
                            reader.GetInt64(1),
                            reader.GetString(2),
                            reader.GetString(3),
                            reader.IsDBNull(4) ? null : reader.GetString(4),
                            reader.GetInt32(5),
                            reader.GetInt32(6),
                            reader.IsDBNull(7) ? null : reader.GetString(7)));
                    }
                }

                var referenceList = new List<LocalStore.Index.SymbolReference>();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT from_symbol_id, to_symbol_id, file_id, line, kind FROM symbol_refs";
                    using var reader = command.ExecuteReader();
                    while (reader.Read())
                    {
                        token.ThrowIfCancellationRequested();
                        referenceList.Add(new LocalStore.Index.SymbolReference(
                            reader.IsDBNull(0) ? null : reader.GetInt64(0),
                            reader.GetInt64(1),
                            reader.GetInt64(2),
                            reader.GetInt32(3),
                            reader.GetString(4)));
                    }
                }

                return Task.FromResult((symbolList, referenceList));
            },
            cancellationToken).ConfigureAwait(false);

        var definitions = new List<SymbolDefinition>(symbols.Count);
        var storedById = new Dictionary<long, SymbolRecord>();
        var storedIds = new Dictionary<long, long>();
        var ordinals = new Dictionary<(string, string, string, SymbolKind), int>();
        foreach (var symbol in symbols)
        {
            if (!pathById.TryGetValue(symbol.FileId, out var path) || !SymbolKinds.TryParse(symbol.Kind, out SymbolKind kind))
            {
                continue;
            }

            var container = symbol.Container ?? string.Empty;
            var key = (path, container, symbol.Name, kind);
            var ordinal = ordinals.TryGetValue(key, out var seen) ? seen + 1 : 0;
            ordinals[key] = ordinal;
            var definition = new SymbolDefinition(path, symbol.Name, kind, container, symbol.StartLine, symbol.EndLine, symbol.Signature ?? string.Empty, LanguageOf(path), ordinal: ordinal);
            definitions.Add(definition);
            storedById[symbol.Id] = symbol;
            if (definition.Id != symbol.Id)
            {
                storedIds[definition.Id] = symbol.Id;
            }
        }

        var names = new List<ContextEngine.Symbols.SymbolReference>(references.Count);
        foreach (var reference in references)
        {
            if (!storedById.TryGetValue(reference.ToSymbolId, out var target) || !pathById.TryGetValue(reference.FileId, out var path)
                || !SymbolKinds.TryParse(reference.Kind, out SymbolReferenceKind kind))
            {
                continue;
            }

            var from = reference.FromSymbolId is { } fromId && storedById.TryGetValue(fromId, out var source) ? source : null;
            names.Add(new ContextEngine.Symbols.SymbolReference
            {
                FilePath = path,
                Line = reference.Line,
                Name = target.Name,
                Kind = kind,
                Qualifier = LastSegment(target.Container),
                FromContainer = from is null ? string.Empty : (string.IsNullOrEmpty(from.Container) ? from.Name : from.Container + Breadcrumbs.Separator + from.Name),
                Language = LanguageOf(path),
            });
        }

        return (SymbolGraphBuilder.Build(definitions, names), storedIds);
    }

    private static string? LastSegment(string? container)
    {
        if (string.IsNullOrEmpty(container))
        {
            return null;
        }

        var last = container.Split(Breadcrumbs.Separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? container;
        var dot = last.LastIndexOf('.');
        last = dot >= 0 ? last[(dot + 1)..] : last;
        var angle = last.IndexOf('<', StringComparison.Ordinal);
        return angle > 0 ? last[..angle] : last;
    }

    private static string LanguageOf(string path) => LanguageCatalog.Default.Find(path)?.Id ?? LanguageIds.Text;

    [LoggerMessage(EventId = 460, Level = LogLevel.Warning, Message = "Mentions could not list the open indexes ({ExceptionType}); the phase 1 paths answer instead.")]
    private static partial void LogCatalogFailed(ILogger logger, string exceptionType);

    [LoggerMessage(EventId = 461, Level = LogLevel.Warning, Message = "Mentions could not read index {IndexId} ({ExceptionType}); it is skipped.")]
    private static partial void LogViewFailed(ILogger logger, string indexId, string exceptionType);
}
