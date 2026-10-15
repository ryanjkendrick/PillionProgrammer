using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Pillion.Indexing.Discovery.Ignore;

namespace Pillion.Indexing.Discovery;

/// <summary>
/// Enumerates the files under a repository root and decides, with a reason, whether each is indexed (design section 7,
/// Discover). Rules apply in this order: secret-bearing names (so a gitignored <c>.env</c> is still listed), the
/// <c>.gitignore</c> files at every level, the root's <c>.pillionignore</c>, the built-in names, the
/// <c>indexing.exclude</c> patterns, generated, minified and lock file names, the size cap, and finally the first bytes
/// (binary, auto-generated header). Excluded directories are reported once and not entered; reparse points are never
/// followed; a directory that holds its own <c>.git</c> is another repository and is skipped. Nothing is hashed here.
/// </summary>
public sealed class RepositoryScanner
{
    private static readonly EnumerationOptions DirectoryEnumeration = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = true,
        ReturnSpecialDirectories = false,

        // Hidden files count (.env is not hidden on Windows, but .gitignore-style dot files may be); system files do not.
        AttributesToSkip = FileAttributes.System,
    };

    private readonly ScanOptions _options;

    /// <summary>Initializes a new instance of the <see cref="RepositoryScanner"/> class.</summary>
    /// <param name="options">How to scan; the defaults when <see langword="null"/>.</param>
    public RepositoryScanner(ScanOptions? options = null)
    {
        _options = options ?? new ScanOptions();
    }

    /// <summary>
    /// Scans a root lazily and in parallel: the directory walk is sequential, the content checks of the files that passed
    /// every other rule run on up to <see cref="ScanOptions.MaxDegreeOfParallelism"/> threads, and entries arrive as they
    /// are decided (in no fixed order when the parallelism is above one). Stopping the enumeration early or cancelling
    /// stops the walk.
    /// </summary>
    /// <param name="root">The repository root directory.</param>
    /// <param name="cancellationToken">Cancels the scan.</param>
    /// <returns>Every file and every excluded directory with its decision.</returns>
    /// <exception cref="DirectoryNotFoundException">The root does not exist.</exception>
    public async IAsyncEnumerable<ScanEntry> ScanAsync(string root, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var rootFull = NormalizeRoot(root);
        var parallelism = Math.Max(1, _options.MaxDegreeOfParallelism);
        var channel = Channel.CreateBounded<ScanEntry>(new BoundedChannelOptions(Math.Max(64, parallelism * 32))
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait,
        });

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = linked.Token;
        var producer = Task.Run(
            async () =>
            {
                try
                {
                    await Parallel.ForEachAsync(
                        Walk(rootFull, token),
                        new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = token, TaskScheduler = _options.TaskScheduler ?? TaskScheduler.Default },
                        async (candidate, inner) =>
                        {
                            await channel.Writer.WriteAsync(candidate.Resolve(), inner).ConfigureAwait(false);
                        }).ConfigureAwait(false);
                    channel.Writer.TryComplete();
                }
                catch (Exception ex)
                {
                    channel.Writer.TryComplete(ex);
                }
            },
            CancellationToken.None);

        try
        {
            await foreach (var entry in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return entry;
            }
        }
        finally
        {
            await linked.CancelAsync().ConfigureAwait(false);
            await producer.ConfigureAwait(false);
        }
    }

    /// <summary>Scans a root and returns everything, sorted by path. Convenient for tests and small trees.</summary>
    /// <param name="root">The repository root directory.</param>
    /// <param name="cancellationToken">Cancels the scan.</param>
    /// <returns>Every file and excluded directory with its decision, ordered by path.</returns>
    public async Task<IReadOnlyList<ScanEntry>> ScanToListAsync(string root, CancellationToken cancellationToken = default)
    {
        var entries = new List<ScanEntry>();
        await foreach (var entry in ScanAsync(root, cancellationToken).ConfigureAwait(false))
        {
            entries.Add(entry);
        }

        entries.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.RelativePath, b.RelativePath));
        return entries;
    }

    /// <summary>
    /// Walks the tree sequentially and decides everything that needs no file content. A candidate that passed every rule
    /// so far still has its content check pending (<see cref="Candidate.NeedsContentCheck"/>).
    /// </summary>
    /// <param name="rootFull">The normalised root path.</param>
    /// <param name="cancellationToken">Cancels the walk.</param>
    /// <returns>The candidates, directory by directory in name order.</returns>
    internal IEnumerable<Candidate> Walk(string rootFull, CancellationToken cancellationToken)
    {
        var ignoreCase = _options.IgnoreCase;
        var user = _options.ExcludePatterns.Count > 0 ? IgnoreRules.FromPatterns(_options.ExcludePatterns, "indexing.exclude", ignoreCase) : null;
        IgnoreRules? pillion = null;
        if (TryReadText(Path.Combine(rootFull, ".pillionignore")) is { } pillionText)
        {
            pillion = IgnoreRules.Parse(pillionText, string.Empty, ".pillionignore", ignoreCase);
        }

        var pending = new Stack<(string Relative, string Full, IgnoreChain Git)>();
        pending.Push((string.Empty, rootFull, IgnoreChain.Empty));
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (relative, full, git) = pending.Pop();

            if (TryReadText(Path.Combine(full, ".gitignore")) is { } gitText)
            {
                var source = relative.Length == 0 ? ".gitignore" : relative + "/.gitignore";
                git = git.Push(IgnoreRules.Parse(gitText, relative, source, ignoreCase));
            }

            var entries = ReadDirectory(full);
            var subdirectories = new List<(string Relative, string Full)>();
            foreach (var entry in entries)
            {
                var childRelative = relative.Length == 0 ? entry.Name : relative + "/" + entry.Name;
                if (entry.IsDirectory)
                {
                    var decision = ClassifyDirectory(entry, childRelative, Path.Combine(full, entry.Name), git, pillion, user);
                    if (decision.IsIncluded)
                    {
                        subdirectories.Add((childRelative, Path.Combine(full, entry.Name)));
                    }
                    else
                    {
                        yield return Candidate.Final(new ScanEntry(childRelative, true, decision, Languages.Other, 0, entry.ModifiedUnixMilliseconds));
                    }

                    continue;
                }

                var fullPath = Path.Combine(full, entry.Name);
                var fileDecision = ClassifyFile(entry, childRelative, git, pillion, user);
                var language = Languages.Detect(entry.Name);
                var scanEntry = new ScanEntry(childRelative, false, fileDecision, language, entry.Length, entry.ModifiedUnixMilliseconds);
                if (!fileDecision.IsIncluded)
                {
                    yield return Candidate.Final(scanEntry);
                }
                else if (_options.SkipContentChecks?.Invoke(childRelative, entry.Length, entry.ModifiedUnixMilliseconds) == true)
                {
                    yield return Candidate.Final(scanEntry);
                }
                else
                {
                    yield return new Candidate(scanEntry, fullPath);
                }
            }

            for (var i = subdirectories.Count - 1; i >= 0; i--)
            {
                pending.Push((subdirectories[i].Relative, subdirectories[i].Full, git));
            }
        }
    }

    /// <summary>Normalises and validates a root: a full path without a trailing separator.</summary>
    /// <param name="root">The root as given.</param>
    /// <returns>The full path.</returns>
    /// <exception cref="DirectoryNotFoundException">The root does not exist.</exception>
    internal static string NormalizeRoot(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (!Directory.Exists(full))
        {
            throw new DirectoryNotFoundException($"The repository root '{full}' does not exist.");
        }

        return full;
    }

    private static FileDecision ClassifyDirectory(RawEntry entry, string relative, string full, IgnoreChain git, IgnoreRules? pillion, IgnoreRules? user)
    {
        if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            return new FileDecision(ExclusionReason.ReparsePoint);
        }

        if (MatchIgnore(git, pillion, relative, isDirectory: true) is { } ignored)
        {
            return ignored;
        }

        if (MatchBuiltIn(entry, relative, full, pillion, isDirectory: true) is { } builtIn)
        {
            return new FileDecision(ExclusionReason.BuiltIn, builtIn);
        }

        if (user?.Match(relative, isDirectory: true) is { IsIgnored: true } userMatch)
        {
            return new FileDecision(ExclusionReason.UserExclude, userMatch.Describe());
        }

        if (Directory.Exists(Path.Combine(full, ".git")) || File.Exists(Path.Combine(full, ".git")))
        {
            return new FileDecision(ExclusionReason.NestedRepository);
        }

        return FileDecision.Included;
    }

    private FileDecision ClassifyFile(RawEntry entry, string relative, IgnoreChain git, IgnoreRules? pillion, IgnoreRules? user)
    {
        if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            return new FileDecision(ExclusionReason.ReparsePoint);
        }

        if (NameRules.MatchSecret(entry.Name) is { } secret)
        {
            return new FileDecision(ExclusionReason.SecretBearing, secret);
        }

        if (MatchIgnore(git, pillion, relative, isDirectory: false) is { } ignored)
        {
            return ignored;
        }

        if (MatchBuiltIn(entry, relative, string.Empty, pillion, isDirectory: false) is { } builtIn)
        {
            return new FileDecision(ExclusionReason.BuiltIn, builtIn);
        }

        if (user?.Match(relative, isDirectory: false) is { IsIgnored: true } userMatch)
        {
            return new FileDecision(ExclusionReason.UserExclude, userMatch.Describe());
        }

        if (NameRules.MatchGenerated(entry.Name) is { } generated)
        {
            return new FileDecision(generated.Reason, generated.Detail);
        }

        if (entry.Length > _options.MaxFileBytes)
        {
            return new FileDecision(ExclusionReason.TooLarge, $"{entry.Length} bytes, cap {_options.MaxFileBytes}");
        }

        return FileDecision.Included;
    }

    /// <summary>
    /// Matches the built-in exclusions. <c>packages</c> counts only for a directory at the repository root that looks like
    /// a NuGet packages folder; elsewhere it is source. A <c>.pillionignore</c> negation (<c>!packages/</c>, <c>!bin/</c>)
    /// opts back in to any built-in name except <c>.git</c>; <c>indexing.exclude</c> and the secret list cannot be opted
    /// back in to.
    /// </summary>
    private static string? MatchBuiltIn(RawEntry entry, string relative, string full, IgnoreRules? pillion, bool isDirectory)
    {
        var name = NameRules.MatchBuiltIn(entry.Name);
        if (name is null
            && isDirectory
            && entry.Name.Equals(NameRules.PackagesName, StringComparison.OrdinalIgnoreCase)
            && relative.Length == entry.Name.Length
            && NameRules.LooksLikeNuGetPackagesFolder(full))
        {
            name = NameRules.PackagesName;
        }

        if (name is null)
        {
            return null;
        }

        return name != ".git" && pillion?.Match(relative, isDirectory).Verdict == IgnoreVerdict.Included ? null : name;
    }

    /// <summary>
    /// Combines the <c>.gitignore</c> stack with <c>.pillionignore</c>: a pillion rule that ignores wins, and a pillion
    /// negation re-includes what <c>.gitignore</c> ignored.
    /// </summary>
    private static FileDecision? MatchIgnore(IgnoreChain git, IgnoreRules? pillion, string relative, bool isDirectory)
    {
        var fromPillion = pillion?.Match(relative, isDirectory) ?? IgnoreMatch.None;
        if (fromPillion.IsIgnored)
        {
            return new FileDecision(ExclusionReason.PillionIgnore, fromPillion.Describe());
        }

        if (fromPillion.Verdict == IgnoreVerdict.Included)
        {
            return null;
        }

        var fromGit = git.Match(relative, isDirectory);
        return fromGit.IsIgnored ? new FileDecision(ExclusionReason.GitIgnore, fromGit.Describe()) : null;
    }

    private static string? TryReadText(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static List<RawEntry> ReadDirectory(string full)
    {
        var entries = new List<RawEntry>();
        try
        {
            var enumerable = new System.IO.Enumeration.FileSystemEnumerable<RawEntry>(
                full,
                static (ref System.IO.Enumeration.FileSystemEntry e) => new RawEntry(
                    e.FileName.ToString(),
                    e.IsDirectory,
                    e.Attributes,
                    e.IsDirectory ? 0 : e.Length,
                    e.LastWriteTimeUtc.ToUnixTimeMilliseconds()),
                DirectoryEnumeration);
            entries.AddRange(enumerable);
        }
        catch (Exception ex) when (ex is DirectoryNotFoundException or UnauthorizedAccessException or IOException)
        {
            // The directory vanished or cannot be listed: it contributes nothing.
        }

        entries.Sort(static (a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name));
        return entries;
    }

    /// <summary>A directory entry as the file system reports it.</summary>
    private readonly record struct RawEntry(string Name, bool IsDirectory, FileAttributes Attributes, long Length, long ModifiedUnixMilliseconds);

    /// <summary>A file that passed every rule that needs no content, or an entry that is already final.</summary>
    internal sealed record Candidate(ScanEntry Entry, string? FullPath)
    {
        /// <summary>Gets a value indicating whether the first bytes still have to be read.</summary>
        public bool NeedsContentCheck => FullPath is not null;

        /// <summary>Creates a candidate whose decision is already made.</summary>
        /// <param name="entry">The final entry.</param>
        /// <returns>The candidate.</returns>
        public static Candidate Final(ScanEntry entry) => new(entry, null);

        /// <summary>Completes the decision: reads the first bytes of a pending candidate.</summary>
        /// <returns>The final entry.</returns>
        public ScanEntry Resolve()
        {
            if (FullPath is null)
            {
                return Entry;
            }

            var content = ContentInspector.Inspect(FullPath);
            return content.IsIncluded ? Entry : Entry with { Decision = content };
        }
    }
}
