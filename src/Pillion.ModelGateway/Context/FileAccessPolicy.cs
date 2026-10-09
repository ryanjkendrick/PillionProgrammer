using System.Text.RegularExpressions;

namespace Pillion.ModelGateway;

/// <summary>Why a file may not be put into a prompt without the user's approval.</summary>
public enum FileAccessVerdict
{
    /// <summary>The file may be used.</summary>
    Allowed,

    /// <summary>A glob in <c>indexing.exclude</c> (the user's or a repository policy's) matches the path.</summary>
    ExcludedByGlob,

    /// <summary>The path or the content looks like a secret file (design section 12, Secret scanner).</summary>
    SecretFile,
}

/// <summary>The answer of <see cref="FileAccessPolicy.Check"/>.</summary>
/// <param name="Verdict">The verdict.</param>
/// <param name="Reason">A sentence the UI can show; empty when the file is allowed.</param>
public readonly record struct FileAccessDecision(FileAccessVerdict Verdict, string Reason)
{
    /// <summary>Gets a value indicating whether the file may be used without approval.</summary>
    public bool IsAllowed => Verdict == FileAccessVerdict.Allowed;

    /// <summary>Gets the decision for a file that may be used.</summary>
    public static FileAccessDecision Allowed => new(FileAccessVerdict.Allowed, string.Empty);
}

/// <summary>
/// Decides whether a file may be pinned or sent as context (design section 7, Context pinning; section 12): files matched
/// by the <c>indexing.exclude</c> globs and files that look like secrets need the user's per-file approval, and an approved
/// one is never sent to a cloud model.
/// </summary>
/// <remarks>
/// Glob syntax: <c>*</c> matches within a path segment, <c>**</c> across segments, <c>?</c> one character; case is ignored
/// and both slash styles are accepted. A glob matches when it matches the path or any trailing run of path segments, so
/// <c>*.pem</c> matches a file name anywhere, <c>secrets/**</c> matches under any folder called <c>secrets</c>, and a glob
/// without a wildcard such as <c>node_modules</c> matches a folder or file of that name. Negation (<c>!</c>) is not supported.
/// </remarks>
public sealed class FileAccessPolicy
{
    private static readonly string[] SecretExtensions =
    [
        ".pfx", ".p12", ".pem", ".key", ".jks", ".keystore", ".snk", ".ppk", ".kdbx", ".publishsettings",
    ];

    private static readonly string[] SecretNames =
    [
        ".env", "secrets.json", ".secrets", ".netrc", ".npmrc", ".pypirc", ".git-credentials",
        "id_rsa", "id_dsa", "id_ecdsa", "id_ed25519",
    ];

    private static readonly string[] SecretFolders = [".ssh", ".aws", ".gnupg"];

    private static readonly string[] TemplateSuffixes = ["example", "sample", "template", "dist", "defaults"];

    private readonly List<(string Glob, Regex Pattern)> _excludes = [];

    /// <summary>Initializes a new instance of the <see cref="FileAccessPolicy"/> class.</summary>
    /// <param name="excludeGlobs">The <c>indexing.exclude</c> globs (the effective ones, with repository policies applied); may be empty.</param>
    public FileAccessPolicy(IEnumerable<string>? excludeGlobs)
    {
        foreach (var glob in excludeGlobs ?? [])
        {
            var trimmed = glob?.Trim();
            if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith('#') || trimmed.StartsWith('!'))
            {
                continue;
            }

            _excludes.Add((trimmed, ToRegex(trimmed)));
        }
    }

    /// <summary>Gets a policy with the built-in secret-file rules only.</summary>
    public static FileAccessPolicy Default { get; } = new FileAccessPolicy([]);

    /// <summary>Checks a file.</summary>
    /// <param name="path">The file's path, absolute or relative.</param>
    /// <param name="readContent">
    /// Supplies the file's text for the content rule (<c>appsettings.*.json</c> files that mention <c>ConnectionStrings</c> or
    /// <c>Password</c>); it is called at most once. When <see langword="null"/>, such a file counts as a secret.
    /// </param>
    /// <returns>The decision.</returns>
    public FileAccessDecision Check(string path, Func<string?>? readContent = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        var normalized = Normalize(path);
        foreach (var (glob, pattern) in _excludes)
        {
            if (pattern.IsMatch(normalized))
            {
                return new FileAccessDecision(FileAccessVerdict.ExcludedByGlob, $"It matches the exclusion '{glob}' (indexing.exclude).");
            }
        }

        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var name = segments.Length > 0 ? segments[^1] : normalized;
        foreach (var folder in segments.Take(Math.Max(0, segments.Length - 1)))
        {
            if (SecretFolders.Contains(folder, StringComparer.OrdinalIgnoreCase))
            {
                return Secret($"it is inside a '{folder}' folder");
            }
        }

        if (SecretNames.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            return Secret($"'{name}' is a secrets file");
        }

        if (name.StartsWith(".env.", StringComparison.OrdinalIgnoreCase) &&
            !TemplateSuffixes.Contains(name[5..], StringComparer.OrdinalIgnoreCase))
        {
            return Secret($"'{name}' is an environment file");
        }

        var extension = Path.GetExtension(name);
        if (SecretExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            return Secret($"'{extension}' files hold keys or certificates");
        }

        if (name.StartsWith("appsettings.", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".json", StringComparison.OrdinalIgnoreCase) && name.Length > "appsettings.json".Length)
        {
            string? content;
            try
            {
                content = readContent?.Invoke();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                content = null;
            }

            if (content is null || content.Contains("ConnectionStrings", StringComparison.OrdinalIgnoreCase) ||
                content.Contains("Password", StringComparison.OrdinalIgnoreCase))
            {
                return Secret($"'{name}' holds connection strings or passwords");
            }
        }

        return FileAccessDecision.Allowed;
    }

    private static FileAccessDecision Secret(string why) =>
        new(FileAccessVerdict.SecretFile, $"It looks like a secret file: {why}.");

    private static string Normalize(string path) => path.Replace('\\', '/');

    private static Regex ToRegex(string glob)
    {
        var text = Normalize(glob).TrimStart('/');
        var body = new System.Text.StringBuilder();
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '*')
            {
                if (i + 1 < text.Length && text[i + 1] == '*')
                {
                    i++;
                    if (i + 1 < text.Length && text[i + 1] == '/')
                    {
                        i++;
                        body.Append("(?:.*/)?");
                    }
                    else
                    {
                        body.Append(".*");
                    }
                }
                else
                {
                    body.Append("[^/]*");
                }
            }
            else if (c == '?')
            {
                body.Append("[^/]");
            }
            else
            {
                body.Append(Regex.Escape(c.ToString()));
            }
        }

        return new Regex("(?:^|/)" + body + "(?:/.*)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline);
    }
}
