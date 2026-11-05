using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.IO.Hashing;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Pillion.Common.Redaction;
using Pillion.ContextEngine;
using Pillion.ContextEngine.Chunking;
using Pillion.ContextEngine.Symbols;
using Pillion.Contracts;
using Pillion.Indexing.Discovery;
using Pillion.Indexing.Storage;
using Pillion.Indexing.Sweep;
using Pillion.LocalStore.Index;
using Pillion.ModelGateway;
using StoredSymbolReference = Pillion.LocalStore.Index.SymbolReference;

namespace Pillion.Host.Indexing;

/// <summary>
/// The progress of one run, shared between the run (which writes it) and the status (which reads it). The phase only moves
/// forward (Discovering, Hashing, Chunking, Embedding, Storing); pauses are reported on top of it.
/// </summary>
internal sealed class PassProgress
{
    private long _bytesTotal;
    private long _bytesDone;
    private long _embeddingStartedTicks;
    private int _phase;

    /// <summary>Gets the furthest step the run has reached.</summary>
    public IndexPhase Phase => (IndexPhase)Volatile.Read(ref _phase);

    /// <summary>Gets or sets the files this run has finished.</summary>
    public int FilesDone { get; set; }

    /// <summary>Gets or sets the files this run has to process.</summary>
    public int FilesTotal { get; set; }

    /// <summary>Gets or sets the chunks this run has stored.</summary>
    public long ChunksDone { get; set; }

    /// <summary>Moves the phase forward (never back).</summary>
    /// <param name="phase">The step reached.</param>
    /// <returns><see langword="true"/> when the phase changed.</returns>
    public bool Advance(IndexPhase phase)
    {
        while (true)
        {
            var current = Volatile.Read(ref _phase);
            if ((int)phase <= current)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref _phase, (int)phase, current) == current)
            {
                return true;
            }
        }
    }

    /// <summary>Sets the bytes of the files to process, for the estimate.</summary>
    /// <param name="bytes">The bytes.</param>
    public void SetBytesTotal(long bytes) => Volatile.Write(ref _bytesTotal, bytes);

    /// <summary>Adds the bytes of a finished file.</summary>
    /// <param name="bytes">The bytes.</param>
    public void AddBytesDone(long bytes) => Interlocked.Add(ref _bytesDone, bytes);

    /// <summary>Marks the start of embedding (the estimate measures from there).</summary>
    /// <param name="timestamp">A <see cref="Stopwatch"/> timestamp.</param>
    public void MarkEmbeddingStarted(long timestamp) => Interlocked.CompareExchange(ref _embeddingStartedTicks, timestamp, 0);

    /// <summary>Estimates the seconds left from the bytes done since embedding started.</summary>
    /// <returns>The estimate, or -1 when there is none yet.</returns>
    public int EstimateRemainingSeconds()
    {
        var started = Volatile.Read(ref _embeddingStartedTicks);
        var done = Volatile.Read(ref _bytesDone);
        var total = Volatile.Read(ref _bytesTotal);
        if (started == 0 || done <= 0 || total <= 0)
        {
            return -1;
        }

        var elapsed = Stopwatch.GetElapsedTime(started).TotalSeconds;
        var remaining = Math.Max(0, total - done);
        return elapsed <= 0 ? -1 : (int)Math.Ceiling(remaining / (done / elapsed));
    }
}

/// <summary>What one run did, for logs and the performance tests.</summary>
internal sealed class PassStatistics
{
    /// <summary>Gets or sets the files chunked and stored.</summary>
    public int FilesIndexed { get; set; }

    /// <summary>Gets or sets the files whose rows were deleted.</summary>
    public int FilesDeleted { get; set; }

    /// <summary>Gets or sets the files whose symbols were extracted without re-chunking.</summary>
    public int FilesSymbolsOnly { get; set; }

    /// <summary>Gets or sets the lines of the files chunked.</summary>
    public long Lines { get; set; }

    /// <summary>Gets or sets the chunks stored.</summary>
    public long Chunks { get; set; }

    /// <summary>Gets or sets how many of the stored chunks the secret scanner redacted before they were stored (work item 3.2).</summary>
    public long ChunksRedacted { get; set; }

    /// <summary>Gets or sets the sum of the stored chunks' token counts (in the embedding model's tokenizer).</summary>
    public long Tokens { get; set; }

    /// <summary>Gets or sets the embedding requests sent.</summary>
    public int EmbeddingBatches { get; set; }

    /// <summary>Gets or sets the time spent waiting for embeddings.</summary>
    public TimeSpan EmbeddingTime { get; set; }

    /// <summary>Gets or sets the time spent in the sweep (discovery and hashing).</summary>
    public TimeSpan SweepTime { get; set; }

    /// <summary>Gets or sets the time spent refreshing the symbol graph and the vector copy.</summary>
    public TimeSpan RefreshTime { get; set; }

    /// <summary>Gets or sets the number of refreshes.</summary>
    public int Refreshes { get; set; }

    /// <summary>Gets or sets the whole run's duration.</summary>
    public TimeSpan Duration { get; set; }

    /// <summary>Gets or sets the time from the start of the run to the end of the first refresh (partial results searchable), or <see langword="null"/> when nothing was stored.</summary>
    public TimeSpan? FirstSearchable { get; set; }

    /// <summary>Gets or sets, for an incremental run, the longest time from a save's arrival to its file being searchable.</summary>
    public TimeSpan? SaveToSearchable { get; set; }

    /// <summary>Gets or sets how many of the run's added or changed files were open in Visual Studio (indexed first).</summary>
    public int PriorityFiles { get; set; }

    /// <summary>Gets or sets the time from the start of the run until the open documents were stored and refreshed (searchable lexically and by vector).</summary>
    public TimeSpan? PrioritySearchable { get; set; }

    /// <summary>Gets or sets when the open documents became searchable (a <see cref="Stopwatch"/> timestamp), for measurements from outside the run.</summary>
    public long? PrioritySearchableAt { get; set; }

    /// <summary>Gets or sets the saved files stored before all their chunks had vectors (the save budget ran out).</summary>
    public int PartialFiles { get; set; }

    /// <summary>Gets or sets, for the last saved file, how many chunks it has.</summary>
    public int SavedChunks { get; set; }

    /// <summary>Gets or sets, for the last saved file, how many of its chunks had vectors when it became searchable (reused or embedded within the budget).</summary>
    public int SavedChunksWithVectors { get; set; }

    /// <summary>Gets or sets, for the last saved file, the time from the save's arrival to every chunk having its vector.</summary>
    public TimeSpan? SavedAllVectorsAfter { get; set; }

    private long _chunksReused;

    /// <summary>Gets the chunks of changed files whose text was unchanged, so their stored vectors were kept instead of embedding them again.</summary>
    public long ChunksReused => Interlocked.Read(ref _chunksReused);

    /// <summary>Counts reused chunks (from several pipeline threads).</summary>
    /// <param name="count">The chunks.</param>
    public void AddChunksReused(int count) => Interlocked.Add(ref _chunksReused, count);
}

/// <summary>
/// One indexing run over one root this host owns (design section 7, steps 1 to 6): sweep (discover and hash), delete what is
/// gone, then for each added or changed file read, chunk and extract symbols on the pipeline threads, embed the chunks in
/// batches through the gateway, and store each file's row, chunks, vectors and symbols in one transaction. The symbol graph
/// is rebuilt and its changed edges written, and the in-memory vector copy reloaded, after the first stored batch (so partial
/// results are searchable) and then at a bounded rate; the symbols version is recorded and <c>state.json</c> updated at the end.
/// </summary>
internal sealed partial class IndexPass
{
    /// <summary>
    /// The shortest prefix of a secret that is replaced in a breadcrumb cut inside the secret (PR 121 review): shorter ones are
    /// the kind's own marker (<c>sk_live_</c>, <c>AKIA</c>) rather than key material.
    /// </summary>
    internal const int MinBreadcrumbSecretPrefix = 8;

    private static readonly FileChunker Chunker = new();
    private static readonly FileSymbolExtractor Extractor = new();

    private readonly RootIndex _root;
    private readonly IIndexEmbedder _embedder;
    private readonly IndexingOptions _options;
    private readonly PipelineScheduler _scheduler;
    private readonly PauseGate _pause;
    private readonly MemoryGovernor _memory;
    private readonly IReadOnlyList<string> _exclude;
    private readonly Action _changed;
    private readonly ILogger _logger;
    private readonly ChunkOptions _chunkOptions;
    private readonly long _started = Stopwatch.GetTimestamp();
    private long _lastRefresh;
    private TimeSpan _lastRefreshCost;
    private bool _symbolWritesFailed;
    private bool _incremental;
    private int _priorityTotal;
    private int _priorityStored;
    private TimeSpan _lastEmbedDuration;

    /// <summary>Initializes a new instance of the <see cref="IndexPass"/> class.</summary>
    /// <param name="root">The root; this host must own its index.</param>
    /// <param name="embedder">Embeds chunks.</param>
    /// <param name="options">The tunables.</param>
    /// <param name="scheduler">The below-normal pipeline threads.</param>
    /// <param name="pause">The pause reasons, checked before each file and each batch.</param>
    /// <param name="memory">The memory governor, asked before each batch.</param>
    /// <param name="exclude">The effective <c>indexing.exclude</c> patterns.</param>
    /// <param name="changed">Called when the progress or phase changed (the status is published).</param>
    /// <param name="logger">The logger.</param>
    public IndexPass(
        RootIndex root,
        IIndexEmbedder embedder,
        IndexingOptions options,
        PipelineScheduler scheduler,
        PauseGate pause,
        MemoryGovernor memory,
        IReadOnlyList<string> exclude,
        Action changed,
        ILogger logger)
    {
        _root = root;
        _embedder = embedder;
        _options = options;
        _scheduler = scheduler;
        _pause = pause;
        _memory = memory;
        _exclude = exclude;
        _changed = changed;
        _logger = logger;
        _chunkOptions = ChunkOptions.Default with
        {
            MaxTokens = Math.Max(ChunkOptions.MinimumMaxTokens, Math.Min(root.Model.MaxTokens, options.MaxChunkTokens)),
            TokenCounter = root.Model.TokenCounter,
        };
    }

    /// <summary>Gets the progress of the run.</summary>
    public PassProgress Progress { get; } = new();

    /// <summary>Gets what the run did.</summary>
    public PassStatistics Statistics { get; } = new();

    /// <summary>Gets an action run when the sweep is about to start (after any pause): the watcher's queued paths are dropped, the sweep sees them.</summary>
    public Action? BeforeSweep { get; init; }

    /// <summary>
    /// Gets the paths (relative to the root) of the documents open in Visual Studio (work item 2.10): a run indexes them
    /// before every other added or changed file, and refreshes the vectors and the graph as soon as they are stored, so after
    /// a branch switch what the developer is looking at is searchable first.
    /// </summary>
    public IReadOnlySet<string> PriorityPaths { get; init; } = new HashSet<string>();

    /// <summary>Runs the pass to the end.</summary>
    /// <param name="cancellationToken">Stops the run (files already stored stay stored).</param>
    /// <returns>A task that completes when the index is up to date with the tree.</returns>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        await _pause.WaitWhilePausedAsync(cancellationToken).ConfigureAwait(false);
        BeforeSweep?.Invoke();
        Advance(IndexPhase.Discovering);

        // 1 and 2. Discover and hash (one walk: unchanged files are neither opened nor hashed).
        var stored = await _root.Store.Files.GetAllAsync(cancellationToken).ConfigureAwait(false);
        var sweepStarted = Stopwatch.GetTimestamp();
        var sweeper = new RepositorySweeper(new ScanOptions
        {
            ExcludePatterns = _exclude,
            MaxDegreeOfParallelism = _options.MaxDegreeOfParallelism,
            TaskScheduler = _scheduler,
        });
        var sweep = await sweeper.SweepAsync(_root.Path, stored, _root.Partitions, new SweepObserver(this), cancellationToken).ConfigureAwait(false);
        Statistics.SweepTime = Stopwatch.GetElapsedTime(sweepStarted);
        RecordSweep(sweep);

        // Rows of files that are gone go first (their chunks, vectors and symbols by cascade); touched files only get new metadata.
        await RemoveAndTouchAsync(sweep, cancellationToken).ConfigureAwait(false);
        foreach (var row in sweep.Unchanged.Concat(sweep.Touched))
        {
            Track(row);
        }

        var symbolsCurrent = string.Equals(
            await _root.Storage.Symbols.GetSymbolsVersionAsync(cancellationToken).ConfigureAwait(false),
            ContextEngineVersions.SymbolsVersion,
            StringComparison.Ordinal);

        // Files whose chunks stay but whose symbols the builder does not hold yet (the first run of a session), or whose stored
        // symbols were built by other rules.
        var symbolsOnly = !_root.SymbolsLoaded || !symbolsCurrent
            ? [.. sweep.Unchanged, .. sweep.Touched]
            : new List<FileRecord>();
        var work = sweep.Added.Concat(sweep.Changed).ToList();
        if (PriorityPaths.Count > 0)
        {
            // Open documents first (a stable partition keeps the sweep's order within each part).
            work = [.. work.Where(f => PriorityPaths.Contains(f.Path)), .. work.Where(f => !PriorityPaths.Contains(f.Path))];
            _priorityTotal = work.Count(f => PriorityPaths.Contains(f.Path));
            Statistics.PriorityFiles = _priorityTotal;
        }

        Progress.FilesTotal = work.Count + symbolsOnly.Count;
        Progress.SetBytesTotal(work.Sum(f => f.Size));
        Advance(IndexPhase.Chunking);

        // 3 and 4 for the unchanged files: symbols only.
        await ExtractSymbolsOnlyAsync(symbolsOnly, storeSymbols: !symbolsCurrent, cancellationToken).ConfigureAwait(false);
        _root.SymbolsLoaded = true;

        // 3 to 6 for the added and changed files: the open documents on their own first, then searchable, then the rest.
        if (_priorityTotal > 0)
        {
            await IndexFilesAsync(work.GetRange(0, _priorityTotal), cancellationToken).ConfigureAwait(false);
            await RefreshAsync(force: true, cancellationToken).ConfigureAwait(false);
        }

        if (work.Count > _priorityTotal)
        {
            await IndexFilesAsync(work.GetRange(_priorityTotal, work.Count - _priorityTotal), cancellationToken).ConfigureAwait(false);
        }

        Advance(IndexPhase.Storing);
        await RefreshAsync(force: true, cancellationToken).ConfigureAwait(false);
        await FinishAsync(cancellationToken).ConfigureAwait(false);
        Statistics.Duration = Stopwatch.GetElapsedTime(_started);
        LogFinished(_logger, _root.Store.Identity.Id, Statistics.FilesIndexed, Statistics.FilesDeleted, Statistics.Chunks, Statistics.Duration.TotalSeconds);
    }

    /// <summary>The <c>files.kind</c> text of a chunk kind.</summary>
    /// <param name="kind">The chunk kind.</param>
    /// <returns>The stored name.</returns>
    internal static string KindName(ChunkKind kind) => kind switch
    {
        ChunkKind.Document => "document",
        ChunkKind.TypeHeader => "type-header",
        ChunkKind.Member => "member",
        ChunkKind.Statement => "statement",
        ChunkKind.Markup => "markup",
        ChunkKind.Window => "window",
        _ => kind.ToString().ToLowerInvariant(),
    };

    private static List<ExcludedFile> ToExcluded(SweepResult sweep)
    {
        var list = new List<ExcludedFile>(sweep.SecretFiles.Count + sweep.Excluded.Count);
        foreach (var entry in sweep.SecretFiles.Concat(sweep.Excluded))
        {
            list.Add(new ExcludedFile
            {
                Path = entry.RelativePath,
                Reason = entry.Decision.Reason.ToString(),
                Description = entry.Decision.Describe(),
                IsSecretBearing = entry.Decision.IsSecretBearing,
                IsDirectory = entry.IsDirectory,
            });
        }

        list.Sort(static (a, b) => a.IsSecretBearing != b.IsSecretBearing
            ? (a.IsSecretBearing ? -1 : 1)
            : StringComparer.OrdinalIgnoreCase.Compare(a.Path, b.Path));
        return list;
    }

    private static List<NewSymbol> ToSymbols(SymbolExtraction extraction)
    {
        var seen = new HashSet<long>();
        var symbols = new List<NewSymbol>(extraction.Definitions.Count);
        foreach (var definition in extraction.Definitions)
        {
            if (seen.Add(definition.Id))
            {
                symbols.Add(new NewSymbol(
                    definition.Name.Length == 0 ? "?" : definition.Name,
                    SymbolKinds.Name(definition.Kind),
                    definition.Container.Length == 0 ? null : definition.Container,
                    definition.StartLine,
                    definition.EndLine,
                    definition.Signature.Length == 0 ? null : definition.Signature)
                {
                    Id = definition.Id,
                });
            }
        }

        return symbols;
    }

    private static ulong Fingerprint(List<SymbolEdge> edges)
    {
        var hash = new XxHash64();
        Span<byte> buffer = stackalloc byte[28];
        foreach (var edge in edges)
        {
            BinaryPrimitives.WriteInt64LittleEndian(buffer, edge.FromSymbolId ?? 0);
            BinaryPrimitives.WriteInt64LittleEndian(buffer[8..], edge.ToSymbolId);
            BinaryPrimitives.WriteInt32LittleEndian(buffer[16..], edge.Line);
            BinaryPrimitives.WriteInt32LittleEndian(buffer[20..], (int)edge.Kind);
            BinaryPrimitives.WriteInt32LittleEndian(buffer[24..], edge.CandidateCount);
            hash.Append(buffer);
        }

        return hash.GetCurrentHashAsUInt64();
    }

    private static int CountLines(string text)
    {
        var lines = 1;
        foreach (var c in text.AsSpan())
        {
            if (c == '\n')
            {
                lines++;
            }
        }

        return lines;
    }

    private void Advance(IndexPhase phase)
    {
        if (Progress.Advance(phase))
        {
            _changed();
        }
    }

    private void RecordSweep(SweepResult sweep)
    {
        var excluded = ToExcluded(sweep);
        _root.Excluded = excluded;
        _root.SecretFileCount = sweep.SecretFiles.Count;
        _root.TotalFileCount = sweep.Added.Count + sweep.Changed.Count + sweep.Touched.Count + sweep.Unchanged.Count;
        LogSwept(_logger, _root.Store.Identity.Id, sweep.Added.Count, sweep.Changed.Count, sweep.Touched.Count, sweep.Unchanged.Count, sweep.Deleted.Count, excluded.Count, sweep.Duration.TotalMilliseconds);
    }

    private void Track(FileRecord row)
    {
        _root.FileIds[row.Path] = row.Id;
        _root.FilePartitions[row.Path] = row.Partition;
    }

    private async Task RemoveAndTouchAsync(SweepResult sweep, CancellationToken cancellationToken)
    {
        if (sweep.Deleted.Count == 0 && sweep.Touched.Count == 0)
        {
            return;
        }

        var files = _root.Store.Files;
        var deleted = sweep.Deleted.Select(r => r.Path).ToList();
        var touched = await _scheduler.RunAsync(
            () => _root.Storage.WriteAsync(
                (session, token) =>
                {
                    FilesRepository.Delete(session.Connection, session.Transaction, deleted, token);
                    return files.Upsert(session.Connection, session.Transaction, sweep.Touched, token);
                },
                cancellationToken),
            cancellationToken).ConfigureAwait(false);

        foreach (var path in deleted)
        {
            _root.Symbols.RemoveFile(path);
            _root.FileIds.Remove(path);
            _root.FilePartitions.Remove(path);
            _root.EdgeFingerprints.Remove(path);
        }

        foreach (var row in touched)
        {
            Track(row);
        }

        Statistics.FilesDeleted = deleted.Count;
        _root.LastUpdatedUtc = DateTimeOffset.UtcNow;
    }

    private async Task ExtractSymbolsOnlyAsync(List<FileRecord> files, bool storeSymbols, CancellationToken cancellationToken)
    {
        if (files.Count == 0)
        {
            return;
        }

        var extractions = new SymbolExtraction?[files.Count];
        await Parallel.ForEachAsync(
            Enumerable.Range(0, files.Count),
            new ParallelOptions { MaxDegreeOfParallelism = _options.MaxDegreeOfParallelism, TaskScheduler = _scheduler, CancellationToken = cancellationToken },
            async (index, token) =>
            {
                await _pause.WaitWhilePausedAsync(token).ConfigureAwait(false);
                var record = files[index];
                var text = ReadText(record);
                extractions[index] = text is null ? null : Extract(record.Path, text, ScanFile(record.Path, text));
            }).ConfigureAwait(false);

        // Stored in groups (one transaction each) when the stored symbols were built by other rules; otherwise only held in memory.
        const int GroupSize = 200;
        for (var start = 0; start < files.Count; start += GroupSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var group = Enumerable.Range(start, Math.Min(GroupSize, files.Count - start)).Where(i => extractions[i] is not null).ToList();
            if (storeSymbols && group.Count > 0)
            {
                await _scheduler.RunAsync(
                    () => _root.Storage.WriteAsync(
                        (session, token) =>
                        {
                            foreach (var i in group)
                            {
                                session.ReplaceFileSymbols(files[i].Id, ToSymbols(extractions[i]!), token);
                            }

                            return true;
                        },
                        cancellationToken),
                    cancellationToken).ConfigureAwait(false);
            }

            foreach (var i in group)
            {
                _root.Symbols.AddFile(extractions[i]!);
            }

            Progress.FilesDone += Math.Min(GroupSize, files.Count - start);
            _changed();
        }

        Statistics.FilesSymbolsOnly = files.Count;
    }

    private string? ReadText(FileRecord record)
    {
        try
        {
            return File.ReadAllText(System.IO.Path.Combine(_root.Path, record.Path.Replace('/', System.IO.Path.DirectorySeparatorChar)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogUnreadable(_logger, ex);
            return null;
        }
    }

    private async Task IndexFilesAsync(List<FileRecord> work, CancellationToken cancellationToken)
    {
        var prepared = Channel.CreateBounded<PreparedFile>(new BoundedChannelOptions(Math.Max(1, _options.PreparedFileQueue))
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait,
        });

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var producer = ProduceAsync(work, prepared.Writer, linked.Token);
        try
        {
            await ConsumeAsync(prepared.Reader, linked.Token).ConfigureAwait(false);
        }
        catch
        {
            await linked.CancelAsync().ConfigureAwait(false);
            throw;
        }
        finally
        {
            try
            {
                await producer.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (linked.IsCancellationRequested)
            {
                // The consumer stopped first; its exception is the one that matters.
            }
        }
    }

    private async Task ProduceAsync(List<FileRecord> work, ChannelWriter<PreparedFile> writer, CancellationToken cancellationToken)
    {
        try
        {
            // Ordered: files are handed to the embedder in the sweep's order, at most the pipeline's parallelism ahead.
            await Parallel.ForEachAsync(
                work,
                new ParallelOptions { MaxDegreeOfParallelism = _options.MaxDegreeOfParallelism, TaskScheduler = _scheduler, CancellationToken = cancellationToken },
                async (record, token) =>
                {
                    // Stay on the pipeline's below-normal threads (the scheduler is captured): chunking follows.
                    await _pause.WaitWhilePausedAsync(token).ConfigureAwait(true);
                    var reusable = await ReusableVectorsAsync(record, token).ConfigureAwait(true);
                    var file = Prepare(record, reusable);
                    await writer.WriteAsync(file, token).ConfigureAwait(false);
                }).ConfigureAwait(false);
            writer.TryComplete();
        }
        catch (Exception ex)
        {
            writer.TryComplete(ex);
            throw;
        }
    }

    /// <summary>Reads, chunks and extracts a file; chunks whose text the file already had keep their stored vectors.</summary>
    private PreparedFile Prepare(FileRecord record, Dictionary<string, sbyte[]>? reusable = null)
    {
        var text = ReadText(record);
        if (text is null)
        {
            return new PreparedFile(record, [], null, 0);
        }

        // The secret scanner runs here, before anything else is built from the text: the embedding input, the vector-reuse
        // lookup, the FTS rows, the stored row and the symbol signatures all see the redacted text (design section 12, work
        // item 3.2, issue #103). Secrets are found once over the whole file, so one that the chunker split (a long PEM block)
        // is redacted in every chunk it touches, in its breadcrumb and in the source the symbol extractor reads; then each
        // chunk's text and breadcrumb are scanned again for anything that shows only in that text.
        var spans = ScanFile(record.Path, text);
        var chunks = Redact(
            text,
            spans,
            Chunker.Chunk(record.Path, text, _chunkOptions),
            _chunkOptions.TokenCounter,
            out var redacted,
            chunk => LogChunkNotMapped(_logger, _root.Store.Identity.Id, record.Path, chunk.StartLine, chunk.EndLine));
        var symbols = Extract(record.Path, text, spans);
        _options.OnFilePrepared?.Invoke(record.Path);
        var file = new PreparedFile(record, chunks, symbols, CountLines(text), redacted);
        if (reusable is { Count: > 0 })
        {
            var reused = 0;
            for (var i = 0; i < chunks.Count; i++)
            {
                if (reusable.TryGetValue(chunks[i].EmbeddingText, out var vector))
                {
                    file.Vectors[i] = new EmbeddedVector(vector, 0, null);
                    file.Pending--;
                    reused++;
                }
            }

            Statistics.AddChunksReused(reused);
        }

        return file;
    }

    /// <summary>
    /// Runs the secret scanner over a whole file's text (work item 3.2, issue #103), so a secret the chunker splits across
    /// chunks, or that a signature's normalization would cut, is found whole.
    /// </summary>
    /// <param name="path">The file's path, for the log.</param>
    /// <param name="text">The file text.</param>
    /// <returns>
    /// The secret spans, by offset into <paramref name="text"/>; <see langword="null"/> when the scan timed out, in which case
    /// the file is unverified and <see cref="Redact"/> and <see cref="Extract"/> fail closed (the file's chunks and signatures
    /// are stored as <see cref="IndexRedactor.ScanFailedPlaceholder"/>, never as its text).
    /// </returns>
    private IReadOnlyList<SecretMatch>? ScanFile(string path, string text)
    {
        try
        {
            return (_options.SecretScan ?? SecretScanner.Scan)(text);
        }
        catch (RegexMatchTimeoutException)
        {
            LogScanTimedOut(_logger, _root.Store.Identity.Id, path);
            return null;
        }
    }

    /// <summary>
    /// Redacts a file's chunks (work item 3.2, issue #103): first the spans found over the whole file text, so a secret the
    /// chunker split across chunks (a PEM block longer than the chunk cap, whose later chunks hold key material without the
    /// header the pattern needs) is redacted in every chunk it overlaps, by character offset; then each chunk's own text is
    /// scanned, for anything that shows only in chunk context. The breadcrumb is redacted with the text (PR 121 review): a
    /// SQL statement's or a CSS rule's breadcrumb is built from its first line, so a key in it would otherwise reach the
    /// embedding input, the vector-reuse key, the <c>chunks.breadcrumb</c> column, the FTS rows and the reranker; a secret
    /// span of the chunk is looked for in the breadcrumb whole and as a prefix (the chunker cuts a name at 80 or 120
    /// characters), and the breadcrumb is scanned on its own as well. A chunk with a secret gets a copy whose text and
    /// breadcrumb carry <c>&lt;redacted-index:kind&gt;</c> placeholders and whose token count is counted again on the redacted
    /// embedding text (a placeholder can be longer than a short secret); its line range is kept. When the whole-file scan
    /// timed out (<paramref name="spans"/> is <see langword="null"/>) the file is unverified and every chunk fails closed, as
    /// a chunk whose own scan times out does: its text and breadcrumb are <see cref="IndexRedactor.ScanFailedPlaceholder"/>
    /// and it is counted as redacted. So does a chunk that a secret span overlaps by line range but whose text is not the
    /// file's slice at its first line (no chunker produces one today): the span cannot be mapped into it, so it is withheld
    /// rather than stored on the strength of its own scan, which misses the body of a split secret. The per-chunk scans are
    /// no fallback: that is what the whole-file scan is for.
    /// </summary>
    /// <param name="text">The file text the chunks were cut from.</param>
    /// <param name="spans">The secret spans of <paramref name="text"/> from <see cref="ScanFile"/>, or <see langword="null"/> when that scan timed out.</param>
    /// <param name="chunks">The chunks as the chunker produced them.</param>
    /// <param name="tokenCounter">The embedding model's token counter, for the redacted chunks' counts.</param>
    /// <param name="redacted">Per chunk, whether anything was replaced.</param>
    /// <param name="notMapped">Called for each chunk withheld because a secret span could not be mapped into it (the caller logs it). Optional.</param>
    /// <returns>The same list when nothing was redacted, else a list with the redacted chunks replaced.</returns>
    internal static IReadOnlyList<Chunk> Redact(string text, IReadOnlyList<SecretMatch>? spans, IReadOnlyList<Chunk> chunks, Pillion.ContextEngine.Chunking.TokenCounter tokenCounter, out bool[] redacted, Action<Chunk>? notMapped = null)
    {
        redacted = new bool[chunks.Count];
        if (spans is null)
        {
            var withheld = new Chunk[chunks.Count];
            for (var i = 0; i < chunks.Count; i++)
            {
                withheld[i] = Withhold(chunks[i]);
                redacted[i] = true;
            }

            return withheld;
        }

        var lineStarts = spans.Count > 0 ? LineStarts(text) : [];
        Chunk[]? copy = null;
        for (var i = 0; i < chunks.Count; i++)
        {
            var chunk = chunks[i];
            var current = chunk.Text;
            var breadcrumb = chunk.Breadcrumb;
            var changed = false;

            if (spans.Count > 0)
            {
                // Spans found over the whole file, mapped into this chunk by offset. The chunk's text is the file's text from
                // the start of its first line (a prefix of it when truncated), which is checked before any offset is trusted.
                if (TryLocate(text, lineStarts, chunk, out var start))
                {
                    var end = start + current.Length;
                    StringBuilder? builder = null;
                    var position = 0;
                    foreach (var span in spans)
                    {
                        if (span.End <= start || span.Start >= end)
                        {
                            continue;
                        }

                        var from = Math.Max(span.Start, start) - start;
                        var to = Math.Min(span.End, end) - start;
                        builder ??= new StringBuilder(current.Length);
                        builder.Append(current, position, from - position).Append(IndexRedactor.PlaceholderPrefix).Append(span.KindId).Append('>');
                        position = to;

                        // The breadcrumb was built from the same lines: the secret, or the start of it where the name was cut.
                        if (RedactBreadcrumb(breadcrumb, text, span, out var redactedBreadcrumb))
                        {
                            breadcrumb = redactedBreadcrumb;
                            changed = true;
                        }
                    }

                    if (builder is not null)
                    {
                        builder.Append(current, position, current.Length - position);
                        current = builder.ToString();
                        changed = true;
                    }
                }
                else if (OverlapsLines(spans, text, lineStarts, chunk))
                {
                    // A secret lies in this chunk's lines and cannot be mapped into its text: withheld, never stored on the
                    // strength of the chunk's own scan.
                    notMapped?.Invoke(chunk);
                    copy ??= [.. chunks];
                    copy[i] = Withhold(chunk);
                    redacted[i] = true;
                    continue;
                }
            }

            // Then the chunk's own text and breadcrumb: what shows only in that text.
            var result = IndexRedactor.Redact(current);
            if (result.IsRedacted)
            {
                current = result.Text;
                changed = true;
            }

            var crumb = IndexRedactor.Redact(breadcrumb);
            if (crumb.IsRedacted)
            {
                breadcrumb = crumb.Text;
                changed = true;
            }

            if (!changed)
            {
                continue;
            }

            // The count is of the embedding text; a placeholder can be longer than the short secret it replaced.
            var replacement = chunk with { Text = current, Breadcrumb = breadcrumb };
            copy ??= [.. chunks];
            copy[i] = replacement with { TokenCount = tokenCounter(replacement.EmbeddingText) };
            redacted[i] = true;
        }

        return copy ?? chunks;
    }

    /// <summary>A chunk whose scan failed, or into which a found secret could not be mapped: its text and breadcrumb are the scan-failed placeholder; its line range and token count are kept.</summary>
    private static Chunk Withhold(Chunk chunk) => chunk with { Text = IndexRedactor.ScanFailedPlaceholder, Breadcrumb = IndexRedactor.ScanFailedPlaceholder };

    /// <summary>Finds the chunk's text in the file: the file's text from the start of its first line, which is checked before the offset is trusted.</summary>
    private static bool TryLocate(string text, int[] lineStarts, Chunk chunk, out int start)
    {
        start = 0;
        if (chunk.StartLine < 1 || chunk.StartLine > lineStarts.Length)
        {
            return false;
        }

        start = lineStarts[chunk.StartLine - 1];
        var length = chunk.Text.Length;
        return start + length <= text.Length && string.CompareOrdinal(text, start, chunk.Text, 0, length) == 0;
    }

    /// <summary>
    /// Whether any secret span lies in the lines the chunk names (<see cref="Chunk.StartLine"/> to <see cref="Chunk.EndLine"/>);
    /// a chunk whose lines cannot be placed in the file counts as overlapping, since nothing about it can be checked.
    /// </summary>
    private static bool OverlapsLines(IReadOnlyList<SecretMatch> spans, string text, int[] lineStarts, Chunk chunk)
    {
        if (chunk.StartLine < 1 || chunk.StartLine > lineStarts.Length || chunk.EndLine < chunk.StartLine)
        {
            return true;
        }

        var start = lineStarts[chunk.StartLine - 1];
        var end = chunk.EndLine < lineStarts.Length ? lineStarts[chunk.EndLine] : text.Length;
        foreach (var span in spans)
        {
            if (span.Start < end && span.End > start)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Replaces a secret span's value in a breadcrumb built from the chunk's lines. The chunker makes a breadcrumb segment
    /// from source text with <see cref="Breadcrumbs.Sanitize"/> (white space collapsed, cut with an ellipsis at the segment
    /// limit), so the value is looked for sanitized the same way: whole, or as a prefix of at least
    /// <see cref="MinBreadcrumbSecretPrefix"/> characters where the segment was cut inside it (a cut secret no longer matches
    /// its own pattern, which is why the breadcrumb's own scan is not enough).
    /// </summary>
    private static bool RedactBreadcrumb(string breadcrumb, string text, SecretMatch span, out string redacted)
    {
        redacted = breadcrumb;
        if (breadcrumb.Length == 0)
        {
            return false;
        }

        var secret = Breadcrumbs.Sanitize(text.Substring(span.Start, span.End - span.Start), int.MaxValue);
        if (secret.Length == 0)
        {
            return false;
        }

        var placeholder = IndexRedactor.PlaceholderPrefix + span.KindId + ">";
        if (breadcrumb.Contains(secret, StringComparison.Ordinal))
        {
            redacted = breadcrumb.Replace(secret, placeholder, StringComparison.Ordinal);
            return true;
        }

        // Cut inside the secret: the breadcrumb holds a prefix of it (the span is the whole match, so a prefix of it that the
        // rest does not follow can only be where the segment was cut), with the ellipsis after it when the limit cut it.
        for (var length = Math.Min(secret.Length - 1, breadcrumb.Length); length >= MinBreadcrumbSecretPrefix; length--)
        {
            var at = breadcrumb.AsSpan().IndexOf(secret.AsSpan(0, length), StringComparison.Ordinal);
            if (at >= 0)
            {
                var after = at + length;
                if (after < breadcrumb.Length && breadcrumb[after] == '…')
                {
                    after++;
                }

                redacted = string.Concat(breadcrumb.AsSpan(0, at), placeholder, breadcrumb.AsSpan(after));
                return true;
            }
        }

        return false;
    }

    /// <summary>The offset of the first character of each line, by the chunker's own rule (CR, LF, CRLF, NEL, LS and PS end a line).</summary>
    private static int[] LineStarts(string text)
    {
        var starts = new List<int>(Math.Max(16, text.Length / 32)) { 0 };
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c is '\r' or '\n' or (char)0x85 or (char)0x2028 or (char)0x2029)
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }

                starts.Add(i + 1);
            }
        }

        return [.. starts];
    }

    /// <summary>
    /// Extracts a file's symbols and redacts what can carry a value (work item 3.2, issue #103): a definition's signature (a
    /// default parameter value or an attribute argument can hold a key), and its name and container should one ever do. The
    /// spans found over the whole file are redacted in the source before the extractor reads it (<see cref="RedactSource"/>),
    /// because the extractor normalizes a signature (line breaks collapsed, cut to <see cref="SignatureText.MaxLength"/>
    /// characters) as it builds it, after which a multi-line secret such as a PEM block in a default parameter value no longer
    /// matches its pattern while its first lines are still there; each signature, name and container is then scanned once
    /// more, for anything that shows only in that text. When the whole-file scan timed out (<paramref name="spans"/> is
    /// <see langword="null"/>) the signatures are unverified and fail closed, as the chunks do: each is stored as
    /// <see cref="IndexRedactor.ScanFailedPlaceholder"/>. A reference holds identifiers only (a name, a qualifier, a container)
    /// and is stored as extracted. The definition's id hashes path, container, name, kind and ordinal, and the extractor
    /// reads a source with the same line breaks, so a redacted definition keeps its id, its line range and the edges other
    /// files hold to it.
    /// </summary>
    /// <param name="path">The file's path.</param>
    /// <param name="text">The file text.</param>
    /// <param name="spans">The secret spans of <paramref name="text"/> from <see cref="ScanFile"/>, or <see langword="null"/> when that scan timed out.</param>
    /// <returns>The extraction, with its secret-bearing definitions replaced.</returns>
    private static SymbolExtraction Extract(string path, string text, IReadOnlyList<SecretMatch>? spans)
    {
        var extraction = Extractor.Extract(path, spans is { Count: > 0 } ? RedactSource(text, spans) : text);
        SymbolDefinition[]? copy = null;
        for (var i = 0; i < extraction.Definitions.Count; i++)
        {
            var definition = extraction.Definitions[i];
            var signature = spans is null && definition.Signature.Length > 0
                ? new IndexRedaction(IndexRedactor.ScanFailedPlaceholder, 1)
                : IndexRedactor.Redact(definition.Signature);
            var name = IndexRedactor.Redact(definition.Name);
            var container = IndexRedactor.Redact(definition.Container);
            if (!signature.IsRedacted && !name.IsRedacted && !container.IsRedacted)
            {
                continue;
            }

            copy ??= [.. extraction.Definitions];
            copy[i] = new SymbolDefinition(
                definition.FilePath,
                name.Text,
                definition.Kind,
                container.Text,
                definition.StartLine,
                definition.EndLine,
                signature.Text,
                definition.Language,
                definition.IsApproximate,
                definition.Ordinal);
        }

        return copy is null ? extraction : extraction with { Definitions = copy };
    }

    /// <summary>
    /// The file text with each secret span replaced by its <c>&lt;redacted-index:kind&gt;</c> placeholder, followed by the
    /// line breaks the span held, so every line of the result starts where the same line of the original does: the
    /// extractor's line numbers (and so the definitions' ranges and ids) are those of the file.
    /// </summary>
    /// <param name="text">The file text.</param>
    /// <param name="spans">The secret spans, in document order, non-overlapping.</param>
    /// <returns>The redacted source.</returns>
    private static string RedactSource(string text, IReadOnlyList<SecretMatch> spans)
    {
        var builder = new StringBuilder(text.Length);
        var position = 0;
        foreach (var span in spans)
        {
            builder.Append(text, position, span.Start - position).Append(IndexRedactor.PlaceholderPrefix).Append(span.KindId).Append('>');
            for (var i = span.Start; i < span.End; i++)
            {
                var c = text[i];
                if (c is '\r' or '\n' or (char)0x85 or (char)0x2028 or (char)0x2029)
                {
                    builder.Append(c);
                }
            }

            position = span.End;
        }

        return builder.Append(text, position, text.Length - position).ToString();
    }

    /// <summary>
    /// The vectors of a changed file's stored chunks, by embedding text (breadcrumb and text): the model and the text are the
    /// same, so the vector is too (work item 2.10). A branch switch or an edit usually changes a few members of a file, and
    /// only those are embedded again.
    /// </summary>
    private Task<Dictionary<string, sbyte[]>?> ReusableVectorsAsync(FileRecord record, CancellationToken cancellationToken)
    {
        if (record.Id == 0)
        {
            return Task.FromResult<Dictionary<string, sbyte[]>?>(null);
        }

        return _root.Storage.Database.ReadAsync<Dictionary<string, sbyte[]>?>(
            (connection, token) =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT c.breadcrumb, c.text, e.embedding FROM chunks c JOIN chunk_embeddings e ON e.chunk_id = c.id WHERE c.file_id = $file";
                command.Parameters.AddWithValue("$file", record.Id);
                using var reader = command.ExecuteReader();
                var vectors = new Dictionary<string, sbyte[]>(StringComparer.Ordinal);
                while (reader.Read())
                {
                    token.ThrowIfCancellationRequested();
                    var bytes = (byte[])reader.GetValue(2);
                    vectors.TryAdd(reader.GetString(0) + "\n" + reader.GetString(1), System.Runtime.InteropServices.MemoryMarshal.Cast<byte, sbyte>(bytes).ToArray());
                }

                return Task.FromResult<Dictionary<string, sbyte[]>?>(vectors);
            },
            cancellationToken);
    }

    private async Task ConsumeAsync(ChannelReader<PreparedFile> reader, CancellationToken cancellationToken)
    {
        var waiting = new Queue<PreparedFile>();
        var batch = new List<(PreparedFile File, int Index)>();
        await foreach (var file in reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            waiting.Enqueue(file);
            for (var i = 0; i < file.Chunks.Count; i++)
            {
                if (file.Vectors[i] is not null)
                {
                    continue; // Reused from the stored chunk with the same text.
                }

                batch.Add((file, i));
                if (batch.Count >= _memory.BatchSize * Math.Max(1, _options.EmbeddingSortWindow))
                {
                    await EmbedAsync(batch, cancellationToken).ConfigureAwait(false);
                    batch.Clear();
                    await StoreReadyAsync(waiting, cancellationToken).ConfigureAwait(false);
                }
            }

            if (file.Pending == 0)
            {
                await StoreReadyAsync(waiting, cancellationToken).ConfigureAwait(false);
            }
        }

        if (batch.Count > 0)
        {
            await EmbedAsync(batch, cancellationToken).ConfigureAwait(false);
        }

        await StoreReadyAsync(waiting, cancellationToken).ConfigureAwait(false);
    }

    private async Task EmbedAsync(List<(PreparedFile File, int Index)> window, CancellationToken cancellationToken)
    {
        // Chunks of similar length go in the same request: the model pads every text of a batch to the longest one.
        var batch = window.OrderBy(item => item.File.Chunks[item.Index].TokenCount).ToList();
        var offset = 0;
        while (offset < batch.Count)
        {
            // The governor may halve the batch (or pause) between requests.
            _memory.BeforeBatch();
            await _pause.WaitWhilePausedAsync(cancellationToken).ConfigureAwait(false);
            var size = Math.Min(_memory.BatchSize, batch.Count - offset);
            var slice = batch.GetRange(offset, size);
            var texts = slice.Select(item => item.File.Chunks[item.Index].EmbeddingText).ToList();

            Progress.MarkEmbeddingStarted(Stopwatch.GetTimestamp());
            Advance(IndexPhase.Embedding);
            var started = Stopwatch.GetTimestamp();
            var vectors = await EmbedWithRetriesAsync(texts, cancellationToken).ConfigureAwait(false);
            Statistics.EmbeddingTime += Stopwatch.GetElapsedTime(started);
            Statistics.EmbeddingBatches++;
            for (var i = 0; i < slice.Count; i++)
            {
                var (file, index) = slice[i];
                file.Vectors[index] = vectors[i];
                file.Pending--;
            }

            offset += size;
        }
    }

    private async Task<IReadOnlyList<EmbeddedVector>> EmbedWithRetriesAsync(List<string> texts, CancellationToken cancellationToken)
    {
        var delay = _options.EmbeddingRetryDelay;
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await _embedder.EmbedAsync(_root.Model, texts, cancellationToken).ConfigureAwait(false);
            }
            catch (PillionRpcException ex) when (attempt < _options.EmbeddingRetries && ex.Error.Code is not PillionErrorCode.PolicyDenied)
            {
                LogEmbeddingRetry(_logger, attempt + 1, ex.Message);
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                delay *= 2;
            }
        }
    }

    private async Task StoreReadyAsync(Queue<PreparedFile> waiting, CancellationToken cancellationToken)
    {
        var stored = false;
        while (waiting.Count > 0 && waiting.Peek().Pending == 0)
        {
            var file = waiting.Dequeue();
            if (file.Symbols is not null)
            {
                await StoreAsync(file, cancellationToken).ConfigureAwait(false);
                stored = true;
            }

            Progress.FilesDone++;
            Progress.AddBytesDone(file.Record.Size);
        }

        if (stored)
        {
            _changed();

            // An incremental run's files are searchable once stored (their vectors go to the in-memory copy with the commit);
            // its one refresh, for the symbol graph, comes at the end.
            if (!_incremental)
            {
                await RefreshAsync(force: false, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Stores a file's row, chunks (with the vectors it has) and symbols in one transaction. A file stored before all its
    /// chunks have vectors (the save path's budget ran out) gets a modified time of 0 in its row until they do, so if the
    /// rest never arrives (cancelled, failed) the next sweep sees the file as changed and indexes it again.
    /// </summary>
    /// <returns>The stored row and the new chunk ids, in order.</returns>
    private async Task<(FileRecord Row, IReadOnlyList<long> ChunkIds)> StoreAsync(PreparedFile file, CancellationToken cancellationToken)
    {
        var chunks = new NewChunk[file.Chunks.Count];
        long tokens = 0;
        var complete = true;
        var redacted = 0;
        for (var i = 0; i < chunks.Length; i++)
        {
            var chunk = file.Chunks[i];
            tokens += chunk.TokenCount;
            var vector = file.Vectors[i];
            complete &= vector is not null;
            redacted += file.Redacted[i] ? 1 : 0;
            chunks[i] = new NewChunk(chunk.StartLine, chunk.EndLine, KindName(chunk.Kind), chunk.Breadcrumb, chunk.Text, chunk.TokenCount)
            {
                Embedding = vector is null ? ReadOnlyMemory<sbyte>.Empty : vector.Quantized,
                IsRedacted = file.Redacted[i],
            };
        }

        var record = complete ? file.Record : file.Record with { ModifiedUnixMilliseconds = 0 };
        var symbols = ToSymbols(file.Symbols!);
        var files = _root.Store.Files;
        var incremental = _incremental;
        var (row, ids) = await _scheduler.RunAsync(
            () => _root.Storage.WriteAsync(
                (session, token) =>
                {
                    session.UpdateVectorCache = incremental;
                    var stored = files.Upsert(session.Connection, session.Transaction, [record], token)[0];
                    var chunkIds = session.ReplaceFileChunks(stored.Id, chunks);
                    session.ReplaceFileSymbols(stored.Id, symbols, token);
                    return (stored, chunkIds);
                },
                cancellationToken),
            cancellationToken).ConfigureAwait(false);

        Track(row);
        if (_priorityTotal > 0 && PriorityPaths.Contains(row.Path))
        {
            _priorityStored++;
        }

        _root.Symbols.AddFile(file.Symbols!);
        _root.LastUpdatedUtc = DateTimeOffset.UtcNow;
        Statistics.FilesIndexed++;
        Statistics.Lines += file.Lines;
        Statistics.Chunks += chunks.Length;
        Statistics.Tokens += tokens;
        Statistics.ChunksRedacted += redacted;
        Progress.ChunksDone += chunks.Length;
        return (row, ids);
    }

    private bool IsRefreshDue()
    {
        if (_lastRefresh == 0)
        {
            return true;
        }

        var wait = TimeSpan.FromTicks(Math.Max(_options.MinimumRefreshInterval.Ticks, _lastRefreshCost.Ticks * _options.RefreshCostFactor));
        return Stopwatch.GetElapsedTime(_lastRefresh) >= wait;
    }

    /// <summary>
    /// Rebuilds the symbol graph, writes the reference edges of every file whose edges changed (one transaction), and reloads
    /// the in-memory vector copy so new chunks are searchable by vector at once.
    /// </summary>
    private async Task RefreshAsync(bool force, CancellationToken cancellationToken)
    {
        if (!force && !IsRefreshDue())
        {
            return;
        }

        var started = Stopwatch.GetTimestamp();
        var graph = await _scheduler.Run(() => _root.Symbols.Build(), cancellationToken).ConfigureAwait(false);
        _root.Graph = graph;
        var byFile = new Dictionary<string, List<SymbolEdge>>(StringComparer.Ordinal);
        foreach (var edge in graph.Edges)
        {
            if (!byFile.TryGetValue(edge.FilePath, out var list))
            {
                list = [];
                byFile[edge.FilePath] = list;
            }

            list.Add(edge);
        }

        var writes = new List<(string Path, long FileId, List<StoredSymbolReference> References, ulong Fingerprint)>();
        var paths = _root.EdgeFingerprintsKnown ? byFile.Keys.Concat(_root.EdgeFingerprints.Keys).Distinct(StringComparer.Ordinal) : _root.FileIds.Keys;
        foreach (var path in paths.ToList())
        {
            if (!_root.FileIds.TryGetValue(path, out var fileId))
            {
                _root.EdgeFingerprints.Remove(path);
                continue;
            }

            var edges = byFile.TryGetValue(path, out var found) ? found : [];
            var fingerprint = Fingerprint(edges);
            if (_root.EdgeFingerprintsKnown && _root.EdgeFingerprints.TryGetValue(path, out var previous) && previous == fingerprint)
            {
                continue;
            }

            var references = edges.Select(e => new StoredSymbolReference(e.FromSymbolId, e.ToSymbolId, fileId, e.Line, SymbolKinds.Name(e.Kind), e.IsAmbiguous, e.CandidateCount)).ToList();
            writes.Add((path, fileId, references, fingerprint));
        }

        if (writes.Count > 0)
        {
            try
            {
                await _scheduler.RunAsync(
                    () => _root.Storage.WriteAsync(
                        (session, token) =>
                        {
                            foreach (var write in writes)
                            {
                                session.ReplaceFileReferences(write.FileId, write.References, token);
                            }

                            return true;
                        },
                        cancellationToken),
                    cancellationToken).ConfigureAwait(false);

                foreach (var write in writes)
                {
                    if (write.References.Count == 0)
                    {
                        _root.EdgeFingerprints.Remove(write.Path);
                    }
                    else
                    {
                        _root.EdgeFingerprints[write.Path] = write.Fingerprint;
                    }
                }

                _root.EdgeFingerprintsKnown = true;
            }
            catch (SqliteException ex)
            {
                // An edge named a symbol that is not stored (it should not happen): the symbols are rebuilt on the next run.
                _symbolWritesFailed = true;
                _root.EdgeFingerprintsKnown = false;
                LogReferencesFailed(_logger, _root.Store.Identity.Id, ex.Message);
            }
        }
        else
        {
            _root.EdgeFingerprintsKnown = true;
        }

        // Off the query path: the next search finds the copy loaded.
        await _root.Storage.WarmVectorCacheAsync(cancellationToken).ConfigureAwait(false);
        await _root.RefreshCountsAsync(cancellationToken).ConfigureAwait(false);

        _lastRefreshCost = Stopwatch.GetElapsedTime(started);
        _lastRefresh = Stopwatch.GetTimestamp();
        Statistics.RefreshTime += _lastRefreshCost;
        Statistics.Refreshes++;
        if (Statistics.FirstSearchable is null && Statistics.FilesIndexed > 0)
        {
            Statistics.FirstSearchable = Stopwatch.GetElapsedTime(_started);
        }


        if (Statistics.PrioritySearchable is null && _priorityTotal > 0 && _priorityStored >= _priorityTotal)
        {
            Statistics.PrioritySearchable = Stopwatch.GetElapsedTime(_started);
            Statistics.PrioritySearchableAt = Stopwatch.GetTimestamp();
        }

        LogRefreshed(_logger, _root.Store.Identity.Id, writes.Count, graph.Edges.Count, _lastRefreshCost.TotalMilliseconds);
        _changed();
    }

    private async Task FinishAsync(CancellationToken cancellationToken)
    {
        var model = _root.Model;
        await _scheduler.RunAsync(
            () => _root.Storage.WriteAsync(
                (session, _) =>
                {
                    session.WriteMeta(IndexMetaKeysExtra.EmbeddingTokenizer, model.TokenizerFamily);
                    session.WriteMeta(IndexMetaKeysExtra.EmbeddingMaxTokens, model.MaxTokens.ToString(CultureInfo.InvariantCulture));
                    session.WriteMeta(IndexMetaKeysExtra.ChunkMaxTokens, _chunkOptions.MaxTokens.ToString(CultureInfo.InvariantCulture));
                    if (!_symbolWritesFailed)
                    {
                        session.WriteMeta(IndexSymbolsSchema.MetaKeys.SymbolsVersion, ContextEngineVersions.SymbolsVersion);
                    }
                    else
                    {
                        Sql(session, "DELETE FROM meta WHERE key = 'symbols_version'");
                    }

                    return true;
                },
                cancellationToken),
            cancellationToken).ConfigureAwait(false);

        if (_symbolWritesFailed)
        {
            _root.SymbolsLoaded = false;
        }

        // state.json: the last full sweep (only the owner writes it).
        _root.Store.RecordFullSweep();
    }

    private static void Sql(IndexWriteSession session, string sql)
    {
        using var command = session.Connection.CreateCommand();
        command.Transaction = session.Transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    [LoggerMessage(EventId = 2900, Level = LogLevel.Information, Message = "Index {IndexId}: {Added} added, {Changed} changed, {Touched} touched, {Unchanged} unchanged, {Deleted} deleted, {Excluded} excluded ({Milliseconds:0} ms).")]
    private static partial void LogSwept(ILogger logger, string indexId, int added, int changed, int touched, int unchanged, int deleted, int excluded, double milliseconds);

    [LoggerMessage(EventId = 2901, Level = LogLevel.Information, Message = "Index {IndexId} is up to date: {Files} files indexed, {Deleted} removed, {Chunks} chunks stored in {Seconds:0.0} s.")]
    private static partial void LogFinished(ILogger logger, string indexId, int files, int deleted, long chunks, double seconds);

    [LoggerMessage(EventId = 2902, Level = LogLevel.Debug, Message = "Index {IndexId}: wrote the references of {Files} files ({Edges} edges in the graph) and reloaded the vectors in {Milliseconds:0} ms.")]
    private static partial void LogRefreshed(ILogger logger, string indexId, int files, int edges, double milliseconds);

    [LoggerMessage(EventId = 2903, Level = LogLevel.Warning, Message = "Index {IndexId}: the reference edges could not be stored ({Reason}); the symbols are rebuilt on the next run.")]
    private static partial void LogReferencesFailed(ILogger logger, string indexId, string reason);

    [LoggerMessage(EventId = 2904, Level = LogLevel.Warning, Message = "An embedding batch failed (attempt {Attempt}); retrying: {Reason}")]
    private static partial void LogEmbeddingRetry(ILogger logger, int attempt, string reason);

    [LoggerMessage(EventId = 2905, Level = LogLevel.Debug, Message = "A file could not be read for indexing; it is retried on the next run.")]
    private static partial void LogUnreadable(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 2908, Level = LogLevel.Warning, Message = "Index {IndexId}: the secret scan of {Path} timed out; the file could not be verified, so its chunks and symbol signatures are stored as scan-failed placeholders, not as its text.")]
    private static partial void LogScanTimedOut(ILogger logger, string indexId, string path);

    [LoggerMessage(EventId = 2909, Level = LogLevel.Warning, Message = "Index {IndexId}: a secret found in {Path} lies in lines {StartLine}-{EndLine}, but the chunk of those lines is not the file's text there, so the secret could not be mapped into it; the chunk is stored as a scan-failed placeholder, not as its text.")]
    private static partial void LogChunkNotMapped(ILogger logger, string indexId, string path, int startLine, int endLine);

    /// <summary>A file read, chunked and extracted, waiting for its vectors.</summary>
    private sealed class PreparedFile(FileRecord record, IReadOnlyList<Chunk> chunks, SymbolExtraction? symbols, int lines, bool[]? redacted = null)
    {
        public FileRecord Record { get; } = record;

        public IReadOnlyList<Chunk> Chunks { get; } = chunks;

        /// <summary>Gets the symbols, or <see langword="null"/> when the file could not be read (it is skipped).</summary>
        public SymbolExtraction? Symbols { get; } = symbols;

        public int Lines { get; } = lines;

        public EmbeddedVector?[] Vectors { get; } = new EmbeddedVector?[chunks.Count];

        /// <summary>Gets, per chunk, whether the secret scanner replaced something in its text (stored as <c>chunks.redacted</c>).</summary>
        public bool[] Redacted { get; } = redacted ?? new bool[chunks.Count];

        public int Pending { get; set; } = chunks.Count;
    }

    /// <summary>Turns sweep progress into the Discovering and Hashing phases.</summary>
    private sealed class SweepObserver(IndexPass pass) : IProgress<SweepProgress>
    {
        public void Report(SweepProgress value)
        {
            if (value.FilesHashed > 0)
            {
                pass.Advance(IndexPhase.Hashing);
            }
        }
    }
}

/// <summary>The <c>meta</c> keys the pipeline adds to the index identity rows (the embedding model's tokenizer and input size, and the chunk cap).</summary>
internal static class IndexMetaKeysExtra
{
    /// <summary>The embedding model's tokenizer family.</summary>
    public const string EmbeddingTokenizer = "embedding_tokenizer";

    /// <summary>The most tokens the embedding model reads from one text.</summary>
    public const string EmbeddingMaxTokens = "embedding_max_tokens";

    /// <summary>The chunk cap the chunks were made with, in the model's tokens.</summary>
    public const string ChunkMaxTokens = "chunk_max_tokens";
}
