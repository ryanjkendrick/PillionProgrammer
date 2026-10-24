namespace Pillion.Indexing.Sweep;

/// <summary>A project of the solution, as the extension relays it: the name to use as a partition and the folder it lives in.</summary>
/// <param name="Name">The project name.</param>
/// <param name="Directory">The project's folder.</param>
public sealed record IndexedProject(string Name, string Directory)
{
    /// <summary>Creates a project from its project file path; the name is the file name without extension and the folder is the file's.</summary>
    /// <param name="projectFilePath">The path of a <c>.csproj</c>, <c>.vbproj</c> or similar file.</param>
    /// <returns>The project.</returns>
    public static IndexedProject FromProjectFile(string projectFilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectFilePath);
        var full = Path.GetFullPath(projectFilePath);
        return new IndexedProject(Path.GetFileNameWithoutExtension(full), Path.GetDirectoryName(full)!);
    }
}

/// <summary>
/// Chooses the <c>files.partition</c> of a file (design section 10): the project whose folder contains it (the deepest
/// one), otherwise the top-level folder of the repository it is in, otherwise <see cref="RootPartition"/> for a file at
/// the root.
/// </summary>
public sealed class PartitionResolver
{
    /// <summary>The partition of a file that is neither in a project nor in a folder.</summary>
    public const string RootPartition = "(root)";

    private readonly (string Directory, string Name)[] _projects;

    /// <summary>Initializes a new instance of the <see cref="PartitionResolver"/> class.</summary>
    /// <param name="rootPath">The repository root.</param>
    /// <param name="projects">The projects of the solution; those outside the root are ignored.</param>
    public PartitionResolver(string rootPath, IEnumerable<IndexedProject>? projects = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
        var list = new List<(string Directory, string Name)>();
        foreach (var project in projects ?? [])
        {
            var directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(project.Directory));
            if (directory.Equals(root, StringComparison.OrdinalIgnoreCase))
            {
                list.Add((string.Empty, project.Name));
            }
            else if (directory.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                list.Add((directory[(root.Length + 1)..].Replace('\\', '/'), project.Name));
            }
        }

        // Deepest folder first, so a nested project wins over the one that contains it.
        list.Sort(static (a, b) => b.Directory.Length.CompareTo(a.Directory.Length));
        _projects = [.. list];
    }

    /// <summary>Gets the partition of a file.</summary>
    /// <param name="relativePath">The path relative to the root, <c>/</c>-separated.</param>
    /// <returns>The partition name.</returns>
    public string Resolve(string relativePath)
    {
        ArgumentNullException.ThrowIfNull(relativePath);

        foreach (var (directory, name) in _projects)
        {
            if (directory.Length == 0
                || (relativePath.Length > directory.Length
                    && relativePath[directory.Length] == '/'
                    && relativePath.StartsWith(directory, StringComparison.OrdinalIgnoreCase)))
            {
                return name;
            }
        }

        var slash = relativePath.IndexOf('/', StringComparison.Ordinal);
        return slash > 0 ? relativePath[..slash] : RootPartition;
    }
}
