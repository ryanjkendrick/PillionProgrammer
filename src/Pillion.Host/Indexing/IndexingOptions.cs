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

    /// <summary>Gets an optional hook called on the pipeline thread after each file is chunked (tests check the thread's priority).</summary>
    public Action<string>? OnFilePrepared { get; init; }
}
