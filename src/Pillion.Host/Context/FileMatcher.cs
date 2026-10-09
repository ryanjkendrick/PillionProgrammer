namespace Pillion.Host.Context;

/// <summary>The outcome of matching a typed name against a list of paths.</summary>
/// <param name="Single">The one path that matches, or <see langword="null"/> when none or several do.</param>
/// <param name="Candidates">When more than one path matches (or only loose matches exist), up to 10 of them, best first.</param>
/// <param name="IsLoose">Whether the candidates are only letter-order matches (tier 4), not name or path matches.</param>
internal sealed record FileMatch(string? Single, IReadOnlyList<string> Candidates, bool IsLoose = false)
{
    /// <summary>Gets a value indicating whether nothing matched.</summary>
    public bool IsEmpty => Single is null && Candidates.Count == 0;
}

/// <summary>
/// Fuzzy matching of an <c>@file</c> or <c>@folder</c> name against the solution or folder file list (design section 7:
/// "a fuzzy name"; phase 1 has no index, so the client supplies the list). Matching goes by file name first, then by path:
/// the best tier that has any match decides, and a tier with exactly one match resolves.
/// </summary>
/// <remarks>
/// Tiers for a name without a slash, on the last path segment: 0 the whole name, 1 the name without its extension,
/// 2 starts with, 3 contains, 4 the letters in order (loose). With a slash the path is compared: 0 ends with the query,
/// 1 ends with it ignoring the extension, 3 contains it, 4 the letters in order. Tiers 0 to 3 resolve when unique; tier 4
/// never resolves on its own, it only offers candidates. Comparison ignores case and slash style.
/// </remarks>
internal static class FileMatcher
{
    /// <summary>The most candidates returned for an ambiguous match.</summary>
    public const int MaxCandidates = 10;

    /// <summary>Matches a name against paths.</summary>
    /// <param name="query">The typed name or partial path.</param>
    /// <param name="paths">The full paths (files or folders).</param>
    /// <returns>The match.</returns>
    public static FileMatch Match(string query, IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(paths);
        var q = Normalize(query).Trim('/');
        if (q.Length == 0)
        {
            return new FileMatch(null, []);
        }

        var hasSlash = q.Contains('/', StringComparison.Ordinal);
        var best = int.MaxValue;
        var matches = new List<string>();
        foreach (var path in paths)
        {
            var tier = TierOf(q, hasSlash, path);
            if (tier < 0 || tier > best)
            {
                continue;
            }

            if (tier < best)
            {
                best = tier;
                matches.Clear();
            }

            matches.Add(path);
        }

        if (matches.Count == 0)
        {
            return new FileMatch(null, []);
        }

        if (matches.Count == 1 && best <= 3)
        {
            return new FileMatch(matches[0], []);
        }

        var ordered = matches
            .OrderBy(p => p.Length)
            .ThenBy(p => p, StringComparer.OrdinalIgnoreCase)
            .Take(MaxCandidates)
            .ToList();
        return new FileMatch(null, ordered, best == 4);
    }

    private static int TierOf(string q, bool hasSlash, string path)
    {
        var normalized = Normalize(path);
        if (hasSlash)
        {
            if (normalized.Equals(q, StringComparison.OrdinalIgnoreCase) || normalized.EndsWith("/" + q, StringComparison.OrdinalIgnoreCase))
            {
                return 0;
            }

            var withoutExtension = StripExtension(normalized);
            if (withoutExtension.EndsWith("/" + q, StringComparison.OrdinalIgnoreCase))
            {
                return 1;
            }

            if (normalized.Contains(q, StringComparison.OrdinalIgnoreCase))
            {
                return 3;
            }

            return IsSubsequence(q, normalized) ? 4 : -1;
        }

        var name = normalized[(normalized.LastIndexOf('/') + 1)..];
        if (name.Equals(q, StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        if (StripExtension(name).Equals(q, StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        if (name.StartsWith(q, StringComparison.OrdinalIgnoreCase))
        {
            return 2;
        }

        if (name.Contains(q, StringComparison.OrdinalIgnoreCase))
        {
            return 3;
        }

        return IsSubsequence(q, name) ? 4 : -1;
    }

    private static string Normalize(string path) => path.Replace('\\', '/');

    private static string StripExtension(string path)
    {
        var slash = path.LastIndexOf('/');
        var dot = path.LastIndexOf('.');
        return dot > slash + 1 ? path[..dot] : path;
    }

    private static bool IsSubsequence(string needle, string haystack)
    {
        var n = 0;
        foreach (var c in haystack)
        {
            if (n < needle.Length && char.ToLowerInvariant(c) == char.ToLowerInvariant(needle[n]))
            {
                n++;
            }
        }

        return n == needle.Length;
    }
}
