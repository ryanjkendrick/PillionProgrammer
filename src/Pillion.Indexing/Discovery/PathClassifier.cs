using Pillion.Indexing.Discovery.Ignore;

namespace Pillion.Indexing.Discovery;

/// <summary>
/// Decides about one path at a time with exactly the rules of <see cref="RepositoryScanner"/> (work item 2.10): the file
/// watcher filters its events through <see cref="ClassifyByName"/>, which needs no file system access beyond the ignore
/// files (read once per directory and cached), and the incremental indexer decides each changed file with
/// <see cref="Classify"/>, which also applies the size cap, the content checks and the directory rules that look at the
/// disk (reparse points, nested repositories, the NuGet <c>packages</c> folder). Every ancestor directory of the path is
/// checked as the scan would have met it, so a file under an excluded directory is excluded with that directory's reason.
/// </summary>
/// <remarks>
/// The ignore files are cached: call <see cref="Invalidate"/> when a <c>.gitignore</c> or <c>.pillionignore</c> changes
/// (<see cref="IsIgnoreFile"/>), which also forgets the directory decisions. Thread-safe.
/// </remarks>
public sealed class PathClassifier
{
    private readonly ScanOptions _options;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, IgnoreChain> _chains = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FileDecision> _directories = new(StringComparer.OrdinalIgnoreCase);
    private IgnoreRules? _pillion;
    private IgnoreRules? _user;
    private bool _loaded;

    /// <summary>Initializes a new instance of the <see cref="PathClassifier"/> class.</summary>
    /// <param name="root">The repository root directory.</param>
    /// <param name="options">The scan rules (exclusions, size cap, case); the defaults when <see langword="null"/>.</param>
    /// <exception cref="DirectoryNotFoundException">The root does not exist.</exception>
    public PathClassifier(string root, ScanOptions? options = null)
    {
        Root = RepositoryScanner.NormalizeRoot(root);
        _options = options ?? new ScanOptions();
    }

    /// <summary>Gets the root (full path, no trailing separator).</summary>
    public string Root { get; }

    /// <summary>Gets whether a path names an ignore file whose change can change decisions anywhere below its folder.</summary>
    /// <param name="relativePath">The path relative to the root.</param>
    /// <returns><see langword="true"/> for <c>.gitignore</c> at any depth and <c>.pillionignore</c> at the root.</returns>
    public static bool IsIgnoreFile(string relativePath)
    {
        var path = Normalize(relativePath);
        var name = path[(path.LastIndexOf('/') + 1)..];
        return name.Equals(".gitignore", StringComparison.OrdinalIgnoreCase)
            || (path.Equals(".pillionignore", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Normalises a relative path: <c>/</c> separators, no leading or trailing separator.</summary>
    /// <param name="relativePath">The path.</param>
    /// <returns>The normalised path.</returns>
    public static string Normalize(string relativePath)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        return relativePath.Replace('\\', '/').Trim('/');
    }

    /// <summary>Forgets the cached ignore files and directory decisions (an ignore file changed, or the settings did).</summary>
    public void Invalidate()
    {
        lock (_gate)
        {
            _chains.Clear();
            _directories.Clear();
            _pillion = null;
            _user = null;
            _loaded = false;
        }
    }

    /// <summary>
    /// Decides a path from its name and the ignore rules only, as the scan would before it looks at the disk: the ancestor
    /// directories (built-in names, ignore files, <c>indexing.exclude</c>), then the path itself (for a file also the
    /// secret, generated, minified and lock-file names). Never opens the path; reads only ignore files, once each.
    /// </summary>
    /// <param name="relativePath">The path relative to the root.</param>
    /// <param name="isDirectory">Whether the path is a directory.</param>
    /// <returns>Included, or the first rule that excludes it.</returns>
    public FileDecision ClassifyByName(string relativePath, bool isDirectory = false)
    {
        var path = Normalize(relativePath);
        if (path.Length == 0)
        {
            return FileDecision.Included;
        }

        var (pillion, user) = Rules();
        var slash = path.IndexOf('/', StringComparison.Ordinal);
        while (slash >= 0)
        {
            var directory = path[..slash];
            if (ByName(directory, isDirectory: true, pillion, user) is { IsIncluded: false } excluded)
            {
                return excluded;
            }

            slash = path.IndexOf('/', slash + 1);
        }

        return ByName(path, isDirectory, pillion, user) ?? FileDecision.Included;
    }

    /// <summary>
    /// Decides a path fully, as a scan would: every ancestor directory with the rules that look at the disk, then the file
    /// (size cap and the content checks included).
    /// </summary>
    /// <param name="relativePath">The path relative to the root.</param>
    /// <returns>The entry, or <see langword="null"/> when nothing exists at the path. A directory is returned with <see cref="ScanEntry.IsDirectory"/> set and its own decision.</returns>
    public ScanEntry? Classify(string relativePath)
    {
        var path = Normalize(relativePath);
        if (path.Length == 0)
        {
            return null;
        }

        var full = Path.Combine(Root, path.Replace('/', Path.DirectorySeparatorChar));
        FileSystemInfo info = Directory.Exists(full) ? new DirectoryInfo(full) : new FileInfo(full);
        if (!info.Exists)
        {
            return null;
        }

        var isDirectory = info is DirectoryInfo;
        var name = info.Name;
        var language = isDirectory ? Languages.Other : Languages.Detect(name);
        long size;
        long modified;
        FileAttributes attributes;
        try
        {
            size = info is FileInfo file ? file.Length : 0;
            modified = new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeMilliseconds();
            attributes = info.Attributes;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        // Ancestors first: a file under an excluded directory takes the directory's reason, as the scan never enters it.
        var slash = path.IndexOf('/', StringComparison.Ordinal);
        while (slash >= 0)
        {
            var ancestor = DirectoryDecision(path[..slash]);
            if (!ancestor.IsIncluded)
            {
                return new ScanEntry(path, isDirectory, ancestor, language, size, modified);
            }

            slash = path.IndexOf('/', slash + 1);
        }

        if (isDirectory)
        {
            return new ScanEntry(path, true, DirectoryDecision(path), Languages.Other, 0, modified);
        }

        var (pillion, user) = Rules();
        var entry = new RepositoryScanner.RawEntry(name, false, attributes, size, modified);
        var decision = RepositoryScanner.ClassifyFile(entry, path, Chain(Parent(path)), pillion, user, _options.MaxFileBytes);
        if (decision.IsIncluded)
        {
            var content = ContentInspector.Inspect(full);
            if (!content.IsIncluded)
            {
                decision = content;
            }
        }

        return new ScanEntry(path, false, decision, language, size, modified);
    }

    private static string Parent(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash < 0 ? string.Empty : path[..slash];
    }

    private FileDecision? ByName(string path, bool isDirectory, IgnoreRules? pillion, IgnoreRules? user)
    {
        var name = path[(path.LastIndexOf('/') + 1)..];
        if (!isDirectory && NameRules.MatchSecret(name) is { } secret)
        {
            return new FileDecision(ExclusionReason.SecretBearing, secret);
        }

        if (RepositoryScanner.MatchIgnore(Chain(Parent(path)), pillion, path, isDirectory) is { } ignored)
        {
            return ignored;
        }

        // The packages folder needs a look at the disk; Classify applies it.
        var entry = new RepositoryScanner.RawEntry(name, isDirectory, 0, 0, 0);
        if (NameRules.MatchBuiltIn(name) is not null && RepositoryScanner.MatchBuiltIn(entry, path, string.Empty, pillion, isDirectory) is { } builtIn)
        {
            return new FileDecision(ExclusionReason.BuiltIn, builtIn);
        }

        if (user?.Match(path, isDirectory) is { IsIgnored: true } userMatch)
        {
            return new FileDecision(ExclusionReason.UserExclude, userMatch.Describe());
        }

        if (!isDirectory && NameRules.MatchGenerated(name) is { } generated)
        {
            return new FileDecision(generated.Reason, generated.Detail);
        }

        return null;
    }

    private FileDecision DirectoryDecision(string path)
    {
        lock (_gate)
        {
            if (_directories.TryGetValue(path, out var cached))
            {
                return cached;
            }
        }

        var full = Path.Combine(Root, path.Replace('/', Path.DirectorySeparatorChar));
        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(full);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            attributes = FileAttributes.Directory;
        }

        var (pillion, user) = Rules();
        var name = path[(path.LastIndexOf('/') + 1)..];
        var decision = RepositoryScanner.ClassifyDirectory(new RepositoryScanner.RawEntry(name, true, attributes, 0, 0), path, full, Chain(Parent(path)), pillion, user);
        lock (_gate)
        {
            _directories[path] = decision;
        }

        return decision;
    }

    /// <summary>The ignore files that apply to the entries of a directory: the root's, then one per directory down to it.</summary>
    private IgnoreChain Chain(string directory)
    {
        lock (_gate)
        {
            if (_chains.TryGetValue(directory, out var cached))
            {
                return cached;
            }
        }

        var parent = directory.Length == 0 ? IgnoreChain.Empty : Chain(Parent(directory));
        var full = directory.Length == 0 ? Root : Path.Combine(Root, directory.Replace('/', Path.DirectorySeparatorChar));
        var chain = parent;
        if (RepositoryScanner.TryReadText(Path.Combine(full, ".gitignore")) is { } text)
        {
            var source = directory.Length == 0 ? ".gitignore" : directory + "/.gitignore";
            chain = parent.Push(IgnoreRules.Parse(text, directory, source, _options.IgnoreCase));
        }

        lock (_gate)
        {
            _chains[directory] = chain;
        }

        return chain;
    }

    private (IgnoreRules? Pillion, IgnoreRules? User) Rules()
    {
        lock (_gate)
        {
            if (_loaded)
            {
                return (_pillion, _user);
            }
        }

        var user = _options.ExcludePatterns.Count > 0 ? IgnoreRules.FromPatterns(_options.ExcludePatterns, "indexing.exclude", _options.IgnoreCase) : null;
        var pillion = RepositoryScanner.TryReadText(Path.Combine(Root, ".pillionignore")) is { } text
            ? IgnoreRules.Parse(text, string.Empty, ".pillionignore", _options.IgnoreCase)
            : null;
        lock (_gate)
        {
            _pillion = pillion;
            _user = user;
            _loaded = true;
        }

        return (pillion, user);
    }
}
