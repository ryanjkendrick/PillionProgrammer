namespace Pillion.Indexing.Discovery.Ignore;

/// <summary>What an ignore file says about one path.</summary>
public enum IgnoreVerdict
{
    /// <summary>No rule matches the path.</summary>
    None,

    /// <summary>The last matching rule ignores the path.</summary>
    Ignored,

    /// <summary>The last matching rule is a negation (<c>!pattern</c>): the path is explicitly not ignored.</summary>
    Included,
}

/// <summary>The outcome of matching a path against ignore rules.</summary>
/// <param name="Verdict">What the rules say.</param>
/// <param name="Rule">The rule that decided, as written in the file (with its negation and slashes); <see langword="null"/> when no rule matched.</param>
/// <param name="Source">The ignore file or setting the rule came from, for example <c>src/.gitignore</c> with its line number; <see langword="null"/> when no rule matched.</param>
public readonly record struct IgnoreMatch(IgnoreVerdict Verdict, string? Rule = null, string? Source = null)
{
    /// <summary>Gets the match for a path that no rule touches.</summary>
    public static IgnoreMatch None { get; } = new(IgnoreVerdict.None);

    /// <summary>Gets a value indicating whether the path is ignored.</summary>
    public bool IsIgnored => Verdict == IgnoreVerdict.Ignored;

    /// <summary>Gets a description for a decision's detail text: the rule and where it is written.</summary>
    public string Describe() => Rule is null ? string.Empty : $"{Source}: {Rule}";
}

/// <summary>
/// The rules of one ignore file (<c>.gitignore</c>, <c>.pillionignore</c>, or the <c>indexing.exclude</c> setting), in
/// <c>gitignore</c> syntax: blank lines and <c>#</c> comments are skipped, <c>!</c> negates, a trailing <c>/</c> limits a
/// rule to directories, a leading or inner <c>/</c> anchors it to the file's directory, a rule without a slash matches at
/// any depth, and the last matching rule wins. Wildcards: <c>*</c> and <c>?</c> (never across a <c>/</c>), <c>[a-z]</c>
/// and <c>[!a-z]</c> classes (POSIX names such as <c>[:digit:]</c> too), <c>**</c> as a whole path segment, and
/// <c>\</c> to escape.
/// </summary>
public sealed class IgnoreRules
{
    private readonly IgnoreRule[] _rules;
    private readonly StringComparison _comparison;

    private IgnoreRules(IgnoreRule[] rules, string baseDirectory, string source, bool ignoreCase)
    {
        _rules = rules;
        BaseDirectory = baseDirectory;
        Source = source;
        _comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    }

    /// <summary>Gets the directory the file lives in, relative to the repository root with <c>/</c> separators; empty for the root.</summary>
    public string BaseDirectory { get; }

    /// <summary>Gets the name of the file or setting the rules came from, for messages.</summary>
    public string Source { get; }

    /// <summary>Gets the number of rules (blank lines and comments do not count).</summary>
    public int Count => _rules.Length;

    /// <summary>Parses the text of an ignore file.</summary>
    /// <param name="text">The file's content; any line ending.</param>
    /// <param name="baseDirectory">The directory of the file relative to the repository root, <c>/</c>-separated; empty for the root.</param>
    /// <param name="source">What to call the file in messages, for example <c>src/.gitignore</c>.</param>
    /// <param name="ignoreCase">Whether names match regardless of case; defaults to <see langword="true"/> as on Windows.</param>
    /// <returns>The rules.</returns>
    public static IgnoreRules Parse(string text, string baseDirectory = "", string source = ".gitignore", bool ignoreCase = true)
    {
        ArgumentNullException.ThrowIfNull(text);
        return FromLines(text.Split('\n'), baseDirectory, source, ignoreCase);
    }

    /// <summary>Builds rules from patterns given one per entry, such as the <c>indexing.exclude</c> setting.</summary>
    /// <param name="patterns">The patterns, in <c>.gitignore</c> syntax.</param>
    /// <param name="source">What to call them in messages.</param>
    /// <param name="ignoreCase">Whether names match regardless of case.</param>
    /// <returns>The rules, relative to the repository root.</returns>
    public static IgnoreRules FromPatterns(IEnumerable<string> patterns, string source, bool ignoreCase = true)
    {
        ArgumentNullException.ThrowIfNull(patterns);
        return FromLines(patterns, string.Empty, source, ignoreCase);
    }

    /// <summary>Matches one path against the rules; the path itself only, not its parents.</summary>
    /// <param name="relativePath">The path relative to the repository root, <c>/</c>-separated, without a leading or trailing slash.</param>
    /// <param name="isDirectory">Whether the path is a directory (directory-only rules need it).</param>
    /// <returns>What the last matching rule says, or <see cref="IgnoreMatch.None"/>.</returns>
    public IgnoreMatch Match(string relativePath, bool isDirectory)
    {
        ArgumentNullException.ThrowIfNull(relativePath);

        ReadOnlySpan<char> local = relativePath;
        if (BaseDirectory.Length > 0)
        {
            if (local.Length <= BaseDirectory.Length
                || local[BaseDirectory.Length] != '/'
                || !local.StartsWith(BaseDirectory, _comparison))
            {
                return IgnoreMatch.None;
            }

            local = local[(BaseDirectory.Length + 1)..];
        }

        var name = local[(local.LastIndexOf('/') + 1)..];
        for (var i = _rules.Length - 1; i >= 0; i--)
        {
            var rule = _rules[i];
            if (rule.DirectoryOnly && !isDirectory)
            {
                continue;
            }

            if (rule.Pattern.IsMatch(rule.Anchored ? local : name))
            {
                return new IgnoreMatch(rule.Negated ? IgnoreVerdict.Included : IgnoreVerdict.Ignored, rule.Text, $"{Source}:{rule.Line}");
            }
        }

        return IgnoreMatch.None;
    }

    private static IgnoreRules FromLines(IEnumerable<string> lines, string baseDirectory, string source, bool ignoreCase)
    {
        var rules = new List<IgnoreRule>();
        var number = 0;
        foreach (var raw in lines)
        {
            number++;
            if (TryParseLine(raw, number, ignoreCase) is { } rule)
            {
                rules.Add(rule);
            }
        }

        return new IgnoreRules([.. rules], baseDirectory.Trim('/'), source, ignoreCase);
    }

    private static IgnoreRule? TryParseLine(string raw, int number, bool ignoreCase)
    {
        var line = raw.TrimEnd('\r');
        if (number == 1 && line.Length > 0 && line[0] == '﻿')
        {
            line = line[1..];
        }

        if (line.Length == 0 || line[0] == '#')
        {
            return null;
        }

        // Trailing spaces are dropped unless escaped with a backslash.
        var end = line.Length;
        while (end > 0 && line[end - 1] == ' ' && !(end >= 2 && line[end - 2] == '\\'))
        {
            end--;
        }

        var pattern = line[..end];
        if (pattern.Length == 0)
        {
            return null;
        }

        var negated = false;
        if (pattern[0] == '!')
        {
            negated = true;
            pattern = pattern[1..];
        }

        var directoryOnly = false;
        if (pattern.Length > 0 && pattern[^1] == '/')
        {
            directoryOnly = true;
            pattern = pattern.TrimEnd('/');
        }

        var anchored = false;
        if (pattern.Length > 0 && pattern[0] == '/')
        {
            anchored = true;
            pattern = pattern.TrimStart('/');
        }
        else if (pattern.Contains('/'))
        {
            anchored = true;
        }

        if (pattern.Length == 0)
        {
            return null;
        }

        return new IgnoreRule(GlobPattern.Compile(pattern, ignoreCase), negated, directoryOnly, anchored, line[..end], number);
    }

    private sealed record IgnoreRule(GlobPattern Pattern, bool Negated, bool DirectoryOnly, bool Anchored, string Text, int Line);
}

/// <summary>
/// The ignore files that apply at one point of a directory walk: the root's first, then one per directory down to the
/// current one. A rule in a deeper file overrides a shallower one, as in git.
/// </summary>
public sealed class IgnoreChain
{
    private readonly IgnoreChain? _parent;
    private readonly IgnoreRules? _rules;

    private IgnoreChain(IgnoreChain? parent, IgnoreRules? rules)
    {
        _parent = parent;
        _rules = rules;
    }

    /// <summary>Gets the stack with no ignore files.</summary>
    public static IgnoreChain Empty { get; } = new(null, null);

    /// <summary>Returns a stack with one more (deeper) ignore file on top.</summary>
    /// <param name="rules">The rules of an ignore file; an empty file adds nothing.</param>
    /// <returns>The new stack; this one is unchanged.</returns>
    public IgnoreChain Push(IgnoreRules rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        return rules.Count == 0 ? this : new IgnoreChain(this, rules);
    }

    /// <summary>Matches a path, deepest file first: the first file with a matching rule decides.</summary>
    /// <param name="relativePath">The path relative to the repository root, <c>/</c>-separated.</param>
    /// <param name="isDirectory">Whether the path is a directory.</param>
    /// <returns>The deciding match, or <see cref="IgnoreMatch.None"/>.</returns>
    public IgnoreMatch Match(string relativePath, bool isDirectory)
    {
        for (var node = this; node is not null; node = node._parent)
        {
            if (node._rules is null)
            {
                continue;
            }

            var match = node._rules.Match(relativePath, isDirectory);
            if (match.Verdict != IgnoreVerdict.None)
            {
                return match;
            }
        }

        return IgnoreMatch.None;
    }

    /// <summary>
    /// Matches a path the way git does when it does not prune: first each parent directory from the top (an ignored
    /// parent ignores everything below it, whatever a negation says), then the path itself.
    /// </summary>
    /// <param name="relativePath">The path relative to the repository root, <c>/</c>-separated.</param>
    /// <param name="isDirectory">Whether the path is a directory.</param>
    /// <returns>The deciding match, or <see cref="IgnoreMatch.None"/>.</returns>
    public IgnoreMatch MatchWithParents(string relativePath, bool isDirectory)
    {
        ArgumentNullException.ThrowIfNull(relativePath);

        var slash = relativePath.IndexOf('/', StringComparison.Ordinal);
        while (slash >= 0)
        {
            var parent = Match(relativePath[..slash], isDirectory: true);
            if (parent.IsIgnored)
            {
                return parent;
            }

            slash = relativePath.IndexOf('/', slash + 1);
        }

        return Match(relativePath, isDirectory);
    }
}
