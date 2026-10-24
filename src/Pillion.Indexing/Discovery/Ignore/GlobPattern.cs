using System.Text;
using System.Text.RegularExpressions;

namespace Pillion.Indexing.Discovery.Ignore;

/// <summary>
/// One compiled <c>gitwildmatch</c> pattern body (what is left of a <c>.gitignore</c> line once the negation, the
/// leading slash and the trailing slash are removed): <c>*</c>, <c>?</c>, <c>[...]</c> classes with ranges and POSIX
/// names, <c>**</c> as a whole path segment, and backslash escapes. Plain names, <c>*suffix</c> and <c>prefix*</c> are
/// matched with string comparisons; everything else with a non-backtracking regular expression, so a pattern from a
/// repository can never make matching slow.
/// </summary>
internal sealed class GlobPattern
{
    private readonly Kind _kind;
    private readonly string? _text;
    private readonly Regex? _regex;
    private readonly StringComparison _comparison;

    private GlobPattern(Kind kind, string? text, Regex? regex, StringComparison comparison)
    {
        _kind = kind;
        _text = text;
        _regex = regex;
        _comparison = comparison;
    }

    private enum Kind
    {
        Literal,
        Suffix,
        Prefix,
        Regex,
    }

    /// <summary>Compiles a pattern body.</summary>
    /// <param name="pattern">The pattern; <c>/</c> separates path segments.</param>
    /// <param name="ignoreCase">Whether letters match regardless of case (Windows file systems do).</param>
    /// <returns>The compiled pattern.</returns>
    public static GlobPattern Compile(string pattern, bool ignoreCase)
    {
        var comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var literal = new StringBuilder();
        var regex = new StringBuilder();
        var wildcard = false;
        var i = 0;
        var n = pattern.Length;
        while (i < n)
        {
            var c = pattern[i];
            switch (c)
            {
                case '\\' when i + 1 < n:
                    literal.Append(pattern[i + 1]);
                    regex.Append(Regex.Escape(pattern[i + 1].ToString()));
                    i += 2;
                    break;
                case '*':
                    {
                        wildcard = true;
                        var j = i;
                        while (j < n && pattern[j] == '*')
                        {
                            j++;
                        }

                        var segmentStart = i == 0 || pattern[i - 1] == '/';
                        var segmentEnd = j == n || pattern[j] == '/';
                        if (j - i >= 2 && segmentStart && segmentEnd)
                        {
                            if (j < n)
                            {
                                // "**/": zero or more whole directories, trailing slash included.
                                regex.Append("(?:.*/)?");
                                i = j + 1;
                            }
                            else
                            {
                                regex.Append(".*");
                                i = j;
                            }
                        }
                        else
                        {
                            regex.Append("[^/]*");
                            i = j;
                        }

                        break;
                    }

                case '?':
                    wildcard = true;
                    regex.Append("[^/]");
                    i++;
                    break;
                case '[' when TryTranslateClass(pattern, i, out var translated, out var next):
                    wildcard = true;
                    regex.Append(translated);
                    i = next;
                    break;
                default:
                    literal.Append(c);
                    regex.Append(Regex.Escape(c.ToString()));
                    i++;
                    break;
            }
        }

        if (!wildcard)
        {
            return new GlobPattern(Kind.Literal, literal.ToString(), null, comparison);
        }

        // "*.ext" and "name*" without any other wildcard or escape are the common cases.
        if (n > 1 && pattern[0] == '*' && pattern.AsSpan(1).IndexOfAny("*?[\\") < 0)
        {
            return new GlobPattern(Kind.Suffix, pattern[1..], null, comparison);
        }

        if (n > 1 && pattern[^1] == '*' && pattern.AsSpan(0, n - 1).IndexOfAny("*?[\\") < 0)
        {
            return new GlobPattern(Kind.Prefix, pattern[..^1], null, comparison);
        }

        var options = RegexOptions.NonBacktracking | RegexOptions.CultureInvariant | RegexOptions.Singleline;
        if (ignoreCase)
        {
            options |= RegexOptions.IgnoreCase;
        }

        return new GlobPattern(Kind.Regex, null, new Regex("^" + regex + "$", options), comparison);
    }

    /// <summary>Determines whether the subject (a name or a relative path with <c>/</c> separators) matches.</summary>
    /// <param name="subject">The text to test.</param>
    /// <returns><see langword="true"/> on a match.</returns>
    public bool IsMatch(ReadOnlySpan<char> subject) => _kind switch
    {
        Kind.Literal => subject.Equals(_text, _comparison),
        Kind.Suffix => subject.EndsWith(_text, _comparison) && !subject[..^_text!.Length].Contains('/'),
        Kind.Prefix => subject.StartsWith(_text, _comparison) && !subject[_text!.Length..].Contains('/'),
        _ => _regex!.IsMatch(subject),
    };

    /// <summary>Translates a bracket expression starting at <paramref name="start"/> (the <c>[</c>).</summary>
    private static bool TryTranslateClass(string pattern, int start, out string translated, out int next)
    {
        translated = string.Empty;
        next = start;
        var n = pattern.Length;
        var i = start + 1;
        var negated = false;
        if (i < n && (pattern[i] == '!' || pattern[i] == '^'))
        {
            negated = true;
            i++;
        }

        var body = new StringBuilder();
        var first = true;
        var previousWasLiteral = false;
        while (i < n)
        {
            var c = pattern[i];
            if (c == ']' && !first)
            {
                var text = new StringBuilder("[");
                if (negated)
                {
                    // A bracket expression never matches the path separator.
                    text.Append('^').Append('/');
                }

                text.Append(body).Append(']');
                translated = text.ToString();
                next = i + 1;
                return body.Length > 0;
            }

            first = false;
            if (c == '[' && i + 1 < n && pattern[i + 1] == ':')
            {
                var close = pattern.IndexOf(":]", i + 2, StringComparison.Ordinal);
                if (close < 0 || !PosixClasses.TryGetValue(pattern[(i + 2)..close], out var members))
                {
                    return false;
                }

                body.Append(members);
                i = close + 2;
                previousWasLiteral = false;
                continue;
            }

            if (c == '\\' && i + 1 < n)
            {
                AppendClassChar(body, pattern[i + 1]);
                i += 2;
                previousWasLiteral = true;
                continue;
            }

            if (c == '-' && previousWasLiteral && i + 1 < n && pattern[i + 1] != ']')
            {
                body.Append('-');
                i++;
                previousWasLiteral = false;
                continue;
            }

            AppendClassChar(body, c);
            i++;
            previousWasLiteral = true;
        }

        return false;
    }

    private static void AppendClassChar(StringBuilder body, char c)
    {
        if (c is '\\' or ']' or '[' or '^' or '-')
        {
            body.Append('\\');
        }

        body.Append(c);
    }

    private static readonly Dictionary<string, string> PosixClasses = new(StringComparer.Ordinal)
    {
        ["alnum"] = "a-zA-Z0-9",
        ["alpha"] = "a-zA-Z",
        ["blank"] = " \\t",
        ["cntrl"] = "\\x00-\\x1F\\x7F",
        ["digit"] = "0-9",
        ["graph"] = "\\x21-\\x7E",
        ["lower"] = "a-z",
        ["print"] = "\\x20-\\x7E",
        ["punct"] = "!-/:-@\\[-`{-~",
        ["space"] = " \\t\\n\\r\\f\\v",
        ["upper"] = "A-Z",
        ["xdigit"] = "0-9A-Fa-f",
    };
}
