namespace Pillion.Indexing.Roots;

/// <summary>
/// Reads what a git working tree has checked out, from the real git directory (<see cref="RepositoryRoot.GitDirectory"/>:
/// <c>.git</c>, or the folder a worktree's or submodule's <c>.git</c> file points to), without a git library (work item
/// 2.10). The watcher compares two readings to tell a branch switch, a pull, a reset or a rebase (the checked-out commit
/// moved) from git operations that leave the commit alone (<c>git status</c> refreshing the index, <c>git add</c>).
/// </summary>
/// <remarks>
/// <c>HEAD</c> is either a commit id (detached) or <c>ref: refs/heads/&lt;branch&gt;</c>. A symbolic ref is resolved in the
/// git directory, then in the common directory (a linked worktree's <c>commondir</c> file names it), then in the common
/// directory's <c>packed-refs</c>. A ref that cannot be resolved (an unborn branch) reads as the ref alone.
/// </remarks>
public static class GitHead
{
    /// <summary>The name of the file that holds the checked-out ref or commit.</summary>
    public const string HeadFileName = "HEAD";

    /// <summary>The lock git holds on the index while it rewrites it (checkout, reset, merge, add); its disappearance ends the operation.</summary>
    public const string IndexLockFileName = "index.lock";

    /// <summary>Reads the checked-out state.</summary>
    /// <param name="gitDirectory">The real git directory.</param>
    /// <returns><c>ref: refs/heads/main @ &lt;commit&gt;</c>, <c>&lt;commit&gt;</c> when detached, or <see langword="null"/> when <c>HEAD</c> cannot be read.</returns>
    public static string? Read(string gitDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gitDirectory);
        var head = ReadFirstLine(Path.Combine(gitDirectory, HeadFileName));
        if (head is null)
        {
            return null;
        }

        if (!head.StartsWith("ref:", StringComparison.Ordinal))
        {
            return head;
        }

        var reference = head[4..].Trim();
        var commit = Resolve(gitDirectory, reference);
        return commit is null ? head : $"{head} @ {commit}";
    }

    /// <summary>Finds the common git directory: the one a linked worktree's <c>commondir</c> file names, else the git directory itself.</summary>
    /// <param name="gitDirectory">The real git directory.</param>
    /// <returns>The common directory's full path.</returns>
    public static string CommonDirectory(string gitDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gitDirectory);
        var common = ReadFirstLine(Path.Combine(gitDirectory, "commondir"));
        if (string.IsNullOrEmpty(common))
        {
            return Path.GetFullPath(gitDirectory);
        }

        try
        {
            return Path.GetFullPath(Path.IsPathRooted(common) ? common : Path.Combine(gitDirectory, common));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Path.GetFullPath(gitDirectory);
        }
    }

    private static string? Resolve(string gitDirectory, string reference)
    {
        if (reference.Length == 0 || reference.Contains("..", StringComparison.Ordinal))
        {
            return null;
        }

        var relative = reference.Replace('/', Path.DirectorySeparatorChar);
        if (ReadFirstLine(Path.Combine(gitDirectory, relative)) is { Length: > 0 } local)
        {
            return local;
        }

        var common = CommonDirectory(gitDirectory);
        if (ReadFirstLine(Path.Combine(common, relative)) is { Length: > 0 } shared)
        {
            return shared;
        }

        try
        {
            var packed = Path.Combine(common, "packed-refs");
            if (!File.Exists(packed))
            {
                return null;
            }

            foreach (var line in File.ReadLines(packed))
            {
                // "<commit> <ref>"; peeled tag lines start with '^', the header with '#'.
                var space = line.IndexOf(' ', StringComparison.Ordinal);
                if (space > 0 && line[0] != '#' && line[0] != '^' && line.AsSpan(space + 1).Trim().SequenceEqual(reference))
                {
                    return line[..space];
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Being rewritten: the next reading resolves it.
        }

        return null;
    }

    private static string? ReadFirstLine(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            // Shared read: git replaces these files by rename, and may hold the old one open.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return reader.ReadLine()?.Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
