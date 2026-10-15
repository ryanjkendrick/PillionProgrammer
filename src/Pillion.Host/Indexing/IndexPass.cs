using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.IO.Hashing;
using System.Threading.Channels;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
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

    /// <summary>Runs the pass to the end.</summary>
    /// <param name="cancellationToken">Stops the run (files already stored stay stored).</param>
    /// <returns>A task that completes when the index is up to date with the tree.</returns>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        await _pause.WaitWhilePausedAsync(cancellationToken).ConfigureAwait(false);
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
        Progress.FilesTotal = work.Count + symbolsOnly.Count;
        Progress.SetBytesTotal(work.Sum(f => f.Size));
        Advance(IndexPhase.Chunking);

        // 3 and 4 for the unchanged files: symbols only.
        await ExtractSymbolsOnlyAsync(symbolsOnly, storeSymbols: !symbolsCurrent, cancellationToken).ConfigureAwait(false);
        _root.SymbolsLoaded = true;

        // 3 to 6 for the added and changed files.
        if (work.Count > 0)
        {
            await IndexFilesAsync(work, cancellationToken).ConfigureAwait(false);
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
                extractions[index] = text is null ? null : Extractor.Extract(record.Path, text);
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
                    await _pause.WaitWhilePausedAsync(token).ConfigureAwait(false);
                    var file = Prepare(record);
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

    private PreparedFile Prepare(FileRecord record)
    {
        var text = ReadText(record);
        if (text is null)
        {
            return new PreparedFile(record, [], null, 0);
        }

        var chunks = Chunker.Chunk(record.Path, text, _chunkOptions);
        var symbols = Extractor.Extract(record.Path, text);
        _options.OnFilePrepared?.Invoke(record.Path);
        return new PreparedFile(record, chunks, symbols, CountLines(text));
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
                batch.Add((file, i));
                if (batch.Count >= _memory.BatchSize * Math.Max(1, _options.EmbeddingSortWindow))
                {
                    await EmbedAsync(batch, cancellationToken).ConfigureAwait(false);
                    batch.Clear();
                    await StoreReadyAsync(waiting, cancellationToken).ConfigureAwait(false);
                }
            }

            if (file.Chunks.Count == 0)
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
            await RefreshAsync(force: false, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task StoreAsync(PreparedFile file, CancellationToken cancellationToken)
    {
        var chunks = new NewChunk[file.Chunks.Count];
        long tokens = 0;
        for (var i = 0; i < chunks.Length; i++)
        {
            var chunk = file.Chunks[i];
            tokens += chunk.TokenCount;
            chunks[i] = new NewChunk(chunk.StartLine, chunk.EndLine, KindName(chunk.Kind), chunk.Breadcrumb, chunk.Text, chunk.TokenCount)
            {
                Embedding = file.Vectors[i]!.Quantized,
            };
        }

        var symbols = ToSymbols(file.Symbols!);
        var files = _root.Store.Files;
        var row = await _scheduler.RunAsync(
            () => _root.Storage.WriteAsync(
                (session, token) =>
                {
                    var stored = files.Upsert(session.Connection, session.Transaction, [file.Record], token)[0];
                    session.ReplaceFileChunks(stored.Id, chunks);
                    session.ReplaceFileSymbols(stored.Id, symbols, token);
                    return stored;
                },
                cancellationToken),
            cancellationToken).ConfigureAwait(false);

        Track(row);
        _root.Symbols.AddFile(file.Symbols!);
        _root.LastUpdatedUtc = DateTimeOffset.UtcNow;
        Statistics.FilesIndexed++;
        Statistics.Lines += file.Lines;
        Statistics.Chunks += chunks.Length;
        Statistics.Tokens += tokens;
        Progress.ChunksDone += chunks.Length;
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

    /// <summary>A file read, chunked and extracted, waiting for its vectors.</summary>
    private sealed class PreparedFile(FileRecord record, IReadOnlyList<Chunk> chunks, SymbolExtraction? symbols, int lines)
    {
        public FileRecord Record { get; } = record;

        public IReadOnlyList<Chunk> Chunks { get; } = chunks;

        /// <summary>Gets the symbols, or <see langword="null"/> when the file could not be read (it is skipped).</summary>
        public SymbolExtraction? Symbols { get; } = symbols;

        public int Lines { get; } = lines;

        public EmbeddedVector?[] Vectors { get; } = new EmbeddedVector?[chunks.Count];

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
