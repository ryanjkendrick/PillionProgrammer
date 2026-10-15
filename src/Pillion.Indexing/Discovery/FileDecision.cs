namespace Pillion.Indexing.Discovery;

/// <summary>Why a file or directory is not indexed. <see cref="None"/> means it is.</summary>
public enum ExclusionReason
{
    /// <summary>Not excluded: the file is indexed.</summary>
    None,

    /// <summary>The file name says it can hold secrets (<c>.env</c>, <c>*.pem</c>, <c>secrets.json</c> and so on). Never indexed; listed in the Index window.</summary>
    SecretBearing,

    /// <summary>A <c>.gitignore</c> rule ignores the path.</summary>
    GitIgnore,

    /// <summary>A rule in the root's <c>.pillionignore</c> ignores the path.</summary>
    PillionIgnore,

    /// <summary>The name is one of the built-in exclusions: <c>bin</c>, <c>obj</c>, <c>node_modules</c>, <c>.git</c>, <c>.vs</c>, <c>packages</c>.</summary>
    BuiltIn,

    /// <summary>A pattern of the <c>indexing.exclude</c> setting (or a repository policy's <c>exclude</c>) matches the path.</summary>
    UserExclude,

    /// <summary>The file name marks generated code (<c>*.g.cs</c>, <c>*.g.i.cs</c>, <c>*.designer.cs</c> and so on).</summary>
    GeneratedFile,

    /// <summary>The file name marks a minified file (<c>*.min.js</c>, <c>*.min.css</c>).</summary>
    MinifiedFile,

    /// <summary>The file is a package manager lock file (<c>package-lock.json</c>, <c>yarn.lock</c>, <c>packages.lock.json</c> and so on).</summary>
    LockFile,

    /// <summary>The first 2 KB carry an auto-generated header (<c>&lt;auto-generated</c>, <c>@generated</c> and so on).</summary>
    GeneratedHeader,

    /// <summary>The first 8 KB contain a NUL byte, so the file is binary.</summary>
    Binary,

    /// <summary>The file is larger than the size cap (1 MB by default).</summary>
    TooLarge,

    /// <summary>The path is a symbolic link, junction or other reparse point, which is never followed.</summary>
    ReparsePoint,

    /// <summary>The directory is the root of another git repository (it has its own <c>.git</c>); it is indexed under its own root, not this one.</summary>
    NestedRepository,

    /// <summary>The file could not be opened or read (locked, access denied, vanished).</summary>
    Unreadable,
}

/// <summary>The decision about one file or directory: indexed, or excluded with the rule that did it.</summary>
/// <param name="Reason">Why it is excluded, or <see cref="ExclusionReason.None"/> when it is indexed.</param>
/// <param name="Detail">The exact rule, for example the gitignore line with its file, the matched file name pattern, or the size; <see langword="null"/> when included.</param>
public readonly record struct FileDecision(ExclusionReason Reason, string? Detail = null)
{
    /// <summary>Gets the decision of an indexed file.</summary>
    public static FileDecision Included { get; } = new(ExclusionReason.None);

    /// <summary>Gets a value indicating whether the file is indexed.</summary>
    public bool IsIncluded => Reason == ExclusionReason.None;

    /// <summary>Gets a value indicating whether the file is secret-bearing (excluded and listed separately).</summary>
    public bool IsSecretBearing => Reason == ExclusionReason.SecretBearing;

    /// <summary>Gets a short sentence for the Index window.</summary>
    public string Describe() => Reason switch
    {
        ExclusionReason.None => "Indexed.",
        ExclusionReason.SecretBearing => $"Not indexed: the file may hold secrets ({Detail}).",
        ExclusionReason.GitIgnore => $"Not indexed: ignored by .gitignore ({Detail}).",
        ExclusionReason.PillionIgnore => $"Not indexed: ignored by .pillionignore ({Detail}).",
        ExclusionReason.BuiltIn => $"Not indexed: built-in exclusion ({Detail}).",
        ExclusionReason.UserExclude => $"Not indexed: matches indexing.exclude ({Detail}).",
        ExclusionReason.GeneratedFile => $"Not indexed: generated file ({Detail}).",
        ExclusionReason.MinifiedFile => $"Not indexed: minified file ({Detail}).",
        ExclusionReason.LockFile => $"Not indexed: lock file ({Detail}).",
        ExclusionReason.GeneratedHeader => $"Not indexed: auto-generated header ({Detail}).",
        ExclusionReason.Binary => "Not indexed: binary file.",
        ExclusionReason.TooLarge => $"Not indexed: larger than the size cap ({Detail}).",
        ExclusionReason.ReparsePoint => "Not indexed: symbolic link or junction (never followed).",
        ExclusionReason.NestedRepository => "Not indexed here: another git repository, indexed under its own root.",
        ExclusionReason.Unreadable => $"Not indexed: could not be read ({Detail}).",
        _ => Reason.ToString(),
    };
}
