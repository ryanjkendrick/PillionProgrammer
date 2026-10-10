using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Pillion.VisualStudio.Core.Mentions;

/// <summary>A file or folder that matched what was typed after <c>@</c>.</summary>
public sealed class PathMatch
{
    /// <summary>Initializes a new instance of the <see cref="PathMatch"/> class.</summary>
    /// <param name="fullPath">The full path.</param>
    /// <param name="relativePath">The path relative to the workspace root it is under (with <c>/</c>), or the full path.</param>
    /// <param name="rank">The match quality: lower is better.</param>
    public PathMatch(string fullPath, string relativePath, int rank)
    {
        FullPath = fullPath;
        RelativePath = relativePath;
        Name = Path.GetFileName(fullPath.TrimEnd('\\', '/'));
        Rank = rank;
    }

    /// <summary>Gets the full path.</summary>
    public string FullPath { get; }

    /// <summary>Gets the path relative to its workspace root, with <c>/</c> separators.</summary>
    public string RelativePath { get; }

    /// <summary>Gets the file or folder name.</summary>
    public string Name { get; }

    /// <summary>Gets the match quality: 0 an exact name, then a name prefix, a name substring, the name's letters in order, a path substring, the path's letters in order.</summary>
    public int Rank { get; }
}

/// <summary>
/// Fuzzy matching of the picker's file and folder entries over the solution or folder file list (phase 1 has no index; the
/// phase 1 plan's decision of 2026-10-08). Matches rank by the file name first (exact, prefix, substring, letters in
/// order) and then by the path (substring, letters in order); a query with a <c>/</c> or <c>\</c> matches the relative path.
/// Ties go to the shorter name, then the shorter path, then the path in ordinal order.
/// </summary>
public static class MentionFileMatcher
{
    /// <summary>The most matches the picker lists.</summary>
    public const int MaxMatches = 20;

    /// <summary>Matches files.</summary>
    /// <param name="files">The full paths of the files.</param>
    /// <param name="roots">The workspace roots, for relative paths.</param>
    /// <param name="query">What was typed; empty matches nothing.</param>
    /// <param name="max">The most matches to return.</param>
    /// <returns>The matches, best first.</returns>
    public static IReadOnlyList<PathMatch> MatchFiles(IReadOnlyList<string> files, IReadOnlyList<string> roots, string? query, int max = MaxMatches) =>
        Match(files, roots, query, max);

    /// <summary>Matches the folders that hold the files (every ancestor up to a workspace root).</summary>
    /// <param name="files">The full paths of the files.</param>
    /// <param name="roots">The workspace roots.</param>
    /// <param name="query">What was typed; empty matches nothing.</param>
    /// <param name="max">The most matches to return.</param>
    /// <returns>The matches, best first.</returns>
    public static IReadOnlyList<PathMatch> MatchFolders(IReadOnlyList<string> files, IReadOnlyList<string> roots, string? query, int max = MaxMatches) =>
        Match(Folders(files, roots), roots, query, max);

    /// <summary>Gets a path relative to the longest workspace root that contains it, with <c>/</c> separators.</summary>
    /// <param name="path">The full path.</param>
    /// <param name="roots">The workspace roots.</param>
    /// <returns>The relative path, or the full path when no root contains it.</returns>
    public static string Relative(string path, IReadOnlyList<string> roots)
    {
        var best = string.Empty;
        foreach (var root in roots ?? [])
        {
            var trimmed = (root ?? string.Empty).TrimEnd('\\', '/');
            if (trimmed.Length > best.Length
                && path.Length > trimmed.Length + 1
                && path.StartsWith(trimmed, StringComparison.OrdinalIgnoreCase)
                && path[trimmed.Length] is '\\' or '/')
            {
                best = trimmed;
            }
        }

        return (best.Length == 0 ? path : path.Substring(best.Length + 1)).Replace('\\', '/');
    }

    private static List<PathMatch> Match(IEnumerable<string> paths, IReadOnlyList<string> roots, string? query, int max)
    {
        var q = (query ?? string.Empty).Trim().Replace('\\', '/').ToLowerInvariant();
        if (q.Length == 0 || max <= 0)
        {
            return [];
        }

        var byPath = q.IndexOf('/') >= 0;
        var matches = new List<PathMatch>();
        foreach (var path in paths)
        {
            if (string.IsNullOrEmpty(path))
            {
                continue;
            }

            var relative = Relative(path, roots);
            var lowerRelative = relative.ToLowerInvariant();
            var name = lowerRelative.Substring(lowerRelative.LastIndexOf('/') + 1);
            var rank = byPath ? RankPath(lowerRelative, q) : RankName(name, lowerRelative, q);
            if (rank >= 0)
            {
                matches.Add(new PathMatch(path, relative, rank));
            }
        }

        return matches
            .OrderBy(m => m.Rank)
            .ThenBy(m => m.Name.Length)
            .ThenBy(m => m.RelativePath.Length)
            .ThenBy(m => m.RelativePath, StringComparer.OrdinalIgnoreCase)
            .Take(max)
            .ToList();
    }

    private static int RankName(string name, string relative, string q)
    {
        if (name == q)
        {
            return 0;
        }

        if (name.StartsWith(q, StringComparison.Ordinal))
        {
            return 1;
        }

        if (name.IndexOf(q, StringComparison.Ordinal) >= 0)
        {
            return 2;
        }

        if (IsSubsequence(q, name))
        {
            return 3;
        }

        if (relative.IndexOf(q, StringComparison.Ordinal) >= 0)
        {
            return 4;
        }

        return IsSubsequence(q, relative) ? 5 : -1;
    }

    private static int RankPath(string relative, string q)
    {
        if (relative == q || relative.EndsWith("/" + q, StringComparison.Ordinal))
        {
            return 0;
        }

        if (relative.EndsWith(q, StringComparison.Ordinal))
        {
            return 1;
        }

        if (relative.IndexOf(q, StringComparison.Ordinal) >= 0)
        {
            return 4;
        }

        return IsSubsequence(q, relative) ? 5 : -1;
    }

    private static bool IsSubsequence(string needle, string haystack)
    {
        var j = 0;
        for (var i = 0; i < haystack.Length && j < needle.Length; i++)
        {
            if (haystack[i] == needle[j])
            {
                j++;
            }
        }

        return j == needle.Length;
    }

    private static List<string> Folders(IReadOnlyList<string> files, IReadOnlyList<string> roots)
    {
        var rootSet = new HashSet<string>((roots ?? []).Select(r => (r ?? string.Empty).TrimEnd('\\', '/')), StringComparer.OrdinalIgnoreCase);
        var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files ?? [])
        {
            var directory = SafeDirectoryName(file);
            while (!string.IsNullOrEmpty(directory) && !rootSet.Contains(directory!) && folders.Add(directory!))
            {
                directory = SafeDirectoryName(directory!);
                if (rootSet.Count == 0)
                {
                    break; // Without roots only the immediate folders are listed.
                }
            }
        }

        return [.. folders];
    }

    private static string? SafeDirectoryName(string path)
    {
        try
        {
            return Path.GetDirectoryName(path);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
