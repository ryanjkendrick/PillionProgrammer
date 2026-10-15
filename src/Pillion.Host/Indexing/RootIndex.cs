using Pillion.ContextEngine.Symbols;
using Pillion.Contracts;
using Pillion.Indexing.Ownership;
using Pillion.Indexing.Roots;
using Pillion.Indexing.Sweep;
using Pillion.LocalStore.Index;
using IndexRole = Pillion.Indexing.Ownership.IndexRole;

namespace Pillion.Host.Indexing;

/// <summary>
/// One repository root this host has open: the index (as its writer or a reader), the storage over it, and what the
/// pipeline keeps between runs in this session (the symbol graph builder with every file's latest extraction, the
/// fingerprints of the reference edges last written, the excluded files of the last sweep, and the counts the status shows).
/// </summary>
internal sealed class RootIndex : IAsyncDisposable
{
    private SymbolGraph? _graph;

    /// <summary>Initializes a new instance of the <see cref="RootIndex"/> class.</summary>
    /// <param name="root">The repository root.</param>
    /// <param name="store">The open index.</param>
    /// <param name="model">The embedding model the index is built with.</param>
    /// <param name="projects">The solution's projects (for partitions).</param>
    public RootIndex(RepositoryRoot root, IndexStore store, IndexEmbeddingModel model, IReadOnlyList<IndexedProject> projects)
    {
        Root = root;
        Store = store;
        Model = model;
        Storage = IndexStorage.Create(store.Database, ownsDatabase: false);
        Partitions = new PartitionResolver(store.Identity.RootPath, projects);
        Symbols = NewBuilder();
    }

    /// <summary>Gets the repository root as discovered.</summary>
    public RepositoryRoot Root { get; }

    /// <summary>Gets the root folder (full path, no trailing separator).</summary>
    public string Path => Store.Identity.RootPath;

    /// <summary>Gets the open index.</summary>
    public IndexStore Store { get; }

    /// <summary>Gets the chunk, vector and symbol storage over the index's database.</summary>
    public IndexStorage Storage { get; }

    /// <summary>Gets the embedding model of the index.</summary>
    public IndexEmbeddingModel Model { get; }

    /// <summary>Gets the partition of each file (the project, else the top-level folder).</summary>
    public PartitionResolver Partitions { get; }

    /// <summary>Gets a value indicating whether this host owns the index.</summary>
    public bool IsWriter => Store.Role == IndexRole.Writer;

    /// <summary>Gets the role as the contract names it.</summary>
    public Contracts.IndexRole ContractRole => IsWriter ? Contracts.IndexRole.Writer : Contracts.IndexRole.Reader;

    /// <summary>Gets or sets the builder holding every stored file's latest symbol extraction (pipeline thread only).</summary>
    public SymbolGraphBuilder Symbols { get; set; }

    /// <summary>
    /// Gets or sets the symbol graph built at the pipeline's last refresh (immutable and thread-safe), for retrieval and the
    /// workspace queries' fallback; <see langword="null"/> before the first refresh of a session and on a reader.
    /// </summary>
    public SymbolGraph? Graph
    {
        get => Volatile.Read(ref _graph);
        set => Volatile.Write(ref _graph, value);
    }

    /// <summary>Gets or sets a value indicating whether <see cref="Symbols"/> holds every indexed file (it is filled on the first run of a session).</summary>
    public bool SymbolsLoaded { get; set; }

    /// <summary>Gets the <c>files.id</c> of each stored path (pipeline thread only).</summary>
    public Dictionary<string, long> FileIds { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Gets the partition of each stored path (pipeline thread only), for symbol resolution.</summary>
    public Dictionary<string, string> FilePartitions { get; } = new(StringComparer.Ordinal);

    /// <summary>Gets the fingerprint of the reference edges last written for each path (pipeline thread only).</summary>
    public Dictionary<string, ulong> EdgeFingerprints { get; } = new(StringComparer.Ordinal);

    /// <summary>Gets or sets a value indicating whether <see cref="EdgeFingerprints"/> describes what the database holds (false until the first refresh of a session).</summary>
    public bool EdgeFingerprintsKnown { get; set; }

    /// <summary>Gets or sets a value indicating whether the root needs a run (opened, rebuilt, or taken over from a dead writer).</summary>
    public bool NeedsRun { get; set; } = true;

    /// <summary>Gets or sets the excluded files and folders of the last sweep, secret-bearing first, then by path.</summary>
    public IReadOnlyList<ExcludedFile> Excluded { get; set; } = [];

    /// <summary>Gets or sets how many of <see cref="Excluded"/> are secret-bearing.</summary>
    public int SecretFileCount { get; set; }

    /// <summary>Gets or sets the files that pass the exclusions, from the last sweep (-1 before the first).</summary>
    public int TotalFileCount { get; set; } = -1;

    /// <summary>Gets or sets the files stored in the index.</summary>
    public long IndexedFileCount { get; set; }

    /// <summary>Gets or sets the chunks stored in the index.</summary>
    public long ChunkCount { get; set; }

    /// <summary>Gets or sets when the index last changed.</summary>
    public DateTimeOffset? LastUpdatedUtc { get; set; }

    /// <summary>Starts the session's symbol state again (after a rebuild wiped the index).</summary>
    public void ResetSession()
    {
        Symbols = NewBuilder();
        SymbolsLoaded = false;
        FileIds.Clear();
        FilePartitions.Clear();
        EdgeFingerprints.Clear();
        EdgeFingerprintsKnown = false;
        Graph = null;
    }

    /// <summary>Counts the stored files and chunks again from the database.</summary>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>A task that completes when the counts are refreshed.</returns>
    public async Task RefreshCountsAsync(CancellationToken cancellationToken)
    {
        IndexedFileCount = await Store.Files.CountAsync(cancellationToken).ConfigureAwait(false);
        ChunkCount = await Storage.Chunks.CountChunksAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        await Storage.DisposeAsync().ConfigureAwait(false);
        await Store.DisposeAsync().ConfigureAwait(false);
    }

    private SymbolGraphBuilder NewBuilder() => new(new SymbolGraphOptions
    {
        PartitionOf = path => FilePartitions.TryGetValue(path, out var partition) ? partition : SymbolGraphOptions.TopLevelFolder(path),
    });
}
