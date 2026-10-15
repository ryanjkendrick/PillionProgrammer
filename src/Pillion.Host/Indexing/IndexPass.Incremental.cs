using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Pillion.Contracts;
using Pillion.Indexing.Discovery;
using Pillion.Indexing.Hashing;
using Pillion.Indexing.Storage;
using Pillion.LocalStore.Index;

namespace Pillion.Host.Indexing;

/// <summary>
/// The incremental run of work item 2.10: the paths the watcher or a save relayed from Visual Studio reported, instead of
/// a sweep of the whole tree. Each path is decided with the discovery rules (<see cref="PathClassifier"/>) and hashed; files
/// that are gone or now excluded lose their rows (chunks, vectors and symbols with them), files whose content changed are
/// chunked, embedded and stored, and every stored or deleted vector goes straight into the in-memory copy with its commit
/// (<see cref="IndexWriteSession.UpdateVectorCache"/>), so the file is searchable lexically and by vector as soon as it is
/// stored. Saved files go first, one at a time, within <see cref="IndexingOptions.SaveSearchableBudget"/>; the symbol graph
/// is updated once at the end (the builder's <c>AddFile</c>/<c>RemoveFile</c>, then a rebuild and the changed edges).
/// </summary>
internal sealed partial class IndexPass
{
    /// <summary>Runs the incremental update of some paths of the root.</summary>
    /// <param name="paths">The changed paths, relative to the root, with the time a save of each arrived (a <see cref="Stopwatch"/> timestamp) or <see langword="null"/> for a watcher event.</param>
    /// <param name="cancellationToken">Stops the run (files already stored stay stored).</param>
    /// <returns>What the run found; <see cref="IncrementalOutcome.SweepNeeded"/> when a folder with files appeared and only a sweep can index it.</returns>
    public async Task<IncrementalOutcome> RunIncrementalAsync(IReadOnlyDictionary<string, long?> paths, CancellationToken cancellationToken)
    {
        _incremental = true;
        var outcome = new IncrementalOutcome();
        await _pause.WaitWhilePausedAsync(cancellationToken).ConfigureAwait(false);
        Progress.FilesTotal = paths.Count;
        Advance(IndexPhase.Storing);

        // 1. Decide and hash every path on the pipeline threads.
        var classifier = _root.Classifier;
        var decided = await _scheduler.Run(() => paths.Keys.Select(path => Decide(classifier, path)).ToList(), cancellationToken).ConfigureAwait(false);

        var deletes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var touched = new List<FileRecord>();
        var work = new List<(FileRecord Record, long? SavedAt)>();
        var excluded = new List<ExcludedFile>();
        foreach (var decision in decided)
        {
            switch (decision.Kind)
            {
                case PathKind.Gone:
                case PathKind.ExcludedDirectory:
                    DeleteWithDescendants(decision.Path, deletes);
                    break;
                case PathKind.Directory:
                    // A folder appeared (moved in, copied, checked out): a sweep finds its files with the full walk.
                    outcome.SweepNeeded = true;
                    break;
                case PathKind.Excluded:
                    if (_root.FileIds.TryGetValue(decision.Path, out var excludedId))
                    {
                        deletes[decision.Path] = excludedId;
                    }

                    excluded.Add(ToExcluded(decision.Entry!));
                    break;
                case PathKind.Unreadable:
                    outcome.Retry.Add(decision.Path);
                    break;
                case PathKind.File:
                    var entry = decision.Entry!;
                    var fingerprint = decision.Fingerprint;
                    var row = await _root.Store.Files.GetAsync(entry.RelativePath, cancellationToken).ConfigureAwait(false);
                    var partition = _root.Partitions.Resolve(entry.RelativePath);
                    var record = new FileRecord(row?.Id ?? 0, entry.RelativePath, entry.Language, partition, fingerprint.Hash, fingerprint.Size, fingerprint.ModifiedUnixMilliseconds, row?.IndexedAtUnixMilliseconds ?? 0);
                    if (row is not null && row.Hash == fingerprint.Hash && row.ModifiedUnixMilliseconds != 0)
                    {
                        if (row.Size != record.Size || row.ModifiedUnixMilliseconds != record.ModifiedUnixMilliseconds || row.Path != record.Path || row.Language != record.Language || row.Partition != record.Partition)
                        {
                            touched.Add(record);
                        }
                        else
                        {
                            outcome.Unchanged++;
                        }
                    }
                    else
                    {
                        work.Add((record, paths[decision.Path]));
                    }

                    break;
            }
        }

        Progress.FilesDone = decided.Count - work.Count;
        _changed();

        // 2. Rows of files that are gone or excluded (their chunks, vectors and symbols with them), and new metadata of touched ones.
        await RemoveAndTouchIncrementallyAsync(deletes, touched, cancellationToken).ConfigureAwait(false);
        UpdateExcluded(paths.Keys, excluded);

        // 3. Saved files first, one at a time within the budget; the rest through the batched pipeline.
        var saved = work.Where(w => w.SavedAt is not null).OrderBy(w => w.SavedAt).ToList();
        foreach (var (record, savedAt) in saved)
        {
            var searchable = await IndexSavedFileAsync(record, savedAt!.Value, cancellationToken).ConfigureAwait(false);
            if (searchable is { } elapsed && (outcome.SaveToSearchable is null || elapsed > outcome.SaveToSearchable))
            {
                outcome.SaveToSearchable = elapsed;
            }
        }

        var others = work.Where(w => w.SavedAt is null).Select(w => w.Record).ToList();
        if (others.Count > 0)
        {
            await IndexFilesAsync(others, cancellationToken).ConfigureAwait(false);
        }

        if (_root.TotalFileCount >= 0)
        {
            _root.TotalFileCount = Math.Max(0, _root.TotalFileCount + work.Count(w => w.Record.Id == 0) - deletes.Count);
        }

        // 4. The symbol graph and the reference edges of the files whose edges changed; the vector copy is already current.
        if (deletes.Count > 0 || work.Count > 0 || touched.Count > 0)
        {
            await RefreshAsync(force: true, cancellationToken).ConfigureAwait(false);
        }

        outcome.Indexed = Statistics.FilesIndexed;
        outcome.Deleted = deletes.Count;
        Statistics.Duration = Stopwatch.GetElapsedTime(_started);
        Statistics.SaveToSearchable = outcome.SaveToSearchable;
        LogIncremental(_logger, _root.Store.Identity.Id, paths.Count, outcome.Indexed, outcome.Deleted, outcome.Unchanged, Statistics.Duration.TotalMilliseconds);
        return outcome;
    }

    private static ExcludedFile ToExcluded(ScanEntry entry) => new()
    {
        Path = entry.RelativePath,
        Reason = entry.Decision.Reason.ToString(),
        Description = entry.Decision.Describe(),
        IsSecretBearing = entry.Decision.IsSecretBearing,
        IsDirectory = entry.IsDirectory,
    };

    private static Decided Decide(PathClassifier classifier, string path)
    {
        ScanEntry? entry;
        try
        {
            entry = classifier.Classify(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new Decided(path, PathKind.Unreadable, null, default);
        }

        if (entry is null)
        {
            return new Decided(path, PathKind.Gone, null, default);
        }

        if (entry.IsDirectory)
        {
            if (!entry.Decision.IsIncluded)
            {
                return new Decided(path, PathKind.ExcludedDirectory, entry, default);
            }

            var full = Path.Combine(classifier.Root, entry.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            bool hasFiles;
            try
            {
                hasFiles = Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories).Any();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                hasFiles = true;
            }

            return new Decided(path, hasFiles ? PathKind.Directory : PathKind.EmptyDirectory, entry, default);
        }

        if (!entry.Decision.IsIncluded)
        {
            // A file the editor still has locked is retried, not dropped.
            return entry.Decision.Reason == ExclusionReason.Unreadable
                ? new Decided(path, PathKind.Unreadable, entry, default)
                : new Decided(path, PathKind.Excluded, entry, default);
        }

        try
        {
            var fingerprint = FileHasher.Hash(Path.Combine(classifier.Root, entry.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
            return new Decided(path, PathKind.File, entry, fingerprint);
        }
        catch (FileNotFoundException)
        {
            return new Decided(path, PathKind.Gone, null, default);
        }
        catch (DirectoryNotFoundException)
        {
            return new Decided(path, PathKind.Gone, null, default);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new Decided(path, PathKind.Unreadable, entry, default);
        }
    }

    /// <summary>The stored file at a path, or every stored file under it when it was a folder.</summary>
    private void DeleteWithDescendants(string path, Dictionary<string, long> deletes)
    {
        if (_root.FileIds.TryGetValue(path, out var id))
        {
            deletes[path] = id;
        }

        var prefix = path + "/";
        foreach (var (stored, storedId) in _root.FileIds)
        {
            if (stored.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                deletes[stored] = storedId;
            }
        }
    }

    private async Task RemoveAndTouchIncrementallyAsync(Dictionary<string, long> deletes, List<FileRecord> touched, CancellationToken cancellationToken)
    {
        if (deletes.Count == 0 && touched.Count == 0)
        {
            return;
        }

        var files = _root.Store.Files;
        var deleted = deletes.ToList();
        var stored = await _scheduler.RunAsync(
            () => _root.Storage.WriteAsync(
                (session, token) =>
                {
                    session.UpdateVectorCache = true;
                    foreach (var (_, id) in deleted)
                    {
                        session.DeleteFileChunks(id);
                    }

                    FilesRepository.Delete(session.Connection, session.Transaction, deleted.Select(d => d.Key).ToList(), token);
                    return files.Upsert(session.Connection, session.Transaction, touched, token);
                },
                cancellationToken),
            cancellationToken).ConfigureAwait(false);

        foreach (var (path, _) in deleted)
        {
            _root.Symbols.RemoveFile(path);
            _root.FileIds.Remove(path);
            _root.FilePartitions.Remove(path);
            _root.EdgeFingerprints.Remove(path);
        }

        foreach (var row in stored)
        {
            Track(row);
        }

        Statistics.FilesDeleted += deleted.Count;
        _root.LastUpdatedUtc = DateTimeOffset.UtcNow;
    }

    /// <summary>Keeps the excluded list of the last sweep current for the paths this run decided.</summary>
    private void UpdateExcluded(IEnumerable<string> decidedPaths, List<ExcludedFile> nowExcluded)
    {
        var decided = decidedPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (nowExcluded.Count == 0 && !_root.Excluded.Any(e => decided.Contains(e.Path)))
        {
            return;
        }

        var list = _root.Excluded.Where(e => !decided.Contains(e.Path)).Concat(nowExcluded).ToList();
        list.Sort(static (a, b) => a.IsSecretBearing != b.IsSecretBearing
            ? (a.IsSecretBearing ? -1 : 1)
            : StringComparer.OrdinalIgnoreCase.Compare(a.Path, b.Path));
        _root.Excluded = list;
        _root.SecretFileCount = list.Count(e => e.IsSecretBearing);
    }

    /// <summary>
    /// Indexes one saved file within the save budget: its chunks are embedded in small batches, in file order, while the
    /// next batch still fits; then the file is stored (searchable lexically in full, and by vector for the chunks embedded so
    /// far), and the remaining chunks get their vectors right after.
    /// </summary>
    /// <returns>The time from the save's arrival to the file being searchable, or <see langword="null"/> when it could not be read.</returns>
    private async Task<TimeSpan?> IndexSavedFileAsync(FileRecord record, long savedAt, CancellationToken cancellationToken)
    {
        var file = await _scheduler.Run(() => Prepare(record), cancellationToken).ConfigureAwait(false);
        if (file.Symbols is null)
        {
            Progress.FilesDone++;
            return null;
        }

        var budget = _options.SaveSearchableBudget - _options.SaveStoreReserve;
        var count = file.Chunks.Count;
        var done = 0;
        var lastBatch = TimeSpan.Zero;
        while (done < count)
        {
            if (done > 0 && Stopwatch.GetElapsedTime(savedAt) + lastBatch > budget)
            {
                break;
            }

            done += await EmbedRangeAsync(file, done, Math.Min(Math.Max(1, _options.SaveEmbeddingBatchSize), _memory.BatchSize), cancellationToken).ConfigureAwait(false);
            lastBatch = _lastEmbedDuration;
        }

        var (_, chunkIds) = await StoreAsync(file, cancellationToken).ConfigureAwait(false);
        var searchable = Stopwatch.GetElapsedTime(savedAt);
        Progress.FilesDone++;
        Progress.AddBytesDone(record.Size);
        _changed();

        if (done < count)
        {
            // Over budget: the rest of the vectors now, in normal batches, then the row is marked complete.
            Statistics.PartialFiles++;
            LogPartial(_logger, done, count, searchable.TotalMilliseconds);
            var from = done;
            while (done < count)
            {
                done += await EmbedRangeAsync(file, done, _memory.BatchSize, cancellationToken).ConfigureAwait(false);
            }

            var embeddings = Enumerable.Range(from, count - from).Select(i => new ChunkEmbedding(chunkIds[i], file.Vectors[i]!.Quantized)).ToList();
            var files = _root.Store.Files;
            var complete = file.Record;
            await _scheduler.RunAsync(
                () => _root.Storage.WriteAsync(
                    (session, token) =>
                    {
                        session.UpdateVectorCache = true;
                        session.UpsertEmbeddings(embeddings, token);
                        return files.Upsert(session.Connection, session.Transaction, [complete], token);
                    },
                    cancellationToken),
                cancellationToken).ConfigureAwait(false);
        }

        return searchable;
    }

    /// <summary>Embeds up to <paramref name="size"/> chunks of a file from <paramref name="start"/>, in file order.</summary>
    /// <returns>How many were embedded.</returns>
    private async Task<int> EmbedRangeAsync(PreparedFile file, int start, int size, CancellationToken cancellationToken)
    {
        _memory.BeforeBatch();
        await _pause.WaitWhilePausedAsync(cancellationToken).ConfigureAwait(false);
        var take = Math.Min(size, file.Chunks.Count - start);
        var texts = new List<string>(take);
        for (var i = start; i < start + take; i++)
        {
            texts.Add(file.Chunks[i].EmbeddingText);
        }

        Progress.MarkEmbeddingStarted(Stopwatch.GetTimestamp());
        var started = Stopwatch.GetTimestamp();
        var vectors = await EmbedWithRetriesAsync(texts, cancellationToken).ConfigureAwait(false);
        _lastEmbedDuration = Stopwatch.GetElapsedTime(started);
        Statistics.EmbeddingTime += _lastEmbedDuration;
        Statistics.EmbeddingBatches++;
        for (var i = 0; i < take; i++)
        {
            file.Vectors[start + i] = vectors[i];
        }

        file.Pending -= take;
        return take;
    }

    [LoggerMessage(EventId = 2906, Level = LogLevel.Debug, Message = "Index {IndexId}: {Paths} changed paths, {Indexed} files indexed, {Deleted} removed, {Unchanged} unchanged in {Milliseconds:0} ms.")]
    private static partial void LogIncremental(ILogger logger, string indexId, int paths, int indexed, int deleted, int unchanged, double milliseconds);

    [LoggerMessage(EventId = 2907, Level = LogLevel.Information, Message = "A saved file was stored with vectors for {Embedded} of its {Chunks} chunks after {Milliseconds:0} ms (the save budget); the rest follow at once.")]
    private static partial void LogPartial(ILogger logger, int embedded, int chunks, double milliseconds);

    private enum PathKind
    {
        Gone,
        File,
        Excluded,
        Directory,
        EmptyDirectory,
        ExcludedDirectory,
        Unreadable,
    }

    private readonly record struct Decided(string Path, PathKind Kind, ScanEntry? Entry, FileFingerprint Fingerprint);
}

/// <summary>What an incremental run found (work item 2.10).</summary>
internal sealed class IncrementalOutcome
{
    /// <summary>Gets or sets a value indicating whether a folder with files appeared, which only a sweep indexes.</summary>
    public bool SweepNeeded { get; set; }

    /// <summary>Gets the paths that could not be read now (locked by the editor), to try again shortly.</summary>
    public List<string> Retry { get; } = [];

    /// <summary>Gets or sets the files chunked and stored.</summary>
    public int Indexed { get; set; }

    /// <summary>Gets or sets the files whose rows were deleted.</summary>
    public int Deleted { get; set; }

    /// <summary>Gets or sets the files whose content had not changed.</summary>
    public int Unchanged { get; set; }

    /// <summary>Gets or sets the longest time from a save's arrival to its file being searchable, or <see langword="null"/> when no saved file was indexed.</summary>
    public TimeSpan? SaveToSearchable { get; set; }
}
