namespace Pillion.Host.Logging;

/// <summary>Deletes old host log files: anything older than a maximum age, then the oldest until under a size cap.</summary>
internal static class LogRetention
{
    /// <summary>The default maximum age of a log file.</summary>
    public static readonly TimeSpan DefaultMaxAge = TimeSpan.FromDays(7);

    /// <summary>The default cap on the total size of the host's log files.</summary>
    public const long DefaultMaxTotalBytes = 100L * 1024 * 1024;

    /// <summary>
    /// Applies the retention policy to files in <paramref name="directory"/> that match <paramref name="prefix"/>*.log.
    /// Other files are never touched. Files that cannot be deleted (for example in use) are skipped.
    /// </summary>
    /// <param name="directory">The log folder.</param>
    /// <param name="prefix">The file name prefix of the host's logs (for example <c>host-</c>).</param>
    /// <param name="maxAge">Files last written longer ago than this are deleted.</param>
    /// <param name="maxTotalBytes">After the age pass, the oldest files are deleted until the total is at most this.</param>
    /// <param name="now">The current time.</param>
    /// <returns>The full paths of the files deleted.</returns>
    public static IReadOnlyList<string> Apply(string directory, string prefix, TimeSpan maxAge, long maxTotalBytes, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        ArgumentException.ThrowIfNullOrEmpty(prefix);

        var deleted = new List<string>();
        if (!Directory.Exists(directory))
        {
            return deleted;
        }

        var files = new DirectoryInfo(directory)
            .EnumerateFiles(prefix + "*.log")
            .Where(f => f.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                        f.Name.EndsWith(".log", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f.LastWriteTimeUtc)
            .ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var cutoff = now.UtcDateTime - maxAge;
        var kept = new List<FileInfo>();
        foreach (var file in files)
        {
            if (file.LastWriteTimeUtc < cutoff && TryDelete(file))
            {
                deleted.Add(file.FullName);
            }
            else
            {
                kept.Add(file);
            }
        }

        var total = kept.Sum(f => f.Length);
        foreach (var file in kept)
        {
            if (total <= maxTotalBytes)
            {
                break;
            }

            var length = file.Length;
            if (TryDelete(file))
            {
                deleted.Add(file.FullName);
                total -= length;
            }
        }

        return deleted;
    }

    private static bool TryDelete(FileInfo file)
    {
        try
        {
            file.Delete();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
