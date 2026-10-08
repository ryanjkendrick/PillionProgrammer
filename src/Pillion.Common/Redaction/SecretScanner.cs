using System.Text.RegularExpressions;

namespace Pillion.Common.Redaction;

/// <summary>
/// Finds secrets in text. Redaction itself is done by <see cref="RedactionSession"/> (reversible, for model
/// requests) and <see cref="LogRedactor"/> (one way, for logs, audit and index writes).
/// </summary>
/// <remarks>
/// Every pattern is a compiled, culture-invariant regex with <see cref="MatchTimeout"/>. See the README in this
/// folder for the pattern list, the entropy threshold and known false positives and negatives.
/// </remarks>
public static class SecretScanner
{
    /// <summary>The per-match timeout applied to every regex.</summary>
    public static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// The Shannon entropy (bits per character) a value assigned to a credential-named variable must exceed to be
    /// reported as <see cref="SecretKind.GenericHighEntropy"/>.
    /// </summary>
    public const double GenericEntropyThreshold = SecretPatterns.GenericEntropyThreshold;

    /// <summary>Finds the secrets in <paramref name="text"/>.</summary>
    /// <param name="text">The text to scan; may be empty.</param>
    /// <returns>
    /// Non-overlapping matches in document order. When patterns overlap, the more specific pattern wins (whole
    /// blocks, then self-identifying token formats, then context-dependent formats, then the generic
    /// high-entropy heuristic); within the same specificity the longer match wins, then the earlier one.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is <see langword="null"/>.</exception>
    /// <exception cref="RegexMatchTimeoutException">
    /// A pattern exceeded <see cref="MatchTimeout"/>. Callers sending text to a cloud model must treat this as "do
    /// not send"; the text could not be verified.
    /// </exception>
    public static IReadOnlyList<SecretMatch> Scan(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 0)
        {
            return [];
        }

        var candidates = new List<SecretMatch>();
        var priorities = new List<int>();
        foreach (var pattern in SecretPatterns.All)
        {
            foreach (Match match in pattern.Regex.Matches(text))
            {
                var group = match.Groups["v"];
                if (!group.Success || group.Length == 0)
                {
                    continue;
                }

                var value = group.Value;
                if (SecretAllowList.IsAllowed(value) || (pattern.Accept is not null && !pattern.Accept(value)))
                {
                    continue;
                }

                var severity = pattern.SeverityOf?.Invoke(value) ?? pattern.Severity;
                candidates.Add(new SecretMatch(pattern.Kind, group.Index, group.Length, severity));
                priorities.Add(pattern.Priority);
            }
        }

        return Resolve(candidates, priorities);
    }

    /// <summary>Picks non-overlapping matches, most specific then longest first, and returns them in document order.</summary>
    private static List<SecretMatch> Resolve(List<SecretMatch> candidates, List<int> priorities)
    {
        var order = new int[candidates.Count];
        for (var i = 0; i < order.Length; i++)
        {
            order[i] = i;
        }

        Array.Sort(order, (a, b) =>
        {
            var byPriority = priorities[a].CompareTo(priorities[b]);
            if (byPriority != 0)
            {
                return byPriority;
            }

            var byLength = candidates[b].Length.CompareTo(candidates[a].Length);
            return byLength != 0 ? byLength : candidates[a].Start.CompareTo(candidates[b].Start);
        });

        // Accepted matches kept sorted by start so an overlap check only needs the two neighbours.
        var accepted = new List<SecretMatch>();
        foreach (var index in order)
        {
            var candidate = candidates[index];
            var lo = 0;
            var hi = accepted.Count;
            while (lo < hi)
            {
                var mid = (lo + hi) >>> 1;
                if (accepted[mid].Start < candidate.Start)
                {
                    lo = mid + 1;
                }
                else
                {
                    hi = mid;
                }
            }

            var overlapsPrevious = lo > 0 && accepted[lo - 1].End > candidate.Start;
            var overlapsNext = lo < accepted.Count && accepted[lo].Start < candidate.End;
            if (!overlapsPrevious && !overlapsNext)
            {
                accepted.Insert(lo, candidate);
            }
        }

        return accepted;
    }
}
