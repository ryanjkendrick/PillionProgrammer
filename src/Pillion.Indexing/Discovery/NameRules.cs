using Pillion.Indexing.Discovery.Ignore;

namespace Pillion.Indexing.Discovery;

/// <summary>
/// The exclusions that depend on a file's name alone (design section 7, Discover): the built-in directory names, the
/// secret-bearing file names, generated and minified files and lock files. Names are matched without regard to case.
/// </summary>
public static class NameRules
{
    private static readonly string[] BuiltInNames = ["bin", "obj", "node_modules", ".git", ".vs", "packages"];

    private static readonly string[] SecretPatterns =
    [
        ".env",
        ".env.*",
        "*.pfx",
        "*.pem",
        "*.key",
        "*.p12",
        "secrets.json",
        "appsettings.*.json",
        "*.publishsettings",
        "id_rsa",
        "id_dsa",
        "id_ecdsa",
        "id_ed25519",
        "*.ppk",
        "*.jks",
        "*.keystore",
        ".netrc",
        ".pgpass",
    ];

    private static readonly string[] GeneratedPatterns =
    [
        "*.g.cs",
        "*.g.i.cs",
        "*.designer.cs",
        "*.generated.cs",
        "*.g.vb",
        "*.designer.vb",
        "*.generated.vb",
    ];

    private static readonly string[] MinifiedPatterns = ["*.min.js", "*.min.css"];

    private static readonly HashSet<string> LockFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "package-lock.json",
        "npm-shrinkwrap.json",
        "yarn.lock",
        "pnpm-lock.yaml",
        "packages.lock.json",
        "composer.lock",
        "Cargo.lock",
        "poetry.lock",
        "Pipfile.lock",
        "Gemfile.lock",
        "go.sum",
        "bun.lockb",
    };

    private static readonly (string Pattern, GlobPattern Compiled)[] Secrets = Compile(SecretPatterns);
    private static readonly (string Pattern, GlobPattern Compiled)[] Generated = Compile(GeneratedPatterns);
    private static readonly (string Pattern, GlobPattern Compiled)[] Minified = Compile(MinifiedPatterns);

    /// <summary>Gets the built-in exclusions: directory (and file) names that are never indexed, wherever they are.</summary>
    public static IReadOnlyList<string> BuiltInExclusions => BuiltInNames;

    /// <summary>Gets the file name patterns that mark a file as secret-bearing.</summary>
    public static IReadOnlyList<string> SecretFilePatterns => SecretPatterns;

    /// <summary>Gets the file name patterns that mark generated code.</summary>
    public static IReadOnlyList<string> GeneratedFilePatterns => GeneratedPatterns;

    /// <summary>Gets the file name patterns that mark minified files.</summary>
    public static IReadOnlyList<string> MinifiedFilePatterns => MinifiedPatterns;

    /// <summary>Gets the names of the package manager lock files that are never indexed.</summary>
    public static IReadOnlyCollection<string> LockFiles => LockFileNames;

    /// <summary>Determines whether a name is one of the built-in exclusions.</summary>
    /// <param name="name">A file or directory name (not a path).</param>
    /// <returns>The built-in name it equals, or <see langword="null"/>.</returns>
    public static string? MatchBuiltIn(ReadOnlySpan<char> name)
    {
        foreach (var builtIn in BuiltInNames)
        {
            if (name.Equals(builtIn, StringComparison.OrdinalIgnoreCase))
            {
                return builtIn;
            }
        }

        return null;
    }

    /// <summary>Determines whether a file name marks a secret-bearing file.</summary>
    /// <param name="fileName">A file name (not a path).</param>
    /// <returns>The pattern that matched, or <see langword="null"/>.</returns>
    public static string? MatchSecret(ReadOnlySpan<char> fileName) => Match(Secrets, fileName);

    /// <summary>Classifies a file name as generated code, a minified file or a lock file.</summary>
    /// <param name="fileName">A file name (not a path).</param>
    /// <returns>The reason and the pattern or name that matched, or <see langword="null"/> when the name is ordinary.</returns>
    public static (ExclusionReason Reason, string Detail)? MatchGenerated(ReadOnlySpan<char> fileName)
    {
        if (Match(Generated, fileName) is { } generated)
        {
            return (ExclusionReason.GeneratedFile, generated);
        }

        if (Match(Minified, fileName) is { } minified)
        {
            return (ExclusionReason.MinifiedFile, minified);
        }

        if (LockFileNames.GetAlternateLookup<ReadOnlySpan<char>>().TryGetValue(fileName, out var lockFile))
        {
            return (ExclusionReason.LockFile, lockFile);
        }

        return null;
    }

    private static (string Pattern, GlobPattern Compiled)[] Compile(string[] patterns) =>
        [.. patterns.Select(p => (p, GlobPattern.Compile(p, ignoreCase: true)))];

    private static string? Match((string Pattern, GlobPattern Compiled)[] patterns, ReadOnlySpan<char> fileName)
    {
        foreach (var (pattern, compiled) in patterns)
        {
            if (compiled.IsMatch(fileName))
            {
                return pattern;
            }
        }

        return null;
    }
}
