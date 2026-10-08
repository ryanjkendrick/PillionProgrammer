using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.RegularExpressions;

namespace Pillion.Common.Redaction;

/// <summary>
/// One-way redaction for logs, the audit trail and index writes: secrets become <c>&lt;redacted:kind&gt;</c> and there
/// is no way back. Use <see cref="RedactionSession"/> instead when the originals must be restored.
/// </summary>
public static class LogRedactor
{
    /// <summary>
    /// The text returned when scanning fails (a regex timeout). Failing closed is deliberate: a log line that could
    /// not be checked is not written.
    /// </summary>
    public const string ScanFailedPlaceholder = "<redacted:scan-failed>";

    /// <summary>Replaces every secret in <paramref name="text"/> with <c>&lt;redacted:kind&gt;</c>.</summary>
    /// <param name="text">The text to be logged, audited or indexed; may be <see langword="null"/> or empty.</param>
    /// <returns>
    /// The redacted text, <paramref name="text"/> unchanged when nothing matched, <see langword="null"/> for a
    /// <see langword="null"/> input, or <see cref="ScanFailedPlaceholder"/> if scanning timed out.
    /// </returns>
    [return: NotNullIfNotNull(nameof(text))]
    public static string? Redact(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        IReadOnlyList<SecretMatch> matches;
        try
        {
            matches = SecretScanner.Scan(text);
        }
        catch (RegexMatchTimeoutException)
        {
            return ScanFailedPlaceholder;
        }

        if (matches.Count == 0)
        {
            return text;
        }

        var builder = new StringBuilder(text.Length);
        var position = 0;
        foreach (var match in matches)
        {
            builder.Append(text, position, match.Start - position);
            builder.Append("<redacted:").Append(match.KindId).Append('>');
            position = match.End;
        }

        builder.Append(text, position, text.Length - position);
        return builder.ToString();
    }
}
