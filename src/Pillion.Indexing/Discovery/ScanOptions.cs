namespace Pillion.Indexing.Discovery;

/// <summary>How a repository is scanned.</summary>
public sealed class ScanOptions
{
    /// <summary>The default file size cap: 1 MB.</summary>
    public const long DefaultMaxFileBytes = 1024 * 1024;

    /// <summary>Gets the patterns of the <c>indexing.exclude</c> setting (and a repository policy's <c>exclude</c>), in <c>.gitignore</c> syntax relative to the root.</summary>
    public IReadOnlyList<string> ExcludePatterns { get; init; } = [];

    /// <summary>Gets the largest file that is indexed, in bytes; larger files are excluded as <see cref="ExclusionReason.TooLarge"/>. Defaults to 1 MB.</summary>
    public long MaxFileBytes { get; init; } = DefaultMaxFileBytes;

    /// <summary>
    /// Gets how many files are inspected (and, in a sweep, hashed) at once. Defaults to half the logical processors, at
    /// least one, so indexing stays polite (design section 13: at most half the cores). The directory walk itself is
    /// sequential.
    /// </summary>
    public int MaxDegreeOfParallelism { get; init; } = DefaultParallelism;

    /// <summary>Gets a value indicating whether names match regardless of case (ignore files, built-in names, settings). Defaults to <see langword="true"/> as on Windows.</summary>
    public bool IgnoreCase { get; init; } = true;

    /// <summary>
    /// Gets a callback that says the caller already knows a file passed the content checks and has not changed, so it is
    /// not opened again. It receives the relative path, the size and the modified time (Unix milliseconds). A sweep uses
    /// it for files whose size and modified time match the stored row. Optional.
    /// </summary>
    public Func<string, long, long, bool>? SkipContentChecks { get; init; }

    /// <summary>Gets half the logical processors, at least one.</summary>
    public static int DefaultParallelism => Math.Max(1, Environment.ProcessorCount / 2);
}

/// <summary>One file or directory met by a scan, with the decision about it.</summary>
/// <param name="RelativePath">The path relative to the root, <c>/</c>-separated.</param>
/// <param name="IsDirectory">Whether this is a directory that was excluded as a whole (its files are not listed).</param>
/// <param name="Decision">Indexed, or the reason it is not.</param>
/// <param name="Language">The language from the extension (see <see cref="Languages"/>); <see cref="Languages.Other"/> for directories.</param>
/// <param name="Size">The size in bytes; 0 for a directory.</param>
/// <param name="ModifiedUnixMilliseconds">The last write time in UTC as Unix milliseconds.</param>
public sealed record ScanEntry(
    string RelativePath,
    bool IsDirectory,
    FileDecision Decision,
    string Language,
    long Size,
    long ModifiedUnixMilliseconds);
