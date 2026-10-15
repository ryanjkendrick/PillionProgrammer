namespace Pillion.Host.Indexing;

/// <summary>Tunables of the indexing pipeline (work item 2.9). The defaults follow design sections 7 and 13; tests shorten the intervals.</summary>
internal sealed class IndexingOptions
{
    /// <summary>Gets the chunks per embedding request (design section 7: batches of 32). The memory governor halves it under pressure.</summary>
    public int EmbeddingBatchSize { get; init; } = 32;

    /// <summary>
    /// Gets how many batches of chunks are gathered and sorted by length before they are embedded, so each request holds
    /// chunks of similar length (the model pads a batch to its longest text). 1 embeds chunks in file order.
    /// </summary>
    public int EmbeddingSortWindow { get; init; } = 8;

    /// <summary>Gets the smallest batch the memory governor halves down to before it pauses indexing instead.</summary>
    public int MinimumEmbeddingBatchSize { get; init; } = 4;

    /// <summary>Gets how many threads the pipeline's CPU work runs on: at most half the logical processors (design section 13).</summary>
    public int MaxDegreeOfParallelism { get; init; } = Math.Max(1, Environment.ProcessorCount / 2);

    /// <summary>
    /// Gets the largest chunk in the embedding model's tokens. The cap is the model's own input size, but never more than
    /// this: models with long windows (Jina's 8,192, Ollama's) would otherwise get chunks too large for retrieval.
    /// </summary>
    public int MaxChunkTokens { get; init; } = 512;

    /// <summary>Gets how many prepared (chunked) files may wait for the embedder; bounds the memory chunk text takes.</summary>
    public int PreparedFileQueue { get; init; } = 64;

    /// <summary>
    /// Gets the shortest time between two refreshes during a run (the symbol graph rebuilt with its changed edges written,
    /// and the in-memory vector copy reloaded). The first stored batch refreshes at once so partial results are searchable;
    /// later refreshes wait at least this long and at least <see cref="RefreshCostFactor"/> times what the last one took.
    /// </summary>
    public TimeSpan MinimumRefreshInterval { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Gets how many times the duration of the last refresh must pass before the next one (keeps refreshes under about 10% of a run).</summary>
    public int RefreshCostFactor { get; init; } = 10;

    /// <summary>Gets how many times a failed embedding batch is retried before the run stops as failed.</summary>
    public int EmbeddingRetries { get; init; } = 3;

    /// <summary>Gets the first pause before an embedding retry; it doubles each time.</summary>
    public TimeSpan EmbeddingRetryDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Gets how often Windows' battery saver state is read.</summary>
    public TimeSpan PowerPollInterval { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>Gets how often the working set is sampled while indexing is paused for memory.</summary>
    public TimeSpan MemoryPollInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Gets how often a host that only reads an index checks whether its owner is still alive (and takes over when it is not).</summary>
    public TimeSpan ReaderPollInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Gets the fraction of the memory cap below which indexing resumes after a memory pause.</summary>
    public double MemoryResumeFraction { get; init; } = 0.9;

    /// <summary>Gets the fraction of the memory cap below which a halved batch is doubled again.</summary>
    public double MemoryRecoverFraction { get; init; } = 0.75;

    /// <summary>Gets a value indicating whether the roots this host owns are watched for changes (work item 2.10). Tests that drive runs by hand turn it off.</summary>
    public bool WatchFileSystem { get; init; } = true;

    /// <summary>Gets how long a path must be quiet before its change is indexed (design section 7: 500 ms debounce).</summary>
    public TimeSpan WatcherDebounce { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Gets how long a burst of changes may last before the paths that are ready are indexed anyway (1 s).</summary>
    public TimeSpan WatcherCoalescingWindow { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Gets the file-system watcher's buffer: 64 KiB, the most Windows allows for a watcher (the default is 8 KiB).</summary>
    public int WatcherBufferBytes { get; init; } = 64 * 1024;

    /// <summary>Gets how often a root whose watcher failed is swept (and the watcher started again), while it stays broken.</summary>
    public TimeSpan WatcherFallbackInterval { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Gets how long the git directory must be quiet (and <c>index.lock</c> gone) before a moved <c>HEAD</c> is acted on.</summary>
    public TimeSpan GitSettleDelay { get; init; } = TimeSpan.FromMilliseconds(300);

    /// <summary>Gets the longest the watcher holds file events back while git holds <c>index.lock</c> (a stale lock left by a crash).</summary>
    public TimeSpan GitBusyMaxWait { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Gets the save-to-searchable budget (Gate 2: 500 ms). A saved file's chunks are embedded in batches while the next
    /// batch still fits the budget, less <see cref="SaveStoreReserve"/>; the file is then stored and searchable, and the
    /// rest of its chunks get their vectors right after.
    /// </summary>
    public TimeSpan SaveSearchableBudget { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Gets the part of <see cref="SaveSearchableBudget"/> kept for storing the file and updating the in-memory vectors.</summary>
    public TimeSpan SaveStoreReserve { get; init; } = TimeSpan.FromMilliseconds(80);

    /// <summary>Gets the chunks per embedding request on the save path: small, so the budget is checked often.</summary>
    public int SaveEmbeddingBatchSize { get; init; } = 8;

    /// <summary>
    /// Gets how often the retention sweep runs while the host runs (issue #99): first once the workspace's indexes are open,
    /// then this often (a day). Zero or less turns the scheduled sweep off.
    /// </summary>
    public TimeSpan RetentionSweepInterval { get; init; } = TimeSpan.FromDays(1);

    /// <summary>Gets an optional hook called on the pipeline thread after each file is chunked (tests check the thread's priority).</summary>
    public Action<string>? OnFilePrepared { get; init; }
}
