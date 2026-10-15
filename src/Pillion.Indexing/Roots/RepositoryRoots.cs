namespace Pillion.Indexing.Roots;

/// <summary>What kind of folder a repository root is.</summary>
public enum RepositoryRootKind
{
    /// <summary>A git repository: the folder has a <c>.git</c> directory.</summary>
    GitRepository,

    /// <summary>A linked git worktree: <c>.git</c> is a file pointing into the main repository's <c>worktrees</c> folder.</summary>
    GitWorktree,

    /// <summary>A submodule checkout: <c>.git</c> is a file pointing into the superproject's <c>modules</c> folder.</summary>
    GitSubmodule,

    /// <summary>Some other <c>.git</c> file that points at a git directory (a separate git dir).</summary>
    GitFile,

    /// <summary>No git repository was found: the opened folder (or the solution's folder) is the root.</summary>
    Folder,
}

/// <summary>A repository root: the folder an index covers.</summary>
/// <param name="Path">The full path of the root folder, without a trailing separator.</param>
/// <param name="Kind">What kind of folder it is.</param>
/// <param name="GitDirectory">For a git root, the real git directory (where <c>HEAD</c> lives): <c>&lt;root&gt;\.git</c>, or the directory a <c>.git</c> file points to; <see langword="null"/> for a plain folder or a <c>.git</c> file that could not be read.</param>
public sealed record RepositoryRoot(string Path, RepositoryRootKind Kind, string? GitDirectory)
{
    /// <summary>Gets a value indicating whether the root is a git working tree of any kind.</summary>
    public bool IsGit => Kind != RepositoryRootKind.Folder;
}

/// <summary>
/// Finds the repository roots for the paths the extension relays (design sections 7 and 10): the git top-level directory
/// of each path (the nearest folder above it that holds <c>.git</c>, whether that is a directory or, for a worktree or
/// a submodule, a file with <c>gitdir:</c>), or the opened folder when there is none. Several projects under one
/// repository collapse to one root; a solution that spans several repositories yields several. Pure path logic: no git
/// library, and only <c>.git</c> entries are looked at.
/// </summary>
public static class RepositoryRoots
{
    /// <summary>Resolves the roots for a set of paths.</summary>
    /// <param name="paths">Solution files, project files, or folders. A path that is not an existing folder is taken as a file (or a folder that is gone) and resolved from its parent.</param>
    /// <returns>
    /// The distinct roots in the order they were first found. Paths with no git repository above them give their folder;
    /// such a folder that lies inside another such folder is absorbed by it (a solution folder holds its projects).
    /// </returns>
    public static IReadOnlyList<RepositoryRoot> Resolve(IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        var roots = new List<RepositoryRoot>();
        var folders = new List<string>();
        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            var start = StartingFolder(path);
            if (start is null)
            {
                continue;
            }

            if (FindGitRoot(start) is { } git)
            {
                if (!roots.Any(r => SamePath(r.Path, git.Path)))
                {
                    roots.Add(git);
                }
            }
            else if (!folders.Any(f => SamePath(f, start)))
            {
                folders.Add(start);
            }
        }

        // A folder without git absorbs the folders below it: the solution folder holds its projects' folders.
        foreach (var folder in folders)
        {
            if (folders.Any(other => IsBelow(folder, other)))
            {
                continue;
            }

            roots.Add(new RepositoryRoot(folder, RepositoryRootKind.Folder, null));
        }

        return roots;
    }

    /// <summary>Resolves the root of a single path.</summary>
    /// <param name="path">A solution file, project file, or folder.</param>
    /// <returns>The git root above it, or the folder it is in when there is no git repository.</returns>
    public static RepositoryRoot? ResolveOne(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var start = StartingFolder(path);
        return start is null ? null : FindGitRoot(start) ?? new RepositoryRoot(start, RepositoryRootKind.Folder, null);
    }

    /// <summary>Walks up from a folder to the nearest one that holds <c>.git</c>.</summary>
    /// <param name="folder">The folder to start at.</param>
    /// <returns>The git root, or <see langword="null"/> when none of the folders above holds <c>.git</c>.</returns>
    public static RepositoryRoot? FindGitRoot(string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);

        for (var current = Normalize(folder); current is not null; current = System.IO.Path.GetDirectoryName(current))
        {
            var dotGit = System.IO.Path.Combine(current, ".git");
            if (Directory.Exists(dotGit))
            {
                return new RepositoryRoot(current, RepositoryRootKind.GitRepository, dotGit);
            }

            if (File.Exists(dotGit))
            {
                return FromGitFile(current, dotGit);
            }
        }

        return null;
    }

    private static RepositoryRoot FromGitFile(string root, string dotGit)
    {
        string? target = null;
        try
        {
            foreach (var line in File.ReadLines(dotGit))
            {
                if (line.StartsWith("gitdir:", StringComparison.Ordinal))
                {
                    target = line["gitdir:".Length..].Trim();
                    break;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            target = null;
        }

        if (string.IsNullOrEmpty(target))
        {
            return new RepositoryRoot(root, RepositoryRootKind.GitFile, null);
        }

        // The path may use forward slashes and may be relative to the folder that holds the .git file.
        var gitDirectory = System.IO.Path.GetFullPath(target.Replace('/', System.IO.Path.DirectorySeparatorChar), root);
        var normalized = gitDirectory.Replace('\\', '/');
        var kind = normalized.Contains("/.git/worktrees/", StringComparison.OrdinalIgnoreCase) || normalized.Contains("/worktrees/", StringComparison.OrdinalIgnoreCase)
            ? RepositoryRootKind.GitWorktree
            : normalized.Contains("/modules/", StringComparison.OrdinalIgnoreCase)
                ? RepositoryRootKind.GitSubmodule
                : RepositoryRootKind.GitFile;
        return new RepositoryRoot(root, kind, gitDirectory);
    }

    /// <summary>The folder a path is resolved from: the path itself when it is a folder, otherwise its parent.</summary>
    private static string? StartingFolder(string path)
    {
        string full;
        try
        {
            full = Normalize(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        if (Directory.Exists(full))
        {
            return full;
        }

        // A file (a solution or project), or something that no longer exists: its parent folder.
        return System.IO.Path.GetDirectoryName(full);
    }

    private static string Normalize(string path) => System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(path));

    private static bool SamePath(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static bool IsBelow(string folder, string ancestor) =>
        !SamePath(folder, ancestor)
        && folder.StartsWith(ancestor + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}
