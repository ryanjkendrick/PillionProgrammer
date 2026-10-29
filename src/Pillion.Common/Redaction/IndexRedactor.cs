using System.Text;
using System.Text.RegularExpressions;

namespace Pillion.Common.Redaction;

/// <summary>
/// One-way redaction for chunk text before it is stored in the index (design section 12; work item 3.2, issue #103).
/// Secrets become <c>&lt;redacted-index:kind&gt;</c>: a placeholder marked as the index's, distinct from a request's
/// <c>&lt;redacted:kind&gt;</c>, so that no <see cref="RedactionSession"/> ever restores it (its placeholder shape requires
/// <c>&lt;redacted:</c>), the model-boundary scanner never counts it as a secret again (<see cref="SecretAllowList"/>), and an
/// edit that carries one can be recognised and bounced (work item 3.10) rather than written into a file.
/// </summary>
public static class IndexRedactor
{
    /// <summary>The prefix of every placeholder this redactor writes.</summary>
    public const string PlaceholderPrefix = "<redacted-index:";

    /// <summary>
    /// The text stored in place of a chunk whose scan failed (a regex timeout). Failing closed is deliberate: a chunk that
    /// could not be checked is not stored.
    /// </summary>
    public const string ScanFailedPlaceholder = PlaceholderPrefix + "scan-failed>";

    private static readonly Regex PlaceholderShape = new(
        @"<redacted-index:[^<>\r\n]{0,80}>",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        SecretScanner.MatchTimeout);

    /// <summary>Replaces every secret in <paramref name="text"/> with <c>&lt;redacted-index:kind&gt;</c>.</summary>
    /// <param name="text">A chunk's text, about to be stored.</param>
    /// <returns>
    /// The text to store (the same instance when nothing matched, <see cref="ScanFailedPlaceholder"/> when the scan timed
    /// out) and how many secrets were replaced (1 for a scan failure, since the chunk's text is withheld).
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is <see langword="null"/>.</exception>
    public static IndexRedaction Redact(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 0)
        {
            return new IndexRedaction(text, 0);
        }

        IReadOnlyList<SecretMatch> matches;
        try
        {
            matches = SecretScanner.Scan(text);
        }
        catch (RegexMatchTimeoutException)
        {
            return new IndexRedaction(ScanFailedPlaceholder, 1);
        }

        if (matches.Count == 0)
        {
            return new IndexRedaction(text, 0);
        }

        var builder = new StringBuilder(text.Length);
        var position = 0;
        foreach (var match in matches)
        {
            builder.Append(text, position, match.Start - position);
            builder.Append(PlaceholderPrefix).Append(match.KindId).Append('>');
            position = match.End;
        }

        builder.Append(text, position, text.Length - position);
        return new IndexRedaction(builder.ToString(), matches.Count);
    }

    /// <summary>Tells whether <paramref name="text"/> carries an index placeholder (text that came out of the index, not out of a file).</summary>
    /// <param name="text">Any text: a retrieved chunk, a proposed edit.</param>
    /// <returns><see langword="true"/> when an <c>&lt;redacted-index:...&gt;</c> token is present.</returns>
    public static bool ContainsPlaceholder(string? text) =>
        !string.IsNullOrEmpty(text)
        && text.Contains(PlaceholderPrefix, StringComparison.Ordinal)
        && PlaceholderShape.IsMatch(text);

    /// <summary>Finds the distinct index placeholders in <paramref name="text"/>, in order of first appearance.</summary>
    /// <param name="text">Any text.</param>
    /// <returns>The placeholders; empty when there are none.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is <see langword="null"/>.</exception>
    public static IReadOnlyList<string> FindPlaceholders(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (!text.Contains(PlaceholderPrefix, StringComparison.Ordinal))
        {
            return [];
        }

        var found = new List<string>();
        foreach (Match match in PlaceholderShape.Matches(text))
        {
            if (!found.Contains(match.Value))
            {
                found.Add(match.Value);
            }
        }

        return found;
    }
}

/// <summary>What <see cref="IndexRedactor.Redact"/> produced.</summary>
/// <param name="Text">The text to store.</param>
/// <param name="Count">How many secrets were replaced; 0 means <see cref="Text"/> is the input unchanged.</param>
public readonly record struct IndexRedaction(string Text, int Count)
{
    /// <summary>Gets a value indicating whether anything was redacted.</summary>
    public bool IsRedacted => Count > 0;
}
