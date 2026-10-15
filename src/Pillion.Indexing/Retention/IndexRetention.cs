using Pillion.Common;
using Pillion.Common.Settings;
using Pillion.Indexing.Ownership;

namespace Pillion.Indexing.Retention;

/// <summary>How long an unused index is kept (the <c>indexing.retentionDays</c> setting).</summary>
/// <param name="RetentionDays">Days an index may go unopened; zero or less turns the age rule off. Defaults to 30.</param>
public sealed record IndexRetentionPolicy(int RetentionDays = 30);

/// <summary>One index folder as the Index window lists it.</summary>
/// <param name="Id">The index id (the folder name).</param>
/// <param name="Directory">The index folder.</param>
/// <param name="RootPath">The repository root, from <c>state.json</c>; <see langword="null"/> if the state is missing.</param>
/// <param name="EmbeddingModel">The embedding model id, from <c>state.json</c>.</param>
/// <param name="ChunkerVersion">The chunker version, from <c>state.json</c>.</param>
/// <param name="DiskBytes">Disk use of the whole folder.</param>
/// <param name="LastUsedUtc">The latest of the last open, the last write to the database and the lock file.</param>
/// <param name="RootExists">Whether the repository root still exists; <see langword="null"/> when that cannot be told (no state, or its drive is not available).</param>
/// <param name="InUse">Whether a live host holds <c>writer.lock</c>.</param>
public sealed record IndexUsage(
    string Id,
    string Directory,
    string? RootPath,
    string? EmbeddingModel,
    string? ChunkerVersion,
    long DiskBytes,
    DateTimeOffset LastUsedUtc,
    bool? RootExists,
    bool InUse);

/// <summary>What retention decided for one index.</summary>
public enum RetentionDecision
{
    /// <summary>Kept: its root exists and it was used recently.</summary>
    Keep,

    /// <summary>Kept because a live host owns it, whatever its age.</summary>
    KeepInUse,

    /// <summary>Deleted (or would be): its repository root no longer exists.</summary>
    DeleteRootMissing,

    /// <summary>Deleted (or would be): not opened for longer than the retention period.</summary>
    DeleteUnused,
}

/// <summary>What <see cref="IndexRetention.Delete"/> did.</summary>
public enum IndexDeleteOutcome
{
    /// <summary>The folder was deleted.</summary>
    Deleted,

    /// <summary>There is no index with that id.</summary>
    NotFound,

    /// <summary>A live host holds the index's <c>writer.lock</c>; nothing was deleted.</summary>
    InUse,

    /// <summary>The folder could not be deleted (a file is open elsewhere); part of it may remain.</summary>
    Failed,
}

/// <summary>The decision about one index and what became of it.</summary>
/// <param name="Usage">The index.</param>
/// <param name="Decision">What retention decided.</param>
/// <param name="Deleted">Whether the folder was deleted (never in a dry run).</param>
/// <param name="Error">Why the folder could not be deleted, if it could not.</param>
public sealed record IndexRetentionItem(IndexUsage Usage, RetentionDecision Decision, bool Deleted, string? Error = null)
{
    /// <summary>Gets a value indicating whether retention wants this index gone.</summary>
    public bool WantsDelete => Decision is RetentionDecision.DeleteRootMissing or RetentionDecision.DeleteUnused;
}

/// <summary>The outcome of a retention sweep.</summary>
/// <param name="DryRun">Whether nothing was deleted because this was a dry run.</param>
/// <param name="Skipped">Whether the sweep did nothing because <c>features.indexing</c> is off.</param>
/// <param name="Items">One entry per index folder, in id order.</param>
public sealed record IndexRetentionReport(bool DryRun, bool Skipped, IReadOnlyList<IndexRetentionItem> Items)
{
    /// <summary>Gets the disk use of the indexes that were deleted (or, in a dry run, would be).</summary>
    public long BytesReclaimed => Items.Where(i => i.WantsDelete && (DryRun || i.Deleted)).Sum(i => i.Usage.DiskBytes);
}

/// <summary>
/// Deletes indexes nobody needs (design section 10, Retention): those whose repository root no longer exists, and those
/// not opened for <c>indexing.retentionDays</c> (30 by default). An index with a live owner is never deleted. A root
/// whose drive is not available (a removable or network drive that is offline) does not count as missing. Only folders
/// named like an index id are looked at. <see cref="GetUsage"/> reports disk use per index for the Index window.
/// </summary>
public sealed class IndexRetention
{
    private readonly PillionPaths _paths;
    private readonly IFeatureFlags? _features;
    private readonly IProcessProbe _probe;
    private readonly TimeProvider _time;
    private readonly IndexRetentionPolicy _policy;

    /// <summary>Initializes a new instance of the <see cref="IndexRetention"/> class.</summary>
    /// <param name="paths">The data layout.</param>
    /// <param name="features">The feature flags; while <c>indexing</c> is off the sweep does nothing. Optional.</param>
    /// <param name="policy">The retention period.</param>
    /// <param name="probe">Decides whether a lock's process is alive.</param>
    /// <param name="time">The clock.</param>
    public IndexRetention(
        PillionPaths paths,
        IFeatureFlags? features = null,
        IndexRetentionPolicy? policy = null,
        IProcessProbe? probe = null,
        TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _paths = paths;
        _features = features;
        _policy = policy ?? new IndexRetentionPolicy();
        _probe = probe ?? ProcessProbe.Instance;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Lists every index folder with its disk use, last use and whether it is in use.</summary>
    /// <returns>One entry per index folder, ordered by id.</returns>
    public IReadOnlyList<IndexUsage> GetUsage()
    {
        if (!Directory.Exists(_paths.IndexDirectory))
        {
            return [];
        }

        var usages = new List<IndexUsage>();
        foreach (var directory in Directory.EnumerateDirectories(_paths.IndexDirectory))
        {
            if (IsIndexId(Path.GetFileName(directory)))
            {
                usages.Add(Describe(new IndexFolder(directory)));
            }
        }

        return [.. usages.OrderBy(u => u.Id, StringComparer.Ordinal)];
    }

    /// <summary>Decides, and unless <paramref name="dryRun"/> carries out, the deletion of unneeded indexes.</summary>
    /// <param name="dryRun">When <see langword="true"/>, only report what would be deleted.</param>
    /// <param name="retentionDays">Overrides the policy's retention period for this sweep.</param>
    /// <param name="cancellationToken">Cancels the sweep between indexes.</param>
    /// <returns>The report, with one item per index.</returns>
    public Task<IndexRetentionReport> SweepAsync(bool dryRun = false, int? retentionDays = null, CancellationToken cancellationToken = default)
    {
        if (_features is not null && !_features.IsEnabled(FeatureNames.Indexing))
        {
            return Task.FromResult(new IndexRetentionReport(dryRun, Skipped: true, []));
        }

        return Task.Run(() => Sweep(dryRun, retentionDays ?? _policy.RetentionDays, cancellationToken), cancellationToken);
    }

    /// <summary>
    /// Deletes one index folder on request (the Index window's Delete). Refused while a live host holds its
    /// <c>writer.lock</c>: the caller closes its own copy first, and another host's index is never deleted from here.
    /// </summary>
    /// <param name="indexId">The index id (the folder name).</param>
    /// <returns>What happened.</returns>
    public IndexDeleteOutcome Delete(string indexId)
    {
        if (string.IsNullOrEmpty(indexId) || !IsIndexId(indexId))
        {
            return IndexDeleteOutcome.NotFound;
        }

        var folder = new IndexFolder(Path.Combine(_paths.IndexDirectory, indexId));
        if (!Directory.Exists(folder.Directory))
        {
            return IndexDeleteOutcome.NotFound;
        }

        if (WriterLock.Read(folder.LockFile) is { } owner && _probe.IsAlive(owner.ProcessId, owner.ProcessStartUtc))
        {
            return IndexDeleteOutcome.InUse;
        }

        try
        {
            Directory.Delete(folder.Directory, recursive: true);
            return IndexDeleteOutcome.Deleted;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return IndexDeleteOutcome.Failed;
        }
    }

    private static bool IsIndexId(string name) =>
        name.Length == 32 && name.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    private IndexRetentionReport Sweep(bool dryRun, int retentionDays, CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();
        var items = new List<IndexRetentionItem>();
        foreach (var usage in GetUsage())
        {
            cancellationToken.ThrowIfCancellationRequested();

            var decision = Decide(usage, now, retentionDays);
            var deleted = false;
            string? error = null;
            if (!dryRun && decision is RetentionDecision.DeleteRootMissing or RetentionDecision.DeleteUnused)
            {
                try
                {
                    Directory.Delete(usage.Directory, recursive: true);
                    deleted = true;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    error = ex.Message;
                }
            }

            items.Add(new IndexRetentionItem(usage, decision, deleted, error));
        }

        return new IndexRetentionReport(dryRun, Skipped: false, items);
    }

    private static RetentionDecision Decide(IndexUsage usage, DateTimeOffset now, int retentionDays)
    {
        if (usage.InUse)
        {
            return RetentionDecision.KeepInUse;
        }

        if (usage.RootExists == false)
        {
            return RetentionDecision.DeleteRootMissing;
        }

        if (retentionDays > 0 && now - usage.LastUsedUtc > TimeSpan.FromDays(retentionDays))
        {
            return RetentionDecision.DeleteUnused;
        }

        return RetentionDecision.Keep;
    }

    private IndexUsage Describe(IndexFolder folder)
    {
        var state = IndexStateFile.Read(folder.StateFile);

        var lastUsed = state?.LastOpenedUtc ?? DateTimeOffset.MinValue;
        foreach (var file in new[] { folder.DatabaseFile, folder.DatabaseFile + "-wal", folder.LockFile, folder.StateFile })
        {
            if (File.Exists(file))
            {
                var written = new DateTimeOffset(File.GetLastWriteTimeUtc(file), TimeSpan.Zero);
                if (written > lastUsed)
                {
                    lastUsed = written;
                }
            }
        }

        if (lastUsed == DateTimeOffset.MinValue)
        {
            lastUsed = new DateTimeOffset(Directory.GetLastWriteTimeUtc(folder.Directory), TimeSpan.Zero);
        }

        var inUse = WriterLock.Read(folder.LockFile) is { } owner && _probe.IsAlive(owner.ProcessId, owner.ProcessStartUtc);
        return new IndexUsage(
            folder.Id,
            folder.Directory,
            state?.RootPath,
            state?.EmbeddingModel,
            state?.ChunkerVersion,
            folder.GetDiskUsageBytes(),
            lastUsed,
            RootExists(state?.RootPath),
            inUse);
    }

    private static bool? RootExists(string? root)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            return null;
        }

        try
        {
            if (Directory.Exists(root))
            {
                return true;
            }

            // A root on a drive or share that is not there right now is unknown, not missing.
            var volume = Path.GetPathRoot(root);
            return string.IsNullOrEmpty(volume) || !Directory.Exists(volume) ? null : false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }
}
