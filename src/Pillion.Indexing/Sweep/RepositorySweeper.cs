using System.Collections.Concurrent;
using System.Diagnostics;
using Pillion.Indexing.Discovery;
using Pillion.Indexing.Hashing;
using Pillion.Indexing.Storage;

namespace Pillion.Indexing.Sweep;

/// <summary>
/// Compares the files of a repository with the stored <c>files</c> rows (design section 7, Hash): files whose size and
/// modified time match their row are neither opened nor hashed; the others are hashed on up to
/// <see cref="ScanOptions.MaxDegreeOfParallelism"/> threads and sorted into added, changed (hash differs) and touched
/// (hash equal); stored rows with no file left are deleted. A branch switch runs this, not a rebuild.
/// </summary>
/// <remarks>
/// A row whose modified time is within <see cref="RacyWindowMilliseconds"/> of the moment it was stored is hashed again
/// even if size and time match: a file written twice inside the file system's time resolution would otherwise keep a
/// stale hash (git's "racily clean" rule).
/// </remarks>
public sealed class RepositorySweeper
{
    /// <summary>How close a row's modified time may be to its <c>indexed_at</c> before size and time alone are not trusted.</summary>
    public const long RacyWindowMilliseconds = 2000;

    private readonly ScanOptions _options;

    /// <summary>Initializes a new instance of the <see cref="RepositorySweeper"/> class.</summary>
    /// <param name="options">The scan rules and the parallelism; <see cref="ScanOptions.SkipContentChecks"/> is ignored (the sweep supplies its own).</param>
    public RepositorySweeper(ScanOptions? options = null)
    {
        _options = options ?? new ScanOptions();
    }

    /// <summary>Sweeps a root against the stored rows.</summary>
    /// <param name="rootPath">The repository root directory.</param>
    /// <param name="stored">Every stored row (see <see cref="FilesRepository.GetAllAsync"/>); empty for a first sweep.</param>
    /// <param name="partitions">Chooses the partition of each file; the top-level folder when <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels the sweep.</param>
    /// <returns>The added, changed, touched, unchanged and deleted sets and what was excluded.</returns>
    /// <exception cref="DirectoryNotFoundException">The root does not exist.</exception>
    public async Task<SweepResult> SweepAsync(
        string rootPath,
        IReadOnlyList<FileRecord> stored,
        PartitionResolver? partitions = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stored);

        var started = Stopwatch.GetTimestamp();
        var rootFull = RepositoryScanner.NormalizeRoot(rootPath);
        partitions ??= new PartitionResolver(rootFull);

        var storedByPath = new Dictionary<string, FileRecord>(stored.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var row in stored)
        {
            storedByPath[row.Path] = row;
        }

        var scanner = new RepositoryScanner(new ScanOptions
        {
            ExcludePatterns = _options.ExcludePatterns,
            MaxFileBytes = _options.MaxFileBytes,
            MaxDegreeOfParallelism = _options.MaxDegreeOfParallelism,
            IgnoreCase = _options.IgnoreCase,
            SkipContentChecks = (path, size, modified) => storedByPath.TryGetValue(path, out var row) && IsUnchanged(row, size, modified),
        });

        var added = new ConcurrentBag<FileRecord>();
        var changed = new ConcurrentBag<FileRecord>();
        var touched = new ConcurrentBag<FileRecord>();
        var unchanged = new ConcurrentBag<FileRecord>();
        var excluded = new ConcurrentBag<ScanEntry>();
        var secrets = new ConcurrentBag<ScanEntry>();
        var unreadable = new ConcurrentBag<string>();
        var present = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);

        await Parallel.ForEachAsync(
            scanner.Walk(rootFull, cancellationToken),
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, _options.MaxDegreeOfParallelism), CancellationToken = cancellationToken },
            (candidate, token) =>
            {
                token.ThrowIfCancellationRequested();
                var entry = candidate.Resolve();
                if (!entry.Decision.IsIncluded)
                {
                    if (entry.Decision.IsSecretBearing)
                    {
                        secrets.Add(entry);
                    }
                    else
                    {
                        excluded.Add(entry);
                        if (entry.Decision.Reason == ExclusionReason.Unreadable)
                        {
                            present[entry.RelativePath] = 0;
                            unreadable.Add(entry.RelativePath);
                        }
                    }

                    return ValueTask.CompletedTask;
                }

                present[entry.RelativePath] = 0;
                var partition = partitions.Resolve(entry.RelativePath);
                storedByPath.TryGetValue(entry.RelativePath, out var row);
                if (row is not null && IsUnchanged(row, entry.Size, entry.ModifiedUnixMilliseconds))
                {
                    if (row.Language == entry.Language && row.Partition == partition && row.Path == entry.RelativePath)
                    {
                        unchanged.Add(row);
                    }
                    else
                    {
                        touched.Add(row with { Path = entry.RelativePath, Language = entry.Language, Partition = partition });
                    }

                    return ValueTask.CompletedTask;
                }

                FileFingerprint fingerprint;
                try
                {
                    fingerprint = FileHasher.Hash(Path.Combine(rootFull, entry.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    unreadable.Add(entry.RelativePath);
                    excluded.Add(entry with { Decision = new FileDecision(ExclusionReason.Unreadable, ex.Message) });
                    return ValueTask.CompletedTask;
                }

                var record = new FileRecord(
                    row?.Id ?? 0,
                    entry.RelativePath,
                    entry.Language,
                    partition,
                    fingerprint.Hash,
                    fingerprint.Size,
                    fingerprint.ModifiedUnixMilliseconds,
                    row?.IndexedAtUnixMilliseconds ?? 0);
                if (row is null)
                {
                    added.Add(record);
                }
                else if (row.Hash != fingerprint.Hash)
                {
                    changed.Add(record);
                }
                else
                {
                    touched.Add(record);
                }

                return ValueTask.CompletedTask;
            }).ConfigureAwait(false);

        var deleted = stored.Where(row => !present.ContainsKey(row.Path)).OrderBy(r => r.Path, StringComparer.OrdinalIgnoreCase).ToList();
        return new SweepResult
        {
            Added = Sorted(added),
            Changed = Sorted(changed),
            Touched = Sorted(touched),
            Unchanged = Sorted(unchanged),
            Deleted = deleted,
            Excluded = [.. excluded.OrderBy(e => e.RelativePath, StringComparer.OrdinalIgnoreCase)],
            SecretFiles = [.. secrets.OrderBy(e => e.RelativePath, StringComparer.OrdinalIgnoreCase)],
            Unreadable = [.. unreadable.Order(StringComparer.OrdinalIgnoreCase)],
            Duration = Stopwatch.GetElapsedTime(started),
        };
    }

    /// <summary>Decides whether a stored row can be trusted without reading the file.</summary>
    /// <param name="row">The stored row.</param>
    /// <param name="size">The file's current size.</param>
    /// <param name="modifiedUnixMilliseconds">The file's current modified time.</param>
    /// <returns><see langword="true"/> when size and time match and the row was stored clear of the racy window.</returns>
    internal static bool IsUnchanged(FileRecord row, long size, long modifiedUnixMilliseconds) =>
        row.Size == size
        && row.ModifiedUnixMilliseconds == modifiedUnixMilliseconds
        && row.IndexedAtUnixMilliseconds - row.ModifiedUnixMilliseconds >= RacyWindowMilliseconds;

    private static List<FileRecord> Sorted(ConcurrentBag<FileRecord> bag) =>
        [.. bag.OrderBy(r => r.Path, StringComparer.OrdinalIgnoreCase)];
}
